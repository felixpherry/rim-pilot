using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class SnapshotStoreTests
{
    private static int passed, failed;
    private static readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"), Ids.New("m"));

    // Members are ints; eligible unless listed. Witnesses are mutable to simulate churn.
    private sealed class Source : ISnapshotSource
    {
        internal readonly HashSet<int> Hidden = new HashSet<int>();
        internal long Structure, Eligibility;
        internal bool HasEligibility, StructureAvailable = true;
        internal int Calls;
        internal Action<int>? OnVisit;
        public bool TryStructure(out long version) { version = Structure; return StructureAvailable; }
        public bool TryEligibility(out long revision) { revision = Eligibility; return HasEligibility; }
        public TargetGateResult Revalidate(object member, INestedWork work)
        {
            Calls++; OnVisit?.Invoke((int)member);
            return Hidden.Contains((int)member) ? TargetGateResult.Unavailable : TargetGateResult.Eligible;
        }
    }
    // Production witness bookkeeping over int members; Hidden is the simulated game state.
    private sealed class WitnessedSource : ISnapshotSource
    {
        internal readonly HashSet<int> Hidden = new HashSet<int>();
        internal readonly EditCounter Counter = new EditCounter();
        private readonly MemberWitnesses<bool> witnesses;
        internal int Calls;
        internal WitnessedSource() { witnesses = new MemberWitnesses<bool>(Counter, (m, hidden) => Hidden.Contains((int)m) == hidden); }
        public bool TryStructure(out long version) { version = 0; return true; }
        public bool TryEligibility(out long revision) => witnesses.TryRevision(out revision);
        public TargetGateResult Revalidate(object member, INestedWork work)
        {
            Calls++; bool hidden = Hidden.Contains((int)member);
            if (!witnesses.Record(member, hidden)) return TargetGateResult.Unavailable;
            return hidden ? TargetGateResult.Unavailable : TargetGateResult.Eligible;
        }
    }
    private sealed class Work : INestedWork
    {
        internal int Left;
        internal Work(int left) { Left = left; }
        public bool TryNestedFields(int count) { if (count > Left) return false; Left -= count; return true; }
    }
    private sealed class Clock { internal long Now; }

    private static EligibleSnapshot Make(SnapshotStore store, ISnapshotSource source, int count, int limit = 2, ReadMethod method = ReadMethod.Things)
    {
        var items = new JObject[count]; var members = new object[count];
        for (int i = 0; i < count; i++) { items[i] = new JObject { ["n"] = i }; members[i] = i; }
        source.TryStructure(out long structure);
        return store.Create(Scope, method, limit, items, members, count * 8, source, structure);
    }
    private static Revalidation Proven(SnapshotStore store, EligibleSnapshot snapshot, object update)
    {
        Revalidation pass = store.BeginRevalidation(snapshot);
        Equal(Revalidated.Valid, store.Revalidate(pass, update, new Work(10000)));
        return pass;
    }
    private static SampleStamp Sample() => new SampleStamp(Scope, 1, 1);

    internal static int Run()
    {
        Test("retained snapshot pages through reusable opaque cursors", () =>
        {
            var store = new SnapshotStore(); var source = new Source(); object update = new object();
            EligibleSnapshot snapshot = Make(store, source, 5);
            Revalidation pass = Proven(store, snapshot, update);
            True(store.Retain(pass, update));
            JObject first = store.Page(pass, update, 0, Sample());
            Equal(snapshot.Id, (string)first["snapshot"]!); Equal(false, (bool)first["complete"]!);
            Equal(2, ((JArray)first["items"]!).Count); True(first["total"] == null);
            string next = (string)first["next"]!;
            True(Ids.Is(next, "q") && next != snapshot.Id);
            // Repeated polls reuse the same boundary cursor; no allocation per poll.
            Equal(next, (string)store.Page(pass, update, 0, Sample())["next"]!); Equal(1, store.CursorCount);
            True(store.TryResolve(Scope, ReadMethod.Things, 2, next, out EligibleSnapshot? found, out int start));
            True(ReferenceEquals(snapshot, found)); Equal(2, start);
            JObject second = store.Page(pass, update, start, Sample());
            JObject last = store.Page(pass, update, 4, Sample());
            Equal(1, ((JArray)last["items"]!).Count); Equal(true, (bool)last["complete"]!); True(last["next"] == null);
            // Non-consuming: the same cursor still resolves.
            True(store.TryResolve(Scope, ReadMethod.Things, 2, next, out _, out _));
            Equal(2, (int)((JArray)second["items"]!)[0]["n"]!);
        });
        Test("wrong binding, unknown and malformed cursors are uniformly unresolved", () =>
        {
            var store = new SnapshotStore(); var source = new Source(); object update = new object();
            Revalidation pass = Proven(store, Make(store, source, 5), update);
            store.Retain(pass, update);
            string next = (string)store.Page(pass, update, 0, Sample())["next"]!;
            True(!store.TryResolve(Scope, ReadMethod.Pawns, 2, next, out _, out _));
            True(!store.TryResolve(Scope, ReadMethod.Things, 3, next, out _, out _));
            True(!store.TryResolve(new Scope(Scope.Session, Scope.Root, Scope.Game), ReadMethod.Things, 2, next, out _, out _));
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, Ids.New("q"), out _, out _));
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, "q_0", out _, out _));
        });
        Test("unproven, stale-update or unretained snapshots never publish", () =>
        {
            var store = new SnapshotStore(); var source = new Source(); object update = new object();
            EligibleSnapshot snapshot = Make(store, source, 3);
            Revalidation pass = store.BeginRevalidation(snapshot);
            True(!store.Retain(pass, update));
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new Work(100)));
            Expired(() => store.Page(pass, update, 0, Sample())); // proven but not retained
            True(!store.Retain(pass, new object())); // proof belongs to its own update only
            True(store.Retain(pass, update));
            Expired(() => store.Page(pass, new object(), 0, Sample()));
            Expired(() => store.Page(pass, update, 1, Sample())); // not a page boundary
            Expired(() => store.Page(pass, update, 4, Sample()));
        });
        Test("empty snapshot publishes one complete empty page", () =>
        {
            var store = new SnapshotStore(); object update = new object();
            Revalidation pass = Proven(store, Make(store, new Source(), 0), update);
            True(store.Retain(pass, update));
            JObject page = store.Page(pass, update, 0, Sample());
            Equal(0, ((JArray)page["items"]!).Count); Equal(true, (bool)page["complete"]!);
        });
        Test("historical DTO values are frozen copies", () =>
        {
            var store = new SnapshotStore(); object update = new object();
            var item = new JObject { ["n"] = 1 };
            EligibleSnapshot snapshot = store.Create(Scope, ReadMethod.Things, 4, new[] { item }, new object[] { 1 }, 8, new Source(), 0);
            Revalidation pass = Proven(store, snapshot, update); store.Retain(pass, update);
            JObject page = store.Page(pass, update, 0, Sample());
            ((JObject)((JArray)page["items"]!)[0])["n"] = 99;
            Equal(1, (int)((JArray)store.Page(pass, update, 0, Sample())["items"]!)[0]["n"]!);
        });
        Test("any ineligible member expires the whole snapshot without row diagnosis", () =>
        {
            var store = new SnapshotStore(); var source = new Source(); object update = new object();
            EligibleSnapshot snapshot = Make(store, source, 6);
            Revalidation pass = Proven(store, snapshot, update); store.Retain(pass, update);
            string next = (string)store.Page(pass, update, 0, Sample())["next"]!;
            source.Hidden.Add(5);
            object later = new object();
            Revalidation again = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Expired, store.Revalidate(again, later, new Work(100)));
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, next, out _, out _));
            Equal(0, store.Count); Equal(0, store.CursorCount);
            Expired(() => store.Page(again, later, 0, Sample()));
        });
        Test("structural churn after build start expires snapshot and cursors", () =>
        {
            var store = new SnapshotStore(); var source = new Source(); object update = new object();
            EligibleSnapshot snapshot = Make(store, source, 4);
            Revalidation pass = Proven(store, snapshot, update); store.Retain(pass, update);
            string next = (string)store.Page(pass, update, 0, Sample())["next"]!;
            source.Structure++; // e.g. lister add/remove, even with identical membership later
            Equal(Revalidated.Expired, store.Revalidate(store.BeginRevalidation(snapshot), new object(), new Work(100)));
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, next, out _, out _));
            var gone = new Source { StructureAvailable = false };
            Equal(Revalidated.Expired, store.Revalidate(store.BeginRevalidation(Make(store, gone, 1)), new object(), new Work(10)));
        });
        Test("structure change during the final slice still expires", () =>
        {
            var store = new SnapshotStore(); var source = new Source();
            source.OnVisit = n => { if (n == 2) source.Structure++; };
            Equal(Revalidated.Expired, store.Revalidate(store.BeginRevalidation(Make(store, source, 3)), new object(), new Work(100)));
        });
        Test("without eligibility witness the whole pass must fit one update", () =>
        {
            var store = new SnapshotStore(); var source = new Source();
            EligibleSnapshot snapshot = Make(store, source, 6);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(3)));
            // Early member becomes hidden between slices: the restart rechecks it.
            source.Hidden.Add(0);
            Equal(Revalidated.Expired, store.Revalidate(pass, new object(), new Work(100)));
            // Split passes never accumulate across updates into a Valid result.
            var other = new Source(); EligibleSnapshot big = Make(store, other, 6);
            Revalidation split = store.BeginRevalidation(big);
            for (int i = 0; i < 5; i++) Equal(Revalidated.Continue, store.Revalidate(split, new object(), new Work(4)));
            Equal(Revalidated.Valid, store.Revalidate(split, new object(), new Work(12)));
        });
        Test("eligibility witness carries slices across updates; any change restarts", () =>
        {
            var store = new SnapshotStore(); var source = new Source { HasEligibility = true };
            EligibleSnapshot snapshot = Make(store, source, 6);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(4)));
            int before = source.Calls; object update = new object();
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new Work(100)));
            Equal(before + 2, source.Calls); // resumed at member 4, not restarted
            // Early member hidden between slices with the revision advanced: restart sees it.
            Revalidation again = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(again, new object(), new Work(4)));
            source.Hidden.Add(0); source.Eligibility++;
            Equal(Revalidated.Expired, store.Revalidate(again, new object(), new Work(100)));
            // Revision moving during the last slice cannot yield Valid.
            var churn = new Source { HasEligibility = true };
            churn.OnVisit = n => { if (n == 1) churn.Eligibility++; };
            Revalidation moving = store.BeginRevalidation(Make(store, churn, 2));
            Equal(Revalidated.Continue, store.Revalidate(moving, new object(), new Work(100)));
            churn.OnVisit = null;
            object stable = new object();
            Equal(Revalidated.Valid, store.Revalidate(moving, stable, new Work(100)));
            // Witness disappearing mid-pass restarts under the single-update rule.
            var lost = new Source { HasEligibility = true };
            Revalidation lose = store.BeginRevalidation(Make(store, lost, 4));
            Equal(Revalidated.Continue, store.Revalidate(lose, new object(), new Work(3)));
            lost.HasEligibility = false; lost.Hidden.Add(0);
            Equal(Revalidated.Expired, store.Revalidate(lose, new object(), new Work(100)));
        });
        Test("gate deferral and throwing gates", () =>
        {
            var store = new SnapshotStore(); var source = new Source();
            Revalidation pass = store.BeginRevalidation(Make(store, source, 2));
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(0)));
            source.OnVisit = n => throw new InvalidOperationException("gate");
            Equal(Revalidated.Expired, store.Revalidate(pass, new object(), new Work(10)));
        });
        Test("five-second lifetime, oldest eviction and four-snapshot cap", () =>
        {
            var clock = new Clock(); var store = new SnapshotStore(() => clock.Now, 5000);
            long second = System.Diagnostics.Stopwatch.Frequency;
            var passes = new List<Revalidation>(); var nexts = new List<string>();
            for (int i = 0; i < WireLimits.Snapshots + 1; i++)
            {
                clock.Now += 1; object update = new object();
                Revalidation pass = Proven(store, Make(store, new Source(), 3), update);
                True(store.Retain(pass, update));
                passes.Add(pass); nexts.Add((string)store.Page(pass, update, 0, Sample())["next"]!);
            }
            Equal(WireLimits.Snapshots, store.Count);
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, nexts[0], out _, out _));
            True(store.TryResolve(Scope, ReadMethod.Things, 2, nexts[1], out _, out _));
            clock.Now += 5 * second;
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, nexts[4], out _, out _));
            Equal(0, store.Count); Equal(0, store.CursorCount);
            // A build revalidated too late cannot be retained.
            object late = new object(); EligibleSnapshot old = Make(store, new Source(), 1);
            Revalidation stale = store.BeginRevalidation(old);
            Equal(Revalidated.Valid, store.Revalidate(stale, late, new Work(10)));
            clock.Now += 5 * second;
            True(!store.Retain(stale, late));
        });
        Test("expire drops retained snapshots, cursors and in-flight passes", () =>
        {
            var store = new SnapshotStore(); var source = new Source { HasEligibility = true }; object update = new object();
            EligibleSnapshot snapshot = Make(store, source, 4);
            Revalidation pass = Proven(store, snapshot, update); store.Retain(pass, update);
            string next = (string)store.Page(pass, update, 0, Sample())["next"]!;
            Revalidation inflight = store.BeginRevalidation(Make(store, source, 4));
            Equal(Revalidated.Continue, store.Revalidate(inflight, new object(), new Work(2)));
            store.Expire();
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, next, out _, out _));
            Equal(Revalidated.Expired, store.Revalidate(inflight, new object(), new Work(100)));
            Equal(Revalidated.Expired, store.Revalidate(store.BeginRevalidation(snapshot), new object(), new Work(100)));
            Expired(() => store.Page(pass, update, 0, Sample()));
        });
        Test("bounds are explicit", () =>
        {
            var store = new SnapshotStore(); var source = new Source();
            Invalid(() => store.Create(Scope, ReadMethod.Things, 0, new JObject[0], new object[0], 0, source, 0));
            Invalid(() => store.Create(Scope, ReadMethod.Things, WireLimits.PageItems + 1, new JObject[0], new object[0], 0, source, 0));
            Invalid(() => store.Create(Scope, ReadMethod.Things, 1, new JObject[1], new object[1], 0, source, 0));
            Invalid(() => store.Create(Scope, ReadMethod.Things, 1, new JObject[0], new object[1], 0, source, 0));
            Invalid(() => store.Create(Scope, ReadMethod.Things, 1, new JObject[0], new object[0], WireLimits.SnapshotBytes + 1, source, 0));
            var many = new JObject[WireLimits.SnapshotItems + 1]; var members = new object[many.Length];
            for (int i = 0; i < many.Length; i++) { many[i] = new JObject(); members[i] = i; }
            Invalid(() => store.Create(Scope, ReadMethod.Things, 1, many, members, 0, source, 0));
            // Largest snapshot at limit 1 needs exactly 2,048 boundary slots, allocated lazily.
            Array.Resize(ref many, WireLimits.SnapshotItems); Array.Resize(ref members, WireLimits.SnapshotItems);
            Equal(WireLimits.SnapshotItems, store.Create(Scope, ReadMethod.Things, 1, many, members, 0, source, 0).Cursors.Length);
        });
        Test("generation rotation and refused samples expire retained snapshots", () =>
        {
            var tracker = new GenerationTracker<object>(Ids.New("s"));
            var source = new Source(); object update = new object();
            var store = tracker.Snapshots;
            Revalidation pass = Proven(store, Make(store, source, 4), update); store.Retain(pass, update);
            string next = (string)store.Page(pass, update, 0, Sample())["next"]!;
            tracker.Clear();
            True(!store.TryResolve(Scope, ReadMethod.Things, 2, next, out _, out _));
        });
        Test("list witness latches any mutation, including identical final references", () =>
        {
            var list = new List<object> { 1, 2 }; var witness = new ListWitness<object>(list);
            True(witness.Unchanged(list)); True(witness.Unchanged(list));
            object first = list[0]; list.Remove(first); list.Insert(0, first);
            True(!witness.Unchanged(list)); True(!witness.Unchanged(list));
            witness = new ListWitness<object>(list); list[1] = list[1]; True(!witness.Unchanged(list));
            witness = new ListWitness<object>(list); True(!witness.Unchanged(new List<object>(list)));
            var empty = new List<object>(); witness = new ListWitness<object>(empty);
            empty.Add(1); empty.Clear(); True(!witness.Unchanged(empty));
        });
        Test("member witnesses: shared edits, changed or throwing witnesses advance revision", () =>
        {
            var counter = new EditCounter(); var state = new Dictionary<object, int>();
            var witnesses = new MemberWitnesses<int>(counter, (m, s) => m is string ? throw new Exception() : state[m] == s);
            True(!witnesses.TryRevision(out _)); counter.MarkInstalled();
            True(witnesses.TryRevision(out long a)); object one = 1; state[one] = 5; True(witnesses.Record(one, 5));
            True(witnesses.TryRevision(out long b)); Equal(a, b);
            counter.Bump(); True(witnesses.TryRevision(out long c)); True(c != b); Equal(0, witnesses.Count);
            witnesses.Record(one, 5); state[one] = 6; True(witnesses.TryRevision(out long d)); True(d != c);
            witnesses.Record("x", 0); True(witnesses.TryRevision(out long e)); True(e != d);
            True(witnesses.TryRevision(out long f)); Equal(e, f);
        });
        Test("member witness table is bounded; existing members still update", () =>
        {
            var counter = new EditCounter(); counter.MarkInstalled();
            var witnesses = new MemberWitnesses<int>(counter, (m, s) => true);
            var members = new object[WireLimits.SnapshotItems];
            for (int i = 0; i < members.Length; i++) True(witnesses.Record(members[i] = new object(), i));
            True(!witnesses.Record(new object(), 0)); True(witnesses.Record(members[0], 1));
        });
        Test("witnessed multi-update pass expires when an early member is hidden between slices", () =>
        {
            var store = new SnapshotStore(); var source = new WitnessedSource(); source.Counter.MarkInstalled();
            EligibleSnapshot snapshot = Make(store, source, 4);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(2)));
            source.Hidden.Add(0);
            Revalidated result = store.Revalidate(pass, new object(), new Work(10000));
            Equal(Revalidated.Expired, result); Equal(0, store.Count);
        });
        Test("witnessed multi-update pass publishes across updates without changes", () =>
        {
            var store = new SnapshotStore(); var source = new WitnessedSource(); source.Counter.MarkInstalled();
            EligibleSnapshot snapshot = Make(store, source, 4);
            Revalidation pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(2)));
            object update = new object();
            Equal(Revalidated.Valid, store.Revalidate(pass, update, new Work(2)));
            True(store.Retain(pass, update));
            // A shared (fog) edit between slices restarts the pass from the first member.
            pass = store.BeginRevalidation(snapshot);
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(2)));
            source.Counter.Bump(); int before = source.Calls;
            Equal(Revalidated.Continue, store.Revalidate(pass, new object(), new Work(2)));
            Equal(before + 2, source.Calls);
        });
        Console.WriteLine($"Snapshot store: {passed} passed, {failed} failed (host primitives, not game proof).");
        return failed;
    }
    private static void Expired(Action action)
    {
        try { action(); throw new Exception("published without proof"); }
        catch (WireException error) when (error.Code == WireError.CursorExpired) { }
    }
    private static void Invalid(Action action)
    {
        try { action(); throw new Exception("accepted invalid input"); }
        catch (ArgumentException) { }
    }
    private static void True(bool value) { if (!value) throw new Exception("assertion failed"); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"expected {expected}; got {actual}"); }
    private static void Test(string name, Action body)
    {
        try { body(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL snapshot store {name}: {error.Message}"); }
    }
}
