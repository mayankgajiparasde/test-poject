namespace HotelUnderwriting.Application;

/// <summary><see cref="WasCached"/> is true when the record came from the store rather than from this call's own computation.</summary>
public sealed record SubmitResult(RunRecord Record, bool WasCached);
