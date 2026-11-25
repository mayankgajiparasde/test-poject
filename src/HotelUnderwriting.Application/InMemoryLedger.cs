namespace HotelUnderwriting.Application;

public sealed class InMemoryLedger : ILedger
{
    private readonly List<LedgerEntry> _entries = new();

    public void Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_entries) _entries.Add(entry);
    }

    public IReadOnlyList<LedgerEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }
}
