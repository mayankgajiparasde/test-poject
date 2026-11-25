using System.Reflection;
using Npgsql;

namespace HotelUnderwriting.Persistence;

/// <summary>Applies the embedded SQL migrations in version order, once each, in one transaction under an advisory lock (safe from several processes). Expand/contract only (AD-18).</summary>
public static class PostgresMigrator
{
    public static void Apply(string connectionString, string schema = "public")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        var migrations = Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.StartsWith("Migrations/", StringComparison.Ordinal))
            .Select(n => (Name: n, Version: VersionOf(n)))
            .OrderBy(m => m.Version)
            .ToList();

        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        void Exec(string sql, params (string, object)[] ps)
        {
            using var cmd = new NpgsqlCommand(sql, conn, tx);
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
            cmd.ExecuteNonQuery();
        }

        Exec("select pg_advisory_xact_lock(hashtext('hue_migrate:' || @s))", ("s", schema));
        Exec($"create schema if not exists {Quote(schema)}");
        Exec("select set_config('search_path', @s, true)", ("s", schema));
        Exec("create table if not exists schema_version (version int primary key, applied_at timestamptz not null default now())");
        foreach (var (name, version) in migrations)
        {
            using var check = new NpgsqlCommand("select 1 from schema_version where version = @v", conn, tx);
            check.Parameters.AddWithValue("v", version);
            if (check.ExecuteScalar() is not null) continue;
            using var reader = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!);
            Exec(reader.ReadToEnd());
            Exec("insert into schema_version (version) values (@v)", ("v", version));
        }
        tx.Commit();
    }

    private static int VersionOf(string resource)
    {
        var file = resource["Migrations/".Length..];
        var digits = new string(file.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var v) && file.Length > digits.Length && file[digits.Length] == '_'
            ? v
            : throw new InvalidOperationException($"Migration resource '{resource}' must be named <number>_<name>.sql.");
    }

    /// <summary>Touches every table the API uses; throws when the database or a table is unavailable.</summary>
    public static void Probe(string connectionString, string schema = "public")
    {
        using var conn = new NpgsqlConnection(PostgresConnection.WithSchema(connectionString, schema));
        conn.Open();
        foreach (var table in new[] { "assumption_sets", "runs", "ledger", "jobs", "job_chunks", "job_results", "deal_resources" })
        {
            using var cmd = new NpgsqlCommand($"select 1 from {table} limit 1", conn);
            cmd.ExecuteScalar();
        }
    }

    internal static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
