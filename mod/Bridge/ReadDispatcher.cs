using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

namespace RimPilot.Mod.Bridge
{
    // Only audited transition hooks use this gate. Never held for a loading operation: active
    // brackets make readers refuse instead. Worker hooks touch no Unity/Verse state.
    internal struct GameAssignmentState
    {
        internal bool Observed, Entered;
    }

    internal sealed class TransitionFence
    {
        private readonly object gate = new object();
        private readonly PendingReads pending;
        private long revision;
        private int active;
        internal TransitionFence(PendingReads pending) { this.pending = pending; }
        internal long Revision => Interlocked.Read(ref revision);
        internal ObservationEpochs Observations { get; } = new ObservationEpochs();
        internal ActiveReads Active { get; } = new ActiveReads();
        internal void Begin()
        {
            lock (gate)
            {
                active++;
                Observations.BeginBoundary();
                Interlocked.Increment(ref revision);
                pending.Invalidate(WireError.NotReady);
                Active.Invalidate(WireError.NotReady);
            }
        }
        internal void BeginGameAssignment(object? current, object? next, out GameAssignmentState state)
        {
            // A menu's repeated null assignment cannot replace or resurrect a Game.
            // Keep every assignment involving a Game, including same-reference writes.
            state = new GameAssignmentState { Observed = true, Entered = current != null || next != null };
            if (state.Entered) Begin();
        }
        internal void FinishGameAssignment(ref GameAssignmentState state)
        {
            // Harmony may skip a prefix with reference arguments after another prefix
            // skips the setter. Without before/after evidence, invalidate conservatively.
            if (!state.Observed) { state.Observed = true; state.Entered = true; Begin(); }
            Finish(ref state.Entered);
        }
        internal void End()
        {
            lock (gate)
            {
                active--;
                Observations.EndBoundary();
                Interlocked.Increment(ref revision);
                pending.Invalidate(WireError.NotReady);
                Active.Invalidate(WireError.NotReady);
            }
        }
        internal void Finish(ref bool entered)
        {
            if (!entered) return;
            // Installed Harmony can retry finalizers after a later finalizer throws.
            entered = false;
            End();
        }
        internal bool TryRead()
        {
            if (!Monitor.TryEnter(gate)) return false;
            if (active == 0) return true;
            Monitor.Exit(gate); return false;
        }
        internal void ReleaseRead() => Monitor.Exit(gate);
    }

    internal sealed class UpdateBudget
    {
        private readonly Func<long> now;
        private readonly long end;
        // Named safety/readiness boundary reads have an independent finite allowance.
        // Producer exhaustion must never suppress a boundary sample.
        internal const int BoundarySampleMs = 2;
        internal UpdateBudget(Func<long> now, long spentTicks = 0, int milliseconds = WireLimits.WorkPerUpdateMs)
        {
            this.now = now;
            end = now() + Math.Max(0, (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000) - Math.Max(0, spentTicks));
        }
        internal static UpdateBudget Boundary(Func<long> now) => new UpdateBudget(now, 0, BoundarySampleMs);
        internal bool Available => now() < end;
    }

    // Supplied only by trusted main-thread adapter, never transport/client data. No map getter
    // may run until adapter has checked AnyEventNowOrWaiting, actual root and program state.
    internal sealed class ReadFrame<TMap> where TMap : class
    {
        internal readonly object? Root, Game;
        internal readonly List<TMap>? Maps;
        internal readonly bool Ready;
        internal readonly DeveloperSafety Safety;
        internal readonly FrameGeometry? Geometry;
        // Audited normal-player map access gate (loaded/initialized fog/not generating). Null
        // means no map is inspectable: map IDs are never allocated or resolved without it.
        internal readonly Func<TMap, bool>? MapAccess;
        internal ReadFrame(object? root, object? game, List<TMap>? maps, bool ready,
            DeveloperSafety safety = DeveloperSafety.Unknown, FrameGeometry? geometry = null, Func<TMap, bool>? mapAccess = null)
        { Root = root; Game = game; Maps = maps; Ready = ready; Safety = safety; Geometry = geometry; MapAccess = mapAccess; }
    }

    // Public root/game mismatch is stale; a map target that is unknown, removed, hidden or not
    // inspectable is indistinguishable from any other unavailable target.
    internal enum ScopeMatch { Match, Stale, Unavailable }

    internal sealed class GenerationTracker<TMap> where TMap : class
    {
        private readonly string session;
        private readonly TMap?[] maps = new TMap?[WireLimits.TrackedMaps];
        private readonly string?[] mapIds = new string?[WireLimits.TrackedMaps];
        private object? root, game;
        private List<TMap>? source;
        private Func<TMap, bool>? access;
        private List<TMap>.Enumerator version;
        private long revision = -1;
        private int count;
        private string? rootId, gameId;
        internal Scope? Scope => rootId == null ? null : new Scope(session, rootId, gameId);
        internal int RetainedMaps => count;
        internal bool SameSources(object currentRoot, object? currentGame, long edge) => revision == edge
            && ReferenceEquals(root, currentRoot) && ReferenceEquals(game, currentGame);
        // Eligible-only pawn/thing refs; any generation rotation drops all of them.
        internal ReferenceTable Entities { get; } = new ReferenceTable();
        // Retained eligible snapshots/cursors; any generation rotation expires all of them.
        internal SnapshotStore Snapshots { get; }
        internal GenerationTracker(string session, Func<long>? now = null)
        { this.session = session; Snapshots = new SnapshotStore(now); }
        internal void Clear()
        {
            root = null; game = null; source = null; access = null; version = default;
            rootId = null; gameId = null; count = 0;
            Array.Clear(maps, 0, maps.Length); Array.Clear(mapIds, 0, mapIds.Length);
            Entities.Clear();
            Snapshots.Expire();
        }
        internal bool Sample(ReadFrame<TMap> frame, long edge, UpdateBudget budget)
        {
            if (!frame.Ready || frame.Root == null) { Clear(); revision = edge; return false; }
            bool changed = revision != edge || !ReferenceEquals(root, frame.Root) || !ReferenceEquals(game, frame.Game)
                || !ReferenceEquals(source, frame.Maps);
            if (!changed && source != null)
            {
                // Installed Mono List<T>.Enumerator checks version even when empty/exhausted.
                // Detects raw Maps.Remove/Add/Clear/indexer changes with identical final refs.
                // Never call MoveNext while loading. Refresh witness after every safe sample.
                try { version.MoveNext(); }
                catch (InvalidOperationException) { changed = true; }
            }
            int size = frame.Maps?.Count ?? 0;
            if (size > maps.Length || (frame.Game == null && frame.Maps != null)) { Clear(); return false; }
            if (changed) Clear();
            for (int i = 0; i < size; i++)
            {
                // An exhausted allowance is not evidence of change: unchanged identity survives.
                if (!budget.Available) { if (changed) Clear(); return false; }
                TMap map = frame.Maps![i];
                if (map == null) { Clear(); return false; }
                // Duplicate references are invalid, not alternate targets. At most 128²/2 checks.
                for (int j = 0; j < i; j++)
                    if (ReferenceEquals(maps[j], map)) { Clear(); return false; }
                maps[i] = map;
            }
            if (!budget.Available) { if (changed) Clear(); return false; }
            root = frame.Root; game = frame.Game; source = frame.Maps; access = frame.MapAccess; revision = edge; count = size;
            if (source != null) version = source.GetEnumerator();
            if (rootId == null) rootId = Ids.New("r");
            if (game != null && gameId == null) gameId = Ids.New("g");
            return true;
        }
        // IDs are allocated only after the audited access gate passes for that exact map.
        internal Scope? MapScope(TMap map)
        {
            if (rootId == null || gameId == null) return null;
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(maps[i], map))
                    return Accessible(map) ? new Scope(session, rootId, gameId, mapIds[i] ?? (mapIds[i] = Ids.New("m"))) : null;
            return null;
        }
        // Rechecks access at every resolution: a once-allocated ID is not authorization.
        internal TMap? ResolveMap(Scope requested)
        {
            if (Check(requested) != ScopeMatch.Match || requested.Map == null) return null;
            for (int i = 0; i < count; i++)
                if (mapIds[i] != null && string.Equals(mapIds[i], requested.Map, StringComparison.Ordinal))
                    return Accessible(maps[i]!) ? maps[i] : null;
            return null;
        }
        internal ScopeMatch Check(Scope requested)
        {
            Scope? current = Scope;
            if (current == null || requested.Session != current.Session || requested.Root != current.Root
                || requested.Game != current.Game) return ScopeMatch.Stale;
            if (requested.Map == null) return ScopeMatch.Match;
            for (int i = 0; i < count; i++)
                if (mapIds[i] != null && string.Equals(mapIds[i], requested.Map, StringComparison.Ordinal))
                    return Accessible(maps[i]!) ? ScopeMatch.Match : ScopeMatch.Unavailable;
            return ScopeMatch.Unavailable;
        }
        internal bool Matches(Scope requested) => Check(requested) == ScopeMatch.Match;
        // Caller has already passed every audited target gate for this exact map.
        internal string? Reference(string kind, object target, TMap map)
        {
            if (MapScope(map) == null) return null;
            return Entities.Allocate(kind, target, map);
        }
        // Candidate only: caller must rerun all target gates before using or publishing it.
        internal object? ResolveEntity(Scope requested, string kind, string id)
        {
            TMap? map = ResolveMap(requested);
            return map == null ? null : Entities.Resolve(kind, id, map);
        }
        private bool Accessible(TMap map)
        {
            if (access == null) return false;
            try { return access(map); }
            catch { return false; }
        }
    }

    internal sealed class ReadDispatcher<TMap> where TMap : class
    {
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly PendingReads pending;
        private readonly TransitionFence transitions;
        private readonly Func<long> now;
        private readonly IReadProducer<TMap>?[] producers = new IReadProducer<TMap>?[Enum.GetValues(typeof(ReadMethod)).Length];
        private readonly ProducerWork<TMap>?[] works = new ProducerWork<TMap>?[WireLimits.ActiveReads];
        private int rotation;
        internal GenerationTracker<TMap> Generations { get; }
        internal bool Ready { get; private set; }
        // Private authorization only. No completed-render proof enabled here.
        internal DeveloperSafety Safety { get; private set; }
        internal ObservationEpochs Observations => transitions.Observations;
        internal Func<long>? ExtraWorkTicks { get; set; }
        internal Func<int>? ExtraCandidates { get; set; }
        internal Func<int>? ExtraNestedFields { get; set; }
        internal int ProcessedLastUpdate { get; private set; }
        internal long SpentTicks { get; private set; }
        internal long BoundarySampleTicks { get; private set; }
        internal void BeginUpdateAllowance() { SpentTicks = 0; BoundarySampleTicks = 0; }
        internal int ActiveCount => transitions.Active.Count;
        internal ReadDispatcher(string session, PendingReads pending, TransitionFence transitions, Func<long>? now = null,
            params (ReadMethod Method, IReadProducer<TMap> Producer)[] routes)
        {
            this.pending = pending; this.transitions = transitions; this.now = now ?? (() => Monotonic.Now);
            Generations = new GenerationTracker<TMap>(session, this.now);
            foreach (var route in routes)
            {
                // Bootstrap/auth stay producer-free; one trusted producer per ordinary method.
                if (route.Producer == null || route.Method <= ReadMethod.Capabilities
                    || producers[(int)route.Method] != null) throw new ArgumentException("Invalid producer route.");
                producers[(int)route.Method] = route.Producer;
            }
        }
        internal void Pump(Func<ReadFrame<TMap>> sample) => Pump(_ => sample());
        internal void Pump(Func<UpdateBudget, ReadFrame<TMap>> sample) => Pump(sample, true);
        internal void SampleBoundary(Func<UpdateBudget, ReadFrame<TMap>> sample) => Pump(sample, false);
        private void Pump(Func<UpdateBudget, ReadFrame<TMap>> sample, bool processRequests)
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Game thread required.");
            if (processRequests) BeginUpdateAllowance();
            long started = now();
            var budget = UpdateBudget.Boundary(now);
            long sampleTicks = -1;
            ProcessedLastUpdate = 0; Ready = false; Safety = DeveloperSafety.Unknown;
            bool held = transitions.TryRead();
            try
            {
                Scope? before = Generations.Scope;
                long edge = transitions.Revision;
                ReadFrame<TMap>? frame = null;
                if (held && budget.Available)
                {
                    frame = sample(budget);
                    Safety = frame.Safety == DeveloperSafety.Unsafe ? DeveloperSafety.Unsafe
                        : budget.Available ? frame.Safety : DeveloperSafety.Unknown;
                    // A sample that ran the allowance out while reporting not-ready supplies no
                    // source evidence either way (first-use JIT, render/event work share it): never
                    // rotate root/game identity for that. Reads stay refused; the next fully
                    // budgeted sample still rechecks refs/revision and clears a real change.
                    bool noEvidence = !frame.Ready && !budget.Available && Safety != DeveloperSafety.Unsafe;
                    Ready = !noEvidence && Generations.Sample(frame, edge, budget);
                    if (!budget.Available && Safety != DeveloperSafety.Unsafe) Safety = DeveloperSafety.Unknown;
                    Observations.Observe(Ready, Safety, noEvidence ? null : Generations.Scope, frame.Geometry, budget.Available);
                    // Any refused/unsafe/incomplete sample is an authorization change for retained data.
                    if (Observations.Refusal.HasValue) Generations.Snapshots.Expire();
                }
                else
                {
                    // An exhausted shared callback allowance supplies no source evidence.
                    // Revoke authorization and retained data, but do not invent a root/game
                    // replacement. Next safe sample must recheck refs/list version/edge before
                    // any read can run. A held transition still clears generations immediately.
                    if (!held) Generations.Clear();
                    Generations.Snapshots.Expire();
                    Observations.Observe(false, Safety, null, null, false);
                }
                if (transitions.Revision != edge)
                { Ready = false; Safety = DeveloperSafety.Unknown; Generations.Clear(); Observations.Invalidate(); }
                if (before != null && (Generations.Scope == null || !before.Same(Generations.Scope)))
                    pending.Invalidate(Safety == DeveloperSafety.Unsafe ? WireError.ObservationRefused
                        : Ready ? WireError.StaleScope : WireError.NotReady);
                // A missing/failed boundary sample revokes active work too. Only producer
                // exhaustion may defer work; it cannot suppress safety sampling.
                for (int i = 0; i < works.Length; i++)
                    if (works[i] != null && Refusal(works[i]!, i, edge) is WireError refused) Drop(i, refused);
                sampleTicks = Math.Max(0, now() - started);
                budget = new UpdateBudget(now, SpentTicks + (ExtraWorkTicks?.Invoke() ?? 0));
                var allowance = new UpdateAllowance(budget);
                allowance.Candidates = Math.Max(0, allowance.Candidates - Math.Max(0, ExtraCandidates?.Invoke() ?? 0));
                allowance.NestedFields = Math.Max(0, allowance.NestedFields - Math.Max(0, ExtraNestedFields?.Invoke() ?? 0));
                while (processRequests && budget.Available && ProcessedLastUpdate < WireLimits.ReadsPerUpdate && pending.TryTake(out PendingRead? work))
                {
                    ProcessedLastUpdate++;
                    if (!work!.IsLive) continue;
                    Request request = work.Request;
                    // Authenticated bootstrap exceptions contain no game/UI/manifest data and
                    // need no developer-safety evidence. Sampled readiness never enables reads.
                    if (request.Method == ReadMethod.Health || request.Method == ReadMethod.Capabilities)
                    {
                        if (work.IsLive && budget.Available)
                            work.TryComplete(request.Method == ReadMethod.Health
                                ? Responses.Health(Ready && transitions.Revision == edge, HealthScope(edge))
                                : Responses.BootstrapCapabilities(m => producers[(int)m] != null));
                        else if (work.IsLive) work.TryReject(WireError.Busy);
                        continue;
                    }
                    WireError? error;
                    if (request.Method == ReadMethod.Auth) error = WireError.InvalidRequest;
                    else if (Safety == DeveloperSafety.Unsafe) error = WireError.ObservationRefused;
                    else if (!Ready || transitions.Revision != edge) error = WireError.NotReady;
                    else if (Observations.Refusal is WireError refusal) error = refusal;
                    else if (request.Scope != null && ScopeRefusal(request.Scope) is WireError scoped) error = scoped;
                    // Authorization prerequisites alone never enable an absent producer.
                    else if (producers[(int)request.Method] is IReadProducer<TMap> producer) error = Admit(work, producer);
                    else error = WireError.Unavailable;
                    if (!error.HasValue) continue;
                    if (work.IsLive && budget.Available) work.TryReject(error.Value);
                    else if (work.IsLive) work.TryReject(WireError.Busy);
                }
                if (processRequests && frame != null) Step(frame, allowance, edge);
            }
            catch
            {
                Ready = false; Safety = DeveloperSafety.Unknown;
                Generations.Clear(); Observations.Observe(false, Safety, null, null, false);
                pending.Invalidate(WireError.NotReady);
                for (int i = 0; i < works.Length; i++) if (works[i] != null) Drop(i, WireError.NotReady);
                // Never log arbitrary getter/patch errors or expose their private state.
            }
            finally
            {
                long elapsed = Math.Max(0, now() - started);
                if (sampleTicks < 0) sampleTicks = elapsed;
                BoundarySampleTicks += sampleTicks;
                if (processRequests) SpentTicks += Math.Max(0, elapsed - sampleTicks);
                if (held) transitions.ReleaseRead();
            }
        }

        // Cold-start handle: root/game only, and only when an ordinary read would be admitted now.
        // Unsafe, unknown, loading or refused samples publish no scope.
        private Scope? HealthScope(long edge) =>
            Ready && transitions.Revision == edge && Safety == DeveloperSafety.Safe && !Observations.Refusal.HasValue
                ? Generations.Scope : null;

        private WireError? Admit(PendingRead read, IReadProducer<TMap> producer)
        {
            if (producer.SnapshotBuild)
                foreach (ProducerWork<TMap>? other in works)
                    if (other != null && other.SnapshotBuild) return WireError.Busy;
            int slot = transitions.Active.Claim(read);
            if (slot < 0) return WireError.Busy;
            IReadContinuation<TMap> continuation;
            try { continuation = producer.Start(read.Request); }
            catch { transitions.Active.Free(slot); return WireError.Unavailable; }
            works[slot] = new ProducerWork<TMap>(transitions.Active, read, continuation, producer.SnapshotBuild,
                Generations.Scope!, Generations);
            return null;
        }

        // Developer evidence wins; then readiness, epochs and exact start scope (never re-targeted).
        private WireError? Refusal(ProducerWork<TMap> work, int slot, long edge)
        {
            if (transitions.Active.Revoked(slot) || !work.Read.IsLive) return WireError.NotReady;
            if (Safety == DeveloperSafety.Unsafe) return WireError.ObservationRefused;
            if (!Ready || transitions.Revision != edge) return WireError.NotReady;
            if (Observations.Refusal is WireError refusal) return refusal;
            Scope? current = Generations.Scope;
            if (current == null || !work.Scope.Same(current)) return WireError.StaleScope;
            return work.Request.Scope != null ? ScopeRefusal(work.Request.Scope) : null;
        }

        private WireError? ScopeRefusal(Scope scope)
        {
            switch (Generations.Check(scope))
            {
                case ScopeMatch.Match: return null;
                case ScopeMatch.Stale: return WireError.StaleScope;
                default: return WireError.Unavailable;
            }
        }

        // Round-robin from a rotating start; each step gets a fair share of the shared allowance.
        private void Step(ReadFrame<TMap> frame, UpdateAllowance allowance, long edge)
        {
            int sharers = 0;
            foreach (ProducerWork<TMap>? work in works) if (work != null) sharers++;
            if (sharers == 0 || !allowance.Time.Available) return;
            ObservationLease lease;
            try { lease = Observations.Acquire(Generations.Scope!); }
            catch (WireException) { return; } // Revalidated above; refused works drop next sample.
            int start = rotation; rotation = (rotation + 1) % works.Length;
            for (int k = 0; k < works.Length; k++)
            {
                int i = (start + k) % works.Length;
                ProducerWork<TMap>? work = works[i];
                if (work == null) continue;
                if (Refusal(work, i, edge) is WireError refused) { Drop(i, refused); continue; }
                if (!allowance.Time.Available) break;
                if (!work.Completed)
                {
                    work.Begin(frame, allowance, sharers--);
                    StepResult result;
                    try { result = work.Continuation.Step(work); }
                    catch { work.Fail(WireError.Unavailable); result = StepResult.Continue; }
                    finally { work.End(); }
                    if (Refusal(work, i, edge) is WireError late) { Drop(i, late); continue; }
                    if (work.Failure is WireError failure) { Drop(i, failure); continue; }
                    if (result != StepResult.Complete) continue;
                    work.Completed = true;
                }
                Release(i, work, frame, allowance, lease);
            }
        }

        // Final safety/scope/eligibility admission and primitive handoff in one releasing update.
        private void Release(int slot, ProducerWork<TMap> work, ReadFrame<TMap> frame, UpdateAllowance allowance, ObservationLease lease)
        {
            if (!allowance.Time.Available) return; // Retry admission next update.
            try
            {
                work.Begin(frame, allowance, 1);
                bool eligible;
                try { eligible = work.Continuation.Eligible(work); }
                finally { work.End(); }
                if (work.Failure is WireError failure) throw new WireException(failure);
                if (!eligible && work.Yielded) return;
                if (!eligible) throw new WireException(WireError.Unavailable);
                JObject result = work.Continuation.Result();
                // Over budget after the audit: admission would be refused as unsampled. Defer, never reject.
                if (!allowance.Time.Available) return;
                PendingRead read = work.Read;
                Observations.Release(lease, Ready, Safety, Generations.Scope, frame.Geometry,
                    allowance.Time.Available, () => read.TryComplete(result));
                Drop(slot, null);
            }
            catch (WireException error) { Drop(slot, error.Code); }
            catch { Drop(slot, WireError.Unavailable); }
        }

        private void Drop(int slot, WireError? error)
        {
            ProducerWork<TMap> work = works[slot]!;
            works[slot] = null;
            if (error.HasValue) work.Read.TryReject(error.Value);
            work.Read.Cancel(); // No-op after completion; never leaves transport awaiting.
            work.ReleaseMemory();
            transitions.Active.Free(slot);
            try { work.Continuation.Discard(); }
            catch { /* Partial managed state is unreachable after this point. */ }
        }
    }
}
