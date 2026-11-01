using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Equipped and worn gear rows for a live own pawn (docs/visibility-audit.md#gear). Game
    // thread only, after the caller's own-pawn and target gates passed. Mirrors what
    // ITab_Pawn_Gear lists in its Equipment (equipment, then worn belts) and Apparel sections
    // with each row's Thing.LabelCap, from fields: never draws, never calls label, stat,
    // quality, style or tooltip getters, never fills GenLabel's label cache or the stat cache,
    // and never reads inventory contents (only the tab's own visibility count). Unaudited thing
    // or comp classes, styled-precept or biocoded labels outside the formatter mirror, or any
    // patch on the closure omit the whole family.
    internal sealed class GameGearReader
    {
        internal const int MaxNested = 256, MaxComps = 16, MaxTypes = 256, MaxModifiers = 64;

        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly GameKeyedText text = new GameKeyedText();
        private readonly Dictionary<Type, bool> things = new Dictionary<Type, bool>();
        private readonly Dictionary<Type, CompKind?> comps = new Dictionary<Type, CompKind?>();
        private enum CompKind { Neutral, Styleable, Biocodable, Art }
        private readonly AccessTools.FieldRef<Pawn_AgeTracker, int> lifeStage;
        private readonly AccessTools.FieldRef<RaceProperties, FleshTypeDef> fleshType;
        private readonly AccessTools.FieldRef<Thing, int> hitPoints;
        private readonly AccessTools.FieldRef<Thing, ThingDef> stuff;
        private readonly AccessTools.FieldRef<CompQuality, QualityCategory> quality;
        private readonly AccessTools.FieldRef<CompStyleable, Precept_ThingStyle> sourcePrecept;
        private readonly AccessTools.FieldRef<CompBiocodable, bool> biocoded;
        private readonly AccessTools.FieldRef<Apparel, bool> wornByCorpse;
        private readonly AccessTools.FieldRef<StatDef, StatWorker> statWorker;
        private readonly AccessTools.FieldRef<StatWorker, Dictionary<Thing, StatCacheEntry>> statCache;
        private readonly AccessTools.FieldRef<StatWorker, ConcurrentDictionary<Thing, float>> immutableStatCache;
        private readonly AccessTools.FieldRef<Scenario, List<ScenPart>> scenarioParts;
        private readonly MethodBase[] closure;
        private int checkedFrame = -1;

        internal GameGearReader(int gameThread, IProjectionPatchAudit patches)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            this.gameThread = gameThread;
            this.patches = patches ?? throw new ArgumentNullException(nameof(patches));
            lifeStage = Field<Pawn_AgeTracker, int>("cachedLifeStageIndex");
            fleshType = Field<RaceProperties, FleshTypeDef>("fleshType");
            hitPoints = Field<Thing, int>("hitPointsInt");
            stuff = Field<Thing, ThingDef>("stuffInt");
            quality = Field<CompQuality, QualityCategory>("qualityInt");
            sourcePrecept = Field<CompStyleable, Precept_ThingStyle>("sourcePrecept");
            biocoded = Field<CompBiocodable, bool>("biocoded");
            wornByCorpse = Field<Apparel, bool>("wornByCorpseInt");
            statWorker = Field<StatDef, StatWorker>("workerInt");
            statCache = Field<StatWorker, Dictionary<Thing, StatCacheEntry>>("temporaryStatCache");
            immutableStatCache = Field<StatWorker, ConcurrentDictionary<Thing, float>>("immutableStatCache");
            scenarioParts = Field<Scenario, List<ScenPart>>("parts");
            if (!Admitted(typeof(ThingWithComps)) || !Admitted(typeof(Apparel)) || CompKinded(typeof(CompStyleable)) != CompKind.Styleable
                || CompKinded(typeof(CompBiocodable)) != CompKind.Biocodable || CompKinded(typeof(CompQuality)) != CompKind.Neutral)
                throw new InvalidOperationException("Gear binding unavailable.");

            closure = new MethodBase[]
            {
                // Normal tab and rows (never called).
                GameKeyedText.Getter(typeof(ITab_Pawn_Gear), "IsVisible"), Method(typeof(ITab_Pawn_Gear), "FillTab"),
                Method(typeof(ITab_Pawn_Gear), "DrawThingRow", typeof(float).MakeByRefType(), typeof(float), typeof(Thing), typeof(bool)),
                Method(typeof(ITab_Pawn_Gear), "ShouldShowInventory", typeof(Pawn)), Method(typeof(ITab_Pawn_Gear), "ShouldShowApparel", typeof(Pawn)),
                Method(typeof(ITab_Pawn_Gear), "ShouldShowEquipment", typeof(Pawn)),
                GameKeyedText.Getter(typeof(Pawn_AgeTracker), "CurLifeStage"), GameKeyedText.Getter(typeof(Pawn_AgeTracker), "CurLifeStageIndex"),
                GameKeyedText.Getter(typeof(RaceProperties), "IsAnomalyEntity"), GameKeyedText.Getter(typeof(RaceProperties), "IsDrone"),
                GameKeyedText.Getter(typeof(RaceProperties), "FleshType"), GameKeyedText.Getter(typeof(RaceProperties), "Humanlike"),
                GameKeyedText.Getter(typeof(ModsConfig), "AnomalyActive", true), GameKeyedText.Getter(typeof(ModsConfig), "OdysseyActive", true),
                // Called: plain list getters.
                GameKeyedText.Getter(typeof(Pawn_EquipmentTracker), "AllEquipmentListForReading"), GameKeyedText.Getter(typeof(Pawn_ApparelTracker), "WornApparel"),
                GameKeyedText.Getter(typeof(ThingOwner), "Count"), GameKeyedText.Getter(typeof(ThingOwner<Thing>), "Count"),
                GameKeyedText.Getter(typeof(ThingOwner<Thing>), "InnerListForReading"),
                GameKeyedText.Getter(typeof(ThingWithComps), "AllComps"),
                GameKeyedText.Getter(typeof(ThingOwner<ThingWithComps>), "InnerListForReading"), GameKeyedText.Getter(typeof(ThingOwner<Apparel>), "InnerListForReading"),
                // Label closure (mirrored).
                GameKeyedText.Getter(typeof(Thing), "LabelShort"), GameKeyedText.Getter(typeof(Thing), "LabelCap"), GameKeyedText.Getter(typeof(Thing), "Label"), GameKeyedText.Getter(typeof(Thing), "LabelNoCount"),
                GameKeyedText.Getter(typeof(ThingWithComps), "LabelNoCount"), GameKeyedText.Getter(typeof(Thing), "HitPoints"),
                GameKeyedText.Getter(typeof(Thing), "MaxHitPoints"), GameKeyedText.Getter(typeof(Thing), "Stuff"), GameKeyedText.Getter(typeof(Thing), "StyleDef"),
                GameKeyedText.Getter(typeof(ThingDef), "LabelAsStuff"), GameKeyedText.Getter(typeof(Apparel), "WornByCorpse"),
                GameKeyedText.Getter(typeof(CompQuality), "Quality"),
                Method(typeof(GenLabel), "ThingLabel", typeof(Thing), typeof(int), typeof(bool), typeof(bool)),
                Method(typeof(GenLabel), "NewThingLabel", typeof(Thing), typeof(int), typeof(bool), typeof(bool)),
                Method(typeof(GenLabel), "ThingLabel", typeof(BuildableDef), typeof(ThingDef), typeof(int)),
                Method(typeof(GenLabel), "NewThingLabel", typeof(BuildableDef), typeof(ThingDef), typeof(int)),
                Method(typeof(GenLabel), "LabelExtras", typeof(Thing), typeof(bool), typeof(bool)),
                Method(typeof(QualityUtility), "TryGetQuality", typeof(Thing), typeof(QualityCategory).MakeByRefType()),
                Method(typeof(QualityUtility), "GetLabel", typeof(QualityCategory)),
                Method(typeof(ThingStyleHelper), "GetStyleDef", typeof(Thing)),
                Method(typeof(GenText), "ToStringPercent", typeof(float)), Method(typeof(GenText), "ToStringDecimalIfSmall", typeof(float)),
                Method(typeof(GenString), "ToStringCached", typeof(int)),
                Method(typeof(ThingComp), "TransformLabel", typeof(string)), Method(typeof(CompStyleable), "TransformLabel", typeof(string)),
                Method(typeof(CompBiocodable), "TransformLabel", typeof(string)),
                Method(typeof(CompArt), "TransformLabel", typeof(string)), GameKeyedText.Getter(typeof(CompArt), "Active"),
                GameKeyedText.Getter(typeof(Thing), "StyleSourcePrecept"), GameKeyedText.Getter(typeof(CompStyleable), "SourcePrecept"),
                Method(typeof(ThingComp), "GetStatOffset", typeof(StatDef)), Method(typeof(ThingComp), "GetStatFactor", typeof(StatDef)),
                // MaxHitPoints stat (cache read or field mirror).
                Method(typeof(StatExtension), "GetStatValue", typeof(Thing), typeof(StatDef), typeof(bool), typeof(int)),
                Method(typeof(StatWorker), "GetValue", typeof(Thing), typeof(bool), typeof(int)),
                Method(typeof(StatWorker), "GetValue", typeof(StatRequest), typeof(bool)),
                Method(typeof(StatWorker), "GetValueUnfinalized", typeof(StatRequest), typeof(bool)),
                Method(typeof(StatWorker), "FinalizeValue", typeof(StatRequest), typeof(float).MakeByRefType(), typeof(bool)),
                Method(typeof(StatWorker), "GetBaseValueFor", typeof(StatRequest)),
                Method(typeof(Scenario), "GetStatFactor", typeof(StatDef)),
                Method(typeof(ScenPart_StatFactor), "GetStatFactor", typeof(StatDef)),
            }.Concat(GameKeyedText.Closure()).ToArray();
        }

        // Official/mod thing class whose label, hit-point and style members are exactly the
        // audited Thing/ThingWithComps implementations.
        private bool Admitted(Type type)
        {
            if (things.TryGetValue(type, out bool admitted)) return admitted;
            if (things.Count >= MaxTypes) return false;
            admitted = typeof(ThingWithComps).IsAssignableFrom(type) && !typeof(Pawn).IsAssignableFrom(type) && !typeof(MinifiedThing).IsAssignableFrom(type)
                && Decl(type, "LabelCap") == typeof(Thing) && Decl(type, "Label") == typeof(Thing) && Decl(type, "LabelNoCount") == typeof(ThingWithComps)
                && Decl(type, "HitPoints") == typeof(Thing) && Decl(type, "StyleDef") == typeof(Thing);
            things[type] = admitted;
            return admitted;
        }

        // Comps that leave the label and MaxHitPoints untouched, plus the two mirrored ones.
        private CompKind? CompKinded(Type type)
        {
            if (comps.TryGetValue(type, out CompKind? kind)) return kind;
            if (comps.Count >= MaxTypes) return null;
            bool stats = DeclMethod(type, "GetStatOffset") == typeof(ThingComp) && DeclMethod(type, "GetStatFactor") == typeof(ThingComp);
            Type? label = DeclMethod(type, "TransformLabel");
            kind = !stats ? null : label == typeof(ThingComp) ? CompKind.Neutral
                : type == typeof(CompStyleable) && label == typeof(CompStyleable) ? CompKind.Styleable
                : type == typeof(CompBiocodable) && label == typeof(CompBiocodable) ? CompKind.Biocodable
                : type == typeof(CompArt) && label == typeof(CompArt) ? CompKind.Art : (CompKind?)null;
            comps[type] = kind;
            return kind;
        }

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

        // Value: rows in tab order ([] = the tab shows no equipment or apparel). Unverified:
        // omit the family (including a hidden gear tab). Yield: retry next update.
        internal FamilyRead Read(Pawn pawn, Game game, INestedWork work, out JArray? rows)
        {
            rows = null;
            if (pawn == null || game == null || !Fresh()) return FamilyRead.Unverified;
            try
            {
                if (Current.ProgramState != ProgramState.Playing || !ReferenceEquals(game, Current.Game) || game.tickManager == null) return FamilyRead.Unverified;
                RaceProperties? race = pawn.def?.race;
                if (race == null || pawn.ageTracker == null || pawn.health == null || pawn.health.State == PawnHealthState.Dead) return FamilyRead.Unverified;
                // ITab_Pawn_Gear.IsVisible, from the cached life stage only.
                int index = lifeStage(pawn.ageTracker);
                List<LifeStageAge>? ages = race.lifeStageAges;
                if (ages == null || index < 0 || index >= ages.Count || ages[index] == null) return FamilyRead.Unverified;
                if (ages[index].def == LifeStageDefOf.HumanlikeBaby) return FamilyRead.Unverified;
                FleshTypeDef flesh = fleshType(race) ?? FleshTypeDefOf.Normal;
                if (ModsConfig.AnomalyActive && (flesh == FleshTypeDefOf.EntityMechanical || flesh == FleshTypeDefOf.EntityFlesh || flesh == FleshTypeDefOf.Fleshbeast))
                    return FamilyRead.Unverified;
                if (ModsConfig.OdysseyActive && flesh == FleshTypeDefOf.Drone) return FamilyRead.Unverified;
                bool humanlike = race.intelligence >= Intelligence.Humanlike;
                List<ThingWithComps>? equipment = pawn.equipment?.AllEquipmentListForReading;
                List<Apparel>? worn = pawn.apparel?.WornApparel;
                if ((pawn.equipment != null && equipment == null) || (pawn.apparel != null && worn == null)) return FamilyRead.Unverified;
                bool showApparel = worn != null && (humanlike || worn.Count > 0);
                bool showEquipment = equipment != null;
                if (!humanlike && !showApparel && !showEquipment)
                {
                    ThingOwner? inventory = pawn.inventory?.innerContainer;
                    if (inventory == null || inventory.GetType() != typeof(ThingOwner<Thing>)) return FamilyRead.Unverified;
                    if (inventory.Count == 0) return FamilyRead.Unverified; // Tab hidden.
                }
                // The equipment section adds worn belts; without an apparel tracker the game throws.
                if (showEquipment && worn == null) return FamilyRead.Unverified;

                int total = (showEquipment ? equipment!.Count : 0) + (worn?.Count ?? 0);
                if (total > GearProjection.MaxItems) return FamilyRead.Unverified;
                int cost = 8;
                foreach (ThingWithComps? thing in (showEquipment ? (IEnumerable<ThingWithComps>)equipment! : new ThingWithComps[0]).Concat(worn ?? new List<Apparel>()))
                {
                    List<ThingComp>? list = thing?.AllComps;
                    if (list == null || list.Count > MaxComps) return FamilyRead.Unverified;
                    if (thing == null) return FamilyRead.Unverified;
                    cost += LabelCost(thing, game);
                }
                if (cost > MaxNested) return FamilyRead.Unverified;
                if (!work.TryNestedFields(cost)) return FamilyRead.Yield;

                var entries = new List<GearEntry>();
                if (showEquipment)
                {
                    foreach (ThingWithComps thing in equipment!) entries.Add(new GearEntry(LabelCap(thing, game), true));
                    foreach (Apparel apparel in worn!) if (Belt(apparel)) entries.Add(new GearEntry(LabelCap(apparel, game), true));
                }
                if (showApparel)
                {
                    var shown = new List<Apparel>(); var orders = new List<int>();
                    foreach (Apparel apparel in worn!)
                    {
                        if (Belt(apparel)) continue;
                        List<BodyPartGroupDef>? groups = apparel.def.apparel.bodyPartGroups;
                        if (groups == null || groups.Count == 0 || groups[0] == null) return FamilyRead.Unverified; // Game would throw.
                        shown.Add(apparel); orders.Add(groups[0].listOrder);
                    }
                    foreach (int i in GearProjection.ApparelOrder(orders)) entries.Add(new GearEntry(LabelCap(shown[i], game), false));
                }
                rows = GearProjection.Rows(entries);
                return rows == null ? FamilyRead.Unverified : FamilyRead.Value;
            }
            catch { rows = null; return FamilyRead.Unverified; }
        }

        private static bool Belt(Apparel apparel)
        {
            List<ApparelLayerDef>? layers = apparel.def?.apparel?.layers;
            if (layers == null) throw Refuse();
            return layers.Contains(ApparelLayerDefOf.Belt);
        }

        // Conservative per-label traversal charge, shared by gear rows and job targets.
        // Count checks precede traversal; scenario GetStatFactor is a finite nonvirtual loop.
        internal int LabelCost(ThingWithComps thing, Game game)
        {
            if (!Fresh()) throw Refuse();
            ThingDef? material = stuff(thing);
            StuffProperties? props = material?.stuffProps;
            List<ScenPart>? parts = game.Scenario == null ? null : scenarioParts(game.Scenario);
            int[] counts = { thing.AllComps?.Count ?? 0, thing.def?.statBases?.Count ?? 0,
                props?.statFactors?.Count ?? 0, props?.statOffsets?.Count ?? 0,
                props?.statFactorsQuality?.Count ?? 0, props?.statOffsetsQuality?.Count ?? 0, parts?.Count ?? 0 };
            int cost = 18;
            foreach (int count in counts) { if (count > MaxModifiers) throw Refuse(); cost += count; }
            if (cost > MaxNested) throw Refuse();
            return cost;
        }

        // Thing.LabelCap = Label.CapitalizeFirst(def); Label adds " xN" to LabelNoCount for a
        // stack; ThingWithComps.LabelNoCount runs each comp's TransformLabel over
        // GenLabel.ThingLabel(this, 1) (cache-equivalent NewThingLabel).
        internal string LabelCap(ThingWithComps thing, Game game)
        {
            string label = LabelShort(thing, game);
            if (thing.stackCount > 1) label += " x" + thing.stackCount.ToString();
            return text.Cap(label, thing.def);
        }

        // Current-job legacy target text uses LabelShort (no stack suffix or capitalization).
        // Caller reserves bounded work and re-gates the directly spawned target first.
        internal string LabelShort(ThingWithComps thing, Game game)
        {
            if (!Fresh() || thing == null || thing.def == null || !Admitted(thing.GetType())) throw Refuse();
            List<ThingComp> list = thing.AllComps;
            if (list == null || list.Count > MaxComps) throw Refuse();
            foreach (ThingComp comp in list) if (comp == null || CompKinded(comp.GetType()) == null) throw Refuse();
            string label = BaseLabel(thing, game);
            foreach (ThingComp comp in list)
            {
                switch (CompKinded(comp.GetType()))
                {
                    case CompKind.Styleable: if (sourcePrecept((CompStyleable)comp) != null) throw Refuse(); break;
                    case CompKind.Art:
                        // Normal weapons carry CompArt even at normal quality. Exact CompArt.Active
                        // only reads taleRef; without a style source its label is unchanged, whether
                        // active or not. Never evaluate Title (which may write/log) or art contents.
                        if (thing.compStyleable != null && sourcePrecept(thing.compStyleable) != null) throw Refuse();
                        break;
                    case CompKind.Biocodable:
                        if (biocoded((CompBiocodable)comp))
                            label = text.Format("Biocoded", FormatArg.Text(label), FormatArg.Def(null, thing.def.label ?? throw Refuse()));
                        break;
                }
            }
            return label;
        }

        // GenLabel.NewThingLabel(t, 1, includeHp: true, includeQuality: true).
        private string BaseLabel(ThingWithComps thing, Game game)
        {
            ThingDef def = thing.def;
            ThingDef? material = stuff(thing);
            string? style = thing.compStyleable?.styleDef?.overrideLabel;
            string label;
            if (!string.IsNullOrEmpty(style)) label = style!;
            else if (material != null)
            {
                if (material.stuffProps == null) throw Refuse();
                string adjective = !string.IsNullOrEmpty(material.stuffProps.stuffAdjective) ? material.stuffProps.stuffAdjective : material.label;
                label = text.Format("ThingMadeOfStuffLabel", FormatArg.Text(adjective ?? throw Refuse()), FormatArg.Text(def.label ?? throw Refuse()));
            }
            else label = def.label ?? throw Refuse();

            CompQuality? qualityComp = thing.compQuality;
            string? qualityLabel = qualityComp == null ? null : QualityLabel(quality(qualityComp));
            string? hp = null;
            if (def.useHitPoints && def.stackLimit == 1)
            {
                int current = hitPoints(thing), max = MaxHitPoints(thing, def, material, qualityComp, game);
                if (current < max) hp = GearProjection.Percent((float)current / (float)max);
            }
            string? corpse = thing is Apparel apparel && wornByCorpse(apparel) ? text.Keyed("WornByCorpseChar") : null;
            return label + GearProjection.Extras(qualityLabel, hp, corpse);
        }

        private string QualityLabel(QualityCategory category)
        {
            switch (category)
            {
                case QualityCategory.Awful: return text.Keyed("QualityCategory_Awful");
                case QualityCategory.Poor: return text.Keyed("QualityCategory_Poor");
                case QualityCategory.Normal: return text.Keyed("QualityCategory_Normal");
                case QualityCategory.Good: return text.Keyed("QualityCategory_Good");
                case QualityCategory.Excellent: return text.Keyed("QualityCategory_Excellent");
                case QualityCategory.Masterwork: return text.Keyed("QualityCategory_Masterwork");
                case QualityCategory.Legendary: return text.Keyed("QualityCategory_Legendary");
                default: throw Refuse();
            }
        }

        // Thing.MaxHitPoints: RoundToInt(GetStatValue(MaxHitPoints, true, 10)). A fresh worker
        // cache entry is what the tab shows (read only); otherwise recompute from fields when
        // the stat has no parts/factors/post-processing and every comp is stat-neutral.
        private int MaxHitPoints(ThingWithComps thing, ThingDef def, ThingDef? material, CompQuality? qualityComp, Game game)
        {
            StatDef? stat = StatDefOf.MaxHitPoints;
            StatWorker? worker = stat == null ? null : statWorker(stat);
            if (stat == null || stat.GetType() != typeof(StatDef) || worker == null || worker.GetType() != typeof(StatWorker)
                || stat.workerClass != typeof(StatWorker)) throw Refuse();
            // StatWorker.GetValue checks this persistent cache first for immutable stats.
            // Reading it preserves the normal label even if its inputs have since changed;
            // a miss follows the same audited field recomputation, never cache insertion.
            if (stat.immutable)
            {
                ConcurrentDictionary<Thing, float>? permanent = immutableStatCache(worker);
                if (permanent == null) throw Refuse(); // The normal worker would fail here too.
                if (permanent.TryGetValue(thing, out float cached)) return GearProjection.CachedHitPoints(cached) ?? throw Refuse();
            }
            else
            {
                Dictionary<Thing, StatCacheEntry>? cache = statCache(worker);
                if (cache != null && cache.TryGetValue(thing, out StatCacheEntry entry) && game.tickManager.TicksGame - entry.gameTick < 10)
                    return GearProjection.CachedHitPoints(entry.statValue) ?? throw Refuse();
            }
            if ((stat.parts != null && stat.parts.Count != 0) || stat.postProcessCurve != null || stat.statFactors != null
                || (stat.postProcessStatFactors != null && stat.postProcessStatFactors.Count != 0)) throw Refuse();
            var input = new GearProjection.MaxHitPointsInputs { Base = stat.defaultBaseValue, ApplyFactorsIfNegative = stat.applyFactorsIfNegative };
            if (def.statBases != null)
            {
                if (def.statBases.Count > MaxModifiers) throw Refuse();
                foreach (StatModifier modifier in def.statBases)
                {
                    if (modifier == null) throw Refuse();
                    if (modifier.stat == stat) { input.Base = modifier.value; break; }
                }
            }
            if (material != null)
            {
                StuffProperties props = material.stuffProps ?? throw Refuse();
                input.HasStuff = true;
                input.StuffFactor = Lookup(props.statFactors, stat, 1f);
                input.StuffOffset = Lookup(props.statOffsets, stat, 0f);
                // Quality-dependent stuff modifiers for this stat are not mirrored.
                if (qualityComp != null && (Touches(props.statFactorsQuality, stat) || Touches(props.statOffsetsQuality, stat))) throw Refuse();
            }
            Scenario? scenario = game.Scenario;
            if (scenario != null)
            {
                if (scenarioParts(scenario) == null || scenarioParts(scenario).Count > MaxModifiers) throw Refuse();
                input.ScenarioFactor = scenario.GetStatFactor(stat);
            }
            input.RoundToFiveOver = stat.roundToFiveOver; input.RoundValue = stat.roundValue;
            input.Min = stat.minValue; input.Max = stat.maxValue;
            return GearProjection.MaxHitPoints(input) ?? throw Refuse();
        }

        private static float Lookup(List<StatModifier>? list, StatDef stat, float fallback)
        {
            if (list == null) return fallback;
            if (list.Count > MaxModifiers) throw Refuse();
            foreach (StatModifier modifier in list)
            {
                if (modifier == null) throw Refuse();
                if (modifier.stat == stat) return modifier.value;
            }
            return fallback;
        }

        private static bool Touches(List<StatModifierQuality>? list, StatDef stat)
        {
            if (list == null) return false;
            if (list.Count > MaxModifiers) throw Refuse();
            foreach (StatModifierQuality modifier in list) if (modifier == null || modifier.stat == stat) return true;
            return false;
        }

        private static Exception Refuse() => new InvalidOperationException("Gear row unverified.");
        private static Type? Decl(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true)?.DeclaringType;
        private static Type? DeclMethod(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType;
        private static MethodInfo Method(Type type, string name, params Type[] parameters) => GameKeyedText.Method(type, name, parameters);
        private static AccessTools.FieldRef<TOwner, TField> Field<TOwner, TField>(string name)
        {
            FieldInfo? field = typeof(TOwner).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != typeof(TField)) throw new InvalidOperationException("Gear binding unavailable.");
            return AccessTools.FieldRefAccess<TOwner, TField>(field);
        }
    }
}
