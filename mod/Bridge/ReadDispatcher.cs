using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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
        internal void Begin()
        {
            lock (gate)
            {
                active++;
                Interlocked.Increment(ref revision);
                pending.Invalidate(WireError.NotReady);
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
                Interlocked.Increment(ref revision);
                pending.Invalidate(WireError.NotReady);
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
        internal UpdateBudget(Func<long> now)
        {
            this.now = now;
            end = now() + (long)Math.Ceiling(WireLimits.WorkPerUpdateMs * (double)Stopwatch.Frequency / 1000);
        }
        internal bool Available => now() < end;
    }

    // Supplied only by trusted main-thread adapter, never transport/client data. No map getter
    // may run until adapter has checked AnyEventNowOrWaiting, actual root and program state.
    internal sealed class ReadFrame<TMap> where TMap : class
    {
        internal readonly object? Root, Game;
        internal readonly List<TMap>? Maps;
        internal readonly bool Ready;
        internal ReadFrame(object? root, object? game, List<TMap>? maps, bool ready)
        { Root = root; Game = game; Maps = maps; Ready = ready; }
    }

    internal sealed class GenerationTracker<TMap> where TMap : class
    {
        private readonly string session;
        private readonly TMap?[] maps = new TMap?[WireLimits.TrackedMaps];
        private readonly string?[] mapIds = new string?[WireLimits.TrackedMaps];
        private object? root, game;
        private List<TMap>? source;
        private List<TMap>.Enumerator version;
        private long revision = -1;
        private int count;
        private string? rootId, gameId;
        internal Scope? Scope => rootId == null ? null : new Scope(session, rootId, gameId);
        internal int RetainedMaps => count;
        internal GenerationTracker(string session) { this.session = session; }
        internal void Clear()
        {
            root = null; game = null; source = null; version = default;
            rootId = null; gameId = null; count = 0;
            Array.Clear(maps, 0, maps.Length); Array.Clear(mapIds, 0, mapIds.Length);
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
                if (!budget.Available) { Clear(); return false; }
                TMap map = frame.Maps![i];
                if (map == null) { Clear(); return false; }
                // Duplicate references are invalid, not alternate targets. At most 128²/2 checks.
                for (int j = 0; j < i; j++)
                    if (ReferenceEquals(maps[j], map)) { Clear(); return false; }
                maps[i] = map;
            }
            if (!budget.Available) { Clear(); return false; }
            root = frame.Root; game = frame.Game; source = frame.Maps; revision = edge; count = size;
            if (source != null) version = source.GetEnumerator();
            if (rootId == null) rootId = Ids.New("r");
            if (game != null && gameId == null) gameId = Ids.New("g");
            return true;
        }
        internal Scope? MapScope(TMap map)
        {
            if (rootId == null || gameId == null) return null;
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(maps[i], map))
                    return new Scope(session, rootId, gameId, mapIds[i] ?? (mapIds[i] = Ids.New("m")));
            return null;
        }
        internal bool Matches(Scope requested)
        {
            Scope? current = Scope;
            if (current == null || requested.Session != current.Session || requested.Root != current.Root
                || requested.Game != current.Game) return false;
            if (requested.Map == null) return true;
            for (int i = 0; i < count; i++) if (mapIds[i] == requested.Map) return true;
            return false;
        }
    }

    internal sealed class ReadDispatcher<TMap> where TMap : class
    {
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly PendingReads pending;
        private readonly TransitionFence transitions;
        private readonly Func<long> now;
        internal GenerationTracker<TMap> Generations { get; }
        internal bool Ready { get; private set; }
        internal int ProcessedLastUpdate { get; private set; }
        internal ReadDispatcher(string session, PendingReads pending, TransitionFence transitions, Func<long>? now = null)
        {
            this.pending = pending; this.transitions = transitions; this.now = now ?? (() => Monotonic.Now);
            Generations = new GenerationTracker<TMap>(session);
        }
        internal void Pump(Func<ReadFrame<TMap>> sample)
        {
            if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Game thread required.");
            var budget = new UpdateBudget(now);
            ProcessedLastUpdate = 0; Ready = false;
            bool held = transitions.TryRead();
            try
            {
                Scope? before = Generations.Scope;
                long edge = transitions.Revision;
                if (held && budget.Available) Ready = Generations.Sample(sample(), edge, budget);
                else Generations.Clear();
                if (transitions.Revision != edge) { Ready = false; Generations.Clear(); }
                if (before != null && (Generations.Scope == null || !before.Same(Generations.Scope)))
                    pending.Invalidate(Ready ? WireError.StaleScope : WireError.NotReady);
                while (budget.Available && ProcessedLastUpdate < WireLimits.ReadsPerUpdate && pending.TryTake(out PendingRead? work))
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
                                ? Responses.Health(Ready && transitions.Revision == edge)
                                : Responses.BootstrapCapabilities());
                        else if (work.IsLive) work.TryReject(WireError.Busy);
                        continue;
                    }
                    WireError error;
                    if (request.Method == ReadMethod.Auth) error = WireError.InvalidRequest;
                    else if (!Ready || transitions.Revision != edge) error = WireError.NotReady;
                    else if (request.Scope != null && !Generations.Matches(request.Scope)) error = WireError.StaleScope;
                    // No producer/safety adapter yet. Only typed read intents reach here; no game
                    // getters, client closures or accidental schema-as-capability success path.
                    else error = WireError.Unavailable;
                    if (work.IsLive && budget.Available) work.TryReject(error);
                    else if (work.IsLive) work.TryReject(WireError.Busy);
                }
            }
            catch
            {
                Ready = false; Generations.Clear(); pending.Invalidate(WireError.NotReady);
                // Never log arbitrary getter/patch errors or expose their private state.
            }
            finally { if (held) transitions.ReleaseRead(); }
        }
    }
}
