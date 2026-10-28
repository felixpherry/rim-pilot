using System;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class PublicEventWitnessTests
{
    private sealed class Work : INestedWork { public bool TryNestedFields(int n) => true; }
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"));
    internal static int Run()
    {
        Test("strip presentation tags, never return faction/ideo arguments", () =>
        {
            Equal("Ada joined Colony", PublicEventText.Sanitize("(*Name)Ada(/Name) joined (*Faction=Faction_123)<color=#ffeeff>Colony</color>(/Faction)", 2048));
            Equal("News\nPublic", PublicEventText.Sanitize("<b>News</b>\n<i>Public</i>", 2048));
            foreach (string raw in new[] { "<secret=x>Text</secret>", "(*Unknown=secret)Text(/Unknown)", "<b", "\0", "\ud800", new string('x', 4097) })
                Equal<string?>(null, PublicEventText.Sanitize(raw, 2048));
            Equal<string?>(null, PublicEventText.Sanitize(new string('x', 513), 512));
        });
        Test("canonical identity dedup, identical text on distinct deliveries allowed", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring); object a = new object(), b = new object();
            object acceptance = new object();
            w.Accepted(acceptance, a, null, false, null, "Public"); w.Accepted(acceptance, a, null, false, null, "Public");
            Equal(2, ring.Count); w.Accepted(new object(), b, null, false, null, "Public"); Equal(3, ring.Count);
            w.Accepted(new object(), a, null, false, null, "Public"); Equal(4, ring.Count);
        });
        Test("nonhistorical message expires at live eviction with gap", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring); string cursor = Tail(ring); object a = new object();
            w.Accepted(new object(), a, null, false, null, "Public"); w.RefreshMessages(new[] { a }, 1); Equal(2, ring.Count);
            w.RefreshMessages(Array.Empty<object>(), 0); Equal(1, ring.Count); Gap(ring, cursor);
        });
        Test("historical message survives live expiry but not archive removal", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring); object a = new object(), archive = new object();
            w.Accepted(new object(), a, archive, false, null, "Public"); w.RefreshMessages(Array.Empty<object>(), 0); Equal(2, ring.Count);
            w.RemovedFromArchive(new object(), a); Equal(2, ring.Count);
            w.RemovedFromArchive(archive, a); Equal(1, ring.Count);
        });
        Test("letter remains available live OR archived, no archive enumeration", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring); object a = new object(), archive = new object();
            w.Accepted(new object(), a, archive, true, "Label", "Public"); w.RemovedFromArchive(archive, a); Equal(2, ring.Count);
            w.RemovedLetter(a); Equal(1, ring.Count);
            object b = new object(); w.Accepted(new object(), b, archive, true, "Label", "Public"); w.RemovedLetter(b); Equal(2, ring.Count);
            w.RemovedFromArchive(archive, b); Equal(1, ring.Count);
        });
        Test("bounded dedup table continues past entry and byte retention", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring);
            for (int i = 0; i < 800; i++) w.Accepted(new object(), new object(), new object(), false, null, "Public " + i);
            Equal(256, ring.Count);
            JObject page = ring.Read(Scope, null, 64, new Work());
            Equal("Public 544", (string?)page["items"]![0]!["text"]);
            for (int i = 0; i < 100; i++) w.Accepted(new object(), new object(), null, false, null, new string('雪', 2048));
            True(ring.Bytes <= WireLimits.EventBytes); True(ring.Count < 256);
        });
        Test("bad payload never evicts history even with full witness table", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring);
            for (int i = 0; i < 256; i++) w.Accepted(new object(), new object(), null, false, null, "Public");
            JObject before = ring.Read(Scope, null, 64, new Work());
            w.Accepted(new object(), new object(), null, false, null, "<secret>");
            Equal(before.ToString(), ring.Read(Scope, null, 64, new Work()).ToString());
        });
        Test("unsafe reset cannot restore old publication or availability witness", () =>
        {
            var ring = Ring(); var w = new PublicEventWitnesses(ring); object a = new object(), archive = new object();
            w.Accepted(new object(), a, archive, false, null, "Old public"); ring.Suspend(); w.Clear(); ring.Ready(Scope);
            w.RemovedFromArchive(archive, a); w.RefreshMessages(new[] { a }, 1);
            Equal(1, ring.Count); Equal("observation_resumed", (string?)ring.Read(Scope, null, 64, new Work())["items"]![0]!["text"]);
        });
        Console.WriteLine($"Public event witnesses: {passed} passed, {failed} failed (host only)"); return failed;
    }
    private static PublicEventRing Ring() { var ring = new PublicEventRing(() => 0); ring.Ready(Scope); return ring; }
    private static string Tail(PublicEventRing ring) => (string)ring.Read(Scope, null, 64, new Work())["next"]!;
    private static void Gap(PublicEventRing ring, string cursor) { try { ring.Read(Scope, cursor, 64, new Work()); } catch (WireException e) when (e.Code == WireError.EventGap) { return; } throw new Exception("Expected gap"); }
    private static void Test(string name, Action action) { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); } }
    private static void Equal<T>(T a, T b) { if (!Equals(a, b)) throw new Exception($"Expected {a}, got {b}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
}
