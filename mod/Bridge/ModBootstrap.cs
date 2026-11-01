using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;
using UnityEngine;
using Verse;

namespace RimPilot.Mod.Bridge
{
    internal static class ModBootstrap
    {
        private const string HarmonyId = "rim-pilot.observation.lifecycle.v1";
        private static readonly PendingReads pending = new PendingReads();
        private static readonly TransitionFence transitions = new TransitionFence(pending);
        private static ReadDispatcher<Map>? dispatcher;
        private static GameDeveloperSafety? safetySource;
        private static DeveloperSafetySampler<Window>? safetySampler;
        private static Func<Map, bool>? mapAccess;
        private static bool mapAccessBound;
        private static GameTargetAccess? targetAccess;
        private static GamePublicEvents? publicEvents;
        private static ScreenshotCapture? screenshots;
        internal static GameNativeRender? NativeRender { get; private set; }
        private static readonly ProcessLifecycle<LoopbackTransport> lifecycle =
            new ProcessLifecycle<LoopbackTransport>(InstallPatches, RegisterFallback, RegisterQuitting, Start);

        internal static void Bootstrap() => lifecycle.Bootstrap();
        internal static LoopbackTransport? Session => lifecycle.IsGameThread ? lifecycle.Session : null;

        private static void InstallPatches()
        {
            // Exact metadata lookup only. No Current/Find/Prefs, Unity objects or game getters.
            // Explicit patch rather than PatchAll avoids accidentally activating future routes.
            // Cache two exact private-field read delegates during metadata-only bootstrap,
            // outside the frame budget. Never invoke them on this loading worker.
            try { safetySource = new GameDeveloperSafety(); }
            catch { safetySource = null; }
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(Exact(typeof(Root), nameof(Root.Update), Type.EmptyTypes),
                prefix: Hook(nameof(BeforeRootUpdate), Priority.First));
            foreach (Type root in new[] { typeof(Root_Entry), typeof(Root_Play) })
                harmony.Patch(Exact(root, nameof(Root.Update), Type.EmptyTypes),
                    prefix: Hook(nameof(BeforeObservedUpdate), Priority.First),
                    finalizer: Hook(nameof(AfterObservedUpdate), Priority.Last));
            harmony.Patch(Exact(typeof(Root), nameof(Root.OnGUI), Type.EmptyTypes),
                prefix: Hook(nameof(BeginGuiBoundary), Priority.First),
                finalizer: Hook(nameof(AfterRootGui), Priority.Last));
            foreach (MethodInfo target in ObservationBoundaryTargets())
                harmony.Patch(target, prefix: Hook(nameof(BeginObservationBoundary), Priority.First),
                    finalizer: Hook(nameof(EndObservationBoundary), Priority.Last));
            // Options > General writes DevMode with its current value every GUI frame: only a CHANGED (or failed,
            // or foreign-altered) write is an unobserved safety interval. See SettingWriteBracket.
            harmony.Patch(Exact(typeof(Prefs), "set_DevMode", typeof(bool)),
                prefix: Hook(nameof(BeginDevModeWrite), Priority.First),
                finalizer: Hook(nameof(EndDevModeWrite), Priority.Last));
            harmony.Patch(Exact(typeof(Current), "set_Game", typeof(Game)),
                prefix: Hook(nameof(BeginGameAssignment), Priority.First),
                finalizer: Hook(nameof(EndGameAssignment), Priority.Last));
            foreach (MethodInfo target in TransitionTargets())
                harmony.Patch(target, prefix: Hook(nameof(BeginTransition), Priority.First),
                    finalizer: Hook(nameof(EndTransition), Priority.Last));
            // Every installed fog-bit writer (docs/visibility-audit.md#list-witnesses). Partial
            // installation leaves the counter unavailable: lists then revalidate in one update.
            try
            {
                foreach (MethodInfo target in FogWriters())
                    harmony.Patch(target, prefix: Hook(nameof(FogEdit), Priority.First),
                        finalizer: Hook(nameof(FogEdited), Priority.Last));
                EditCounter.Fog.MarkInstalled();
            }
            catch { /* Counter stays unavailable. */ }
        }

        private static MethodInfo[] FogWriters() => new[]
        {
            Exact(typeof(FogGrid), "UnfogWorker", typeof(IntVec3)),
            Exact(typeof(FogGrid), nameof(FogGrid.Refog), typeof(CellRect)),
            Exact(typeof(FogGrid), "SetAllFogged"),
            Exact(typeof(FogGrid), nameof(FogGrid.ExposeData)),
            Exact(typeof(FogGrid), nameof(FogGrid.Dispose))
        };
        // Before and after (including throws): no write can fall between two equal reads.
        private static void FogEdit() => EditCounter.Fog.Bump();
        private static void FogEdited() => EditCounter.Fog.Bump();

        private static MethodInfo Exact(Type type, string name, params Type[] args) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, args, null) ?? throw new InvalidOperationException();

        private static HarmonyMethod Hook(string name, int priority) => new HarmonyMethod(
            typeof(ModBootstrap).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException()) { priority = priority };

        private static MethodInfo[] TransitionTargets() => new[]
        {
            Exact(typeof(Current), "set_ProgramState", typeof(ProgramState)),
            Exact(typeof(Current), nameof(Current.Notify_LoadedSceneChanged)),
            Exact(typeof(Root), nameof(Root.Start)),
            Exact(typeof(GenScene), nameof(GenScene.GoToMainMenu)),
            Exact(typeof(Game), nameof(Game.InitNewGame)),
            Exact(typeof(Game), nameof(Game.LoadGame)),
            Exact(typeof(Game), nameof(Game.FinalizeInit)),
            Exact(typeof(Game), nameof(Game.Dispose)),
            Exact(typeof(Game), "set_World", typeof(RimWorld.Planet.World)),
            Exact(typeof(Game), nameof(Game.AddMap), typeof(Map)),
            Exact(typeof(Game), nameof(Game.DeinitAndRemoveMap), typeof(Map), typeof(bool)),
            Exact(typeof(Map), nameof(Map.Dispose)),
            Exact(typeof(Verse.Profile.MemoryUtility), nameof(Verse.Profile.MemoryUtility.ClearAllMapsAndWorld)),
            Exact(typeof(LongEventHandler), nameof(LongEventHandler.ClearQueuedEvents)),
            Exact(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), typeof(Action), typeof(string),
                typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool), typeof(Action)),
            Exact(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), typeof(System.Collections.IEnumerable),
                typeof(string), typeof(Action<Exception>), typeof(bool), typeof(bool)),
            Exact(typeof(LongEventHandler), nameof(LongEventHandler.QueueLongEvent), typeof(Action), typeof(string),
                typeof(string), typeof(bool), typeof(Action<Exception>), typeof(bool), typeof(bool))
        };

        private static MethodInfo[] ObservationBoundaryTargets() => new[]
        {
            Exact(typeof(Prefs), "set_UIScale", typeof(float)),
            Exact(typeof(Game), "set_CurrentMap", typeof(Map)),
            Exact(typeof(WindowStack), nameof(WindowStack.Add), typeof(Window)),
            Exact(typeof(WindowStack), nameof(WindowStack.TryRemove), typeof(Window), typeof(bool))
        };

        // Every unobserved callback interval revokes prior leases even when both endpoints
        // look safe. This catches on/off intervals without pretending all direct flag writes
        // have setter hooks. It does NOT certify framebuffer contents or native completion.
        private static void BeginObservationBoundary(out bool __state)
        { transitions.Observations.BeginBoundary(); __state = true; }
        private static void EndObservationBoundary(ref bool __state)
        {
            if (!__state) { transitions.Observations.Invalidate(); return; }
            __state = false;
            transitions.Observations.EndBoundary();
        }
        private static void BeginDevModeWrite(bool value, out SettingWrite __state)
        { __state = SettingWriteBracket.Begin(transitions.Observations, value, Prefs.DevMode); }
        private static void EndDevModeWrite(ref SettingWrite __state, Exception? __exception)
        { SettingWriteBracket.End(transitions.Observations, ref __state, Prefs.DevMode, __exception != null); }
        private static void BeginGuiBoundary(out bool __state)
        {
            NativeRender?.BeginGui();
            transitions.Observations.BeginBoundary(true); __state = true;
        }
        private struct ObservedUpdateState
        {
            internal bool Entered;
            internal long Revision;
        }
        private static void BeforeObservedUpdate(out ObservedUpdateState __state)
        {
            publicEvents?.BeginUpdate();
            screenshots?.BeginUpdate();
            dispatcher?.BeginUpdateAllowance();
            NativeRender?.BeginFrame();
            transitions.Observations.BeginBoundary(true);
            __state = new ObservedUpdateState { Entered = true, Revision = transitions.Revision };
        }

        // Installed Harmony treats __state-only void prefixes as non-skipping observers even
        // when another prefix returns false. Default false state handles earlier prefix throws.
        // Void finalizer preserves original exception/result and balances failed/skipped originals.
        private static void BeginTransition(out bool __state) { transitions.Begin(); __state = true; }
        // UIRoot_Entry.DoMainMenu assigns null on every GUI pass. Inject the exact static
        // backing reference, not a getter/collection read, including on loading workers.
        private static void BeginGameAssignment(Game ___gameInt, Game value, out GameAssignmentState __state) =>
            transitions.BeginGameAssignment(___gameInt, value, out __state);
        private static void EndGameAssignment(ref GameAssignmentState __state) => transitions.FinishGameAssignment(ref __state);
        private static void EndTransition(ref bool __state) => transitions.Finish(ref __state);

        private static void RegisterFallback(Action stop)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, __) => stop();
            AppDomain.CurrentDomain.DomainUnload += (_, __) => stop();
        }

        private static void RegisterQuitting(Action stop) => Application.quitting += stop;

        private static LoopbackTransport Start(CancellationToken cancellation) =>
            LoopbackTransport.Start(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"), cancellation, pending);

        // Keep initial bootstrap on the audited base callback. Dispatch only after the complete
        // derived Update; base postfix runs before UpdatePlay/UpdateEntry. GUI is later still.
        private static void BeforeRootUpdate() => lifecycle.InitializeOnGameThread();

        private static void AfterObservedUpdate(Root __instance, bool ___destroyed,
            ref ObservedUpdateState __state, Exception? __exception, bool __runOriginal)
        {
            if (!__state.Entered) { transitions.Observations.Invalidate(); return; }
            __state.Entered = false; // Finalizer retries must not decrement a bracket twice.
            transitions.Observations.EndBoundary(true);
            if (!lifecycle.IsGameThread) return;
            LoopbackTransport? session = Session;
            if (session == null) return;
            if (dispatcher == null) dispatcher = CreateDispatcher(session.Session);
            if (NativeRender == null)
            {
                try
                {
                    NativeRender = __instance.gameObject.AddComponent<GameNativeRender>();
                    NativeRender.Initialize(transitions.Observations, SampleRender);
                }
                catch
                {
                    if (NativeRender != null) UnityEngine.Object.Destroy(NativeRender);
                    NativeRender = null;
                }
            }
            long revision = __state.Revision;
            dispatcher.Pump(budget => __exception == null ? Sample(__instance, ___destroyed, revision, budget)
                : new ReadFrame<Map>(null, null, null, false));
            NativeRender?.EndUpdate(__exception == null && __runOriginal);
        }

        private static void AfterRootGui(Root __instance, bool ___destroyed, ref bool __state, Exception? __exception, bool __runOriginal)
        {
            bool entered = __state;
            __state = false;
            if (entered) transitions.Observations.EndBoundary(true);
            else transitions.Observations.Invalidate();
            if (!entered || !lifecycle.IsGameThread || dispatcher == null) return;
            long revision = transitions.Revision;
            // Boundary sample only: never multiply per-update request/producer allowance by
            // OnGUI layout/repaint/input passes. A returned GUI call is not a completed frame.
            dispatcher.SampleBoundary(budget => __exception == null ? Sample(__instance, ___destroyed, revision, budget)
                : new ReadFrame<Map>(null, null, null, false));
            NativeRender?.EndGui(__exception == null && __runOriginal,
                Event.current != null && Event.current.type == EventType.Repaint);
        }

        private static DeveloperSafety SampleDeveloperSafety(UpdateBudget budget)
        {
            if (!budget.Available || safetySource == null) return DeveloperSafety.Unknown;
            try
            {
                if (safetySampler == null)
                    safetySampler = new DeveloperSafetySampler<Window>(safetySource);
                return safetySampler.Sample(budget);
            }
            catch { return DeveloperSafety.Unknown; }
        }

        // One-time exact binding on the game thread. Failure leaves every map inaccessible.
        private static Func<Map, bool>? MapAccess()
        {
            if (mapAccessBound) return mapAccess;
            mapAccessBound = true;
            try
            {
                targetAccess = new GameTargetAccess(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit());
                mapAccess = targetAccess.MapAccessible;
            }
            catch { mapAccess = null; targetAccess = null; }
            return mapAccess;
        }

        // Routes are fixed here. Only audited producers whose exact bindings resolved are
        // registered; every other ordinary method stays unavailable/unverified.
        private static ReadDispatcher<Map> CreateDispatcher(string session)
        {
            var routes = new List<(ReadMethod, IReadProducer<Map>)>();
            MapAccess();
            if (targetAccess is GameTargetAccess access)
            {
                try { routes.Add((ReadMethod.Summary, new GameSummaryProducer(access, new GameProjectionPatchAudit()))); }
                catch { /* Summary stays unavailable. */ }
                try
                {
                    var reader = new GameOwnPawnReader(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit());
                    GameNeedsReader? needs = null;
                    try { needs = new GameNeedsReader(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit()); }
                    catch { /* Needs family stays omitted from pawn detail. */ }
                    GameHealthReader? health = null;
                    try { health = new GameHealthReader(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit()); }
                    catch { /* Health family stays omitted from pawn detail. */ }
                    GameGearReader? gear = null;
                    try { gear = new GameGearReader(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit()); }
                    catch { /* Gear family stays omitted from pawn detail. */ }
                    GameJobsReader? jobs = null;
                    try { jobs = new GameJobsReader(Thread.CurrentThread.ManagedThreadId, new GameProjectionPatchAudit(), access, reader, gear); }
                    catch { /* Public job/lord families stay omitted from pawn detail. */ }
                    var list = new GameOwnPawnsProducer(access, reader);
                    var detail = new GameOwnPawnProducer(access, reader, needs, health, gear, jobs);
                    routes.Add((ReadMethod.Pawns, list)); routes.Add((ReadMethod.Pawn, detail));
                }
                catch { /* Own-pawn routes stay unavailable. */ }
                try
                {
                    var patches = new GameProjectionPatchAudit();
                    var reader = new GameOwnPawnReader(Thread.CurrentThread.ManagedThreadId, patches);
                    var labels = new GameGearReader(Thread.CurrentThread.ManagedThreadId, patches);
                    routes.Add((ReadMethod.Things, new GameThingsProducer(access, reader, labels, patches, false)));
                    routes.Add((ReadMethod.Resources, new GameThingsProducer(access, reader, labels, patches, true)));
                    routes.Add((ReadMethod.Ui, new GameUiProducer(access, reader, labels, patches)));
                }
                catch { /* Unbound thing/resource/UI routes stay unavailable. */ }
            }
            try
            {
                publicEvents = new GamePublicEvents(EventScope);
                publicEvents.Install();
                transitions.Observations.Register(publicEvents);
                routes.Add((ReadMethod.Events, publicEvents.Producer));
            }
            catch { publicEvents?.Disable(); publicEvents = null; /* Partial hook setup never enables events. */ }
            screenshots = new ScreenshotCapture(new GameScreenshotBackend(() => NativeRender), transitions);
            routes.Add((ReadMethod.Screenshot, new ScreenshotProducer<Map>(screenshots)));
            var result = new ReadDispatcher<Map>(session, pending, transitions, null, routes.ToArray());
            result.ExtraWorkTicks = () => (publicEvents?.SpentTicks ?? 0) + (screenshots?.SpentTicks ?? 0);
            result.ExtraCandidates = () => publicEvents?.Candidates ?? 0;
            result.ExtraNestedFields = () => publicEvents?.NestedFields ?? 0;
            return result;
        }

        // Fresh publication evidence during the original Update (whose render/read leases
        // are deliberately revoked). No map/target enumeration or epoch reuse here.
        private static Scope? EventScope(UpdateBudget budget)
        {
            if (!lifecycle.IsGameThread || dispatcher == null || !transitions.TryRead()) return null;
            try
            {
                if (SampleDeveloperSafety(budget) != DeveloperSafety.Safe || LongEventHandler.AnyEventNowOrWaiting
                    || Scribe.mode != LoadSaveMode.Inactive || !budget.Available) return null;
                Root root = Current.Root;
                if (root == null || root.uiRoot == null) return null;
                Game? game = Current.Game;
                bool ready = Current.ProgramState == ProgramState.Entry && root is Root_Entry && GenScene.InEntryScene && game == null
                    || Current.ProgramState == ProgramState.Playing && root is Root_Play && GenScene.InPlayScene && game != null;
                return ready && dispatcher.Generations.SameSources(root, game, transitions.Revision) && budget.Available
                    ? dispatcher.Generations.Scope : null;
            }
            finally { transitions.ReleaseRead(); }
        }

        // Same finite named safety/readiness/source closure at both ends of every witnessed
        // render callback. No map enumeration or quota-multiplying dispatch from render hooks.
        private static RenderSample SampleRender(UpdateBudget budget)
        {
            if (!lifecycle.IsGameThread || dispatcher == null || !transitions.TryRead())
                return new RenderSample(null, null, WireError.NotReady);
            try
            {
                DeveloperSafety safety = SampleDeveloperSafety(budget);
                if (safety != DeveloperSafety.Safe)
                    return new RenderSample(null, null, WireError.ObservationRefused);
                if (LongEventHandler.AnyEventNowOrWaiting || Scribe.mode != LoadSaveMode.Inactive || !budget.Available)
                    return new RenderSample(null, null, WireError.NotReady);
                Root root = Current.Root;
                Game? game = Current.Game;
                bool ready = root != null && root.uiRoot != null && (
                    Current.ProgramState == ProgramState.Entry && root is Root_Entry && GenScene.InEntryScene && game == null
                    || Current.ProgramState == ProgramState.Playing && root is Root_Play && GenScene.InPlayScene && game != null);
                Scope? scope = ready && dispatcher.Generations.SameSources(root!, game, transitions.Revision)
                    ? dispatcher.Generations.Scope : null;
                var geometry = new FrameGeometry(Time.frameCount, Screen.width, Screen.height, Prefs.UIScale,
                    game != null && game.CurrentMap != null && RimWorld.Planet.WorldRendererUtility.DrawingMap);
                return scope != null && geometry.Valid && budget.Available
                    ? new RenderSample(scope, geometry, null) : new RenderSample(null, null, WireError.NotReady);
            }
            finally { transitions.ReleaseRead(); }
        }

        private static ReadFrame<Map> Sample(Root root, bool destroyed, long beforeUpdate, UpdateBudget budget)
        {
            // First gate before even obtaining Game.Maps. destroyed is audited Root field
            // injected by Harmony, not a generic reflection/read endpoint.
            DeveloperSafety safety = SampleDeveloperSafety(budget);
            bool loading = LongEventHandler.AnyEventNowOrWaiting;
            if (Readiness.Check(loading, !destroyed && root != null && ReferenceEquals(root, Current.Root)
                && root.uiRoot != null, beforeUpdate != transitions.Revision,
                false, false, false, false, false).HasValue)
                return new ReadFrame<Map>(null, null, null, false, safety);
            if (!budget.Available) return new ReadFrame<Map>(null, null, null, false, safety);
            var geometry = new FrameGeometry(Time.frameCount, Screen.width, Screen.height, Prefs.UIScale);
            ProgramState state = Current.ProgramState;
            if (state == ProgramState.Entry && root is Root_Entry && GenScene.InEntryScene)
                return new ReadFrame<Map>(root, null, null, true, safety, geometry);
            if (state != ProgramState.Playing || !(root is Root_Play) || !GenScene.InPlayScene || Current.Game == null)
                return new ReadFrame<Map>(null, null, null, false, safety);
            Game game = Current.Game;
            return new ReadFrame<Map>(root, game, game.Maps, true, safety, geometry, MapAccess());
        }
    }
}
