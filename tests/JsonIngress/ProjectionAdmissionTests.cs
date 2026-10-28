using System;
using System.Reflection;
using RimPilot.Mod.Observation;

internal static class ProjectionAdmissionTests
{
    private static int passed, failed;
    private class Provider
    {
        internal static int Calls;
        public Provider() { }
        public virtual string Label { get { Calls++; return "public"; } }
        public string Report() { Calls++; return "public report"; }
    }
    private sealed class CustomProvider : Provider
    { public override string Label => throw new Exception("unaudited getter invoked"); }
    private class Definition
    {
        public override bool Equals(object? other) => throw new Exception("definition Equals invoked");
        public override int GetHashCode() => throw new Exception("definition hash invoked");
        public override string ToString() => throw new Exception("definition label invoked");
    }
    private sealed class CustomDefinition : Definition { }
    private class Component { }
    private sealed class CustomComponent : Component { }
    private sealed class Patches : IProjectionPatchAudit
    {
        internal int Calls, FailAt = -1;
        internal bool Throws;
        internal Action<int>? OnRead;
        public bool IsUnpatched(MethodBase method)
        {
            int call = Calls++; OnRead?.Invoke(call);
            if (Throws) throw new Exception("private patch information");
            return call != FailAt;
        }
    }
    private static readonly MethodInfo Label = typeof(Provider).GetProperty("Label")!.GetGetMethod()!;
    private static readonly MethodInfo Report = typeof(Provider).GetMethod("Report")!;
    private static readonly ComponentEvidence[] Empty = Array.Empty<ComponentEvidence>();
    private static ProjectionBudget Budget(int operations = ProjectionBudget.MaxOperations, int bytes = ProjectionBudget.MaxBytes)
        => new ProjectionBudget(() => true, operations, bytes);
    private static AdmissionRecipe Recipe(object def, ComponentAdmission[]? comps = null, MethodBase[]? methods = null) =>
        new AdmissionRecipe(ObservationFamily.OwnPawnIdentity, "synthetic exact audit", typeof(Provider), def,
            typeof(Definition), comps ?? Array.Empty<ComponentAdmission>(), methods ?? new[] { Label, Report });

    internal static int Run()
    {
        Test("exact provider/definition admits without invoking getter/report/equality", () =>
        {
            Provider.Calls = 0; var def = new Definition(); var patches = new Patches();
            Equal(ProjectionResult.Value, Recipe(def).Check(new Provider(), def, Empty, patches, Budget()));
            Equal(0, Provider.Calls); Equal(2, patches.Calls);
        });
        Test("same-assembly subclass is unknown before every provider/patch access", () =>
        {
            var def = new Definition(); var patches = new Patches();
            Equal(ProjectionResult.Unverified, Recipe(def).Check(new CustomProvider(), def, Empty, patches, Budget()));
            Equal(0, patches.Calls);
        });
        Test("definition name/equality cannot admit replacement", () =>
        {
            var def = new Definition(); var patches = new Patches(); var recipe = Recipe(def);
            foreach (object replacement in new object[] { new Definition(), new CustomDefinition(), new object() })
                Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), replacement, Empty, patches, Budget()));
            Equal(0, patches.Calls);
        });
        Test("null candidate/definition/component evidence fail closed", () =>
        {
            var def = new Definition(); var recipe = Recipe(def); var patches = new Patches();
            Equal(ProjectionResult.Unverified, recipe.Check(null!, def, Empty, patches, Budget()));
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), null!, Empty, patches, Budget()));
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, null!, patches, Budget()));
            Equal(0, patches.Calls);
        });
        Test("exact component type/props/order/cardinality required before provider access", () =>
        {
            var def = new Definition(); var props = new Definition(); var other = new Definition();
            var recipe = Recipe(def, new[] { new ComponentAdmission(typeof(Component), props) });
            var patches = new Patches();
            foreach (ComponentEvidence[] evidence in new[]
            {
                Empty, new[] { new ComponentEvidence(new CustomComponent(), props) },
                new[] { new ComponentEvidence(new Component(), other) },
                new[] { new ComponentEvidence(new Component(), props), new ComponentEvidence(new Component(), props) },
                new ComponentEvidence[] { null! }, new[] { new ComponentEvidence(null!, props) }
            }) Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, evidence, patches, Budget()));
            Equal(0, patches.Calls);
            Equal(ProjectionResult.Value, recipe.Check(new Provider(), def,
                new[] { new ComponentEvidence(new Component(), props) }, patches, Budget()));
            var ordered = Recipe(def, new[] { new ComponentAdmission(typeof(Component), props), new ComponentAdmission(typeof(Component), other) });
            Equal(ProjectionResult.Unverified, ordered.Check(new Provider(), def,
                new[] { new ComponentEvidence(new Component(), other), new ComponentEvidence(new Component(), props) }, new Patches(), Budget()));
        });
        Test("recipes clone component/provider arrays", () =>
        {
            var def = new Definition(); var props = new Definition();
            var comps = new[] { new ComponentAdmission(typeof(Component), props) }; var methods = new[] { Label, Report };
            var recipe = Recipe(def, comps, methods); comps[0] = new ComponentAdmission(typeof(CustomComponent), props); methods[0] = null!;
            Equal(ProjectionResult.Value, recipe.Check(new Provider(), def,
                new[] { new ComponentEvidence(new Component(), props) }, new Patches(), Budget()));
        });
        for (int i = 0; i < 2; i++)
        {
            int method = i;
            Test("every getter/report patch denied before evaluation " + i, () =>
            {
                var def = new Definition(); Provider.Calls = 0;
                Equal(ProjectionResult.Unverified, Recipe(def).Check(new Provider(), def, Empty, new Patches { FailAt = method }, Budget()));
                Equal(0, Provider.Calls);
            });
        }
        Test("missing/failed patch inspector is unknown, not unpatched", () =>
        {
            var def = new Definition(); var recipe = Recipe(def);
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, Empty, null!, Budget()));
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, Empty, new Patches { Throws = true }, Budget()));
        });
        Test("constructor dependencies require patch audit too", () =>
        {
            var def = new Definition(); var constructor = typeof(Provider).GetConstructor(Type.EmptyTypes)!;
            var recipe = Recipe(def, methods: new MethodBase[] { Label, constructor });
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, Empty, new Patches { FailAt = 1 }, Budget()));
            Equal(ProjectionResult.Value, recipe.Check(new Provider(), def, Empty, new Patches(), Budget()));
        });
        Test("no stale cached patch approval", () =>
        {
            var def = new Definition(); var recipe = Recipe(def); var patch = new Patches();
            Equal(ProjectionResult.Value, recipe.Check(new Provider(), def, Empty, patch, Budget()));
            patch.FailAt = patch.Calls;
            Equal(ProjectionResult.Unverified, recipe.Check(new Provider(), def, Empty, patch, Budget()));
        });
        Test("empty patch allowlist includes six categories including inner hooks", () =>
        {
            True(PatchCategories.Unpatched(0, 0, 0, 0, 0, 0));
            for (int i = 0; i < 6; i++)
                foreach (int count in new[] { -1, 1, 4096, int.MaxValue })
                {
                    int[] counts = new int[6]; counts[i] = count;
                    True(!PatchCategories.Unpatched(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]));
                }
        });
        for (int i = 0; i < 4; i++)
        {
            int operations = i;
            Test("operation budget stops closure before excess access " + i, () =>
            {
                var def = new Definition(); var patches = new Patches();
                ProjectionResult result = Recipe(def).Check(new Provider(), def, Empty, patches, Budget(operations));
                Equal(operations >= 3 ? ProjectionResult.Value : ProjectionResult.BudgetExceeded, result);
                True(patches.Calls <= Math.Max(0, operations - 1));
            });
        }
        Test("deadline checked after inspector and exhaustion cannot resurrect", () =>
        {
            bool available = true; var budget = new ProjectionBudget(() => available);
            var def = new Definition(); var patches = new Patches { OnRead = _ => available = false };
            Equal(ProjectionResult.BudgetExceeded, Recipe(def).Check(new Provider(), def, Empty, patches, budget));
            Equal(1, patches.Calls); available = true; True(!budget.Charge(0)); True(!budget.Charge());
        });
        Test("Unicode/localized primitive label and normal bar fraction", () =>
        {
            Equal(ProjectionResult.Value, PrimitiveProjection.NeedBar("Humeur 雪 😀", 0.25f, 1, Budget(), out var value));
            Equal("Humeur 雪 😀", value!.Label); Equal(0.25f, value.LevelFraction);
            Equal(ProjectionResult.Value, PrimitiveProjection.NeedBar("Food", 0.75f, 1.5f, Budget(), out value));
            Equal(0.5f, value!.LevelFraction); // Arithmetic only, NOT admission of Food.MaxLevel.
        });
        foreach (float current in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, 2f })
            Test("invalid bar current is unknown, never healthy zero " + current, () =>
            {
                Equal(ProjectionResult.Unverified, PrimitiveProjection.NeedBar("Mood", current, 1, Budget(), out var value));
                True(value == null);
            });
        foreach (float maximum in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0f, -1f })
            Test("invalid bar maximum is unknown " + maximum, () =>
            {
                Equal(ProjectionResult.Unverified, PrimitiveProjection.NeedBar("Food", 0, maximum, Budget(), out var value));
                True(value == null);
            });
        Test("definition capitalization preserves localization without mutating cache", () =>
        {
            Equal(ProjectionResult.Value, PrimitiveProjection.DefinitionLabel("mood", null, Budget(), out var label));
            Equal("Mood", label);
            Equal(ProjectionResult.Value, PrimitiveProjection.DefinitionLabel("mood", "Mood", Budget(), out label));
            Equal("Mood", label);
            Equal(ProjectionResult.Value, PrimitiveProjection.DefinitionLabel("雪 😀", "", Budget(), out label));
            Equal("雪 😀", label);
            Equal(ProjectionResult.Unverified, PrimitiveProjection.DefinitionLabel("new name", "Old name", Budget(), out label));
            True(label == null);
        });
        foreach (string raw in new[] { "<color=red>mood</color>", "(*secret)pawn", "text*)", "<", ">" })
            Test("unresolved tagged definition label never leaked " + raw, () =>
            {
                Equal(ProjectionResult.Unverified, PrimitiveProjection.DefinitionLabel(raw, null, Budget(), out var label));
                True(label == null);
            });
        Test("normal mood pane preserves Mood even when ShowOnNeedList is false", () =>
        {
            True(!PrimitiveProjection.NeedShown(false, false));
            True(PrimitiveProjection.NeedShown(true, false));
            True(PrimitiveProjection.NeedShown(false, true));
            True(PrimitiveProjection.NeedShown(true, true));
        });
        Test("zero/full are legitimate values, distinct from excluded/unknown", () =>
        {
            foreach (float level in new[] { 0f, 1f })
            {
                Equal(ProjectionResult.Value, PrimitiveProjection.NeedBar("Rest", level, 1, Budget(), out var value));
                Equal(level, value!.LevelFraction);
            }
        });
        foreach (string? label in new[] { null, "", new string('x', 513), "\uD800", "\uDC00", "valid\uD800suffix" })
            Test("bad/oversized labels rejected without partial value", () =>
            {
                Equal(ProjectionResult.Unverified, PrimitiveProjection.Label(label, Budget(), out var value)); True(value == null);
            });
        Test("exact label bound and byte budget apply before allocation/transfer", () =>
        {
            string label = new string('雪', PrimitiveProjection.MaxLabelChars);
            Equal(ProjectionResult.Value, PrimitiveProjection.Label(label, Budget(bytes: label.Length * 3), out _));
            Equal(ProjectionResult.BudgetExceeded, PrimitiveProjection.NeedBar(label, 1, 1, Budget(bytes: label.Length * 3 - 1), out var value));
            True(value == null);
        });
        Test("families stay pending independent of encountered targets; rendered controls unsupported", () =>
        {
            foreach (ObservationFamily family in Enum.GetValues(typeof(ObservationFamily)))
            {
                bool controls = family == ObservationFamily.RenderedControls;
                Equal(!controls, ObservationPolicy.RequiredBaseline(family));
                Equal(controls ? FamilyAvailability.Unsupported : FamilyAvailability.Unverified, ObservationPolicy.Availability(family));
            }
            True(!ObservationPolicy.RequiredBaseline((ObservationFamily)999));
        });
        Test("incomplete/overbound recipe cannot be constructed", () =>
        {
            var def = new Definition();
            Reject(() => Recipe(def, methods: Array.Empty<MethodInfo>()));
            Reject(() => Recipe(def, methods: new MethodInfo[AdmissionRecipe.MaxMethods + 1]));
            Reject(() => Recipe(def, new ComponentAdmission[AdmissionRecipe.MaxComponents + 1]));
            Reject(() => Recipe(def, methods: new MethodInfo[] { null! }));
            Reject(() => Recipe(new CustomDefinition()));
        });
        Console.WriteLine($"Projection admission: {passed} passed, {failed} failed (host primitives, not game proof).");
        return failed;
    }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected incomplete audit refusal."); }
    private static void Test(string name, Action action)
    { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine("FAIL admission " + name + ": " + e); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
}
