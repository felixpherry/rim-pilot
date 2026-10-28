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

internal static class ReadDispatcherTests
{
    private static int passed, failed;
    internal static int Run()
    {
        Test("menu scope stable without Game or maps", () =>
        {
            using var f = new Fixture(); f.Game = null; f.Maps = null; f.Pump();
            Scope first = f.Scope; f.Pump(); True(first.Same(f.Scope)); True(f.Dispatch.Ready); Equal(0, f.Tracker.RetainedMaps);
        });
        Test("playing game and map scope stable by reference", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope scope = f.MapScope; f.Pump();
            True(scope.Same(f.MapScope)); True(f.Tracker.Matches(scope)); Equal(1, f.Tracker.RetainedMaps);
        });
        Test("value-equal maps have distinct reference identities", () =>
        {
            using var f = new Fixture(); f.Maps!.Add(new EqualMap()); f.Pump();
            True(f.MapScope.Map != f.Tracker.MapScope(f.Maps[1])!.Map);
        });
        Test("replacement root invalidates all generations", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; f.Root = new object(); f.Pump(); Changed(old, f.MapScope);
        });
        Test("replacement Game invalidates all generations despite same map refs", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; f.Game = new object(); f.Pump(); Changed(old, f.MapScope);
        });
        Test("same-reference Game return between samples invalidated by edge", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Fence.Begin(); f.Fence.End(); f.Pump(); Changed(old, f.MapScope);
        });
        Test("same-reference root scene return between samples invalidated by edge", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            for (int i = 0; i < 2; i++) { f.Fence.Begin(); f.Fence.End(); }
            f.Pump(); Changed(old, f.MapScope);
        });
        Test("raw remove re-add same map between samples detected by List version", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; EqualMap map = f.Maps![0];
            f.Maps.Remove(map); f.Maps.Add(map); f.Pump(); Changed(old, f.MapScope);
        });
        Test("raw clear re-add identical final maps detected", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; EqualMap map = f.Maps![0];
            f.Maps.Clear(); f.Maps.Add(map); f.Pump(); Changed(old, f.MapScope);
        });
        Test("same-reference list index assignment invalidates", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Maps![0] = f.Maps[0]; f.Pump(); Changed(old, f.MapScope);
        });
        Test("empty list mutation witness checks version too", () =>
        {
            using var f = new Fixture(); f.Maps!.Clear(); f.Pump(); Scope old = f.Scope;
            f.Maps.Add(new EqualMap()); f.Maps.Clear(); f.Pump(); True(!old.Same(f.Scope));
        });
        Test("replacement list with same references invalidates", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Maps = new List<EqualMap>(f.Maps!); f.Pump(); Changed(old, f.MapScope);
        });
        Test("removed map never resolves and re-added ref gets new identity", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; EqualMap map = f.Maps![0];
            f.Maps.Clear(); f.Pump(); True(f.Tracker.MapScope(map) == null); True(!f.Tracker.Matches(old));
            f.Maps.Add(map); f.Pump(); Changed(old, f.MapScope);
        });
        Test("loading clears retained refs without accessing maps", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Ready = false; f.Pump(); True(f.Tracker.Scope == null); Equal(0, f.Tracker.RetainedMaps);
            f.Ready = true; f.Pump(); Changed(old, f.MapScope);
        });
        Test("missing root refuses and never retains map refs", () =>
        {
            using var f = new Fixture(); f.Root = null; f.Pump(); True(!f.Dispatch.Ready); Equal(0, f.Tracker.RetainedMaps);
        });
        Test("maps without Game refused", () =>
        {
            using var f = new Fixture(); f.Game = null; f.Pump(); True(!f.Dispatch.Ready); Equal(0, f.Tracker.RetainedMaps);
        });
        Test("128-map ceiling accepted and identities allocated only on demand", () =>
        {
            using var f = new Fixture(); f.Maps!.Clear();
            for (int i = 0; i < WireLimits.TrackedMaps; i++) f.Maps.Add(new EqualMap());
            f.Pump(); True(f.Dispatch.Ready); Equal(WireLimits.TrackedMaps, f.Tracker.RetainedMaps); True(f.MapScope.Map != null);
        });
        Test("over-ceiling list refuses before retaining population", () =>
        {
            using var f = new Fixture();
            for (int i = 0; i < WireLimits.TrackedMaps; i++) f.Maps!.Add(new EqualMap());
            f.Pump(); True(!f.Dispatch.Ready); Equal(0, f.Tracker.RetainedMaps);
        });
        Test("null or duplicate references fail closed", () =>
        {
            using var f = new Fixture(); f.Maps!.Add(f.Maps[0]); f.Pump(); True(!f.Dispatch.Ready);
            f.Maps.Clear(); f.Maps.Add(null!); f.Pump(); True(!f.Dispatch.Ready); Equal(0, f.Tracker.RetainedMaps);
        });
        Test("active worker transition skips sampling, invalidates work immediately", () =>
        {
            using var f = new Fixture(); f.Pump(); PendingRead work = f.Enqueue();
            Task.Run(f.Fence.Begin).GetAwaiter().GetResult(); Equal(WireError.NotReady, Error(work));
            f.Dispatch.Pump(() => throw new Exception("must not sample loading state")); True(!f.Dispatch.Ready);
            f.Fence.End(); f.Pump(); True(f.Dispatch.Ready);
        });
        Test("menu null-to-null Game assignments preserve scope and queued bootstrap", () =>
        {
            using var f = new Fixture(); f.Game = null; f.Maps = null; f.Pump(); Scope scope = f.Scope;
            PendingRead health = f.Enqueue(), capabilities = f.Enqueue("capabilities");
            long revision = f.Fence.Revision;
            for (int i = 0; i < 8; i++)
            {
                f.Fence.BeginGameAssignment(null, null, out GameAssignmentState state); True(!state.Entered);
                f.Fence.FinishGameAssignment(ref state);
            }
            Equal(revision, f.Fence.Revision); f.Pump(); True(scope.Same(f.Scope));
            Equal("ready", (string)Data(health)["state"]!); True(Data(capabilities)["capabilities"] != null);
        });
        Test("Game assignments with either non-null reference still invalidate pending work", () =>
        {
            using var f = new Fixture(); object game = new object();
            foreach (var pair in new[] { (Before: (object?)null, After: (object?)game),
                (Before: (object?)game, After: (object?)null), (Before: (object?)game, After: (object?)game) })
            {
                f.Pump(); Scope old = f.Scope; PendingRead work = f.Enqueue(); long revision = f.Fence.Revision;
                f.Fence.BeginGameAssignment(pair.Before, pair.After, out GameAssignmentState state); True(state.Entered);
                Equal(WireError.NotReady, Error(work)); f.Pump(); True(!f.Dispatch.Ready);
                f.Fence.FinishGameAssignment(ref state); Equal(revision + 2, f.Fence.Revision);
                f.Pump(); True(!old.Same(f.Scope));
            }
        });
        Test("same-reference Game return across null never resurrects scopes", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope; object game = f.Game!;
            f.Fence.BeginGameAssignment(game, null, out GameAssignmentState leaving); f.Fence.FinishGameAssignment(ref leaving);
            f.Fence.BeginGameAssignment(null, game, out GameAssignmentState returning); f.Fence.FinishGameAssignment(ref returning);
            f.Pump(); Changed(old, f.MapScope);
        });
        Test("skipped Game-assignment prefix invalidates conservatively and finalizer retry is idempotent", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.Scope; PendingRead work = f.Enqueue();
            GameAssignmentState state = default; long revision = f.Fence.Revision;
            f.Fence.FinishGameAssignment(ref state); Equal(WireError.NotReady, Error(work));
            Equal(revision + 2, f.Fence.Revision); f.Fence.FinishGameAssignment(ref state);
            Equal(revision + 2, f.Fence.Revision); f.Pump(); True(!old.Same(f.Scope)); True(f.Dispatch.Ready);
        });
        Test("observed Game-assignment finalizer retries never invalidate a menu no-op", () =>
        {
            using var f = new Fixture(); f.Game = null; f.Maps = null; f.Pump(); Scope old = f.Scope;
            f.Fence.BeginGameAssignment(null, null, out GameAssignmentState state);
            long revision = f.Fence.Revision;
            f.Fence.FinishGameAssignment(ref state); f.Fence.FinishGameAssignment(ref state);
            Equal(revision, f.Fence.Revision); f.Pump(); True(old.Same(f.Scope));
        });
        Test("nested transitions remain unavailable until outer finalizer", () =>
        {
            using var f = new Fixture(); f.Fence.Begin(); f.Fence.Begin(); f.Fence.End(); f.Pump(); True(!f.Dispatch.Ready);
            f.Fence.End(); f.Pump(); True(f.Dispatch.Ready);
        });
        Test("finalizer retry balances entered bracket exactly once", () =>
        {
            using var f = new Fixture(); f.Fence.Begin(); bool entered = true;
            f.Fence.Finish(ref entered); long revision = f.Fence.Revision;
            f.Fence.Finish(ref entered); Equal(revision, f.Fence.Revision); True(!entered);
            f.Pump(); True(f.Dispatch.Ready);
        });
        Test("finalizer for earlier-prefix failure does not decrement unentered bracket", () =>
        {
            using var f = new Fixture(); bool entered = false; long revision = f.Fence.Revision;
            f.Fence.Finish(ref entered); Equal(revision, f.Fence.Revision); f.Pump(); True(f.Dispatch.Ready);
        });
        Test("reentrant transition during sample discards scope before execution", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue();
            f.Dispatch.Pump(() => { f.Fence.Begin(); f.Fence.End(); return f.Frame(); });
            True(!f.Dispatch.Ready); True(f.Tracker.Scope == null); Equal(WireError.NotReady, Error(work));
        });
        Test("requests admitted during transition invalidated at exit", () =>
        {
            using var f = new Fixture(); f.Fence.Begin(); PendingRead work = f.Enqueue();
            f.Fence.End(); Equal(WireError.NotReady, Error(work)); Equal(0, f.Pending.Count);
        });
        Test("edge invalidation drains finite queue not latent work", () =>
        {
            using var f = new Fixture(); var work = new List<PendingRead>();
            for (int i = 0; i < WireLimits.PendingRequests; i++) work.Add(f.Enqueue());
            f.Fence.Begin(); f.Fence.End(); foreach (PendingRead read in work) Equal(WireError.NotReady, Error(read));
            Equal(0, f.Pending.Count); f.Pump(); Equal(0, f.Dispatch.ProcessedLastUpdate);
        });
        Test("sample-detected reference transition invalidates pending scope", () =>
        {
            using var f = new Fixture(); f.Pump(); PendingRead work = f.Enqueue("summary", f.Scope);
            f.Root = new object(); f.Pump(); Equal(WireError.StaleScope, Error(work));
        });
        Test("wrong session root game stale but unknown map uniformly unavailable", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope current = f.MapScope;
            foreach (Scope scope in new[] { new Scope(Ids.New("s"), current.Root, current.Game, current.Map),
                new Scope(current.Session, Ids.New("r"), current.Game, current.Map),
                new Scope(current.Session, current.Root, Ids.New("g"), current.Map) })
            { PendingRead work = f.Enqueue("things.list", scope); f.Pump(); Equal(WireError.StaleScope, Error(work)); }
            PendingRead unknown = f.Enqueue("things.list", new Scope(current.Session, current.Root, current.Game, Ids.New("m")));
            f.Pump(); Equal(WireError.Unavailable, Error(unknown));
        });
        Test("map IDs need audited access; hidden known and unknown maps are indistinguishable", () =>
        {
            using var f = new Fixture(); f.Access = null; f.Pump();
            True(f.Tracker.MapScope(f.Maps![0]) == null); True(f.Tracker.Scope != null);
            f.Access = _ => true; f.Pump(); Scope known = f.MapScope;
            f.Access = _ => false; f.Pump(); True(f.Tracker.Scope!.Same(new Scope(known.Session, known.Root, known.Game)));
            Equal(ScopeMatch.Unavailable, f.Tracker.Check(known)); True(f.Tracker.ResolveMap(known) == null);
            PendingRead hidden = f.Enqueue("things.list", known), unknown = f.Enqueue("things.list",
                new Scope(known.Session, known.Root, known.Game, Ids.New("m")));
            f.Pump(); Equal(Error(unknown), Error(hidden)); Equal(WireError.Unavailable, Error(hidden));
            f.Access = _ => throw new Exception("adapter failure"); f.Pump(); Equal(ScopeMatch.Unavailable, f.Tracker.Check(known));
            f.Access = _ => true; f.Pump(); True(ReferenceEquals(f.Maps[0], f.Tracker.ResolveMap(known)));
        });
        Test("noncurrent maps resolve independently without a current-map default", () =>
        {
            using var f = new Fixture(); f.Maps!.Add(new EqualMap()); EqualMap second = f.Maps[1];
            f.Access = map => ReferenceEquals(map, second); f.Pump();
            True(f.Tracker.MapScope(f.Maps[0]) == null); Scope other = f.Tracker.MapScope(second)!;
            True(ReferenceEquals(second, f.Tracker.ResolveMap(other)));
            Equal(ScopeMatch.Stale, f.Tracker.Check(new Scope(other.Session, other.Root, Ids.New("g"), other.Map)));
        });
        Test("entity refs allocated only for accessible maps, map-bound and dropped on rotation", () =>
        {
            using var f = new Fixture(); f.Maps!.Add(new EqualMap()); f.Pump();
            object pawn = new object(); EqualMap first = f.Maps[0], second = f.Maps[1];
            Scope a = f.Tracker.MapScope(first)!, b = f.Tracker.MapScope(second)!;
            string id = f.Tracker.Reference("p", pawn, first)!; Equal(id, f.Tracker.Reference("p", pawn, first));
            True(ReferenceEquals(pawn, f.Tracker.ResolveEntity(a, "p", id)));
            True(f.Tracker.ResolveEntity(b, "p", id) == null); True(f.Tracker.ResolveEntity(a, "t", id) == null);
            f.Access = map => !ReferenceEquals(map, first); f.Pump();
            True(f.Tracker.Reference("t", new object(), first) == null); True(f.Tracker.ResolveEntity(a, "p", id) == null);
            f.Access = _ => true; f.Pump(); True(ReferenceEquals(pawn, f.Tracker.ResolveEntity(a, "p", id)));
            f.Game = new object(); f.Pump(); Equal(0, f.Tracker.Entities.Count);
            Scope fresh = f.Tracker.MapScope(first)!; True(f.Tracker.ResolveEntity(fresh, "p", id) == null);
        });
        Test("unregistered routes remain unavailable even with current scopes", () =>
        {
            using var f = new Fixture(); f.Pump();
            foreach (string method in new[] { "summary", "pawns.list", "pawn.get", "things.list", "resources", "ui", "screenshot", "events.read" })
            { PendingRead work = f.Enqueue(method, f.MapScope); f.Pump(); Equal(WireError.Unavailable, Error(work)); }
        });
        Test("instantaneous safety never registers absent producers; bootstrap scope remains safety-gated", () =>
        {
            using var f = new Fixture();
            foreach (DeveloperSafety evidence in Enum.GetValues<DeveloperSafety>())
            {
                f.Safety = evidence; f.Pump(); Equal(evidence, f.Dispatch.Safety);
                foreach (string method in new[] { "summary", "pawns.list", "pawn.get", "things.list", "resources", "ui", "screenshot", "events.read" })
                { PendingRead read = f.Enqueue(method, f.MapScope); f.Pump();
                    Equal(evidence == DeveloperSafety.Safe ? WireError.Unavailable : WireError.ObservationRefused, Error(read)); }
                PendingRead health = f.Enqueue(); f.Pump(); Equal(evidence == DeveloperSafety.Safe ? 2 : 1, Data(health).Count);
                PendingRead capabilities = f.Enqueue("capabilities"); f.Pump();
                foreach (JToken item in (JArray)Data(capabilities)["capabilities"]!)
                    if ((string?)item["state"] == "available")
                        True(Array.IndexOf(new[] { "auth", "health", "capabilities" }, (string)item["name"]!) >= 0);
            }
        });
        Test("developer refusal precedes stale scope and loading without enriching bootstrap", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Safety = DeveloperSafety.Unsafe;
            PendingRead read = f.Enqueue("summary", old); f.Root = new object(); f.Pump();
            Equal(WireError.ObservationRefused, Error(read));
            read = f.Enqueue("summary", old); f.Ready = false; f.Pump();
            Equal(WireError.ObservationRefused, Error(read));
            PendingRead health = f.Enqueue(), capabilities = f.Enqueue("capabilities"); f.Pump();
            Equal(1, Data(health).Count); Equal("loading", (string)Data(health)["state"]!);
            True(Data(capabilities)["capabilities"] != null);
        });
        Test("retained snapshots survive safe samples and expire on refusal or rotation", () =>
        {
            foreach (Action<Fixture> change in new Action<Fixture>[]
            {
                f => f.Safety = DeveloperSafety.Unsafe, f => f.Safety = DeveloperSafety.Unknown,
                f => f.Ready = false, f => f.Game = new object(), f => { f.Fence.Begin(); f.Fence.End(); }
            })
            {
                using var f = new Fixture(); f.Pump();
                SnapshotStore store = f.Tracker.Snapshots; object update = new object();
                EligibleSnapshot snapshot = store.Create(f.MapScope, ReadMethod.Things, 1, new[] { new JObject() },
                    new object[] { 1 }, 2, new StableSource(), 0);
                Revalidation pass = store.BeginRevalidation(snapshot);
                Equal(Revalidated.Valid, store.Revalidate(pass, update, new NestedBudget()));
                True(store.Retain(pass, update));
                f.Pump(); Equal(1, store.Count);
                change(f); f.Pump(); Equal(0, store.Count);
            }
        });
        Test("authorized health supplies root/game cold-start scope only, no baseline", () =>
        {
            using var f = new Fixture(); f.Pump();
            PendingRead work = f.Enqueue(); f.Pump();
            JObject data = Data(work); Equal("ready", (string)data["state"]!); Equal(2, data.Count);
            True(Scope.Read(data["scope"]!).Same(f.Scope)); True(f.Scope.Map == null && f.Scope.Game != null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Health, data), ReadMethod.Health, "read");
            f.Game = null; f.Maps = null; f.Pump(); work = f.Enqueue(); f.Pump();
            data = Data(work); True(Scope.Read(data["scope"]!).Same(f.Scope)); True(f.Scope.Game == null);
        });
        Test("unsafe, unknown, loading or unsampled health publishes no scope", () =>
        {
            foreach (DeveloperSafety safety in new[] { DeveloperSafety.Unsafe, DeveloperSafety.Unknown })
            {
                using var f = new Fixture(); f.Safety = safety; f.Pump();
                PendingRead work = f.Enqueue(); f.Pump(); True(Data(work)["scope"] == null);
            }
            using var g = new Fixture(); g.Pump(); g.Ready = false; g.Pump();
            PendingRead loading = g.Enqueue(); g.Pump(); True(Data(loading)["scope"] == null);
            using var h = new Fixture(); h.Pump();
            PendingRead boundary = h.Enqueue(); h.Dispatch.Observations.BeginBoundary(); h.Pump();
            True(Data(boundary)["scope"] == null); h.Dispatch.Observations.EndBoundary();
        });
        Test("loading bootstrap reports loading and fixed capability gaps without map access", () =>
        {
            using var f = new Fixture(); f.Ready = false;
            PendingRead health = f.Enqueue(), capabilities = f.Enqueue("capabilities"); f.Pump();
            Equal("loading", (string)Data(health)["state"]!);
            JObject data = Data(capabilities);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Capabilities, data), ReadMethod.Capabilities, "read");
            var available = new List<string>(); int unverified = 0, unsupported = 0;
            foreach (JToken item in (JArray)data["capabilities"]!)
            {
                if ((string?)item["state"] == "available") available.Add((string)item["name"]!);
                if ((string?)item["state"] == "unverified") unverified++;
                if ((string?)item["state"] == "unsupported") unsupported++;
            }
            Equal("auth,health,capabilities", string.Join(",", available)); Equal(9, unverified); Equal(2, unsupported);
            Equal(0, f.Tracker.RetainedMaps); True(!f.Dispatch.Ready);
        });
        Test("active transition permits only minimal loading bootstrap, never invokes sampler", () =>
        {
            using var f = new Fixture(); f.Fence.Begin(); PendingRead work = f.Enqueue();
            f.Dispatch.Pump(() => throw new Exception("must not sample"));
            Equal("loading", (string)Data(work)["state"]!); f.Fence.End();
        });
        Test("capability calls own fresh trees, cannot enable observations by mutation", () =>
        {
            JObject first = Responses.BootstrapCapabilities(); first["capabilities"]![3]!["state"] = "available";
            Equal("unverified", (string)Responses.BootstrapCapabilities()["capabilities"]![3]!["state"]!);
        });
        Test("boundary sampling does not dispatch additional queued requests", () =>
        {
            using var f = new Fixture(); f.Pump(); PendingRead work = f.Enqueue();
            f.Dispatch.SampleBoundary(_ => f.Frame());
            True(!work.Completion.IsCompleted); Equal(1, f.Pending.Count); Equal(0, f.Dispatch.ProcessedLastUpdate);
            f.Pump(); Equal("ready", (string)Data(work)["state"]!);
        });
        Test("independent GUI boundary samples despite producer exhaustion without draining queue", () =>
        {
            using var f = new Fixture(); f.Game = null; f.Maps = null; f.Pump(); Scope scope = f.Scope;
            ObservationLease lease = f.Dispatch.Observations.Acquire(scope);
            PendingRead queued = f.Enqueue("summary", scope);
            f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            bool sampled = false;
            f.Dispatch.SampleBoundary(_ => { sampled = true; return f.Frame(); });
            True(sampled); True(f.Dispatch.Ready); Equal(DeveloperSafety.Safe, f.Dispatch.Safety);
            True(!f.Dispatch.Observations.Refusal.HasValue); True(!queued.Completion.IsCompleted);
            True(scope.Same(f.Scope)); Equal(0, f.Dispatch.ProcessedLastUpdate);
            f.Dispatch.ExtraWorkTicks = null; f.Pump();
            True(scope.Same(f.Scope)); Equal(WireError.Unavailable, Error(queued));
            // Real callback brackets separately revoke leases. This synthetic safe sample
            // never entered a bracket and does not invent an unsafe interval.
            bool transferred = false;
            f.Dispatch.Observations.Release(lease, true, DeveloperSafety.Safe, scope,
                new FrameGeometry(1, 1280, 720, 1), true, () => transferred = true);
            True(transferred);
        });
        Test("transition during budget-only boundary still rotates identities on next safe sample", () =>
        {
            using var f = new Fixture(); f.Pump(); Scope old = f.MapScope;
            f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            f.Fence.Begin(); f.Fence.End();
            f.Dispatch.SampleBoundary(_ => f.Frame());
            Changed(old, f.MapScope);
            f.Dispatch.ExtraWorkTicks = null; f.Pump(); Changed(old, f.MapScope);
        });
        Test("independent sample failure still expires retained snapshots", () =>
        {
            using var f = new Fixture(); f.Pump(); SnapshotStore store = f.Tracker.Snapshots;
            object update = new object();
            EligibleSnapshot snapshot = store.Create(f.MapScope, ReadMethod.Things, 1, new[] { new JObject() },
                new object[] { 1 }, 2, new StableSource(), 0);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new NestedBudget())); True(store.Retain(pass, update));
            Equal(1, store.Count); f.Dispatch.ExtraWorkTicks = () => Stopwatch.Frequency;
            f.Dispatch.SampleBoundary(_ => throw new Exception("sample failed")); Equal(0, store.Count);
        });
        Test("transition invalidates registered active request after queue removal before release", () =>
        {
            using var f = new Fixture(); f.Pump(); ObservationLease lease = f.Dispatch.Observations.Acquire(f.Scope);
            PendingRead work = f.Enqueue("summary", f.Scope); True(f.Pending.TryTake(out PendingRead? taken));
            True(ReferenceEquals(work, taken)); Equal(0, f.Pending.Count);
            var owner = new ActiveOwner(work); f.Dispatch.Observations.Register(owner);
            Task.Run(() => { f.Fence.Begin(); f.Fence.End(); }).GetAwaiter().GetResult();
            Equal(WireError.NotReady, Error(work)); True(owner.Cancelled);
            try
            {
                f.Dispatch.Observations.Release(lease, true, DeveloperSafety.Safe, f.Scope,
                    new FrameGeometry(1, 1280, 720, 1), true, () => work.TryComplete(new JObject()));
                throw new Exception("released stale active work");
            }
            catch (WireException error) { Equal(WireError.NotReady, error.Code); }
            True(!work.TryComplete(new JObject())); f.Dispatch.Observations.Unregister(owner);
        });
        Test("sample failure revokes previously admitted active authorization", () =>
        {
            using var f = new Fixture(); f.Pump(); ObservationLease lease = f.Dispatch.Observations.Acquire(f.Scope);
            f.Dispatch.Pump(() => throw new Exception("private")); f.Pump();
            try
            {
                f.Dispatch.Observations.Release(lease, true, DeveloperSafety.Safe, f.Scope,
                    new FrameGeometry(1, 1280, 720, 1), true, () => throw new Exception("must not transfer"));
                throw new Exception("released stale sample");
            }
            catch (WireException error) { Equal(WireError.StaleScope, error.Code); }
        });
        Test("per-update request count bounded, remainder stays queued", () =>
        {
            using var f = new Fixture(); f.Pump();
            for (int i = 0; i < WireLimits.PendingRequests; i++) f.Enqueue();
            f.Pump(); Equal(WireLimits.ReadsPerUpdate, f.Dispatch.ProcessedLastUpdate);
            Equal(WireLimits.PendingRequests - WireLimits.ReadsPerUpdate, f.Pending.Count);
        });
        Test("absolute time budget prevents starting sample/work", () =>
        {
            using var f = new Fixture(); long clock = 0;
            var dispatcher = new ReadDispatcher<EqualMap>(f.Session, f.Pending, f.Fence, () => clock += Stopwatch.Frequency);
            PendingRead work = f.Enqueue(); dispatcher.Pump(() => throw new Exception("must not sample"));
            True(!dispatcher.Ready); Equal(0, dispatcher.ProcessedLastUpdate); True(!work.Completion.IsCompleted);
        });
        Test("tracking budget failure releases all partial refs", () =>
        {
            using var f = new Fixture(); long clock = 0;
            var tracker = new GenerationTracker<EqualMap>(f.Session);
            var budget = new UpdateBudget(() => clock += Stopwatch.Frequency);
            True(!tracker.Sample(f.Frame(), 0, budget)); Equal(0, tracker.RetainedMaps); True(tracker.Scope == null);
        });
        Test("disconnect cancels queued work, no producer or latent read", () =>
        {
            using var f = new Fixture(); f.Pump(); PendingRead work = f.Enqueue(); f.Lifetime.Close(); f.Pump();
            True(!work.IsLive); True(!work.TryComplete(new JObject())); Equal(0, f.Pending.Count); Equal(0, f.Dispatch.ProcessedLastUpdate);
        });
        Test("expired request never starts main-thread work", () =>
        {
            using var f = new Fixture(); f.Lifetime.Begin(1); PendingRead work = f.Enqueue(); Thread.Sleep(5); f.Pump();
            True(!work.IsLive); Equal(0, f.Dispatch.ProcessedLastUpdate); Equal(0, f.Pending.Count);
        });
        Test("completed work cannot be readmitted or completed twice", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue(); True(work.TryReject(WireError.Unavailable));
            True(!work.IsLive); True(!f.Pending.TryAdd(work)); True(!work.TryComplete(new JObject()));
        });
        Test("queue stop cancels owned pending tasks", () =>
        {
            using var f = new Fixture(); PendingRead work = f.Enqueue(); f.Pending.Stop();
            True(work.Completion.IsCanceled); True(!f.Pending.TryAdd(work)); Equal(0, f.Pending.Count);
        });
        Test("wrong-thread pump refuses before sample or queue mutation", () =>
        {
            using var f = new Fixture(); f.Enqueue();
            Task.Run(() =>
            {
                try { f.Dispatch.Pump(() => throw new Exception("must not sample")); throw new Exception("accepted worker"); }
                catch (InvalidOperationException error) { Equal("Game thread required.", error.Message); }
            }).GetAwaiter().GetResult(); Equal(1, f.Pending.Count);
        });
        Test("sample failure clears scopes and rejects pending without leaking exception", () =>
        {
            using var f = new Fixture(); f.Pump(); PendingRead work = f.Enqueue();
            f.Dispatch.Pump(() => throw new Exception("private")); Equal(WireError.NotReady, Error(work)); True(f.Tracker.Scope == null);
            f.Pump(); True(f.Dispatch.Ready);
        });
        Test("read gate unavailable skips adapter rather than blocking frame thread", () =>
        {
            using var f = new Fixture(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            Task worker = Task.Run(() => { True(f.Fence.TryRead()); entered.Set(); release.Wait(); f.Fence.ReleaseRead(); });
            True(entered.Wait(1000));
            try { f.Dispatch.Pump(() => throw new Exception("must not sample")); True(!f.Dispatch.Ready); }
            finally { release.Set(); worker.GetAwaiter().GetResult(); }
        });
        Console.WriteLine($"Read dispatcher: {passed} passed; {failed} failed. BCL/host sockets only; no Harmony/Mono/game proof.");
        return failed;
    }
    private sealed class ActiveOwner : IObservationInvalidationSink
    {
        private readonly PendingRead work;
        internal bool Cancelled;
        internal ActiveOwner(PendingRead work) { this.work = work; }
        public void Invalidate(ObservationInvalidation cause) { Cancelled = true; work.TryReject(WireError.NotReady); }
    }
    private sealed class EqualMap
    {
        public override bool Equals(object? other) => other is EqualMap;
        public override int GetHashCode() => 0;
    }
    private sealed class StableSource : ISnapshotSource
    {
        public bool TryStructure(out long version) { version = 0; return true; }
        public bool TryEligibility(out long revision) { revision = 0; return false; }
        public TargetGateResult Revalidate(object member, INestedWork work) => TargetGateResult.Eligible;
    }
    private sealed class NestedBudget : INestedWork { public bool TryNestedFields(int count) => true; }
    private sealed class Fixture : IDisposable
    {
        internal readonly string Session = Ids.New("s");
        internal object? Root = new object(), Game = new object();
        internal List<EqualMap>? Maps = new List<EqualMap> { new EqualMap() };
        internal bool Ready = true;
        internal DeveloperSafety Safety = DeveloperSafety.Safe;
        internal readonly PendingReads Pending = new PendingReads();
        internal readonly TransitionFence Fence;
        internal readonly ReadDispatcher<EqualMap> Dispatch;
        internal readonly SocketLifetime Lifetime;
        private readonly Socket peer;
        internal GenerationTracker<EqualMap> Tracker => Dispatch.Generations;
        internal Scope Scope => Tracker.Scope ?? throw new Exception("missing scope");
        internal Scope MapScope => Tracker.MapScope(Maps![0]) ?? throw new Exception("missing map");
        internal Fixture()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); peer.Connect(listener.LocalEndPoint!);
            Lifetime = new SocketLifetime(listener.Accept(), 10000);
            Fence = new TransitionFence(Pending); Dispatch = new ReadDispatcher<EqualMap>(Session, Pending, Fence, () => 0);
        }
        internal Func<EqualMap, bool>? Access = _ => true;
        internal ReadFrame<EqualMap> Frame() => new ReadFrame<EqualMap>(Root, Game, Ready ? Maps : null, Ready, Safety,
            new FrameGeometry(1, 1280, 720, 1), Access);
        internal void Pump() => Dispatch.Pump(Frame);
        internal PendingRead Enqueue(string method = "health", Scope? scope = null)
        {
            var args = new JObject(); if (scope != null) args["scope"] = scope.Json();
            if (method.EndsWith(".list", StringComparison.Ordinal) || method == "events.read") args["limit"] = 1;
            if (method == "pawn.get") args["pawn"] = Ids.New("p");
            var json = new JObject { ["v"] = 1, ["id"] = "read", ["method"] = method, ["params"] = args };
            var read = new PendingRead(Request.Read(WireJson.Encode(json)), Lifetime);
            True(Pending.TryAdd(read)); return read;
        }
        public void Dispose() { Pending.Stop(); Lifetime.Dispose(); peer.Dispose(); }
    }
    private static JObject Data(PendingRead work) => work.Completion.GetAwaiter().GetResult().Data ?? throw new Exception("missing data");
    private static WireError Error(PendingRead work) => work.Completion.GetAwaiter().GetResult().Error ?? throw new Exception("missing error");
    private static void Changed(Scope before, Scope after)
    {
        True(before.Root != after.Root); True(before.Game != after.Game); True(before.Map != after.Map);
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL dispatcher {name}: {error.Message}"); }
    }
}
