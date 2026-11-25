using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>DebtEvent is abstract, so it travels as an object with a "$type" discriminator. Only "Refinance" exists.</summary>
public sealed class DebtEventConverter : JsonConverter<DebtEvent>
{
    public override DebtEvent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonSerializer.Deserialize<JsonObject>(ref reader, options) ?? throw new JsonException("DebtEvent must be an object.");
        var type = node["$type"] is JsonValue v && v.TryGetValue(out string? t) ? t : null;
        return type == "Refinance"
            ? node.Deserialize<Refinance>(options)!
            : throw new JsonException($"Unknown or missing debt event $type '{type}'.");
    }

    public override void Write(Utf8JsonWriter writer, DebtEvent value, JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), options)!.AsObject();
        node["$type"] = value.GetType().Name;
        node.WriteTo(writer);
    }
}
