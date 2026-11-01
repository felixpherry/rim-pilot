using System;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class PublicEventRingTests
{
    private sealed class Work : INestedWork { public bool TryNestedFields(int n) => n <= 1024; }
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"));
    private static JObject Read(PublicEventRing ring, string? cursor = null, int limit = 64) => ring.Read(Scope, cursor, limit, new Work());
    private static long Add(PublicEventRing ring, string text = "Public") => ring.Publish(PublicationDisposition.AcceptedPublic, true, true, true, "message", null, text);
    internal static int Run()
    {
        Test("independent readers, immutable reusable boundaries, empty tail", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope); Add(ring);
            JObject a = Read(ring, null, 1), b = Read(ring, null, 1);
            Equal(a.ToString(), b.ToString()); string c = (string)a["next"]!;
            JObject page = Read(ring, c); Equal("Public", (string?)page["items"]![0]!["text"]);
            page["items"]![0]!["text"] = "Changed";
            Equal("Public", (string?)Read(ring, c)["items"]![0]!["text"]);
            string tail = (string)Read(ring, c)["next"]!;
            Equal(tail, (string?)Read(ring, tail)["next"]); Equal(0, ((JArray)Read(ring, tail)["items"]!).Count);
            ResponseContracts.Read(WireJson.EncodeResponse("ev", ReadMethod.Events, Read(ring)), ReadMethod.Events, "ev");
        });
        Test("filter precedes sequence, retention, cursor and allocation", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope);
            foreach (PublicationDisposition d in Enum.GetValues(typeof(PublicationDisposition)))
                if (d != PublicationDisposition.AcceptedPublic) Equal(0L, ring.Publish(d, true, true, true, "message", null, "Secret"));
            Equal(0L, ring.Publish(PublicationDisposition.AcceptedPublic, true, true, false, "message", null, "Secret"));
            Equal(0L, ring.Publish(PublicationDisposition.AcceptedPublic, false, true, true, "message", null, "Secret"));
            foreach (string text in new[] { "", "<b>Text</b>", "\0", "\ud800", new string('x', 2049) }) Equal(0L, Add(ring, text));
            Equal(1, ring.Count); Equal(2L, Add(ring)); Equal(0, ring.CursorCount);
        });
        Test("entry overflow yields gap without loss detail", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope); string c = (string)Read(ring)["next"]!;
            for (int i = 0; i < 260; i++) Add(ring);
            Equal(256, ring.Count); Error(WireError.EventGap, () => Read(ring, c)); Equal(64, ((JArray)Read(ring)["items"]!).Count);
        });
        Test("byte retention bounds encoded Unicode and escaping", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope);
            for (int i = 0; i < 256; i++) Add(ring, new string('雪', 2048));
            True(ring.Bytes <= WireLimits.EventBytes); True(ring.Count < 256);
        });
        Test("removed availability gaps only readers before missing publication", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope); string before = (string)Read(ring)["next"]!;
            long id = Add(ring); string after = (string)Read(ring)["next"]!; ring.Remove(id);
            Error(WireError.EventGap, () => Read(ring, before)); Equal(0, ((JArray)Read(ring, after)["items"]!).Count);
        });
        Test("unsafe clears history and cursors, safe resume fixed text only", () =>
        {
            var ring = new PublicEventRing(() => 0); ring.Ready(Scope); Add(ring); string c = (string)Read(ring)["next"]!;
            ring.Suspend(); Equal(0, ring.Count); Equal(0L, Add(ring)); ring.Ready(Scope);
            Error(WireError.CursorExpired, () => Read(ring, c)); Equal("observation_resumed", (string?)Read(ring)["items"]![0]!["text"]);
            var next = new Scope(Scope.Session, Ids.New("r")); ring.Suspend(); ring.Ready(next);
            Error(WireError.CursorExpired, () => Read(ring, c));
            Equal("scope_changed", (string?)ring.Read(next, null, 64, new Work())["items"]![0]!["text"]);
        });
        Test("cursor expiry finite table and unknown indistinguishable", () =>
        {
            long now = 0; var ring = new PublicEventRing(() => now); ring.Ready(Scope);
            string first = (string)Read(ring)["next"]!;
            string cursor = first;
            for (int i = 0; i < 128; i++) { Add(ring); cursor = (string)Read(ring, cursor)["next"]!; }
            Equal(128, ring.CursorCount); Error(WireError.CursorExpired, () => Read(ring, first));
            string last = (string)Read(ring)["next"]!; now = 5000;
            Error(WireError.CursorExpired, () => Read(ring, last)); Error(WireError.CursorExpired, () => Read(ring, Ids.New("e")));
        });
        Test("reused tail cursor expires only after five idle seconds", () =>
        {
            long now = 0; var ring = new PublicEventRing(() => now); ring.Ready(Scope); Add(ring);
            string tail = (string)Read(ring)["next"]!;
            for (int step = 0; step < 4; step++) { now += 3000; Equal(tail, (string?)Read(ring, tail)["next"]); }
            Equal(12000L, now); now += 5000;
            Error(WireError.CursorExpired, () => Read(ring, tail));
        });
        Console.WriteLine($"Public event ring: {passed} passed, {failed} failed"); return failed;
    }
    private static void Test(string name, Action action) { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e}"); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T a, T b) { if (!Equals(a, b)) throw new Exception($"Expected {a}, got {b}."); }
    private static void Error(WireError error, Action action) { try { action(); } catch (WireException e) when (e.Code == error) { return; } throw new Exception("Expected " + error); }
}
