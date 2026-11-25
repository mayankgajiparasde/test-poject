using System.Diagnostics.CodeAnalysis;

namespace HotelUnderwriting.Application;

public interface IRunStore
{
    bool TryGet(string runKey, [MaybeNullWhen(false)] out RunRecord record);

    /// <summary>Stores the record unless its key is already present (first writer wins). Returns the stored record; when this call stored <paramref name="record"/> it returns that same instance.</summary>
    RunRecord SaveIfAbsent(RunRecord record);
}
