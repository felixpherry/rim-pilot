using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

// Pure summary DTO assembly only; the game producer is covered by configured net472 compile.
internal static class SummaryProjectionTests
{
    private static int passed, failed;
    private static readonly string Session = Ids.New("s"), Root = Ids.New("r"), Game = Ids.New("g"), Map = Ids.New("m");

    internal static int Run()
    {
        Test("menu summary has scene/build/manifest only and validates", () =>
        {
            var sample = new SampleStamp(new Scope(Session, Root), 7, null);
            JObject data = SummaryProjection.Build(sample, "1.6.4633 rev1261", null, null, null,
                new[] { new ManifestRow("ludeon.rimworld.royalty", "Royalty") },
                new[] { new ManifestRow("ludeon.rimworld", "Core"), new ManifestRow("ludeon.rimworld.royalty", "Royalty") });
            Equal("menu", (string)data["scene"]!); True(data["paused"] == null && data["maps"] == null && data["currentMap"] == null);
            True(data["sample"]!["tick"] == null);
            Valid(data);
        });
        Test("playing summary keeps unavailable fields absent, never zero", () =>
        {
            var sample = new SampleStamp(new Scope(Session, Root, Game), 9, 1200);
            var maps = new[] { new MapSummaryRow(Map, "Ödvang 新", 250, 250, 3), new MapSummaryRow(Ids.New("m"), "Site", null, null, null) };
            JObject data = SummaryProjection.Build(sample, "1.6", true, Map, maps, null, null);
            Equal("playing", (string)data["scene"]!); Equal(true, (bool)data["paused"]!); Equal(Map, (string)data["currentMap"]!);
            Equal(3, (int)data["maps"]![0]!["eligibleOwnPawns"]!); True(data["maps"]![1]!["eligibleOwnPawns"] == null);
            True(data["maps"]![1]!["width"] == null && data["mods"] == null && data["dlcs"] == null);
            Equal("Ödvang 新", (string)data["maps"]![0]!["label"]!);
            Valid(data);
            JObject empty = SummaryProjection.Build(sample, "1.6", null, null, new MapSummaryRow[0], new ManifestRow[0], new ManifestRow[0]);
            Equal(0, ((JArray)empty["maps"]!).Count); Valid(empty);
        });
        Test("staged one-row-at-a-time manifest JSON builds the same summary and checks counts", () =>
        {
            var sample = new SampleStamp(new Scope(Session, Root), 7, null);
            var dlcs = new[] { new ManifestRow("ludeon.rimworld.royalty", "Royalty") };
            var mods = new[] { new ManifestRow("ludeon.rimworld", "Core"), new ManifestRow("ludeon.rimworld.royalty", "Royalty") };
            JObject once = SummaryProjection.Build(sample, "1.6", null, null, null, dlcs, mods);
            var dlcRows = new JArray(); foreach (ManifestRow row in dlcs) dlcRows.Add(SummaryProjection.ManifestRowJson(row));
            var modRows = new JArray(); foreach (ManifestRow row in mods) modRows.Add(SummaryProjection.ManifestRowJson(row));
            JObject staged = SummaryProjection.Build(sample, "1.6", null, null, null, dlcRows.Count, modRows.Count, dlcRows, modRows);
            Equal(once.ToString(), staged.ToString()); Valid(staged);
            // A discarded attempt detaches the staged arrays so a retry reuses them without cloning.
            SummaryProjection.DetachManifests(staged); True(dlcRows.Parent == null && modRows.Parent == null);
            Equal(once.ToString(), SummaryProjection.Build(sample, "1.6", null, null, null, dlcRows.Count, modRows.Count, dlcRows, modRows).ToString());
            Invalid(() => SummaryProjection.Build(sample, "1.6", null, null, null, 1, 2, new JArray(), modRows));
            Invalid(() => SummaryProjection.ManifestRowJson(new ManifestRow("", "A")));
            Invalid(() => SummaryProjection.ManifestRowJson(new ManifestRow("a", new string('x', 513))));
        });
        Test("invalid or over-limit inputs are refused, never truncated", () =>
        {
            var menu = new SampleStamp(new Scope(Session, Root), 1, null);
            var play = new SampleStamp(new Scope(Session, Root, Game), 1, 1);
            Invalid(() => SummaryProjection.Build(menu, "1.6", false, null, null, null, null));
            Invalid(() => SummaryProjection.Build(menu, "1.6", null, null, new MapSummaryRow[0], null, null));
            Invalid(() => SummaryProjection.Build(new SampleStamp(new Scope(Session, Root, Game, Map), 1, 1), "1.6", null, null, null, null, null));
            Invalid(() => SummaryProjection.Build(play, "", null, null, null, null, null));
            Invalid(() => SummaryProjection.Build(play, new string('x', 513), null, null, null, null, null));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, "m_bad", null, null, null));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, null, new[] { new MapSummaryRow(Map, "A", 4097, 1, null) }, null, null));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, null, new[] { new MapSummaryRow(Map, "A", 1, 1, 2049) }, null, null));
            var many = new List<MapSummaryRow>();
            for (int i = 0; i <= SummaryProjection.MaxMaps; i++) many.Add(new MapSummaryRow(Ids.New("m"), "A", 1, 1, 0));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, null, many, null, null));
            var dlcs = new List<ManifestRow>();
            for (int i = 0; i <= SummaryProjection.MaxDlcs; i++) dlcs.Add(new ManifestRow("a", "A"));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, null, null, dlcs, null));
            Invalid(() => SummaryProjection.Build(play, "1.6", null, null, null, null, new[] { new ManifestRow(new string('a', 257), "A") }));
            True(SummaryProjection.Text("\ud800", 10) == null); True(SummaryProjection.Text(null, 10) == null);
            True(SummaryProjection.DefinitionLabel("<b>x</b>") == null); Equal("Settlement", SummaryProjection.DefinitionLabel("settlement"));
            True(SummaryProjection.Dimension(0) == null);
        });
        Console.WriteLine($"Summary projection: {passed} passed, {failed} failed (host primitives, not game proof).");
        return failed;
    }

    private static void Valid(JObject data) =>
        ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Summary, data), ReadMethod.Summary, "read");
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted invalid summary"); }
        catch (ArgumentException) { }
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL summary {name}: {error.Message}"); }
    }
}
