using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Api.Tests;

public class SweepApiTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private HttpClient C => server.Client;

    // A tweaked set per test keeps these runs away from the workbook runs other tests expect to compute first.
    private static Assumptions Tweaked(int n) => A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + n } : s).ToArray() };

    internal static async Task<(HttpResponseMessage Res, JsonObject Json)> Send(HttpClient c, HttpMethod m, string url, string? body = null, string? key = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (key is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        var res = await c.SendAsync(req);
        return (res, JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject());
    }

    internal static async Task<string> Publish(HttpClient c, Assumptions a)
    {
        var (_, j) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", JsonSerializer.Serialize(a, ApiHost.Json));
        return j["id"]!.GetValue<string>();
    }

    internal static string Sweep(string setId, string cases = """[{"name":"six","overrides":[{"path":"interestRate","value":0.06}]},{"overrides":[{"path":"interestRate","value":0.07}]},{"overrides":[{"path":"interestRate","value":0.08}]}]""") =>
        $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","cases":{{cases}}}""";

    internal static async Task<JsonObject> WaitFor(HttpClient c, string jobId, string status = "succeeded")
    {
        var until = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < until)
        {
            var (_, j) = await Send(c, HttpMethod.Get, $"/v1/jobs/{jobId}");
            if (j["status"]!.GetValue<string>() == status) return j;
            await Task.Delay(100);
        }
        throw new TimeoutException($"job {jobId} never reached {status}");
    }

    private static void AssertProblem(HttpResponseMessage res, JsonObject json, int status)
    {
        Assert.Equal(status, (int)res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(status, json["status"]!.GetValue<int>());
        Assert.DoesNotContain("Exception", json.ToJsonString());
    }

    [Fact]
    public async Task Submit_answers_202_and_the_job_completes_with_results_and_paging()
    {
        var a = Tweaked(31);
        var setId = await Publish(C, a);
        var (res, json) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var jobId = json["jobId"]!.GetValue<string>();
        Assert.Equal($"/v1/jobs/{jobId}", res.Headers.Location!.ToString());
        Assert.Equal((3, 1, "queued"), (json["caseCount"]!.GetValue<int>(), json["chunkCount"]!.GetValue<int>(), json["status"]!.GetValue<string>()));

        var job = await WaitFor(C, jobId);
        Assert.Equal((3, 0), (job["completedCases"]!.GetValue<int>(), job["failedCases"]!.GetValue<int>()));
        Assert.Equal((1, 1, 0, 0, 0), (job["chunks"]!["total"]!.GetValue<int>(), job["chunks"]!["done"]!.GetValue<int>(), job["chunks"]!["queued"]!.GetValue<int>(), job["chunks"]!["leased"]!.GetValue<int>(), job["chunks"]!["dead"]!.GetValue<int>()));
        Assert.NotNull(job["createdUtc"]);

        var (rr, all) = await Send(C, HttpMethod.Get, $"/v1/jobs/{jobId}/results");
        Assert.Equal(HttpStatusCode.OK, rr.StatusCode);
        var items = all["items"]!.AsArray();
        Assert.Equal(new[] { 0, 1, 2 }, items.Select(i => i!["caseIndex"]!.GetValue<int>()));
        Assert.Null(all["next"]);
        Assert.Equal("six", items[0]!["name"]!.GetValue<string>());
        Assert.Null(items[1]!["name"]);
        var baseS = a.Scenarios.First(s => s.Name == "Base");
        var direct = Underwriter.Run(a, baseS with { InterestRate = 0.06 }).Summary;
        Assert.Equal(RunKey.Compute(a, baseS with { InterestRate = 0.06 }, EngineInfo.Version, WorkbookQuirks.None), items[0]!["runKey"]!.GetValue<string>());
        Assert.Equal(direct.LeveredIrr!.Value, items[0]!["summary"]!["leveredIrr"]!.GetValue<double>(), 12);
        Assert.Equal(direct.GrossSalePrice, items[0]!["summary"]!["grossSalePrice"]!.GetValue<double>(), 6);
        foreach (var f in new[] { "unleveredIrr", "leveredMultiple", "lpIrr", "gpIrr", "equityAtClose" }) Assert.NotNull(items[0]!["summary"]![f]);
        Assert.Null(items[0]!["error"]);

        var (_, p1) = await Send(C, HttpMethod.Get, $"/v1/jobs/{jobId}/results?limit=2");
        Assert.Equal(new[] { 0, 1 }, p1["items"]!.AsArray().Select(i => i!["caseIndex"]!.GetValue<int>()));
        Assert.Equal(1, p1["next"]!.GetValue<int>());
        var (_, p2) = await Send(C, HttpMethod.Get, $"/v1/jobs/{jobId}/results?after=1&limit=2");
        Assert.Equal(new[] { 2 }, p2["items"]!.AsArray().Select(i => i!["caseIndex"]!.GetValue<int>()));
        Assert.Null(p2["next"]);
    }

    [Fact]
    public async Task A_bad_case_is_recorded_and_the_job_still_succeeds()
    {
        var setId = await Publish(C, Tweaked(32));
        var (_, json) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId, """[{"overrides":[{"path":"interestRate","value":0.06}]},{"overrides":[{"path":"holdMonths","value":5}]}]"""));
        var job = await WaitFor(C, json["jobId"]!.GetValue<string>());
        Assert.Equal((2, 1), (job["completedCases"]!.GetValue<int>(), job["failedCases"]!.GetValue<int>()));
        var (_, results) = await Send(C, HttpMethod.Get, $"/v1/jobs/{job["jobId"]}/results");
        Assert.NotNull(results["items"]![1]!["error"]);
        Assert.Null(results["items"]![1]!["runKey"]);
        Assert.Null(results["items"]![1]!["summary"]);
    }

    [Fact]
    public async Task Idempotency_key_and_error_statuses()
    {
        var setId = await Publish(C, Tweaked(33));
        var (r1, j1) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId), "k-33");
        var (r2, j2) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId), "k-33");
        Assert.Equal((HttpStatusCode.Accepted, HttpStatusCode.Accepted), (r1.StatusCode, r2.StatusCode));
        Assert.Equal(j1["jobId"]!.GetValue<string>(), j2["jobId"]!.GetValue<string>());
        Assert.Equal($"/v1/jobs/{j1["jobId"]}", r2.Headers.Location!.ToString());
        var (r3, j3) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId, """[{"overrides":[{"path":"interestRate","value":0.05}]}]"""), "k-33");
        AssertProblem(r3, j3, 409);
        var (r4, j4) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId), "bad key!");
        AssertProblem(r4, j4, 400);

        var (n1, nj1) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep("nope"));
        AssertProblem(n1, nj1, 404);
        var (n2, nj2) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId).Replace("\"Base\"", "\"Nope\""));
        AssertProblem(n2, nj2, 404);
        var (n3, nj3) = await Send(C, HttpMethod.Get, "/v1/jobs/nope");
        AssertProblem(n3, nj3, 404);
        var (n4, nj4) = await Send(C, HttpMethod.Get, "/v1/jobs/nope/results");
        AssertProblem(n4, nj4, 404);

        foreach (var cases in new[]
        {
            """[{"overrides":[{"path":"bogus","value":1}]}]""",
            """[{"overrides":[{"path":"interestRate","value":"NaN"}]}]""",
            """[{"overrides":[{"path":"holdMonths","value":1.5}]}]""",
            "[]",
        })
        {
            var (res, json) = await Send(C, HttpMethod.Post, "/v1/sweeps", Sweep(setId, cases));
            AssertProblem(res, json, 422);
        }
        var both = $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","cases":[{"overrides":[]}],"grid":[{"path":"interestRate","values":[0.06]}]}""";
        var (rb, jb) = await Send(C, HttpMethod.Post, "/v1/sweeps", both);
        AssertProblem(rb, jb, 422);
        var big = $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","grid":[{"path":"interestRate","values":[{{string.Join(",", Enumerable.Repeat("0.06", 1000))}}]},{"path":"exitCapRate","values":[{{string.Join(",", Enumerable.Repeat("0.07", 101))}}]}]}""";
        var (rg, jg) = await Send(C, HttpMethod.Post, "/v1/sweeps", big);
        AssertProblem(rg, jg, 422);
        var dup = $$"""{"assumptionSetId":"{{setId}}","scenario":"Base","grid":[{"path":"interestRate","values":[0.06]},{"path":"interestRate","values":[0.07]}]}""";
        var (rd, jd) = await Send(C, HttpMethod.Post, "/v1/sweeps", dup);
        AssertProblem(rd, jd, 422);
        var (rn, jn) = await Send(C, HttpMethod.Post, "/v1/sweeps", """{"scenario":"Base","cases":[{"overrides":[]}]}""");
        AssertProblem(rn, jn, 400);
        var (rm, jm) = await Send(C, HttpMethod.Post, "/v1/sweeps", """{"assumptionSetId":"x"}""");
        AssertProblem(rm, jm, 400);
        var text = await C.PostAsync("/v1/sweeps", new StringContent("{}", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);

        var (l0, lj0) = await Send(C, HttpMethod.Get, $"/v1/jobs/{j1["jobId"]}/results?limit=0");
        AssertProblem(l0, lj0, 400);
        var (l1, lj1) = await Send(C, HttpMethod.Get, $"/v1/jobs/{j1["jobId"]}/results?limit=1001");
        AssertProblem(l1, lj1, 400);
        var (l2, lj2) = await Send(C, HttpMethod.Get, $"/v1/jobs/{j1["jobId"]}/results?after=-1");
        AssertProblem(l2, lj2, 400);
    }

    [Fact]
    public async Task Role_api_stores_but_does_not_process_and_bad_config_is_refused()
    {
        await using var app = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0", "--Role=api" });
        await app.StartAsync();
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var setId = await Publish(c, Tweaked(34));
        var (res, json) = await Send(c, HttpMethod.Post, "/v1/sweeps", Sweep(setId));
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        await Task.Delay(1500);
        var (_, job) = await Send(c, HttpMethod.Get, $"/v1/jobs/{json["jobId"]}");
        Assert.Equal(("queued", 0, 1), (job["status"]!.GetValue<string>(), job["completedCases"]!.GetValue<int>(), job["chunks"]!["queued"]!.GetValue<int>()));

        Assert.Throws<InvalidOperationException>(() => ApiHost.Build(new[] { "--Role=boss" }));
        Assert.Throws<InvalidOperationException>(() => ApiHost.Build(new[] { "--Worker:Count=0" }));
        Assert.Throws<InvalidOperationException>(() => ApiHost.Build(new[] { "--Worker:Count=17" }));
        await using var ok = ApiHost.Build(new[] { "--urls", "http://127.0.0.1:0", "--Role=all", "--Worker:Count=16" });
    }
}
