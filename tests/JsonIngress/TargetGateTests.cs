using System;
using System.Collections.Generic;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class TargetGateTests
{
    private static int passed, failed;
    // Fogged cells by index; Fogged must never be asked about an index outside the grid.
    private struct Fog : IFogCells
    {
        internal HashSet<int> Clear;
        public int Length { get; set; }
        public bool Fogged(int index)
        {
            if (index < 0 || index >= Length) throw new Exception("unchecked fog index");
            return !Clear.Contains(index);
        }
    }
    private static Fog Grid(int length, params int[] clear) => new Fog { Length = length, Clear = new HashSet<int>(clear) };
    // comps[h] = null hediff, else per-comp "may hide" flags.
    private struct HediffList : IPawnConcealment
    {
        internal bool[]?[]? List;
        public int Hediffs => List == null ? -1 : List.Length;
        public int Comps(int hediff) => List![hediff] == null ? -1 : List[hediff]!.Length;
        public bool MayHide(int hediff, int comp) => List![hediff]![comp];
    }
    private sealed class Work : INestedWork
    {
        internal int Left = int.MaxValue, Charged;
        public bool TryNestedFields(int count)
        {
            if (count < 1) throw new Exception("empty charge");
            if (count > Left) return false;
            Left -= count; Charged += count; return true;
        }
    }
    private static TargetGateResult Pawn(bool known, bool own, Work work, params bool[]?[]? hediffs) =>
        TargetGate.Pawn(known, own, new HediffList { List = hediffs }, work);
    private class Target { public override bool Equals(object? other) => true; public override int GetHashCode() => 0; }
    private sealed class DerivedTarget : Target { }

    internal static int Run()
    {
        Test("map gate requires matching geometry and created fog", () =>
        {
            Equal(TargetGateResult.Eligible, TargetGate.Map(10, 20, 10, 20, 200, false, false));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(10, 20, 10, 20, -1, false, false));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(10, 20, 10, 20, 199, false, false));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(10, 20, 20, 10, 200, false, false));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(10, 20, 10, 20, 200, true, false));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(10, 20, 10, 20, 200, false, true));
            Equal(TargetGateResult.Unavailable, TargetGate.Map(0, 0, 0, 0, 0, false, false));
        });
        Test("direct spawn requires exact non-null map container, not null holder", () =>
        {
            var mapContainer = new Target();
            Equal(true, TargetGate.DirectSpawnHolder(mapContainer, mapContainer));
            Equal(false, TargetGate.DirectSpawnHolder(mapContainer, null));
            Equal(false, TargetGate.DirectSpawnHolder(null, null));
            Equal(false, TargetGate.DirectSpawnHolder(null, mapContainer));
            // Even overloaded-equal containers on another map/holder cannot authorize.
            Equal(false, TargetGate.DirectSpawnHolder(mapContainer, new Target()));
            object replacement = new object();
            Equal(false, TargetGate.DirectSpawnHolder(replacement, mapContainer));
            Equal(true, TargetGate.DirectSpawnHolder(replacement, replacement));
        });
        Test("footprint ceiling is explicit and charged before checking", () =>
        {
            Equal(1, TargetGate.FootprintCells(1, 1)); Equal(256, TargetGate.FootprintCells(16, 16));
            Equal(-1, TargetGate.FootprintCells(17, 16)); Equal(-1, TargetGate.FootprintCells(0, 1));
            Equal(-1, TargetGate.FootprintCells(257, 1));
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(100 * 100), 100, 100, 0, 0, 17, 16));
        });
        Test("one-cell target needs its own in-bounds cell unfogged", () =>
        {
            Equal(TargetGateResult.Eligible, TargetGate.Footprint(Grid(100, 23), 10, 10, 3, 2, 1, 1));
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(100, 24), 10, 10, 3, 2, 1, 1));
            // Installed IsFogged(c) returns false out of bounds; that is not visibility evidence.
            foreach (var (x, z) in new[] { (-1, 0), (0, -1), (10, 0), (0, 10), (int.MaxValue, 0), (int.MinValue, int.MinValue) })
                Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(100), 10, 10, x, z, 1, 1));
        });
        Test("multi-cell target eligible through any non-origin unfogged cell only", () =>
        {
            Equal(TargetGateResult.Eligible, TargetGate.Footprint(Grid(100, 2 * 10 + 4), 10, 10, 2, 1, 3, 2));
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(100, 0, 99), 10, 10, 2, 1, 3, 2));
            // Out-of-bounds part of a footprint is skipped, never counted as unfogged.
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(100), 10, 10, 8, 8, 4, 4));
            Equal(TargetGateResult.Eligible, TargetGate.Footprint(Grid(100, 99), 10, 10, 8, 8, 4, 4));
        });
        Test("uninitialized or mismatched fog never authorizes", () =>
        {
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(-1), 10, 10, 0, 0, 1, 1));
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(99, 0), 10, 10, 0, 0, 1, 1));
            Equal(TargetGateResult.Unavailable, TargetGate.Footprint(Grid(0), 0, 0, 0, 0, 1, 1));
        });
        Test("own invisible pawns stay inspectable without scanning concealment", () =>
        {
            var work = new Work();
            Equal(TargetGateResult.Eligible, Pawn(true, true, work, new[] { true }));
            Equal(TargetGateResult.Eligible, Pawn(true, true, work, null));
            Equal(0, work.Charged);
            Equal(TargetGateResult.Unavailable, Pawn(false, true, work, new bool[0]));
            Equal(TargetGateResult.Unavailable, Pawn(false, false, work, new bool[0]));
        });
        Test("visible non-player pawns pass; any hide-capable invisibility component conceals", () =>
        {
            var work = new Work();
            Equal(TargetGateResult.Eligible, Pawn(true, false, work));
            Equal(TargetGateResult.Eligible, Pawn(true, false, work, new bool[0], new[] { false, false }));
            Equal(4, work.Charged); // two hediffs + two components
            // Later hediff, later component: whole list is examined, not just the first match.
            Equal(TargetGateResult.Unavailable, Pawn(true, false, work, new[] { false }, new bool[0], new[] { false, true }));
            Equal(TargetGateResult.Unavailable, Pawn(true, false, work, (bool[]?)null));
            Equal(TargetGateResult.Unavailable, TargetGate.Pawn(true, false, new HediffList(), work));
        });
        Test("concealment scan is bounded and charged before reading", () =>
        {
            Equal(TargetGateResult.Unavailable, Pawn(true, false, new Work(), new bool[TargetGate.MaxHediffs + 1][]));
            Equal(TargetGateResult.Unavailable, Pawn(true, false, new Work(), new bool[TargetGate.MaxHediffComps + 1]));
            var full = new bool[TargetGate.MaxHediffs][];
            for (int i = 0; i < full.Length; i++) full[i] = new bool[0];
            Equal(TargetGateResult.Eligible, Pawn(true, false, new Work(), full));
            // Budget exhaustion defers (retry), never reads unpaid entries or reports ineligible.
            Equal(TargetGateResult.Deferred, Pawn(true, false, new Work { Left = 1 }, new[] { true }, new bool[0]));
            Equal(TargetGateResult.Deferred, Pawn(true, false, new Work { Left = 1 }, new[] { true }));
        });
        Test("type admission is exact runtime type, never inheritance", () =>
        {
            var types = new TypeAdmission(typeof(Target));
            True(types.Admits(new Target())); True(!types.Admits(new DerivedTarget())); True(!types.Admits(null));
            Invalid(() => new TypeAdmission()); Invalid(() => new TypeAdmission(typeof(IFogCells)));
            Invalid(() => new TypeAdmission(typeof(List<>)));
        });
        Test("references stable by identity, kind- and owner-bound, overloaded Equals ignored", () =>
        {
            var table = new ReferenceTable(4); object map = new object(), other = new object();
            Target a = new Target(), b = new Target();
            string ra = table.Allocate("p", a, map), rb = table.Allocate("t", b, map);
            True(ra.StartsWith("p_", StringComparison.Ordinal) && rb.StartsWith("t_", StringComparison.Ordinal));
            Equal(ra, table.Allocate("p", a, map)); True(ra != rb);
            True(ReferenceEquals(a, table.Resolve("p", ra, map))); True(table.Resolve("t", ra, map) == null);
            True(table.Resolve("p", ra, other) == null); True(table.Resolve("p", Ids.New("p"), map) == null);
            True(table.Resolve("p", "not-an-id", map) == null);
        });
        Test("moved target gets a fresh reference and the old one stops resolving", () =>
        {
            var table = new ReferenceTable(4); object map = new object(), other = new object(), a = new object();
            string old = table.Allocate("p", a, map), moved = table.Allocate("p", a, other);
            True(old != moved); True(table.Resolve("p", old, map) == null); True(ReferenceEquals(a, table.Resolve("p", moved, other)));
            Equal(1, table.Count);
        });
        Test("bounded table evicts oldest allocation; evicted lookup uniform", () =>
        {
            var table = new ReferenceTable(2); object map = new object();
            object a = new object(), b = new object(), c = new object();
            string ra = table.Allocate("t", a, map), rb = table.Allocate("t", b, map), rc = table.Allocate("t", c, map);
            Equal(2, table.Count); True(table.Resolve("t", ra, map) == null);
            True(ReferenceEquals(b, table.Resolve("t", rb, map))); True(ReferenceEquals(c, table.Resolve("t", rc, map)));
            True(table.Allocate("t", a, map) != ra); Equal(2, table.Count);
            table.Clear(); Equal(0, table.Count); True(table.Resolve("t", rc, map) == null);
            Equal(WireLimits.Snapshots * WireLimits.SnapshotItems, new ReferenceTable().Capacity);
            Invalid(() => new ReferenceTable(0)); Invalid(() => new ReferenceTable(WireLimits.TrackedEntities + 1));
            Invalid(() => table.Allocate("m", a, map));
        });
        Console.WriteLine($"Target gates: {passed} passed, {failed} failed (host primitives, not game proof).");
        return failed;
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
        catch (Exception error) { failed++; Console.Error.WriteLine($"FAIL target gate {name}: {error.Message}"); }
    }
}
