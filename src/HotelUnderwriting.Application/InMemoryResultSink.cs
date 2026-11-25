namespace HotelUnderwriting.Application;

public sealed class InMemoryResultSink : IResultSink
{
    private readonly List<RunRecord> _records = new();

    public IReadOnlyList<RunRecord> Records { get { lock (_records) return _records.ToArray(); } }

    public void Write(RunRecord record) { lock (_records) _records.Add(record); }
}
