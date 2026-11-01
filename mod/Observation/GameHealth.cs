using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimWorld;
using Verse;

namespace RimPilot.Mod.Observation
{
    // Normal health-card rows for a live own pawn (docs/visibility-audit.md#health-rows). Game
    // thread only, after the caller's own-pawn and target gates passed. Mirrors what
    // ITab_Pawn_Health → DrawPawnHealthCard(showBloodLoss = true for a live pawn) lists with
    // hidden-condition/debug display off: missing-part common ancestors, then every other
    // Visible condition, grouped by part and UIGroupKey. Everything is mirrored from fields:
    // never draws, never calls tooltips, label/visibility getters, translation, the
    // missing-parts cache builder or any health aggregate. Formatted keyed labels (injury tool
    // sources, psylink level, chemical dependency) go through the restricted formatter mirror.
    // Unaudited hediff/comp classes or any patch on the closure omit the whole family.
    internal sealed class GameHealthReader
    {
        // Charged in one step; leaves room for gate, identity and needs in the same update.
        internal const int MaxNested = 320, MaxHediffs = 128, MaxComps = 16, MaxTypes = 512;
        private enum Kind { Base, Injury, Missing, Level, Addiction, Dependency, Hangover, RotStink, Never }
        private enum CompKind { Base, Immunizable, Disappears, SeverityPerDay }

        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly Dictionary<Type, Kind?> kinds = new Dictionary<Type, Kind?>();
        private readonly Dictionary<Type, CompKind?> compKinds = new Dictionary<Type, CompKind?>();
        private readonly Dictionary<Type, bool> concealable = new Dictionary<Type, bool>();
        private readonly AccessTools.FieldRef<Hediff, bool> visible;
        private readonly AccessTools.FieldRef<Hediff, float> severity;
        private readonly AccessTools.FieldRef<Hediff_MissingPart, bool> fresh;
        private readonly AccessTools.FieldRef<HediffComp_GetsPermanent, PainCategory> pain;
        private readonly AccessTools.FieldRef<ImmunityHandler, List<ImmunityRecord>> immunities;
        private readonly AccessTools.FieldRef<HediffSet, List<Hediff_MissingPart>> cachedAncestors;
        private readonly AccessTools.FieldRef<BodyPartRecord, string> cachedPartLabel;
        private readonly AccessTools.FieldRef<Pawn_NeedsTracker, List<Need>> needList;
        private readonly AccessTools.FieldRef<Need, float> curLevel;
        private readonly AccessTools.FieldRef<Hediff_ChemicalDependency, Gene_ChemicalDependency> linkedGene;
        private readonly AccessTools.FieldRef<Pawn_GeneTracker, List<Gene>> xenogenes, endogenes, cachedGenes;
        private readonly AccessTools.FieldRef<Pawn_AgeTracker, long> biologicalTicks;
        private readonly AccessTools.FieldRef<Pawn_MutantTracker, MutantDef> mutantDef;
        private readonly MethodBase[] closure;
        private readonly GameKeyedText text = new GameKeyedText();
        private int checkedFrame = -1;

        internal GameHealthReader(int gameThread, IProjectionPatchAudit patches)
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            this.gameThread = gameThread;
            this.patches = patches ?? throw new ArgumentNullException(nameof(patches));
            visible = Field<Hediff, bool>("visible");
            severity = Field<Hediff, float>("severityInt");
            fresh = Field<Hediff_MissingPart, bool>("isFreshInt");
            pain = Field<HediffComp_GetsPermanent, PainCategory>("painCategory");
            immunities = Field<ImmunityHandler, List<ImmunityRecord>>("immunityList");
            cachedAncestors = Field<HediffSet, List<Hediff_MissingPart>>("cachedMissingPartsCommonAncestors");
            cachedPartLabel = Field<BodyPartRecord, string>("cachedCustomLabelCap");
            needList = Field<Pawn_NeedsTracker, List<Need>>("needs");
            curLevel = Field<Need, float>("curLevelInt");
            linkedGene = Field<Hediff_ChemicalDependency, Gene_ChemicalDependency>("cachedDependencyGene");
            xenogenes = Field<Pawn_GeneTracker, List<Gene>>("xenogenes");
            endogenes = Field<Pawn_GeneTracker, List<Gene>>("endogenes");
            cachedGenes = Field<Pawn_GeneTracker, List<Gene>>("cachedGenes");
            biologicalTicks = Field<Pawn_AgeTracker, long>("ageBiologicalTicksInt");
            mutantDef = Field<Pawn_MutantTracker, MutantDef>("def");

            // Installed-build shape the mirrors below were audited against.
            Expect(typeof(Hediff_Injury), Kind.Injury, ("LabelBase", typeof(Hediff_Injury)), ("LabelInBrackets", typeof(Hediff_Injury)), ("UIGroupKey", typeof(Hediff_Injury)));
            Expect(typeof(Hediff_MissingPart), Kind.Missing, ("LabelBase", typeof(Hediff_MissingPart)), ("LabelInBrackets", typeof(Hediff_MissingPart)));
            Expect(typeof(Hediff_Addiction), Kind.Addiction, ("LabelInBrackets", typeof(Hediff_Addiction)), ("CurStageIndex", typeof(Hediff_Addiction)));
            Expect(typeof(Hediff_ChemicalDependency), Kind.Dependency, ("LabelBase", typeof(Hediff_ChemicalDependency)), ("Severity", typeof(Hediff_ChemicalDependency)));
            Expect(typeof(Hediff_Hangover), Kind.Hangover, ("Visible", typeof(Hediff_Hangover)));
            if (Kinded(typeof(Hediff_Level)) != Kind.Level || Kinded(typeof(Hediff_Psylink)) != Kind.Level
                || Decl(typeof(Gene_ChemicalDependency), "Active") != typeof(Gene_ChemicalDependency) || Decl(typeof(Gene), "Active") != typeof(Gene)
                || Decl(typeof(Need_Chemical), "CurLevel") != typeof(Need_Chemical) || Decl(typeof(Need_Chemical), "CurCategory") != typeof(Need_Chemical))
                throw new InvalidOperationException("Health binding unavailable.");
            foreach (Type comp in new[] { typeof(HediffComp_Immunizable), typeof(HediffComp_Disappears), typeof(HediffComp_SeverityPerDay) })
                if (Decl(comp, "CompLabelInBracketsExtra") != comp || !BaseComp(comp, false)) throw new InvalidOperationException("Health binding unavailable.");
            // Classes that override only visibility: a missing or changed shape just leaves them
            // unadmitted (refused when the card could show them, skipped when it hides them).
            Register(typeof(Hediff_RotStinkExposure), Kind.RotStink, ("Visible", typeof(Hediff_RotStinkExposure)));
            Register(typeof(Hediff_PsychicBondTorn), Kind.Never, ("Visible", typeof(Hediff_PsychicBondTorn)), ("LabelBase", typeof(Hediff_PsychicBondTorn)));
            if (Kinded(typeof(Hediff_AddedPart)) != Kind.Base || Kinded(typeof(HediffWithComps)) != Kind.Base
                || CompKinded(typeof(HediffComp_TendDuration)) != CompKind.Base || CompKinded(typeof(HediffComp_GetsPermanent)) != CompKind.Base)
                throw new InvalidOperationException("Health binding unavailable.");

            closure = new MethodBase[]
            {
                // Normal caller and card (never called).
                Method(typeof(ITab_Pawn_Health), "FillTab"),
                Method(typeof(HealthCardUtility), "ShowBloodLoss", typeof(Thing)),
                Method(typeof(HealthCardUtility), "DrawPawnHealthCard", typeof(UnityEngine.Rect), typeof(Pawn), typeof(bool), typeof(bool), typeof(Thing)),
                Method(typeof(HealthCardUtility), "DrawHediffListing", typeof(UnityEngine.Rect), typeof(Pawn), typeof(bool), typeof(float), typeof(bool)),
                Method(typeof(HealthCardUtility), "VisibleHediffGroupsInOrder", typeof(Pawn), typeof(bool)),
                Method(typeof(HealthCardUtility), "VisibleHediffs", typeof(Pawn), typeof(bool)),
                Method(typeof(HealthCardUtility), "GetListPriority", typeof(BodyPartRecord)),
                Method(typeof(HealthCardUtility), "DrawHediffRow", typeof(UnityEngine.Rect), typeof(Pawn), typeof(IEnumerable<Hediff>), typeof(float).MakeByRefType(), typeof(float)),
                // Missing-part projection (mirrored).
                Method(typeof(HediffSet), "GetMissingPartsCommonAncestors"), Method(typeof(HediffSet), "CacheMissingPartsCommonAncestors"),
                Method(typeof(HediffSet), "PartOrAnyAncestorHasDirectlyAddedParts", typeof(BodyPartRecord)),
                Method(typeof(HediffSet), "HasDirectlyAddedPartFor", typeof(BodyPartRecord)),
                // Row labels, visibility and grouping (mirrored).
                Getter(typeof(Hediff), "Visible"), Getter(typeof(Hediff), "Label"), Getter(typeof(Hediff), "LabelBase"),
                Getter(typeof(Hediff), "LabelInBrackets"), Getter(typeof(Hediff), "LabelCap"), Getter(typeof(Hediff), "UIGroupKey"),
                Getter(typeof(Hediff), "CurStage"), Getter(typeof(Hediff), "CurStageIndex"), Getter(typeof(Hediff), "Severity"),
                Getter(typeof(Hediff), "Part"),
                Getter(typeof(HediffWithComps), "LabelBase"), Getter(typeof(HediffWithComps), "LabelInBrackets"), Getter(typeof(HediffWithComps), "Visible"),
                Getter(typeof(Hediff_Injury), "LabelBase"), Getter(typeof(Hediff_Injury), "LabelInBrackets"), Getter(typeof(Hediff_Injury), "UIGroupKey"),
                Getter(typeof(Hediff_MissingPart), "LabelBase"), Getter(typeof(Hediff_MissingPart), "LabelInBrackets"),
                Getter(typeof(Hediff_MissingPart), "IsFresh"), Getter(typeof(Hediff_MissingPart), "IsFreshNonSolidExtremity"),
                Getter(typeof(Hediff_MissingPart), "ParentIsMissing", BindingFlags.NonPublic),
                Getter(typeof(HediffComp), "CompLabelPrefix"), Getter(typeof(HediffComp), "CompLabelInBracketsExtra"),
                Method(typeof(HediffComp), "CompDisallowVisible"),
                Getter(typeof(HediffComp_Immunizable), "CompLabelInBracketsExtra"), Getter(typeof(HediffComp_Immunizable), "FullyImmune"),
                Getter(typeof(HediffComp_Immunizable), "Immunity"), Getter(typeof(HediffComp_Immunizable), "Hidden", BindingFlags.NonPublic),
                Getter(typeof(HediffComp_Disappears), "CompLabelInBracketsExtra"), Getter(typeof(HediffComp_SeverityPerDay), "CompLabelInBracketsExtra"),
                Getter(typeof(HediffComp_Disappears), "Props"),
                Getter(typeof(HediffComp_Disappears), "EffectiveTicksToDisappear"), Getter(typeof(HediffComp_Disappears), "TicksLostPerTick"),
                Method(typeof(GenTicks), "ToStringSecondsFromTicks", typeof(int), typeof(string)),
                Method(typeof(GenTicks), "TicksToSeconds", typeof(int)),
                Method(typeof(GenDate), "ToStringTicksToPeriod", typeof(int), typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool)),
                Getter(typeof(HediffComp_GetsPermanent), "IsPermanent"), Getter(typeof(HediffComp_GetsPermanent), "PainCategory"),
                Getter(typeof(HediffComp_TendDuration), "IsTended"),
                Method(typeof(HediffUtility), "IsTended", typeof(Hediff)), Method(typeof(HediffUtility), "IsPermanent", typeof(Hediff)),
                Method(typeof(ImmunityHandler), "GetImmunity", typeof(HediffDef), typeof(bool)),
                Method(typeof(HediffDef), "StageAtSeverity", typeof(float)),
                Method(typeof(HealthUtility), "GetGeneralDestroyedPartLabel", typeof(BodyPartRecord), typeof(bool), typeof(bool)),
                Getter(typeof(BodyPartRecord), "LabelCap"),
                Method(typeof(Gen), "HashCombineInt", typeof(int), typeof(int)),
                Method(typeof(GenText), "ToStringPercent", typeof(float), typeof(string)),
                Getter(typeof(BodyPartGroupDef), "LabelShort"),
                // Psylink/level, addiction, chemical dependency and hangover overrides (mirrored).
                Getter(typeof(Hediff_Level), "Label"),
                Getter(typeof(Hediff_Addiction), "LabelInBrackets"), Getter(typeof(Hediff_Addiction), "CurStageIndex"), Getter(typeof(Hediff_Addiction), "Need"),
                Method(typeof(HediffDef), "CompProps").MakeGenericMethod(typeof(HediffCompProperties_SeverityPerDay)),
                Method(typeof(Pawn_NeedsTracker), "TryGetNeed", typeof(NeedDef), typeof(Need).MakeByRefType()),
                Getter(typeof(Need_Chemical), "CurCategory"), Getter(typeof(Need_Chemical), "CurLevel"), Getter(typeof(Need), "CurLevel"),
                Getter(typeof(Hediff_ChemicalDependency), "LabelBase"), Getter(typeof(Hediff_ChemicalDependency), "Severity"),
                Getter(typeof(Hediff_ChemicalDependency), "LinkedGene"), Getter(typeof(Pawn_GeneTracker), "GenesListForReading"),
                Getter(typeof(Gene_ChemicalDependency), "Active"), Getter(typeof(Gene), "Active"), Getter(typeof(Gene), "Overridden"),
                Getter(typeof(Pawn_AgeTracker), "AgeBiologicalYears"), Getter(typeof(Pawn_MutantTracker), "Def"),
                Getter(typeof(Pawn), "IsGhoul"), Getter(typeof(Pawn), "IsMutant"), Getter(typeof(Pawn), "Dead"), Getter(typeof(ModsConfig), "AnomalyActive", BindingFlags.Public, true),
                Getter(typeof(Hediff_Hangover), "Visible"), Getter(typeof(Hediff_RotStinkExposure), "Visible"), Getter(typeof(Hediff_PsychicBondTorn), "Visible"),
                Method(typeof(HediffSet), "HasHediff", typeof(HediffDef), typeof(bool)),
                // Called: pure solidity walk over the hediff list and plain field getters.
                Method(typeof(BodyPartDef), "IsSolid", typeof(BodyPartRecord), typeof(List<Hediff>)),
                Getter(typeof(Pawn_HealthTracker), "State"), Getter(typeof(BodyDef), "AllParts"),
            }.Concat(GameKeyedText.Closure()).ToArray();
        }

        // Exact audited class: the named members are its own mirrored overrides, the rest base.
        private void Expect(Type type, Kind kind, params (string Member, Type Declaring)[] overrides)
        {
            if (!Shaped(type, overrides)) throw new InvalidOperationException("Health binding unavailable.");
            kinds[type] = kind;
        }

        private void Register(Type type, Kind kind, params (string Member, Type Declaring)[] overrides)
        {
            if (Shaped(type, overrides)) kinds[type] = kind;
        }

        // Label/visibility/stage/grouping members the card reads. Unlisted members must be
        // Hediff's (label/visibility may also be HediffWithComps's) audited implementation.
        private static readonly string[] Members = { "Label", "LabelCap", "CurStage", "CurStageIndex", "Severity", "Part", "UIGroupKey", "LabelBase", "LabelInBrackets", "Visible" };
        private static bool Shaped(Type type, params (string Member, Type Declaring)[] overrides)
        {
            if (type.Assembly != typeof(Hediff).Assembly) return false;
            foreach (string member in Members)
            {
                Type? declaring = Decl(type, member), expected = null;
                foreach ((string Member, Type Declaring) entry in overrides) if (entry.Member == member) expected = entry.Declaring;
                if (expected != null ? declaring != expected
                    : declaring != typeof(Hediff) && !(declaring == typeof(HediffWithComps) && (member == "LabelBase" || member == "LabelInBrackets" || member == "Visible")))
                    return false;
            }
            return true;
        }

        // Official class whose card members are exactly Hediff's/HediffWithComps's, or whose
        // only override is Hediff_Level's label (psylink). Anything else is unverified.
        private Kind? Kinded(Type type)
        {
            if (kinds.TryGetValue(type, out Kind? kind)) return kind;
            if (kinds.Count >= MaxTypes) return null;
            kind = Shaped(type) ? Kind.Base : Shaped(type, ("Label", typeof(Hediff_Level))) ? Kind.Level : (Kind?)null;
            kinds[type] = kind;
            return kind;
        }

        // An official, unadmitted class whose visibility, stage and severity members are exactly
        // Hediff's (Visible may also be HediffWithComps's, whose comps can only hide more): its
        // card visibility is fully decided by the visible flag and the stage fields, so "hidden"
        // is trustworthy even though its labels are not mirrored. Missing/added parts excluded.
        private bool ConcealableShape(Type type)
        {
            if (concealable.TryGetValue(type, out bool known)) return known;
            if (concealable.Count >= MaxTypes) return false;
            Type? visibility = Decl(type, "Visible");
            bool shape = type.Assembly == typeof(Hediff).Assembly
                && !typeof(Hediff_MissingPart).IsAssignableFrom(type) && !typeof(Hediff_AddedPart).IsAssignableFrom(type)
                && (visibility == typeof(Hediff) || visibility == typeof(HediffWithComps))
                && Decl(type, "CurStage") == typeof(Hediff) && Decl(type, "CurStageIndex") == typeof(Hediff) && Decl(type, "Severity") == typeof(Hediff);
            concealable[type] = shape;
            return shape;
        }

        // Does the normal tab definitively hide this condition right now? Exact for the classes
        // with mirrored visibility and for ConcealableShape classes; false (not "visible") otherwise.
        private bool Concealed(Hediff hediff, Kind? kind, List<Hediff> hediffs)
        {
            if (hediff is Hediff_MissingPart || hediff is Hediff_AddedPart) return false;
            if (kind == Kind.Never) return true;
            if (kind == null && !ConcealableShape(hediff.GetType())) return false;
            HediffStage? stage = Stage(hediff);
            bool baseHidden = HealthProjection.HiddenByFields(visible(hediff), stage != null, stage != null && stage.becomeVisible);
            if (kind == Kind.RotStink) return HealthProjection.RotStinkHidden(baseHidden, Has(hediffs, HediffDefOf.LungRot));
            if (kind == Kind.Hangover && Has(hediffs, HediffDefOf.AlcoholHigh)) return true;
            return baseHidden;
        }

        // HediffSet.HasHediff(def, mustBeVisible: false) over the list, without calling it.
        private static bool Has(List<Hediff> hediffs, HediffDef? def)
        {
            foreach (Hediff other in hediffs) if (other.def == def) return true;
            return false;
        }

        private CompKind? CompKinded(Type type)
        {
            if (compKinds.TryGetValue(type, out CompKind? kind)) return kind;
            if (compKinds.Count >= MaxTypes) return null;
            if (type == typeof(HediffComp_Immunizable)) kind = CompKind.Immunizable;
            else if (type == typeof(HediffComp_Disappears)) kind = CompKind.Disappears;
            else if (type == typeof(HediffComp_SeverityPerDay)) kind = CompKind.SeverityPerDay;
            else kind = type.Assembly == typeof(Hediff).Assembly && BaseComp(type, true) ? CompKind.Base : (CompKind?)null;
            compKinds[type] = kind;
            return kind;
        }
        private static bool BaseComp(Type type, bool brackets) =>
            Decl(type, "CompLabelPrefix") == typeof(HediffComp) && (!brackets || Decl(type, "CompLabelInBracketsExtra") == typeof(HediffComp))
            && type.GetMethod("CompDisallowVisible", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)?.DeclaringType == typeof(HediffComp);

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

        // Value: rows in normal card order ([] = "no health conditions"). Unverified: omit the
        // family. Yield: retry next update. Never a partial list.
        internal FamilyRead Read(Pawn pawn, INestedWork work, out JArray? rows)
        {
            rows = null;
            if (pawn == null || !Fresh()) return FamilyRead.Unverified;
            try
            {
                if (Current.ProgramState != ProgramState.Playing) return FamilyRead.Unverified;
                Pawn_HealthTracker? health = pawn.health;
                HediffSet? set = health?.hediffSet;
                List<Hediff>? hediffs = set?.hediffs;
                BodyDef? body = pawn.def?.race?.body;
                List<BodyPartRecord>? all = body?.AllParts;
                // Live pawns only: corpses use a different blood-loss rule and caller.
                if (health == null || health.GetType() != typeof(Pawn_HealthTracker) || health.State == PawnHealthState.Dead
                    || set == null || set.GetType() != typeof(HediffSet) || hediffs == null || hediffs.Count > MaxHediffs
                    || body?.corePart == null || all == null) return FamilyRead.Unverified;
                int comps = 0;
                foreach (Hediff hediff in hediffs)
                    if (hediff is HediffWithComps withComps)
                    {
                        if (withComps.comps == null || withComps.comps.Count > MaxComps) return FamilyRead.Unverified;
                        comps += withComps.comps.Count;
                    }
                // Addictions scan the need list; chemical dependencies scan the gene lists.
                int extra = 0;
                foreach (Hediff hediff in hediffs)
                {
                    if (hediff is Hediff_Addiction && pawn.needs != null) extra += needList(pawn.needs)?.Count ?? 0;
                    if (hediff is Hediff_ChemicalDependency && pawn.genes != null)
                        extra += (xenogenes(pawn.genes)?.Count ?? 0) + (endogenes(pawn.genes)?.Count ?? 0);
                }
                int cost = 8 + 4 * hediffs.Count + all.Count + comps + extra;
                if (cost > MaxNested) return FamilyRead.Unverified;
                if (!work.TryNestedFields(cost)) return FamilyRead.Yield;

                // Admission plus the added/missing indexes the common-ancestor walk needs.
                var added = new HashSet<BodyPartRecord>();
                bool addedWholeBody = false;
                var firstMissing = new Dictionary<BodyPartRecord, Hediff_MissingPart>();
                // Conditions the normal tab definitively hides: never shown, never refuse the
                // family (field presence must not reveal hidden state), decided before admission.
                var concealed = new HashSet<Hediff>();
                foreach (Hediff hediff in hediffs)
                {
                    if (hediff?.def == null || !ReferenceEquals(hediff.pawn, pawn)) return FamilyRead.Unverified;
                    Kind? kind = Kinded(hediff.GetType());
                    bool structural = hediff is Hediff_MissingPart || hediff is Hediff_AddedPart;
                    bool shape = kind != null || (!structural && ConcealableShape(hediff.GetType()));
                    HealthFate fate = HealthProjection.Fate(structural, kind != null, shape, shape && Concealed(hediff, kind, hediffs));
                    if (fate == HealthFate.Skip) { concealed.Add(hediff); continue; }
                    if (fate == HealthFate.Refuse || kind == null) return FamilyRead.Unverified;
                    if (hediff is HediffWithComps withComps)
                        foreach (HediffComp comp in withComps.comps)
                            if (comp == null || !ReferenceEquals(comp.parent, hediff) || CompKinded(comp.GetType()) == null) return FamilyRead.Unverified;
                    BodyPartRecord? part = hediff.Part;
                    if (hediff is Hediff_AddedPart) { if (part == null) addedWholeBody = true; else added.Add(part); }
                    if (hediff is Hediff_MissingPart missing && part != null && !firstMissing.ContainsKey(part)) firstMissing[part] = missing;
                }
                List<Hediff_MissingPart>? ancestors = HealthProjection.CommonAncestors<BodyPartRecord, Hediff_MissingPart>(body.corePart,
                    p => p.parent, p => p.parts, p => added.Contains(p), p => firstMissing.TryGetValue(p, out Hediff_MissingPart? m) ? m : null);
                if (ancestors == null) return FamilyRead.Unverified;
                // A built cache is what the card draws; it must agree with the mirror.
                List<Hediff_MissingPart>? cached = cachedAncestors(set);
                if (cached != null)
                {
                    if (cached.Count != ancestors.Count) return FamilyRead.Unverified;
                    for (int i = 0; i < cached.Count; i++) if (!ReferenceEquals(cached[i], ancestors[i])) return FamilyRead.Unverified;
                }

                var context = new Context(pawn, hediffs, added, addedWholeBody);
                var entries = new List<HealthEntry>();
                var parts = new Dictionary<BodyPartRecord, int>();
                foreach (Hediff_MissingPart missing in ancestors) entries.Add(Entry(missing, context, parts));
                foreach (Hediff hediff in hediffs)
                {
                    // showBloodLoss is true for a live pawn, so blood loss is not filtered.
                    if (hediff is Hediff_MissingPart || concealed.Contains(hediff) || !Visible(hediff, context)) continue;
                    entries.Add(Entry(hediff, context, parts));
                }
                rows = HealthProjection.Rows(entries);
                return rows == null ? FamilyRead.Unverified : FamilyRead.Value;
            }
            catch { rows = null; return FamilyRead.Unverified; }
        }

        private sealed class Context
        {
            internal readonly Pawn Pawn; internal readonly List<Hediff> Hediffs;
            internal readonly HashSet<BodyPartRecord> Added; internal readonly bool AddedWholeBody;
            internal Context(Pawn pawn, List<Hediff> hediffs, HashSet<BodyPartRecord> added, bool addedWholeBody)
            { Pawn = pawn; Hediffs = hediffs; Added = added; AddedWholeBody = addedWholeBody; }
        }

        private HealthEntry Entry(Hediff hediff, Context context, Dictionary<BodyPartRecord, int> parts)
        {
            Kind kind = Kinded(hediff.GetType()) ?? throw Refuse();
            string label = Label(hediff, kind, context);
            string cap = Cap(label, hediff.def);
            int group = label.GetHashCode();
            if (kind == Kind.Injury && Tended(hediff)) group = Gen.HashCombineInt(group, 152235495);
            BodyPartRecord? part = hediff.Part;
            if (part == null) return new HealthEntry(-1, HealthProjection.WholeBodyPriority, null, group, cap);
            if (!parts.TryGetValue(part, out int index)) { index = parts.Count; parts[part] = index; }
            float priority = HealthProjection.Priority((int)part.height, part.coverageAbsWithChildren) ?? throw Refuse();
            return new HealthEntry(index, priority, PartLabel(part), group, cap);
        }

        // Hediff.Visible / HediffWithComps.Visible: admitted comps never disallow visibility.
        // Hediff_Hangover hides itself while the pawn has any alcohol high.
        private bool Visible(Hediff hediff, Context context)
        {
            Kind kind = Kinded(hediff.GetType()) ?? throw Refuse();
            if (kind == Kind.Never) return false;
            if (kind == Kind.Hangover)
                foreach (Hediff other in context.Hediffs) if (other.def == HediffDefOf.AlcoholHigh) return false;
            HediffStage? stage = Stage(hediff);
            bool baseHidden = HealthProjection.HiddenByFields(visible(hediff), stage != null, stage != null && stage.becomeVisible);
            return kind == Kind.RotStink ? !HealthProjection.RotStinkHidden(baseHidden, Has(context.Hediffs, HediffDefOf.LungRot)) : !baseHidden;
        }

        // Hediff.CurStage: def.stages[CurStageIndex], where CurStageIndex is
        // def.StageAtSeverity(Severity) except Hediff_Addiction's need-category index.
        private HediffStage? Stage(Hediff hediff)
        {
            List<HediffStage>? stages = hediff.def.stages;
            if (stages == null || stages.Count == 0) return null;
            int index = 0;
            if (Kinded(hediff.GetType()) == Kind.Addiction) index = AddictionStage((Hediff_Addiction)hediff);
            else
            {
                float value = Severity(hediff);
                for (int i = stages.Count - 1; i >= 0; i--)
                {
                    if (stages[i] == null) throw Refuse();
                    if (value >= stages[i].minSeverity) { index = i; break; }
                }
            }
            if (index >= stages.Count) throw Refuse(); // The game would index out of range.
            return stages[index] ?? throw Refuse();
        }

        // Hediff.Severity, or Hediff_ChemicalDependency's: initial severity unless its linked
        // dependency gene is active. LinkedGene's cache is read, never filled.
        private float Severity(Hediff hediff)
        {
            if (Kinded(hediff.GetType()) != Kind.Dependency) return severity(hediff);
            var dependency = (Hediff_ChemicalDependency)hediff;
            Gene_ChemicalDependency? gene = linkedGene(dependency);
            Pawn_GeneTracker? genes = dependency.pawn.genes;
            if (gene == null && genes != null)
            {
                List<Gene>? cached = cachedGenes(genes);
                List<Gene>? first = cached ?? xenogenes(genes), second = cached == null ? endogenes(genes) : null;
                if (first == null || (cached == null && second == null) || first.Count + (second?.Count ?? 0) > MaxHediffs) throw Refuse();
                gene = FindDependencyGene(first, dependency.chemical) ?? (second == null ? null : FindDependencyGene(second, dependency.chemical));
            }
            return gene == null || !GeneActive(gene) ? dependency.def.initialSeverity : severity(hediff);
        }

        private static Gene_ChemicalDependency? FindDependencyGene(List<Gene> genes, ChemicalDef? chemical)
        {
            foreach (Gene gene in genes)
            {
                if (gene == null || gene.def == null) throw Refuse();
                if (gene is Gene_ChemicalDependency match && match.def.chemical == chemical) return match;
            }
            return null;
        }

        // Gene_ChemicalDependency.Active over Gene.Active; exact class only.
        private bool GeneActive(Gene_ChemicalDependency gene)
        {
            if (gene.GetType() != typeof(Gene_ChemicalDependency) || gene.def == null) throw Refuse();
            Pawn? pawn = gene.pawn;
            if (gene.overriddenByGene != null) return false;
            if (pawn?.ageTracker != null && (float)(int)(biologicalTicks(pawn.ageTracker) / 3600000) < gene.def.minAgeActive) return false;
            MutantDef? mutant = pawn?.mutant == null ? null : mutantDef(pawn.mutant);
            if (pawn?.mutant != null && (mutant?.disablesGenes ?? throw Refuse()).Contains(gene.def)) return false;
            if (pawn == null) return false;
            return !(ModsConfig.AnomalyActive && pawn.mutant != null && mutant == MutantDefOf.Ghoul);
        }

        // Hediff_Addiction.CurStageIndex: 1 in withdrawal of its chemical need, else 0.
        private int AddictionStage(Hediff_Addiction addiction)
        {
            Pawn_NeedsTracker? tracker = addiction.pawn.needs;
            List<Need>? needs = tracker == null ? null : needList(tracker);
            if (needs == null || needs.Count > MaxHediffs) throw Refuse();
            foreach (Need need in needs)
            {
                if (need == null) throw Refuse();
                if (need.def != addiction.def.chemicalNeed) continue;
                if (need.GetType() != typeof(Need_Chemical)) throw Refuse();
                return curLevel(need) > 0.01f ? 0 : 1; // Not in withdrawal.
            }
            return 0;
        }

        // Hediff.Label = LabelBase + " (" + LabelInBrackets + ")" when brackets are non-empty.
        private string Label(Hediff hediff, Kind kind, Context context)
        {
            string? labelBase, brackets;
            switch (kind)
            {
                case Kind.Injury: labelBase = InjuryBase((Hediff_Injury)hediff); brackets = InjuryBrackets((Hediff_Injury)hediff); break;
                case Kind.Missing:
                    var missing = (Hediff_MissingPart)hediff;
                    labelBase = MissingBase(missing, context);
                    var text = new StringBuilder(); text.Append(Brackets(hediff));
                    if (FreshNonSolid(missing, context)) { Separate(text); text.Append(Keyed("FreshMissingBodyPart")); }
                    brackets = text.ToString();
                    break;
                case Kind.Level: return LevelLabel((Hediff_Level)hediff);
                case Kind.Addiction: labelBase = BaseLabel(hediff); brackets = AddictionBrackets((Hediff_Addiction)hediff); break;
                case Kind.Dependency:
                {
                    ChemicalDef chemical = ((Hediff_ChemicalDependency)hediff).chemical ?? throw Refuse();
                    labelBase = Format("ChemicalDependency", FormatArg.Def("CHEMICAL", chemical.label ?? throw Refuse()));
                    brackets = Brackets(hediff);
                    break;
                }
                default: labelBase = BaseLabel(hediff); brackets = Brackets(hediff); break;
            }
            return labelBase + (string.IsNullOrEmpty(brackets) ? "" : " (" + brackets + ")");
        }

        // Hediff_Level.Label: "label (LevelNum)" or "label xN" for quantity levels.
        private string LevelLabel(Hediff_Level level)
        {
            string label = level.def.label ?? throw Refuse();
            return level.def.levelIsQuantity ? label + " x" + level.level : label + " (" + Format("LevelNum", FormatArg.Number(level.level)) + ")";
        }

        // Hediff_Addiction.LabelInBrackets: base brackets plus remaining percentage when the
        // def has a severity-per-day comp.
        private string? AddictionBrackets(Hediff_Addiction addiction)
        {
            string? brackets = Brackets(addiction);
            string percent = HealthProjection.AddictionPercent(severity(addiction));
            bool perDay = false;
            if (addiction.def.comps != null)
                foreach (HediffCompProperties props in addiction.def.comps) if (props is HediffCompProperties_SeverityPerDay) { perDay = true; break; }
            if (!perDay) return brackets;
            return string.IsNullOrEmpty(brackets) ? percent : brackets + " " + percent;
        }

        // Hediff.LabelBase (admitted comps have no label prefix, so HediffWithComps adds nothing).
        private string? BaseLabel(Hediff hediff) => Stage(hediff)?.overrideLabel ?? hediff.def.label;

        // Hediff.LabelInBrackets + HediffWithComps comp extras joined with ", ".
        private string? Brackets(Hediff hediff)
        {
            HediffStage? stage = Stage(hediff);
            string? own = stage != null && !string.IsNullOrEmpty(stage.label) ? stage.label : null;
            if (!(hediff is HediffWithComps withComps)) return own;
            var text = new StringBuilder();
            text.Append(own);
            foreach (HediffComp comp in withComps.comps)
            {
                string? extra = CompExtra(comp);
                if (string.IsNullOrEmpty(extra)) continue;
                Separate(text); text.Append(extra);
            }
            return text.ToString();
        }

        private string? CompExtra(HediffComp comp)
        {
            switch (CompKinded(comp.GetType()) ?? throw Refuse())
            {
                case CompKind.Base: return null;
                case CompKind.Disappears:
                {
                    if (!(comp.props is HediffCompProperties_Disappears disappears)) throw Refuse();
                    if (!disappears.showRemainingTime) return null;
                    // CompKinded admits the exact class only: TicksLostPerTick is exactly 1.
                    var timer = (HediffComp_Disappears)comp;
                    return HealthProjection.DisappearingTime(timer.ticksToDisappear, disappears.canUseDecimalsShortForm, Keyed) ?? throw Refuse();
                }
                case CompKind.SeverityPerDay:
                    return comp.props is HediffCompProperties_SeverityPerDay perDay && !perDay.showHoursToRecover ? null : throw Refuse();
                default:
                {
                    // Immunizable: "developed immunity" unless hidden; Hidden ignores props only
                    // under dev god mode. FullyImmune = first matching record >= 1 (the 0.65
                    // floor for hediff/gene immunity never reaches 1).
                    if (!(comp.props is HediffCompProperties_Immunizable props)) throw Refuse();
                    bool hidden = Prefs.DevMode && DebugSettings.godMode ? false : props.hidden;
                    if (hidden) return null;
                    List<ImmunityRecord>? records = comp.parent.pawn?.health?.immunity == null ? null : immunities(comp.parent.pawn.health.immunity);
                    if (records == null || records.Count > MaxHediffs) throw Refuse();
                    float immunity = 0f;
                    foreach (ImmunityRecord record in records)
                    {
                        if (record == null) throw Refuse();
                        if (record.hediffDef == comp.parent.def) { immunity = record.immunity; break; }
                    }
                    return immunity >= 1f ? Keyed("DevelopedImmunityLower") : null;
                }
            }
        }

        private static T? FirstComp<T>(Hediff hediff) where T : HediffComp
        {
            if (!(hediff is HediffWithComps withComps)) return null;
            foreach (HediffComp comp in withComps.comps) if (comp is T match) return match;
            return null;
        }

        private static bool Tended(Hediff hediff)
        {
            HediffComp_TendDuration? tend = FirstComp<HediffComp_TendDuration>(hediff);
            return tend != null && Current.ProgramState == ProgramState.Playing && tend.tendTicksLeft > 0;
        }

        private string? InjuryBase(Hediff_Injury injury)
        {
            HediffComp_GetsPermanent? permanent = FirstComp<HediffComp_GetsPermanent>(injury);
            if (permanent != null && permanent.isPermanentInt)
            {
                if (!(permanent.props is HediffCompProperties_GetsPermanent props)) throw Refuse();
                if (injury.Part != null && injury.Part.def.delicate && !string.IsNullOrEmpty(props.instantlyPermanentLabel))
                    return props.instantlyPermanentLabel;
                if (!string.IsNullOrEmpty(props.permanentLabel)) return props.permanentLabel;
            }
            return BaseLabel(injury);
        }

        private string InjuryBrackets(Hediff_Injury injury)
        {
            var text = new StringBuilder();
            text.Append(Brackets(injury));
            if (injury.sourceHediffDef != null) { Separate(text); text.Append(injury.sourceHediffDef.label); }
            else if (injury.sourceDef != null)
            {
                Separate(text);
                // "SourceToolLabel".Translate(sourceLabel, tool or group LabelShort).
                string? tool = !string.IsNullOrEmpty(injury.sourceToolLabel) ? injury.sourceToolLabel
                    : injury.sourceBodyPartGroup == null ? null
                    : !string.IsNullOrEmpty(injury.sourceBodyPartGroup.labelShort) ? injury.sourceBodyPartGroup.labelShort : injury.sourceBodyPartGroup.label;
                if (tool == null && (!string.IsNullOrEmpty(injury.sourceToolLabel) || injury.sourceBodyPartGroup != null)) throw Refuse();
                if (tool == null) text.Append(injury.sourceLabel);
                else text.Append(Format("SourceToolLabel", FormatArg.Text(injury.sourceLabel ?? throw Refuse()), FormatArg.Text(tool)));
            }
            HediffComp_GetsPermanent? permanent = FirstComp<HediffComp_GetsPermanent>(injury);
            if (permanent != null && permanent.isPermanentInt && pain(permanent) != PainCategory.Painless)
            {
                Separate(text); text.Append(Keyed("PainCategory_" + pain(permanent)));
            }
            return text.ToString();
        }

        private string? MissingBase(Hediff_MissingPart missing, Context context)
        {
            BodyPartRecord part = missing.Part ?? throw Refuse();
            HediffDef? last = missing.lastInjury;
            if (last != null && last.injuryProps.useRemovedLabel) return Keyed("RemovedBodyPart");
            if (last != null && last.injuryProps.alwaysUseDestroyedLabel) return last.injuryProps.destroyedLabel;
            if (last == null || part.depth == BodyPartDepth.Inside)
            {
                bool solid = part.def.IsSolid(part, context.Hediffs);
                // HealthUtility.GetGeneralDestroyedPartLabel.
                if (part.parent == null) return Keyed("SeriouslyImpaired");
                if (part.depth == BodyPartDepth.Inside || FreshNonSolid(missing, context))
                    return Keyed(solid ? "ShatteredBodyPart" : "DestroyedBodyPart");
                return Keyed("MissingBodyPart");
            }
            if (part.def.socketed && !string.IsNullOrEmpty(last.injuryProps.destroyedOutLabel)) return last.injuryProps.destroyedOutLabel;
            return last.injuryProps.destroyedLabel;
        }

        // Hediff_MissingPart.IsFreshNonSolidExtremity (in play, never Entry).
        private bool FreshNonSolid(Hediff_MissingPart missing, Context context)
        {
            BodyPartRecord part = missing.Part ?? throw Refuse();
            if (!fresh(missing) || missing.ageTicks >= 90000 || part.depth == BodyPartDepth.Inside
                || part.def.IsSolid(part, context.Hediffs) || AddedAt(part.parent, context)) return false;
            foreach (Hediff hediff in context.Hediffs)
                if (hediff is Hediff_MissingPart other && other.Part == part.parent) return false;
            return true;
        }

        // HediffSet.PartOrAnyAncestorHasDirectlyAddedParts, including its null-part case.
        private static bool AddedAt(BodyPartRecord? part, Context context)
        {
            if (part == null)
            {
                if (context.AddedWholeBody) return true;
                throw Refuse(); // The game would dereference null here.
            }
            bool? above = HealthProjection.AddedAbove<BodyPartRecord>(part, p => p.parent, p => context.Added.Contains(p));
            return above ?? throw Refuse();
        }

        // BodyPartRecord.LabelCap: custom label capitalized (its cache must agree) else def.LabelCap.
        private string PartLabel(BodyPartRecord part)
        {
            if (string.IsNullOrEmpty(part.customLabel)) return Cap(part.def.label, part.def);
            string label = PawnProjection.Label("", part.customLabel) ?? throw Refuse();
            string? cached = cachedPartLabel(part);
            if (cached != null && !string.Equals(cached, label, StringComparison.Ordinal)) throw Refuse();
            return label;
        }

        private string Cap(string? text, Def? def) => this.text.Cap(text, def);
        private string Keyed(string key) => text.Keyed(key);
        private string Format(string key, params FormatArg[] args) => text.Format(key, args);

        private static void Separate(StringBuilder text) { if (text.Length != 0) text.Append(", "); }
        private static Exception Refuse() => new InvalidOperationException("Health row unverified.");

        private static Type? Decl(Type type, string property) =>
            type.GetProperty(property, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod(true)?.DeclaringType;
        private static AccessTools.FieldRef<TOwner, TField> Field<TOwner, TField>(string name)
        {
            FieldInfo? field = typeof(TOwner).GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (field == null || field.FieldType != typeof(TField)) throw new InvalidOperationException("Health binding unavailable.");
            return AccessTools.FieldRefAccess<TOwner, TField>(field);
        }
        private static MethodInfo Getter(Type type, string property, BindingFlags visibility = BindingFlags.Public, bool isStatic = false) =>
            type.GetProperty(property, visibility | (isStatic ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.DeclaredOnly)?.GetGetMethod(true)
                ?? throw new InvalidOperationException("Health binding unavailable.");
        private static MethodInfo Method(Type type, string name, params Type[] parameters) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly, null, parameters, null) ?? throw new InvalidOperationException("Health binding unavailable.");
    }
}
