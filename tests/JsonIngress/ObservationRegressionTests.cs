using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

// Cross-channel composition of real host primitives, NOT execution of Verse adapters.
// Source-wiring guards and coverage/observation-adversarial.md explain that boundary.
internal static class ObservationRegressionTests
{
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"), Ids.New("m"));
    private sealed class Work : INestedWork { public bool TryNestedFields(int n) => true; }
    private struct Fog : IFogCells
    {
        public int Length => 100;
        public bool Fogged(int index) => index != 11;
    }
    private struct Concealment : IPawnConcealment
    {
        internal bool Hidden;
        public int Hediffs => 1;
        public int Comps(int i) => 1;
        public bool MayHide(int i, int j) => Hidden;
    }
    private sealed class Source : ISnapshotSource
    {
        public bool TryStructure(out long version) { version = 0; return true; }
        public bool TryEligibility(out long revision) { revision = 0; return true; }
        public TargetGateResult Revalidate(object member, INestedWork work) => TargetGateResult.Eligible;
    }
    private sealed class Candidate
    {
        internal readonly bool Hidden;
        internal readonly int X;
        internal Candidate(int x, bool hidden) { X = x; Hidden = hidden; }
        internal string Label => Hidden || X != 1 ? throw new Exception("hidden label evaluated") : "Steel";
    }

    internal static int Run()
    {
        Test("hidden population cannot affect eligible DTOs, totals, selection or reference allocation", () =>
        {
            object map = new object(), kind = new object();
            var visible = new Candidate(1, false);
            var refs = new ReferenceTable();
            var stamp = new SampleStamp(Scope, 9, 7);
            var summaryStamp = new SampleStamp(new Scope(Scope.Session, Scope.Root, Scope.Game), 9, 7);
            JObject Project(params Candidate[] candidates)
            {
                var items = new List<JObject>(); var members = new List<object>();
                var selection = new JArray(); var totals = new ThingProjection.Resources();
                foreach (Candidate candidate in candidates)
                {
                    if (TargetGate.Footprint(new Fog(), 10, 10, candidate.X, 1, 1, 1) != TargetGateResult.Eligible
                        || TargetGate.Pawn(true, false, new Concealment { Hidden = candidate.Hidden }, new Work()) != TargetGateResult.Eligible) continue;
                    string id = refs.Allocate("t", candidate, map);
                    items.Add(ThingProjection.Item(id, candidate.Label, candidate.X, 1, 3)); members.Add(candidate);
                    selection.Add(UiProjection.Selection(candidate.Label, id, false));
                    True(totals.Add(kind, candidate.Label, 3));
                }
                var store = new SnapshotStore();
                var snapshot = store.Create(Scope, ReadMethod.Things, 64, items.ToArray(), members.ToArray(), 100, new Source(), 0);
                var pass = store.BeginRevalidation(snapshot); object frame = new object();
                Equal(Revalidated.Valid, store.Revalidate(pass, frame, new Work())); True(store.Retain(pass, frame));
                JObject page = store.Page(pass, frame, 0, stamp);
                // Random snapshot identity differs between builds; compare public data, not randomness.
                page.Remove("snapshot");
                return new JObject
                {
                    ["page"] = page,
                    ["resources"] = totals.Result(stamp),
                    ["ui"] = UiProjection.Build(stamp, selection, new JArray()),
                    ["summary"] = SummaryProjection.Build(summaryStamp,
                        "1.6", true, Scope.Map, new[] { new MapSummaryRow(Scope.Map!, "Colony", 10, 10, members.Count) }, null, null)
                };
            }
            string control = Project(visible).ToString(Formatting.None);
            var hidden = Enumerable.Range(0, 128).Select(i => new Candidate(i % 2 == 0 ? 1 : 2, true)).ToArray();
            Equal(control, Project(new[] { visible }.Concat(hidden).ToArray()).ToString(Formatting.None));
            Equal(1, refs.Count);
            Equal(Project().ToString(Formatting.None), Project(hidden).ToString(Formatting.None));
            Equal(1, refs.Count);
        });
        Test("rejected public deliveries do not advance retained event sequence or alter payloads", () =>
        {
            var scope = new Scope(Scope.Session, Scope.Root, Scope.Game);
            var control = new PublicEventRing(() => 0); var subject = new PublicEventRing(() => 0);
            control.Ready(scope); subject.Ready(scope);
            for (int i = 0; i < 256; i++)
                foreach (PublicationDisposition disposition in Enum.GetValues<PublicationDisposition>())
                    if (disposition != PublicationDisposition.AcceptedPublic)
                        Equal(0L, subject.Publish(disposition, true, true, true, "message", null, "Secret"));
            Equal(control.Publish(PublicationDisposition.AcceptedPublic, true, true, true, "message", null, "Public"),
                subject.Publish(PublicationDisposition.AcceptedPublic, true, true, true, "message", null, "Public"));
            Equal(control.Count, subject.Count); Equal(control.Bytes, subject.Bytes);
            Equal(control.Read(scope, null, 64, new Work())["items"]!.ToString(), subject.Read(scope, null, 64, new Work())["items"]!.ToString());
        });
        Test("capability availability follows each registered route, never blanket baseline status", () =>
        {
            foreach (ReadMethod enabled in Enum.GetValues<ReadMethod>().Where(m => m > ReadMethod.Capabilities))
            {
                JObject data = Responses.BootstrapCapabilities(m => m == enabled);
                var available = ((JArray)data["capabilities"]!).Where(c => (string?)c["state"] == "available").ToArray();
                Equal(enabled == ReadMethod.Screenshot ? 5 : 4, available.Length);
                Equal(2, ((JArray)data["capabilities"]!).Count(c => (string?)c["state"] == "unsupported"));
                ResponseContracts.Read(WireJson.EncodeResponse("caps", ReadMethod.Capabilities, data), ReadMethod.Capabilities, "caps");
            }
        });
        Test("mutation aliases, raw input and reflection cannot enter typed read dispatch", () =>
        {
            foreach (string method in new[] { "input.click", "ui.click", "pawn.draft", "game.pause", "save.load", "debug.eval", "reflection", "invoke", "set", "GetType", "camera.move", "grant.create" })
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new JObject { ["v"] = 1, ["id"] = "deny", ["method"] = method, ["params"] = new JObject() }.ToString(Formatting.None));
                try { Request.Read(bytes); throw new Exception("mutation accepted"); }
                catch (WireException error) { Equal(WireError.UnsupportedMethod, error.Code); }
            }
        });
        Console.WriteLine($"Observation regressions: {passed} passed, {failed} failed (host composition, not Mono/game proof).");
        return failed;
    }
    private static void Test(string name, Action action)
    { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL observation {name}: {e}"); } }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T a, T b) { if (!Equals(a, b)) throw new Exception($"Expected {a}, got {b}."); }
}
