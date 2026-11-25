using System.Globalization;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

public class ApplicationTests
{
    private static readonly string WorkbookPath = Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx");
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(WorkbookPath);
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    // Base scenario carrying one refinance and one hurdle tier, so each can be changed alone.
    private static ScenarioInputs Base => A.Scenarios.First(s => s.Name == "Base") with
    {
        DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.06, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 } },
        HurdleTiers = new[] { new HurdleTier(0.15, 0.30) },
    };

    private static string Key(Assumptions a, ScenarioInputs s, string? version = null) => RunKey.Compute(a, s, version ?? EngineInfo.Version, WorkbookQuirks.None);
    private static string Key(ScenarioInputs s) => Key(A, s);

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

    private sealed class CountingStore(IRunStore inner) : IRunStore
    {
        private int _saves;
        public int Saves => _saves;
        public bool TryGet(string runKey, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RunRecord record) => inner.TryGet(runKey, out record);
        public RunRecord SaveIfAbsent(RunRecord record) { Interlocked.Increment(ref _saves); return inner.SaveIfAbsent(record); }
    }

    [Fact]
    public void Key_is_stable_64_char_lowercase_hex_under_any_culture()
    {
        var expected = Key(Base);
        Assert.Matches("^[0-9a-f]{64}$", expected);
        Assert.Equal(expected, Key(Base));
        var saved = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "de-DE", "tr-TR" })
            {
                CultureInfo.CurrentCulture = new CultureInfo(name);
                Assert.Equal(expected, Key(Base));
            }
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Each_changed_input_gives_a_different_key()
    {
        var baseKey = Key(Base);
        var lines = A.Lines.ToList();
        lines[0] = lines[0] with { FixedPct = lines[0].FixedPct + 0.01 };
        var weights = A.Seasonality.ToArray();
        weights[0] += 0.001;
        var items = A.PercentItems.ToList();
        items[0] = items[0] with { Pct = items[0].Pct + 0.001 };

        var keys = new[]
        {
            Key(Base with { PurchasePrice = Base.PurchasePrice + 1 }),
            Key(A with { Lines = lines }, Base),
            Key(A with { Seasonality = weights }, Base),
            Key(A with { Property = A.Property with { Keys = A.Property.Keys + 1 } }, Base),
            Key(A with { PercentItems = items }, Base),
            Key(Base with { InterestRate = Base.InterestRate + 0.001 }),
            Key(Base with { HoldMonths = Base.HoldMonths - 1 }),
            Key(Base with { Name = "Base renamed" }), // the stored ScenarioResult carries the name, so a rename must change the key
            Key(Base with { DebtEvents = new DebtEvent[] { ((Refinance)Base.DebtEvents[0]) with { Rate = 0.061 } } }),
            Key(Base with { HurdleTiers = new[] { new HurdleTier(0.15, 0.35) } }),
        };
        Assert.DoesNotContain(baseKey, keys);
        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public void Changing_a_sibling_scenario_leaves_the_key_unchanged()
    {
        var scenarios = A.Scenarios.Select(s => s.Name == "Upside" ? s with { PurchasePrice = s.PurchasePrice + 1 } : s).ToList();
        var baseScenario = A.Scenarios.First(s => s.Name == "Base");
        Assert.Equal(Key(baseScenario), Key(A with { Scenarios = scenarios }, baseScenario));
    }

    [Fact]
    public void Engine_version_is_part_of_the_key()
        => Assert.NotEqual(Key(A, Base), Key(A, Base, "1.0.1"));

    [Fact]
    public void Submitting_twice_computes_once_and_serves_the_second_from_the_store()
    {
        var store = new CountingStore(new InMemoryRunStore());
        var submit = new SubmitRun(store, new FixedClock(Now));
        var first = submit.Submit(A, Base);
        var second = submit.Submit(A, Base);

        Assert.False(first.WasCached);
        Assert.True(second.WasCached);
        Assert.Equal(1, store.Saves);
        Assert.Same(first.Record, second.Record);
        Assert.Equal(Now, first.Record.CreatedUtc);
        Assert.Equal(EngineInfo.Version, first.Record.EngineVersion);
        Assert.Equal(Underwriter.Run(A, Base).Summary.LeveredIrr, second.Record.Result.Summary.LeveredIrr);
    }

    [Fact]
    public void Concurrent_submits_store_one_record_and_every_caller_gets_it()
    {
        var store = new CountingStore(new InMemoryRunStore());
        var submit = new SubmitRun(store, new FixedClock(Now));
        var results = new SubmitResult[20];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i => results[i] = submit.Submit(A, Base));

        Assert.Single(results.Select(r => r.Record).Distinct());
        Assert.Single(results, r => !r.WasCached);
        Assert.True(store.TryGet(results[0].Record.RunKey, out var stored));
        Assert.Same(stored, results[0].Record);
    }

    [Fact]
    public void Workbook_quirks_are_rejected()
        => Assert.Throws<ArgumentException>(() => new SubmitRun(new InMemoryRunStore(), new FixedClock(Now)).Submit(A, Base, WorkbookQuirks.PayoffOnOpeningBalance));

    [Fact]
    public void RunAllScenarios_matches_direct_runs_and_second_run_is_all_cached()
    {
        var sink = new InMemoryResultSink();
        var useCase = new RunAllScenarios(new InMemoryAssumptionSource(A), new SubmitRun(new InMemoryRunStore(), new FixedClock(Now)), sink);

        var first = useCase.Run();
        Assert.Equal(3, sink.Records.Count);
        Assert.All(first, r => Assert.False(r.WasCached));
        Assert.Equal(3, sink.Records.Count);
        foreach (var r in sink.Records)
            Assert.Equal(Underwriter.Run(A, r.Result.Inputs).Summary.LeveredIrr, r.Result.Summary.LeveredIrr);

        Assert.All(useCase.Run(), r => Assert.True(r.WasCached));
        Assert.Equal(3, sink.Records.Count); // cached records are not written again
    }

    [Fact]
    public void Quirks_change_the_key()
        => Assert.NotEqual(Key(Base), RunKey.Compute(A, Base, EngineInfo.Version, WorkbookQuirks.PayoffOnOpeningBalance));

    [Fact]
    public void Assumptions_members_are_exactly_those_in_the_key()
        => Assert.Equal(
            new[] { "Lines", "PercentItems", "Property", "Scenarios", "Seasonality" },
            typeof(Assumptions).GetProperties().Select(p => p.Name).Order().ToArray());

    [Fact]
    public void Round_trip_doubles_are_distinguished()
        => Assert.NotEqual(Key(Base with { InterestRate = 0.1 + 0.2 }), Key(Base with { InterestRate = 0.3 }));

    [Fact]
    public void Negative_zero_and_zero_give_the_same_key()
        => Assert.Equal(Key(Base with { InterestRate = 0.0 }), Key(Base with { InterestRate = -0.0 }));

    [Fact]
    public void Non_finite_input_is_an_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => new SubmitRun(new InMemoryRunStore(), new FixedClock(Now)).Submit(A, Base with { InterestRate = double.NaN }));
        Assert.Contains("non-finite", ex.Message);
    }

    [Fact]
    public void Another_engine_version_is_a_cache_miss_and_is_recorded()
    {
        var store = new InMemoryRunStore();
        var clock = new FixedClock(Now);
        var v1 = new SubmitRun(store, clock).Submit(A, Base);
        var v2 = new SubmitRun(store, clock, "1.0.1").Submit(A, Base);
        Assert.False(v1.WasCached);
        Assert.False(v2.WasCached);
        Assert.Equal("1.0.1", v2.Record.EngineVersion);
        Assert.NotEqual(v1.Record.RunKey, v2.Record.RunKey);
    }

    [Fact]
    public void Null_scenario_and_blank_engine_version_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SubmitRun(new InMemoryRunStore(), new FixedClock(Now)).Submit(A, null!));
        Assert.Throws<ArgumentException>(() => new SubmitRun(new InMemoryRunStore(), new FixedClock(Now), " "));
    }

    [Fact]
    public void Layering_core_is_standalone_and_application_depends_on_core_only()
    {
        static string[] Refs(Type t) => t.Assembly.GetReferencedAssemblies().Select(n => n.Name!).ToArray();
        static bool Forbidden(string n) => n == "HotelUnderwriting.Excel" || n.StartsWith("ClosedXML", StringComparison.Ordinal);

        var core = Refs(typeof(Underwriter));
        Assert.DoesNotContain(core, n => n == "HotelUnderwriting.Application" || Forbidden(n));

        var app = Refs(typeof(SubmitRun));
        Assert.Contains("HotelUnderwriting.Core", app);
        Assert.DoesNotContain(app, Forbidden);
    }

    [Fact]
    public void Assumption_set_id_follows_every_input_and_is_order_sensitive()
    {
        var baseline = AssumptionSetId.Compute(A);
        Assert.Matches("^[0-9a-f]{64}$", baseline);
        Assert.Equal(baseline, AssumptionSetId.Compute(A with { Scenarios = A.Scenarios.ToArray() }));

        Assumptions Second(Func<ScenarioInputs, ScenarioInputs> f) => A with { Scenarios = A.Scenarios.Select((s, i) => i == 1 ? f(s) : s).ToArray() };
        var lines = A.Lines.ToList(); lines[0] = lines[0] with { FixedPct = lines[0].FixedPct + 0.01 };
        var items = A.PercentItems.ToList(); items[0] = items[0] with { Pct = items[0].Pct + 0.01 };
        var weights = A.Seasonality.ToArray(); weights[0] += 0.001;
        var changed = new[]
        {
            A with { Property = A.Property with { Keys = A.Property.Keys + 1 } },
            A with { Lines = lines },
            A with { PercentItems = items },
            A with { Seasonality = weights },
            Second(s => s with { PurchasePrice = s.PurchasePrice + 1 }),
            Second(s => s with { DebtEvents = new DebtEvent[] { new Refinance { Month = 24, Rate = 0.06, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = 0.01 } } }),
            Second(s => s with { HurdleTiers = new[] { new HurdleTier(0.15, 0.30) } }),
            // The id is order-sensitive: the same scenarios in another order are a different set.
            A with { Scenarios = A.Scenarios.Reverse().ToArray() },
        };
        var ids = changed.Select(AssumptionSetId.Compute).ToList();
        Assert.DoesNotContain(baseline, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private sealed class ListLedger : ILedger
    {
        private readonly List<LedgerEntry> _e = new();
        public void Append(LedgerEntry entry) { lock (_e) _e.Add(entry); }
        public string[] Actions { get { lock (_e) return _e.Select(x => x.Action).ToArray(); } }
    }

    /// <summary>Hides the atomic interface so the use case takes its non-atomic fallback.</summary>
    private sealed class PlainSetStore(IAssumptionSetStore inner) : IAssumptionSetStore
    {
        public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out AssumptionSetRecord record) => inner.TryGet(id, out record);
        public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record) => inner.SaveIfAbsent(record);
    }

    [Fact]
    public void Non_atomic_store_fallback_appends_one_ledger_row_per_submit()
    {
        var ledger = new ListLedger();
        var submit = new SubmitRun(new CountingStore(new InMemoryRunStore()), new FixedClock(Now), ledger: ledger);
        Assert.False(submit.Submit(A, Base).WasCached);
        Assert.Equal(new[] { LedgerEntry.RunComputed }, ledger.Actions);
        Assert.True(submit.Submit(A, Base).WasCached);
        Assert.Equal(new[] { LedgerEntry.RunComputed, LedgerEntry.RunServedFromStore }, ledger.Actions);
    }

    [Fact]
    public void Non_atomic_store_fallback_appends_one_published_row_per_publish()
    {
        var ledger = new ListLedger();
        var publish = new PublishAssumptionSet(new PlainSetStore(new InMemoryAssumptionSetStore()), new FixedClock(Now), ledger);
        Assert.True(publish.Publish(A).Created);
        Assert.Equal(new[] { LedgerEntry.AssumptionSetPublished }, ledger.Actions);
        Assert.False(publish.Publish(A).Created);
        Assert.Equal(new[] { LedgerEntry.AssumptionSetPublished, LedgerEntry.AssumptionSetPublished }, ledger.Actions);
    }

    [Fact]
    public void In_memory_parallel_identical_submits_log_one_computed_and_the_rest_served()
    {
        var ledger = new InMemoryLedger();
        var store = new InMemoryRunStore(ledger);
        var submit = new SubmitRun(store, new FixedClock(Now), ledger: ledger);
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, _ => submit.Submit(A, Base));
        var actions = ledger.Entries.Select(e => e.Action).ToList();
        Assert.Equal(1, actions.Count(a => a == LedgerEntry.RunComputed));
        Assert.Equal(19, actions.Count(a => a == LedgerEntry.RunServedFromStore));
        Assert.True(store.TryGet(Key(Base), out _));
    }

    [Fact]
    public void In_memory_parallel_identical_publishes_log_every_call_and_store_one_set()
    {
        var ledger = new InMemoryLedger();
        var store = new InMemoryAssumptionSetStore(ledger);
        var publish = new PublishAssumptionSet(store, new FixedClock(Now), ledger);
        Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 20 }, _ => publish.Publish(A));
        Assert.Equal(20, ledger.Entries.Count(e => e.Action == LedgerEntry.AssumptionSetPublished));
        Assert.True(store.TryGet(AssumptionSetId.Compute(A), out _));
    }
}
