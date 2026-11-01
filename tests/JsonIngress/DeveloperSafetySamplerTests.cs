using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

internal static class DeveloperSafetySamplerTests
{
    private static int passed, failed;
    private class Window { }
    private class DevWindow : Window { }
    private sealed class DerivedDevWindow : DevWindow { }
    private sealed class Source : IDeveloperSafetySource<Window>
    {
        internal bool Dev, Inspect = true;
        internal int Reads = DeveloperSafetySampler<Window>.RequiredStaticReads;
        internal int Mismatch = -1, ThrowAt = -1, Visits, WindowVisits, Accesses;
        internal int? CountOverride;
        internal bool ThrowMode, ThrowCount, ThrowClassify, ChangeCount, DevAfterScan;
        internal Action<int>? OnRead, OnWindow;
        internal readonly List<Window> Windows = new List<Window>();
        public bool DeveloperMode
        {
            get { Accesses++; if (ThrowMode) throw new Exception("private error"); return Dev || (DevAfterScan && Visits == Reads); }
        }
        public bool CanInspect { get { Accesses++; return Inspect; } }
        public int StaticReadCount { get { Accesses++; return Reads; } }
        public bool BaselineMatches(int index)
        {
            Accesses++; Visits++; OnRead?.Invoke(index);
            if (index == ThrowAt) throw new MissingFieldException("private field");
            return index != Mismatch;
        }
        public int WindowCount
        {
            get
            {
                Accesses++;
                if (ThrowCount) throw new Exception("private window state");
                return CountOverride ?? (Windows.Count + (ChangeCount && WindowVisits > 0 ? 1 : 0));
            }
        }
        public Window WindowAt(int index) { Accesses++; WindowVisits++; OnWindow?.Invoke(index); return Windows[index]; }
        public bool KnownDeveloperWindow(Window window)
        { Accesses++; if (ThrowClassify) throw new TypeLoadException("private type"); return window is DevWindow; }
    }
    private static UpdateBudget Budget() => new UpdateBudget(() => 0);
    private static DeveloperSafety Sample(Source source) => new DeveloperSafetySampler<Window>(source).Sample(Budget());

    internal static int Run()
    {
        Test("complete named baseline and ordinary windows are safe instantaneous evidence", () =>
        {
            var source = new Source(); source.Windows.Add(new Window());
            Equal(DeveloperSafety.Safe, Sample(source)); Equal(185, source.Visits); Equal(1, source.WindowVisits);
        });
        Test("DevMode refuses before flags/windows/loading access", () =>
        {
            var source = new Source { Dev = true, Inspect = false };
            Equal(DeveloperSafety.Unsafe, Sample(source)); Equal(1, source.Accesses);
        });
        Test("loading or failed named binding reads no flags/windows", () =>
        {
            var source = new Source { Inspect = false };
            Equal(DeveloperSafety.Unknown, Sample(source)); Equal(0, source.Visits); Equal(0, source.WindowVisits);
        });
        for (int index = 0; index < DeveloperSafetySampler<Window>.RequiredStaticReads; index++)
        {
            int i = index;
            Test("each finite baseline mismatch refuses " + i, () =>
            {
                var source = new Source { Mismatch = i };
                Equal(DeveloperSafety.Unsafe, Sample(source)); Equal(i + 1, source.Visits); Equal(0, source.WindowVisits);
            });
            Test("each missing/unreadable flag fails closed " + i, () =>
            {
                var source = new Source { ThrowAt = i };
                Equal(DeveloperSafety.Unknown, Sample(source)); Equal(i + 1, source.Visits); Equal(0, source.WindowVisits);
            });
        }
        foreach (int count in new[] { -1, 0, 184, 186, 256, 257, int.MaxValue })
            Test("incomplete/changed static manifest fails closed " + count, () =>
            {
                var source = new Source { Reads = count };
                Equal(DeveloperSafety.Unknown, Sample(source)); Equal(0, source.Visits);
            });
        Test("known developer ancestry refuses even with DevMode off", () =>
        {
            var source = new Source(); source.Windows.Add(new DerivedDevWindow());
            Equal(DeveloperSafety.Unsafe, Sample(source));
        });
        Test("finite windows include last permitted entry", () =>
        {
            var source = new Source();
            for (int i = 0; i < 64; i++) source.Windows.Add(new Window());
            Equal(DeveloperSafety.Safe, Sample(source)); Equal(64, source.WindowVisits);
            source.Windows[63] = new DerivedDevWindow();
            Equal(DeveloperSafety.Unsafe, Sample(source));
        });
        foreach (int count in new[] { -1, 65, int.MaxValue })
            Test("window count outside finite budget fails closed " + count, () =>
            {
                var source = new Source { CountOverride = count };
                Equal(DeveloperSafety.Unknown, Sample(source)); Equal(0, source.WindowVisits);
            });
        Test("null window fails closed", () =>
        { var source = new Source(); source.Windows.Add(null!); Equal(DeveloperSafety.Unknown, Sample(source)); });
        Test("failed window read fails closed", () =>
        { var source = new Source { CountOverride = 1 }; Equal(DeveloperSafety.Unknown, Sample(source)); });
        Test("changed window count fails closed", () =>
        {
            var source = new Source { ChangeCount = true }; source.Windows.Add(new Window());
            Equal(DeveloperSafety.Unknown, Sample(source));
        });
        Test("failed preferences read fails closed", () => Equal(DeveloperSafety.Unknown, Sample(new Source { ThrowMode = true })));
        Test("failed stack count fails closed", () => Equal(DeveloperSafety.Unknown, Sample(new Source { ThrowCount = true })));
        Test("off-thread sampling cannot touch source", () =>
        {
            var source = new Source(); var sampler = new DeveloperSafetySampler<Window>(source);
            Equal(DeveloperSafety.Unknown, Task.Run(() => sampler.Sample(Budget())).GetAwaiter().GetResult());
            Equal(0, source.Accesses);
        });
        Test("expired shared update budget touches nothing", () =>
        {
            var source = new Source(); var sampler = new DeveloperSafetySampler<Window>(source);
            long clock = 0; var budget = new UpdateBudget(() => clock); clock = Stopwatch.Frequency;
            Equal(DeveloperSafety.Unknown, sampler.Sample(budget)); Equal(0, source.Accesses);
        });
        for (int index = 0; index < DeveloperSafetySampler<Window>.RequiredStaticReads; index++)
        {
            int i = index;
            Test("deadline exhausted at static read cannot authorize " + i, () =>
            {
                long clock = 0;
                var source = new Source { OnRead = n => { if (n == i) clock = Stopwatch.Frequency; } };
                var budget = new UpdateBudget(() => clock);
                Equal(DeveloperSafety.Unknown, new DeveloperSafetySampler<Window>(source).Sample(budget));
                Equal(i + 1, source.Visits); Equal(0, source.WindowVisits);
            });
        }
        for (int index = 0; index < DeveloperSafetySampler<Window>.MaxWindows; index++)
        {
            int i = index;
            Test("deadline exhausted at window read cannot authorize " + i, () =>
            {
                long clock = 0;
                var source = new Source { OnWindow = n => { if (n == i) clock = Stopwatch.Frequency; } };
                for (int j = 0; j < 64; j++) source.Windows.Add(new Window());
                var budget = new UpdateBudget(() => clock);
                Equal(DeveloperSafety.Unknown, new DeveloperSafetySampler<Window>(source).Sample(budget));
                Equal(i + 1, source.WindowVisits);
            });
        }
        Test("failed named type classification fails closed", () =>
        {
            var source = new Source { ThrowClassify = true }; source.Windows.Add(new Window());
            Equal(DeveloperSafety.Unknown, Sample(source));
        });
        Test("readiness lost during scan fails closed", () =>
        {
            var source = new Source(); source.Windows.Add(new Window());
            source.OnWindow = _ => source.Inspect = false;
            Equal(DeveloperSafety.Unknown, Sample(source));
        });
        Test("final DevMode recheck refuses", () =>
            Equal(DeveloperSafety.Unsafe, Sample(new Source { DevAfterScan = true })));
        Test("safe result never cached after later failed or unsafe sample", () =>
        {
            var source = new Source(); var sampler = new DeveloperSafetySampler<Window>(source);
            Equal(DeveloperSafety.Safe, sampler.Sample(Budget()));
            source.Inspect = false; Equal(DeveloperSafety.Unknown, sampler.Sample(Budget()));
            source.Inspect = true; source.Mismatch = 179; Equal(DeveloperSafety.Unsafe, sampler.Sample(Budget()));
            source.Mismatch = -1; Equal(DeveloperSafety.Safe, sampler.Sample(Budget()));
            // No claim that final Safe certifies intervening interval; epochs are next task.
        });
        Test("dispatcher shares update deadline and clears prior safety evidence", () =>
        {
            var pending = new PendingReads(); var fence = new TransitionFence(pending);
            long clock = 0;
            var dispatcher = new ReadDispatcher<object>(Ids.New("s"), pending, fence, () => clock);
            object root = new object();
            dispatcher.Pump(_ => new ReadFrame<object>(root, null, null, true, DeveloperSafety.Safe));
            Equal(DeveloperSafety.Safe, dispatcher.Safety);
            dispatcher.Pump(_ => { clock = Stopwatch.Frequency; return new ReadFrame<object>(root, null, null, true, DeveloperSafety.Safe); });
            Equal(DeveloperSafety.Unknown, dispatcher.Safety); True(!dispatcher.Ready);
            dispatcher.Pump(_ => throw new Exception("private error"));
            Equal(DeveloperSafety.Unknown, dispatcher.Safety);
        });
        Console.WriteLine($"Developer safety sampler: {passed} passed; {failed} failed. Fake sources only; no Mono/game/epoch proof.");
        return failed;
    }
    private static void Test(string name, Action action)
    { try { action(); passed++; } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL sampler " + name + ": " + error); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
