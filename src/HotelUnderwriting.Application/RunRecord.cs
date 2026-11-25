using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public sealed record RunRecord(string RunKey, string EngineVersion, DateTimeOffset CreatedUtc, ScenarioResult Result);
