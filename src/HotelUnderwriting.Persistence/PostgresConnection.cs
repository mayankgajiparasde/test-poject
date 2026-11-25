using Npgsql;

namespace HotelUnderwriting.Persistence;

public static class PostgresConnection
{
    private const string Invalid = "Persistence:ConnectionString is invalid";

    /// <summary>Validates the string, applies default timeouts (connect 5 s, command 15 s) and returns the single schema from Search Path (default public). Never echoes the value.</summary>
    public static (string ConnectionString, string Schema) Parse(string raw)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(raw);
            var path = (b.SearchPath ?? "").Split(',', StringSplitOptions.TrimEntries);
            if (path.Length > 1) throw new InvalidOperationException(Invalid);
            var given = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = raw };
            if (!given.ContainsKey("Timeout")) b.Timeout = 5;
            if (!given.ContainsKey("Command Timeout")) b.CommandTimeout = 15;
            // Npgsql defaults to 100 per process, which one PostgreSQL server (default max_connections 100) cannot share between a few processes.
            if (!given.ContainsKey("Maximum Pool Size")) b.MaxPoolSize = 10;
            return (b.ConnectionString, path[0].Length > 0 ? path[0] : "public");
        }
        catch (Exception)
        {
            throw new InvalidOperationException(Invalid);
        }
    }

    internal static string WithSchema(string connectionString, string schema) =>
        new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ConnectionString;
}
