using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public sealed class InMemoryAssumptionSource(Assumptions assumptions) : IAssumptionSource
{
    public Assumptions Load() => assumptions;
}
