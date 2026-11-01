using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RimPilot.Json;
using RimPilot.Protocol;

internal static class ObservationSafetyTests
{
    private static int passed;
    private static int failed;
    private static Scope Scope() => new Scope("s_11111111111111111111111111111111", "r_22222222222222222222222222222222");
    private static byte[] Bytes(JToken token) => Encoding.UTF8.GetBytes(token.ToString(Formatting.None));
    internal static int Run()
    {
        foreach (PublicationDisposition disposition in Enum.GetValues<PublicationDisposition>())
            Test("canonical audited safe publication " + disposition, () =>
                Equal(disposition == PublicationDisposition.AcceptedPublic, PublicEventPolicy.CanPublish(disposition, true, true, true)));
        Test("delayed letter publishes only at later accepted delivery", () =>
        {
            True(!PublicEventPolicy.CanPublish(PublicationDisposition.Delayed, true, true, true));
            True(PublicEventPolicy.CanPublish(PublicationDisposition.AcceptedPublic, true, true, true));
        });
        Test("overload and duplicate callbacks cannot republish", () =>
            True(!PublicEventPolicy.CanPublish(PublicationDisposition.AcceptedPublic, true, false, true)));
        Test("unaudited notification payload absent", () =>
            True(!PublicEventPolicy.CanPublish(PublicationDisposition.AcceptedPublic, false, true, true)));
        Test("unsafe publication never buffered for later replay", () =>
            True(!PublicEventPolicy.CanPublish(PublicationDisposition.AcceptedPublic, true, true, false)));
        for (int mask = 0; mask < 16; mask++)
        {
            int bits = mask;
            Test("developer safety truth table " + bits, () => Equal(bits != 12,
                DeveloperObservationPolicy.IsUnsafe((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0)));
        }
        Test("dev disabled does not authorize lingering panel or flags", () =>
        {
            True(DeveloperObservationPolicy.IsUnsafe(false, true, true, true));
            True(DeveloperObservationPolicy.IsUnsafe(false, false, false, true));
            True(DeveloperObservationPolicy.IsUnsafe(false, false, true, false));
        });
        var scope = Scope();
        var request = new CaptureRequestStamp(scope, 3, 20);
        var rendered = new RenderEvidence(scope, 3, 21, true, true);
        Test("fresh safe completed render receives independent capture ID", () =>
        {
            var a = CompletedRenderStamp.Complete(request, rendered, scope, 3, null);
            var b = CompletedRenderStamp.Complete(request, rendered, scope, 3, null);
            True(Ids.Is(a.Capture, "c")); True(a.Capture != b.Capture);
            True(a.Capture != new SampleStamp(scope, 21, null).Capture);
            Equal(21L, a.Frame); a.CheckRelease(scope, 3, null);
            JObject data = Responses.Capture(a, 1, 1, 1, new byte[8]);
            Equal("completed_render", (string)data["phase"]!); True(data["safetyEpoch"] == null);
        });
        foreach (long frame in new long[] { 19, 20 })
            Test("prior or same request frame refused " + frame, () => Reject(() =>
                CompletedRenderStamp.Complete(request, new RenderEvidence(scope, 3, frame, true, true), scope, 3, null), WireError.NotReady));
        foreach (bool completed in new[] { false, true })
            foreach (bool safe in new[] { false, true })
                if (!completed || !safe)
                    Test("partial or unsafe rendered frame " + completed + "/" + safe, () => Reject(() =>
                        CompletedRenderStamp.Complete(request, new RenderEvidence(scope, 3, 21, completed, safe), scope, 3, null), WireError.NotReady));
        Test("safe now does not release earlier unsafe epoch", () => Reject(() =>
            CompletedRenderStamp.Complete(request, rendered, scope, 5, null), WireError.NotReady));
        Test("render from other safety epoch refused", () => Reject(() =>
            CompletedRenderStamp.Complete(request, new RenderEvidence(scope, 2, 21, true, true), scope, 3, null), WireError.NotReady));
        var replacement = new Scope(scope.Session, Ids.New("r"));
        Test("render scope replacement refused", () => Reject(() =>
            CompletedRenderStamp.Complete(request, new RenderEvidence(replacement, 3, 21, true, true), scope, 3, null), WireError.StaleScope));
        Test("current scope replacement refused", () => Reject(() =>
            CompletedRenderStamp.Complete(request, rendered, replacement, 3, null), WireError.StaleScope));
        Test("developer refusal precedes scene and epoch mismatch", () => Reject(() =>
            CompletedRenderStamp.Complete(request, rendered, replacement, 4, WireError.ObservationRefused), WireError.ObservationRefused));
        var stamp = CompletedRenderStamp.Complete(request, rendered, scope, 3, null);
        foreach (WireError refusal in new[] { WireError.NotReady, WireError.ObservationRefused, WireError.Timeout })
            Test("release rechecks readiness/cancellation " + refusal, () => Reject(() => stamp.CheckRelease(scope, 3, refusal), refusal));
        Test("dev on then off during encode invalidates cached capture", () => Reject(() => stamp.CheckRelease(scope, 5, null), WireError.NotReady));
        Test("scope changes after encode invalidate capture", () => Reject(() => stamp.CheckRelease(replacement, 3, null), WireError.StaleScope));
        Test("map-view proof cannot authorize menu capture", () =>
        {
            var menu = new Scope(Ids.New("s"), Ids.New("r"));
            Reject(() => CompletedRenderStamp.Complete(new CaptureRequestStamp(menu, 3, 20),
                new RenderEvidence(menu, 3, 21, true, false, true), menu, 3, null), WireError.NotReady);
        });
        Test("weaker map-view proof still requires completion", () => Reject(() =>
            CompletedRenderStamp.Complete(request, new RenderEvidence(scope, 3, 21, false, false, true),
                scope, 3, null), WireError.NotReady));
        Test("invalid capture evidence cannot be constructed", () =>
        {
            Reject(() => new CaptureRequestStamp(scope, 0, 20), WireError.InvalidRequest);
            Reject(() => new RenderEvidence(scope, 1, -1, true, true), WireError.InvalidRequest);
        });

        JObject fixtures = (JObject)StrictJson.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "responses.json")));
        JObject Event(string kind, string text) => new JObject { ["kind"] = kind, ["text"] = text };
        void CheckEvent(JObject item, bool accepted)
        {
            JObject response = (JObject)fixtures["accepted"]![10]!.DeepClone();
            response["data"]!["items"] = new JArray(item);
            if (accepted) ResponseContracts.Read(Bytes(response), ReadMethod.Events, "e");
            else Reject(() => ResponseContracts.Read(Bytes(response), ReadMethod.Events, "e"), WireError.InvalidRequest);
        }
        foreach (string lifecycle in new[] { "observation_ready", "scope_changed", "observation_resumed" })
            Test("fixed lifecycle " + lifecycle, () => CheckEvent(Event("lifecycle", lifecycle), true));
        Test("arbitrary lifecycle diagnostics absent", () => CheckEvent(Event("lifecycle", "hidden incident attempted"), false));
        Test("lifecycle label cannot carry debug state", () =>
        { JObject item = Event("lifecycle", "observation_ready"); item["label"] = "debug secret"; CheckEvent(item, false); });
        foreach (string extra in new[] { "debugInfo", "lookTargets", "arrivalTick", "rawSequence", "quest", "faction", "hiddenCount", "getter" })
            Test("notification excludes " + extra, () =>
            { JObject item = Event("letter", "Public text"); item[extra] = "private"; CheckEvent(item, false); });
        foreach (var entry in new[] { (8, ReadMethod.Ui, "u", "windows", "internals"), (8, ReadMethod.Ui, "u", "controls", "label"),
            (4, ReadMethod.Pawn, "d", "pawn", "summaryHealthPercent"), (7, ReadMethod.Resources, "r", "items", "rawCount") })
            Test("unaudited DTO field absent " + entry.Item5, () =>
            {
                JObject response = (JObject)fixtures["accepted"]![entry.Item1]!.DeepClone();
                JToken data = response["data"]!;
                if (entry.Item4 == "controls") data[entry.Item4] = new JArray(new JObject { [entry.Item5] = "offscreen" });
                else if (data[entry.Item4] is JArray array) array[0]![entry.Item5] = "private";
                else data[entry.Item4]![entry.Item5] = "private";
                Reject(() => ResponseContracts.Read(Bytes(response), entry.Item2, entry.Item3), WireError.InvalidRequest);
            });
        Console.WriteLine($"Observation safety contracts: {passed} passed; {failed} failed. Synthetic evidence only; no game hooks/render proof.");
        return failed;
    }
    private static void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS safety " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL safety " + name + ": " + error); } }
    private static void Reject(Action action, WireError code) { try { action(); } catch (WireException error) { Equal(code, error.Code); Equal("Bridge request rejected.", error.Message); return; } throw new Exception("Expected " + code); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
}
