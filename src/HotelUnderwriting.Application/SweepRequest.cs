using System.Text.Json;
using System.Text.Json.Nodes;

namespace HotelUnderwriting.Application;

public sealed record ScenarioOverride(string Path, double Value);

public sealed record SweepCase(IReadOnlyList<ScenarioOverride> Overrides, string? Name = null);

public sealed record GridAxis(string Path, IReadOnlyList<double> Values);

/// <summary>One base scenario of a published set plus either explicit <see cref="Cases"/> or a <see cref="Grid"/> (cartesian product); exactly one of the two.</summary>
public sealed record SweepRequest(string AssumptionSetId, string Scenario, IReadOnlyList<SweepCase>? Cases = null, IReadOnlyList<GridAxis>? Grid = null)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Only for a request that passed <see cref="SweepPlan.Create"/> (finite numbers).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Web);

    public static SweepRequest FromJson(string json) => JsonSerializer.Deserialize<SweepRequest>(json, Web)!;

    /// <summary>SHA-256 of the canonical JSON of the request (sorted keys): the ledger input hash and the idempotency comparison.</summary>
    public string Hash() => RunKey.Hash(JsonSerializer.SerializeToNode(this, Web)!.AsObject());
}
