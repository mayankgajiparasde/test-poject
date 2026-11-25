using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public sealed record AssumptionSetRecord(string Id, Assumptions Assumptions, DateTimeOffset CreatedUtc);
