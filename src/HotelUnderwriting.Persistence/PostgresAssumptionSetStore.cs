using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using Npgsql;
using NpgsqlTypes;

namespace HotelUnderwriting.Persistence;

public sealed class PostgresAssumptionSetStore(string connectionString, string schema = "public") : IAtomicAssumptionSetStore
{
    private readonly string _cs = PostgresJson.ConnectionFor(connectionString, schema);

    public bool TryGet(string id, [MaybeNullWhen(false)] out AssumptionSetRecord record)
    {
        if (id.Any(char.IsControl)) { record = null; return false; } // PostgreSQL cannot store NUL: such an id is an unknown id
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select created_utc, assumptions from assumption_sets where id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) { record = null; return false; }
        record = new AssumptionSetRecord(id, JsonSerializer.Deserialize<Assumptions>(r.GetString(1), PostgresJson.Options)!, r.GetFieldValue<DateTimeOffset>(0));
        return true;
    }

    public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record, LedgerEntry ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var tx = conn.BeginTransaction(); // disposed without Commit = rollback
        var created = Insert(conn, tx, record);
        PostgresLedger.Insert(conn, tx, ledgerEntry);
        AssumptionSetRecord? stored = record;
        if (!created) stored = Read(conn, tx, record.Id) ?? throw new InvalidOperationException("Assumption set vanished after a conflicting insert.");
        tx.Commit();
        return stored;
    }

    private static AssumptionSetRecord? Read(NpgsqlConnection conn, NpgsqlTransaction tx, string id)
    {
        using var cmd = new NpgsqlCommand("select created_utc, assumptions from assumption_sets where id = @id", conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new AssumptionSetRecord(id, JsonSerializer.Deserialize<Assumptions>(r.GetString(1), PostgresJson.Options)!, r.GetFieldValue<DateTimeOffset>(0)) : null;
    }

    private static bool Insert(NpgsqlConnection conn, NpgsqlTransaction? tx, AssumptionSetRecord record)
    {
        using var cmd = new NpgsqlCommand("insert into assumption_sets (id, created_utc, assumptions) values (@id, @at, @json) on conflict (id) do nothing", conn, tx);
        cmd.Parameters.AddWithValue("id", record.Id);
        cmd.Parameters.AddWithValue("at", record.CreatedUtc.ToUniversalTime());
        cmd.Parameters.Add(new NpgsqlParameter("json", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(record.Assumptions, PostgresJson.Options) });
        return cmd.ExecuteNonQuery() == 1;
    }

    public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        if (Insert(conn, null, record)) return record;
        return TryGet(record.Id, out var stored) ? stored : throw new InvalidOperationException("Assumption set vanished after a conflicting insert.");
    }
}
