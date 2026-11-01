using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class ThingProjectionTests
{
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"), Ids.New("m"));
    private sealed class Work : INestedWork { public bool TryNestedFields(int count) => true; }
    private sealed class Source : ISnapshotSource
    {
        internal long Revision, Structure;
        internal bool Available = true;
        public bool TryStructure(out long version) { version = Structure; return true; }
        public bool TryEligibility(out long revision) { revision = Revision; return Available; }
        public TargetGateResult Revalidate(object member, INestedWork work) => TargetGateResult.Eligible;
    }
    private sealed class WitnessedSource : ISnapshotSource
    {
        internal readonly EditCounter Counter = new EditCounter();
        internal bool Hidden = true;
        private readonly object member = new object();
        private readonly MemberWitnesses<bool> witnesses;
        internal WitnessedSource() { witnesses = new MemberWitnesses<bool>(Counter, (_, value) => value == Hidden); }
        internal void Record() => witnesses.Record(member, Hidden);
        public bool TryStructure(out long version) { version = 0; return true; }
        public bool TryEligibility(out long revision) => witnesses.TryRevision(out revision);
        public TargetGateResult Revalidate(object value, INestedWork work) => TargetGateResult.Eligible;
    }
    // Raw decisions include excluded members, not just the frozen eligible vector. These
    // primitives deliberately have no game objects: field/hook closure is a separate audit.
    private sealed class DecisionSource : ISnapshotSource
    {
        internal readonly List<object> Raw = new List<object>();
        internal readonly Dictionary<object, int> Values = new Dictionary<object, int>();
        internal readonly EditCounter Counter = new EditCounter();
        private readonly MemberWitnesses<int> witnesses;
        private readonly ListWitness<object> structure;
        internal int Remaining;
        internal DecisionSource(int count)
        {
            Counter.MarkInstalled();
            for (int i = 0; i < count; i++) { object member = new object(); Raw.Add(member); Values.Add(member, 0); }
            structure = new ListWitness<object>(Raw);
            witnesses = new MemberWitnesses<int>(Counter, (member, value) => Values[member] == value);
        }
        internal bool Decide(int index) => witnesses.Record(Raw[index], Values[Raw[index]]);
        public bool TryStructure(out long version) { version = structure.Unchanged(Raw) ? 0 : 1; return true; }
        public bool TryEligibility(out long revision) => witnesses.TryRevision(out revision);
        public TargetGateResult Revalidate(object member, INestedWork work)
        {
            if (Remaining == 0) return TargetGateResult.Deferred;
            Remaining--;
            return Values[member] > 0 && witnesses.Record(member, Values[member])
                ? TargetGateResult.Eligible : TargetGateResult.Unavailable;
        }
    }
    internal static int Run()
    {
        Test("thing primitives round-trip exact wire, no raw source/holder fields", () =>
        {
            JObject item = ThingProjection.Item(Ids.New("t"), "Steel x75", 17, 23, 75);
            Equal(5, item.Count);
            JObject page = Responses.Page(new SampleStamp(Scope, 7, 3), Ids.New("q"), new JArray(item), null);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Things, page), ReadMethod.Things, "read");
            Invalid(() => ThingProjection.Item(Ids.New("p"), "Steel", 1, 1, 1));
            Invalid(() => ThingProjection.Item(Ids.New("t"), "Steel", -1, 1, 1));
            Invalid(() => ThingProjection.Item(Ids.New("t"), "Steel", 1, 4096, 1));
            Invalid(() => ThingProjection.Item(Ids.New("t"), "Steel", 1, 1, 0));
            Invalid(() => ThingProjection.Item(Ids.New("t"), "<b>Hidden</b>", 1, 1, 1));
        });
        Test("resources sum only supplied eligible stacks, public definition identity not label", () =>
        {
            var totals = new ThingProjection.Resources(); object steel = new object(), other = new object();
            True(totals.Add(steel, "Steel", 75)); True(totals.Add(steel, "Steel", 25));
            True(totals.Add(other, "Steel", 12));
            JObject result = totals.Result(new SampleStamp(Scope, 7, 3));
            Equal(ThingProjection.ResourceBasis, (string?)result["basis"]);
            JArray rows = (JArray)result["items"]!; Equal(2, rows.Count);
            Equal(100L, (long)rows[0]["stackUnits"]!); Equal(12L, (long)rows[1]["stackUnits"]!);
            ResponseContracts.Read(WireJson.EncodeResponse("read", ReadMethod.Resources, result), ReadMethod.Resources, "read");
            rows[0]["stackUnits"] = 999;
            Equal(100L, (long)totals.Result(new SampleStamp(Scope, 7, 3))["items"]![0]!["stackUnits"]!);
            True(!totals.Add(steel, "Changed", 3)); True(!totals.Add(steel, "Steel", 0));
            True(!totals.Add(new object(), "<b>Hidden</b>", 1));
        });
        Test("resource kinds finite, empty means completed empty eligible vector", () =>
        {
            var totals = new ThingProjection.Resources();
            Equal(0, ((JArray)totals.Result(new SampleStamp(Scope, 7, 3))["items"]!).Count);
            for (int i = 0; i < 256; i++) True(totals.Add(new object(), "Resource", int.MaxValue));
            True(!totals.Add(new object(), "Resource", 1));
        });
        Test("long aggregation does not wrap at Int32 boundary", () =>
        {
            var totals = new ThingProjection.Resources(); object kind = new object();
            True(totals.Add(kind, "Steel", int.MaxValue)); True(totals.Add(kind, "Steel", int.MaxValue));
            Equal(4294967294L, (long)totals.Result(new SampleStamp(Scope, 7, 3))["items"]![0]!["stackUnits"]!);
        });
        Test("stable decision vector validates through actual snapshot store", () =>
        {
            var raw = new Source(); var source = new ConsistentSnapshotSource(raw); var store = new SnapshotStore();
            source.TryStructure(out long version); Equal(0L, version);
            var snapshot = store.Create(Scope, ReadMethod.Things, 1,
                new[] { ThingProjection.Item(Ids.New("t"), "Steel", 1, 1, 1) }, new object[] { new object() }, 100, source, 0);
            var pass = store.BeginRevalidation(snapshot); object update = new object();
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new Work())); True(store.Retain(pass, update));
            JObject page = store.Page(pass, update, 0, new SampleStamp(Scope, 7, 3)); Equal(true, (bool)page["complete"]!);
            raw.Revision++; // excluded member became visible OR included stack changed units
            Equal(Revalidated.Expired, store.Revalidate(pass, new object(), new Work()));
        });
        Test("excluded member revision change latches, even when snapshot has zero members", () =>
        {
            var raw = new Source(); var source = new ConsistentSnapshotSource(raw); var store = new SnapshotStore();
            var snapshot = store.Create(Scope, ReadMethod.Resources, 64, Array.Empty<JObject>(), Array.Empty<object>(), 0, source, 0);
            var pass = store.BeginRevalidation(snapshot);
            raw.Revision++;
            Equal(Revalidated.Expired, store.Revalidate(pass, new object(), new Work()));
            raw.Revision = 0; source.TryStructure(out long version); Equal(1L, version);
        });
        Test("field-admission revocation expires historical pages", () =>
        {
            bool admitted = true; var source = new ConsistentSnapshotSource(new Source(), () => admitted);
            source.TryStructure(out long version); Equal(0L, version);
            admitted = false; source.TryStructure(out version); Equal(1L, version);
            admitted = true; source.TryStructure(out version); Equal(1L, version);
        });
        Test("actual member witnesses catch excluded-member changes before reset can hide them", () =>
        {
            var raw = new WitnessedSource(); raw.Counter.MarkInstalled();
            var source = new ConsistentSnapshotSource(raw);
            raw.Record(); // filtered-out candidate is still part of the complete decision vector
            source.TryStructure(out long version); Equal(0L, version);
            raw.Hidden = false;
            source.TryStructure(out version); Equal(1L, version);
            raw.Record(); // a new gate decision must not rescue the old frozen vector
            source.TryStructure(out version); Equal(1L, version);
        });
        Test("cross-update raw scan catches either early or late excluded decision changing", () =>
        {
            // A fog/list revision alone cannot cover this: mutate a plain decision input while
            // keeping both counters unchanged, after the member's slice has already finished.
            foreach (int changed in new[] { 0, 127 })
            {
                var raw = new DecisionSource(256); var source = new ConsistentSnapshotSource(raw);
                for (int i = 0; i < 128; i++) True(raw.Decide(i));
                source.TryStructure(out long before); Equal(0L, before);
                raw.Values[raw.Raw[changed]] = 1;
                for (int i = 128; i < raw.Raw.Count; i++) True(raw.Decide(i));
                True(raw.TryStructure(out long structure)); Equal(0L, structure);
                True(raw.Counter.TryRead(out long fog)); Equal(0L, fog);
                // Even an all-excluded frozen vector must expire, never publish exact empty.
                var store = new SnapshotStore();
                var snapshot = store.Create(Scope, ReadMethod.Resources, 64,
                    Array.Empty<JObject>(), Array.Empty<object>(), 0, source, 0);
                var pass = store.BeginRevalidation(snapshot); object update = new object();
                Equal(Revalidated.Expired, store.Revalidate(pass, update, new Work()));
                True(!store.Retain(pass, update));
                raw.Values[raw.Raw[changed]] = 0;
                True(raw.Decide(changed));
                source.TryStructure(out long after); Equal(1L, after);
            }
        });
        Test("excluded mutation during retained-member slices expires the whole old vector", () =>
        {
            var raw = new DecisionSource(4); var source = new ConsistentSnapshotSource(raw);
            raw.Values[raw.Raw[1]] = raw.Values[raw.Raw[3]] = 1;
            for (int i = 0; i < raw.Raw.Count; i++) True(raw.Decide(i));
            var store = new SnapshotStore();
            var snapshot = store.Create(Scope, ReadMethod.Things, 1,
                new[] { ThingProjection.Item(Ids.New("t"), "Steel", 1, 1, 1),
                    ThingProjection.Item(Ids.New("t"), "Wood", 2, 2, 1) },
                new[] { raw.Raw[1], raw.Raw[3] }, 200, source, 0);
            var pass = store.BeginRevalidation(snapshot);
            raw.Remaining = 1;
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work()));
            raw.Values[raw.Raw[0]] = 1; // not present in snapshot.Members
            raw.Remaining = 1; object update = new object();
            Equal(Revalidated.Expired, store.Revalidate(pass, update, new Work()));
            True(!store.Retain(pass, update)); Equal(0, store.CursorCount);
        });
        Test("all-excluded raw decisions cannot silently overflow the witness table", () =>
        {
            var raw = new DecisionSource(WireLimits.SnapshotItems + 1);
            for (int i = 0; i < WireLimits.SnapshotItems; i++) True(raw.Decide(i));
            True(!raw.Decide(WireLimits.SnapshotItems));
            // Full table is a query failure, not permission to discard older hidden members.
            var source = new ConsistentSnapshotSource(raw);
            raw.Values[raw.Raw[0]] = 1;
            source.TryStructure(out long version); Equal(1L, version);
        });
        Test("missing eligibility revision fails closed, never certifies exact aggregate", () =>
        {
            var raw = new Source { Available = false }; var source = new ConsistentSnapshotSource(raw);
            raw.Available = true; source.TryStructure(out long version); Equal(1L, version);
            raw = new Source(); source = new ConsistentSnapshotSource(raw);
            raw.Structure++; source.TryStructure(out version); Equal(1L, version);
            raw.Structure = 0; source.TryStructure(out version); Equal(1L, version);
        });
        Console.WriteLine($"Thing projection: {passed} passed, {failed} failed");
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
