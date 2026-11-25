namespace HotelUnderwriting.Application;

public sealed class InMemoryJobStore(ILedger? ledger = null) : IJobStore
{
    private sealed class Chunk(long id, string jobId, int first, int count)
    {
        public readonly long Id = id;
        public readonly string JobId = jobId;
        public readonly int First = first, Count = count;
        public string State = "queued";
        public int Attempts;
        public string? Owner;
        public DateTimeOffset LeaseUntil;
    }

    private readonly ILedger _ledger = ledger ?? new NullLedger();
    private readonly object _gate = new();
    private readonly Dictionary<string, JobRecord> _jobs = new();
    private readonly Dictionary<string, string> _keys = new();
    private readonly List<Chunk> _chunks = new(); // ponytail: linear scan per claim; fine for in-memory dev mode
    private readonly Dictionary<string, SortedDictionary<int, CaseResult>> _results = new();
    private long _nextChunkId;

    public (JobRecord Job, bool Created) Create(JobRecord job, string? idempotencyKey, LedgerEntry ledgerEntry)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ledgerEntry);
        lock (_gate)
        {
            if (idempotencyKey is not null && _keys.TryGetValue(idempotencyKey, out var id))
            {
                var existing = _jobs[id];
                return existing.RequestHash == job.RequestHash ? (existing, false) : throw new IdempotencyConflictException("The idempotency key was already used with a different request.");
            }
            _ledger.Append(ledgerEntry); // append first: if it throws nothing was stored
            _jobs[job.Id] = job;
            if (idempotencyKey is not null) _keys[idempotencyKey] = job.Id;
            _results[job.Id] = new();
            for (var c = 0; c < job.ChunkCount; c++)
                _chunks.Add(new Chunk(++_nextChunkId, job.Id, c * SweepLimits.ChunkSize, Math.Min(SweepLimits.ChunkSize, job.CaseCount - c * SweepLimits.ChunkSize)));
            return (job, true);
        }
    }

    public JobRecord? TryGetJob(string jobId) { lock (_gate) return _jobs.GetValueOrDefault(jobId); }

    public JobProgress? GetProgress(string jobId)
    {
        lock (_gate)
        {
            if (!_jobs.ContainsKey(jobId)) return null;
            var mine = _chunks.Where(c => c.JobId == jobId).ToList();
            var results = _results[jobId].Values;
            return new JobProgress(mine.Count, mine.Count(c => c.State == "done"), mine.Count(c => c.State == "queued"), mine.Count(c => c.State == "leased"),
                mine.Count(c => c.State == "dead"), mine.Any(c => c.Attempts > 0), results.Count, results.Count(r => r.Error is not null));
        }
    }

    public JobChunk? TryClaimChunk(DateTimeOffset now, TimeSpan lease, string workerId)
    {
        lock (_gate)
        {
            foreach (var c in _chunks)
            {
                if (!(c.State == "queued" || (c.State == "leased" && c.LeaseUntil <= now))) continue;
                if (c.Attempts >= SweepLimits.MaxAttempts) { c.State = "dead"; c.Owner = null; continue; }
                c.State = "leased";
                c.Attempts++;
                c.Owner = workerId;
                c.LeaseUntil = now + lease;
                return new JobChunk(c.Id, c.JobId, c.First, c.Count, c.Attempts);
            }
            return null;
        }
    }

    public bool CompleteChunk(long chunkId, string workerId, IReadOnlyList<CaseResult> results)
    {
        lock (_gate)
        {
            var c = _chunks.FirstOrDefault(x => x.Id == chunkId);
            if (c is null || c.State != "leased" || c.Owner != workerId) return false;
            foreach (var r in results) _results[c.JobId][r.CaseIndex] = r;
            c.State = "done";
            c.Owner = null;
            return true;
        }
    }

    public void FailChunk(long chunkId, string workerId, string error)
    {
        lock (_gate)
        {
            var c = _chunks.FirstOrDefault(x => x.Id == chunkId);
            if (c is null || c.State != "leased" || c.Owner != workerId) return;
            c.State = "queued";
            c.Owner = null;
        }
    }

    public void ReleaseChunk(long chunkId, string workerId)
    {
        lock (_gate)
        {
            var c = _chunks.FirstOrDefault(x => x.Id == chunkId);
            if (c is null || c.State != "leased" || c.Owner != workerId) return;
            c.State = "queued";
            c.Owner = null;
            c.Attempts--;
        }
    }

    public void DeferChunk(long chunkId, string workerId, DateTimeOffset until)
    {
        lock (_gate)
        {
            var c = _chunks.FirstOrDefault(x => x.Id == chunkId);
            if (c is null || c.State != "leased" || c.Owner != workerId) return;
            c.LeaseUntil = until;
            c.Attempts--;
        }
    }

    public bool RenewLease(long chunkId, string workerId, DateTimeOffset now, TimeSpan lease)
    {
        lock (_gate)
        {
            var c = _chunks.FirstOrDefault(x => x.Id == chunkId);
            if (c is null || c.State != "leased" || c.Owner != workerId || c.LeaseUntil <= now) return false;
            c.LeaseUntil = now + lease;
            return true;
        }
    }

    public (int Queued, double OldestQueuedAgeSeconds, int Dead) GetQueueStats(DateTimeOffset now)
    {
        lock (_gate)
        {
            var waiting = _chunks.Where(c => c.State == "queued" || (c.State == "leased" && c.LeaseUntil <= now)).ToList();
            var oldest = waiting.Count == 0 ? 0 : (now - waiting.Min(c => _jobs[c.JobId].CreatedUtc)).TotalSeconds;
            return (waiting.Count, Math.Max(0, oldest), _chunks.Count(c => c.State == "dead"));
        }
    }

    public IReadOnlyList<CaseResult> GetResults(string jobId, int afterCaseIndex, int limit)
    {
        lock (_gate) return _results.TryGetValue(jobId, out var r) ? r.Values.Where(x => x.CaseIndex > afterCaseIndex).Take(limit).ToList() : [];
    }
}
