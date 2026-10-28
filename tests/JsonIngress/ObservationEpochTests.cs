using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

internal static class ObservationEpochTests
{
    private static int passed, failed;
    internal static int Run()
    {
        Test("no admission before complete sample", () =>
        {
            var f = new Fixture(false); Error(WireError.NotReady, () => f.Epochs.Acquire(f.Scope));
        });
        Test("stable supplied evidence admits private lease and primitive transfer", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); int transferred = 0;
            f.Release(lease, () => transferred++); Equal(1, transferred);
            f.Frame = new FrameGeometry(2, 1280, 720, 1); f.Release(lease);
        });
        Test("unsafe on off interval cannot resurrect lease", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            f.Safety = DeveloperSafety.Unsafe; f.Observe(); f.Safety = DeveloperSafety.Safe; f.Observe();
            Error(WireError.NotReady, () => f.Release(lease)); f.Release(f.Lease());
        });
        Test("unobserved on off interval invalidates even with identical safe endpoints", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            f.Epochs.BeginBoundary(); f.Epochs.EndBoundary();
            Error(WireError.NotReady, () => f.Release(lease)); f.Release(f.Lease());
        });
        Test("unchanged setting write never revokes a lease, changed or failed write does", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            SettingWrite same = SettingWriteBracket.Begin(f.Epochs, false, false); SettingWriteBracket.End(f.Epochs, ref same, false, false);
            f.Release(lease);
            ObservationLease changed = f.Lease();
            SettingWrite write = SettingWriteBracket.Begin(f.Epochs, true, false); SettingWriteBracket.End(f.Epochs, ref write, true, false);
            Error(WireError.NotReady, () => f.Release(changed));
            ObservationLease failed = f.Lease();
            SettingWrite broken = SettingWriteBracket.Begin(f.Epochs, false, false); SettingWriteBracket.End(f.Epochs, ref broken, false, true);
            Error(WireError.NotReady, () => f.Release(failed)); f.Release(f.Lease());
        });
        Test("unchanged setting write inside an open outer bracket keeps the bracket balanced", () =>
        {
            var f = new Fixture(); f.Epochs.BeginBoundary();
            SettingWrite same = SettingWriteBracket.Begin(f.Epochs, true, true); SettingWriteBracket.End(f.Epochs, ref same, true, false);
            Error(WireError.NotReady, () => f.Lease()); f.Epochs.EndBoundary(); f.Observe(); f.Release(f.Lease());
        });
        Test("ordinary callback brackets preserve public history, never preserve leases", () =>
        {
            var f = new Fixture(); var history = new History(); f.Epochs.Register(history);
            ObservationLease lease = f.Lease();
            f.Epochs.BeginBoundary(true); f.Epochs.EndBoundary(true); Equal(0, history.Calls);
            Error(WireError.NotReady, () => f.Release(lease));
            f.Safety = DeveloperSafety.Unsafe; f.Observe(); True(history.Calls > 0);
            int before = history.Calls; f.Epochs.BeginBoundary(); True(history.Calls > before); f.Epochs.EndBoundary();
        });
        Test("nested callback brackets never admit until outer exit and resample", () =>
        {
            var f = new Fixture(); f.Epochs.BeginBoundary(); f.Epochs.BeginBoundary();
            f.Epochs.EndBoundary(); f.Observe(); Error(WireError.NotReady, () => f.Lease());
            f.Epochs.EndBoundary(); Error(WireError.NotReady, () => f.Lease());
            f.Observe(); f.Release(f.Lease());
        });
        Test("unbalanced boundary permanently refuses", () =>
        {
            var f = new Fixture(); f.Epochs.EndBoundary(); f.Observe();
            Error(WireError.NotReady, () => f.Lease());
        });
        Test("unknown failed incomplete sample revokes old authorization", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            f.Safety = DeveloperSafety.Unknown; f.Observe();
            Error(WireError.ObservationRefused, () => f.Release(lease));
            f.Safety = DeveloperSafety.Safe; f.Observe(); Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("exhausted sampling budget cannot preserve or recreate old lease", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); f.WithinBudget = false;
            Error(WireError.NotReady, () => f.Release(lease));
            f.WithinBudget = true; f.Observe(); Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("missing sample invalidation cannot be repaired by safe cached endpoint", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); f.Epochs.Invalidate();
            Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("loading and resume invalidate even same root game map", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); f.Ready = false; f.Observe();
            Error(WireError.NotReady, () => f.Release(lease)); f.Ready = true; f.Observe();
            Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("source revision invalidates independently of unchanged safety and scope", () =>
        {
            var f = new Fixture(); ObservationLease old = f.Lease();
            f.Epochs.Invalidate(ObservationInvalidation.Source); f.Observe(); ObservationLease next = f.Lease();
            Equal(old.Safety, next.Safety); Equal(old.Readiness, next.Readiness); True(old.Source < next.Source);
            Error(WireError.NotReady, () => f.Release(old));
        });
        Test("scope replacement return never resurrects retained data", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); Scope old = f.Scope;
            f.Scope = new Scope(old.Session, Ids.New("r")); f.Observe();
            Error(WireError.StaleScope, () => f.Release(lease));
            f.Scope = old; f.Observe(); Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("lease cannot cross epoch owner even with identical numbers", () =>
        {
            var f = new Fixture(); var other = new Fixture(false) { Scope = f.Scope }; other.Observe();
            Error(WireError.NotReady, () => other.Release(f.Lease()));
        });
        foreach (var item in new (string Name, FrameGeometry? Geometry)[]
        {
            ("missing", null), ("negative frame", new FrameGeometry(-1, 1280, 720, 1)),
            ("zero width", new FrameGeometry(1, 0, 720, 1)), ("negative height", new FrameGeometry(1, 1280, -1, 1)),
            ("zero scale", new FrameGeometry(1, 1280, 720, 0)), ("NaN scale", new FrameGeometry(1, 1280, 720, float.NaN)),
            ("infinite scale", new FrameGeometry(1, 1280, 720, float.PositiveInfinity))
        }) Test(item.Name + " geometry closes admission and revokes retained frame", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); f.Frame = item.Geometry;
            Error(WireError.NotReady, () => f.Release(lease));
            f.Frame = new FrameGeometry(1, 1280, 720, 1); f.Observe();
            Error(WireError.NotReady, () => f.Release(lease));
        });
        foreach (var item in new (string Name, FrameGeometry Geometry)[]
        {
            ("frame rollback", new FrameGeometry(0, 1280, 720, 1)),
            ("missing intervening frames", new FrameGeometry(3, 1280, 720, 1)),
            ("width", new FrameGeometry(1, 1281, 720, 1)), ("height", new FrameGeometry(1, 1280, 721, 1)),
            ("scale", new FrameGeometry(1, 1280, 720, 1.25f))
        }) Test(item.Name + " discontinuity irreversibly invalidates", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); f.Frame = item.Geometry; f.Observe();
            Error(WireError.NotReady, () => f.Release(lease));
            f.Frame = new FrameGeometry(1, 1280, 720, 1); f.Observe();
            Error(WireError.NotReady, () => f.Release(lease)); f.Release(f.Lease());
        });
        Test("known developer refusal precedes loading stale lease and missing geometry", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            f.Ready = false; f.Safety = DeveloperSafety.Unsafe; f.WithinBudget = false;
            f.Scope = new Scope(Ids.New("s"), Ids.New("r")); f.Frame = null;
            Error(WireError.ObservationRefused, () => f.Release(lease));
        });
        Test("all bounded owner classes revoke before any transfer", () =>
        {
            var f = new Fixture(); var owners = new List<Owner>();
            foreach (string kind in new[] { "active", "snapshot", "event", "capture" })
            { var owner = new Owner(); owners.Add(owner); f.Epochs.Register(owner); }
            ObservationLease lease = f.Lease(); f.Epochs.BeginBoundary();
            foreach (Owner owner in owners) { True(owner.Cancelled); True(owner.Retained == null); Equal(1, owner.Calls); }
            f.Epochs.EndBoundary(); bool transferred = false;
            Error(WireError.NotReady, () => f.Release(lease, () => transferred = true)); True(!transferred);
        });
        Test("throwing owner still invalidates other owners and permanently closes", () =>
        {
            var f = new Fixture(); var broken = new Owner { Throws = true }; var capture = new Owner();
            f.Epochs.Register(broken); f.Epochs.Register(capture); f.Epochs.Invalidate();
            True(capture.Cancelled); f.Observe(); Error(WireError.NotReady, () => f.Lease());
        });
        foreach (string counter in new[] { "safetyEpoch", "readinessEpoch", "sourceEpoch" })
            Test(counter + " exhaustion permanently closes without wrap", () =>
            {
                var f = new Fixture();
                // Synthetic BCL tracker only; no runtime game reflection or product endpoint.
                var field = typeof(ObservationEpochs).GetField(counter,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                field.SetValue(f.Epochs, long.MaxValue); f.Epochs.Invalidate(); f.Observe();
                Equal(long.MaxValue, (long)field.GetValue(f.Epochs)!);
                Error(WireError.NotReady, () => f.Lease());
            });
        Test("owner registration bounded duplicate refusal and unregister", () =>
        {
            var f = new Fixture(); var first = new Owner(); f.Epochs.Register(first);
            Invalid(() => f.Epochs.Register(first));
            for (int i = 1; i < ObservationEpochs.MaxOwners; i++) f.Epochs.Register(new Owner());
            Invalid(() => f.Epochs.Register(new Owner())); f.Epochs.Unregister(first);
            f.Epochs.Register(new Owner()); f.Epochs.Invalidate(); Equal(0, first.Calls);
        });
        Test("registration holes cannot duplicate an owner", () =>
        {
            var f = new Fixture(); var first = new Owner(); var second = new Owner();
            f.Epochs.Register(first); f.Epochs.Register(second); f.Epochs.Unregister(first);
            Invalid(() => f.Epochs.Register(second)); f.Epochs.Invalidate(); Equal(1, second.Calls);
        });
        Test("empty or unknown invalidation mask cannot resurrect a lease", () =>
        {
            foreach (ObservationInvalidation cause in new[] { (ObservationInvalidation)0, (ObservationInvalidation)8 })
            {
                var f = new Fixture(); ObservationLease lease = f.Lease(); f.Epochs.Invalidate(cause);
                Error(WireError.NotReady, () => f.Release(lease));
            }
        });
        Test("lease admission also requires owning sample thread", () =>
        {
            var f = new Fixture(); Task.Run(() => Invalid(() => f.Lease())).GetAwaiter().GetResult();
            f.Release(f.Lease());
        });
        Test("worker edge cancels active work already taken outside pending queue", () =>
        {
            var pending = new PendingReads(); var fence = new TransitionFence(pending);
            var f = new Fixture(false, fence.Observations); f.Observe(); ObservationLease lease = f.Lease();
            var active = new Owner(); f.Epochs.Register(active);
            Task.Run(() => { fence.Begin(); fence.End(); }).GetAwaiter().GetResult();
            True(active.Cancelled); Equal(0, pending.Count); Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("sample release refuses wrong thread before transfer", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease(); bool transferred = false;
            Task.Run(() => Invalid(() => f.Release(lease, () => transferred = true))).GetAwaiter().GetResult();
            True(!transferred); f.Observe(); Error(WireError.NotReady, () => f.Release(lease));
        });
        Test("release transfer and worker revocation serialized", () =>
        {
            var f = new Fixture(); ObservationLease lease = f.Lease();
            using var starting = new ManualResetEventSlim(); using var complete = new ManualResetEventSlim();
            Task? worker = null;
            f.Release(lease, () =>
            {
                worker = Task.Run(() => { starting.Set(); f.Epochs.Invalidate(); complete.Set(); });
                True(starting.Wait(1000)); True(!complete.Wait(20));
            });
            worker!.GetAwaiter().GetResult(); True(complete.IsSet);
            Error(WireError.NotReady, () => f.Release(lease));
        });
        Console.WriteLine($"Observation epochs: {passed} passed; {failed} failed. Supplied host evidence only; no completed-render/Mono/game proof.");
        return failed;
    }
    private sealed class Fixture
    {
        internal readonly ObservationEpochs Epochs;
        internal Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"));
        internal FrameGeometry? Frame = new FrameGeometry(1, 1280, 720, 1);
        internal bool Ready = true, WithinBudget = true;
        internal DeveloperSafety Safety = DeveloperSafety.Safe;
        internal Fixture(bool observe = true, ObservationEpochs? epochs = null)
        { Epochs = epochs ?? new ObservationEpochs(); if (observe) Observe(); }
        internal void Observe() => Epochs.Observe(Ready, Safety, Scope, Frame, WithinBudget);
        internal ObservationLease Lease() => Epochs.Acquire(Scope);
        internal void Release(ObservationLease lease, Action? transfer = null) =>
            Epochs.Release(lease, Ready, Safety, Scope, Frame, WithinBudget, transfer ?? (() => { }));
    }
    private sealed class History : IObservationHistorySink
    {
        internal int Calls;
        public void Invalidate(ObservationInvalidation cause) { Calls++; }
    }
    private sealed class Owner : IObservationInvalidationSink
    {
        internal bool Cancelled, Throws;
        internal object? Retained = new object();
        internal int Calls;
        public void Invalidate(ObservationInvalidation cause)
        { Calls++; Cancelled = true; Retained = null; if (Throws) throw new Exception("private"); }
    }
    private static void Error(WireError expected, Action action)
    {
        try { action(); throw new Exception("accepted revoked lease"); }
        catch (WireException error) { Equal(expected, error.Code); }
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted invalid operation"); }
        catch (InvalidOperationException) { }
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL epochs {name}: {error.Message}"); }
    }
}
