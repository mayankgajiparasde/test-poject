using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Npgsql;
using Xunit;

namespace HotelUnderwriting.Persistence.Tests;

/// <summary>Story 12 against a real PostgreSQL, which cannot store a NUL character.</summary>
public class HardeningPersistenceTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));

    private static async Task<(HttpStatusCode Status, string Text)> Send(HttpClient c, HttpMethod m, string url, string? body = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var res = await c.SendAsync(req);
        return (res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    [PgFact]
    public async Task An_id_with_a_NUL_or_control_character_is_an_unknown_id_not_a_500()
    {
        using var t = new TempSchema();
        await using var app = ApiHost.Build(["--urls", "http://127.0.0.1:0", $"--Persistence:ConnectionString={t.SchemaCs}"]);
        await app.StartAsync();
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var set = JsonNode.Parse((await Send(c, HttpMethod.Post, "/v1/assumption-sets", JsonSerializer.Serialize(A, ApiHost.Json))).Text)!["id"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await Send(c, HttpMethod.Post, "/v1/runs", $$"""{"assumptionSetId":"{{set}}","scenario":"Base"}""")).Status);

        foreach (var bad in new[] { "a%00b", "a%01b", "%00" })
        {
            foreach (var url in new[] { $"/v1/assumption-sets/{bad}", $"/v1/runs/{bad}", $"/v1/jobs/{bad}", $"/v1/jobs/{bad}/results" })
            {
                var r = await Send(c, HttpMethod.Get, url);
                Assert.True(r.Status is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"GET {url}: {r.Status} {r.Text}");
            }
        }
        const string nul = "a\\u0000b";
        var post = await Send(c, HttpMethod.Post, "/v1/runs", $$"""{"assumptionSetId":"{{nul}}","scenario":"Base"}""");
        Assert.True(post.Status == HttpStatusCode.NotFound, $"POST /v1/runs: {post.Status} {post.Text}");
        post = await Send(c, HttpMethod.Post, "/v1/sweeps", $$"""{"assumptionSetId":"{{nul}}","scenario":"Base","grid":[{"path":"promote","values":[0.2]}]}""");
        Assert.True(post.Status == HttpStatusCode.NotFound, $"POST /v1/sweeps: {post.Status} {post.Text}");
    }

    // Kestrel may reject a %00 path before it reaches a store, so the test above is not proof of the guards; these call the stores themselves.
    [PgFact]
    public void The_stores_answer_a_NUL_or_control_id_as_unknown_instead_of_throwing()
    {
        using var t = new TempSchema();
        var sets = new PostgresAssumptionSetStore(t.SchemaCs);
        var runs = new PostgresRunStore(t.SchemaCs);
        var jobs = new PostgresJobStore(t.SchemaCs);
        foreach (var id in new[] { "a" + (char)0 + "b", (char)0 + "", "a" + (char)1, "a" + (char)0x7F })
        {
            Assert.False(sets.TryGet(id, out _));
            Assert.False(runs.TryGet(id, out _));
            Assert.Null(jobs.TryGetJob(id));
        }
        // the same call without the guard is what PostgreSQL refuses: proof that the guard is what keeps these from throwing
        using var conn = new NpgsqlConnection(t.SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select 1 from runs where run_key = @k", conn);
        cmd.Parameters.AddWithValue("k", "a" + (char)0 + "b");
        Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());
    }

    [PgFact]
    public void What_Npgsql_throws_for_a_command_timeout_and_an_exhausted_pool_counts_as_an_outage()
    {
        using var t = new TempSchema();
        var cs = new NpgsqlConnectionStringBuilder(t.SchemaCs) { MaxPoolSize = 1, Timeout = 1, CommandTimeout = 1, ApplicationName = "story12-pool" }.ConnectionString;
        using var held = new NpgsqlConnection(cs);
        held.Open();
        var exhausted = Assert.ThrowsAny<Exception>(() => { using var second = new NpgsqlConnection(cs); second.Open(); });
        Assert.True(ApiHost.IsDatabaseFailure(exhausted), $"pool exhausted: {exhausted.GetType().Name}");

        using var cmd = new NpgsqlCommand("select pg_sleep(3)", held);
        var slow = Assert.ThrowsAny<Exception>(() => cmd.ExecuteScalar());
        Assert.True(ApiHost.IsDatabaseFailure(slow), $"command timeout: {slow.GetType().Name}");
        Assert.False(ApiHost.IsDatabaseFailure(new TimeoutException("not from Npgsql")));
    }
}
