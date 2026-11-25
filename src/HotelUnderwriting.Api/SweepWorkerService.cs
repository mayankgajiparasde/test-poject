using HotelUnderwriting.Application;

namespace HotelUnderwriting.Api;

/// <summary>Runs <paramref name="count"/> competing worker loops in this process; each polls the job store and sleeps briefly when idle. Cancellation is honoured between cases, and stopping waits at most <see cref="StopCap"/>.</summary>
public sealed class SweepWorkerService(SweepWorker worker, int count, ILogger<SweepWorkerService> log) : BackgroundService
{
    public static readonly TimeSpan StopCap = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var prefix = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}";
        return Task.WhenAll(Enumerable.Range(1, count).Select(n => Task.Run(() => Loop($"{prefix}-{n}", ct), CancellationToken.None)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var stop = base.StopAsync(cancellationToken);
        if (await Task.WhenAny(stop, Task.Delay(StopCap, CancellationToken.None)) != stop) log.LogWarning("Sweep workers did not stop within {Seconds} seconds", StopCap.TotalSeconds);
    }

    private async Task Loop(string workerId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMilliseconds(250);
            try { if (worker.ProcessOne(workerId, ct)) continue; }
            catch (Exception e) { log.LogError(e, "Sweep worker {Worker} failed", workerId); delay = TimeSpan.FromSeconds(1); }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
        }
    }
}
