using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Exact normal-label providers. No inspect strings, holder traversal, ResourceCounter or
    // minified contents. Additional specialized/custom classes need their own visibility audit.
    internal sealed class GameThingsProducer : IReadProducer<Map>
    {
        private readonly GameTargetAccess access;
        private readonly GameOwnPawnReader pawns;
        private readonly GameGearReader labels;
        private readonly GameKeyedText text = new GameKeyedText();
        private readonly IProjectionPatchAudit patches;
        private readonly bool resources;
        private readonly TypeAdmission types = new TypeAdmission(typeof(Thing), typeof(ThingWithComps), typeof(Pawn),
            typeof(Building), typeof(Building_Storage), typeof(Building_Door), typeof(Building_Bed),
            typeof(Building_WorkTable), typeof(Building_ResearchBench), typeof(Building_TurretGun), typeof(Plant), typeof(Apparel));
        private readonly MethodInfo resourceGetter = GameKeyedText.Getter(typeof(ThingDef), "CountAsResource");

        internal GameThingsProducer(GameTargetAccess access, GameOwnPawnReader pawns, GameGearReader labels,
            IProjectionPatchAudit patches, bool resources)
        { this.access = access; this.pawns = pawns; this.labels = labels; this.patches = patches; this.resources = resources; }
        private bool Fresh() => labels.Fresh() && pawns.Fresh() && (!resources || patches.IsUnpatched(resourceGetter));
        public bool SnapshotBuild => true;
        public IReadContinuation<Map> Start(Request request) => new Continuation(this);

        private sealed class Continuation : IReadContinuation<Map>
        {
            private readonly GameThingsProducer owner;
            private readonly List<JObject> items = new List<JObject>();
            private readonly List<object> members = new List<object>();
            private ThingProjection.Resources totals = new ThingProjection.Resources();
            private GameListSource<Thing>? list;
            private ConsistentSnapshotSource? source;
            private Map? map;
            private Scope? scope;
            private Revalidation? pass;
            private JObject? result;
            private int index, bytes, start, restarts;
            private bool paging;
            private ReadMethod Method => owner.resources ? ReadMethod.Resources : ReadMethod.Things;
            internal Continuation(GameThingsProducer owner) { this.owner = owner; }

            public StepResult Step(ProducerWork<Map> work)
            {
                if (map == null && !Bind(work)) return StepResult.Continue;
                if (pass == null && !Build(work)) return StepResult.Continue;
                Revalidated valid = work.Generations.Snapshots.Revalidate(pass!, work.Frame!, work);
                if (valid == Revalidated.Expired) { Restart(work); return StepResult.Continue; }
                return valid == Revalidated.Valid ? StepResult.Complete : StepResult.Continue;
            }

            private bool Bind(ProducerWork<Map> work)
            {
                Request request = work.Request;
                if (!(work.Frame!.Game is Game) || request.Scope == null) return work.Fail(WireError.Unavailable);
                map = work.Generations.ResolveMap(request.Scope);
                scope = map == null ? null : work.Generations.MapScope(map);
                if (map == null || scope == null || !scope.Same(request.Scope)) { map = null; return work.Fail(WireError.Unavailable); }
                if (request.Cursor == null) return true;
                paging = true;
                if (!work.Generations.Snapshots.TryResolve(scope, Method, request.Limit, request.Cursor,
                    out EligibleSnapshot? snapshot, out start)) return work.Fail(WireError.CursorExpired);
                pass = work.Generations.Snapshots.BeginRevalidation(snapshot!);
                return true;
            }

            private bool Build(ProducerWork<Map> work)
            {
                Game game = (Game)work.Frame!.Game!;
                if (list == null)
                {
                    List<Thing>? raw = owner.access.AllThings(map!);
                    if (raw == null || raw.Count > WireLimits.SnapshotItems) return work.Fail(WireError.Unavailable);
                    list = new GameListSource<Thing>(owner.access, map!, game, owner.types, owner.access.AllThings);
                    source = new ConsistentSnapshotSource(list, owner.Fresh);
                }
                if (!source!.TryStructure(out long structure) || structure != 0) return Restart(work);
                while (index < list.List.Count)
                {
                    if (!work.TryCandidate()) return false;
                    Thing thing = list.List[index];
                    if (thing == null) return work.Fail(WireError.Unavailable);
                    TargetGateResult gate = list.Gate(thing, work, out bool witnessed);
                    if (gate == TargetGateResult.Deferred) return false;
                    if (!witnessed) return work.Fail(WireError.Unavailable);
                    if (gate == TargetGateResult.Eligible && !Project(work, thing, game)) return false;
                    index++;
                }
                if (!source.TryStructure(out structure) || structure != 0) return Restart(work);
                EligibleSnapshot snapshot = work.Generations.Snapshots.Create(scope!, Method,
                    owner.resources ? WireLimits.PageItems : work.Request.Limit, items.ToArray(), members.ToArray(), bytes, source, 0);
                pass = work.Generations.Snapshots.BeginRevalidation(snapshot);
                return true;
            }

            private bool Project(ProducerWork<Map> work, Thing thing, Game game)
            {
                int cost;
                try { cost = thing is ThingWithComps twc ? owner.labels.LabelCost(twc, game) : GameOwnPawnReader.NestedFields; }
                catch { return work.Fail(WireError.Unavailable); }
                if (!work.TryNestedFields(cost + 8)) return false;
                try
                {
                    // Definition class and instance class must match the audited binding. Ordinary
                    // labels are mirrored, not invoked; unknown eligible label => whole query fails.
                    if (!owner.labels.Fresh()) return work.Fail(WireError.Unavailable);
                    ThingDef def = thing.def;
                    if (def.GetType() != typeof(ThingDef) || def.thingClass != thing.GetType() || thing.stackCount < 1)
                        return work.Fail(WireError.Unavailable);
                    string? label;
                    if (thing is Pawn pawn) label = owner.pawns.LabelShort(pawn) is string shortLabel
                        ? PawnProjection.Label("", shortLabel) : null;
                    else if (thing is ThingWithComps withComps) label = owner.labels.LabelCap(withComps, game);
                    else
                    {
                        // Exact base Thing, without stuff/quality/comp/style labels or HP extras.
                        if (!owner.patches.IsUnpatched(GameKeyedText.Getter(typeof(Thing), "Stuff"))) return work.Fail(WireError.Unavailable);
                        if (thing.GetType() != typeof(Thing) || thing.Stuff != null || (def.useHitPoints && def.stackLimit == 1))
                            return work.Fail(WireError.Unavailable);
                        label = owner.text.Cap(def.label, def);
                        if (thing.stackCount > 1) label += " x" + thing.stackCount.ToString();
                    }
                    if (JobProjection.Public(label) == null) return work.Fail(WireError.Unavailable);
                    string? id = work.Generations.Reference("t", thing, map!);
                    if (id == null) return work.Fail(WireError.Unavailable);
                    JObject item = ThingProjection.Item(id, label!, thing.Position.x, thing.Position.z, thing.stackCount);
                    string? resourceLabel = null;
                    if (owner.resources)
                    {
                        if (!owner.patches.IsUnpatched(owner.resourceGetter)) return work.Fail(WireError.Unavailable);
                        // CountAsResource is exactly the priority-field comparison, no getter call.
                        if (def.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                        {
                            if (def.category != ThingCategory.Item) return work.Fail(WireError.Unavailable);
                            resourceLabel = owner.text.Cap(def.label, def);
                            if (JobProjection.Public(resourceLabel) == null) return work.Fail(WireError.Unavailable);
                        }
                    }
                    int size = Encoding.UTF8.GetByteCount(item.ToString(Formatting.None))
                        + (resourceLabel == null ? 0 : Encoding.UTF8.GetByteCount(resourceLabel) + 64);
                    if (!work.TryRetain(size)) return false;
                    if (resourceLabel != null && !totals.Add(def, resourceLabel, thing.stackCount)) return work.Fail(WireError.Unavailable);
                    items.Add(item); members.Add(thing); bytes += size;
                    return true;
                }
                catch { return work.Fail(WireError.Unavailable); }
            }

            private bool Restart(ProducerWork<Map> work)
            {
                if (paging) return work.Fail(WireError.CursorExpired);
                if (++restarts > 2) return work.Fail(WireError.Unavailable);
                work.ReleaseMemory(); items.Clear(); members.Clear(); totals = new ThingProjection.Resources();
                list = null; source = null; pass = null; index = bytes = 0;
                return false;
            }

            public bool Eligible(ProducerWork<Map> work)
            {
                ReadFrame<Map> frame = work.Frame!;
                if (pass == null || map == null || scope == null || frame.Geometry == null || !(frame.Game is Game game)
                    || work.Generations.MapScope(map)?.Map != scope.Map) return false;
                SnapshotStore store = work.Generations.Snapshots;
                if (!ReferenceEquals(pass.Proven, frame))
                {
                    Revalidated valid = store.Revalidate(pass, frame, work);
                    if (valid == Revalidated.Expired) return work.Fail(paging ? WireError.CursorExpired : WireError.Unavailable);
                    if (valid != Revalidated.Valid) return false;
                }
                var sample = new SampleStamp(scope, frame.Geometry.Frame, owner.pawns.Tick(game));
                if (owner.resources)
                {
                    if (!work.TryNestedFields(totals.Count + 1)) return false;
                    result = totals.Result(sample);
                }
                else
                {
                    if (!store.Retain(pass, frame)) return work.Fail(paging ? WireError.CursorExpired : WireError.Unavailable);
                    result = store.Page(pass, frame, start, sample);
                }
                return true;
            }
            public JObject Result() => result ?? throw new InvalidOperationException("Thing projection incomplete.");
            public void Discard() { items.Clear(); members.Clear(); totals = new ThingProjection.Resources(); list = null; source = null; pass = null; result = null; map = null; }
        }
    }
}
