using HotelUnderwriting.Application;
using Npgsql;

namespace HotelUnderwriting.Persistence;

public sealed class PostgresLedger(string connectionString, string schema = "public") : ILedger
{
    private readonly string _cs = PostgresJson.ConnectionFor(connectionString, schema);

    public void Append(LedgerEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        Insert(conn, null, e);
    }

    /// <summary>Shared by <see cref="Append"/> and the stores, which call it inside their own transaction.</summary>
    internal static void Insert(NpgsqlConnection conn, NpgsqlTransaction? tx, LedgerEntry e)
    {
        using var cmd = new NpgsqlCommand(
            "insert into ledger (at, actor, action, subject_id, engine_version, input_hash, output_hash, correlation_id) values (@at, @actor, @action, @subject, @engine, @in, @out, @corr)", conn, tx);
        cmd.Parameters.AddWithValue("at", e.At.ToUniversalTime());
        cmd.Parameters.AddWithValue("actor", e.Actor);
        cmd.Parameters.AddWithValue("action", e.Action);
        cmd.Parameters.AddWithValue("subject", (object?)e.SubjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("engine", (object?)e.EngineVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("in", (object?)e.InputHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("out", (object?)e.OutputHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("corr", e.CorrelationId);
        cmd.ExecuteNonQuery();
    }
}
