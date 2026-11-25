namespace HotelUnderwriting.Application;

/// <summary>Submits every scenario and writes each newly computed record to the sink immediately; cached records are not written again, so the sink holds each run once.</summary>
public sealed class RunAllScenarios(IAssumptionSource source, SubmitRun submit, IResultSink sink)
{
    public IReadOnlyList<SubmitResult> Run()
    {
        var a = source.Load();
        var results = new List<SubmitResult>();
        foreach (var s in a.Scenarios)
        {
            var r = submit.Submit(a, s);
            if (!r.WasCached) sink.Write(r.Record);
            results.Add(r);
        }
        return results;
    }
}
