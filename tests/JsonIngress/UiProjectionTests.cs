using System;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class UiProjectionTests
{
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"), Ids.New("m"));
    internal static int Run()
    {
        Test("UI round trips only selected public labels/refs and ordinary kinds", () =>
        {
            var selection = new JArray(UiProjection.Selection("Ada", Ids.New("p"), true),
                UiProjection.Selection("Steel x75", Ids.New("t"), false));
            JObject value = UiProjection.Build(new SampleStamp(Scope, 9, 3), selection,
                new JArray(UiProjection.Window("research"), UiProjection.Window("options")));
            Equal(3, value.Count); Equal(2, ((JObject)value["selection"]![0]!).Count);
            Equal(1, ((JObject)value["windows"]![0]!).Count);
            ResponseContracts.Read(WireJson.EncodeResponse("ui", ReadMethod.Ui, value), ReadMethod.Ui, "ui");
        });
        Test("unsupported selection differs from completed empty eligible selection", () =>
        {
            var stamp = new SampleStamp(Scope, 9, 3);
            True(UiProjection.Build(stamp, null, new JArray())["selection"] == null);
            Equal(0, ((JArray)UiProjection.Build(stamp, new JArray(), new JArray())["selection"]!).Count);
            var menu = new Scope(Ids.New("s"), Ids.New("r"));
            JObject value = UiProjection.Build(new SampleStamp(menu, 9, null), null, new JArray());
            ResponseContracts.Read(WireJson.EncodeResponse("ui", ReadMethod.Ui, value), ReadMethod.Ui, "ui");
        });
        Test("wrong ref kinds, markup, empty and oversized labels refused", () =>
        {
            Invalid(() => UiProjection.Selection("Ada", Ids.New("t"), true));
            Invalid(() => UiProjection.Selection("Steel", Ids.New("p"), false));
            Invalid(() => UiProjection.Selection("<b>Hidden</b>", Ids.New("t"), false));
            Invalid(() => UiProjection.Selection("", Ids.New("t"), false));
            Invalid(() => UiProjection.Selection(new string('x', 513), Ids.New("t"), false));
        });
        Test("window kind cannot be arbitrary title or fully qualified type", () =>
        {
            Invalid(() => UiProjection.Window("Verse.SecretDialog"));
            Invalid(() => UiProjection.Window("<b>Secret</b>"));
            Invalid(() => UiProjection.Window(""));
            Invalid(() => UiProjection.Window(new string('x', 65)));
        });
        Test("UI vectors own immutable copies of supplied DTOs", () =>
        {
            var selection = new JArray(UiProjection.Selection("Ada", Ids.New("p"), true));
            var windows = new JArray(UiProjection.Window("options"));
            JObject value = UiProjection.Build(new SampleStamp(Scope, 9, 3), selection, windows);
            selection[0]["label"] = "Changed"; windows.Clear();
            Equal("Ada", (string?)value["selection"]![0]!["label"]);
            Equal(1, ((JArray)value["windows"]!).Count);
        });
        Test("bounds refuse whole result rather than truncate selection/windows", () =>
        {
            var selection = new JArray(); var windows = new JArray();
            for (int i = 0; i < UiProjection.MaxSelection; i++) selection.Add(UiProjection.Selection("Ada", Ids.New("p"), true));
            for (int i = 0; i < UiProjection.MaxWindows; i++) windows.Add(UiProjection.Window("options"));
            var stamp = new SampleStamp(Scope, 9, 3);
            JObject value = UiProjection.Build(stamp, selection, windows);
            ResponseContracts.Read(WireJson.EncodeResponse("ui", ReadMethod.Ui, value), ReadMethod.Ui, "ui");
            selection.Add(UiProjection.Selection("Ada", Ids.New("p"), true));
            Invalid(() => UiProjection.Build(stamp, selection, windows)); selection.RemoveAt(0);
            windows.Add(UiProjection.Window("options")); Invalid(() => UiProjection.Build(stamp, selection, windows));
        });
        Console.WriteLine($"UI projection: {passed} passed, {failed} failed");
        return failed;
    }
    private static void Test(string name, Action action)
    { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.Message}"); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual)
    { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Invalid(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected refusal."); }
}
