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
    // `summary` producer (docs/visibility-audit.md#summary). Game thread only, inside the
    // dispatcher's fence/safety/scope admission. Header facts come from plain fields and
    // closure-checked field getters, all read in the releasing update; per-map own-pawn counts
    // traverse bounded witnessed lists across updates and are rechecked before release.
    internal sealed class GameSummaryProducer : IReadProducer<Map>
    {
        private static readonly Type[] NamedParents = { typeof(Settlement), typeof(ArchotechSettlement) };
        private readonly GameTargetAccess access;
        private readonly IProjectionPatchAudit patches;
        private readonly MethodBase[] closure, pausedClosure;
        private readonly TypeAdmission pawns = new TypeAdmission(typeof(Pawn));

        internal GameSummaryProducer(GameTargetAccess access, IProjectionPatchAudit patches)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.patches = patches ?? throw new ArgumentNullException(nameof(patches));
            closure = new MethodBase[]
            {
                StaticGetter(typeof(VersionControl), "CurrentVersionStringWithRev"),
                Getter(typeof(TickManager), "TicksGame"), Getter(typeof(Game), "CurrentMap"),
                Getter(typeof(MapInfo), "Size"), Getter(typeof(Settlement), "Name"),
                StaticGetter(typeof(LoadedModManager), "RunningModsListForReading"),
                Getter(typeof(ModContentPack), "PackageIdPlayerFacing"), Getter(typeof(ModContentPack), "PackageId"),
                Getter(typeof(ModContentPack), "Name"), Getter(typeof(ModContentPack), "IsOfficialMod"),
                Getter(typeof(ModContentPack), "IsCoreMod")
            };
            // TickManager.Paused: time speed field, or forced pause from open windows, long
            // events, tile picker, gravship cutscene/landing confirmation or map-gen debug.
            pausedClosure = new MethodBase[]
            {
                Getter(typeof(TickManager), "Paused"), Getter(typeof(TickManager), "ForcePaused"),
                StaticGetter(typeof(Find), "WindowStack"), StaticGetter(typeof(Find), "UIRoot"),
                StaticGetter(typeof(Current), "Root"), Getter(typeof(WindowStack), "WindowsForcePause"),
                StaticGetter(typeof(LongEventHandler), "ForcePause"), StaticGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"),
                StaticGetter(typeof(Find), "TilePicker"), StaticGetter(typeof(Find), "WorldInterface"),
                StaticGetter(typeof(Find), "World"), StaticGetter(typeof(Current), "Game"), StaticGetter(typeof(Current), "CreatingWorld"),
                Getter(typeof(Game), "World"), Getter(typeof(TilePicker), "Active"),
                StaticGetter(typeof(Find), "GravshipController"),
                GenericComponentGetter().MakeGenericMethod(typeof(WorldComponent_GravshipController)),
                StaticGetter(typeof(WorldComponent_GravshipController), "CutsceneInProgress"),
                Getter(typeof(WorldComponent_GravshipController), "LandingAreaConfirmationInProgress")
            };
        }

        public bool SnapshotBuild => false;
        public IReadContinuation<Map> Start(Request request) => new Continuation(this);

        private bool Unpatched(MethodBase[] methods)
        {
            try { foreach (MethodBase method in methods) if (!patches.IsUnpatched(method)) return false; }
            catch { return false; }
            return true;
        }

        private static MethodInfo GenericComponentGetter()
        {
            foreach (MethodInfo method in typeof(World).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.Name == "GetComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                    return method;
            throw new InvalidOperationException("Summary binding unavailable.");
        }
        private static MethodInfo Getter(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                ?.GetGetMethod() ?? throw new InvalidOperationException("Summary binding unavailable.");
        private static MethodInfo StaticGetter(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                ?.GetGetMethod() ?? throw new InvalidOperationException("Summary binding unavailable.");

        // One map's witnessed own-pawn count. Omitted (null) rather than partial when it cannot
        // be completed and proven consistent within its bounds.
        private sealed class MapCount
        {
            internal const int MaxRestarts = 2;
            internal readonly Map Map;
            internal readonly string Id;
            internal GameListSource<Pawn>? Source;
            internal object? Start;
            internal long Revision;
            internal bool Witnessed, Done, Omitted;
            internal int Index, Count, Restarts;
            internal MapCount(Map map, string id) { Map = map; Id = id; }
            internal void Restart()
            {
                Source = null; Start = null; Done = false; Index = 0; Count = 0;
                if (++Restarts > MaxRestarts) Omitted = true;
            }
            // Same raw list since start, and either an unchanged eligibility revision or the
            // whole traversal inside this one update.
            internal bool Consistent(object update)
            {
                if (Source == null || !Source.TryStructure(out long structure) || structure != 0) return false;
                bool witnessed = Source.TryEligibility(out long revision);
                return witnessed == Witnessed && (Witnessed ? revision == Revision : ReferenceEquals(Start, update));
            }
        }

        private sealed class Continuation : IReadContinuation<Map>
        {
            private readonly GameSummaryProducer owner;
            private List<MapCount>? counts;
            private JObject? result;
            private List<ModContentPack>? packs;
            private int packCount, packIndex;
            private JArray? modRows, dlcRows;
            private bool manifestDone;
            internal Continuation(GameSummaryProducer owner) { this.owner = owner; }

            public StepResult Step(ProducerWork<Map> work)
            {
                ReadFrame<Map> frame = work.Frame!;
                Game? game = frame.Game as Game;
                if (counts == null)
                {
                    counts = new List<MapCount>();
                    if (game != null && frame.Maps != null)
                        foreach (Map map in frame.Maps)
                            if (work.Generations.MapScope(map)?.Map is string id) counts.Add(new MapCount(map, id));
                }
                foreach (MapCount count in counts)
                    if (!count.Done && !count.Omitted && !Traverse(count, game!, work)) return StepResult.Continue;
                foreach (MapCount count in counts) if (!count.Done && !count.Omitted) return StepResult.Continue;
                // Final consistency in the completing update; a changed list/witness restarts it.
                bool restarted = false;
                foreach (MapCount count in counts)
                    if (count.Done && !count.Consistent(frame)) { count.Restart(); restarted = true; }
                if (restarted) return StepResult.Continue;
                result = Header(work, game);
                return result == null ? StepResult.Continue : StepResult.Complete;
            }

            // False = yielded/failed this update; progress kept for the next update.
            private bool Traverse(MapCount count, Game game, ProducerWork<Map> work)
            {
                if (count.Source == null)
                {
                    List<Pawn>? list = owner.access.SpawnedPawns(count.Map);
                    if (list == null || list.Count > WireLimits.SnapshotItems) { count.Omitted = true; return true; }
                    count.Source = new GameListSource<Pawn>(owner.access, count.Map, game, owner.pawns, owner.access.SpawnedPawns);
                    if (count.Source.Player == null) { count.Omitted = true; return true; } // Never "zero own pawns".
                    count.Witnessed = count.Source.TryEligibility(out count.Revision);
                    count.Start = work.Frame;
                }
                GameListSource<Pawn> source = count.Source;
                if (!source.TryStructure(out long structure) || structure != 0) { count.Restart(); return true; }
                while (count.Index < source.List.Count)
                {
                    if (work.Candidates >= WireLimits.ScanCandidates) { count.Omitted = true; return true; }
                    if (!work.TryCandidate()) return false;
                    Pawn pawn = source.List[count.Index];
                    if (pawn == null) { count.Omitted = true; return true; }
                    bool witnessed;
                    if (!owner.access.OwnPawn(pawn, source.Player))
                        witnessed = source.Witness(pawn); // Not counted; its inputs must still hold.
                    else
                    {
                        TargetGateResult gate = source.Gate(pawn, work, out witnessed);
                        if (gate == TargetGateResult.Deferred) return false;
                        if (witnessed && gate == TargetGateResult.Eligible) count.Count++;
                    }
                    if (!witnessed) { count.Omitted = true; return true; }
                    count.Index++;
                }
                count.Done = true;
                return true;
            }

            private JObject? Header(ProducerWork<Map> work, Game? game)
            {
                ReadFrame<Map> frame = work.Frame!;
                Scope scope = work.Scope;
                if (frame.Geometry == null || !owner.Unpatched(owner.closure)) { work.Fail(WireError.Unavailable); return null; }
                if (!work.TryNestedFields(1)) return null;
                string? build = SummaryProjection.Text(VersionControl.CurrentVersionStringWithRev, PrimitiveProjection.MaxLabelChars);

                if (build == null) { work.Fail(WireError.Unavailable); return null; }
                long? tick = null; bool? paused = null; string? currentMap = null;
                List<MapSummaryRow>? maps = null;
                if (game != null)
                {
                    TickManager? ticks = game.tickManager;
                    if (ticks == null) { work.Fail(WireError.Unavailable); return null; }
                    tick = ticks.TicksGame;
                    if (tick < 0) tick = null;
                    if (owner.Unpatched(owner.pausedClosure))
                        try { paused = ticks.Paused; }
                        catch { paused = null; } // Unavailable, never assumed running/paused.
                    Map? current = game.CurrentMap;
                    if (current != null) currentMap = work.Generations.MapScope(current)?.Map;
                    if (counts!.Count <= SummaryProjection.MaxMaps)
                    {
                        maps = new List<MapSummaryRow>(counts.Count);
                        foreach (MapCount count in counts)
                        {
                            if (!work.TryNestedFields(2)) return null;
                            if (work.Generations.MapScope(count.Map)?.Map != count.Id) { work.Fail(WireError.Unavailable); return null; }
                            string? label = MapLabel(count.Map);
                            if (label == null) { work.Fail(WireError.Unavailable); return null; }
                            IntVec3 size = count.Map.info.Size;
                            maps.Add(new MapSummaryRow(count.Id, label, SummaryProjection.Dimension(size.x),
                                SummaryProjection.Dimension(size.z), count.Done && !count.Omitted ? count.Count : (int?)null));
                        }
                    }
                }
                if (!Manifest(work)) return null;
                JObject summary;
                try
                {
                    summary = SummaryProjection.Build(new SampleStamp(scope, frame.Geometry.Frame, tick), build, paused,
                        currentMap, maps, dlcRows?.Count, modRows?.Count, dlcRows, modRows);
                }
                catch (ArgumentException) { work.Fail(WireError.Unavailable); return null; }
                int bytes = Encoding.UTF8.GetByteCount(summary.ToString(Formatting.None));
                if (work.TryRetain(bytes)) return summary;
                // Yielded after the DTO was built: detach the staged manifest rows so the retry reuses them.
                SummaryProjection.DetachManifests(summary);
                return null;
            }

            // Running content packs in load order: player-facing package ID and name fields. DLCs
            // are the official non-core packs. Any invalid entry omits both lists. One charged
            // field per pack and the finished JSON rows are kept across yields: the running pack
            // list is fixed for the process, and a replaced list or changed length restarts.
            private bool Manifest(ProducerWork<Map> work)
            {
                if (manifestDone) return true;
                List<ModContentPack>? running = LoadedModManager.RunningModsListForReading;
                if (!ReferenceEquals(running, packs) || (running != null && running.Count != packCount))
                { packs = running; packCount = running?.Count ?? 0; packIndex = 0; modRows = null; dlcRows = null; }
                if (running == null || running.Count > SummaryProjection.MaxMods) { manifestDone = true; return true; }
                if (modRows == null || dlcRows == null) { modRows = new JArray(); dlcRows = new JArray(); }
                for (; packIndex < running.Count; packIndex++)
                {
                    if (!work.TryNestedFields(1)) return false;
                    ModContentPack pack = running[packIndex];
                    string? id = pack == null ? null : SummaryProjection.Text(pack.PackageIdPlayerFacing, 256);
                    string? label = pack == null ? null : SummaryProjection.Text(pack.Name, PrimitiveProjection.MaxLabelChars);
                    if (id == null || label == null) { modRows = null; dlcRows = null; manifestDone = true; return true; }
                    var row = new ManifestRow(id, label);
                    modRows.Add(SummaryProjection.ManifestRowJson(row));
                    if (pack!.IsOfficialMod && !pack.IsCoreMod) dlcRows.Add(SummaryProjection.ManifestRowJson(row));
                }
                if (dlcRows.Count > SummaryProjection.MaxDlcs) dlcRows = null;
                manifestDone = true;
                return true;
            }

            // Exact parent types only: settlements show their name field, base-label parents
            // their definition label. Other parents (sites, space maps, mods) override Label with
            // unaudited logic, so they also use the public world-object definition label.
            private static string? MapLabel(Map map)
            {
                MapParent? parent = map.info?.parent;
                if (parent == null || parent.def == null) return null;
                Type type = parent.GetType();
                if (Array.IndexOf(NamedParents, type) >= 0)
                {
                    string? name = SummaryProjection.Text(((Settlement)parent).Name, PrimitiveProjection.MaxLabelChars);
                    if (name != null) return name;
                }
                return SummaryProjection.DefinitionLabel(parent.def.label);
            }

            public bool Eligible(ProducerWork<Map> work)
            {
                if (result == null || counts == null) return false;
                foreach (MapCount count in counts)
                {
                    if (work.Generations.MapScope(count.Map)?.Map != count.Id) return false;
                    if (count.Done && !count.Omitted && !count.Consistent(work.Frame!)) return false;
                }
                return true;
            }

            public JObject Result() => result ?? throw new InvalidOperationException("Summary incomplete.");
            public void Discard() { counts = null; result = null; packs = null; modRows = null; dlcRows = null; }
        }
    }
}
