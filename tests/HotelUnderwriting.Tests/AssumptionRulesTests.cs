using System.Globalization;
using HotelUnderwriting.Application;
using HotelUnderwriting.Core;
using HotelUnderwriting.Excel;
using Xunit;

namespace HotelUnderwriting.Tests;

/// <summary>Story 12: every publish and run rule, each with a value just inside (accepted) and just outside (422). A rule removed from <see cref="AssumptionRules"/> fails its row.</summary>
public class AssumptionRulesTests
{
    private static readonly Assumptions A = WorkbookReader.ReadAssumptions(Path.Combine(AppContext.BaseDirectory, "data", "Hotel_Acquisition_Underwriting_Model.xlsx"));
    private static ScenarioInputs Base => A.Scenarios.First(s => s.Name == "Base");

    private static Assumptions WithBase(Func<ScenarioInputs, ScenarioInputs> change) =>
        A with { Scenarios = A.Scenarios.Select(s => s.Name == "Base" ? change(s) : s).ToArray() };

    /// <summary>The refusal text, or null when the set publishes.</summary>
    private static string? Refusal(Assumptions a)
    {
        try { new PublishAssumptionSet(new InMemoryAssumptionSetStore(), new SystemClock()).Publish(a); return null; }
        catch (InvalidAssumptionsException e) { return e.Message; }
    }

    private static void Accepts(string rule, Assumptions a) => Assert.True(Refusal(a) is null, $"{rule}: expected to publish, got: {Refusal(a)}");

    /// <summary>The refusal must carry this fixed text: a refusal for any other reason (an engine rule, another rule) does not count.</summary>
    private static void Refuses(string rule, Assumptions a, string text, string? echoed = null)
    {
        var message = Refusal(a);
        Assert.True(message is not null, $"{rule}: expected a 422");
        Assert.True(message!.Contains(text), $"{rule}: expected \"{text}\", got \"{message}\"");
        if (echoed is { Length: >= 4 }) Assert.DoesNotContain(echoed, message);
    }

    private static string Show(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private const string Fin = "number that is too large or not finite";
    private const string Chars = "no control characters, format or invisible characters";

    /// <summary>Rows marked "engine" are rules <c>Underwriter</c> already enforced before story 12: they show the boundary and are not claimed as new.</summary>
    private static readonly (string Rule, Func<ScenarioInputs, double, ScenarioInputs> Set, double[] Inside, double[] Outside, string Text)[] NumberRules =
    [
        ("purchase price > 0", (s, v) => s with { PurchasePrice = v }, [1, 123456789], [0, -1, -123456789], "purchase price must be greater than 0"),
        ("closing costs >= 0", (s, v) => s with { ClosingCosts = v }, [0], [-1, -123456789], "closing costs must not be negative"),
        ("placement costs >= 0", (s, v) => s with { PlacementCosts = v }, [0], [-1, -123456789], "placement costs must not be negative"),
        ("PIP per key >= 0", (s, v) => s with { PipPerKey = v }, [0], [-1, -123456789], "PIP per key must not be negative"),
        ("acquisition fee in [0,1]", (s, v) => s with { AcquisitionFeePct = v }, [0, 1], [-0.0001, 1.0001, 5], "acquisition fee must be from 0 to 1"),
        ("origination fee in [0,1]", (s, v) => s with { OriginationFeePct = v }, [0, 1], [-0.0001, 1.0001, 5], "origination fee must be from 0 to 1"),
        ("disposition fee in [0,1]", (s, v) => s with { DispositionFeePct = v }, [0, 0.5], [-0.0001, 1.0001, 5], "disposition fee must be from 0 to 1"),
        ("cost of sale in [0,1]", (s, v) => s with { CostOfSalePct = v }, [0, 0.5], [-0.0001, 1.0001, 5], "cost of sale must be from 0 to 1"),
        ("asset management in [0,1]", (s, v) => s with { AssetMgmtFeePct = v }, [0, 1], [-0.0001, 1.0001, 5], "asset management fee must be from 0 to 1"),
        ("interest rate in [0,1]", (s, v) => s with { InterestRate = v }, [0, 1], [-0.0001, 1.0001, 10], "interest rate must be from 0 to 1"),
        ("exit cap rate > 0 (engine)", (s, v) => s with { ExitCapRate = v }, [0.0001, 1], [0, -0.01], "exit cap rate must be positive"),
        ("exit cap rate <= 1", (s, v) => s with { ExitCapRate = v }, [0.0001, 1], [1.0001, 5], "exit cap rate must be at most 1"),
        ("loan-to-cost in [0,1]", (s, v) => s with { LoanToCost = v }, [0, 1], [-0.0001, 1.0001, 1.5], "loan-to-cost must be from 0 to 1"),
        ("GP share in [0,1]", (s, v) => s with { GpShare = v }, [0, 1], [-0.0001, 1.0001], "GP share must be from 0 to 1"),
        ("promote in [0,1]", (s, v) => s with { Promote = v }, [0, 1], [-0.0001, 1.0001], "promote must be from 0 to 1"),
        ("preferred return in [0,1]", (s, v) => s with { PrefReturn = v }, [0, 1], [-0.0001, 1.0001], "preferred return must be from 0 to 1"),
        ("occupancy in [0,1]", (s, v) => s with { Occupancy = [v, .. s.Occupancy.Skip(1)] }, [0, 1], [-0.0001, 1.0001, 5], "occupancy must be from 0 to 1"),
        ("ADR growth > -1", (s, v) => s with { AdrGrowth = [v, .. s.AdrGrowth.Skip(1)] }, [-0.99, 0], [-1, -1.5], "ADR growth must be greater than -1"),
        ("expense inflation > -1", (s, v) => s with { ExpenseInflation = v }, [-0.99, 0], [-1, -1.5], "expense inflation must be greater than -1"),
        ("amortization years > 0 (engine)", (s, v) => s with { AmortizationYears = v }, [0.5, 30], [0, -1], "amortization years must be positive"),
        ("interest-only months >= 0", (s, v) => s with { InterestOnlyMonths = (int)v }, [0, 12], [-1], "interest-only months must not be negative"),
    ];

    [Fact]
    public void Every_scenario_number_rule_accepts_the_boundary_inside_and_refuses_the_one_outside()
    {
        foreach (var (rule, set, inside, outside, text) in NumberRules)
        {
            foreach (var v in inside) Accepts($"{rule} with {Show(v)}", WithBase(s => set(s, v)));
            foreach (var v in outside) Refuses($"{rule} with {Show(v)}", WithBase(s => set(s, v)), text, Show(v));
        }
    }

    [Fact]
    public void The_same_rules_refuse_a_run_and_store_nothing()
    {
        foreach (var (rule, set, _, outside, text) in NumberRules)
            foreach (var v in outside)
            {
                var store = new InMemoryRunStore();
                var scenario = set(Base, v);
                var e = Assert.ThrowsAny<ArgumentException>(() => new SubmitRun(store, new SystemClock()).Submit(A, scenario));
                Assert.True(e.Message.Contains(text), $"{rule} with {Show(v)}: got \"{e.Message}\"");
                Assert.False(store.TryGet(RunKey.Compute(A, scenario, EngineInfo.Version, WorkbookQuirks.None), out _), $"{rule} with {Show(v)}: a run was stored");
            }
    }

    [Fact]
    public void Property_rules()
    {
        // exactly 0 base occupancy passes the range rule and is then refused by the finite check (the engine divides by it)
        Refuses("base occupancy 0", A with { Property = A.Property with { BaseOccupancy = 0 } }, "has a zero base year because the base occupancy is 0");
        Accepts("keys 1", A with { Property = A.Property with { Keys = 1 } });
        foreach (var v in new[] { 0, -1 }) Refuses($"keys {v} (engine)", A with { Property = A.Property with { Keys = v } }, "Keys must be positive");
        foreach (var v in new[] { 0.0001, 1 }) Accepts($"base occupancy {v}", A with { Property = A.Property with { BaseOccupancy = v } });
        foreach (var v in new[] { -0.0001, 1.0001, 5 }) Refuses($"base occupancy {v}", A with { Property = A.Property with { BaseOccupancy = v } }, "Base occupancy must be from 0 to 1");
        Accepts("base ADR 1", A with { Property = A.Property with { BaseAdr = 1 } });
        foreach (var v in new[] { 0.0, -1 }) Refuses($"base ADR {v}", A with { Property = A.Property with { BaseAdr = v } }, "Base ADR must be greater than 0");
    }

    [Fact]
    public void Percent_of_revenue_rates_are_in_0_to_1()
    {
        PercentItem[] With(double pct) => [A.PercentItems[0] with { Pct = pct }, .. A.PercentItems.Skip(1)];
        foreach (var v in new[] { 0.0, 0.1 }) Accepts($"percent item {v}", A with { PercentItems = With(v) });
        foreach (var v in new[] { -0.0001, 1.0001, 5 }) Refuses($"percent item {v}", A with { PercentItems = With(v) }, "percent-of-revenue rate must be from 0 to 1", Show(v));
    }

    [Fact]
    public void Pip_must_fit_the_hold_period_when_there_is_a_pip_budget()
    {
        Assumptions Pip(double perKey, int start, int duration) => WithBase(s => s with { HoldMonths = 12, PipPerKey = perKey, PipStartMonth = start, PipDurationMonths = duration });
        Accepts("pip 1..12", Pip(1000, 1, 12));
        Accepts("pip ends in the last month", Pip(1000, 12, 1));
        Refuses("pip duration 0", Pip(1000, 1, 0), "PIP duration must be at least 1");
        Refuses("pip duration -1", Pip(1000, 1, -1), "PIP duration must be at least 1");
        Refuses("pip start 0", Pip(1000, 0, 3), "PIP start month must be at least 1");
        Refuses("pip start -3", Pip(1000, -3, 3), "PIP start month must be at least 1");
        Refuses("pip ends one month after the hold", Pip(1000, 12, 2), "the PIP must end within the hold period");
        Refuses("pip start far past the hold", Pip(1000, int.MaxValue, int.MaxValue), "the PIP must end within the hold period");
        Accepts("no pip budget: its timing is not checked", Pip(0, 0, 0));
    }

    [Fact]
    public void Years_must_be_strictly_ascending()
    {
        var years = Base.Years.ToArray();
        Accepts("ascending", WithBase(s => s with { Years = years }));
        (years[0], years[1]) = (years[1], years[0]);
        Refuses("swapped", WithBase(s => s with { Years = years }), "years must be strictly ascending");
        years[0] = years[1];
        Refuses("equal", WithBase(s => s with { Years = years }), "years must be strictly ascending");
    }

    [Fact]
    public void Names_and_codes_are_not_empty_not_padded_and_unique()
    {
        Assumptions WithLine(string code) => A with { Lines = [.. A.Lines, A.Lines[0] with { Code = code, Section = "FIXED", BaseAmount = 1, Driver = "OCC_ROOMS" }] };
        Assumptions WithItem(string code) => A with { PercentItems = [.. A.PercentItems, A.PercentItems[0] with { Code = code }] };
        Accepts("scenario Base2", A with { Scenarios = [A.Scenarios[0] with { Name = "Base2" }] });
        foreach (var bad in new[] { "", " ", "Base ", " Base" }) Refuses($"scenario name '{bad}'", A with { Scenarios = [A.Scenarios[0] with { Name = bad }] }, "scenario name must not be empty");
        Refuses("duplicate scenario names (rule from before story 12)", A with { Scenarios = [A.Scenarios[0], A.Scenarios[0]] }, "Scenario names must be unique");

        Accepts("a new line code", WithLine("Z1"));
        foreach (var bad in new[] { "", " ", "Z1 ", " Z1" }) Refuses($"line code '{bad}'", WithLine(bad), "line code must not be empty");
        Refuses("duplicate line codes", WithLine(A.Lines[0].Code), "Each line code must be unique");
        Refuses("no operating line", A with { Lines = [] }, "at least one operating line");

        Accepts("a new item code", WithItem("Z1"));
        foreach (var bad in new[] { "", " ", "Z1 ", " Z1" }) Refuses($"item code '{bad}'", WithItem(bad), "item code must not be empty");
        Refuses("duplicate item codes", WithItem(A.PercentItems[0].Code), "Each percent-of-revenue item code must be unique");
    }

    [Fact]
    public void The_acquisition_date_is_the_first_of_a_month_at_midnight_and_within_range()
    {
        const string First = "must be the 1st of a month, at midnight";
        Assumptions On(DateTime d) => A with { Property = A.Property with { AcquisitionDate = d } };
        Accepts("reference date", A);
        Accepts("another first of a month", On(new DateTime(2027, 6, 1)));
        Refuses("mid month", On(new DateTime(2027, 4, 15)), First);
        Refuses("last day of a month", On(new DateTime(2027, 4, 30)), First);
        Refuses("time of day", On(new DateTime(2027, 4, 1, 12, 0, 0)), First);
        Refuses("one tick after midnight", On(new DateTime(2027, 4, 1).AddTicks(1)), First);
        var overflow = Assert.Throws<InvalidAssumptionsException>(() => new PublishAssumptionSet(new InMemoryAssumptionSetStore(), new SystemClock()).Publish(On(new DateTime(9999, 12, 1))));
        Assert.Equal("The acquisition date is too far in the future.", overflow.Message); // fixed text, no .NET message
    }

    private static string Char(int code) => ((char)code).ToString();

    [Fact]
    public void Control_format_invisible_and_unassigned_characters_are_refused_everywhere_text_is_taken()
    {
        // Cc, Cf (bidi, zero-width, LRM/RLM/ALM, soft hyphen, U+180E, invisible operators, tag characters, interlinear), Cs (lone), Co, Cn, Zl, Zp, and U+034F
        var bad = new[] { 0x01, 0x7F, 0x85, 0x202E, 0x202A, 0x2066, 0x2069, 0x200B, 0x200D, 0x2060, 0xFEFF, 0x200E, 0x200F, 0x061C, 0x00AD, 0x180E, 0x2061, 0x2064, 0xFFF9, 0xFFFB, 0x2028, 0x2029, 0xE000, 0x0378, 0x034F, 0xE0001, 0xE007F, 0xF0000 }
            .Select(char.ConvertFromUtf32).Concat([Char(0xD800), Char(0xDC00)]).ToArray();
        foreach (var c in bad)
        {
            var text = "a" + c + "b";
            Refuses($"property U+{(int)c[0]:X4}", A with { Property = A.Property with { Name = text } }, Chars);
            Refuses($"scenario U+{(int)c[0]:X4}", A with { Scenarios = [A.Scenarios[0] with { Name = text }] }, Chars);
            Refuses($"line code U+{(int)c[0]:X4}", A with { Lines = [A.Lines[0] with { Code = text }, .. A.Lines.Skip(1)] }, Chars);
            Refuses($"line name U+{(int)c[0]:X4}", A with { Lines = [A.Lines[0] with { Name = text }, .. A.Lines.Skip(1)] }, Chars);
            Refuses($"section U+{(int)c[0]:X4}", A with { Lines = [A.Lines[0] with { Section = text }, .. A.Lines.Skip(1)] }, Chars);
            Refuses($"driver U+{(int)c[0]:X4}", A with { Lines = [A.Lines[0] with { Driver = text }, .. A.Lines.Skip(1)] }, Chars);
            Refuses($"item code U+{(int)c[0]:X4}", A with { PercentItems = [A.PercentItems[0] with { Code = text }, .. A.PercentItems.Skip(1)] }, Chars);
            Refuses($"item base U+{(int)c[0]:X4}", A with { PercentItems = [A.PercentItems[0] with { Base = text }, .. A.PercentItems.Skip(1)] }, Chars);
            var request = new SweepRequest("s", "Base", [new SweepCase([new ScenarioOverride("interestRate", 0.06)], text)]);
            var e = Assert.Throws<InvalidAssumptionsException>(() => new SubmitSweep(new InMemoryJobStore(), new SystemClock()).Submit(request));
            Assert.Contains(Chars, e.Message);
            Assert.Equal(1, SweepPlan.Create(request).CaseCount); // a stored job (read through SweepPlan) stays readable
        }
        foreach (var ok in new[] { "Hôtel Zürich 東京", "فندق الشرق", "Ελληνικά", "A-B_C.1 (x)", "Base 2", "emoji " + char.ConvertFromUtf32(0x1F3E8), char.ConvertFromUtf32(0x1D49C) + "x" })
            Accepts($"ordinary text '{ok}'", A with { Property = A.Property with { Name = ok }, Scenarios = [A.Scenarios[0] with { Name = ok }, .. A.Scenarios.Skip(1)] });
        foreach (var padded in new[] { "Base" + Char(0xA0), Char(0xA0) + "Base", Char(0x3000) + "Base", "Base" + Char(0x3000), "Base" + Char(0x2003) })
            Refuses($"padded name U+{(int)(padded[0] == 'B' ? padded[^1] : padded[0]):X4}", A with { Scenarios = [A.Scenarios[0] with { Name = padded }] }, "scenario name must not be empty");
    }

    [Fact]
    public void Every_numeric_series_of_a_result_must_be_finite()
    {
        AssumptionRules.CheckFinite(Underwriter.Run(A, Base));
        void Poison(string what, Action<ScenarioResult> set)
        {
            var r = Underwriter.Run(A, Base);
            set(r);
            Assert.True(Record.Exception(() => AssumptionRules.CheckFinite(r)) is InvalidAssumptionsException, $"{what}: a non-finite value was not refused");
        }
        foreach (var (name, series) in new (string, Func<MonthlySchedule, double[]>)[]
                 {
                     ("PrefBop", m => m.PrefBop), ("PrefEop", m => m.PrefEop), ("UnleveredCf", m => m.UnleveredCf), ("LeveredCf", m => m.LeveredCf),
                     ("LpCf", m => m.LpCf), ("GpCf", m => m.GpCf), ("Tier1", m => m.Tier1), ("Interest", m => m.Interest), ("Revenue", m => m.Revenue),
                 })
            Poison(name, r => series(r.Monthly)[5] = double.NaN);
        Poison("proforma noi", r => r.Proforma.Noi[1] = double.PositiveInfinity);
        Poison("proforma line", r => r.Proforma.Lines[0].Amounts[1] = double.NaN);
    }

    [Fact]
    public void A_huge_price_is_refused_also_through_the_pref_series()
    {
        var e = Assert.Throws<InvalidAssumptionsException>(() => new PublishAssumptionSet(new InMemoryAssumptionSetStore(), new SystemClock()).Publish(WithBase(s => s with { PurchasePrice = 1e308 })));
        Assert.Equal(InvalidAssumptionsException.NotFinite, e.Message);
    }

    [Fact]
    public void Sweep_cases_that_break_a_rule_or_overflow_are_case_errors_and_no_run_is_stored()
    {
        var clock = new SystemClock();
        var sets = new InMemoryAssumptionSetStore();
        var jobs = new InMemoryJobStore();
        var runs = new InMemoryRunStore();
        var setId = new PublishAssumptionSet(sets, clock).Publish(A).Record.Id;
        var worker = new SweepWorker(jobs, sets, new SubmitRun(runs, clock), clock);
        var cases = new (string Path, double Value)[] { ("interestRate", 1e308), ("loanToCost", 1e308), ("expenseInflation", 1e300), ("pipPerKey", 1e308), ("purchasePrice", 0), ("purchasePrice", 1e308) };
        var req = new SweepRequest(setId, "Base", cases.Select(c => new SweepCase([new ScenarioOverride(c.Path, c.Value)])).ToArray());
        var job = new SubmitSweep(jobs, clock).Submit(req).Job;
        while (worker.ProcessOne("w")) { }

        var results = jobs.GetResults(job.Id, -1, int.MaxValue);
        Assert.Equal(cases.Length, results.Count);
        Assert.All(results, r => { Assert.Null(r.RunKey); Assert.False(string.IsNullOrEmpty(r.Error)); Assert.DoesNotContain("NaN", r.Error); Assert.DoesNotContain("Infinity", r.Error); });
        Assert.Equal((cases.Length, 0), (jobs.GetProgress(job.Id)!.FailedCases, jobs.GetProgress(job.Id)!.CompletedCases - jobs.GetProgress(job.Id)!.FailedCases));
        foreach (var (path, value) in cases)
            Assert.False(runs.TryGet(RunKey.Compute(A, SweepOverrides.Apply(Base, [new ScenarioOverride(path, value)]), EngineInfo.Version, WorkbookQuirks.None), out _));
    }

    [Fact]
    public void A_stored_run_is_served_without_checking_the_rules_again()
    {
        // a run stored by an earlier version for inputs that today's rules refuse (purchase price 0)
        var old = Base with { PurchasePrice = 0 };
        Assert.Throws<InvalidAssumptionsException>(() => new SubmitRun(new InMemoryRunStore(), new SystemClock()).Submit(A, old)); // the rules do refuse it
        var store = new InMemoryRunStore();
        var record = new RunRecord(RunKey.Compute(A, old, EngineInfo.Version, WorkbookQuirks.None), EngineInfo.Version, DateTimeOffset.UtcNow, Underwriter.Run(A, old));
        store.SaveIfAbsent(record);
        var served = new SubmitRun(store, new SystemClock()).Submit(A, old);
        Assert.True(served.WasCached);
        Assert.Same(record, served.Record);
    }

    [Fact]
    public void The_reference_workbook_still_publishes_with_the_same_ids_and_returns()
    {
        Accepts("reference workbook", A);
        Assert.Equal("bddceda5e0021a3a43bdda20bbfc61f1fcb9cea6a737f13442280ad3993c2f99", AssumptionSetId.Compute(A));
        Assert.Equal("974d28bf9084e5375243e7607dd1be3156eb5bf8d1892757e62b63d7c7be3929", RunKey.Compute(A, Base, EngineInfo.Version, WorkbookQuirks.None));
        var irr = A.Scenarios.ToDictionary(s => s.Name, s => Math.Round(Underwriter.Run(A, s).Summary.LeveredIrr!.Value * 100, 4));
        Assert.Equal((18.3860, 32.3000, -3.8510), (irr["Base"], irr["Upside"], irr["Downside"]));
    }

    // ---- story 12b: unbounded numbers, case-insensitive names, no database for rules

    [Fact]
    public void Line_shares_percent_items_and_sales_costs_are_bounded()
    {
        OperatingLine[] Line(Func<OperatingLine, OperatingLine> f) => [f(A.Lines[0]), .. A.Lines.Skip(1)];
        foreach (var v in new[] { 0.0, 1 }) Accepts($"fixed share {v}", A with { Lines = Line(l => l with { FixedPct = v }) });
        foreach (var v in new[] { -0.0001, 1.0001, 5 }) Refuses($"fixed share {v}", A with { Lines = Line(l => l with { FixedPct = v }) }, "fixed share must be from 0 to 1", Show(v));
        Accepts("base amount 0", A with { Lines = [.. A.Lines.SkipLast(1), A.Lines[^1] with { BaseAmount = 0 }] });
        Refuses("base amount -1", A with { Lines = Line(l => l with { BaseAmount = -1 }) }, "base amount must not be negative");

        PercentItem[] Items(double a) => [A.PercentItems[0] with { Pct = a }, .. A.PercentItems.Skip(1)];
        var rest = A.PercentItems.Skip(1).Sum(i => i.Pct);
        Accepts("percent items add up to exactly 1", A with { PercentItems = Items(1 - rest) });
        Refuses("percent items add up to more than 1", A with { PercentItems = Items(1 - rest + 0.0001) }, "must add up to at most 1");

        foreach (var (cost, fee, ok) in new[] { (0.5, 0.5, true), (1.0, 0.0, true), (0.5, 0.5001, false), (1.0, 1.0, false) })
        {
            var a = WithBase(s => s with { CostOfSalePct = cost, DispositionFeePct = fee });
            if (ok) Accepts($"cost of sale {cost} + disposition {fee}", a);
            else Refuses($"cost of sale {cost} + disposition {fee}", a, "cost of sale and disposition fee together must be at most 1");
        }
    }

    [Fact]
    public void Refinance_rate_fee_and_hurdles_have_an_upper_bound()
    {
        Assumptions Refi(double rate, double fee) => WithBase(s => s with { DebtEvents = [new Refinance { Month = 24, Rate = rate, InterestOnlyMonths = 12, AmortizationYears = 30, FeePct = fee }] });
        Accepts("refinance rate 1, fee 1", Refi(1, 1));
        Refuses("refinance rate 1.0001", Refi(1.0001, 0.01), "a refinance rate must be at most 1");
        Refuses("refinance fee 1.0001", Refi(0.06, 1.0001), "a refinance fee must be at most 1");
        Assert.Contains("rate must not be negative", Refusal(Refi(-0.01, 0.01))); // the engine's own lower bound
        Assert.Contains("fee must not be negative", Refusal(Refi(0.06, -0.01)));
        Assumptions Tier(double hurdle) => WithBase(s => s with { PrefReturn = 0.08, GpShare = 0.1, Promote = 0.2, HurdleTiers = [new HurdleTier(hurdle, 0.3)] });
        Accepts("hurdle 1", Tier(1));
        Refuses("hurdle 1.0001", Tier(1.0001), "a hurdle must be at most 1");
        Assert.Contains("must be above the pref return", Refusal(Tier(0.08))); // the engine's own lower bound
    }

    [Fact]
    public void Sweep_cases_obey_the_new_bounds_and_a_stored_run_is_untouched()
    {
        var store = new InMemoryRunStore();
        var submit = new SubmitRun(store, new SystemClock());
        var e = Assert.Throws<InvalidAssumptionsException>(() => submit.Submit(A, Base with { CostOfSalePct = 0.6, DispositionFeePct = 0.6 }));
        Assert.Contains("together must be at most 1", e.Message);
    }

    [Fact]
    public void Scenario_names_are_unique_ignoring_case_and_sweep_case_names_are_not_empty_or_padded()
    {
        Refuses("Base vs base", A with { Scenarios = [A.Scenarios[0] with { Name = "Base" }, A.Scenarios[1] with { Name = "base" }] }, "unique, ignoring case");
        Accepts("Base vs Base2", A with { Scenarios = [A.Scenarios[0] with { Name = "Base" }, A.Scenarios[1] with { Name = "Base2" }] });
        foreach (var bad in new[] { "", " ", " x", "x ", Char(0xA0) + "x" })
        {
            var request = new SweepRequest("s", "Base", [new SweepCase([new ScenarioOverride("interestRate", 0.06)], bad)]);
            Assert.Throws<InvalidAssumptionsException>(() => new SubmitSweep(new InMemoryJobStore(), new SystemClock()).Submit(request));
            Assert.Equal(1, SweepPlan.Create(request).CaseCount); // stored jobs stay readable
        }
        var good = new SweepRequest("s", "Base", [new SweepCase([new ScenarioOverride("interestRate", 0.06)], "six percent"), new SweepCase([new ScenarioOverride("interestRate", 0.07)])]);
        Assert.Equal(2, new SubmitSweep(new InMemoryJobStore(), new SystemClock()).Submit(good).Job.CaseCount);
    }

    [Fact]
    public void An_unknown_driver_is_refused_without_quoting_it()
    {
        var a = A with { Lines = [A.Lines[0] with { Driver = "zq-secret-driver" }, .. A.Lines.Skip(1)] };
        var message = Refusal(a)!;
        Assert.DoesNotContain("zq-secret", message);
        Assert.Contains($"Line {A.Lines[0].Code}: unknown or not-yet-computed driver", message);
    }

    private sealed class DownStore : IAtomicAssumptionSetStore
    {
        public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out AssumptionSetRecord record) => throw new InvalidOperationException("database down");
        public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record) => throw new InvalidOperationException("database down");
        public AssumptionSetRecord SaveIfAbsent(AssumptionSetRecord record, LedgerEntry ledgerEntry) => throw new InvalidOperationException("database down");
    }

    [Fact]
    public void Rules_that_need_no_engine_run_are_answered_without_touching_the_store()
    {
        var publish = new PublishAssumptionSet(new DownStore(), new SystemClock());
        foreach (var broken in new[]
                 {
                     WithBase(s => s with { PurchasePrice = 0 }),
                     A with { Property = A.Property with { AcquisitionDate = new DateTime(2027, 4, 15) } },
                     A with { Scenarios = [A.Scenarios[0] with { Name = "Base " }] },
                     A with { Lines = [A.Lines[0] with { FixedPct = 5 }, .. A.Lines.Skip(1)] },
                     A with { Scenarios = [A.Scenarios[0], A.Scenarios[1] with { Name = A.Scenarios[0].Name.ToLowerInvariant() }] },
                 })
            Assert.Throws<InvalidAssumptionsException>(() => publish.Publish(broken));
        // a valid set does need the store, and its failure is not an assumptions problem
        Assert.Throws<InvalidOperationException>(() => publish.Publish(A));
    }

    [Fact]
    public void A_driver_line_with_a_zero_base_amount_is_a_clear_422()
    {
        var driver = A.Lines.First(l => l.Section == "REV");
        var user = A.Lines.First(l => l.Driver == driver.Code);
        var a = A with { Lines = A.Lines.Select(l => l.Code == driver.Code ? l with { BaseAmount = 0 } : l).ToArray() };
        Assert.Equal($"Line {user.Code}: its driver line {driver.Code} has a zero base amount.", Refusal(a));
        var e = Assert.Throws<InvalidAssumptionsException>(() => new SubmitRun(new InMemoryRunStore(), new SystemClock()).Submit(a, Base));
        Assert.Contains("has a zero base amount", e.Message);
    }
}
