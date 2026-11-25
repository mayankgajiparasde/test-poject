using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HotelUnderwriting.Api.Tests;

/// <summary>The real app on a random loopback port.</summary>
public sealed class ServerFixture : IAsyncLifetime
{
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        App = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0" });
        await App.StartAsync();
        Client = new HttpClient { BaseAddress = new Uri(App.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

public class ApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly Dictionary<string, double> Irr = new() { ["Base"] = 0.183860, ["Upside"] = 0.323000, ["Downside"] = -0.038510 };
    private HttpClient C => server.Client;

    private static StringContent Body(object o) => new(JsonSerializer.Serialize(o, ApiHost.Json), Encoding.UTF8, "application/json");
    private static StringContent Raw(string s, string type = "application/json") => new(s, Encoding.UTF8, type);

    private static Assumptions With(Func<ScenarioInputs, ScenarioInputs> change, string name = "Base") =>
        A with { Scenarios = A.Scenarios.Select(s => s.Name == name ? change(s) : s).ToArray() };

    private async Task<(HttpResponseMessage Res, JsonObject Json)> Send(HttpMethod m, string url, HttpContent? body = null)
    {
        var res = await C.SendAsync(new HttpRequestMessage(m, url) { Content = body });
        var text = await res.Content.ReadAsStringAsync();
        return (res, JsonNode.Parse(text)!.AsObject());
    }

    private static void AssertProblem(HttpResponseMessage res, JsonObject json, int status)
    {
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, json["status"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(json["title"]?.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(json["detail"]?.GetValue<string>()));
        Assert.DoesNotContain("Exception", json.ToJsonString());
        Assert.DoesNotContain("   at ", json["detail"]!.GetValue<string>());
    }

    private Task<(HttpResponseMessage Res, JsonObject Json)> Publish(Assumptions a) => Send(HttpMethod.Post, "/v1/assumption-sets", Body(a));
    private Task<(HttpResponseMessage Res, JsonObject Json)> Run(string setId, string scenario) => Send(HttpMethod.Post, "/v1/runs", Body(new { assumptionSetId = setId, scenario }));

    [Fact]
    public async Task Publish_creates_then_repeats_with_same_id()
    {
        var a = With(s => s with { PipPerKey = s.PipPerKey + 1 });
        var (res, json) = await Publish(a);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = json["id"]!.GetValue<string>();
        Assert.Equal(AssumptionSetId.Compute(a), id);
        Assert.Equal($"/v1/assumption-sets/{id}", res.Headers.Location!.ToString());
        Assert.Equal(A.Scenarios.Select(s => s.Name), json["scenarios"]!.AsArray().Select(n => n!.GetValue<string>()));

        var (res2, json2) = await Publish(a);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.Equal(id, json2["id"]!.GetValue<string>());
        Assert.Equal(json["createdUtc"]!.ToJsonString(), json2["createdUtc"]!.ToJsonString());

        var (res3, json3) = await Send(HttpMethod.Get, $"/v1/assumption-sets/{id}");
        Assert.Equal(HttpStatusCode.OK, res3.StatusCode);
        Assert.Equal(id, json3["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Run_each_workbook_scenario_matches_engine_and_workbook_irrs_and_caches()
    {
        var setId = (await Publish(A)).Json["id"]!.GetValue<string>();
        foreach (var s in A.Scenarios)
        {
            var (res, json) = await Run(setId, s.Name);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var direct = Underwriter.Run(A, s);
            Assert.Equal(direct.Summary.LeveredIrr!.Value, json["summary"]!["leveredIrr"]!.GetValue<double>(), 12);
            Assert.Equal(Irr[s.Name], json["summary"]!["leveredIrr"]!.GetValue<double>(), 6);
            Assert.Equal(RunKey.Compute(A, s, EngineInfo.Version, WorkbookQuirks.None), json["runKey"]!.GetValue<string>());
            Assert.Equal(EngineInfo.Version, json["engineVersion"]!.GetValue<string>());
            Assert.Equal(s.Name, json["scenario"]!.GetValue<string>());
            Assert.False(json["wasCached"]!.GetValue<bool>());
            Assert.Null(json["monthly"]);

            var (_, again) = await Run(setId, s.Name);
            Assert.True(again["wasCached"]!.GetValue<bool>());
            Assert.Equal(json["runKey"]!.GetValue<string>(), again["runKey"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Fetch_run_adds_monthly_and_proforma_only_when_requested()
    {
        // Tweaked so this test's run is not the workbook Base run that the IRR test must see computed first.
        var a = With(s => s with { PipPerKey = s.PipPerKey + 2 });
        var setId = (await Publish(a)).Json["id"]!.GetValue<string>();
        var key = (await Run(setId, "Base")).Json["runKey"]!.GetValue<string>();

        var (res, plain) = await Send(HttpMethod.Get, $"/v1/runs/{key}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(Underwriter.Run(a, a.Scenarios.First(s => s.Name == "Base")).Summary.LeveredIrr!.Value, plain["summary"]!["leveredIrr"]!.GetValue<double>(), 12);
        Assert.Null(plain["monthly"]);
        Assert.Null(plain["proforma"]);

        var (_, m) = await Send(HttpMethod.Get, $"/v1/runs/{key}?include=monthly");
        Assert.NotNull(m["monthly"]);
        Assert.Null(m["proforma"]);
        Assert.Equal(Underwriter.Horizon + 1, m["monthly"]!["leveredCf"]!.AsArray().Count);

        var (_, both) = await Send(HttpMethod.Get, $"/v1/runs/{key}?include=monthly,proforma");
        Assert.NotNull(both["monthly"]);
        Assert.NotNull(both["proforma"]);
        Assert.NotEmpty(both["proforma"]!["lines"]!.AsArray());
        Assert.NotEmpty(both["proforma"]!["lines"]![0]!["amounts"]!.AsArray());

        var (bad, badJson) = await Send(HttpMethod.Get, $"/v1/runs/{key}?include=nope");
        AssertProblem(bad, badJson, 400);
    }

    [Fact]
    public async Task Unknown_ids_are_404_problems()
    {
        var setId = (await Publish(A)).Json["id"]!.GetValue<string>();
        var (r1, j1) = await Send(HttpMethod.Get, "/v1/assumption-sets/nope");
        AssertProblem(r1, j1, 404);
        var (r2, j2) = await Send(HttpMethod.Get, "/v1/runs/nope");
        AssertProblem(r2, j2, 404);
        var (r3, j3) = await Run("nope", "Base");
        AssertProblem(r3, j3, 404);
        var (r4, j4) = await Run(setId, "NoSuchScenario");
        AssertProblem(r4, j4, 404);
        Assert.Contains("NoSuchScenario", j4["detail"]!.GetValue<string>());
        var (r5, j5) = await Send(HttpMethod.Get, "/no/such/path");
        AssertProblem(r5, j5, 404);
    }

    [Fact]
    public async Task Invalid_sets_are_422_and_not_stored()
    {
        var bad = new[]
        {
            With(s => s with { HoldMonths = 5 }),
            With(s => s with { ExitCapRate = double.NaN }),
            With(s => s with { DebtEvents = new DebtEvent[] { new Refinance { Month = 200, Rate = 0.06, InterestOnlyMonths = 0, AmortizationYears = 30, FeePct = 0.01 } } }),
        };
        foreach (var a in bad)
        {
            var (res, json) = await Publish(a);
            AssertProblem(res, json, 422);
            var (get, getJson) = await Send(HttpMethod.Get, $"/v1/assumption-sets/{SafeId(a)}");
            AssertProblem(get, getJson, 404);
        }
        var (r, j) = await Publish(bad[0]);
        Assert.Contains("hold period 5", j["detail"]!.GetValue<string>());
        Assert.DoesNotContain("Parameter", j["detail"]!.GetValue<string>());
    }

    private static string SafeId(Assumptions a)
    {
        try { return AssumptionSetId.Compute(a); } catch (ArgumentException) { return "not-computable"; }
    }

    [Fact]
    public async Task Malformed_json_and_unknown_type_are_400()
    {
        var (r1, j1) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw("{ not json"));
        AssertProblem(r1, j1, 400);
        var (r2, j2) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw(""));
        AssertProblem(r2, j2, 400);

        var node = JsonSerializer.SerializeToNode(With(s => s with { DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.06, InterestOnlyMonths = 0, AmortizationYears = 30, FeePct = 0.01 } } }), ApiHost.Json)!;
        node["scenarios"]![0]!["debtEvents"]![0]!["$type"] = "Mystery";
        var (r3, j3) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw(node.ToJsonString()));
        AssertProblem(r3, j3, 400);

        var (r4, j4) = await Send(HttpMethod.Post, "/v1/runs", Raw("{}"));
        AssertProblem(r4, j4, 400);
    }

    [Fact]
    public async Task Transport_limits_and_methods()
    {
        var (r1, j1) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw("{\"x\":\"" + new string('a', 1_200_000) + "\"}"));
        AssertProblem(r1, j1, 413);
        var (r2, j2) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw("{}", "text/plain"));
        AssertProblem(r2, j2, 415);
        var (r3, j3) = await Send(HttpMethod.Delete, "/v1/runs");
        AssertProblem(r3, j3, 405);
    }

    [Fact]
    public async Task Refinance_and_hurdle_tier_round_trip_equal_the_direct_run()
    {
        var a = With(s => s with
        {
            DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.06, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 } },
            HurdleTiers = new[] { new HurdleTier(0.15, 0.30) },
        });
        var (res, json) = await Publish(a);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var (_, run) = await Run(json["id"]!.GetValue<string>(), "Base");
        var scenario = a.Scenarios.First(s => s.Name == "Base");
        var direct = Underwriter.Run(a, scenario).Summary;
        Assert.Equal(direct.LeveredIrr!.Value, run["summary"]!["leveredIrr"]!.GetValue<double>(), 12);
        Assert.Equal(direct.LpIrr!.Value, run["summary"]!["lpIrr"]!.GetValue<double>(), 12);
        Assert.Equal(direct.GpMultiple!.Value, run["summary"]!["gpMultiple"]!.GetValue<double>(), 12);
        Assert.Equal(RunKey.Compute(a, scenario, EngineInfo.Version, WorkbookQuirks.None), run["runKey"]!.GetValue<string>());
        Assert.NotEqual(Irr["Base"], run["summary"]!["leveredIrr"]!.GetValue<double>(), 6);
    }

    [Fact]
    public async Task Health_endpoints_are_200()
    {
        Assert.Equal(HttpStatusCode.OK, (await C.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await C.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served_and_matches_registered_routes()
    {
        var res = await C.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("application/json", res.Content.Headers.ContentType!.MediaType);
        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Assert.StartsWith("3.1", doc["openapi"]!.GetValue<string>());

        var documented = doc["paths"]!.AsObject()
            .SelectMany(p => p.Value!.AsObject().Select(m => (m.Key.ToUpperInvariant(), p.Key))).OrderBy(x => x).ToList();
        var registered = ((IEndpointRouteBuilder)server.App).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(m => (m, e.RoutePattern.RawText!))).OrderBy(x => x).ToList();
        Assert.Equal(registered, documented);

        foreach (var path in doc["paths"]!.AsObject())
            foreach (var op in path.Value!.AsObject())
                foreach (var response in op.Value!["responses"]!.AsObject().Where(r => int.Parse(r.Key) >= 400))
                    Assert.Equal(new[] { "application/problem+json" }, response.Value!["content"]!.AsObject().Select(c => c.Key));
        foreach (var name in new[] { "Assumptions", "RunResponse", "ReturnsSummary", "Problem" })
            Assert.NotNull(doc["components"]!["schemas"]![name]);
    }

    [Fact]
    public async Task Include_is_case_insensitive_and_may_repeat()
    {
        var a = With(s => s with { PipPerKey = s.PipPerKey + 3 });
        var setId = (await Publish(a)).Json["id"]!.GetValue<string>();
        var key = (await Run(setId, "Base")).Json["runKey"]!.GetValue<string>();
        var (_, caps) = await Send(HttpMethod.Get, $"/v1/runs/{key}?include=Monthly");
        Assert.NotNull(caps["monthly"]);
        var (_, both) = await Send(HttpMethod.Get, $"/v1/runs/{key}?include=monthly&include=proforma");
        Assert.NotNull(both["monthly"]);
        Assert.NotNull(both["proforma"]);
        var line = both["proforma"]!["lines"]![0]!;
        foreach (var f in new[] { "code", "name", "section" }) Assert.False(string.IsNullOrEmpty(line[f]!.GetValue<string>()));
    }

    private static async Task<JsonObject> Edited(Action<JsonObject> edit, Func<Assumptions, Assumptions>? pre = null)
    {
        var node = JsonSerializer.SerializeToNode(pre?.Invoke(A) ?? A, ApiHost.Json)!.AsObject();
        edit(node);
        await Task.CompletedTask;
        return node;
    }

    [Fact]
    public async Task Publish_input_problems_have_documented_statuses()
    {
        var dup = A with { Scenarios = new[] { A.Scenarios[0], A.Scenarios[0] } };
        var (r1, j1) = await Publish(dup);
        AssertProblem(r1, j1, 422);
        var (r2, j2) = await Publish(A with { Scenarios = Array.Empty<ScenarioInputs>() });
        AssertProblem(r2, j2, 422);

        var nan = await Edited(n => n["scenarios"]![0]!["purchasePrice"] = "NaN");
        var (r3, j3) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw(nan.ToJsonString()));
        AssertProblem(r3, j3, 422);

        var missing = await Edited(n => n["scenarios"]![0]!.AsObject().Remove("holdMonths"));
        var (r4, j4) = await Send(HttpMethod.Post, "/v1/assumption-sets", Raw(missing.ToJsonString()));
        AssertProblem(r4, j4, 400);

        var many = A with { Scenarios = Enumerable.Range(0, PublishAssumptionSet.MaxScenarios + 1).Select(i => A.Scenarios[0] with { Name = "S" + i }).ToArray() };
        var (r5, j5) = await Publish(many);
        AssertProblem(r5, j5, 422);
    }

    [Fact]
    public async Task Concurrent_identical_publishes_create_exactly_once()
    {
        var a = With(s => s with { PipPerKey = s.PipPerKey + 4 });
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Publish(a)));
        Assert.Single(results, r => r.Res.StatusCode == HttpStatusCode.Created);
        Assert.Equal(19, results.Count(r => r.Res.StatusCode == HttpStatusCode.OK));
        Assert.Single(results.Select(r => r.Json["id"]!.GetValue<string>()).Distinct());
    }

    [Fact]
    public async Task Summary_schema_names_equal_a_real_summary_object()
    {
        var doc = JsonNode.Parse(await C.GetStringAsync("/openapi/v1.json"))!;
        var documented = doc["components"]!["schemas"]!["ReturnsSummary"]!["properties"]!.AsObject().Select(p => p.Key).OrderBy(x => x).ToList();
        var a = With(s => s with { PipPerKey = s.PipPerKey + 5 });
        var setId = (await Publish(a)).Json["id"]!.GetValue<string>();
        var actual = (await Run(setId, "Base")).Json["summary"]!.AsObject().Select(p => p.Key).OrderBy(x => x).ToList();
        Assert.Equal(actual, documented);
    }

    [Fact]
    public async Task Correlation_id_is_echoed_when_safe_and_replaced_otherwise_and_reaches_the_ledger()
    {
        await using var app = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0" });
        await app.StartAsync();
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        async Task<HttpResponseMessage> Post(string url, HttpContent? body, string? id)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = body };
            if (id is not null) req.Headers.TryAddWithoutValidation("X-Correlation-Id", id);
            return await c.SendAsync(req);
        }
        static string Id(HttpResponseMessage r) => r.Headers.GetValues("X-Correlation-Id").Single();

        var a = With(s => s with { PipPerKey = s.PipPerKey + 21 });
        Assert.Equal("good-id_1.2", Id(await Post("/v1/assumption-sets", Body(a), "good-id_1.2")));
        var long64 = new string('a', 64);
        Assert.Equal(long64, Id(await Post("/v1/assumption-sets", Body(a), long64)));
        foreach (var bad in new[] { "has space", new string('a', 65) }) // a trailing newline never reaches the server: Kestrel trims header values; the regex ends in \z
            Assert.True(Guid.TryParse(Id(await Post("/v1/assumption-sets", Body(a), bad)), out _), $"'{bad}' should be replaced");

        Assert.NotEmpty(Id(await c.GetAsync("/v1/runs/nope"))); // 404
        var r415 = await Post("/v1/runs", new StringContent("{}", Encoding.UTF8, "text/plain"), null);
        Assert.Equal(415, (int)r415.StatusCode);
        Assert.NotEmpty(Id(r415));
        var dup = A with { Scenarios = new[] { A.Scenarios[0], A.Scenarios[0] } };
        var r422 = await Post("/v1/assumption-sets", Body(dup), null);
        Assert.Equal(422, (int)r422.StatusCode);
        Assert.NotEmpty(Id(r422));

        var entries = ((InMemoryLedger)app.Services.GetRequiredService<ILedger>()).Entries;
        Assert.Equal(4, entries.Count);
        Assert.Equal(new[] { "good-id_1.2", long64 }, entries.Take(2).Select(e => e.CorrelationId));
        Assert.All(entries.Skip(2), e => Assert.True(Guid.TryParse(e.CorrelationId, out _)));
    }

    [Fact]
    public async Task Default_address_is_loopback_5080()
    {
        // the official .NET container images set ASPNETCORE_HTTP_PORTS, which replaces the default: clear it
        await using var app = ApiHost.Build(["--HTTP_PORTS="]);
        Assert.Equal("http://127.0.0.1:5080", app.Configuration["urls"]); // Urls is empty until the server starts
    }
}
