using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>Content address of a run (AD-6): lowercase hex SHA-256 of the canonical JSON of everything that determines the result.</summary>
public static class RunKey
{
    public static string Compute(Assumptions a, ScenarioInputs s, string engineVersion, WorkbookQuirks quirks)
    {
        var root = new JsonObject
        {
            ["property"] = JsonSerializer.SerializeToNode(a.Property),
            ["lines"] = JsonSerializer.SerializeToNode(a.Lines),
            ["percentItems"] = JsonSerializer.SerializeToNode(a.PercentItems),
            ["seasonality"] = JsonSerializer.SerializeToNode(a.Seasonality),
            ["scenario"] = ScenarioNode(s),
            ["engineVersion"] = engineVersion,
            ["quirks"] = (int)quirks,
        };
        return Hash(root);
    }

    // DebtEvent is abstract: serialize each item by runtime type or Refinance fields are dropped.
    internal static JsonObject ScenarioNode(ScenarioInputs s)
    {
        var scenario = Typed(s with { DebtEvents = Array.Empty<DebtEvent>(), HurdleTiers = Array.Empty<HurdleTier>() });
        scenario["DebtEvents"] = new JsonArray(s.DebtEvents.Select(Typed).ToArray());
        scenario["HurdleTiers"] = new JsonArray(s.HurdleTiers.Select(Typed).ToArray());
        return scenario;
    }

    internal static string Hash(JsonObject root)
    {
        var json = new StringBuilder();
        Canonical(root, json);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToString()))).ToLowerInvariant();
    }

    private static JsonObject Typed(object o)
    {
        var node = JsonSerializer.SerializeToNode(o, o.GetType())!.AsObject();
        node["$type"] = o.GetType().Name;
        return node;
    }

    // Keys sorted ordinally, no whitespace; leaves keep System.Text.Json's culture-free shortest round-trip text.
    private static void Canonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    Canonical(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Canonical(arr[i], sb);
                }
                sb.Append(']');
                break;
            default:
                var text = node?.ToJsonString() ?? "null";
                sb.Append(text == "-0" ? "0" : text); // -0.0 and 0.0 are the same input
                break;
        }
    }
}
