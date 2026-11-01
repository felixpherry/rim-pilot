using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Whole UI result is built in the releasing update. No cross-update selection decisions,
    // scratch getters, virtual window properties, draw calls or world-object/holder expansion.
    internal sealed class GameUiProducer : IReadProducer<Map>
    {
        private readonly GameTargetAccess access;
        private readonly GameOwnPawnReader pawns;
        private readonly GameGearReader labels;
        private readonly IProjectionPatchAudit patches;
        private readonly GameKeyedText text = new GameKeyedText();
        private readonly MethodInfo[] closure = {
            GameKeyedText.Getter(typeof(Selector), "SelectedObjectsListForReading"),
            GameKeyedText.Getter(typeof(WindowStack), "Count"), GameKeyedText.Getter(typeof(WindowStack), "Item"),
            GameKeyedText.Getter(typeof(Game), "CurrentMap"), GameKeyedText.Getter(typeof(Game), "World"),
            GameKeyedText.Getter(typeof(Thing), "Stuff")
        };
        private readonly TypeAdmission types = new TypeAdmission(typeof(Thing), typeof(ThingWithComps), typeof(Pawn),
            typeof(Building), typeof(Building_Storage), typeof(Building_Door), typeof(Building_Bed),
            typeof(Building_WorkTable), typeof(Building_ResearchBench), typeof(Building_TurretGun), typeof(Plant), typeof(Apparel));

        internal GameUiProducer(GameTargetAccess access, GameOwnPawnReader pawns, GameGearReader labels, IProjectionPatchAudit patches)
        { this.access = access; this.pawns = pawns; this.labels = labels; this.patches = patches; }
        private bool Fresh()
        {
            foreach (MethodInfo method in closure) if (!patches.IsUnpatched(method)) return false;
            return pawns.Fresh() && labels.Fresh();
        }
        public bool SnapshotBuild => false;
        public IReadContinuation<Map> Start(Request request) => new Continuation(this);

        // Exact runtime types only. Kind describes an open ordinary window, not rendered text
        // or visible controls. Custom subclasses, FloatMenu, letters and specialist dialogs
        // have no inferred title/content/type-name fallback.
        private static string? WindowKind(Window window)
        {
            Type type = window.GetType();
            if (type == typeof(Dialog_Options)) return "options";
            if (type == typeof(Dialog_MessageBox)) return "message_box";
            if (type == typeof(MainTabWindow_Menu)) return "menu";
            if (type == typeof(MainTabWindow_Architect)) return "architect";
            if (type == typeof(MainTabWindow_Work)) return "work";
            if (type == typeof(MainTabWindow_Schedule)) return "schedule";
            if (type == typeof(MainTabWindow_Assign)) return "assign";
            if (type == typeof(MainTabWindow_Animals)) return "animals";
            if (type == typeof(MainTabWindow_Wildlife)) return "wildlife";
            if (type == typeof(MainTabWindow_Mechs)) return "mechs";
            if (type == typeof(MainTabWindow_Research)) return "research";
            if (type == typeof(MainTabWindow_Quests)) return "quests";
            if (type == typeof(MainTabWindow_History)) return "history";
            if (type == typeof(MainTabWindow_Factions)) return "factions";
            if (type == typeof(MainTabWindow_Ideos)) return "ideos";
            if (type == typeof(MainTabWindow_Inspect)) return "inspect";
            return null;
        }

        private sealed class Continuation : IReadContinuation<Map>
        {
            private readonly GameUiProducer owner;
            private JObject? result;
            internal Continuation(GameUiProducer owner) { this.owner = owner; }
            public StepResult Step(ProducerWork<Map> work) => StepResult.Complete;
            public bool Eligible(ProducerWork<Map> work)
            {
                result = null; work.ReleaseMemory();
                try { return Build(work); }
                catch { return work.Fail(WireError.Unavailable); }
            }
            private bool Build(ProducerWork<Map> work)
            {
                ReadFrame<Map> frame = work.Frame!;
                if (frame.Geometry == null || !(frame.Root is Root root) || root.uiRoot?.windows == null
                    || !owner.Fresh()) return work.Fail(WireError.Unavailable);
                WindowStack stack = root.uiRoot.windows;
                int count = stack.Count;
                // Bounds apply to raw candidates, before filtering; never return a scan prefix.
                if (count < 0 || count > UiProjection.MaxWindows) return work.Fail(WireError.Unavailable);
                var windows = new JArray();
                for (int i = 0; i < count; i++)
                {
                    if (!work.TryCandidate() || !work.TryNestedFields(1)) return false;
                    Window window = stack[i];
                    if (window == null) return work.Fail(WireError.Unavailable);
                    string? kind = WindowKind(window);
                    if (kind != null && !Retain(work, windows, UiProjection.Window(kind))) return false;
                }
                Game? game = frame.Game as Game;
                JArray? selection = null;
                // World selection is a separate unsupported schema. No world target labels.
                if (game != null && root.uiRoot is UIRoot_Play play && game.World?.renderer != null
                    && game.World.renderer.wantedMode != WorldRenderMode.Planet)
                {
                    Map? map = game.CurrentMap;
                    if (map != null)
                    {
                        Scope? mapScope = work.Generations.MapScope(map);
                        if (mapScope == null || (work.Scope.Map != null && work.Scope.Map != mapScope.Map))
                            return work.Fail(WireError.Unavailable);
                        List<object>? raw = play.mapUI?.selector?.SelectedObjectsListForReading;
                        if (raw == null || raw.Count > UiProjection.MaxSelection) return work.Fail(WireError.Unavailable);
                        var version = raw.GetEnumerator();
                        selection = new JArray();
                        for (int i = 0; i < raw.Count; i++)
                        {
                            if (!work.TryCandidate()) return false;
                            if (!(raw[i] is Thing thing)) continue; // Zones/plans: no audited schema.
                            TargetGateResult gate = owner.access.Thing(thing, map, game, owner.types, work);
                            if (gate == TargetGateResult.Deferred) return false;
                            if (gate != TargetGateResult.Eligible) continue;
                            int cost = thing is Pawn ? GameOwnPawnReader.NestedFields
                                : thing is ThingWithComps twc ? owner.labels.LabelCost(twc, game) : 8;
                            if (!work.TryNestedFields(cost + 4)) return false;
                            ThingDef def = thing.def;
                            if (def.GetType() != typeof(ThingDef) || def.thingClass != thing.GetType())
                                return work.Fail(WireError.Unavailable);
                            string? label;
                            if (thing is Pawn pawn) label = owner.pawns.LabelShort(pawn) is string shortLabel
                                ? PawnProjection.Label("", shortLabel) : null;
                            else if (thing is ThingWithComps withComps) label = owner.labels.LabelCap(withComps, game);
                            else
                            {
                                if (thing.GetType() != typeof(Thing) || thing.Stuff != null || (def.useHitPoints && def.stackLimit == 1))
                                    return work.Fail(WireError.Unavailable);
                                label = owner.text.Cap(def.label, def);
                                if (thing.stackCount > 1) label += " x" + thing.stackCount.ToString();
                            }
                            if (JobProjection.Public(label) == null) return work.Fail(WireError.Unavailable);
                            // Non-own pawn may have a selection label, but never a pawn.get ref.
                            bool own = thing is Pawn ownPawn && owner.access.OwnPawn(ownPawn, game.World?.factionManager?.OfPlayer);
                            string? id = work.Generations.Reference(own ? "p" : "t", thing, map);
                            if (id == null) return work.Fail(WireError.Unavailable);
                            // Budget yield is not a projection failure. Rebuild the WHOLE UI
                            // next update; never keep partially sampled selection/windows.
                            if (!Retain(work, selection, UiProjection.Selection(label!, id, own))) return false;
                        }
                        try { version.MoveNext(); } catch (InvalidOperationException) { return work.Fail(WireError.Unavailable); }
                    }
                }
                if (stack.Count != count || !owner.Fresh()) return work.Fail(WireError.Unavailable);
                JObject value = UiProjection.Build(new SampleStamp(work.Scope, frame.Geometry.Frame,
                    game == null ? null : owner.pawns.Tick(game)), selection, windows);
                if (!work.TryRetain(Encoding.UTF8.GetByteCount(value.ToString(Formatting.None)))) return false;
                result = value;
                return true;
            }
            private static bool Retain(ProducerWork<Map> work, JArray array, JObject row)
            {
                if (!work.TryRetain(Encoding.UTF8.GetByteCount(row.ToString(Formatting.None)))) return false;
                array.Add(row); return true;
            }
            public JObject Result() => result ?? throw new InvalidOperationException("UI incomplete.");
            public void Discard() => result = null;
        }
    }
}
