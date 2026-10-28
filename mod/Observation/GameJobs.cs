using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimPilot.Mod.Bridge;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimPilot.Mod.Observation
{
    // Own-pawn current reports only. Mirrors resolved members, never invokes providers,
    // inspect, setup or queued Job.GetReport. Unknown lord preference must NOT fall back.
    internal sealed class GameJobsReader
    {
        private const int MaxQueue = 32, MaxHediffs = 128;
        private readonly int gameThread;
        private readonly IProjectionPatchAudit patches;
        private readonly GameTargetAccess access;
        private readonly GameOwnPawnReader pawns;
        private readonly GameGearReader? gear;
        private readonly GameKeyedText text = new GameKeyedText();
        private readonly MethodBase[] closure;
        private readonly AccessTools.FieldRef<Lord, LordJob> lordJob = AccessTools.FieldRefAccess<Lord, LordJob>("curJob");
        private readonly AccessTools.FieldRef<LordJob_Ritual, List<RitualStage>> stages = AccessTools.FieldRefAccess<LordJob_Ritual, List<RitualStage>>("stages");
        private readonly AccessTools.FieldRef<LordJob_Ritual, int> stageIndex = AccessTools.FieldRefAccess<LordJob_Ritual, int>("stageIndex");
        private readonly AccessTools.FieldRef<RitualRoleAssignments, List<Pawn>> spectators = AccessTools.FieldRefAccess<RitualRoleAssignments, List<Pawn>>("spectators");
        private readonly AccessTools.FieldRef<RitualRoleAssignments, Dictionary<string, Pawn>> forced = AccessTools.FieldRefAccess<RitualRoleAssignments, Dictionary<string, Pawn>>("forcedRoles");
        private readonly AccessTools.FieldRef<RitualRoleAssignments, Dictionary<string, List<Pawn>>> assigned = AccessTools.FieldRefAccess<RitualRoleAssignments, Dictionary<string, List<Pawn>>>("assignedRoles");
        private readonly AccessTools.FieldRef<RitualRoleAssignments, List<RitualRole>> roles = AccessTools.FieldRefAccess<RitualRoleAssignments, List<RitualRole>>("allRoles");
        private readonly AccessTools.FieldRef<RitualStage, Dictionary<string, RitualRoleBehavior>> behaviors = AccessTools.FieldRefAccess<RitualStage, Dictionary<string, RitualRoleBehavior>>("behaviorForRole");
        private int checkedFrame = -1;

        internal GameJobsReader(int gameThread, IProjectionPatchAudit patches, GameTargetAccess access, GameOwnPawnReader pawns, GameGearReader? gear)
        {
            this.gameThread = gameThread; this.patches = patches; this.access = access; this.pawns = pawns; this.gear = gear;
            closure = new MethodBase[]
            {
                M(typeof(Pawn), "GetJobReport"), M(typeof(Pawn), "GetInspectString"),
                M(typeof(PawnUtility), "ShouldDisplayJobReport", typeof(Pawn)), M(typeof(PawnUtility), "ShouldDisplayLordReport", typeof(Pawn)),
                M(typeof(PawnUtility), "ShouldShowActionReportToPlayer", typeof(Pawn)), M(typeof(PawnUtility), "ShouldShowCultistLordReport", typeof(Pawn)),
                M(typeof(LordUtility), "GetLord", typeof(Pawn)), G(typeof(Lord), "LordJob"),
                G(typeof(Pawn), "InMentalState"), G(typeof(MentalStateHandler), "InMentalState"), G(typeof(Pawn), "Dead"),
                G(typeof(Pawn_HealthTracker), "Dead"), G(typeof(Pawn_HealthTracker), "State"),
                G(typeof(Pawn), "IsMutant"), G(typeof(Pawn_MutantTracker), "HasTurned"), G(typeof(Pawn_MutantTracker), "Def"),
                G(typeof(Pawn), "CurJobDef"), G(typeof(Pawn), "CurJob"), G(typeof(Pawn), "Crawling"),
                G(typeof(Thing), "LabelShort"), G(typeof(Pawn), "LabelShort"),
                M(typeof(JobDriver), "GetReport"), M(typeof(JobDriver), "GetReportStringOverride"), M(typeof(JobDriver), "ReportStringProcessed", typeof(string)),
                M(typeof(JobUtility), "GetResolvedJobReport", typeof(string), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(LocalTargetInfo)),
                M(typeof(JobUtility), "GetResolvedJobReportRaw", typeof(string), typeof(string), typeof(object), typeof(string), typeof(object), typeof(string), typeof(object)),
                M(typeof(GenCollection), "FirstValid", typeof(List<LocalTargetInfo>)),
                M(typeof(GrammarResolverSimple), "TryResolveSymbol", typeof(object), typeof(string), typeof(string), typeof(TaggedString).MakeByRefType(), typeof(string)),
                G(typeof(LocalTargetInfo), "IsValid"), G(typeof(LocalTargetInfo), "Thing"), G(typeof(IntVec3), "IsValid"),
                M(typeof(LordJob), "GetJobReport", typeof(Pawn)), M(typeof(LordJob), "GetReport", typeof(Pawn)),
                M(typeof(LordJob_Ritual), "GetJobReport", typeof(Pawn)), M(typeof(LordJob_Ritual), "BehaviorFor", typeof(Pawn), typeof(bool)),
                M(typeof(LordJob_Ritual), "RoleFor", typeof(Pawn), typeof(bool)), G(typeof(LordJob_Ritual), "CurrentStage"), G(typeof(LordJob_Ritual), "StageIndex"),
                M(typeof(RitualRoleAssignments), "RoleForPawn", typeof(Pawn), typeof(bool)), M(typeof(RitualRoleAssignments), "GetRole", typeof(string)),
                G(typeof(RitualRoleAssignments), "AllRolesForReading"), M(typeof(RitualStage), "BehaviorForRole", typeof(string)),
                M(typeof(RitualRoleBehavior), "GetJobReportOverride", typeof(Pawn)),
                M(typeof(LordJob_Joinable_Party), "GetReport", typeof(Pawn)), M(typeof(LordJob_Joinable_MarriageCeremony), "GetReport", typeof(Pawn)),
                M(typeof(JobDriver_Wait), "GetReport"),
                M(typeof(JobDriver_LayDown), "GetReport"), M(typeof(HediffSet), "InLabor", typeof(bool)),
                M(typeof(HediffSet), "HasHediff", typeof(HediffDef), typeof(bool)),
                GameKeyedText.Getter(typeof(ModsConfig), "BiotechActive", true),
            }.Concat(GameKeyedText.Closure()).ToArray();
        }

        private bool Fresh()
        {
            if (Thread.CurrentThread.ManagedThreadId != gameThread) throw new InvalidOperationException("Game thread required.");
            int frame = UnityEngine.Time.frameCount;
            if (frame == checkedFrame) return true;
            try { foreach (MethodBase method in closure) if (!patches.IsUnpatched(method)) return false; }
            catch { return false; }
            checkedFrame = frame; return true;
        }

        // Each output independently proven. No public string means omission, never "unknown".
        internal FamilyRead Read(Pawn pawn, Map map, Game game, ProducerWork<Map> work, out string? job, out string? lord)
        {
            job = null; lord = null;
            if (!Fresh() || pawn.health == null || pawn.mindState?.mentalStateHandler == null) return FamilyRead.Unverified;
            if (!work.TryNestedFields(24)) return FamilyRead.Yield;
            try
            {
                bool mental = pawn.health.State != PawnHealthState.Dead && pawn.mindState.mentalStateHandler.InMentalState;
                bool mutant = pawn.mutant != null && pawn.mutant.HasTurned && pawn.mutant.Def.overrideInspectString;
                LordJob? provider = pawn.lord == null ? null : lordJob(pawn.lord);
                if (JobProjection.LordShown(mental, mutant))
                {
                    try { lord = JobProjection.Public(LordReport(provider)); } catch { lord = null; }
                }
                Job? current = pawn.jobs?.curJob;
                if (JobProjection.JobShown(mental, mutant, current?.def?.alwaysShowReport == true))
                {
                    try
                    {
                        // Pawn.GetJobReport uses ??, NOT empty-string fallback.
                        FamilyRead read = Preferred(provider, pawn, work, out string? report);
                        if (read == FamilyRead.Yield) return read;
                        if (report == null)
                        {
                            read = Driver(pawn, map, game, work, out report);
                            if (read == FamilyRead.Yield) return read;
                        }
                        job = JobProjection.Public(report);
                    }
                    catch { job = null; }
                }
                return FamilyRead.Value;
            }
            catch { job = null; lord = null; return FamilyRead.Unverified; }
        }

        // Restricted Ideology preference mirror: bounded assignment lookup, then the
        // current stage's cached behavior (or bounded cache-equivalent scan, never insert).
        // Unknown provider/grammar fails job only, even if its driver would be supported.
        private FamilyRead Preferred(LordJob? provider, Pawn pawn, ProducerWork<Map> work, out string? report)
        {
            report = null;
            if (provider == null || Resolved(provider.GetType(), "GetJobReport", typeof(Pawn)) == typeof(LordJob)) return FamilyRead.Value;
            if (Resolved(provider.GetType(), "GetJobReport", typeof(Pawn)) != typeof(LordJob_Ritual)) throw Refuse();
            var ritual = (LordJob_Ritual)provider;
            List<RitualStage>? list = stages(ritual);
            int index = stageIndex(ritual);
            if (list == null || index < 0 || index >= list.Count) return FamilyRead.Value;
            RitualStage stage = list[index] ?? throw Refuse();
            RitualRoleAssignments? assignment = ritual.assignments;
            if (assignment == null) return FamilyRead.Value;
            List<Pawn> observers = spectators(assignment) ?? throw Refuse();
            Dictionary<string, Pawn>? forcedRoles = forced(assignment);
            Dictionary<string, List<Pawn>> assignedRoles = assigned(assignment) ?? throw Refuse();
            List<RitualRole>? allRoles = roles(assignment);
            int cost = observers.Count + (forcedRoles?.Count ?? 0) + assignedRoles.Count + (allRoles?.Count ?? 0) + (stage.roleBehaviors?.Count ?? 0);
            if (cost > 128) throw Refuse();
            if (!work.TryNestedFields(cost)) return FamilyRead.Yield;
            foreach (Pawn observer in observers) if (ReferenceEquals(observer, pawn)) return FamilyRead.Value;
            string? role = null;
            if (forcedRoles != null) foreach (KeyValuePair<string, Pawn> entry in forcedRoles)
                if (ReferenceEquals(entry.Value, pawn)) { role = entry.Key; break; }
            if (role == null) foreach (KeyValuePair<string, List<Pawn>> entry in assignedRoles)
            {
                List<Pawn>? members = entry.Value;
                if (members == null) continue;
                if (members.Count > 128) throw Refuse();
                if (!work.TryNestedFields(members.Count)) return FamilyRead.Yield;
                foreach (Pawn member in members) if (ReferenceEquals(member, pawn)) { role = entry.Key; break; }
                if (role != null) break;
            }
            bool found = false;
            if (role != null && allRoles != null) foreach (RitualRole item in allRoles)
                if (item != null && item.id == role) { found = true; break; }
            if (!found) return FamilyRead.Value;
            Dictionary<string, RitualRoleBehavior> cache = behaviors(stage) ?? throw Refuse();
            if (!cache.TryGetValue(role!, out RitualRoleBehavior? behavior) && stage.roleBehaviors != null)
                foreach (RitualRoleBehavior item in stage.roleBehaviors)
                {
                    if (item == null) throw Refuse();
                    if (item.roleId == role) { behavior = item; break; }
                }
            if (behavior == null) return FamilyRead.Value;
            if (Resolved(behavior.GetType(), "GetJobReportOverride", typeof(Pawn)) != typeof(RitualRoleBehavior)) throw Refuse();
            string? template = behavior.jobReportOverride;
            // Pawn grammar can add color/name/article or hidden specialist fields: no expansion.
            report = template == null || template.Length == 0 ? template : text.Formatted(template);
            return FamilyRead.Value;
        }

        private string? LordReport(LordJob? provider)
        {
            if (provider == null) return null;
            Type? owner = Resolved(provider.GetType(), "GetReport", typeof(Pawn));
            if (owner == typeof(LordJob)) return null;
            if (provider.GetType() == typeof(LordJob_Joinable_Party)) return text.Keyed("LordReportAttendingParty");
            if (provider.GetType() == typeof(LordJob_Joinable_MarriageCeremony)) return text.Keyed("LordReportAttendingMarriageCeremony");
            throw Refuse();
        }

        private FamilyRead Driver(Pawn pawn, Map map, Game game, ProducerWork<Map> work, out string? report)
        {
            report = null;
            JobDriver? driver = pawn.jobs?.curDriver;
            if (driver == null) return FamilyRead.Value;
            Job? current = pawn.jobs?.curJob;
            if (current == null || current.GetType() != typeof(Job) || current.def == null
                || !ReferenceEquals(driver.job, current) || !ReferenceEquals(driver.pawn, pawn)) throw Refuse();
            Type? owner = Resolved(driver.GetType(), "GetReport");
            if (owner != typeof(JobDriver) && driver.GetType() != typeof(JobDriver_LayDown)
                && !JobProjection.WaitUsesBase(driver.GetType() == typeof(JobDriver_Wait)
                    && owner == typeof(JobDriver_Wait), current.def == JobDefOf.Wait_Combat)) throw Refuse();
            // Crawling's capability closure is not mirrored. No guess from Downed alone.
            if (pawn.health.State == PawnHealthState.Down && !string.IsNullOrEmpty(current.crawlingReportStringOverride)) throw Refuse();
            if (!string.IsNullOrEmpty(current.reportStringOverride)) { report = current.reportStringOverride; return FamilyRead.Value; }
            if (driver.GetType() == typeof(JobDriver_LayDown))
            {
                if (driver.asleep) { report = text.Keyed("ReportSleeping"); return FamilyRead.Value; }
                List<Hediff>? hediffs = pawn.health.hediffSet?.hediffs;
                if (hediffs == null || hediffs.Count > MaxHediffs) throw Refuse();
                if (!work.TryNestedFields(hediffs.Count)) return FamilyRead.Yield;
                bool labor = false;
                if (ModsConfig.BiotechActive)
                    foreach (Hediff hediff in hediffs)
                    {
                        if (hediff == null) throw Refuse();
                        if (hediff.def == HediffDefOf.PregnancyLabor || hediff.def == HediffDefOf.PregnancyLaborPushing) labor = true;
                    }
                // Caller already proved direct spawn (SpawnedParentOrMe == pawn).
                report = text.Keyed(labor ? "GivingBirth" : "ReportResting"); return FamilyRead.Value;
            }
            if (Resolved(driver.GetType(), "ReportStringProcessed", typeof(string)) != typeof(JobDriver)) throw Refuse();
            string template = current.def.reportString;
            if (string.IsNullOrEmpty(template) || template.Length > PrimitiveProjection.MaxLabelChars) throw Refuse();
            var legacy = new[] { "", "", "" };
            var args = new FormatArg[3];
            for (int i = 0; i < 3; i++)
            {
                string name = "Target" + (char)('A' + i);
                args[i] = FormatArg.Text("", name);
                if (template.IndexOf(name, StringComparison.Ordinal) < 0) continue;
                List<LocalTargetInfo>? queue = i == 0 ? current.targetQueueA : i == 1 ? current.targetQueueB : null;
                LocalTargetInfo target = i == 0 ? current.targetA : i == 1 ? current.targetB : current.targetC;
                if (!target.IsValid && queue != null)
                {
                    if (queue.Count > MaxQueue) throw Refuse();
                    if (!work.TryNestedFields(queue.Count)) return FamilyRead.Yield;
                    foreach (LocalTargetInfo candidate in queue) if (candidate.IsValid) { target = candidate; break; }
                }
                if (!target.IsValid) legacy[i] = text.Keyed("UnknownLower");
                else if (target.Thing == null) legacy[i] = text.Keyed("AreaLower"); // No coordinates emitted.
                else
                {
                    Thing thing = target.Thing;
                    if (!work.TryCandidate()) return FamilyRead.Yield;
                    bool cleaningFilth = driver.GetType() == typeof(JobDriver_CleanFilth) && i == 0
                        && thing.GetType() == typeof(Filth);
                    TargetGateResult gate = cleaningFilth ? access.ReportFilth(thing, map, game, work)
                        : access.Thing(thing, map, game, new TypeAdmission(thing.GetType()), work);
                    if (gate == TargetGateResult.Deferred) return FamilyRead.Yield;
                    if (gate != TargetGateResult.Eligible) throw Refuse();
                    if (cleaningFilth)
                    {
                        // Legacy LabelShort is definition-only here; named object grammar uses
                        // Filth.Label (sources/thickness), so that form remains unverified.
                        if (!work.TryNestedFields(8)) return FamilyRead.Yield;
                        if (gear == null || !gear.Fresh() || thing.Stuff != null
                            || template.IndexOf("{" + name, StringComparison.Ordinal) >= 0) throw Refuse();
                        legacy[i] = JobProjection.PlainLegacyTarget(thing.def.label) ?? throw Refuse();
                    }
                    else if (thing.GetType() == typeof(Pawn))
                    {
                        if (!work.TryNestedFields(GameOwnPawnReader.NestedFields)) return FamilyRead.Yield;
                        legacy[i] = pawns.LabelShort((Pawn)thing) ?? throw Refuse();
                        // Pawn grammar symbols have name/color/article semantics: legacy only.
                        if (template.IndexOf("{" + name, StringComparison.Ordinal) >= 0) throw Refuse();
                    }
                    else if (thing is ThingWithComps withComps && gear != null)
                    {
                        if (Resolved(thing.GetType(), "get_LabelShort") != typeof(Thing)) throw Refuse();
                        if (!work.TryNestedFields(gear.LabelCost(withComps, game))) return FamilyRead.Yield;
                        legacy[i] = gear.LabelShort(withComps, game);
                        string label = legacy[i] + (thing.stackCount > 1 ? " x" + thing.stackCount.ToString() : "");
                        args[i] = FormatArg.Def(name, label); // Only _label; no object/article expansion.
                    }
                    else throw Refuse();
                }
                if (target.Thing == null) args[i] = FormatArg.Text(legacy[i], name);
            }
            report = JobProjection.Resolved(text.Formatted(template, args), legacy[0], legacy[1], legacy[2]);
            if (report == null) throw Refuse();
            return FamilyRead.Value;
        }

        private static Type? Resolved(Type type, string name, params Type[] args) => type.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null)?.DeclaringType;
        private static Exception Refuse() => new InvalidOperationException("Job report unverified.");
        private static MethodInfo M(Type type, string name, params Type[] args) => GameKeyedText.Method(type, name, args);
        private static MethodInfo G(Type type, string name) => GameKeyedText.Getter(type, name);
    }
}
