using System.Text.Json;
using System.Text.Json.Nodes;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>Content address of an assumption set: lowercase hex SHA-256 of the canonical JSON of the whole <see cref="Assumptions"/>, every scenario included. Order-sensitive: the same scenarios in a different order give a different id.</summary>
public static class AssumptionSetId
{
    public static string Compute(Assumptions a)
    {
        ArgumentNullException.ThrowIfNull(a);
        try
        {
            return RunKey.Hash(new JsonObject
            {
                ["property"] = JsonSerializer.SerializeToNode(a.Property),
                ["lines"] = JsonSerializer.SerializeToNode(a.Lines),
                ["percentItems"] = JsonSerializer.SerializeToNode(a.PercentItems),
                ["seasonality"] = JsonSerializer.SerializeToNode(a.Seasonality),
                ["scenarios"] = new JsonArray(a.Scenarios.Select(RunKey.ScenarioNode).ToArray()),
            });
        }
        catch (NullReferenceException e)
        {
            throw new InvalidAssumptionsException("Assumptions contain a missing or invalid value.", e);
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new InvalidAssumptionsException("Assumptions contain a non-finite number (NaN or Infinity) or cannot be serialized.", e);
        }
    }
}
