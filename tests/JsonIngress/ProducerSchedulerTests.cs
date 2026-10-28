using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

// Synthetic producers only: proves shared scheduling/ownership, not any real game read.
internal static class ProducerSchedulerTests
{
    private static int passed, failed;
    internal static int Run()
    {
        Test("single-update producer released through final admission", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue("summary"); f.Pump();
            Equal(1, (int)Data(work)["seen"]!); Equal(1, f.Last.Discards); Equal(0, f.Dispatch.ActiveCount);
        });
        Test("continuation resumes across updates within session-wide candidate slice", () =>
        {
            using var f = new Fixture(); f.Need = 300; PendingRead work = f.Enqueue("summary");
            f.Pump(); Equal(WireLimits.CandidatesPerUpdate, f.Last.Seen); True(!work.Completion.IsCompleted);
            f.Pump(); Equal(2 * WireLimits.CandidatesPerUpdate, f.Last.Seen);
            f.Pump(); Equal(300, (int)Data(work)["seen"]!); Equal(1, f.Last.Discards);
        });
        Test("concurrent producers share one update allowance fairly", () =>
        {
            using var f = new Fixture(); f.Need = 200;
            PendingRead a = f.Enqueue("summary"), b = f.Enqueue("ui"), c = f.Enqueue("screenshot"); f.Pump();
            int total = 0; foreach (Fake fake in f.Started) { True(fake.Seen >= 42 && fake.Seen <= 43); total += fake.Seen; }
            Equal(WireLimits.CandidatesPerUpdate, total);
            for (int i = 0; i < 5; i++) f.Pump();
            foreach (PendingRead read in new[] { a, b, c }) Equal(200, (int)Data(read)["seen"]!);
        });
        Test("rotation gives every active work the larger first share", () =>
        {
            using var f = new Fixture(); f.Need = 100000;
            f.Enqueue("summary"); f.Enqueue("ui"); f.Enqueue("screenshot");
            for (int i = 0; i < 3; i++) f.Pump();
            foreach (Fake fake in f.Started) Equal(WireLimits.CandidatesPerUpdate, fake.Seen);
        });
        Test("whole-read candidate cap fails uniformly and discards partial state", () =>
        {
            using var f = new Fixture(); f.Need = WireLimits.ScanCandidates + 1; PendingRead work = f.Enqueue("summary");
            for (int i = 0; i <= WireLimits.ScanCandidates / WireLimits.CandidatesPerUpdate; i++) f.Pump();
            Equal(WireError.Unavailable, Error(work)); Equal(WireLimits.ScanCandidates, f.Last.Seen); Equal(1, f.Last.Discards);
        });
        Test("nested fields bounded per update and per read", () =>
        {
            using var f = new Fixture(); f.Need = 4; f.Nested = 600; PendingRead work = f.Enqueue("summary");
            f.Pump(); Equal(1, f.Last.Seen); f.Pump(); Equal(2, f.Last.Seen);
            f.Pump(); f.Pump(); Equal(4, (int)Data(work)["seen"]!);
            f.Nested = WireLimits.NestedFieldsPerUpdate + 1; work = f.Enqueue("summary"); f.Pump();
            Equal(WireError.Unavailable, Error(work));
        });
        Test("retained bytes capped per read and session-wide", () =>
        {
            using var f = new Fixture(); f.Retain = WireLimits.ResponseBytes + 1; PendingRead work = f.Enqueue("summary"); f.Pump();
            Equal(WireError.Unavailable, Error(work)); Equal(0, f.Dispatch.ActiveCount);
            f.Retain = WireLimits.ActiveWorkBytes; f.Need = 100000; PendingRead build = f.Enqueue("events.read"); f.Pump();
            f.Retain = 1; f.Need = 1; work = f.Enqueue("summary"); f.Pump(); Equal(WireError.Busy, Error(work));
            f.Started[1].Throw = true; f.Pump(); Equal(WireError.Unavailable, Error(build));
            work = f.Enqueue("summary"); f.Pump(); Equal(1, (int)Data(work)["seen"]!);
        });
        Test("one snapshot build and finite active ownership", () =>
        {
            using var f = new Fixture(); f.Need = 100000;
            PendingRead build = f.Enqueue("events.read"), second = f.Enqueue("events.read"); f.Pump();
            Equal(WireError.Busy, Error(second)); True(!build.Completion.IsCompleted);
            var reads = new List<PendingRead>();
            foreach (string method in new[] { "summary", "ui", "screenshot", "summary" }) reads.Add(f.Enqueue(method));
            f.Pump(); Equal(WireLimits.ActiveReads, f.Dispatch.ActiveCount); Equal(WireError.Busy, Error(reads[3]));
        });
        Test("exhausted update time yields without starting or losing work", () =>
        {
            using var f = new Fixture(); f.Need = 2; PendingRead work = f.Enqueue("summary");
            f.OnStep = _ => { f.Clock += Stopwatch.Frequency; f.OnStep = null; }; f.Pump();
            Equal(0, f.Last.Seen); True(!work.Completion.IsCompleted); f.Pump(); Equal(2, (int)Data(work)["seen"]!);
        });
        Test("completed work whose release finds no time is released next update without re-stepping", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue("summary");
            f.AfterStep = () => { f.Clock += Stopwatch.Frequency; f.AfterStep = null; }; f.Pump();
            True(!work.Completion.IsCompleted); f.Pump(); Equal(1, (int)Data(work)["seen"]!); Equal(1, f.Last.Steps);
        });
        Test("release audit that overshoots the allowance defers instead of rejecting", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue("summary");
            f.OnEligible = () => { f.Clock += Stopwatch.Frequency; f.OnEligible = null; }; f.Pump();
            True(!work.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount); Equal(0, f.Last.Discards);
            f.Pump(); Equal(1, (int)Data(work)["seen"]!); Equal(1, f.Last.Steps); Equal(1, f.Last.Discards);
        });
        Test("selection-row retention that yields retries final admission without marking unavailable", () =>
        {
            using var f = new Fixture();
            f.OnAdmission = work => {
                f.Clock += Stopwatch.Frequency; f.OnAdmission = null;
                // Same retention result as GameUi's releasing selection-row append.
                bool retained = work.TryRetain(32);
                True(!retained && work.Yielded && !work.Failure.HasValue);
                return retained;
            };
            PendingRead read = f.Enqueue("ui"); f.Pump();
            True(!read.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount); Equal(0, f.Last.Discards);
            f.Pump(); Equal(1, (int)Data(read)["seen"]!); Equal(1, f.Last.Discards);
        });
        Test("expired completed candidate restarts Step without releasing old result or resetting charges", () =>
        {
            using var f = new Fixture(); f.Need = 2; f.Retain = 32;
            ProducerWork<object>? active = null;
            f.OnAdmission = work => {
                active = work; Equal(2, work.Candidates); Equal(32, work.Bytes);
                f.Last.ResetCandidate(); work.ReleaseMemory(); f.OnAdmission = null;
                return work.RestartStep();
            };
            PendingRead read = f.Enqueue("summary"); f.Pump();
            True(!read.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount);
            Equal(0, f.Last.Discards); Equal(0, active!.Bytes);
            f.Pump(); Equal(2, (int)Data(read)["seen"]!); Equal(2, f.Last.Steps);
            Equal(4, active.Candidates); Equal(1, f.Last.Discards);
        });
        Test("restarted completed candidate remains subject to unsafe cancellation", () =>
        {
            using var f = new Fixture();
            f.OnAdmission = work => work.RestartStep();
            PendingRead read = f.Enqueue("summary"); f.Pump(); True(!read.Completion.IsCompleted);
            f.Safety = DeveloperSafety.Unsafe; f.Pump();
            Equal(WireError.ObservationRefused, Error(read)); Equal(1, f.Last.Discards);
        });
        Test("producer exhaustion cannot skip independent boundary sample or expire a valid paged snapshot", () =>
        {
            using var f = new Fixture();
            SnapshotStore store = f.Dispatch.Generations.Snapshots;
            object update = new object();
            EligibleSnapshot snapshot = store.Create(f.MapScope, ReadMethod.Pawns, 1,
                new[] { new JObject(), new JObject() }, new[] { new object(), new object() }, 4, new StableSource(), 0);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new NestedBudget()));
            True(store.Retain(pass, update));
            string cursor = (string)store.Page(pass, update, 0, new SampleStamp(f.MapScope, 1, 0))["next"]!;
            f.Need = 100; f.OnCandidate = () => f.Clock += Stopwatch.Frequency / 4000; // 0.25 ms per bounded unit
            PendingRead read = f.Enqueue("summary"); f.Pump(); True(!read.Completion.IsCompleted);
            bool sampled = false;
            f.Dispatch.SampleBoundary(_ => { sampled = true; f.Clock += Stopwatch.Frequency * 3 / 10000; return f.Frame(); });
            True(sampled); True(f.Dispatch.Ready);
            True(store.TryResolve(f.MapScope, ReadMethod.Pawns, 1, cursor, out _, out int start)); Equal(1, start);
            // Producer debit has no effect on this sample. An actual sample overrun expires it.
            f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            f.Dispatch.SampleBoundary(_ => f.Frame());
            True(store.TryResolve(f.MapScope, ReadMethod.Pawns, 1, cursor, out _, out _));
            f.Dispatch.SampleBoundary(_ => { f.Clock += Stopwatch.Frequency; return f.Frame(); });
            True(!store.TryResolve(f.MapScope, ReadMethod.Pawns, 1, cursor, out _, out _)); Equal(0, store.Count);
        });
        Test("GUI boundary samples after producer overshoot without granting another producer slice", () =>
        {
            using var f = new Fixture(); f.Need = 2; PendingRead work = f.Enqueue("summary"); Scope scope = f.Scope;
            f.OnStep = _ => { f.Clock += Stopwatch.Frequency; f.OnStep = null; }; f.Pump(); // yields, update allowance spent
            True(!work.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount);
            int steps = f.Last.Steps;
            f.Dispatch.SampleBoundary(_ => f.Frame());
            True(f.Dispatch.Ready); Equal(steps, f.Last.Steps); True(!work.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount);
            Equal(0, f.Last.Discards); True(scope.Same(f.Scope));
            f.Pump(); Equal(2, (int)Data(work)["seen"]!); Equal(1, f.Last.Discards);
        });
        Test("boundary sample that overruns its own allowance keeps identity but revokes active work", () =>
        {
            using var f = new Fixture(); f.Need = 2; PendingRead work = f.Enqueue("summary"); Scope scope = f.Scope;
            f.OnStep = _ => { f.Clock += Stopwatch.Frequency; f.OnStep = null; }; f.Pump();
            f.Dispatch.BeginUpdateAllowance();
            f.Dispatch.SampleBoundary(_ => { f.Clock += Stopwatch.Frequency; return f.Frame(); });
            True(!f.Dispatch.Ready); True(scope.Same(f.Scope)); Equal(WireError.NotReady, Error(work)); Equal(0, f.Dispatch.ActiveCount);
            Equal(1, f.Last.Discards); f.Pump(); True(scope.Same(f.Scope));
        });
        Test("sample that exhausts the allowance while reporting not-ready keeps identity; a real not-ready rotates", () =>
        {
            using var f = new Fixture(); f.Need = 2; PendingRead work = f.Enqueue("summary"); Scope scope = f.Scope;
            f.Pump(); f.Dispatch.BeginUpdateAllowance();
            f.Dispatch.SampleBoundary(_ => { f.Clock += Stopwatch.Frequency; return new ReadFrame<object>(null, null, null, false, f.Safety); });
            True(!f.Dispatch.Ready); True(scope.Same(f.Scope));
            f.Pump(); True(f.Dispatch.Ready); True(scope.Same(f.Scope));
            f.Dispatch.BeginUpdateAllowance();
            f.Dispatch.SampleBoundary(_ => new ReadFrame<object>(null, null, null, false, f.Safety)); // genuine not-ready, budget intact
            True(f.Dispatch.Generations.Scope == null);
        });
        Test("allowance exhaustion never keeps work across a real scope or safety change", () =>
        {
            using var f = new Fixture(); f.Need = 100000; PendingRead work = f.Enqueue("summary"); f.Pump();
            f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            f.Root = new object(); f.Dispatch.SampleBoundary(_ => f.Frame());
            Equal(WireError.StaleScope, Error(work)); Equal(0, f.Dispatch.ActiveCount);
        });
        Test("event preparation that yields keeps the read and retries next update", () =>
        {
            using var f = new Fixture(true); f.Dispatch.ExtraNestedFields = () => WireLimits.NestedFieldsPerUpdate;
            PendingRead page = f.Enqueue("events.read"); f.Pump();
            True(!page.Completion.IsCompleted); Equal(1, f.Dispatch.ActiveCount);
            f.Dispatch.ExtraNestedFields = () => 0; f.Pump(); Equal(1, ((JArray)Data(page)["items"]!).Count);
        });
        Test("deadline expiry after dequeue discards without completing", () =>
        {
            using var f = new Fixture(); f.Need = 100000; f.Lifetime.Begin(30); PendingRead work = f.Enqueue("summary");
            f.Pump(); True(!work.Completion.IsCompleted); Thread.Sleep(50); f.Pump();
            True(work.Completion.IsCanceled); Equal(1, f.Last.Discards); Equal(0, f.Dispatch.ActiveCount);
        });
        Test("disconnect after dequeue cancels active work and frees memory", () =>
        {
            using var f = new Fixture(); f.Need = 100000; f.Retain = 1024; PendingRead work = f.Enqueue("summary"); f.Pump();
            f.Lifetime.Close(); f.Pump(); True(work.Completion.IsCanceled); Equal(1, f.Last.Discards);
            Equal(0, f.Dispatch.ActiveCount); Equal(0, f.Fence.Active.HeldBytes);
        });
        Test("worker transition rejects active work immediately; game thread discards", () =>
        {
            using var f = new Fixture(); f.Need = 100000; PendingRead work = f.Enqueue("summary"); f.Pump();
            Task.Run(() => { f.Fence.Begin(); f.Fence.End(); }).GetAwaiter().GetResult();
            Equal(WireError.NotReady, Error(work)); Equal(0, f.Last.Discards);
            int steps = f.Last.Steps; f.Pump(); Equal(1, f.Last.Discards); Equal(steps, f.Last.Steps);
        });
        Test("developer refusal precedes stale scope for active work", () =>
        {
            using var f = new Fixture(); f.Need = 100000; PendingRead work = f.Enqueue("summary"); f.Pump();
            f.Safety = DeveloperSafety.Unsafe; f.Root = new object(); f.Pump(); Equal(WireError.ObservationRefused, Error(work));
        });
        Test("scope rotation stales active work; never re-targeted", () =>
        {
            using var f = new Fixture(); f.Need = 100000; PendingRead work = f.Enqueue("summary"); f.Pump();
            f.Root = new object(); f.Pump(); Equal(WireError.StaleScope, Error(work)); Equal(1, f.Last.Discards);
        });
        Test("GUI boundary revalidates but grants no producer step", () =>
        {
            using var f = new Fixture(); f.Need = 100000; PendingRead work = f.Enqueue("summary"); f.Pump();
            int steps = f.Last.Steps; f.Dispatch.SampleBoundary(_ => f.Frame()); Equal(steps, f.Last.Steps);
            f.Safety = DeveloperSafety.Unknown; f.Dispatch.SampleBoundary(_ => f.Frame());
            Equal(WireError.ObservationRefused, Error(work)); Equal(steps, f.Last.Steps);
        });
        Test("final eligibility failure publishes nothing", () =>
        {
            using var f = new Fixture(); f.Eligible = false; PendingRead work = f.Enqueue("summary"); f.Pump();
            Equal(WireError.Unavailable, Error(work)); Equal(1, f.Last.Discards);
        });
        Test("reentrant transition during step never releases", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue("summary");
            f.OnStep = _ => { f.Fence.Begin(); f.Fence.End(); }; f.Pump();
            Equal(WireError.NotReady, Error(work)); Equal(1, f.Last.Discards);
        });
        Test("producer exception is uniform and isolated", () =>
        {
            using var f = new Fixture(); f.ThrowNext = true; PendingRead work = f.Enqueue("summary"); f.Pump();
            Equal(WireError.Unavailable, Error(work)); True(f.Dispatch.Ready);
            PendingRead health = f.Enqueue("health"); f.Pump(); Equal("ready", (string)Data(health)["state"]!);
        });
        Test("bootstrap routes producer-free, duplicate routes refused, unrouted methods unavailable", () =>
        {
            using var f = new Fixture();
            foreach (ReadMethod method in new[] { ReadMethod.Auth, ReadMethod.Health, ReadMethod.Capabilities })
                Invalid(() => new ReadDispatcher<object>(f.Session, f.Pending, f.Fence, null, (method, f.Plain)));
            Invalid(() => new ReadDispatcher<object>(f.Session, f.Pending, f.Fence, null,
                (ReadMethod.Summary, f.Plain), (ReadMethod.Summary, f.Plain)));
            f.Pump(); PendingRead read = f.Enqueue("things.list", f.MapScope); f.Pump(); Equal(WireError.Unavailable, Error(read));
        });
        Test("only routes with a registered producer are advertised available", () =>
        {
            using var f = new Fixture(); PendingRead read = f.Enqueue("capabilities"); f.Pump();
            var available = new List<string>();
            foreach (JToken item in (JArray)Data(read)["capabilities"]!)
                if ((string?)item["state"] == "available") available.Add((string)item["name"]!);
            Equal("auth,health,capabilities,summary,ui,screenshot,events.read,screenshot.map_view", string.Join(",", available));
        });
        Test("real event producer uses scheduler, immutable cursors and final admission", () =>
        {
            using var f = new Fixture(true);
            PendingRead first = f.Enqueue("events.read"); f.Pump();
            string cursor = (string)Data(first)["next"]!;
            f.Events.Publish(PublicationDisposition.AcceptedPublic, true, true, true, "message", null, "Public");
            PendingRead a = f.Enqueue("events.read", cursor: cursor), b = f.Enqueue("events.read", cursor: cursor); f.Pump();
            Equal(Data(a).ToString(), Data(b).ToString()); Equal("Public", (string?)Data(a)["items"]![0]!["text"]);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Events, Data(a)), ReadMethod.Events, "read");
            f.Safety = DeveloperSafety.Unsafe;
            PendingRead refused = f.Enqueue("events.read", cursor: cursor); int calls = f.EventPrepares; f.Pump();
            Equal(WireError.ObservationRefused, Error(refused)); Equal(calls, f.EventPrepares);
        });
        Test("hook candidates and nested fields share remaining producer allowance", () =>
        {
            using var f = new Fixture(); f.Need = 100; f.Dispatch.ExtraCandidates = () => 64;
            PendingRead read = f.Enqueue("summary"); f.Pump(); Equal(64, f.Last.Seen); True(!read.Completion.IsCompleted);
            f.Dispatch.ExtraCandidates = () => 0; f.Pump(); Equal(100, (int)Data(read)["seen"]!);
            using var events = new Fixture(true); events.Dispatch.ExtraNestedFields = () => WireLimits.NestedFieldsPerUpdate;
            PendingRead page = events.Enqueue("events.read"); events.Pump(); True(!page.Completion.IsCompleted);
            events.Dispatch.ExtraNestedFields = () => 0; events.Pump(); Equal(1, ((JArray)Data(page)["items"]!).Count);
        });
        Test("hook elapsed cost is subtracted from same producer time allowance", () =>
        {
            using var f = new Fixture(true);
            f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            PendingRead read = f.Enqueue("events.read"); f.Pump(); True(!read.Completion.IsCompleted);
            Equal(0, f.EventPrepares); True(f.Dispatch.Ready);
            f.Dispatch.ExtraWorkTicks = () => 0; f.Pump();
            Equal(1, ((JArray)Data(read)["items"]!).Count);
        });
        Console.WriteLine($"Producer scheduler: {passed} passed; {failed} failed. Synthetic producers on host .NET only; no Mono/game proof.");
        return failed;
    }

    private sealed class StableSource : ISnapshotSource
    {
        public bool TryStructure(out long version) { version = 0; return true; }
        public bool TryEligibility(out long revision) { revision = 0; return true; }
        public TargetGateResult Revalidate(object member, INestedWork work) => TargetGateResult.Eligible;
    }
    private sealed class NestedBudget : INestedWork { public bool TryNestedFields(int count) => true; }
    private sealed class Producer : IReadProducer<object>
    {
        private readonly Fixture owner;
        public bool SnapshotBuild { get; }
        internal Producer(Fixture owner, bool build) { this.owner = owner; SnapshotBuild = build; }
        public IReadContinuation<object> Start(Request request)
        {
            var fake = new Fake(owner) { Need = owner.Need, Nested = owner.Nested, Retain = owner.Retain, Throw = owner.ThrowNext };
            owner.ThrowNext = false; owner.Started.Add(fake); return fake;
        }
    }
    private sealed class Fake : IReadContinuation<object>
    {
        private readonly Fixture owner;
        private bool retained;
        internal int Need, Nested, Retain, Seen, Steps, Discards;
        internal bool Throw;
        internal Fake(Fixture owner) { this.owner = owner; }
        internal void ResetCandidate() { Seen = 0; retained = false; }
        public StepResult Step(ProducerWork<object> work)
        {
            Steps++; owner.OnStep?.Invoke(work);
            if (Throw) throw new Exception("private");
            if (!retained && Retain > 0) { if (!work.TryRetain(Retain)) return StepResult.Continue; retained = true; }
            while (Seen < Need)
            {
                if (!work.TryCandidate()) return StepResult.Continue;
                if (Nested > 0 && !work.TryNestedFields(Nested)) return StepResult.Continue;
                Seen++; owner.OnCandidate?.Invoke();
            }
            if (Seen < Need) return StepResult.Continue;
            owner.AfterStep?.Invoke(); return StepResult.Complete;
        }
        public bool Eligible(ProducerWork<object> work) { owner.OnEligible?.Invoke(); return owner.OnAdmission?.Invoke(work) ?? owner.Eligible; }
        public JObject Result() => new JObject { ["seen"] = Seen };
        public void Discard() => Discards++;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Session = Ids.New("s");
        internal object? Root = new object(), Game = new object();
        internal readonly List<object> Maps = new List<object> { new object() };
        internal DeveloperSafety Safety = DeveloperSafety.Safe;
        internal readonly PendingReads Pending = new PendingReads();
        internal readonly TransitionFence Fence;
        internal readonly ReadDispatcher<object> Dispatch;
        internal readonly SocketLifetime Lifetime;
        internal readonly List<Fake> Started = new List<Fake>();
        internal readonly Producer Plain;
        internal readonly PublicEventRing Events = new PublicEventRing(() => 0);
        internal int EventPrepares;
        internal int Need = 1, Nested, Retain;
        internal bool Eligible = true, ThrowNext;
        internal long Clock;
        internal Action<ProducerWork<object>>? OnStep;
        internal Action? AfterStep, OnEligible, OnCandidate;
        internal Func<ProducerWork<object>, bool>? OnAdmission;
        private readonly Socket peer;
        internal Fake Last => Started[Started.Count - 1];
        internal Scope Scope => Dispatch.Generations.Scope ?? throw new Exception("missing scope");
        internal Scope MapScope => Dispatch.Generations.MapScope(Maps[0]) ?? throw new Exception("missing map");
        internal Fixture(bool events = false)
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); peer.Connect(listener.LocalEndPoint!);
            Lifetime = new SocketLifetime(listener.Accept(), 10000);
            Fence = new TransitionFence(Pending); Plain = new Producer(this, false);
            Dispatch = new ReadDispatcher<object>(Session, Pending, Fence, () => Clock,
                (ReadMethod.Summary, Plain), (ReadMethod.Ui, Plain), (ReadMethod.Screenshot, Plain),
                (ReadMethod.Events, events ? new EventProducer<object>(Events, w => {
                    // Mirrors the game adapter: a yielded charge is a retry, never a failure.
                    if (!w.TryNestedFields(280)) return false;
                    EventPrepares++; Events.Ready(w.Scope); return true;
                }) : new Producer(this, true)));
            Pump();
        }
        internal ReadFrame<object> Frame() => new ReadFrame<object>(Root, Game, Maps, true, Safety, new FrameGeometry(1, 1280, 720, 1), _ => true);
        internal void Pump() => Dispatch.Pump(Frame);
        internal PendingRead Enqueue(string method, Scope? scope = null, string? cursor = null)
        {
            var args = new JObject();
            if (method != "health" && method != "capabilities") args["scope"] = (scope ?? Scope).Json();
            if (method.EndsWith(".list", StringComparison.Ordinal) || method == "events.read") args["limit"] = 1;
            if (cursor != null) args["cursor"] = cursor;
            var json = new JObject { ["v"] = 1, ["id"] = "read", ["method"] = method, ["params"] = args };
            var read = new PendingRead(Request.Read(WireJson.Encode(json)), Lifetime);
            True(Pending.TryAdd(read)); return read;
        }
        public void Dispose() { Pending.Stop(); Lifetime.Dispose(); peer.Dispose(); }
    }
    private static JObject Data(PendingRead work) => work.Completion.GetAwaiter().GetResult().Data ?? throw new Exception("missing data");
    private static WireError Error(PendingRead work)
    {
        if (!work.Completion.IsCompleted) throw new Exception("work still pending");
        return work.Completion.GetAwaiter().GetResult().Error ?? throw new Exception("missing error");
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted invalid route"); }
        catch (ArgumentException) { }
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL scheduler {name}: {error.Message}"); }
    }
}
