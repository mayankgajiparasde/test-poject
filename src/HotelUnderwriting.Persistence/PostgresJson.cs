using System.Text.Json;
using System.Text.Json.Serialization;
using HotelUnderwriting.Application;

namespace HotelUnderwriting.Persistence;

/// <summary>How results and assumptions are stored. IncludeFields round-trips the Proforma.Lines tuple list.</summary>
internal static class PostgresJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        o.Converters.Add(new DebtEventConverter());
        return o;
    }

    public static string ConnectionFor(string connectionString, string schema) =>
        PostgresConnection.WithSchema(connectionString, schema);
}
