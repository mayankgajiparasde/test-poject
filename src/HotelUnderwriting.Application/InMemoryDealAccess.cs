namespace HotelUnderwriting.Application;

public sealed class InMemoryDealAccess : IDealAccess
{
    private readonly object _gate = new();
    private readonly Dictionary<(DealResource, string), HashSet<string>> _grants = new();

    public void Grant(DealResource kind, string resourceId, IReadOnlyCollection<string> dealIds)
    {
        ArgumentNullException.ThrowIfNull(resourceId);
        ArgumentNullException.ThrowIfNull(dealIds);
        lock (_gate)
        {
            if (!_grants.TryGetValue((kind, resourceId), out var deals)) _grants[(kind, resourceId)] = deals = new(StringComparer.Ordinal);
            deals.UnionWith(dealIds);
        }
    }

    public IReadOnlyCollection<string> DealsOf(DealResource kind, string resourceId)
    {
        lock (_gate) return _grants.TryGetValue((kind, resourceId), out var deals) ? deals.ToArray() : [];
    }
}
