using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using Npgsql;
using NpgsqlTypes;

namespace HotelUnderwriting.Persistence;

public sealed class PostgresRunStore(string connectionString, string schema = "public") : IAtomicRunStore
{
    private readonly string _cs = PostgresJson.ConnectionFor(connectionString, schema);

    public bool TryGet(string runKey, [MaybeNullWhen(false)] out RunRecord record)
    {
        if (runKey.Any(char.IsControl)) { record = null; return false; } // PostgreSQL cannot store NUL: such an id is an unknown id
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select engine_version, created_utc, result from runs where run_key = @k", conn);
        cmd.Parameters.AddWithValue("k", runKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { record = null; return false; }
        record = new RunRecord(runKey, r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), JsonSerializer.Deserialize<ScenarioResult>(r.GetString(2), PostgresJson.Options)!);
        return true;
    }

    public RunRecord SaveIfAbsent(RunRecord record, Func<bool, LedgerEntry> ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var tx = conn.BeginTransaction(); // disposed without Commit = rollback
        var created = Insert(conn, tx, record);
        PostgresLedger.Insert(conn, tx, ledgerEntry(created));
        RunRecord? stored = record;
        if (!created) stored = Read(conn, tx, record.RunKey) ?? throw new InvalidOperationException("Run vanished after a conflicting insert.");
        tx.Commit();
        return stored;
    }

    private static RunRecord? Read(NpgsqlConnection conn, NpgsqlTransaction tx, string runKey)
    {
        using var cmd = new NpgsqlCommand("select engine_version, created_utc, result from runs where run_key = @k", conn, tx);
        cmd.Parameters.AddWithValue("k", runKey);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new RunRecord(runKey, r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), JsonSerializer.Deserialize<ScenarioResult>(r.GetString(2), PostgresJson.Options)!) : null;
    }

    private static bool Insert(NpgsqlConnection conn, NpgsqlTransaction? tx, RunRecord record)
    {
        using var cmd = new NpgsqlCommand("insert into runs (run_key, engine_version, created_utc, scenario_name, result) values (@k, @v, @at, @name, @json) on conflict (run_key) do nothing", conn, tx);
        cmd.Parameters.AddWithValue("k", record.RunKey);
        cmd.Parameters.AddWithValue("v", record.EngineVersion);
        cmd.Parameters.AddWithValue("at", record.CreatedUtc.ToUniversalTime());
        cmd.Parameters.AddWithValue("name", record.Result.Inputs.Name);
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(record.Result, PostgresJson.Options) });
        return cmd.ExecuteNonQuery() == 1;
    }

    public RunRecord SaveIfAbsent(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        if (Insert(conn, null, record)) return record;
        return TryGet(record.RunKey, out var stored) ? stored : throw new InvalidOperationException("Run vanished after a conflicting insert.");
    }
}
