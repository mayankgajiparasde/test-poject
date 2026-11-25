using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace HotelUnderwriting.Application;

public sealed class InMemoryRunStore(ILedger? ledger = null) : IAtomicRunStore
{
    private readonly ConcurrentDictionary<string, RunRecord> _records = new();
    private readonly ILedger _ledger = ledger ?? new NullLedger();
    private readonly object _gate = new();

    public bool TryGet(string runKey, [MaybeNullWhen(false)] out RunRecord record) => _records.TryGetValue(runKey, out record);

    public RunRecord SaveIfAbsent(RunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            if (_records.TryGetValue(record.RunKey, out var existing)) return existing;
            _records[record.RunKey] = record;
            return record;
        }
    }

    public RunRecord SaveIfAbsent(RunRecord record, Func<bool, LedgerEntry> ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        lock (_gate)
        {
            if (_records.TryGetValue(record.RunKey, out var existing))
            {
                _ledger.Append(ledgerEntry(false));
                return existing;
            }
            _ledger.Append(ledgerEntry(true)); // append first: if it throws nothing was stored
            _records[record.RunKey] = record;
            return record;
        }
    }
}
