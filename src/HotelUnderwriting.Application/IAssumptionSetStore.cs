using System.Diagnostics.CodeAnalysis;

namespace HotelUnderwriting.Application;

public interface IAssumptionSetStore
{
    bool TryGet(string id, [MaybeNullWhen(false)] out AssumptionSetRecord record);

    /// <summary>Stores the record unless its id is already present (first writer wins). Returns the stored record; when this call stored <paramref name="record"/> it returns that same instance.</summary>
    AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record);
}
