using System.Text.Json;
using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public sealed record LedgerEntry(
    DateTimeOffset At,
    string Actor,
    string Action,
    string? SubjectId,
    string? EngineVersion,
    string? InputHash,
    string? OutputHash,
    string CorrelationId)
{
    public const string AssumptionSetPublished = "assumption_set.published";
    public const string RunComputed = "run.computed";
    public const string RunServedFromStore = "run.served_from_store";
    public const string SweepSubmitted = "sweep.submitted";

    private static readonly JsonSerializerOptions HashOptions = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>Lowercase hex SHA-256 of the canonical JSON of a run summary.</summary>
    public static string OutputHashOf(ReturnsSummary summary) =>
        RunKey.Hash(JsonSerializer.SerializeToNode(summary, HashOptions)!.AsObject());
}
