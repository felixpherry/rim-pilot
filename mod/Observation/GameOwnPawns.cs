using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Own-pawn identity/status reader (docs/visibility-audit.md#own-pawns). Game thread only,
    // after the caller's own-pawn and target gates passed. Reads plain fields and field-returning
    // getters; mirrors Pawn.LabelShortCap without invoking it. Any patch on the closure below,
    // an unknown Name subclass, an uncached life stage or markup refuses the pawn.
    internal sealed class GameOwnPawnReader
    {
        // Nested fields charged per projected pawn before reading it.
        internal const int NestedFields = 16;
        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly AccessTools.FieldRef<Pawn_AgeTracker, int> lifeStage;
        private readonly MethodBase[] closure;
        private int checkedFrame = -1;

        internal GameOwnPawnReader(int gameThread, IProjectionPatchAudit patches)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            this.gameThread = gameThread;
            this.patches = patches ?? throw new ArgumentNullException(nameof(patches));
            FieldInfo? stage = typeof(Pawn_AgeTracker).GetField("cachedLifeStageIndex", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (stage == null || stage.FieldType != typeof(int)) throw new InvalidOperationException("Pawn binding unavailable.");
            lifeStage = AccessTools.FieldRefAccess<Pawn_AgeTracker, int>(stage);
            closure = new MethodBase[]
            {
                // Normal short label path (colonist bar/inspect identity); never called.
                Getter(typeof(Entity), "LabelShortCap"), Getter(typeof(Pawn), "LabelShort"), Getter(typeof(Pawn), "LabelNoCount"),
                Getter(typeof(Pawn), "LabelPrefix", BindingFlags.NonPublic), Getter(typeof(Pawn), "KindLabel"),
                Getter(typeof(Pawn), "IsMutant"), Getter(typeof(Pawn), "RaceProps"), Getter(typeof(RaceProperties), "Humanlike"),
                Getter(typeof(Pawn_AgeTracker), "CurKindLifeStage"), Getter(typeof(Pawn_AgeTracker), "CurLifeStageIndex"),
                Method(typeof(GenLabel), "BestKindLabel", typeof(Pawn), typeof(bool), typeof(bool), typeof(bool), typeof(int)),
                Method(typeof(GenLabel), "BestKindLabel", typeof(PawnKindDef), typeof(Gender), typeof(bool).MakeByRefType(),
                    typeof(bool), typeof(int)),
                Method(typeof(GenText), "CapitalizeFirst", typeof(string)), Method(typeof(GenText), "FirstLetterBetweenTags", typeof(string)),
                Getter(typeof(NameTriple), "ToStringShort"), Getter(typeof(NameSingle), "ToStringShort"),
                // Called: plain field getters (Nick also picks first/last by string hash).
                Getter(typeof(Pawn), "Name"), Getter(typeof(NameTriple), "Nick"), Getter(typeof(NameTriple), "First"),
                Getter(typeof(NameTriple), "Last"), Getter(typeof(NameSingle), "Name"),
                Getter(typeof(Pawn_MutantTracker), "HasTurned"), Getter(typeof(Pawn_MutantTracker), "Def"),
                // Status: explicit booleans from plain fields.
                Getter(typeof(Pawn), "Downed"), Getter(typeof(Pawn), "Dead"), Getter(typeof(Pawn), "Drafted"),
                Getter(typeof(Pawn_HealthTracker), "Downed"), Getter(typeof(Pawn_HealthTracker), "Dead"),
                Getter(typeof(Pawn_HealthTracker), "State"), Getter(typeof(Pawn_DraftController), "Drafted"),
                Getter(typeof(TickManager), "TicksGame")
            };
        }

        // Whole closure rechecked once per rendered frame; any patch fails closed.
        internal bool Fresh()
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            int frame = UnityEngine.Time.frameCount;
            if (frame == checkedFrame) return true;
            try { foreach (MethodBase method in closure) if (!patches.IsUnpatched(method)) return false; }
            catch { return false; }
            checkedFrame = frame;
            return true;
        }

        internal long? Tick(Game game)
        {
            TickManager? ticks = game?.tickManager;
            if (ticks == null || !Fresh()) return null;
            int tick = ticks.TicksGame;
            return tick < 0 ? (long?)null : tick;
        }

        internal bool Read(Pawn pawn, out string? label, out List<string>? status)
        {
            label = null; status = null;
            if (pawn == null || !Fresh()) return false;
            try
            {
                string? prefix = "";
                Pawn_MutantTracker? mutant = pawn.mutant;
                if (mutant != null && mutant.HasTurned) prefix = mutant.Def?.namePrefix;
                Name? name = pawn.Name;
                string? baseLabel;
                if (name == null) baseLabel = KindLabel(pawn);
                else if (name.GetType() == typeof(NameTriple)) baseLabel = ((NameTriple)name).Nick;
                else if (name.GetType() == typeof(NameSingle)) baseLabel = ((NameSingle)name).Name;
                else return false; // Unaudited Name subclass.
                label = PawnProjection.Label(prefix, baseLabel);
                Pawn_HealthTracker? health = pawn.health;
                if (label == null || health == null) { label = null; return false; }
                PawnHealthState state = health.State;
                status = PawnProjection.Status(state == PawnHealthState.Down, state == PawnHealthState.Dead,
                    pawn.drafter != null && pawn.drafter.Drafted);
                return true;
            }
            catch { label = null; status = null; return false; }
        }

        internal string? LabelShort(Pawn pawn)
        {
            if (!Fresh()) return null;
            string? prefix = pawn.mutant != null && pawn.mutant.HasTurned ? pawn.mutant.Def?.namePrefix : "";
            Name? name = pawn.Name;
            string? label = name == null ? KindLabel(pawn) : name.GetType() == typeof(NameTriple) ? ((NameTriple)name).Nick
                : name.GetType() == typeof(NameSingle) ? ((NameSingle)name).Name : null;
            return PawnProjection.Label(prefix, label) == null ? null : prefix + label;
        }

        // GenLabel.BestKindLabel(pawn) with no plural/gender/life-stage notes, using only the
        // cached life-stage index (never RecalculateLifeStageIndex) and definition label fields.
        private string? KindLabel(Pawn pawn)
        {
            RaceProperties? race = pawn.def?.race;
            PawnKindDef? kind = pawn.kindDef;
            if (race == null || kind == null) return null;
            PawnKindLifeStage? stage = null;
            if (race.intelligence < Intelligence.Humanlike)
            {
                if (pawn.ageTracker == null || kind.lifeStages == null) return null;
                int index = lifeStage(pawn.ageTracker);
                if (index < 0 || index >= kind.lifeStages.Count || (stage = kind.lifeStages[index]) == null) return null;
            }
            switch (pawn.gender)
            {
                case Gender.None: return stage?.label ?? kind.label;
                case Gender.Male: return stage?.labelMale ?? stage?.label ?? kind.labelMale ?? kind.label;
                case Gender.Female: return stage?.labelFemale ?? stage?.label ?? kind.labelFemale ?? kind.label;
                default: return null;
            }
        }

        private static MethodInfo Getter(Type type, string property, BindingFlags visibility = BindingFlags.Public) =>
            type.GetProperty(property, visibility | BindingFlags.Instance | BindingFlags.DeclaredOnly)?.GetGetMethod(true)
                ?? throw new InvalidOperationException("Pawn binding unavailable.");
        private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly, null, parameters, null) ?? throw new InvalidOperationException("Pawn binding unavailable.");
    }

    // Snapshot source for one map's own pawns: list structure/eligibility witnesses from
    // GameListSource, revalidation = own-pawn rule plus the full target gate.
    internal sealed class OwnPawnSource : ISnapshotSource
    {
        private readonly GameTargetAccess access;
        internal readonly GameListSource<Pawn> List;
        internal OwnPawnSource(GameTargetAccess access, GameListSource<Pawn> list) { this.access = access; List = list; }
        public bool TryStructure(out long version) => List.TryStructure(out version);
        public bool TryEligibility(out long revision) => List.TryEligibility(out revision);
        public TargetGateResult Revalidate(object member, INestedWork work) =>
            member is Pawn pawn && access.OwnPawn(pawn, List.Player) ? List.Gate(pawn, work, out _) : TargetGateResult.Unavailable;
    }

    // `pawns.list` producer: one bounded build of the map's eligible own pawns into an immutable
    // snapshot, then whole-vector revalidation and paging in the releasing update. Cursor
    // requests revalidate the retained snapshot and never rebuild it.
    internal sealed class GameOwnPawnsProducer : IReadProducer<Map>
    {
        private const int MaxRestarts = 2;
        private readonly GameTargetAccess access;
        private readonly GameOwnPawnReader reader;
        private readonly TypeAdmission pawns = new TypeAdmission(typeof(Pawn));

        internal GameOwnPawnsProducer(GameTargetAccess access, GameOwnPawnReader reader)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public bool SnapshotBuild => true;
        public IReadContinuation<Map> Start(Request request) => new Continuation(this);

        private sealed class Continuation : IReadContinuation<Map>
        {
            private readonly GameOwnPawnsProducer owner;
            private readonly List<JObject> items = new List<JObject>();
            private readonly List<object> members = new List<object>();
            private Map? map;
            private Scope? scope;
            private OwnPawnSource? source;
            private Revalidation? pass;
            private JObject? result;
            private int index, bytes, restarts, start;
            private bool paging;
            internal Continuation(GameOwnPawnsProducer owner) { this.owner = owner; }

            public StepResult Step(ProducerWork<Map> work)
            {
                if (map == null && !Bind(work)) return StepResult.Continue;
                if (pass == null)
                {
                    if (!Build(work)) return StepResult.Continue;
                    // Projection (including closure audit/DTO encoding) and whole-vector
                    // revalidation are distinct bounded steps. Never append revalidation to
                    // a projection step that may already have spent this update's allowance.
                    return StepResult.Continue;
                }
                switch (work.Generations.Snapshots.Revalidate(pass!, work.Frame!, work))
                {
                    case Revalidated.Valid: return StepResult.Complete;
                    case Revalidated.Expired: Expired(work); return StepResult.Continue;
                    default: return StepResult.Continue;
                }
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
                if (!work.Generations.Snapshots.TryResolve(request.Scope, ReadMethod.Pawns, request.Limit, request.Cursor,
                    out EligibleSnapshot? snapshot, out start)) return work.Fail(WireError.CursorExpired);
                pass = work.Generations.Snapshots.BeginRevalidation(snapshot!);
                return true;
            }

            // False = yielded/failed this update; progress is kept for the next update.
            private bool Build(ProducerWork<Map> work)
            {
                Game game = (Game)work.Frame!.Game!;
                if (source == null)
                {
                    List<Pawn>? list = owner.access.SpawnedPawns(map!);
                    if (list == null || list.Count > WireLimits.SnapshotItems) return work.Fail(WireError.Unavailable);
                    var witnessed = new GameListSource<Pawn>(owner.access, map!, game, owner.pawns, owner.access.SpawnedPawns);
                    if (witnessed.Player == null) return work.Fail(WireError.Unavailable);
                    source = new OwnPawnSource(owner.access, witnessed);
                }
                GameListSource<Pawn> spawned = source.List;
                if (!spawned.TryStructure(out long structure) || structure != 0) return Restart(work);
                while (index < spawned.List.Count)
                {
                    if (!work.TryCandidate()) return false;
                    Pawn pawn = spawned.List[index];
                    if (pawn == null) return work.Fail(WireError.Unavailable);
                    if (!owner.access.OwnPawn(pawn, spawned.Player))
                    {
                        // Not listed; its inputs are still witnessed so a later change expires the snapshot.
                        if (!spawned.Witness(pawn)) return work.Fail(WireError.Unavailable);
                        index++; continue;
                    }
                    TargetGateResult gate = spawned.Gate(pawn, work, out bool recorded);
                    if (gate == TargetGateResult.Deferred) return false;
                    if (!recorded) return work.Fail(WireError.Unavailable);
                    if (gate == TargetGateResult.Eligible && !Project(work, pawn)) return false;
                    index++;
                }
                EligibleSnapshot snapshot = work.Generations.Snapshots.Create(scope!, ReadMethod.Pawns, work.Request.Limit,
                    items.ToArray(), members.ToArray(), bytes, source, 0);
                pass = work.Generations.Snapshots.BeginRevalidation(snapshot);
                start = 0;
                return true;
            }

            private bool Project(ProducerWork<Map> work, Pawn pawn)
            {
                if (items.Count >= WireLimits.SnapshotItems) return work.Fail(WireError.Unavailable);
                if (!work.TryNestedFields(GameOwnPawnReader.NestedFields)) return false;
                // An unprojectable eligible own pawn fails the whole list: never a silent partial list.
                if (!owner.reader.Read(pawn, out string? label, out List<string>? status)) return work.Fail(WireError.Unavailable);
                string? id = work.Generations.Reference("p", pawn, map!);
                if (id == null) return work.Fail(WireError.Unavailable);
                JObject item = PawnProjection.Item(id, label!, status!);
                int size = Encoding.UTF8.GetByteCount(item.ToString(Formatting.None));
                if (!work.TryRetain(size)) return false;
                items.Add(item); members.Add(pawn); bytes += size;
                return true;
            }

            // Source changed mid-build or a member lost eligibility before publication.
            private bool Restart(ProducerWork<Map> work)
            {
                if (++restarts > MaxRestarts) return work.Fail(WireError.Unavailable);
                work.ReleaseMemory();
                items.Clear(); members.Clear(); source = null; pass = null; result = null; index = 0; bytes = 0;
                return false;
            }

            private void Expired(ProducerWork<Map> work)
            {
                if (paging) work.Fail(WireError.CursorExpired);
                else Restart(work);
            }

            public bool Eligible(ProducerWork<Map> work)
            {
                ReadFrame<Map> frame = work.Frame!;
                if (pass == null || map == null || frame.Geometry == null || !(frame.Game is Game game)
                    || work.Generations.MapScope(map)?.Map != scope!.Map) return false;
                SnapshotStore store = work.Generations.Snapshots;
                if (!ReferenceEquals(pass.Proven, frame))
                {
                    Revalidated revalidated = store.Revalidate(pass, frame, work);
                    if (revalidated == Revalidated.Expired) return RestartRelease(work);
                    if (revalidated != Revalidated.Valid) return false;
                }
                if (!store.Retain(pass, frame)) return RestartRelease(work);
                result = store.Page(pass, frame, start, new SampleStamp(scope!, frame.Geometry.Frame, owner.reader.Tick(game)));
                return true;
            }

            // A completed first page may expire before release when an allowance-only sample
            // revokes retained data. Never rebase that vector: discard and redo the full build.
            private bool RestartRelease(ProducerWork<Map> work)
            {
                if (paging) return work.Fail(WireError.CursorExpired);
                Restart(work);
                return work.Failure.HasValue ? false : work.RestartStep();
            }

            public JObject Result() => result ?? throw new InvalidOperationException("Pawn list incomplete.");
            public void Discard() { items.Clear(); members.Clear(); source = null; pass = null; result = null; map = null; }
        }
    }

    // `pawn.get` producer: one eligible own pawn by reference. The reference is only a candidate:
    // own-pawn rule and full target gate are rerun in the releasing update.
    internal sealed class GameOwnPawnProducer : IReadProducer<Map>
    {
        private readonly GameTargetAccess access;
        private readonly GameOwnPawnReader reader;
        private readonly GameNeedsReader? needs;
        private readonly GameHealthReader? health;
        private readonly GameGearReader? gear;
        private readonly GameJobsReader? jobs;
        private readonly TypeAdmission pawns = new TypeAdmission(typeof(Pawn));

        // A null family reader (binding failed) keeps that family omitted for every pawn.
        internal GameOwnPawnProducer(GameTargetAccess access, GameOwnPawnReader reader, GameNeedsReader? needs, GameHealthReader? health, GameGearReader? gear = null, GameJobsReader? jobs = null)
        {
            this.access = access ?? throw new ArgumentNullException(nameof(access));
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
            this.needs = needs;
            this.health = health;
            this.gear = gear;
            this.jobs = jobs;
        }

        public bool SnapshotBuild => false;
        public IReadContinuation<Map> Start(Request request) => new Continuation(this);

        private sealed class Continuation : IReadContinuation<Map>
        {
            private readonly GameOwnPawnProducer owner;
            private JObject? result;
            internal Continuation(GameOwnPawnProducer owner) { this.owner = owner; }

            // All work happens in the releasing update's Eligible audit.
            public StepResult Step(ProducerWork<Map> work) => StepResult.Complete;

            public bool Eligible(ProducerWork<Map> work)
            {
                result = null;
                ReadFrame<Map> frame = work.Frame!;
                Request request = work.Request;
                if (!(frame.Game is Game game) || frame.Geometry == null || request.Scope == null || request.Target == null)
                    return work.Fail(WireError.Unavailable);
                Map? map = work.Generations.ResolveMap(request.Scope);
                Scope? scope = map == null ? null : work.Generations.MapScope(map);
                if (map == null || scope == null || !scope.Same(request.Scope)
                    || !(work.Generations.ResolveEntity(request.Scope, "p", request.Target) is Pawn pawn))
                    return work.Fail(WireError.Unavailable);
                Faction? player = game.World?.factionManager?.OfPlayer;
                if (!work.TryCandidate()) return false;
                if (!owner.access.OwnPawn(pawn, player)) return work.Fail(WireError.Unavailable);
                TargetGateResult gate = owner.access.Thing(pawn, map, game, owner.pawns, work);
                if (gate == TargetGateResult.Deferred) return false;
                if (gate != TargetGateResult.Eligible) return work.Fail(WireError.Unavailable);
                if (!work.TryNestedFields(GameOwnPawnReader.NestedFields)) return false;
                if (!owner.reader.Read(pawn, out string? label, out List<string>? status)) return work.Fail(WireError.Unavailable);
                // Same releasing update as the gates above. Unverified omits only this family.
                JArray? shownNeeds = null;
                if (owner.needs != null)
                {
                    FamilyRead read = owner.needs.Read(pawn, game, work, out shownNeeds);
                    if (read == FamilyRead.Yield) return false;
                    if (read != FamilyRead.Value) shownNeeds = null;
                }
                JArray? rows = null;
                if (owner.health != null)
                {
                    FamilyRead read = owner.health.Read(pawn, work, out rows);
                    if (read == FamilyRead.Yield) return false;
                    if (read != FamilyRead.Value) rows = null;
                }
                JArray? items = null;
                if (owner.gear != null)
                {
                    FamilyRead read = owner.gear.Read(pawn, game, work, out items);
                    if (read == FamilyRead.Yield) return false;
                    if (read != FamilyRead.Value) items = null;
                }
                string? job = null, lord = null;
                if (owner.jobs != null)
                {
                    FamilyRead read = owner.jobs.Read(pawn, map, game, work, out job, out lord);
                    if (read == FamilyRead.Yield) return false;
                    if (read != FamilyRead.Value) { job = null; lord = null; }
                }
                JObject detail = PawnProjection.Detail(new SampleStamp(scope, frame.Geometry.Frame, owner.reader.Tick(game)),
                    PawnProjection.Item(request.Target, label!, status!), shownNeeds, rows, items, job, lord);
                if (!work.TryRetain(Encoding.UTF8.GetByteCount(detail.ToString(Formatting.None)))) return false;
                result = detail;
                return true;
            }

            public JObject Result() => result ?? throw new InvalidOperationException("Pawn incomplete.");
            public void Discard() => result = null;
        }
    }
}
