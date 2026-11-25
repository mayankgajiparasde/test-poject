using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Api;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using HotelUnderwriting.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HotelUnderwriting.Persistence.Tests;

/// <summary>
/// These tests need a throwaway PostgreSQL. Set HUE_TEST_PG to a connection string without a password, for example
/// "Host=127.0.0.1;Port=55432;Username=postgres;Database=hue_test". Each test uses its own schema (hue_&lt;random&gt;) and drops it afterwards.
/// </summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (!string.IsNullOrWhiteSpace(Pg.Cs)) return;
        Skip = "set HUE_TEST_PG";   // HUE_REQUIRE_PG=1 turns this into a failure via Database_is_configured_when_required
    }
}

internal static class Pg
{
    public static readonly string? Cs = Environment.GetEnvironmentVariable("HUE_TEST_PG");
}

/// <summary>A migrated, uniquely named schema that is dropped on dispose.</summary>
public sealed class TempSchema : IDisposable
{
    public string Schema { get; } = "hue_" + Guid.NewGuid().ToString("N")[..12];
    public string Cs { get; } = Pg.Cs!;
    /// <summary>Connection string whose search path is the schema (what the API takes).</summary>
    public string SchemaCs => new NpgsqlConnectionStringBuilder(Cs) { SearchPath = Schema, MaxPoolSize = 10 }.ConnectionString;

    public TempSchema(bool migrate = true)
    {
        if (migrate) PostgresMigrator.Apply(Cs, Schema);
    }

    public T Scalar<T>(string sql)
    {
        using var conn = new NpgsqlConnection(SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    public int Exec(string sql)
    {
        using var conn = new NpgsqlConnection(SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        return cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        using var conn = new NpgsqlConnection(Cs);
        conn.Open();
        using var cmd = new NpgsqlCommand($"drop schema if exists \"{Schema}\" cascade", conn);
        cmd.ExecuteNonQuery();
        // Each test has its own pool; release its idle connections so parallel tests stay under the server's 100.
        // The stores (schema on the plain string) and an API host (defaults added by Parse) pool under their own strings: release those too.
        foreach (var pooled in new[] { SchemaCs, new NpgsqlConnectionStringBuilder(Cs) { SearchPath = Schema }.ConnectionString, PostgresConnection.Parse(SchemaCs).ConnectionString })
        {
            using var mine = new NpgsqlConnection(pooled);
            NpgsqlConnection.ClearPool(mine);
        }
    }
}

public class PersistenceTests
{
    /// <summary>CI sets HUE_REQUIRE_PG=1 so a missing database fails loudly instead of the database tests being skipped.</summary>
    [Fact]
    public void Database_is_configured_when_required()
    {
        if (Environment.GetEnvironmentVariable("HUE_REQUIRE_PG") != "1") return;
        Assert.False(string.IsNullOrWhiteSpace(Pg.Cs),
            "HUE_REQUIRE_PG=1 but HUE_TEST_PG is not set: the PostgreSQL tests would be skipped, so this run proves nothing about persistence. Set HUE_TEST_PG (see the README PostgreSQL section).");
    }

    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static readonly JsonSerializerOptions Same = new() { IncludeFields = true, Converters = { new DebtEventConverter() } };

    private static string J(object o) => JsonSerializer.Serialize(o, Same);

    /// <summary>Equal as JSON trees, numbers compared as doubles (jsonb keeps -0 as 0, which is the same value).</summary>
    private static void AssertSame(object expected, object actual) => AssertNode(JsonSerializer.SerializeToNode(expected, Same), JsonSerializer.SerializeToNode(actual, Same), "$");

    private static void AssertNode(JsonNode? e, JsonNode? a, string path)
    {
        switch (e)
        {
            case JsonObject eo:
                var ao = Assert.IsType<JsonObject>(a);
                Assert.Equal(eo.Select(k => k.Key).OrderBy(k => k), ao.Select(k => k.Key).OrderBy(k => k));
                foreach (var kv in eo) AssertNode(kv.Value, ao[kv.Key], path + "." + kv.Key);
                break;
            case JsonArray ea:
                var aa = Assert.IsType<JsonArray>(a);
                Assert.True(ea.Count == aa.Count, $"{path} length");
                for (var i = 0; i < ea.Count; i++) AssertNode(ea[i], aa[i], $"{path}[{i}]");
                break;
            case JsonValue ev when ev.TryGetValue<double>(out var d) && a is JsonValue av && av.TryGetValue<double>(out var x):
                Assert.True(d == x, $"{path}: {d} vs {x}");
                break;
            default:
                Assert.True(e?.ToJsonString() == a?.ToJsonString(), $"{path}: {e} vs {a}");
                break;
        }
    }

    private static RunRecord Record(string key, ScenarioInputs s) =>
        new(key, EngineInfo.Version, DateTimeOffset.UtcNow, Underwriter.Run(A, s));

    private static ScenarioInputs Refi(ScenarioInputs s) =>
        s with { DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.05, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 } } };

    private static ScenarioInputs Tiered(ScenarioInputs s) => s with { HurdleTiers = new[] { new HurdleTier(0.15, 0.30) } };

    [PgFact]
    public void Migrate_twice_and_concurrently_is_idempotent()
    {
        using var t = new TempSchema(migrate: false);
        PostgresMigrator.Apply(t.Cs, t.Schema);
        PostgresMigrator.Apply(t.Cs, t.Schema);
        using var c = new TempSchema(migrate: false);
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ => PostgresMigrator.Apply(c.Cs, c.Schema));
        foreach (var s in new[] { t, c })
        {
            Assert.Equal(3, s.Scalar<int>("select count(*) from schema_version")); // 001_init, 002_sweeps, 003_deal_access
            Assert.Equal(3, s.Scalar<int>("select count(distinct version) from schema_version"));
            foreach (var table in new[] { "schema_version", "assumption_sets", "runs", "ledger", "jobs", "job_chunks", "job_results", "deal_resources" })
                Assert.Equal(1, s.Scalar<int>($"select count(*) from information_schema.tables where table_schema = '{s.Schema}' and table_name = '{table}'"));
        }
    }

    [PgFact]
    public void Assumption_set_round_trips_and_first_writer_wins()
    {
        using var t = new TempSchema();
        var store = new PostgresAssumptionSetStore(t.Cs, t.Schema);
        var withEvents = A with { Scenarios = A.Scenarios.Select((s, i) => i == 0 ? Tiered(Refi(s)) : s).ToArray() };
        var first = new AssumptionSetRecord("set-1", withEvents, DateTimeOffset.UtcNow);
        Assert.Same(first, store.SaveIfAbsent(first));
        Assert.True(store.TryGet("set-1", out var read));
        AssertSame(withEvents, read.Assumptions);
        Assert.IsType<Refinance>(read.Assumptions.Scenarios[0].DebtEvents[0]);
        Assert.Equal(A.Scenarios.Count, read.Assumptions.Scenarios.Count);
        Assert.Equal(first.CreatedUtc.ToUnixTimeMilliseconds(), read.CreatedUtc.ToUnixTimeMilliseconds());

        var other = new AssumptionSetRecord("set-1", A, DateTimeOffset.UtcNow.AddHours(1));
        var returned = store.SaveIfAbsent(other);
        Assert.NotSame(other, returned);
        AssertSame(withEvents, returned.Assumptions);
        Assert.False(store.TryGet("missing", out _));
    }

    [PgFact]
    public void Run_result_round_trips_including_refinance_and_hurdle_tier()
    {
        using var t = new TempSchema();
        var store = new PostgresRunStore(t.Cs, t.Schema);
        var n = 0;
        foreach (var s in A.Scenarios.SelectMany(s => new[] { s, Tiered(Refi(s)) }))
        {
            var rec = Record("key-" + n++, s);
            Assert.Same(rec, store.SaveIfAbsent(rec));
            Assert.True(store.TryGet(rec.RunKey, out var read));
            var (o, r) = (rec.Result, read.Result);
            AssertSame(o.Summary, r.Summary);
            AssertSame(o.Monthly, r.Monthly);
            Assert.Equal(o.Monthly.LeveredCf, r.Monthly.LeveredCf);
            Assert.Equal(o.Monthly.Date, r.Monthly.Date);
            AssertSame(o.Proforma, r.Proforma);
            Assert.Equal(o.Proforma.Lines.Count, r.Proforma.Lines.Count);
            for (var i = 0; i < o.Proforma.Lines.Count; i++)
            {
                Assert.Equal(o.Proforma.Lines[i].Line, r.Proforma.Lines[i].Line);
                Assert.Equal(o.Proforma.Lines[i].Amounts, r.Proforma.Lines[i].Amounts);
            }
            AssertSame(o.Inputs, r.Inputs);
            Assert.Equal(rec.EngineVersion, read.EngineVersion);
        }
        Assert.False(store.TryGet("unknown", out _));
    }

    [PgFact]
    public void Concurrent_saves_of_one_key_store_one_row_and_one_caller_wins()
    {
        using var t = new TempSchema();
        var store = new PostgresRunStore(t.Cs, t.Schema);
        var mine = Enumerable.Range(0, 20).Select(_ => Record("same-key", A.Scenarios[0])).ToArray();
        var returned = new RunRecord[20];
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, i => returned[i] = store.SaveIfAbsent(mine[i]));
        Assert.Equal(1, t.Scalar<int>("select count(*) from runs"));
        Assert.Equal(1, Enumerable.Range(0, 20).Count(i => ReferenceEquals(returned[i], mine[i])));
        Assert.Single(returned.Select(r => r.CreatedUtc.ToUnixTimeMilliseconds()).Distinct());
    }

    [PgFact]
    public void Ledger_appends_in_order_with_all_fields()
    {
        using var t = new TempSchema();
        var ledger = new PostgresLedger(t.Cs, t.Schema);
        var at = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        ledger.Append(new LedgerEntry(at, "anonymous", "run.computed", "k1", "1.0.0", "in1", "out1", "corr-1"));
        ledger.Append(new LedgerEntry(at.AddSeconds(1), "anonymous", "assumption_set.published", null, null, null, null, "corr-2"));
        using var conn = new NpgsqlConnection(t.SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select at, actor, action, subject_id, engine_version, input_hash, output_hash, correlation_id from ledger order by id", conn);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(at, r.GetFieldValue<DateTimeOffset>(0));
        Assert.Equal(new[] { "anonymous", "run.computed", "k1", "1.0.0", "in1", "out1", "corr-1" }, Enumerable.Range(1, 7).Select(i => r.GetString(i)));
        Assert.True(r.Read());
        Assert.Equal("corr-2", r.GetString(7));
        Assert.True(r.IsDBNull(3) && r.IsDBNull(4) && r.IsDBNull(5) && r.IsDBNull(6));
        Assert.False(r.Read());
    }

    [PgFact]
    public void Ledger_rejects_update_delete_and_truncate()
    {
        using var t = new TempSchema();
        new PostgresLedger(t.Cs, t.Schema).Append(new LedgerEntry(DateTimeOffset.UtcNow, "anonymous", "run.computed", "k", null, null, null, "c"));
        foreach (var sql in new[] { "update ledger set actor = 'x'", "delete from ledger", "truncate ledger" })
        {
            var e = Assert.Throws<PostgresException>(() => t.Exec(sql));
            Assert.Contains("append-only", e.MessageText);
        }
        // the statement-level trigger also blocks statements that match no row
        Assert.Throws<PostgresException>(() => t.Exec("delete from ledger where id < 0"));
        Assert.Equal(1, t.Scalar<int>("select count(*) from ledger"));
        Assert.Equal("anonymous", t.Scalar<string>("select actor from ledger"));
    }

    private static async Task<WebApplication> Start(string? cs)
    {
        var args = new List<string> { "--urls", "http://127.0.0.1:0" };
        if (cs is not null) args.Add($"--Persistence:ConnectionString={cs}");
        var app = ApiHost.Build(args.ToArray());
        await app.StartAsync();
        return app;
    }

    private static async Task<(HttpResponseMessage Res, JsonObject Json)> Send(HttpClient c, HttpMethod m, string url, object? body = null, string? correlation = null)
    {
        var req = new HttpRequestMessage(m, url);
        if (body is not null) req.Content = new StringContent(JsonSerializer.Serialize(body, ApiHost.Json), Encoding.UTF8, "application/json");
        if (correlation is not null) req.Headers.Add("X-Correlation-Id", correlation);
        var res = await c.SendAsync(req);
        return (res, JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsObject());
    }

    [PgFact]
    public async Task Api_state_survives_a_restart()
    {
        using var t = new TempSchema(migrate: false); // the host migrates at startup
        string setId, key;
        JsonNode summary;
        var a = A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + 11 } : s).ToArray() };
        await using (var app = await Start(t.SchemaCs))
        {
            using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var (pub, pj) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
            Assert.Equal(HttpStatusCode.Created, pub.StatusCode);
            setId = pj["id"]!.GetValue<string>();
            var (_, run) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" });
            Assert.False(run["wasCached"]!.GetValue<bool>());
            key = run["runKey"]!.GetValue<string>();
            summary = run["summary"]!.DeepClone();
        }
        await using (var app = await Start(t.SchemaCs))
        {
            using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var (_, get) = await Send(c, HttpMethod.Get, $"/v1/runs/{key}");
            Assert.Equal(summary.ToJsonString(), get["summary"]!.ToJsonString());
            var (_, again) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" });
            Assert.True(again["wasCached"]!.GetValue<bool>());
            Assert.Equal(key, again["runKey"]!.GetValue<string>());
            var (setRes, set) = await Send(c, HttpMethod.Get, $"/v1/assumption-sets/{setId}");
            Assert.Equal(HttpStatusCode.OK, setRes.StatusCode);
            Assert.Equal(a.Scenarios.Select(s => s.Name), set["scenarios"]!.AsArray().Select(n => n!.GetValue<string>()));
        }
    }

    [PgFact]
    public async Task Api_writes_one_ledger_row_per_publish_and_run()
    {
        using var t = new TempSchema(migrate: false);
        var a = A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + 12 } : s).ToArray() };
        await using var app = await Start(t.SchemaCs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var (r1, p1) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a, "my-corr-1");
        Assert.Equal("my-corr-1", r1.Headers.GetValues("X-Correlation-Id").Single());
        var setId = p1["id"]!.GetValue<string>();
        var (r2, _) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        var generated = r2.Headers.GetValues("X-Correlation-Id").Single();
        Assert.True(Guid.TryParse(generated, out _));
        var (r3, run1) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" }, "my-corr-2");
        var (_, _) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" }, "bad id with spaces");
        var key = run1["runKey"]!.GetValue<string>();

        using var conn = new NpgsqlConnection(t.SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select action, subject_id, engine_version, input_hash, output_hash, correlation_id, actor from ledger order by id", conn);
        var rows = new List<string?[]>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add(Enumerable.Range(0, 7).Select(i => r.IsDBNull(i) ? null : r.GetString(i)).ToArray());
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { "assumption_set.published", "assumption_set.published", "run.computed", "run.served_from_store" }, rows.Select(r => r[0]));
        Assert.Equal(new[] { "my-corr-1", generated, "my-corr-2" }, rows.Take(3).Select(r => r[5]));
        Assert.True(Guid.TryParse(rows[3][5], out _)); // unsafe header replaced by a generated id
        Assert.All(rows, r => { Assert.Equal("anonymous", r[6]); Assert.Matches("^[0-9a-f]{64}$", r[3]!); Assert.Matches("^[0-9a-f]{64}$", r[4]!); });
        Assert.Equal(setId, rows[0][3]);
        Assert.Equal(key, rows[2][3]);
        Assert.Equal(key, rows[3][1]);
        Assert.Equal(rows[2][4], rows[3][4]); // same summary, same output hash
    }

    private static string[] Actions(TempSchema t, string where = "true")
    {
        using var conn = new NpgsqlConnection(t.SchemaCs);
        conn.Open();
        using var cmd = new NpgsqlCommand($"select action from ledger where {where} order by id", conn);
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows.ToArray();
    }

    private static Assumptions Variant(int n) =>
        A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + 100 + n } : s).ToArray() };

    [PgFact]
    public async Task Run_and_publish_ledger_rows_are_atomic_with_the_state_change()
    {
        using var t = new TempSchema(migrate: false);
        var a = Variant(1);
        await using var app = await Start(t.SchemaCs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        // publish: a ledger failure rolls the set back
        t.Exec($"alter table ledger add constraint no_publish check (action <> '{LedgerEntry.AssumptionSetPublished}')");
        var (failPub, problem) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        Assert.Equal(HttpStatusCode.InternalServerError, failPub.StatusCode);
        Assert.Equal("An unexpected error occurred.", problem["detail"]!.GetValue<string>());
        Assert.Equal(0, t.Scalar<int>("select count(*) from assumption_sets"));
        t.Exec("alter table ledger drop constraint no_publish");
        var (pub, p1) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        Assert.Equal(HttpStatusCode.Created, pub.StatusCode);
        var setId = p1["id"]!.GetValue<string>();

        // run: a ledger failure leaves no run row; the retry computes it
        t.Exec($"alter table ledger add constraint no_computed check (action <> '{LedgerEntry.RunComputed}')");
        var (failRun, _) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" });
        Assert.Equal(HttpStatusCode.InternalServerError, failRun.StatusCode);
        Assert.Equal(0, t.Scalar<int>("select count(*) from runs"));
        Assert.Empty(Actions(t, $"action = '{LedgerEntry.RunComputed}'"));
        t.Exec("alter table ledger drop constraint no_computed");
        var (ok, run) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(run["wasCached"]!.GetValue<bool>());
        Assert.Equal(1, t.Scalar<int>("select count(*) from runs"));
        Assert.Equal(new[] { LedgerEntry.AssumptionSetPublished, LedgerEntry.RunComputed }, Actions(t));

        // a failing ledger on the cached path fails the call and adds nothing
        t.Exec($"alter table ledger add constraint no_served check (action <> '{LedgerEntry.RunServedFromStore}')");
        var (failCached, _) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = setId, scenario = "Base" });
        Assert.Equal(HttpStatusCode.InternalServerError, failCached.StatusCode);
        Assert.Equal(2, Actions(t).Length);
        Assert.Equal(1, t.Scalar<int>("select count(*) from runs"));
    }

    [PgFact]
    public async Task A_run_whose_ledger_insert_failed_is_404_on_get()
    {
        using var t = new TempSchema(migrate: false);
        await using var app = await Start(t.SchemaCs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var a = Variant(2);
        var (_, p) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        var key = RunKey.Compute(a, a.Scenarios.First(s => s.Name == "Base"), EngineInfo.Version, WorkbookQuirks.None);
        t.Exec($"alter table ledger add constraint no_computed check (action <> '{LedgerEntry.RunComputed}')");
        var (res, _) = await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = p["id"]!.GetValue<string>(), scenario = "Base" });
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/v1/runs/{key}")).StatusCode);
    }

    [PgFact]
    public void Twenty_parallel_identical_submits_write_one_run_and_one_computed_row()
    {
        using var t = new TempSchema();
        var submit = new SubmitRun(new PostgresRunStore(t.Cs, t.Schema), new SystemClock(), ledger: new PostgresLedger(t.Cs, t.Schema));
        var wasCached = new bool[20];
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, i => wasCached[i] = submit.Submit(A, A.Scenarios[0], correlationId: "c" + i).WasCached);
        Assert.Equal(1, t.Scalar<int>("select count(*) from runs"));
        Assert.Single(Actions(t, $"action = '{LedgerEntry.RunComputed}'"));
        Assert.Equal(19, Actions(t, $"action = '{LedgerEntry.RunServedFromStore}'").Length);
        Assert.Equal(19, wasCached.Count(x => x));
    }

    [PgFact]
    public void Every_run_and_set_has_its_ledger_row_after_a_mixed_workload()
    {
        using var t = new TempSchema();
        var clock = new SystemClock();
        var ledger = new PostgresLedger(t.Cs, t.Schema);
        var publish = new PublishAssumptionSet(new PostgresAssumptionSetStore(t.Cs, t.Schema), clock, ledger);
        var submit = new SubmitRun(new PostgresRunStore(t.Cs, t.Schema), clock, ledger: ledger);
        Parallel.For(0, 24, i =>
        {
            var a = Variant(i % 3);
            publish.Publish(a);
            submit.Submit(a, a.Scenarios[i % a.Scenarios.Count]);
        });
        Assert.Equal(3, t.Scalar<int>("select count(*) from assumption_sets"));
        Assert.Equal(24, Actions(t, $"action = '{LedgerEntry.AssumptionSetPublished}'").Length);
        Assert.Equal(0, t.Scalar<int>($"select count(*) from runs r where (select count(*) from ledger l where l.action = '{LedgerEntry.RunComputed}' and l.subject_id = r.run_key) <> 1"));
        Assert.Equal(0, t.Scalar<int>($"select count(*) from assumption_sets s where not exists (select 1 from ledger l where l.action = '{LedgerEntry.AssumptionSetPublished}' and l.subject_id = s.id)"));
        Assert.Equal(t.Scalar<int>("select count(*) from runs"), Actions(t, $"action = '{LedgerEntry.RunComputed}'").Length);
    }

    [PgFact]
    public void Twenty_parallel_first_time_publishes_store_one_set_and_log_twenty_rows()
    {
        using var t = new TempSchema();
        var publish = new PublishAssumptionSet(new PostgresAssumptionSetStore(t.Cs, t.Schema), new SystemClock(), new PostgresLedger(t.Cs, t.Schema));
        var a = Variant(7);
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, _ => publish.Publish(a));
        Assert.Equal(1, t.Scalar<int>("select count(*) from assumption_sets"));
        Assert.Equal(20, Actions(t, $"action = '{LedgerEntry.AssumptionSetPublished}'").Length);
    }

    [PgFact]
    public async Task Republish_with_a_failing_ledger_is_500_and_changes_nothing()
    {
        using var t = new TempSchema(migrate: false);
        await using var app = await Start(t.SchemaCs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var a = Variant(8);
        Assert.Equal(HttpStatusCode.Created, (await Send(c, HttpMethod.Post, "/v1/assumption-sets", a)).Res.StatusCode);
        var before = Actions(t);
        t.Exec($"alter table ledger add constraint no_publish check (action <> '{LedgerEntry.AssumptionSetPublished}') not valid"); // existing row stays, new inserts fail
        var (res, problem) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.Equal("An unexpected error occurred.", problem["detail"]!.GetValue<string>());
        Assert.Equal(1, t.Scalar<int>("select count(*) from assumption_sets"));
        Assert.Equal(before, Actions(t));
    }

    private sealed class FailingLedger : ILedger
    {
        public bool Fail = true;
        public void Append(LedgerEntry entry) { if (Fail) throw new InvalidOperationException("ledger down"); }
    }

    [Fact]
    public void In_memory_failing_ledger_leaves_the_stores_unchanged()
    {
        var ledger = new FailingLedger();
        var sets = new InMemoryAssumptionSetStore(ledger);
        var runs = new InMemoryRunStore(ledger);
        var publish = new PublishAssumptionSet(sets, new SystemClock(), ledger);
        var submit = new SubmitRun(runs, new SystemClock(), ledger: ledger);
        var a = Variant(5);
        Assert.Throws<InvalidOperationException>(() => publish.Publish(a));
        Assert.False(sets.TryGet(AssumptionSetId.Compute(a), out _));
        Assert.Throws<InvalidOperationException>(() => submit.Submit(a, a.Scenarios[0]));
        Assert.False(runs.TryGet(RunKey.Compute(a, a.Scenarios[0], EngineInfo.Version, WorkbookQuirks.None), out _));
        ledger.Fail = false;
        Assert.True(publish.Publish(a).Created);
        Assert.False(submit.Submit(a, a.Scenarios[0]).WasCached);
    }

    [Fact]
    public async Task Default_mode_uses_the_in_memory_ledger()
    {
        await using var app = await Start(null);
        Assert.IsType<InMemoryLedger>(app.Services.GetRequiredService<ILedger>());
        Assert.IsType<InMemoryRunStore>(app.Services.GetRequiredService<IRunStore>());
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var a = A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? s with { PipPerKey = s.PipPerKey + 13 } : s).ToArray() };
        var (_, p) = await Send(c, HttpMethod.Post, "/v1/assumption-sets", a);
        await Send(c, HttpMethod.Post, "/v1/runs", new { assumptionSetId = p["id"]!.GetValue<string>(), scenario = "Base" });
        var entries = ((InMemoryLedger)app.Services.GetRequiredService<ILedger>()).Entries;
        Assert.Equal(new[] { "assumption_set.published", "run.computed" }, entries.Select(e => e.Action));
    }

    [Fact]
    public async Task Readiness_is_503_when_the_database_is_unreachable_and_liveness_stays_200()
    {
        const string bad = "Host=127.0.0.1;Port=1;Username=secretuser;Database=secretdb;Timeout=2";
        await using var app = await Start(bad);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var (res, json) = await Send(c, HttpMethod.Get, "/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var body = json.ToJsonString();
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("Npgsql", body);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/live")).StatusCode);
    }

    [PgFact]
    public async Task Readiness_retries_a_migration_that_failed_at_startup()
    {
        using var t = new TempSchema(migrate: false);
        var cs = new NpgsqlConnectionStringBuilder(t.SchemaCs) { CommandTimeout = 1 }.ConnectionString;
        // Hold the migration lock so the startup migration times out, then release it.
        using var holder = new NpgsqlConnection(t.Cs);
        holder.Open();
        using var tx = holder.BeginTransaction();
        using (var lockCmd = new NpgsqlCommand("select pg_advisory_xact_lock(hashtext('hue_migrate:' || @s))", holder, tx))
        {
            lockCmd.Parameters.AddWithValue("s", t.Schema);
            lockCmd.ExecuteNonQuery();
        }
        await using var app = await Start(cs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/health/ready")).StatusCode);
        tx.Commit();
        // The retry runs under the same 1 second command timeout, which a busy test machine can exceed once; each /ready call retries, so poll.
        var status = HttpStatusCode.ServiceUnavailable;
        for (var attempt = 0; attempt < 20 && status != HttpStatusCode.OK; attempt++)
        {
            status = (await c.GetAsync("/health/ready")).StatusCode;
            if (status != HttpStatusCode.OK) await Task.Delay(500);
        }
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, t.Scalar<int>("select count(*) from schema_version"));
    }

    [Fact]
    public void Connection_string_parsing_is_generic_strict_and_sets_timeouts()
    {
        var bad = Assert.Throws<InvalidOperationException>(() => PostgresConnection.Parse("Host=h;Password=topsecret;Bogus Key=1"));
        Assert.Equal("Persistence:ConnectionString is invalid", bad.Message);
        Assert.DoesNotContain("topsecret", bad.ToString());
        var multi = Assert.Throws<InvalidOperationException>(() => PostgresConnection.Parse("Host=h;Search Path=app,public"));
        Assert.Equal("Persistence:ConnectionString is invalid", multi.Message);
        var host = Assert.Throws<InvalidOperationException>(() => ApiHost.Build(new[] { "--Persistence:ConnectionString=Password=topsecret;Search Path=a,b" }));
        Assert.DoesNotContain("topsecret", host.ToString());

        var (cs, schema) = PostgresConnection.Parse("Host=h;Search Path=app");
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("app", schema);
        Assert.Equal((5, 15, 10), (b.Timeout, b.CommandTimeout, b.MaxPoolSize));
        Assert.Equal("public", PostgresConnection.Parse("Host=h").Schema);
        var set = new NpgsqlConnectionStringBuilder(PostgresConnection.Parse("Host=h;Timeout=9;Command Timeout=30").ConnectionString);
        Assert.Equal((9, 30), (set.Timeout, set.CommandTimeout));
    }

    [Fact]
    public void Output_hash_is_canonical_stable_and_total()
    {
        var summary = Underwriter.Run(A, A.Scenarios[0]).Summary;
        var node = JsonSerializer.SerializeToNode(summary, new JsonSerializerOptions { IncludeFields = true })!.AsObject();
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canon(node)))).ToLowerInvariant();
        Assert.Equal(expected, LedgerEntry.OutputHashOf(summary));
        Assert.Equal(LedgerEntry.OutputHashOf(summary), LedgerEntry.OutputHashOf(summary));
        var same = new ReturnsSummary { PipTotal = summary.PipTotal, LoanCommitment = summary.LoanCommitment };
        var changed = new ReturnsSummary { PipTotal = summary.PipTotal + 1, LoanCommitment = summary.LoanCommitment };
        Assert.NotEqual(LedgerEntry.OutputHashOf(same), LedgerEntry.OutputHashOf(changed));
        Assert.Matches("^[0-9a-f]{64}$", LedgerEntry.OutputHashOf(new ReturnsSummary { PipTotal = double.NaN }));
    }

    // Independent canonical form: sorted keys, no whitespace.
    private static string Canon(JsonNode? n) => n switch
    {
        JsonObject o => "{" + string.Join(",", o.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => JsonSerializer.Serialize(k.Key) + ":" + Canon(k.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => n?.ToJsonString() ?? "null",
    };

    [Fact]
    public void System_clock_has_microsecond_precision()
    {
        var clock = new SystemClock();
        for (var i = 0; i < 100; i++) Assert.Equal(0, clock.UtcNow.Ticks % 10);
    }

    [PgFact]
    public async Task Readiness_is_200_on_a_working_database()
    {
        using var t = new TempSchema(migrate: false);
        await using var app = await Start(t.SchemaCs);
        using var c = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
    }
}
