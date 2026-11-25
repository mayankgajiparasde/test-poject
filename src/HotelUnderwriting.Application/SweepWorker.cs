using System.Text.RegularExpressions;

namespace HotelUnderwriting.Application;

/// <summary>Processes one leased chunk at a time through the idempotent <see cref="SubmitRun"/>, so a re-claimed chunk is harmless (runs come back cached, results upsert).</summary>
public sealed class SweepWorker(IJobStore jobs, IAssumptionSetStore sets, SubmitRun submit, IClock clock, SweepPlanCache? plans = null, IWorkLog? log = null, IDealAccess? access = null)
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);
    /// <summary>The lease is renewed every this many cases, so a chunk may take longer than <see cref="Lease"/> without being stolen.</summary>
    public const int RenewEveryCases = 50;

    private readonly SweepPlanCache _plans = plans ?? new SweepPlanCache();
    private readonly IWorkLog _log = log ?? new NullWorkLog();

    /// <summary>True when a chunk was claimed and handled (completed, failed for retry, deferred, released on cancellation or lost); false when nothing was available.</summary>
    public bool ProcessOne(string workerId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return false;
        var chunk = jobs.TryClaimChunk(clock.UtcNow, Lease, workerId);
        if (chunk is null) return false;
        using var span = Telemetry.Start("sweep.chunk");
        span.Tag("job_id", chunk.JobId);
        span.Tag("chunk_id", chunk.ChunkId);
        span.Tag("engine_version", Core.EngineInfo.Version);
        string? correlationId = null;
        string outcome = "failed";
        IDisposable? scope = null;
        try
        {
            _log.Info("Chunk {ChunkId} of job {JobId} claimed by {Worker} (attempt {Attempt})", chunk.ChunkId, chunk.JobId, workerId, chunk.Attempts);
            var job = jobs.TryGetJob(chunk.JobId) ?? throw new InvalidOperationException($"Job '{chunk.JobId}' vanished.");
            correlationId = job.CorrelationId;
            span.Tag("correlation_id", correlationId);
            scope = _log.Scope(correlationId, job.Id, null);
            var deals = access?.DealsOf(DealResource.Job, job.Id) ?? [];
            if (deals.Count == 0 && SubmitSweep.IsDealScoped(job.Id))
            {
                // Runs computed now would belong to no deal. Put the chunk off without using up an attempt, so the job can be repaired at any time.
                var now = clock.UtcNow;
                var age = now - job.CreatedUtc;
                var delay = TimeSpan.FromSeconds(Math.Clamp(age.TotalSeconds / 2, 1, Lease.TotalSeconds));
                jobs.DeferChunk(chunk.ChunkId, workerId, now + delay);
                outcome = "deferred";
                Telemetry.Count(Telemetry.SweepChunks, outcome);
                const string waiting = "Chunk {ChunkId} of job {JobId} has no deal grant yet; deferred for {Seconds} seconds";
                if (age < Lease) _log.Info(waiting, chunk.ChunkId, chunk.JobId, (int)delay.TotalSeconds);
                else _log.Warn(waiting, chunk.ChunkId, chunk.JobId, (int)delay.TotalSeconds);
                return true;
            }
            var set = sets.TryGet(job.Request.AssumptionSetId, out var rec) ? rec : throw new InvalidOperationException("Assumption set vanished.");
            var baseScenario = set.Assumptions.Scenarios.First(s => s.Name == job.Request.Scenario);
            var plan = _plans.Get(job);
            var results = new List<CaseResult>(chunk.CaseCount);
            var errors = 0;
            var renewedAt = clock.UtcNow;
            for (var i = chunk.FirstCase; i < chunk.FirstCase + chunk.CaseCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                // every RenewEveryCases cases, or sooner when slow cases have used a third of the lease
                var due = i > chunk.FirstCase && ((i - chunk.FirstCase) % RenewEveryCases == 0 || clock.UtcNow - renewedAt >= Lease / 3);
                if (due) renewedAt = clock.UtcNow;
                if (due && !jobs.RenewLease(chunk.ChunkId, workerId, renewedAt, Lease))
                {
                    outcome = "lease_lost";
                    Telemetry.Count(Telemetry.SweepChunks, outcome);
                    _log.Warn("Chunk {ChunkId} of job {JobId} lost its lease after {Cases} cases; stopping", chunk.ChunkId, chunk.JobId, i - chunk.FirstCase);
                    return true;
                }
                try
                {
                    var scenario = SweepOverrides.Apply(baseScenario, plan.CaseAt(i).Overrides);
                    results.Add(new CaseResult(i, submit.Submit(set.Assumptions, scenario, actor: "worker", correlationId: job.CorrelationId).Record.RunKey, null));
                }
                catch (ArgumentException e) // engine validation (SubmitRun maps engine InvalidOperation to this): recorded, the job goes on; store failures propagate and retry the chunk
                {
                    var msg = Regex.Replace(e.Message, @"\s*\(Parameter '[^']*'\)", "");
                    results.Add(new CaseResult(i, null, msg.Length > 500 ? msg[..500] : msg));
                    errors++;
                }
                // Granted before CompleteChunk and outside the catch above: a failed grant fails the chunk, and the retry grants again.
                if (deals.Count > 0 && results[^1].RunKey is { } runKey) access!.Grant(DealResource.Run, runKey, deals);
            }
            if (jobs.CompleteChunk(chunk.ChunkId, workerId, results))
            {
                outcome = "done";
                Telemetry.Count(Telemetry.SweepCases, "ok", results.Count - errors);
                Telemetry.Count(Telemetry.SweepCases, "error", errors);
                _log.Info("Chunk {ChunkId} of job {JobId} done: {Ok} ok, {Errors} errors", chunk.ChunkId, chunk.JobId, results.Count - errors, errors);
            }
            else
            {
                outcome = "lease_lost";
                _log.Warn("Chunk {ChunkId} of job {JobId} lost its lease before completion; results discarded", chunk.ChunkId, chunk.JobId);
            }
            Telemetry.Count(Telemetry.SweepChunks, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = "released";
            jobs.ReleaseChunk(chunk.ChunkId, workerId);
            _log.Info("Chunk {ChunkId} of job {JobId} released on shutdown", chunk.ChunkId, chunk.JobId);
        }
        catch (Exception e)
        {
            outcome = chunk.Attempts >= SweepLimits.MaxAttempts ? "dead" : "failed";
            // count and log first: FailChunk itself throws when the database is the thing that failed
            Telemetry.Count(Telemetry.SweepChunks, outcome);
            if (outcome == "dead") _log.Warn("Chunk {ChunkId} of job {JobId} failed on attempt {Attempt} and is dead after {Max} attempts: {Error}", chunk.ChunkId, chunk.JobId, chunk.Attempts, SweepLimits.MaxAttempts, e.GetType().Name);
            else _log.Warn("Chunk {ChunkId} of job {JobId} failed on attempt {Attempt}: {Error}", chunk.ChunkId, chunk.JobId, chunk.Attempts, e.GetType().Name);
            jobs.FailChunk(chunk.ChunkId, workerId, e.Message.Length > 500 ? e.Message[..500] : e.Message);
        }
        finally
        {
            span.Tag("outcome", outcome);
            scope?.Dispose();
        }
        return true;
    }
}
