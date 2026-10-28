using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimPilot.Mod.Observation
{
    internal enum FamilyRead { Value, Unverified, Yield }

    // Shown-needs reader for the own-pawn detail (docs/visibility-audit.md#shown-needs). Game
    // thread only, after the caller's own-pawn and target gates passed. Mirrors the normal needs
    // tab (ITab_Pawn_Needs → NeedsCardUtility: ShowOnNeedList list sorted by listPriority, plus
    // the separate mood pane for exact tracker.mood) from plain fields. Never draws, never calls
    // tooltips, instant mood, need intervals, Def.LabelCap or a stat worker. Food's maximum is
    // the worker's fresh cache entry when one exists, else a field-only MaxNutrition mirror
    // admitted only when no other source references the stat. Any unaudited need class,
    // patch on the closure or unprovable food input omits the whole family (Unverified).
    internal sealed class GameNeedsReader
    {
        // Food inputs charged in one step; leaves room for the gate and identity in one update.
        internal const int MaxNested = 512, MaxDefList = 64, MaxSources = 128, MaxTypes = 512;
        private enum Kind { Plain, Food, MechEnergy, Outdoors, Indoors, ChemicalAny, KillThirst }

        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly Dictionary<Type, Kind> kinds = new Dictionary<Type, Kind>();
        private readonly Dictionary<Type, bool> neutralComps = new Dictionary<Type, bool>();
        private readonly Dictionary<Type, bool> stageTypes = new Dictionary<Type, bool>();
        private readonly AccessTools.FieldRef<Need, float> curLevel;
        private readonly AccessTools.FieldRef<Def, TaggedString> cachedLabel;
        private readonly AccessTools.FieldRef<Pawn_AgeTracker, int> lifeStage;
        private readonly AccessTools.FieldRef<StatDef, StatWorker> statWorker;
        private readonly AccessTools.FieldRef<StatWorker, Dictionary<Thing, StatCacheEntry>> statCache;
        private readonly MethodBase[] closure;
        private int checkedFrame = -1;

        internal GameNeedsReader(int gameThread, IProjectionPatchAudit patches)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            this.gameThread = gameThread;
            this.patches = patches ?? throw new ArgumentNullException(nameof(patches));
            curLevel = Field<Need, float>("curLevelInt");
            cachedLabel = Field<Def, TaggedString>("cachedLabelCap");
            lifeStage = Field<Pawn_AgeTracker, int>("cachedLifeStageIndex");
            statWorker = Field<StatDef, StatWorker>("workerInt");
            statCache = Field<StatWorker, Dictionary<Thing, StatCacheEntry>>("temporaryStatCache");

            // Exact classes whose ShowOnNeedList/CurLevel/MaxLevel were audited in the installed
            // build. Metadata asserts the declaring implementation so a changed build fails here.
            foreach (Type plain in new[] { typeof(Need_Rest), typeof(Need_Joy), typeof(Need_Beauty), typeof(Need_Comfort),
                typeof(Need_RoomSize), typeof(Need_Mood), typeof(Need_Suppression), typeof(Need_Deathrest),
                typeof(Need_Play), typeof(Need_Sadism), typeof(Need_Learning), typeof(Need_Chemical) })
                Admit(plain, Kind.Plain, typeof(Need), plain == typeof(Need_Chemical) ? typeof(Need_Chemical) : typeof(Need), typeof(Need));
            Admit(typeof(Need_Food), Kind.Food, typeof(Need), typeof(Need), typeof(Need_Food));
            Admit(typeof(Need_MechEnergy), Kind.MechEnergy, typeof(Need), typeof(Need), typeof(Need_MechEnergy));
            Admit(typeof(Need_Outdoors), Kind.Outdoors, typeof(Need_Outdoors), typeof(Need), typeof(Need));
            Admit(typeof(Need_Indoors), Kind.Indoors, typeof(Need_Indoors), typeof(Need), typeof(Need));
            Admit(typeof(Need_Chemical_Any), Kind.ChemicalAny, typeof(Need_Chemical_Any), typeof(Need), typeof(Need));
            Admit(typeof(Need_KillThirst), Kind.KillThirst, typeof(Need_KillThirst), typeof(Need), typeof(Need));
            if (Getter(typeof(Def), "LabelCap").DeclaringType != typeof(Def)
                || typeof(NeedDef).GetProperty("LabelCap")?.GetGetMethod()?.DeclaringType != typeof(Def))
                throw new InvalidOperationException("Needs binding unavailable.");

            var methods = new List<MethodBase>
            {
                // Normal caller: needs tab visibility, list/pane/sort and bar drawing (never called).
                Getter(typeof(ITab_Pawn_Needs), "IsVisible"),
                Method(typeof(NeedsCardUtility), "DoNeedsMoodAndThoughts", typeof(UnityEngine.Rect), typeof(Pawn), typeof(UnityEngine.Vector2).MakeByRefType()),
                Method(typeof(NeedsCardUtility), "DoNeeds", typeof(UnityEngine.Rect), typeof(Pawn)),
                Method(typeof(NeedsCardUtility), "DoMoodAndThoughts", typeof(UnityEngine.Rect), typeof(Pawn), typeof(UnityEngine.Vector2).MakeByRefType()),
                Method(typeof(NeedsCardUtility), "UpdateDisplayNeeds", typeof(Pawn)),
                Method(typeof(PawnNeedsUIUtility), "SortInDisplayOrder", typeof(List<Need>)),
                Getter(typeof(Pawn_NeedsTracker), "AllNeeds"), Getter(typeof(Need), "ShowOnNeedList"),
                Getter(typeof(Need), "CurLevel"), Getter(typeof(Need), "MaxLevel"), Getter(typeof(Need), "CurLevelPercentage"),
                Getter(typeof(Need), "LabelCap"), Getter(typeof(Def), "LabelCap"),
                Method(typeof(GenText), "CapitalizeFirst", typeof(string)), Method(typeof(GenText), "FirstLetterBetweenTags", typeof(string)),
                DrawOnGui(typeof(Need)), DrawOnGui(typeof(Need_Mood)), DrawOnGui(typeof(Need_Food)),
                DrawOnGui(typeof(Need_Learning)), DrawOnGui(typeof(Need_Chemical_Any)),
                // Subclass inclusion/level overrides (mirrored, except AgeBiologicalYears/Suppressed/Degree).
                Getter(typeof(Need_Chemical), "CurLevel"), Getter(typeof(Need_MechEnergy), "MaxLevel"),
                Getter(typeof(Need_Outdoors), "ShowOnNeedList"), Getter(typeof(Need_Outdoors), "Disabled", BindingFlags.NonPublic),
                Getter(typeof(Need_Indoors), "ShowOnNeedList"), Getter(typeof(Need_Indoors), "Disabled", BindingFlags.NonPublic),
                Getter(typeof(Need_Chemical_Any), "ShowOnNeedList"), Getter(typeof(Need_Chemical_Any), "Disabled", BindingFlags.NonPublic),
                Getter(typeof(Need_Chemical_Any), "TraitDrugDesire", BindingFlags.NonPublic),
                Getter(typeof(Need_KillThirst), "ShowOnNeedList"), Getter(typeof(Pawn_AgeTracker), "AgeBiologicalYears"),
                Getter(typeof(Pawn_NeedsTracker), "PrefersIndoors"), Getter(typeof(Pawn_NeedsTracker), "PrefersOutdoors"),
                Method(typeof(Pawn_NeedsTracker), "TryGetNeed", typeof(NeedDef), typeof(Need).MakeByRefType()),
                Method(typeof(TraitSet), "GetTrait", typeof(TraitDef)), Getter(typeof(Trait), "Suppressed"), Getter(typeof(Trait), "Degree"),
                Getter(typeof(Gene), "Overridden"), Getter(typeof(Pawn), "Dead"), Getter(typeof(Pawn_HealthTracker), "State"),
                Getter(typeof(Pawn), "RaceProps"), Getter(typeof(Current), "ProgramState", BindingFlags.Public, true),
                // Food maximum: Need_Food.MaxLevel → GetStatValue(MaxNutrition, true, 15) closure (mirrored).
                Getter(typeof(Need_Food), "MaxLevel"),
                Method(typeof(StatExtension), "GetStatValue", typeof(Thing), typeof(StatDef), typeof(bool), typeof(int)),
                Method(typeof(StatWorker), "GetValue", typeof(Thing), typeof(bool), typeof(int)),
                Method(typeof(StatWorker), "GetValue", typeof(StatRequest), typeof(bool)),
                Method(typeof(StatWorker), "GetValueUnfinalized", typeof(StatRequest), typeof(bool)),
                Method(typeof(StatWorker), "GetBaseValueFor", typeof(StatRequest)),
                Method(typeof(StatWorker), "FinalizeValue", typeof(StatRequest), typeof(float).MakeByRefType(), typeof(bool)),
                Method(typeof(StatWorker), "StatOffsetFromGear", typeof(Thing), typeof(StatDef)),
                Method(typeof(StatPart_BodySize), "TransformValue", typeof(StatRequest), typeof(float).MakeByRefType()),
                Method(typeof(StatPart_LifeStageMaxFood), "TransformValue", typeof(StatRequest), typeof(float).MakeByRefType()),
                Method(typeof(StatUtility), "GetStatValueFromList", typeof(List<StatModifier>), typeof(StatDef), typeof(float)),
                Method(typeof(HediffStatsUtility), "GetStatOffsetForSeverity", typeof(StatDef), typeof(HediffStage), typeof(Pawn), typeof(float)),
                Method(typeof(HediffStatsUtility), "GetStatFactorForSeverity", typeof(StatDef), typeof(HediffStage), typeof(Pawn), typeof(float)),
                Method(typeof(HediffDef), "StageAtSeverity", typeof(float)),
                Getter(typeof(Hediff), "CurStage"), Getter(typeof(Hediff), "CurStageIndex"),
                Method(typeof(Trait), "OffsetOfStat", typeof(StatDef)), Method(typeof(Trait), "MultiplierOfStat", typeof(StatDef)),
                Method(typeof(ThingComp), "GetStatOffset", typeof(StatDef)), Method(typeof(ThingComp), "GetStatFactor", typeof(StatDef)),
                Getter(typeof(Pawn), "BodySize"), Getter(typeof(Pawn), "Inspired"), Getter(typeof(Pawn), "InspirationDef"),
                Getter(typeof(Pawn), "Ideo"), Getter(typeof(Pawn_IdeoTracker), "Ideo"), Getter(typeof(Ideo), "PreceptsListForReading"),
                Method(typeof(Ideo), "GetRole", typeof(Pawn)),
                Getter(typeof(Pawn_AgeTracker), "CurLifeStage"), Getter(typeof(Pawn_AgeTracker), "CurLifeStageRace"),
                Getter(typeof(Pawn_AgeTracker), "CurLifeStageIndex"),
                Getter(typeof(StatDef), "Worker"), Getter(typeof(Find), "Scenario", BindingFlags.Public, true),
                Method(typeof(Thing), "GetHashCode"),
                // Called: plain field/list getters and the pure scenario factor loop.
                Getter(typeof(InspirationHandler), "CurStateDef"), Getter(typeof(Pawn_GeneTracker), "Xenogenes"),
                Getter(typeof(Pawn_GeneTracker), "Endogenes"), Getter(typeof(Pawn_ApparelTracker), "WornApparel"),
                Getter(typeof(ThingOwner<Apparel>), "InnerListForReading"),
                Getter(typeof(Pawn_EquipmentTracker), "AllEquipmentListForReading"),
                Getter(typeof(ThingOwner<ThingWithComps>), "InnerListForReading"),
                Getter(typeof(ThingWithComps), "AllComps"), Getter(typeof(CompBladelinkWeapon), "TraitsListForReading"),
                Getter(typeof(Thing), "Stuff"), Getter(typeof(Game), "Scenario"),
                Method(typeof(Scenario), "GetStatFactor", typeof(StatDef)), Method(typeof(ScenPart_StatFactor), "GetStatFactor", typeof(StatDef)),
                Getter(typeof(TickManager), "TicksGame")
            };
            closure = methods.ToArray();
        }

        private void Admit(Type type, Kind kind, Type shown, Type level, Type max)
        {
            if (Getter(type, "ShowOnNeedList", BindingFlags.Public, false, false).DeclaringType != shown
                || Getter(type, "CurLevel", BindingFlags.Public, false, false).DeclaringType != level
                || Getter(type, "MaxLevel", BindingFlags.Public, false, false).DeclaringType != max
                || Getter(type, "CurLevelPercentage", BindingFlags.Public, false, false).DeclaringType != typeof(Need)
                || Getter(type, "LabelCap", BindingFlags.Public, false, false).DeclaringType != typeof(Need))
                throw new InvalidOperationException("Needs binding unavailable.");
            kinds.Add(type, kind);
        }

        // Whole closure rechecked once per rendered frame; any patch fails closed.
        private bool Fresh()
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            int frame = UnityEngine.Time.frameCount;
            if (frame == checkedFrame) return true;
            try { foreach (MethodBase method in closure) if (!patches.IsUnpatched(method)) return false; }
            catch { return false; }
            checkedFrame = frame;
            return true;
        }

        // Value: rows in normal display order (empty = needs tab shows nothing). Unverified:
        // omit the family. Yield: retry next update. Never a partial list.
        internal FamilyRead Read(Pawn pawn, Game game, INestedWork work, out JArray? rows)
        {
            rows = null;
            if (pawn == null || game == null || !Fresh()) return FamilyRead.Unverified;
            try
            {
                Pawn_NeedsTracker? tracker = pawn.needs;
                // ITab_Pawn_Needs.IsVisible: needs tracker with at least one need. Its animal/insect
                // faction and holding-platform branches cannot apply to directly spawned own pawns.
                if (tracker == null) { rows = new JArray(); return FamilyRead.Value; }
                if (tracker.GetType() != typeof(Pawn_NeedsTracker)) return FamilyRead.Unverified;
                List<Need>? all = tracker.AllNeeds;
                if (all == null || all.Count > NeedsProjection.MaxNeeds) return FamilyRead.Unverified;
                if (all.Count == 0) { rows = new JArray(); return FamilyRead.Value; }
                if (!work.TryNestedFields(4 + 2 * all.Count)) return FamilyRead.Yield;

                var shown = new List<Need>(); var priorities = new List<int>();
                Need? pane = null, food = null;
                for (int i = 0; i < all.Count; i++)
                {
                    Need need = all[i];
                    if (need == null || !kinds.TryGetValue(need.GetType(), out Kind kind)) return FamilyRead.Unverified;
                    NeedDef def = need.def;
                    if (def == null || def.GetType() != typeof(NeedDef) || def.needClass != need.GetType()) return FamilyRead.Unverified;
                    bool? listed = Shown(kind, need, pawn, all);
                    if (listed == null) return FamilyRead.Unverified;
                    if (listed.Value) { shown.Add(need); priorities.Add(def.listPriority); }
                    if (ReferenceEquals(tracker.mood, need)) pane = need;
                    if (kind == Kind.Food && listed.Value) food = need;
                }
                // Exact tracker.mood must be one of AllNeeds (TryGetNeed binding), else unknown.
                if (tracker.mood != null && (pane == null || pane.GetType() != typeof(Need_Mood))) return FamilyRead.Unverified;
                int[]? order = NeedsProjection.DisplayOrder(priorities);
                if (order == null) return FamilyRead.Unverified;
                var bars = new List<NeedBarPrimitive>(shown.Count + 1);
                var ordered = new List<Need>(shown.Count + 1);
                foreach (int index in order) ordered.Add(shown[index]);
                // Separate normal mood pane; a mood already in the list is reported once.
                if (pane != null && !ordered.Contains(pane)) ordered.Add(pane);
                float foodMax = 0;
                if (food != null)
                {
                    FamilyRead max = FoodMax(pawn, game, work, out foodMax);
                    if (max != FamilyRead.Value) return max;
                }
                foreach (Need need in ordered)
                {
                    float maximum = need.GetType() == typeof(Need_Food) ? foodMax
                        : need.GetType() == typeof(Need_MechEnergy) ? MechEnergy(pawn) : 1f;
                    NeedBarPrimitive? bar = NeedsProjection.Row(need.def.label, cachedLabel(need.def).RawText, curLevel(need), maximum);
                    if (bar == null) return FamilyRead.Unverified;
                    bars.Add(bar);
                }
                rows = NeedsProjection.Json(bars);
                return FamilyRead.Value;
            }
            catch { rows = null; return FamilyRead.Unverified; }
        }

        // Mirrors each admitted class's ShowOnNeedList; null = cannot be decided exactly.
        private static bool? Shown(Kind kind, Need need, Pawn pawn, List<Need> all)
        {
            NeedDef def = need.def;
            switch (kind)
            {
                case Kind.Plain: case Kind.Food: case Kind.MechEnergy: return def.showOnNeedList;
                case Kind.Outdoors: return !(Dead(pawn) || Has(all, NeedDefOf.Indoors));   // !PrefersIndoors
                case Kind.Indoors: return !(Dead(pawn) || !Has(all, NeedDefOf.Indoors));   // !PrefersOutdoors
                case Kind.ChemicalAny:
                {
                    List<Trait>? traits = pawn.story?.traits?.allTraits;
                    if (traits == null || traits.Count > MaxDefList || TraitDefOf.DrugDesire == null) return null;
                    foreach (Trait trait in traits)
                    {
                        if (trait == null) return null;
                        // TraitSet.GetTrait: first matching, unsuppressed; Disabled = Degree < 1.
                        if (trait.def == TraitDefOf.DrugDesire && !trait.Suppressed) return trait.Degree >= 1;
                    }
                    return false;
                }
                case Kind.KillThirst:
                    if (pawn.ageTracker == null) return null;
                    return pawn.ageTracker.AgeBiologicalYears >= 13 && def.showOnNeedList;
                default: return null;
            }
        }

        private static bool Dead(Pawn pawn) => pawn.health == null || pawn.health.State == PawnHealthState.Dead;
        private static bool Has(List<Need> all, NeedDef? def)
        {
            if (def == null) throw new InvalidOperationException("Needs binding unavailable.");
            foreach (Need need in all) if (need.def == def) return true;
            return false;
        }
        private static float MechEnergy(Pawn pawn) =>
            pawn.def?.race != null ? pawn.def.race.maxMechEnergy : float.NaN;

        // Need_Food.MaxLevel in play: MaxNutrition with cacheStaleAfterTicks 15. A fresh worker
        // cache entry is exactly what the normal tab shows (read only, never written). Otherwise
        // recompute from fields, admitted only when the stat definition has the audited shape and
        // no trait, gene, precept/role, life stage, gear, comp, inspiration or stuff touches it.
        private FamilyRead FoodMax(Pawn pawn, Game game, INestedWork work, out float max)
        {
            max = 0;
            if (Current.ProgramState != ProgramState.Playing) return FamilyRead.Unverified;
            StatDef? stat = StatDefOf.MaxNutrition;
            StatWorker? worker = stat == null ? null : statWorker(stat);
            if (stat == null || stat.GetType() != typeof(StatDef) || worker == null || worker.GetType() != typeof(StatWorker)
                || stat.immutable || game.tickManager == null) return FamilyRead.Unverified;
            Dictionary<Thing, StatCacheEntry>? cache = statCache(worker);
            if (cache != null && cache.TryGetValue(pawn, out StatCacheEntry entry)
                && NeedsProjection.CacheFresh(game.tickManager.TicksGame, entry.gameTick))
            {
                max = entry.statValue;
                return FamilyRead.Value;
            }

            // Stat shape: exactly the official parts and no skill/capacity/stat/post-process inputs.
            List<StatPart>? parts = stat.parts;
            if (parts == null || parts.Count != 2 || parts[0]?.GetType() != typeof(StatPart_BodySize)
                || parts[1]?.GetType() != typeof(StatPart_LifeStageMaxFood) || stat.skillNeedOffsets != null
                || stat.skillNeedFactors != null || stat.capacityOffsets != null || stat.capacityFactors != null
                || stat.statFactors != null || stat.postProcessCurve != null
                || (stat.postProcessStatFactors != null && stat.postProcessStatFactors.Count != 0)) return FamilyRead.Unverified;
            if (pawn.Stuff != null || pawn.def == null || pawn.def.race == null || pawn.health?.hediffSet?.hediffs == null
                || pawn.ageTracker == null) return FamilyRead.Unverified;

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            List<Trait>? traits = pawn.story?.traits?.allTraits;
            List<Gene>? xeno = pawn.genes?.Xenogenes, endo = pawn.genes?.Endogenes;
            List<Precept>? precepts = pawn.ideo?.Ideo?.PreceptsListForReading;
            List<Apparel>? apparel = pawn.apparel?.WornApparel;
            List<ThingWithComps>? equipment = pawn.equipment?.AllEquipmentListForReading;
            List<ThingComp>? comps = pawn.AllComps;
            if (comps == null) return FamilyRead.Unverified;
            int sources = hediffs.Count + Count(traits) + Count(xeno) + Count(endo) + Count(precepts)
                + Count(apparel) + Count(equipment) + comps.Count;
            if (sources > MaxSources * 4) return FamilyRead.Unverified;
            int cost = 16 + 2 * sources;
            if (equipment != null) foreach (ThingWithComps thing in equipment) cost += 2 * (thing?.AllComps.Count ?? 0);
            if (cost > MaxNested) return FamilyRead.Unverified;
            if (!work.TryNestedFields(cost)) return FamilyRead.Yield;

            var input = new NeedsProjection.MaxNutritionInputs { Base = stat.defaultBaseValue };
            List<StatModifier>? bases = pawn.def.statBases;
            if (bases != null)
            {
                if (bases.Count > MaxSources) return FamilyRead.Unverified;
                foreach (StatModifier modifier in bases)
                {
                    if (modifier == null) return FamilyRead.Unverified;
                    if (modifier.stat == stat) { input.Base = modifier.value; break; }
                }
            }
            input.NoSkills = pawn.skills == null;
            input.NoSkillOffset = stat.noSkillOffset; input.NoSkillFactor = stat.noSkillFactor;

            var offsets = new List<float>(); var factors = new List<float>();
            foreach (Hediff hediff in hediffs)
                if (!Contribution(hediff, stat, offsets, factors)) return FamilyRead.Unverified;
            input.HediffOffsets = offsets.ToArray(); input.HediffFactors = factors.ToArray();

            if (traits != null)
                foreach (Trait trait in traits)
                {
                    List<TraitDegreeData>? data = trait?.def?.degreeDatas;
                    if (data == null || data.Count > MaxDefList) return FamilyRead.Unverified;
                    foreach (TraitDegreeData degree in data)
                        if (degree == null || Touches(degree.statOffsets, stat) || Touches(degree.statFactors, stat)) return FamilyRead.Unverified;
                }
            foreach (List<Gene>? genes in new[] { xeno, endo })
                if (genes != null)
                    foreach (Gene gene in genes)
                    {
                        GeneDef? def = gene?.def;
                        if (def == null || Touches(def.statOffsets, stat) || Touches(def.statFactors, stat)
                            || Touches(def.conditionalStatAffecters, stat)) return FamilyRead.Unverified;
                    }
            if (precepts != null)
                foreach (Precept precept in precepts)
                {
                    PreceptDef? def = precept?.def;
                    if (def == null || Touches(def.statOffsets, stat) || Touches(def.statFactors, stat)
                        || Touches(def.conditionalStatAffecters, stat)) return FamilyRead.Unverified;
                    // Ideo.GetRole picks one of these roles; any role touching the stat refuses.
                    if (def.roleEffects != null)
                    {
                        if (def.roleEffects.Count > MaxDefList) return FamilyRead.Unverified;
                        foreach (RoleEffect effect in def.roleEffects)
                            if (effect == null || (effect is RoleEffect_PawnStatModifier modifier && modifier.statDef == stat))
                                return FamilyRead.Unverified;
                    }
                }

            // Life stage from the cached index only (CurLifeStageIndex would recalculate).
            int index = lifeStage(pawn.ageTracker);
            List<LifeStageAge>? ages = pawn.def.race.lifeStageAges;
            LifeStageDef? stage = ages != null && index >= 0 && index < ages.Count ? ages[index]?.def : null;
            if (stage == null || Touches(stage.statOffsets, stat) || Touches(stage.statFactors, stat)) return FamilyRead.Unverified;
            input.BodySizeFactor = stage.bodySizeFactor; input.BaseBodySize = pawn.def.race.baseBodySize;
            input.FoodMaxFactor = stage.foodMaxFactor;

            if (apparel != null)
                foreach (Apparel worn in apparel)
                    if (worn?.def == null || Touches(worn.def.equippedStatOffsets, stat)) return FamilyRead.Unverified;
            if (equipment != null)
                foreach (ThingWithComps held in equipment)
                {
                    if (held?.def == null || Touches(held.def.equippedStatOffsets, stat)) return FamilyRead.Unverified;
                    foreach (ThingComp comp in held.AllComps)
                    {
                        if (comp == null) return FamilyRead.Unverified;
                        if (!(comp is CompBladelinkWeapon bladelink)) continue;
                        List<WeaponTraitDef>? weaponTraits = bladelink.TraitsListForReading;
                        if (weaponTraits == null || weaponTraits.Count > MaxDefList) return FamilyRead.Unverified;
                        foreach (WeaponTraitDef trait in weaponTraits)
                            if (trait == null || Touches(trait.equippedStatOffsets, stat)) return FamilyRead.Unverified;
                    }
                }
            foreach (ThingComp comp in comps)
                if (comp == null || !NeutralComp(comp.GetType())) return FamilyRead.Unverified;
            InspirationDef? inspiration = pawn.mindState?.inspirationHandler?.CurStateDef;
            if (inspiration != null && (Touches(inspiration.statOffsets, stat) || Touches(inspiration.statFactors, stat)))
                return FamilyRead.Unverified;

            // Find.Scenario returns Current.Game.Scenario whenever it is set.
            Scenario? scenario = game.Scenario;
            if (scenario == null || !ReferenceEquals(game, Current.Game)) return FamilyRead.Unverified;
            input.ScenarioFactor = scenario.GetStatFactor(stat);
            input.RoundToFiveOver = stat.roundToFiveOver; input.RoundValue = stat.roundValue;
            input.Min = stat.minValue; input.Max = stat.maxValue;
            float? value = NeedsProjection.MaxNutrition(input);
            if (value == null) return FamilyRead.Unverified;
            max = value.Value;
            return FamilyRead.Value;
        }

        // One hediff's MaxNutrition contribution. Stages that never mention the stat contribute
        // nothing; only a single-stage definition (stage index is always 0) with plain
        // offset/factor values is mirrored. Custom CurStage classes need an explicit audit.
        private bool Contribution(Hediff hediff, StatDef stat, List<float> offsets, List<float> factors)
        {
            if (hediff?.def == null) return false;
            Type type = hediff.GetType();
            if (!stageTypes.TryGetValue(type, out bool baseStage))
            {
                if (stageTypes.Count >= MaxTypes) return false;
                Type? declaring = Getter(type, "CurStage", BindingFlags.Public, false, false).DeclaringType;
                // Audited overrides: band node (MechBandwidth only) and disruptor flash (capMods only).
                if (declaring != typeof(Hediff) && type != typeof(Hediff_BandNode) && type != typeof(Hediff_DisruptorFlash))
                { stageTypes[type] = false; return false; }
                baseStage = declaring == typeof(Hediff)
                    && Getter(type, "CurStageIndex", BindingFlags.Public, false, false).DeclaringType == typeof(Hediff);
                stageTypes[type] = baseStage;
            }
            if (type == typeof(Hediff_BandNode) || type == typeof(Hediff_DisruptorFlash)) return true;
            List<HediffStage>? stages = hediff.def.stages;
            if (stages == null || stages.Count == 0) return true;
            if (stages.Count > MaxDefList) return false;
            bool touched = false;
            foreach (HediffStage candidate in stages)
            {
                if (candidate == null) return false;
                if (Touches(candidate.statOffsets, stat) || Touches(candidate.statFactors, stat)
                    || Touches(candidate.statOffsetsBySeverity, stat) || Touches(candidate.statFactorsBySeverity, stat)) touched = true;
            }
            if (!touched) return true;
            HediffStage only = stages[0];
            if (!baseStage || stages.Count != 1 || Touches(only.statOffsetsBySeverity, stat)
                || Touches(only.statFactorsBySeverity, stat)) return false;
            float offset = First(only.statOffsets, stat, 0f), factor = First(only.statFactors, stat, 1f);
            if (offset != 0f && (only.statOffsetEffectMultiplier != null || only.multiplyStatChangesBySeverity)) return false;
            if (Math.Abs(factor - 1f) > float.Epsilon)
            {
                if (only.statFactorEffectMultiplier != null || only.multiplyStatChangesBySeverity) return false;
            }
            else factor = 1f; // GetStatFactorForSeverity falls through to its (absent) by-severity list.
            offsets.Add(offset); factors.Add(factor);
            return true;
        }

        // ThingComp.GetStatOffset/GetStatFactor base implementations return 0/1.
        private bool NeutralComp(Type type)
        {
            if (neutralComps.TryGetValue(type, out bool neutral)) return neutral;
            if (neutralComps.Count >= MaxTypes) return false;
            neutral = type.GetMethod("GetStatOffset", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(StatDef) }, null)
                    ?.DeclaringType == typeof(ThingComp)
                && type.GetMethod("GetStatFactor", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(StatDef) }, null)
                    ?.DeclaringType == typeof(ThingComp);
            neutralComps[type] = neutral;
            return neutral;
        }

        private static int Count<T>(List<T>? list) => list?.Count ?? 0;
        // True when the list mentions the stat or cannot be inspected (null entry/oversized).
        private static bool Touches(List<StatModifier>? list, StatDef stat)
        {
            if (list == null) return false;
            if (list.Count > MaxDefList) return true;
            foreach (StatModifier modifier in list) if (modifier == null || modifier.stat == stat) return true;
            return false;
        }
        private static bool Touches(List<StatModifierBySeverity>? list, StatDef stat)
        {
            if (list == null) return false;
            if (list.Count > MaxDefList) return true;
            foreach (StatModifierBySeverity modifier in list) if (modifier == null || modifier.stat == stat) return true;
            return false;
        }
        private static bool Touches(List<ConditionalStatAffecter>? list, StatDef stat)
        {
            if (list == null) return false;
            if (list.Count > MaxDefList) return true;
            foreach (ConditionalStatAffecter affecter in list)
                if (affecter == null || Touches(affecter.statOffsets, stat) || Touches(affecter.statFactors, stat)) return true;
            return false;
        }
        // StatUtility.GetStatValueFromList: first matching entry.
        private static float First(List<StatModifier>? list, StatDef stat, float fallback)
        {
            if (list != null) foreach (StatModifier modifier in list) if (modifier.stat == stat) return modifier.value;
            return fallback;
        }

        private static AccessTools.FieldRef<TOwner, TField> Field<TOwner, TField>(string name)
        {
            FieldInfo? field = typeof(TOwner).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != typeof(TField)) throw new InvalidOperationException("Needs binding unavailable.");
            return AccessTools.FieldRefAccess<TOwner, TField>(field);
        }
        private static MethodInfo DrawOnGui(Type type) => Method(type, "DrawOnGUI", typeof(UnityEngine.Rect), typeof(int), typeof(float),
            typeof(bool), typeof(bool), typeof(UnityEngine.Rect?), typeof(bool));
        private static MethodInfo Getter(Type type, string property, BindingFlags visibility = BindingFlags.Public,
            bool isStatic = false, bool declaredOnly = true) =>
            type.GetProperty(property, visibility | (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                | (declaredOnly ? BindingFlags.DeclaredOnly : BindingFlags.Default))?.GetGetMethod(true)
                ?? throw new InvalidOperationException("Needs binding unavailable.");
        private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly, null, parameters, null) ?? throw new InvalidOperationException("Needs binding unavailable.");
    }
}
