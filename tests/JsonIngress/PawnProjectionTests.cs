using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

// Pure own-pawn identity/status projection only; the game reader/producers are covered by
// configured net472 compile, not by these host tests.
internal static class PawnProjectionTests
{
    private static int passed, failed;
    private static readonly Scope MapScope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"), Ids.New("m"));

    internal static int Run()
    {
        Test("own pawn: player faction and unhosted, or colony slave; never prisoner/guest", () =>
        {
            True(PawnProjection.Own(true, false, false)); True(PawnProjection.Own(true, true, true));
            True(!PawnProjection.Own(true, true, false)); True(!PawnProjection.Own(false, false, false));
            True(!PawnProjection.Own(false, true, true));
        });
        Test("label mirrors LabelShortCap: prefix + short name, first letter capitalized", () =>
        {
            Equal("Ghoul tynan", PawnProjection.Label("ghoul ", "tynan")); Equal("Muffalo", PawnProjection.Label("", "muffalo"));
            Equal("Ödvang", PawnProjection.Label("", "ödvang")); Equal("X", PawnProjection.Label("", "x"));
            Equal("新 pawn", PawnProjection.Label("", "新 pawn")); Equal("Cassandra", PawnProjection.Label("", "Cassandra"));
        });
        Test("markup, tagged markers, invalid or oversized labels are refused, never stripped", () =>
        {
            True(PawnProjection.Label("", "<color=red>x</color>") == null); True(PawnProjection.Label("", "a (*Name)b(/Name)") == null);
            True(PawnProjection.Label("<b>", "x") == null); True(PawnProjection.Label(null, "x") == null);
            True(PawnProjection.Label("", null) == null); True(PawnProjection.Label("", "") == null);
            True(PawnProjection.Label("", "\ud800") == null); True(PawnProjection.Label("", new string('a', 513)) == null);
            True(PawnProjection.Label(new string('a', 300), new string('b', 300)) == null);
        });
        Test("status is fixed tokens in fixed order; empty means no flags", () =>
        {
            Equal(0, PawnProjection.Status(false, false, false).Count);
            Equal("downed,drafted", string.Join(",", PawnProjection.Status(true, false, true)));
            Equal("downed,dead,drafted", string.Join(",", PawnProjection.Status(true, true, true)));
        });
        Test("list item and detail validate against the wire contract; later families stay absent", () =>
        {
            string id = Ids.New("p");
            JObject item = PawnProjection.Item(id, "Tynan", PawnProjection.Status(false, false, true));
            Equal(3, item.Count);
            JObject page = Responses.Page(new SampleStamp(MapScope, 3, 10), Ids.New("q"), new JArray(item), null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawns, page), ReadMethod.Pawns, "read");
            JObject detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item);
            foreach (string absent in new[] { "needs", "health", "gear", "job", "lordReport" }) True(detail["pawn"]![absent] == null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, detail), ReadMethod.Pawn, "read");
            Equal(0, ((JArray)PawnProjection.Item(id, "Muffalo", PawnProjection.Status(false, false, false))["status"]!).Count);
        });
        Test("job/lord gates independent: mental blocks lord, always bypasses mental not mutant", () =>
        {
            True(JobProjection.LordShown(false, false)); True(!JobProjection.LordShown(true, false));
            True(!JobProjection.LordShown(false, true)); True(JobProjection.JobShown(false, false, false));
            True(!JobProjection.JobShown(true, false, false)); True(JobProjection.JobShown(true, false, true));
            True(!JobProjection.JobShown(false, true, true)); True(!JobProjection.JobShown(true, true, true));
            True(JobProjection.WaitUsesBase(true, false));
            True(!JobProjection.WaitUsesBase(true, true));
            True(!JobProjection.WaitUsesBase(false, false));
            True(!JobProjection.WaitUsesBase(false, true));
            Equal("Wandering.", JobProjection.Resolved("wandering.", "", "", ""));
        });
        Test("public reports remain separate, absent does not erase other detail families", () =>
        {
            JObject item = PawnProjection.Item(Ids.New("p"), "Tynan", PawnProjection.Status(false, false, false));
            JObject detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, gear: new JArray(),
                job: "Hauling steel", lordReport: "Attending party");
            Equal("Hauling steel", (string?)detail["pawn"]!["job"]); Equal("Attending party", (string?)detail["pawn"]!["lordReport"]);
            True(detail["pawn"]!["gear"] is JArray); True(item["job"] == null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, detail), ReadMethod.Pawn, "read");
            detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, gear: new JArray(), lordReport: "Attending party");
            True(detail["pawn"]!["job"] == null); True(detail["pawn"]!["lordReport"] != null); True(detail["pawn"]!["gear"] is JArray);
            True(JobProjection.Public("") == null); True(JobProjection.Public("<b>secret</b>") == null);
            Invalid(() => PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, job: new string('x', 513)));
        });
        Test("job target formatting is restricted; legacy replacement follows named formatting", () =>
        {
            Equal("Hauling steel to area", JobProjection.Resolved("hauling TargetA to TargetB", "steel", "area", "unknown"));
            Equal("rock rubble", JobProjection.PlainLegacyTarget("rock rubble"));
            Equal("Cleaning rock rubble.", JobProjection.Resolved("cleaning TargetA.", JobProjection.PlainLegacyTarget("rock rubble")!, "", ""));
            foreach (string? unknown in new[] { null, "", "<b>private</b>", "(*hidden)", new string('x', 513) })
                True(JobProjection.PlainLegacyTarget(unknown) == null);
            Equal("Hauling steel x8", JobProjection.Resolved(HealthProjection.Formatted("hauling {TargetA_label}",
                new[] { FormatArg.Def("TargetA", "steel x8") }, true), "steel", "", ""));
            Equal("Going to area", JobProjection.Resolved(HealthProjection.Formatted("going to {TargetA}",
                new[] { FormatArg.Text("area", "TargetA") }, true), "area", "", ""));
            True(HealthProjection.Formatted("{TargetA}", new[] { FormatArg.Def("TargetA", "steel") }, true) == null);
            True(HealthProjection.Formatted("{TargetA_health}", new[] { FormatArg.Def("TargetA", "steel") }, true) == null);
            True(HealthProjection.Formatted("{PAWN_nameFull}", new FormatArg[0], true) == null);
            True(JobProjection.Resolved(null, "x", "y", "z") == null);
        });
        Test("invalid rows are refused", () =>
        {
            Invalid(() => PawnProjection.Item("t_bad", "A", PawnProjection.Status(false, false, false)));
            Invalid(() => PawnProjection.Item(Ids.New("p"), "<b>A</b>", PawnProjection.Status(false, false, false)));
            Invalid(() => PawnProjection.Item(Ids.New("p"), "lower", PawnProjection.Status(false, false, false)));
            Invalid(() => PawnProjection.Item(Ids.New("p"), "A", new[] { "mental" }));
            Invalid(() => PawnProjection.Detail(new SampleStamp(new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g")), 1, 1),
                PawnProjection.Item(Ids.New("p"), "A", PawnProjection.Status(false, false, false))));
        });
        Test("needs display order mirrors installed Mono List.Sort, including unstable 3-item swaps", () =>
        {
            Equal("", string.Join(",", NeedsProjection.DisplayOrder(new int[0])!));
            Equal("1,0", string.Join(",", NeedsProjection.DisplayOrder(new[] { 1, 5 })!));
            Equal("0,1", string.Join(",", NeedsProjection.DisplayOrder(new[] { 3, 3 })!));
            // Swaps (0,1),(0,2),(1,2) on priorities 1,1,2 give 2,1(b),1(a): not stable.
            Equal("2,1,0", string.Join(",", NeedsProjection.DisplayOrder(new[] { 1, 1, 2 })!));
            Equal("0,1,2", string.Join(",", NeedsProjection.DisplayOrder(new[] { 5, 5, 5 })!));
            // 4..16 entries: insertion sort, ties keep AllNeeds order.
            Equal("3,0,2,1", string.Join(",", NeedsProjection.DisplayOrder(new[] { 5, 1, 2, 9 })!));
            Equal("1,2,0,3", string.Join(",", NeedsProjection.DisplayOrder(new[] { 1, 4, 4, 0 })!));
            True(NeedsProjection.DisplayOrder(new int[16]) != null && NeedsProjection.DisplayOrder(new int[17]) == null);
        });
        Test("food stat cache is used only while younger than 15 ticks", () =>
        {
            True(NeedsProjection.CacheFresh(100, 100)); True(NeedsProjection.CacheFresh(114, 100));
            True(!NeedsProjection.CacheFresh(115, 100)); True(!NeedsProjection.CacheFresh(int.MaxValue, int.MinValue));
        });
        Test("MaxNutrition mirror follows worker order: base, hediffs, body size, life stage, scenario, rounding, clamp", () =>
        {
            var human = new NeedsProjection.MaxNutritionInputs { Base = 1f, Min = 0.01f };
            Equal(1f, NeedsProjection.MaxNutrition(human));
            var ghoul = new NeedsProjection.MaxNutritionInputs { Base = 1f, HediffOffsets = new[] { 0f }, HediffFactors = new[] { 2f }, Min = 0.01f };
            Equal(2f, NeedsProjection.MaxNutrition(ghoul));
            var child = new NeedsProjection.MaxNutritionInputs { Base = 1f, BodySizeFactor = 0.5f, BaseBodySize = 1f, FoodMaxFactor = 0.75f, Min = 0.01f };
            Equal(0.375f, NeedsProjection.MaxNutrition(child));
            var animal = new NeedsProjection.MaxNutritionInputs { Base = 1f, NoSkills = true, NoSkillOffset = 0.5f, NoSkillFactor = 2f, BaseBodySize = 2.4f, ScenarioFactor = 0.5f };
            Equal((1f + 0.5f) * 2f * 2.4f * 0.5f, NeedsProjection.MaxNutrition(animal));
            var tiny = new NeedsProjection.MaxNutritionInputs { Base = 0.001f, Min = 0.01f };
            Equal(0.01f, NeedsProjection.MaxNutrition(tiny));
            var rounded = new NeedsProjection.MaxNutritionInputs { Base = 12.5f, RoundToFiveOver = 10f };
            Equal(10f, NeedsProjection.MaxNutrition(rounded)); // Banker's rounding of 2.5, as Mathf.Round.
            var whole = new NeedsProjection.MaxNutritionInputs { Base = 2.5f, RoundValue = true };
            Equal(2f, NeedsProjection.MaxNutrition(whole));
            True(NeedsProjection.MaxNutrition(new NeedsProjection.MaxNutritionInputs { Base = float.NaN }) == null);
        });
        Test("need rows: capitalized plain label, agreeing cache, finite fraction; otherwise unknown", () =>
        {
            NeedBarPrimitive row = NeedsProjection.Row("food", null, 0.3f, 0.6f)!;
            Equal("Food", row.Label); Equal(0.5f, row.LevelFraction);
            Equal("Mood", NeedsProjection.Row("mood", "Mood", 0, 1)!.Label);
            Equal(1f, NeedsProjection.Row("rest", "", 1, 1)!.LevelFraction);
            True(NeedsProjection.Row("mood", "Humeur", 0.5f, 1) == null);
            True(NeedsProjection.Row("<b>mood</b>", null, 0.5f, 1) == null);
            True(NeedsProjection.Row(null, null, 0.5f, 1) == null);
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, -0.1f, 1.1f })
                True(NeedsProjection.Row("rest", null, bad, 1) == null);
            foreach (float bad in new[] { float.NaN, 0f, -1f, float.PositiveInfinity })
                True(NeedsProjection.Row("food", null, 0.5f, bad) == null);
        });
        Test("detail needs validate against the wire contract; absent and empty stay distinct", () =>
        {
            string id = Ids.New("p");
            JObject item = PawnProjection.Item(id, "Tynan", PawnProjection.Status(false, false, false));
            JArray needs = NeedsProjection.Json(new[] { NeedsProjection.Row("food", null, 0.25f, 1)!, NeedsProjection.Row("mood", null, 1, 1)! });
            JObject detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, needs);
            Equal("Food", (string)detail["pawn"]!["needs"]![0]!["label"]!); Equal(0.25, (double)detail["pawn"]!["needs"]![0]!["level"]!);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, detail), ReadMethod.Pawn, "read");
            JObject empty = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, new JArray());
            Equal(0, ((JArray)empty["pawn"]!["needs"]!).Count);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, empty), ReadMethod.Pawn, "read");
            True(PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item)["pawn"]!["needs"] == null);
            True(item["needs"] == null); // Detail never mutates the list item.
            Invalid(() => NeedsProjection.Json(new[] { new NeedBarPrimitive("Food", 1.5f) }));
            Invalid(() => NeedsProjection.Json(new NeedBarPrimitive[33]));
        });
        Test("missing parts use common ancestors: stop below a missing part, skip added-part branches", () =>
        {
            // core -> torso -> [left arm -> left hand -> finger, right arm -> right hand]
            var core = new Part("core"); var torso = core.Add("torso");
            var left = torso.Add("left arm"); var hand = left.Add("left hand"); var finger = hand.Add("finger");
            var right = torso.Add("right arm"); var rightHand = right.Add("right hand");
            var added = new HashSet<Part>(); var missing = new Dictionary<Part, string>();
            List<string>? Walk() => HealthProjection.CommonAncestors<Part, string>(core, p => p.Parent, p => p.Children,
                p => added.Contains(p), p => missing.TryGetValue(p, out string? m) ? m : null);
            Equal(0, Walk()!.Count);
            // Missing child and ancestor: only the ancestor row, never each child.
            missing[finger] = "finger"; missing[hand] = "left hand"; missing[left] = "left arm";
            Equal("left arm", string.Join(",", Walk()!));
            missing.Remove(left);
            Equal("left hand", string.Join(",", Walk()!));
            // Replacement: a directly added part on the part or any ancestor hides missing descendants.
            missing[rightHand] = "right hand";
            Equal("left hand,right hand", string.Join(",", Walk()!));
            added.Add(right);
            Equal("left hand", string.Join(",", Walk()!));
            added.Add(torso);
            Equal(0, Walk()!.Count);
            True(HealthProjection.AddedAbove(rightHand, p => p.Parent, p => added.Contains(p)) == true);
            True(HealthProjection.AddedAbove(core, p => p.Parent, p => added.Contains(p)) == false);
            // Malformed or oversized trees are not projectable.
            var broken = new Part("core"); broken.Children.Add(null!);
            True(HealthProjection.CommonAncestors<Part, string>(broken, p => p.Parent, p => p.Children, p => false, p => null) == null);
            var wide = new Part("core"); for (int i = 0; i < HealthProjection.MaxVisits; i++) wide.Add("p" + i);
            True(HealthProjection.CommonAncestors<Part, string>(wide, p => p.Parent, p => p.Children, p => false, p => null) == null);
        });
        Test("health rows group by part priority then UIGroupKey like the normal card", () =>
        {
            float arm = HealthProjection.Priority(2, 0.08f)!.Value, torsoPriority = HealthProjection.Priority(2, 0.4f)!.Value;
            var entries = new[]
            {
                new HealthEntry(0, arm, "Left arm", 1, "Missing body part"),
                new HealthEntry(1, torsoPriority, "Torso", 7, "Cut"),
                new HealthEntry(-1, HealthProjection.WholeBodyPriority, null, 3, "Blood loss (minor)"),
                new HealthEntry(1, torsoPriority, "Torso", 7, "Cut"),
                new HealthEntry(1, torsoPriority, "Torso", 8, "Bruise"),
                new HealthEntry(2, arm, "Right arm", 5, "Scratch"),
            };
            JArray rows = HealthProjection.Rows(entries)!;
            Equal("Blood loss (minor)|;Cut x2|Torso;Bruise|Torso;Missing body part|Left arm;Scratch|Right arm",
                string.Join(";", rows.Select(r => (string)r["label"]! + "|" + (string?)r["part"])));
            Equal(0, HealthProjection.Rows(new HealthEntry[0])!.Count);
            True(HealthProjection.Rows(new[] { new HealthEntry(0, arm, "Arm", 1, "<b>Cut</b>") }) == null);
            True(HealthProjection.Rows(new[] { new HealthEntry(0, arm, "Arm", 1, "cut") }) == null);
            True(HealthProjection.Rows(new[] { new HealthEntry(-1, HealthProjection.WholeBodyPriority, "Arm", 1, "Cut") }) == null);
            True(HealthProjection.Rows(new[] { new HealthEntry(0, float.NaN, "Arm", 1, "Cut") }) == null);
            var many = Enumerable.Range(0, HealthProjection.MaxRows + 1).Select(i => new HealthEntry(i, arm, "Arm", i, "Cut")).ToArray();
            True(HealthProjection.Rows(many) == null);
            True(HealthProjection.Rows(many.Take(HealthProjection.MaxRows).ToArray())!.Count == HealthProjection.MaxRows);
        });
        Test("health admission: a condition the normal tab hides never refuses the family; a visible unaudited one does", () =>
        {
            // Fate(structural, admittedClass, concealableShape, hiddenByState).
            True(HealthProjection.Fate(false, false, true, true) == HealthFate.Skip);   // unadmitted class, exact visibility members, hidden now
            True(HealthProjection.Fate(false, true, true, true) == HealthFate.Skip);    // admitted class hidden now: no row, comps irrelevant
            True(HealthProjection.Fate(false, false, true, false) == HealthFate.Refuse); // unadmitted class the tab WOULD show
            True(HealthProjection.Fate(false, false, false, true) == HealthFate.Refuse); // visibility members not audited: cannot trust "hidden"
            True(HealthProjection.Fate(false, true, true, false) == HealthFate.Evaluate); // admitted and visible: normal row path
            True(HealthProjection.Fate(true, true, true, true) == HealthFate.Evaluate);  // missing/added parts always feed the ancestor walk
            True(HealthProjection.Fate(true, false, true, true) == HealthFate.Refuse);   // unadmitted missing/added subclass is never skipped
            // Hidden-driven visibility mirrors.
            True(HealthProjection.HiddenByFields(false, true, false));   // visible flag off, stage exists, stage does not reveal
            True(!HealthProjection.HiddenByFields(true, true, false));   // ever-visible flag
            True(!HealthProjection.HiddenByFields(false, false, false)); // no stage: always visible
            True(!HealthProjection.HiddenByFields(false, true, true));   // stage reveals
            True(HealthProjection.RotStinkHidden(true, false));          // base hidden (severity below the reveal stage)
            True(HealthProjection.RotStinkHidden(false, true));          // lung rot present hides the exposure row
            True(!HealthProjection.RotStinkHidden(false, false));
        });
        Test("formatted keyed labels mirror the simple grammar resolver and worker post-processing", () =>
        {
            Equal("Thrumbo horn", HealthProjection.Formatted("{0} {1}", new[] { FormatArg.Text("Thrumbo"), FormatArg.Text("horn") }, true));
            Equal("level 3", HealthProjection.Formatted("level {0}", new[] { FormatArg.Number(3) }, true));
            Equal("go-juice dependency", HealthProjection.Formatted("{CHEMICAL_label} dependency", new[] { FormatArg.Def("CHEMICAL", "go-juice") }, true));
            Equal("horn de Thrumbo", HealthProjection.Formatted("{1} de {0}", new[] { FormatArg.Text("Thrumbo"), FormatArg.Text("horn") }, false));
            // Sentence capitalization after ":" and space merging happen as in the game.
            Equal("Bite: Jaw", HealthProjection.Formatted("{0}: {1}", new[] { FormatArg.Text("Bite"), FormatArg.Text("jaw") }, false));
            Equal("a b", HealthProjection.Formatted("{0}  {1}", new[] { FormatArg.Text("a"), FormatArg.Text("b") }, false));
            // English article fix-ups only under the English worker.
            Equal("an elephant tusk", HealthProjection.Formatted("a {0}", new[] { FormatArg.Text("elephant tusk") }, true));
            Equal("a elephant tusk", HealthProjection.Formatted("a {0}", new[] { FormatArg.Text("elephant tusk") }, false));
            Equal("bit by an ox", HealthProjection.Formatted("bit by a {0}", new[] { FormatArg.Text("ox") }, true));
            // Anything outside the restricted grammar is refused, never guessed.
            foreach (string template in new[] { "{0_plural}", "{0?x}", "{{0}}", "{0", "{fn:0}", "{CHEMICAL}", "{2}", "{OTHER_label}", "<b>{0}</b>", "" })
                True(HealthProjection.Formatted(template, new[] { FormatArg.Text("x"), FormatArg.Def("CHEMICAL", "y") }, true) == null);
            True(HealthProjection.Formatted("{0}", new[] { FormatArg.Text("<i>x</i>") }, true) == null);
            Equal("12%", HealthProjection.AddictionPercent(0.88f));
            Equal("100%", HealthProjection.AddictionPercent(0f));
        });
        Test("visible disappearing condition timer matches short card periods", () =>
        {
            string Key(string key) => key == "SecondsLower" ? "seconds" : key == "LetterHour" ? "h" : key == "LetterDay" ? "d" : "y";
            string? Time(int ticks, bool decimals = false) => HealthProjection.DisappearingTime(ticks, decimals, Key);
            Equal("0 seconds", Time(0)); Equal("1 seconds", Time(60)); Equal("42 seconds", Time(2499));
            Equal("1h", Time(2500)); Equal("3h", Time(8000)); Equal("4h", Time(11250));
            Equal("24h", Time(59999)); Equal("1d", Time(60000)); Equal("1y", Time(3600000));
            Equal((8000f / 2500f).ToString("F1") + "h", Time(8000, true));
            Equal((90000f / 60000f).ToString("F1") + "d", Time(90000, true));
            True(Time(-1) == null);
            // The family still groups only already-visible rows; timer does not bypass visibility.
            var rows = HealthProjection.Rows(new[] { new HealthEntry(-1, HealthProjection.WholeBodyPriority, null, 1,
                "Cryptosleep sickness (" + Time(8000) + ")") });
            Equal("Cryptosleep sickness (3h)", (string)rows![0]!["label"]!);
        });
        Test("hidden conditions are filtered before projection and leave no trace", () =>
        {
            float arm = HealthProjection.Priority(2, 0.08f)!.Value;
            var shown = new HealthEntry(0, arm, "Left arm", 1, "Cut");
            var hidden = new HealthEntry(-1, HealthProjection.WholeBodyPriority, null, 9, "Carcinoma");
            string Project(params (HealthEntry entry, bool visible)[] conditions) =>
                HealthProjection.Rows(conditions.Where(c => c.visible).Select(c => c.entry).ToArray())!.ToString(Formatting.None);
            Equal(Project((shown, true)), Project((shown, true), (hidden, false)));
            Equal(Project(), Project((hidden, false)));
        });
        Test("detail health validates against the wire contract; absent and empty stay distinct", () =>
        {
            string id = Ids.New("p");
            JObject item = PawnProjection.Item(id, "Tynan", PawnProjection.Status(false, false, false));
            JArray health = HealthProjection.Rows(new[] { new HealthEntry(0, 20000.1f, "Left arm", 1, "Cut x2"),
                new HealthEntry(-1, HealthProjection.WholeBodyPriority, null, 2, "Malnutrition") })!;
            JObject detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, null, health);
            Equal("Malnutrition", (string)detail["pawn"]!["health"]![0]!["label"]!);
            True(detail["pawn"]!["health"]![0]!["part"] == null);
            Equal("Left arm", (string)detail["pawn"]!["health"]![1]!["part"]!);
            True(detail["pawn"]!["needs"] == null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, detail), ReadMethod.Pawn, "read");
            JObject none = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, null, new JArray());
            Equal(0, ((JArray)none["pawn"]!["health"]!).Count);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, none), ReadMethod.Pawn, "read");
            True(PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item)["pawn"]!["health"] == null);
        });
        Test("gear labels mirror LabelExtras, hit-point percent and the MaxHitPoints stat", () =>
        {
            Equal("50%", GearProjection.Percent(0.5f)); Equal("5.5%", GearProjection.Percent(0.055f));
            Equal("0.4%", GearProjection.Percent(0.004f)); Equal("86%", GearProjection.Percent(0.856f));
            Equal(" (excellent 50%)", GearProjection.Extras("excellent", "50%", null));
            Equal(" (normal 50% T)", GearProjection.Extras("normal", "50%", "T"));
            Equal(" (T)", GearProjection.Extras(null, null, "T")); Equal("", GearProjection.Extras(null, null, null));
            var input = new GearProjection.MaxHitPointsInputs { Base = 100f, HasStuff = true, StuffFactor = 1.3f, ApplyFactorsIfNegative = false, RoundValue = true, RoundToFiveOver = 200f, Min = 1f };
            Equal(130, GearProjection.MaxHitPoints(input));
            // Immutable hit must preserve stored UI value, not recompute from changed inputs.
            Equal(100, GearProjection.CachedHitPoints(100f));
            Equal(132, GearProjection.CachedHitPoints(131.5f));
            Equal(130, GearProjection.CachedHitPoints(130.5f));
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, float.MaxValue })
                True(GearProjection.CachedHitPoints(invalid) == null);
            input.Base = 200f; input.StuffFactor = 1.12f; Equal(225, GearProjection.MaxHitPoints(input));
            input.Base = -10f; input.StuffFactor = 2f; input.StuffOffset = 3f; Equal(1, GearProjection.MaxHitPoints(input)); // Factor skipped, offset added, clamped.
            input.Base = float.NaN; input.RoundValue = false; True(GearProjection.MaxHitPoints(input) == null);
            Equal("1,2,3,0", string.Join(",", GearProjection.ApparelOrder(new[] { 1, 3, 3, 2 })));
        });
        Test("detail gear keeps tab sections in order and validates against the wire contract", () =>
        {
            JArray gear = GearProjection.Rows(new[] { new GearEntry("Revolver (normal)", true), new GearEntry("Shield belt", true),
                new GearEntry("Flak vest (good 60%)", false), new GearEntry("Cowboy hat", false) })!;
            Equal("Revolver (normal)|equipment;Shield belt|equipment;Flak vest (good 60%)|apparel;Cowboy hat|apparel",
                string.Join(";", gear.Select(r => (string)r["label"]! + "|" + (string)r["slot"]!)));
            True(GearProjection.Rows(new[] { new GearEntry("Hat", false), new GearEntry("Knife", true) }) == null);
            True(GearProjection.Rows(new[] { new GearEntry("<b>Hat</b>", false) }) == null);
            True(GearProjection.Rows(new[] { new GearEntry("hat", false) }) == null);
            True(GearProjection.Rows(Enumerable.Range(0, GearProjection.MaxItems + 1).Select(i => new GearEntry("Hat", false)).ToArray()) == null);
            JObject item = PawnProjection.Item(Ids.New("p"), "Tynan", PawnProjection.Status(false, false, false));
            JObject detail = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, null, null, gear);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Pawn, detail), ReadMethod.Pawn, "read");
            JObject none = PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item, null, null, new JArray());
            Equal(0, ((JArray)none["pawn"]!["gear"]!).Count);
            True(PawnProjection.Detail(new SampleStamp(MapScope, 3, 10), item)["pawn"]!["gear"] == null);
        });
        Console.WriteLine($"Pawn projection: {passed} passed, {failed} failed (host primitives, not game proof).");
        return failed;
    }

    private sealed class Part
    {
        internal readonly string Name; internal Part? Parent; internal readonly List<Part> Children = new List<Part>();
        internal Part(string name) { Name = name; }
        internal Part Add(string name) { var child = new Part(name) { Parent = this }; Children.Add(child); return child; }
    }

    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted invalid pawn"); }
        catch (ArgumentException) { }
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL pawn {name}: {error.Message}"); }
    }
}
