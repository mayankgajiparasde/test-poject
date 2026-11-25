using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static HotelUnderwriting.Api.Tests.AuthRig;

namespace HotelUnderwriting.Api.Tests;

/// <summary>Authentication and per-deal access (story 10a), one or more tests per matrix row.</summary>
public class AuthTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static List<(string Method, string Route, bool Anonymous)> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods
                .Select(m => (m, e.RoutePattern.RawText!, e.Metadata.GetMetadata<IAllowAnonymous>() is not null))).ToList();

    private static string Fill(string route) => route.Replace("{id}", "x").Replace("{runKey}", "x");

    // ---- mode none

    [Theory]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.2:0")]
    [InlineData("http://[::1]:0")]
    [InlineData("http://localhost:{port}")]
    public async Task Mode_none_on_loopback_needs_no_token_and_grants_nothing(string url)
    {
        await using var app = ApiHost.Build(["--urls", url.Replace("{port}", FreePort().ToString()), "--Auth:Mode=none"]);
        await app.StartAsync();
        using var c = Client(app);
        if (url.Contains("127.0.0.2")) c.DefaultRequestHeaders.Host = "localhost"; // it listens there, but only the three loopback host names are served
        var published = await Send(c, HttpMethod.Post, "/v1/assumption-sets", null, JsonSerializer.Serialize(A, ApiHost.Json), deal: "not a deal id!");
        Assert.Equal(HttpStatusCode.Created, published.Status);
        var run = await Send(c, HttpMethod.Post, "/v1/runs", null, $$"""{"assumptionSetId":"{{published.Str("id")}}","scenario":"Base"}""");
        Assert.Equal(HttpStatusCode.OK, run.Status);
        Assert.Equal(HttpStatusCode.OK, (await Get(c, $"/v1/runs/{run.Str("runKey")}", "garbage")).Status);
        var access = app.Services.GetRequiredService<IDealAccess>();
        Assert.Empty(access.DealsOf(DealResource.Set, published.Str("id")));
        Assert.Empty(access.DealsOf(DealResource.Run, run.Str("runKey")));
        Assert.All(((InMemoryLedger)app.Services.GetRequiredService<ILedger>()).Entries, e => Assert.Equal("anonymous", e.Actor));
    }

    [Theory]
    [InlineData("--urls=http://0.0.0.0:{port}")]
    [InlineData("--urls=http://*:{port}")]
    [InlineData("--urls=http://+:{port}")]
    [InlineData("--urls=http://[::]:{port}")]
    [InlineData("--urls=http://some-host.example:{port}")]
    [InlineData("--urls=http://127.0.0.1:0;http://0.0.0.0:{port}")]
    [InlineData("--HTTP_PORTS={port}")]
    [InlineData("--HTTPS_PORTS={port}")]
    [InlineData("--Kestrel:Endpoints:Web:Url=http://0.0.0.0:{port}")]
    [InlineData("--urls=http://0.0.0.0:{port}", "--Role=worker")]
    public async Task Mode_none_does_not_serve_a_non_loopback_address(params string[] args)
    {
        var port = FreePort();
        await using var app = ApiHost.Build(args.Select(a => a.Replace("{port}", port.ToString())).ToArray());
        var e = await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
        Assert.Contains("Auth:Mode", e.ToString());
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port));
    }

    private static async Task<HttpStatusCode> WithHost(HttpClient c, string host)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        req.Headers.Host = host;
        return (await c.SendAsync(req)).StatusCode;
    }

    [Fact]
    public async Task Mode_none_serves_only_loopback_host_headers_whatever_AllowedHosts_says()
    {
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", "--AllowedHosts=*"]);
        await app.StartAsync();
        using var c = Client(app);
        var port = new Uri(app.Urls.First()).Port;
        foreach (var host in new[] { "localhost", $"localhost:{port}", "127.0.0.1", $"127.0.0.1:{port}", "[::1]", $"[::1]:{port}" })
            Assert.Equal(HttpStatusCode.OK, await WithHost(c, host));
        foreach (var host in new[] { "evil.example", $"evil.example:{port}", "127.0.0.1.evil.example", "localhost.evil.example", "127.0.0.2", "0.0.0.0" })
            Assert.Equal(HttpStatusCode.BadRequest, await WithHost(c, host));
    }

    [Fact]
    public async Task Mode_jwt_serves_any_host_header()
    {
        await using var app = await Start();
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.OK, await WithHost(c, "api.example"));
    }

    private sealed class OtherServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken ct) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Fact]
    public void Mode_none_refuses_a_server_other_than_kestrel()
    {
        var e = Assert.Throws<InvalidOperationException>(() => ApiHost.Build([], s => s.AddSingleton<IServer, OtherServer>()));
        Assert.Contains("Auth:Mode", e.Message);
    }

    [Theory]
    [InlineData("http://0.0.0.0:0", 1)]
    [InlineData("http://127.0.0.1:0;http://[::]:0", 1)]
    [InlineData("http://127.0.0.1:0", 0)]
    public async Task Mode_jwt_warns_once_about_plain_http_beyond_loopback(string urls, int warnings)
    {
        var capture = new Capture();
        await using var app = ApiHost.Build([.. JwtArgs.Skip(2), "--urls", urls], s => s.AddSingleton<ILoggerProvider>(capture));
        await app.StartAsync();
        Assert.Equal(warnings, capture.Entries.Count(e => e.Level == LogLevel.Warning && e.Text.Contains("TLS")));
    }

    [Fact]
    public async Task An_unreachable_authority_is_a_401_and_a_visible_warning_without_the_token()
    {
        var capture = new Capture();
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", Mode, $"--Auth:Issuer={Issuer}", $"--Auth:Audience={Audience}", $"--Auth:Authority=http://127.0.0.1:{FreePort()}"],
            s => s.AddSingleton<ILoggerProvider>(capture));
        await app.StartAsync();
        using var c = Client(app);
        using var rsa = RSA.Create(2048);
        var token = Mint(Claims("alice"), new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "r" }, SecurityAlgorithms.RsaSha256));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/v1/runs/x", token)).Status);
        var entry = Assert.Single(capture.Entries, e => e.Text.StartsWith("Bearer token rejected"));
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(token.Split('.')[2], string.Join("\n", capture.Lines));
    }

    [Fact]
    public async Task A_routine_bad_token_is_logged_at_information()
    {
        var capture = new Capture();
        await using var app = await Start(s => s.AddSingleton<ILoggerProvider>(capture));
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/v1/runs/x", Mint(Claims("alice"), Hs256(), expires: TimeSpan.FromSeconds(-600)))).Status);
        Assert.Equal(LogLevel.Information, Assert.Single(capture.Entries, e => e.Text.StartsWith("Bearer token rejected")).Level);
    }

    // ---- bad configuration

    private const string Hidden = "hidden-value-that-must-not-be-echoed-123";
    private const string Mode = "--Auth:Mode=jwt", Iss = $"--Auth:Issuer={Hidden}", Aud = $"--Auth:Audience={Hidden}", Dev1 = $"--Auth:DevSigningKey={Hidden}";

    public static TheoryData<string, string[]> BadConfigs() => new()
    {
        { "Auth:Mode", ["--Auth:Mode=hidden-mode"] },
        { "Auth:Issuer", [Mode, Aud, Dev1] },
        { "Auth:Audience", [Mode, Iss, Dev1] },
        { "Exactly one", [Mode, Iss, Aud] },
        { "Exactly one", [Mode, Iss, Aud, Dev1, "--Auth:Authority=https://hidden.example"] },
        { "32 bytes", [Mode, Iss, Aud, "--Auth:DevSigningKey=hidden-key-of-31-bytes-12345678"] },
        { "Development", [Mode, Iss, Aud, Dev1, "--environment=Production"] },
        { "Development", [Mode, Iss, Aud, Dev1, "--environment=Staging"] },
        { "Development", [Mode, Iss, Aud, Dev1, "--environment=UAT"] },
        { "Development", [Mode, Iss, Aud, Dev1, "--environment=Dev"] },
        { "Auth:Authority", [Mode, Iss, Aud, "--Auth:Authority=http://hidden.example"] },
        { "Auth:Authority", [Mode, Iss, Aud, "--Auth:Authority=hidden/relative"] },
        { "Auth:Authority", [Mode, Iss, Aud, "--Auth:Authority=ftp://hidden.example"] },
    };

    [Theory]
    [MemberData(nameof(BadConfigs))]
    public void Bad_auth_configuration_is_a_startup_error_that_echoes_no_value(string reason, string[] args)
    {
        // Development unless the case says otherwise, so each case fails for its own reason
        string[] all = args.Any(a => a.StartsWith("--environment")) ? args : [.. args, "--environment=Development"];
        var e = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(all));
        Assert.Contains(reason, e.Message);
        Assert.DoesNotContain("hidden", e.ToString());
    }

    [Fact]
    public async Task The_dev_key_is_refused_by_default_and_accepted_at_32_bytes_in_development()
    {
        var args = JwtArgs.Where(a => !a.StartsWith("--environment")).ToArray(); // the default environment is Production
        Assert.Contains("Development", Assert.Throws<InvalidOperationException>(() => ApiHost.Build(args)).Message);
        await using var ok = ApiHost.Build([.. JwtArgs, $"--Auth:DevSigningKey={new string('k', 32)}"]);
    }

    [Theory]
    [InlineData("https://login.example/tenant")]
    [InlineData("http://localhost:1234")]
    [InlineData("http://127.0.0.1:1234")]
    public async Task An_https_or_loopback_authority_is_accepted(string authority)
    {
        await using var app = ApiHost.Build([Mode, $"--Auth:Issuer={Issuer}", $"--Auth:Audience={Audience}", $"--Auth:Authority={authority}"]);
    }

    // ---- bad tokens

    [Fact]
    public async Task Every_bad_token_gets_the_same_401()
    {
        // a long key, so HS384/HS512 tokens signed with this very key are refused for the algorithm alone
        const string key = Key + Key;
        await using var app = await Start(null, $"--Auth:DevSigningKey={key}");
        using var c = Client(app);
        string Make(Dictionary<string, object>? claims = null, SigningCredentials? signing = null, string issuer = Issuer, string audience = Audience, TimeSpan? expires = null, TimeSpan? notBefore = null, bool noExpiry = false) =>
            Mint(claims ?? Claims("alice"), signing ?? Hs256(key), issuer, audience, expires, notBefore, noExpiry);
        var noSub = Claims("x");
        noSub.Remove("sub");
        var twoSubs = Claims("x");
        twoSubs["sub"] = new[] { "a", "b" };
        using var rsa = RSA.Create(2048);
        var unsigned = $"{Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""")}.{Make().Split('.')[1]}.";
        var bad = new Dictionary<string, string?>
        {
            ["none"] = null,
            ["empty"] = "",
            ["malformed"] = "not.a.token",
            ["wrong key"] = Make(signing: Hs256("another-signing-key-0123456789-abcdefgh")),
            ["wrong issuer"] = Make(issuer: "https://other.test"),
            ["wrong audience"] = Make(audience: "other"),
            ["expired"] = Make(expires: TimeSpan.FromSeconds(-120)),
            ["not yet valid"] = Make(notBefore: TimeSpan.FromSeconds(120)),
            ["no exp"] = Make(noExpiry: true),
            ["no sub"] = Make(noSub),
            ["empty sub"] = Make(Claims("")),
            ["blank sub"] = Make(Claims("   ")),
            ["long sub"] = Make(Claims(new string('s', 129))),
            ["two subs"] = Make(twoSubs),
            ["sub worker"] = Make(Claims("worker")),
            ["sub anonymous"] = Make(Claims("anonymous")),
            ["sub with newline"] = Make(Claims("ali\nce")),
            ["sub with NUL"] = Make(Claims("ali\0ce")),
            ["alg none"] = unsigned,
            ["HS384"] = Make(signing: Hs256(key, SecurityAlgorithms.HmacSha384)),
            ["HS512"] = Make(signing: Hs256(key, SecurityAlgorithms.HmacSha512)),
            ["RS256"] = Make(signing: new SigningCredentials(new RsaSecurityKey(rsa.ExportParameters(true)), SecurityAlgorithms.RsaSha256)),
        };
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, "/v1/runs/x", Make())).Status); // control: a good token gets through

        string? body = null;
        foreach (var (name, token) in bad)
        {
            var r = await Get(c, "/v1/runs/x", token);
            Assert.True(r.Status == HttpStatusCode.Unauthorized, $"{name}: {r.Status}");
            Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Bearer", r.Message.Headers.WwwAuthenticate.ToString());
            Assert.NotEmpty(r.Message.Headers.GetValues("X-Correlation-Id"));
            body ??= r.Text;
            Assert.True(body == r.Text, $"{name}: {r.Text}");
        }
    }

    [Fact]
    public async Task Tokens_within_the_limits_are_accepted()
    {
        await using var app = await Start();
        using var c = Client(app);
        foreach (var token in new[]
        {
            Mint(Claims(new string('s', 128)), Hs256()),
            Mint(Claims("Worker"), Hs256()),      // the reserved names are compared exactly
            Mint(Claims("anonymous2"), Hs256()),
            Mint(Claims("alice"), Hs256(), expires: TimeSpan.FromSeconds(-30)),   // 60 s clock skew
            Mint(Claims("alice"), Hs256(), notBefore: TimeSpan.FromSeconds(30)),
            Mint(new() { ["sub"] = "alice", ["roles"] = "analyst", ["deals"] = "d1" }, Hs256()),   // single strings
        })
            Assert.Equal(HttpStatusCode.NotFound, (await Get(c, "/v1/runs/x", token)).Status);
    }

    [Fact]
    public async Task Every_route_but_the_anonymous_ones_and_every_unknown_path_needs_a_token()
    {
        await using var app = await Start();
        using var c = Client(app);
        var targets = Routes(app).Where(r => !r.Anonymous).Select(r => (r.Method, Url: Fill(r.Route)))
            .Concat([("GET", "/v1/nope"), ("POST", "/v1/nope"), ("GET", "/nope"), ("DELETE", "/v1/runs")]).ToList();
        Assert.True(targets.Count >= 11);
        foreach (var (method, url) in targets)
        {
            var r = await Send(c, new HttpMethod(method), url, null, method == "POST" ? "{}" : null);
            Assert.True(r.Status == HttpStatusCode.Unauthorized, $"{method} {url}: {r.Status}");
            Assert.Equal("Bearer", r.Message.Headers.WwwAuthenticate.ToString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, "/v1/nope", Alice)).Status);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(c, HttpMethod.Delete, "/v1/runs", Alice)).Status);
    }

    [Fact]
    public async Task Health_and_openapi_are_anonymous()
    {
        await using var app = await Start();
        using var c = Client(app);
        Assert.Equal(new[] { "/health/live", "/health/ready", "/health/startup", "/openapi/v1.json" }, Routes(app).Where(r => r.Anonymous).Select(r => r.Route).Order());
        foreach (var url in new[] { "/health/live", "/health/startup", "/health/ready", "/openapi/v1.json" })
            Assert.Equal(HttpStatusCode.OK, (await Get(c, url, null)).Status);
    }

    // ---- authority mode

    [Fact]
    public async Task With_an_authority_only_its_asymmetric_keys_and_the_configured_issuer_are_accepted()
    {
        using var rsa = RSA.Create(2048);
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = rsa.ExportParameters(false);
        var e = ec.ExportParameters(false);
        static string B(byte[] b) => Base64UrlEncoder.Encode(b);
        var jwks = $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"r","n":"{{B(r.Modulus!)}}","e":"{{B(r.Exponent!)}}"},{"kty":"EC","use":"sig","kid":"e","crv":"P-256","x":"{{B(e.Q.X!)}}","y":"{{B(e.Q.Y!)}}"}]}""";
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var idp = builder.Build();
        idp.MapGet("/.well-known/openid-configuration", () => Results.Content($$"""{"issuer":"https://discovered.test","jwks_uri":"{{idp.Urls.First()}}/keys"}""", "application/json"));
        idp.MapGet("/keys", () => Results.Content(jwks, "application/json"));
        await idp.StartAsync();
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", Mode, $"--Auth:Issuer={Issuer}", $"--Auth:Audience={Audience}", $"--Auth:Authority={idp.Urls.First()}"]);
        await app.StartAsync();
        using var c = Client(app);

        var rsaKey = new RsaSecurityKey(rsa) { KeyId = "r" };
        var ecKey = new ECDsaSecurityKey(ec) { KeyId = "e" };
        async Task<HttpStatusCode> With(SecurityKey key, string alg, string issuer = Issuer) =>
            (await Get(c, "/v1/runs/x", Mint(Claims("alice"), new SigningCredentials(key, alg), issuer))).Status;

        Assert.Equal(HttpStatusCode.NotFound, await With(rsaKey, SecurityAlgorithms.RsaSha256));
        Assert.Equal(HttpStatusCode.NotFound, await With(rsaKey, SecurityAlgorithms.RsaSsaPssSha256));
        Assert.Equal(HttpStatusCode.NotFound, await With(ecKey, SecurityAlgorithms.EcdsaSha256));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(rsaKey, SecurityAlgorithms.RsaSha384));
        Assert.Equal(HttpStatusCode.Unauthorized, await With(rsaKey, SecurityAlgorithms.RsaSha256, "https://discovered.test"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/v1/runs/x", Alice)).Status); // HS256
    }

    // ---- roles and publish

    [Fact]
    public async Task Roles_decide_who_may_call_what()
    {
        await using var app = await Start();
        using var c = Client(app);
        var set = await Published(c, Alice, Tweaked(201), "d1");
        var run = (await Run(c, Alice, set)).Str("runKey");

        foreach (var nobody in new[] { Token("n", "", "d1"), Token("n", "boss,Analyst", "d1") })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, $"/v1/runs/{run}", nobody)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Run(c, nobody, set)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, "/v1/nope", nobody)).Status);
        }

        var reviewer = Token("rita", "reviewer", "d1");
        var posts = Routes(app).Where(r => r.Method == "POST").Select(r => r.Route).Order().ToList();
        Assert.Equal(new[] { "/v1/assumption-sets", "/v1/assumption-sets/excel", "/v1/runs", "/v1/sweeps" }, posts);
        var attempts = new[]
        {
            await Publish(c, reviewer, Tweaked(202), "d1"),
            await Run(c, reviewer, set),
            await Sweep(c, reviewer, set),
            await Send(c, HttpMethod.Post, "/v1/assumption-sets/excel", reviewer, "{}", "d1"), // 403 comes before the content type
        };
        Assert.All(attempts, r =>
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.Status);
            Assert.Equal("application/problem+json", r.Message.Content.Headers.ContentType?.MediaType);
            Assert.Equal(403, r.Json["status"]!.GetValue<int>());
        });
        foreach (var url in new[] { $"/v1/assumption-sets/{set}", $"/v1/runs/{run}" })
            Assert.Equal(HttpStatusCode.OK, (await Get(c, url, reviewer)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Run(c, Token("both", "reviewer,analyst", "d1"), set)).Status);
    }

    [Fact]
    public async Task Publish_needs_a_deal_the_caller_may_use()
    {
        await using var app = await Start();
        using var c = Client(app);
        var a = Tweaked(203);
        var missing = await Publish(c, Alice, a, null);
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("X-Deal-Id", missing.Str("detail"));
        foreach (var bad in new[] { "", "has space", "d1,d2", new string('d', 65) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Publish(c, Alice, a, bad)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Publish(c, Root, a, "has space")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Publish(c, Alice, a, "d2")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Publish(c, Alice, a, "D1")).Status); // deal ids are case-sensitive
        Assert.False(app.Services.GetRequiredService<IAssumptionSetStore>().TryGet(AssumptionSetId.Compute(a), out _));
        var own = await Publish(c, Alice, a, "d1");
        Assert.Equal(HttpStatusCode.Created, own.Status);
        Assert.Equal(HttpStatusCode.Created, (await Publish(c, Root, Tweaked(204), "any-deal")).Status);
        Assert.Equal(["d1"], app.Services.GetRequiredService<IDealAccess>().DealsOf(DealResource.Set, own.Str("id")));
        var single = Mint(new() { ["sub"] = "sam", ["roles"] = "analyst", ["deals"] = "d7" }, Hs256());
        Assert.Equal(HttpStatusCode.Created, (await Publish(c, single, Tweaked(210), "d7")).Status);
    }

    [Fact]
    public async Task An_analyst_with_two_shared_deals_must_name_one_and_only_that_one_is_granted()
    {
        await using var app = await Start();
        using var c = Client(app);
        var access = app.Services.GetRequiredService<IDealAccess>();
        var a = Tweaked(205);
        var set = await Published(c, Root, a, "d1");
        await Published(c, Root, a, "d2");
        await Published(c, Root, a, "d4");
        var ann = Token("ann", "analyst", "d1,d2,d3,bad deal");
        var key = RunKey.Compute(a, a.Scenarios.First(s => s.Name == "Base"), EngineInfo.Version, WorkbookQuirks.None);

        foreach (var (deal, expected) in new (string?, HttpStatusCode)[] { (null, HttpStatusCode.BadRequest), ("", HttpStatusCode.BadRequest), ("bad deal", HttpStatusCode.BadRequest),
            ("d1,d2", HttpStatusCode.BadRequest), ("d3", HttpStatusCode.Forbidden), ("d4", HttpStatusCode.Forbidden), ("D1", HttpStatusCode.Forbidden) })
        {
            var run = await Run(c, ann, set, deal);
            var sweep = await Sweep(c, ann, set, "k-refused", deal);
            Assert.True(run.Status == expected && sweep.Status == expected, $"'{deal}': {run.Status} {sweep.Status}");
            if (deal is null) Assert.Contains("X-Deal-Id", run.Str("detail"));
        }
        Assert.False(app.Services.GetRequiredService<IRunStore>().TryGet(key, out _)); // nothing computed or stored
        var ledger = ((InMemoryLedger)app.Services.GetRequiredService<ILedger>()).Entries;
        Assert.DoesNotContain(ledger, e => e.Actor is "ann" or "worker");

        var ok = await Run(c, ann, set, "d2");
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Equal(["d2"], access.DealsOf(DealResource.Run, key));
        var job = await Sweep(c, ann, set, "k1", "d2");
        Assert.Equal(HttpStatusCode.Accepted, job.Status);
        Assert.Equal(["d2"], access.DealsOf(DealResource.Job, job.Str("jobId")));
        await WaitFor(c, ann, job.Str("jobId"));
        foreach (var k in await RunKeys(c, ann, job.Str("jobId"))) Assert.Equal(["d2"], access.DealsOf(DealResource.Run, k));
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, $"/v1/jobs/{job.Str("jobId")}", Alice)).Status);

        // the same key for another deal is another job; for the same deal it is the same one
        var other = await Sweep(c, ann, set, "k1", "d1");
        Assert.NotEqual(job.Str("jobId"), other.Str("jobId"));
        Assert.Equal(["d1"], access.DealsOf(DealResource.Job, other.Str("jobId")));
        Assert.Equal(job.Str("jobId"), (await Sweep(c, ann, set, "k1", "d2")).Str("jobId"));
        Assert.Equal(["d2"], access.DealsOf(DealResource.Job, job.Str("jobId")));
        // with one shared deal the header is optional and names the same scope
        Assert.Equal(other.Str("jobId"), (await Sweep(c, Token("ann", "analyst", "d1"), set, "k1")).Str("jobId"));
    }

    [Fact]
    public async Task An_admin_picks_any_deal_of_a_two_deal_set()
    {
        await using var app = await Start(null, "--Role=api");
        using var c = Client(app);
        var access = app.Services.GetRequiredService<IDealAccess>();
        var set = await Published(c, Root, Tweaked(206), "d1");
        await Published(c, Root, Tweaked(206), "d2");
        IEnumerable<string> Of(DealResource kind, Reply r) => access.DealsOf(kind, r.Str(kind == DealResource.Run ? "runKey" : "jobId"));
        var member = Token("root", "admin", "d2");

        // no header: the one it shares, if any
        Assert.Equal(["d2"], Of(DealResource.Job, await Sweep(c, member, set)));
        // no header and none shared: two candidates, so it must choose
        Assert.Equal(HttpStatusCode.BadRequest, (await Run(c, Root, set)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Sweep(c, Root, set)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Sweep(c, Token("root", "admin", "d1,d2"), set)).Status);
        // header: any deal of the set, membership or not, and no other
        Assert.Equal(HttpStatusCode.Forbidden, (await Run(c, Root, set, "d3")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Sweep(c, member, set, deal: "d3")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Run(c, Root, set, "bad deal")).Status);
        Assert.Equal(["d1"], Of(DealResource.Run, await Run(c, member, set, "d1")));
        Assert.Equal(["d1"], Of(DealResource.Job, await Sweep(c, Root, set, deal: "d1")));
        // one deal on the set: no header needed
        var one = await Published(c, Root, Tweaked(211), "d5");
        Assert.Equal(["d5"], Of(DealResource.Run, await Run(c, Root, one)));
    }

    // ---- isolation and grants

    private static async Task OnHost(Func<HttpClient, Task> flow)
    {
        await using var app = await Start();
        using var c = Client(app);
        await flow(c);
    }

    [Fact] public Task Another_deal_gets_a_404_identical_to_an_unknown_id() => OnHost(Isolation);
    [Fact] public Task Identical_content_in_two_deals_shares_the_set_but_not_the_runs_until_submitted() => OnHost(SameContentTwoDeals);
    [Fact] public Task A_cached_run_is_granted_to_the_new_submitters_deal() => OnHost(CachedRun);
    [Fact] public Task Every_sweep_result_run_belongs_to_the_jobs_deal() => OnHost(SweepGrants);
    [Fact] public Task Idempotency_keys_are_scoped_per_subject() => OnHost(IdempotencyScope);

    [Fact]
    public async Task A_failed_grant_is_a_500_that_a_repeat_repairs()
    {
        var access = new FlakyAccess(new InMemoryDealAccess());
        await using var app = await Start(s => s.AddSingleton<IDealAccess>(access));
        using var c = Client(app);
        await GrantFailure(app, c, access);
    }

    [Fact]
    public async Task The_ledger_actor_is_the_subject_and_worker_rows_stay_worker()
    {
        await using var app = await Start();
        using var c = Client(app);
        await LedgerActivity(c);
        var entries = ((InMemoryLedger)app.Services.GetRequiredService<ILedger>()).Entries;
        foreach (var action in new[] { LedgerEntry.AssumptionSetPublished, LedgerEntry.SweepSubmitted })
            Assert.Equal("alice", Assert.Single(entries, e => e.Action == action).Actor);
        Assert.Equal(new[] { "alice", "worker", "worker" }, entries.Where(e => e.Action.StartsWith("run.")).Select(e => e.Actor));
    }

    [Fact]
    public async Task Resources_without_a_deal_are_visible_to_admin_only()
    {
        await using var app = await Start(null, "--Role=api");
        using var c = Client(app);
        var s = app.Services;
        // what mode none stores: no grants
        var set = s.GetRequiredService<PublishAssumptionSet>().Publish(Tweaked(207)).Record;
        var run = s.GetRequiredService<SubmitRun>().Submit(set.Assumptions, set.Assumptions.Scenarios[0]).Record.RunKey;
        var job = s.GetRequiredService<SubmitSweep>().Submit(new SweepRequest(set.Id, "Base", [new SweepCase([])])).Job.Id;
        Assert.True(s.GetRequiredService<SweepWorker>().ProcessOne("w"));
        await OnlyAdminSees(c, set.Id, run, job);

        // an admin sweeping such a set has no deal to give: the job still runs, for admins only
        var adminJob = (await Sweep(c, Root, set.Id)).Str("jobId");
        Assert.True(s.GetRequiredService<SweepWorker>().ProcessOne("w"));
        await WaitFor(c, Root, adminJob);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, $"/v1/jobs/{adminJob}", Alice)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Run(c, Root, set.Id, "d1")).Status); // not a deal of the set
    }

    // ---- dev token

    private static (int Exit, string Out, string Err) Dev(params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = DevToken.Run(args, o, e);
        return (exit, o.ToString().Trim(), e.ToString());
    }

    private static string[] DevArgs(params string[] extra) =>
        [.. JwtArgs.Where(a => a.StartsWith("--Auth:") || a.StartsWith("--environment")), "--sub", "dev", "--roles", "analyst", "--deals", "d1,d2", .. extra];

    [Fact]
    public async Task A_dev_token_is_accepted_by_the_api()
    {
        var (exit, token, err) = Dev(DevArgs());
        Assert.True(exit == 0, err);
        var parsed = new JsonWebToken(token);
        Assert.Equal(("HS256", "dev"), (parsed.Alg, parsed.Subject));
        Assert.Equal(TimeSpan.FromHours(8), parsed.ValidTo - parsed.ValidFrom);
        var day = new JsonWebToken(Dev(DevArgs("--hours", "24")).Out);
        Assert.Equal(TimeSpan.FromHours(24), day.ValidTo - day.ValidFrom);
        await using var app = await Start();
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.Created, (await Publish(c, token, Tweaked(208), "d2")).Status);
    }

    private static async Task<(int Exit, string Out, string Err)> DevProcess(string? environment)
    {
        // DOTNET_HOST_PATH is set by `dotnet test`
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])[typeof(ApiHost).Assembly.Location, "dev-token", "--sub", "dev", "--roles", "analyst", "--deals", "d1"]) start.ArgumentList.Add(a);
        foreach (var name in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" }) start.Environment.Remove(name);
        if (environment is not null) start.Environment["ASPNETCORE_ENVIRONMENT"] = environment;
        start.Environment["Auth__Issuer"] = Issuer;
        start.Environment["Auth__Audience"] = Audience;
        start.Environment["Auth__DevSigningKey"] = Key;
        using var p = Process.Start(start)!;
        var (o, e) = (p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        return (p.ExitCode, await o, await e);
    }

    [Fact]
    public async Task The_dev_token_command_prints_only_the_token()
    {
        var (exit, output, error) = await DevProcess("Development");
        Assert.True(exit == 0, error);
        var token = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Trim();
        Assert.Equal("dev", new JsonWebToken(token).Subject);
        await using var app = await Start();
        using var c = Client(app);
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, "/v1/runs/x", token)).Status);
    }

    [Fact]
    public async Task The_dev_token_command_refuses_in_the_default_environment()
    {
        var (exit, output, error) = await DevProcess(null);
        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains("Development", error);
    }

    [Theory]
    [InlineData("--environment=Production")]
    [InlineData("--environment=Staging")]
    [InlineData("--sub", "worker")]
    [InlineData("--sub", "anonymous")]
    [InlineData("--sub", "a\tb")]
    [InlineData("--deals", "-d1")]
    [InlineData("--deals", "d1,-d2")]
    [InlineData("-x=1")]
    [InlineData("--Auth:DevSigningKey=")]
    [InlineData("--Auth:DevSigningKey=short")]
    [InlineData("--Auth:Issuer=")]
    [InlineData("--hours", "25")]
    [InlineData("--hours", "0")]
    [InlineData("--hours", "soon")]
    [InlineData("--sub", "")]
    [InlineData("--roles", "boss")]
    [InlineData("--deals", "bad deal")]
    public void A_refused_dev_token_exits_non_zero_and_prints_no_token(params string[] extra)
    {
        var (exit, output, error) = Dev(DevArgs(extra));
        Assert.NotEqual(0, exit);
        Assert.Equal("", output);
        Assert.NotEqual("", error);
        Assert.DoesNotContain(Key, error);
    }

    // ---- leaks and contract

    internal sealed class Capture : ILoggerProvider, ILogger
    {
        public readonly ConcurrentQueue<string> Lines = new();
        public readonly ConcurrentQueue<(LogLevel Level, string Text)> Entries = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull { Add(state); return null; }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? e, Func<TState, Exception?, string> format) { Entries.Enqueue((level, format(state, e))); Lines.Enqueue(format(state, e) + " " + e); Add(state); }
        private void Add(object? state) => Lines.Enqueue(state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}")) : $"{state}");
        public void Dispose() { }
    }

    [Fact]
    public async Task No_token_key_or_claim_value_reaches_logs_or_spans()
    {
        var capture = new Capture();
        var tags = new ConcurrentQueue<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { foreach (var t in a.TagObjects) tags.Enqueue($"{t.Key}={t.Value}"); },
        };
        ActivitySource.AddActivityListener(listener);
        await using var app = await Start(s => s.AddSingleton<ILoggerProvider>(capture), "--Logging:LogLevel:Default=Trace", "--Logging:LogLevel:Microsoft.AspNetCore=Trace");
        using var c = Client(app);
        const string deal = "zq-private-deal";
        var good = Token("carol", "analyst", deal);
        var bad = new[]
        {
            Mint(Claims("carol", "analyst", deal), Hs256("another-signing-key-0123456789-abcdefgh")),
            Mint(Claims("carol", "analyst", deal), Hs256(), expires: TimeSpan.FromSeconds(-600)),
            Mint(Claims("carol", "analyst", deal), Hs256(), audience: "zq-wrong-audience"),
        };
        foreach (var t in bad) Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, "/v1/runs/x", t)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await Get(c, "/v1/runs/x", Token("carol", "zq-odd-role", deal))).Status);
        var set = await Published(c, good, Tweaked(209), deal);
        await Run(c, good, set);
        await WaitFor(c, good, (await Sweep(c, good, set, "leak-key")).Str("jobId"));

        Assert.NotEmpty(tags); // the span listener works too
        var text = string.Join("\n", capture.Lines.Concat(tags));
        Assert.Contains("Bearer token rejected", text); // the capture works
        Assert.DoesNotContain("IDX", text);             // no token library message (they quote lifetimes and audiences)
        Assert.DoesNotContain(Key, text);
        foreach (var value in new[] { deal, "zq-wrong-audience", "zq-odd-role", Issuer }) Assert.DoesNotContain(value, text);
        foreach (var t in bad.Append(good))
            foreach (var part in t.Split('.').Skip(1)) Assert.DoesNotContain(part, text);
    }

    [Fact]
    public async Task OpenApi_documents_the_bearer_scheme_the_deal_header_and_401_403_on_protected_routes()
    {
        await using var app = await Start();
        using var c = Client(app);
        var doc = JsonNode.Parse((await Get(c, "/openapi/v1.json", null)).Text)!;
        Assert.Equal("bearer", doc["components"]!["securitySchemes"]!["bearerAuth"]!["scheme"]!.GetValue<string>());
        Assert.NotNull(doc["security"]![0]!["bearerAuth"]);
        var documented = doc["paths"]!.AsObject().SelectMany(p => p.Value!.AsObject().Select(m => (m.Key.ToUpperInvariant(), p.Key))).Order().ToList();
        Assert.Equal(Routes(app).Select(r => (r.Method, r.Route)).Order().ToList(), documented);
        foreach (var (method, route, anonymous) in Routes(app))
        {
            var op = doc["paths"]![route]![method.ToLowerInvariant()]!;
            if (anonymous) Assert.Empty(op["security"]!.AsArray());
            else Assert.Null(op["security"]);
            foreach (var status in new[] { "401", "403" })
                if (anonymous) Assert.Null(op["responses"]![status]);
                else Assert.NotNull(op["responses"]![status]!["content"]!["application/problem+json"]);
        }
        foreach (var route in new[] { "/v1/assumption-sets", "/v1/assumption-sets/excel", "/v1/runs", "/v1/sweeps" })
        {
            var post = doc["paths"]![route]!["post"]!;
            var header = Assert.Single(post["parameters"]!.AsArray(), x => x!["name"]!.GetValue<string>() == "X-Deal-Id")!;
            Assert.Equal("header", header["in"]!.GetValue<string>());
            foreach (var status in new[] { "400", "403" }) Assert.Contains("X-Deal-Id", post["responses"]![status]!["description"]!.GetValue<string>());
        }
    }
}
