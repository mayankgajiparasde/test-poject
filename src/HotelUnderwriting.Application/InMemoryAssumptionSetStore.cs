using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace HotelUnderwriting.Application;

public sealed class InMemoryAssumptionSetStore(ILedger? ledger = null) : IAtomicAssumptionSetStore
{
    private readonly ConcurrentDictionary<string, AssumptionSetRecord> _records = new();
    private readonly ILedger _ledger = ledger ?? new NullLedger();
    private readonly object _gate = new();

    public bool TryGet(string id, [MaybeNullWhen(false)] out AssumptionSetRecord record) => _records.TryGetValue(id, out record);

    public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) return _records.GetOrAdd(record.Id, record);
    }

    public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record, LedgerEntry ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        lock (_gate)
        {
            _ledger.Append(ledgerEntry); // append first: if it throws nothing was stored
            return _records.GetOrAdd(record.Id, record);
        }
    }
}
