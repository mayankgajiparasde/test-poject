using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

public interface IAssumptionSource
{
    Assumptions Load();
}
