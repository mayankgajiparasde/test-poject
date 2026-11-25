namespace HotelUnderwriting.Application;

public sealed class IdempotencyConflictException(string message) : Exception(message);

public sealed record JobRecord(string Id, SweepRequest Request, string RequestHash, string CorrelationId, DateTimeOffset CreatedUtc, int CaseCount, int ChunkCount);

public sealed record JobChunk(long ChunkId, string JobId, int FirstCase, int CaseCount, int Attempts);

/// <summary>Exactly one of <see cref="RunKey"/> and <see cref="Error"/> is set.</summary>
public sealed record CaseResult(int CaseIndex, string? RunKey, string? Error);

/// <summary>Chunk counts by state plus case counts. <see cref="Started"/> is true once any chunk was ever claimed. CompletedCases counts every case with a stored result (failed ones included).</summary>
public sealed record JobProgress(int Total, int Done, int Queued, int Leased, int Dead, bool Started, int CompletedCases, int FailedCases)
{
    public string Status => Dead > 0 ? "failed" : Done == Total ? "succeeded" : Started ? "running" : "queued";
}

/// <summary>Durable job queue: a job is split into chunks that competing workers lease (AD-13).</summary>
public interface IJobStore
{
    /// <summary>Stores the job, its chunks (<see cref="SweepLimits.ChunkSize"/> cases each) and the ledger row as one unit. A repeat of <paramref name="idempotencyKey"/> with the same request returns the existing job (Created false, no ledger row).</summary>
    /// <exception cref="IdempotencyConflictException">The key was used with a different request.</exception>
    (JobRecord Job, bool Created) Create(JobRecord job, string? idempotencyKey, LedgerEntry ledgerEntry);

    JobRecord? TryGetJob(string jobId);

    JobProgress? GetProgress(string jobId);

    /// <summary>Leases the oldest queued chunk or one whose lease expired (<paramref name="now"/> is the caller's clock); a chunk that already used <see cref="SweepLimits.MaxAttempts"/> attempts becomes dead instead. Null when nothing is available.</summary>
    JobChunk? TryClaimChunk(DateTimeOffset now, TimeSpan lease, string workerId);

    /// <summary>Upserts the results and marks the chunk done, only while <paramref name="workerId"/> holds the lease. Returns false (and stores nothing) otherwise.</summary>
    bool CompleteChunk(long chunkId, string workerId, IReadOnlyList<CaseResult> results);

    /// <summary>Releases the lease so the chunk is retried (the attempt was already counted).</summary>
    void FailChunk(long chunkId, string workerId, string error);

    /// <summary>Gives the chunk back without using up an attempt (shutdown, not failure), only while <paramref name="workerId"/> holds the lease.</summary>
    void ReleaseChunk(long chunkId, string workerId);

    /// <summary>Puts off a chunk that cannot be processed yet: it stays leased until <paramref name="until"/> and the attempt is given back, only while <paramref name="workerId"/> holds the lease.</summary>
    void DeferChunk(long chunkId, string workerId, DateTimeOffset until);

    /// <summary>Extends the lease to <paramref name="now"/> plus <paramref name="lease"/> only while <paramref name="workerId"/> still holds a live (unexpired) lease on the chunk. False when it was lost.</summary>
    bool RenewLease(long chunkId, string workerId, DateTimeOffset now, TimeSpan lease);

    /// <summary>Queued chunks (queued plus expired-lease), the age in seconds of the oldest of them (its job's creation time; 0 when none) and the dead chunks.</summary>
    (int Queued, double OldestQueuedAgeSeconds, int Dead) GetQueueStats(DateTimeOffset now);

    /// <summary>Results with case index above <paramref name="afterCaseIndex"/>, ordered by index, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<CaseResult> GetResults(string jobId, int afterCaseIndex, int limit);
}
