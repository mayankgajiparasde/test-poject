using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HotelUnderwriting.Api.Tests;

/// <summary>A jwt-mode host, token minting and the access flows. Also compiled into the persistence tests so PostgreSQL runs the same flows.</summary>
internal static class AuthRig
{
    public const string Issuer = "https://issuer.test", Audience = "hue-api", Key = "unit-test-signing-key-0123456789-abcdef";

    public static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));

    /// <summary>A set no other test uses, so grants and cached runs do not interfere.</summary>
    public static Assumptions Tweaked(int n) => A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + n } : s).ToArray() };

    public static string[] JwtArgs => ["--urls", "http://127.0.0.1:0", "--environment=Development", "--Auth:Mode=jwt", $"--Auth:Issuer={Issuer}", $"--Auth:Audience={Audience}", $"--Auth:DevSigningKey={Key}"];

    public static async Task<WebApplication> Start(Action<IServiceCollection>? services = null, params string[] extra)
    {
        var app = ApiHost.Build([.. JwtArgs, .. extra], services);
        await app.StartAsync();
        return app;
    }

    public static HttpClient Client(WebApplication app) => new() { BaseAddress = new Uri(app.Urls.First()) };

    public static SigningCredentials Hs256(string key = Key, string algorithm = SecurityAlgorithms.HmacSha256) =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm);

    public static Dictionary<string, object> Claims(string sub, string roles = "analyst", string deals = "") => new()
    {
        ["sub"] = sub,
        ["roles"] = roles.Split(',', StringSplitOptions.RemoveEmptyEntries),
        ["deals"] = deals.Split(',', StringSplitOptions.RemoveEmptyEntries),
    };

    /// <summary>Offsets are from now.</summary>
    public static string Mint(Dictionary<string, object> claims, SigningCredentials signing, string issuer = Issuer, string audience = Audience, TimeSpan? expires = null, TimeSpan? notBefore = null, bool noExpiry = false)
    {
        var now = DateTime.UtcNow;
        var d = new SecurityTokenDescriptor { Issuer = issuer, Audience = audience, Claims = claims, SigningCredentials = signing };
        if (!noExpiry) d.Expires = now + (expires ?? TimeSpan.FromHours(1));
        if (notBefore is { } nb) d.NotBefore = now + nb;
        else if (!noExpiry) d.NotBefore = d.Expires!.Value - TimeSpan.FromHours(2);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(d);
    }

    public static string Token(string sub, string roles = "analyst", string deals = "") => Mint(Claims(sub, roles, deals), Hs256());

    public static readonly string Alice = Token("alice", "analyst", "d1"), Bob = Token("bob", "analyst", "d2"), Root = Token("root", "admin");

    public sealed record Reply(HttpStatusCode Status, string Text, HttpResponseMessage Message)
    {
        public JsonObject Json => JsonNode.Parse(Text)!.AsObject();
        public string Str(string name) => Json[name]!.GetValue<string>();
    }

    public static async Task<Reply> Send(HttpClient c, HttpMethod m, string url, string? token, string? body = null, string? deal = null, string? key = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (token is not null) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (deal is not null) req.Headers.TryAddWithoutValidation("X-Deal-Id", deal);
        if (key is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var res = await c.SendAsync(req);
        return new Reply(res.StatusCode, await res.Content.ReadAsStringAsync(), res);
    }

    public static Task<Reply> Get(HttpClient c, string url, string? token) => Send(c, HttpMethod.Get, url, token);
    public static Task<Reply> Publish(HttpClient c, string token, Assumptions a, string? deal) => Send(c, HttpMethod.Post, "/v1/assumption-sets", token, JsonSerializer.Serialize(a, ApiHost.Json), deal);
    public static Task<Reply> Run(HttpClient c, string token, string setId, string? deal = null) => Send(c, HttpMethod.Post, "/v1/runs", token, $$"""{"assumptionSetId":"{{setId}}","scenario":"Base"}""", deal);
    public static string SweepBody(string setId) =>
        $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","cases":[{"overrides":[{"path":"interestRate","value":0.061}]},{"overrides":[{"path":"interestRate","value":0.071}]}]}""";
    public static Task<Reply> Sweep(HttpClient c, string token, string setId, string? key = null, string? deal = null) => Send(c, HttpMethod.Post, "/v1/sweeps", token, SweepBody(setId), deal, key);

    public static async Task<string> Published(HttpClient c, string token, Assumptions a, string deal)
    {
        var r = await Publish(c, token, a, deal);
        Assert.True(r.Status is HttpStatusCode.Created or HttpStatusCode.OK, $"publish: {r.Status} {r.Text}");
        return r.Str("id");
    }

    public static async Task WaitFor(HttpClient c, string? token, string jobId)
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            var r = await Get(c, $"/v1/jobs/{jobId}", token);
            Assert.Equal(HttpStatusCode.OK, r.Status);
            if (r.Str("status") == "succeeded") return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"job {jobId} never succeeded");
    }

    public static async Task<string[]> RunKeys(HttpClient c, string token, string jobId) =>
        (await Get(c, $"/v1/jobs/{jobId}/results", token)).Json["items"]!.AsArray().Select(i => i!["runKey"]!.GetValue<string>()).ToArray();

    private static async Task Is(HttpStatusCode expected, Task<Reply> reply)
    {
        var r = await reply;
        Assert.True(r.Status == expected, $"expected {expected}, got {r.Status}: {r.Text}");
    }

    /// <summary>The hidden id and an id that does not exist must be answered the same, apart from the echoed id.</summary>
    private static async Task HiddenLikeMissing(Func<string, Task<Reply>> use, string hidden)
    {
        const string missing = "no-such-id";
        var (h, m) = (await use(hidden), await use(missing));
        Assert.Equal(HttpStatusCode.NotFound, h.Status);
        Assert.Equal(m.Status, h.Status);
        Assert.Equal(m.Text.Replace(missing, "*"), h.Text.Replace(hidden, "*"));
        Assert.Equal(m.Message.Content.Headers.ContentType?.ToString(), h.Message.Content.Headers.ContentType?.ToString());
    }

    public static async Task Isolation(HttpClient c)
    {
        var set = await Published(c, Alice, Tweaked(101), "d1");
        var run = (await Run(c, Alice, set)).Str("runKey");
        var job = (await Sweep(c, Alice, set)).Str("jobId");
        await WaitFor(c, Alice, job);

        await HiddenLikeMissing(id => Get(c, $"/v1/assumption-sets/{id}", Bob), set);
        await HiddenLikeMissing(id => Run(c, Bob, id), set);
        await HiddenLikeMissing(id => Sweep(c, Bob, id), set);
        // visibility is decided before X-Deal-Id is looked at
        foreach (var deal in new[] { "d1", "d2", "bad deal" })
        {
            await HiddenLikeMissing(id => Run(c, Bob, id, deal), set);
            await HiddenLikeMissing(id => Sweep(c, Bob, id, deal: deal), set);
        }
        // not even the scenario names leak
        await HiddenLikeMissing(id => Send(c, HttpMethod.Post, "/v1/runs", Bob, $$"""{"assumptionSetId":"{{id}}","scenario":"NoSuchScenario"}"""), set);
        await HiddenLikeMissing(id => Get(c, $"/v1/runs/{id}", Bob), run);
        await HiddenLikeMissing(id => Get(c, $"/v1/jobs/{id}", Bob), job);
        await HiddenLikeMissing(id => Get(c, $"/v1/jobs/{id}/results", Bob), job);
        // a reviewer is bound to deals like an analyst
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{run}", Token("rita", "reviewer", "d2")));

        foreach (var token in new[] { Alice, Root, Token("rita", "reviewer", "d1") })
            foreach (var url in new[] { $"/v1/assumption-sets/{set}", $"/v1/runs/{run}", $"/v1/jobs/{job}", $"/v1/jobs/{job}/results" })
                await Is(HttpStatusCode.OK, Get(c, url, token));
    }

    public static async Task SameContentTwoDeals(HttpClient c)
    {
        var a = Tweaked(102);
        var set = await Published(c, Alice, a, "d1");
        var run = (await Run(c, Alice, set)).Str("runKey");
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/assumption-sets/{set}", Bob));
        Assert.Equal(set, await Published(c, Bob, a, "d2"));
        foreach (var token in new[] { Alice, Bob }) await Is(HttpStatusCode.OK, Get(c, $"/v1/assumption-sets/{set}", token));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{run}", Bob));
        Assert.Equal(run, (await Run(c, Bob, set)).Str("runKey"));
        foreach (var token in new[] { Alice, Bob }) await Is(HttpStatusCode.OK, Get(c, $"/v1/runs/{run}", token));
    }

    public static async Task CachedRun(HttpClient c)
    {
        var a = Tweaked(103);
        var set = await Published(c, Alice, a, "d1");
        await Published(c, Bob, a, "d2");
        var first = await Run(c, Alice, set);
        Assert.False(first.Json["wasCached"]!.GetValue<bool>());
        var again = await Run(c, Bob, set);
        Assert.True(again.Json["wasCached"]!.GetValue<bool>());
        await Is(HttpStatusCode.OK, Get(c, $"/v1/runs/{first.Str("runKey")}", Bob));
    }

    public static async Task SweepGrants(HttpClient c)
    {
        var set = await Published(c, Alice, Tweaked(104), "d1");
        var submitted = await Sweep(c, Alice, set);
        Assert.Equal(HttpStatusCode.Accepted, submitted.Status);
        var job = submitted.Str("jobId");
        await WaitFor(c, Alice, job);
        var keys = await RunKeys(c, Alice, job);
        Assert.Equal(2, keys.Distinct().Count());
        foreach (var key in keys)
        {
            await Is(HttpStatusCode.OK, Get(c, $"/v1/runs/{key}", Alice));
            await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{key}", Bob));
        }
    }

    public static async Task IdempotencyScope(HttpClient c)
    {
        var a = Tweaked(105);
        var set = await Published(c, Alice, a, "d1");
        await Published(c, Bob, a, "d2");
        var key = new string('k', SweepLimits.MaxIdempotencyKeyLength);
        var longSub = Token(new string('s', 128), "analyst", "d1");
        var (alice, bob, big) = (await Sweep(c, Alice, set, key), await Sweep(c, Bob, set, key), await Sweep(c, longSub, set, key));
        Assert.All(new[] { alice, bob, big }, r => Assert.Equal(HttpStatusCode.Accepted, r.Status));
        Assert.Equal(3, new[] { alice, bob, big }.Select(r => r.Str("jobId")).Distinct().Count());
        Assert.Equal(alice.Str("jobId"), (await Sweep(c, Alice, set, key)).Str("jobId"));
        Assert.Equal(bob.Str("jobId"), (await Sweep(c, Bob, set, key)).Str("jobId"));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/jobs/{alice.Str("jobId")}", Bob));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/jobs/{bob.Str("jobId")}", Alice));
        var other = await Send(c, HttpMethod.Post, "/v1/sweeps", Alice, SweepBody(set).Replace("0.061", "0.062"), key: key);
        Assert.Equal(HttpStatusCode.Conflict, other.Status); // within one caller a key still means one request
    }

    /// <summary>Publish, sync run and sweep as alice, for the ledger assertions of the caller. Returns the set id.</summary>
    public static async Task<string> LedgerActivity(HttpClient c)
    {
        var set = await Published(c, Alice, Tweaked(106), "d1");
        await Run(c, Alice, set);
        var job = (await Sweep(c, Alice, set)).Str("jobId");
        await WaitFor(c, Alice, job);
        return set;
    }

    /// <summary>For resources made without deals: an analyst cannot see them, an admin can.</summary>
    public static async Task OnlyAdminSees(HttpClient c, string set, string run, string job)
    {
        foreach (var url in new[] { $"/v1/assumption-sets/{set}", $"/v1/runs/{run}", $"/v1/jobs/{job}", $"/v1/jobs/{job}/results" })
        {
            await Is(HttpStatusCode.NotFound, Get(c, url, Alice));
            await Is(HttpStatusCode.OK, Get(c, url, Root));
        }
        await Is(HttpStatusCode.NotFound, Run(c, Alice, set));
        await Is(HttpStatusCode.NotFound, Sweep(c, Alice, set));
    }

    public sealed class FlakyAccess(IDealAccess inner) : IDealAccess
    {
        public DealResource? FailOnce;
        private int _ungrantedJobLooks;
        /// <summary>How often a job without a grant was looked up. With no request in flight, only a worker that claimed its chunk does that.</summary>
        public int UngrantedJobLooks => Volatile.Read(ref _ungrantedJobLooks);
        public void Grant(DealResource kind, string resourceId, IReadOnlyCollection<string> dealIds)
        {
            if (FailOnce == kind) { FailOnce = null; throw new IOException("access store unavailable"); }
            inner.Grant(kind, resourceId, dealIds);
        }
        public IReadOnlyCollection<string> DealsOf(DealResource kind, string resourceId)
        {
            var deals = inner.DealsOf(kind, resourceId);
            if (kind == DealResource.Job && deals.Count == 0) Interlocked.Increment(ref _ungrantedJobLooks);
            return deals;
        }
    }

    /// <summary>The host must use <paramref name="access"/> as its <see cref="IDealAccess"/>.</summary>
    public static async Task GrantFailure(WebApplication app, HttpClient c, FlakyAccess access)
    {
        var a = Tweaked(107);
        var setId = AssumptionSetId.Compute(a);
        access.FailOnce = DealResource.Set;
        await Is(HttpStatusCode.InternalServerError, Publish(c, Alice, a, "d1"));
        foreach (var token in new[] { Alice, Bob }) await Is(HttpStatusCode.NotFound, Get(c, $"/v1/assumption-sets/{setId}", token));
        await Is(HttpStatusCode.OK, Publish(c, Alice, a, "d1")); // the set was stored, so the repeat is 200
        await Is(HttpStatusCode.OK, Get(c, $"/v1/assumption-sets/{setId}", Alice));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/assumption-sets/{setId}", Bob));

        var baseS = a.Scenarios.First(s => s.Name == "Base");
        var runKey = RunKey.Compute(a, baseS, EngineInfo.Version, WorkbookQuirks.None);
        access.FailOnce = DealResource.Run;
        await Is(HttpStatusCode.InternalServerError, Run(c, Alice, setId));
        foreach (var token in new[] { Alice, Bob }) await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{runKey}", token));
        await Is(HttpStatusCode.OK, Run(c, Alice, setId));
        await Is(HttpStatusCode.OK, Get(c, $"/v1/runs/{runKey}", Alice));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{runKey}", Bob));

        var sweepKeys = new[] { 0.061, 0.071 }.Select(v => RunKey.Compute(a, baseS with { InterestRate = v }, EngineInfo.Version, WorkbookQuirks.None)).ToArray();
        access.FailOnce = DealResource.Job;
        await Is(HttpStatusCode.InternalServerError, Sweep(c, Alice, setId, "grant-fails"));
        // Two more claims by a worker: the first one was over (deferred) before the second, so anything it would compute is stored by now.
        var seen = access.UngrantedJobLooks;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (access.UngrantedJobLooks < seen + 2 && DateTime.UtcNow < until) await Task.Delay(50);
        Assert.True(access.UngrantedJobLooks >= seen + 2, "the ungranted job was not claimed again");
        var runs = app.Services.GetRequiredService<IRunStore>();
        Assert.All(sweepKeys, k => Assert.False(runs.TryGet(k, out _)));
        var retry = await Sweep(c, Alice, setId, "grant-fails");
        Assert.Equal(HttpStatusCode.Accepted, retry.Status);
        await WaitFor(c, Alice, retry.Str("jobId"));
        await Is(HttpStatusCode.NotFound, Get(c, $"/v1/jobs/{retry.Str("jobId")}", Bob));
        Assert.Equal(sweepKeys, await RunKeys(c, Alice, retry.Str("jobId")));
        foreach (var k in sweepKeys)
        {
            Assert.Equal(["d1"], access.DealsOf(DealResource.Run, k));
            await Is(HttpStatusCode.OK, Get(c, $"/v1/runs/{k}", Alice));
            await Is(HttpStatusCode.NotFound, Get(c, $"/v1/runs/{k}", Bob));
        }
    }
}
