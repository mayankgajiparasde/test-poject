using HotelUnderwriting.Application;
using Npgsql;
using NpgsqlTypes;

namespace HotelUnderwriting.Persistence;

public sealed class PostgresDealAccess(string connectionString, string schema = "public") : IDealAccess
{
    private readonly string _cs = PostgresJson.ConnectionFor(connectionString, schema);

    private static string Name(DealResource kind) => kind.ToString().ToLowerInvariant();

    public void Grant(DealResource kind, string resourceId, IReadOnlyCollection<string> dealIds)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(dealIds);
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        // one statement: all the deals are granted or none; a stable order so concurrent grants cannot deadlock
        using var cmd = new NpgsqlCommand("insert into deal_resources (kind, resource_id, deal_id) select @kind, @id, d from unnest(@deals) as t(d) order by d on conflict do nothing", conn);
        cmd.Parameters.AddWithValue("kind", Name(kind));
        cmd.Parameters.AddWithValue("id", resourceId);
        cmd.Parameters.Add(new NpgsqlParameter("deals", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = dealIds.ToArray() });
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyCollection<string> DealsOf(DealResource kind, string resourceId)
    {
        using var conn = new NpgsqlConnection(_cs);
        conn.Open();
        using var cmd = new NpgsqlCommand("select deal_id from deal_resources where kind = @kind and resource_id = @id", conn);
        cmd.Parameters.AddWithValue("kind", Name(kind));
        cmd.Parameters.AddWithValue("id", resourceId);
        using var r = cmd.ExecuteReader();
        var deals = new List<string>();
        while (r.Read()) deals.Add(r.GetString(0));
        return deals;
    }
}
