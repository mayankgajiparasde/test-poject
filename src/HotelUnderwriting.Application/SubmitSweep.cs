using HotelUnderwriting.Core;

namespace HotelUnderwriting.Application;

/// <summary>Validates a sweep and stores it as a queued job (AD-12). Stores the request, never the expanded cases.</summary>
public sealed class SubmitSweep(IJobStore jobs, IClock clock)
{
    private const string DealScopedPrefix = "d-";

    /// <summary>True for a job submitted for one or more deals: it must have its grant before a worker may process it. Part of the job id so it is stored atomically with the job.</summary>
    public static bool IsDealScoped(string jobId) => jobId.StartsWith(DealScopedPrefix, StringComparison.Ordinal);

    public (JobRecord Job, bool Created) Submit(SweepRequest request, string? idempotencyKey = null, string actor = "anonymous", string? correlationId = null, bool dealScoped = false)
    {
        var plan = SweepPlan.Create(request);
        // new submissions only: SweepPlan.Create also reads stored jobs, which must stay readable
        foreach (var c in request.Cases ?? []) AssumptionRules.CheckCaseName(c.Name);
        var id = (dealScoped ? DealScopedPrefix : "") + Guid.NewGuid().ToString("N");
        correlationId ??= Guid.NewGuid().ToString();
        var hash = request.Hash();
        var now = clock.UtcNow;
        var job = new JobRecord(id, request, hash, correlationId, now, plan.CaseCount, plan.ChunkCount);
        return jobs.Create(job, idempotencyKey, new LedgerEntry(now, actor, LedgerEntry.SweepSubmitted, id, EngineInfo.Version, hash, null, correlationId));
    }
}
