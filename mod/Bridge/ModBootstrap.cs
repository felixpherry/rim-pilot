using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
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
        private static readonly ProcessLifecycle<LoopbackTransport> lifecycle =
            new ProcessLifecycle<LoopbackTransport>(InstallPatches, RegisterFallback, RegisterQuitting, Start);

        internal static void Bootstrap() => lifecycle.Bootstrap();
        internal static LoopbackTransport? Session => lifecycle.IsGameThread ? lifecycle.Session : null;

        private static void InstallPatches()
        {
            // Exact metadata lookup only. No Current/Find/Prefs, Unity objects or game getters.
            // Explicit patch rather than PatchAll avoids accidentally activating future routes.
            var harmony = new Harmony(HarmonyId);
            harmony.Patch(Exact(typeof(Root), nameof(Root.Update), Type.EmptyTypes),
                prefix: Hook(nameof(BeforeRootUpdate), Priority.First),
                postfix: Hook(nameof(AfterRootUpdate), Priority.Last));
            harmony.Patch(Exact(typeof(Current), "set_Game", typeof(Game)),
                prefix: Hook(nameof(BeginGameAssignment), Priority.First),
                finalizer: Hook(nameof(EndGameAssignment), Priority.Last));
            foreach (MethodInfo target in TransitionTargets())
                harmony.Patch(target, prefix: Hook(nameof(BeginTransition), Priority.First),
                    finalizer: Hook(nameof(EndTransition), Priority.Last));
        }

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

        // Unity invokes Root.Update on the game thread, including menus/pause/loading. Both
        // installed Root_Entry and Root_Play call base.Update. No root reference is retained.
        // Neither callback proves end-of-render; base postfix precedes derived Update and GUI.
        private static void BeforeRootUpdate(out long __state)
        {
            lifecycle.InitializeOnGameThread();
            __state = transitions.Revision;
        }

        private static void AfterRootUpdate(Root __instance, bool ___destroyed, long __state)
        {
            if (!lifecycle.IsGameThread) return;
            LoopbackTransport? session = Session;
            if (session == null) return;
            if (dispatcher == null) dispatcher = new ReadDispatcher<Map>(session.Session, pending, transitions);
            dispatcher.Pump(() => Sample(__instance, ___destroyed, __state));
        }

        private static ReadFrame<Map> Sample(Root root, bool destroyed, long beforeUpdate)
        {
            // First gate before even obtaining Game.Maps. destroyed is audited Root field
            // injected by Harmony, not a generic reflection/read endpoint.
            bool loading = LongEventHandler.AnyEventNowOrWaiting;
            if (Readiness.Check(loading, !destroyed && root != null && ReferenceEquals(root, Current.Root)
                && root.uiRoot != null, beforeUpdate != transitions.Revision,
                false, false, false, false, false).HasValue)
                return new ReadFrame<Map>(null, null, null, false);
            ProgramState state = Current.ProgramState;
            if (state == ProgramState.Entry && root is Root_Entry && GenScene.InEntryScene)
                return new ReadFrame<Map>(root, null, null, true);
            if (state != ProgramState.Playing || !(root is Root_Play) || !GenScene.InPlayScene || Current.Game == null)
                return new ReadFrame<Map>(null, null, null, false);
            Game game = Current.Game;
            return new ReadFrame<Map>(root, game, game.Maps, true);
        }
    }
}
