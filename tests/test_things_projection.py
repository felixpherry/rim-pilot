"""Source wiring guards supplement host primitives; no game/Mono visibility proof."""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class ThingsProjectionTests(unittest.TestCase):
    def test_both_routes_share_complete_scan_gates_and_snapshot_validation(self):
        source = (ROOT / "mod/Observation/GameThings.cs").read_text()
        self.assertIn("public bool SnapshotBuild => true", source)
        self.assertIn("new GameListSource<Thing>", source)
        self.assertIn("new ConsistentSnapshotSource(list, owner.Fresh)", source)
        self.assertIn("list.Gate(thing, work, out bool witnessed)", source)
        self.assertLess(source.index("work.TryCandidate()"), source.index("Thing thing = list.List[index]"))
        self.assertIn("if (gate == TargetGateResult.Deferred) return false", source)
        self.assertIn("if (!witnessed) return work.Fail(WireError.Unavailable)", source)
        self.assertIn("Snapshots.Create(scope!, Method", source)
        self.assertIn("Snapshots.Revalidate(pass!, work.Frame!, work)", source)
        self.assertIn("store.Page(pass, frame, start, sample)", source)
        self.assertIn("if (!work.TryNestedFields(totals.Count + 1)) return false", source)
        self.assertNotIn("GetInnerIfMinified", source)
        self.assertNotIn("UpdateResourceCounts(", source)
        self.assertNotIn("GetCount(", source)
        self.assertNotIn("MapHeld", source)

    def test_oversized_raw_source_refuses_before_any_scan_not_truncated_counts(self):
        source = (ROOT / "mod/Observation/GameThings.cs").read_text()
        cap = "if (raw == null || raw.Count > WireLimits.SnapshotItems) return work.Fail(WireError.Unavailable)"
        self.assertIn(cap, source)
        for operation in ("new GameListSource<Thing>", "while (index < list.List.Count)", "list.Gate(thing, work"):
            self.assertLess(source.index(cap), source.index(operation))
        self.assertNotIn(".Take(", source)
        self.assertNotIn("Math.Min(raw.Count", source)
        self.assertIn("internal const int SnapshotItems = 2048;", (ROOT / "shared/Protocol/Wire.cs").read_text())

    def test_only_post_gate_public_resource_kinds_aggregate(self):
        source = (ROOT / "mod/Observation/GameThings.cs").read_text()
        self.assertIn("def.GetType() != typeof(ThingDef)", source)
        self.assertIn("def.thingClass != thing.GetType()", source)
        self.assertIn("owner.patches.IsUnpatched(owner.resourceGetter)", source)
        self.assertIn("def.resourceReadoutPriority != ResourceCountPriority.Uncounted", source)
        self.assertIn("def.category != ThingCategory.Item", source)
        self.assertIn("totals.Add(def, resourceLabel, thing.stackCount)", source)
        self.assertLess(source.index("work.TryRetain(size)"), source.index("totals.Add("))
        witness = (ROOT / "mod/Observation/GameTargetAccess.cs").read_text()
        self.assertIn("StackCount = thing.stackCount", witness)
        self.assertIn("thing.stackCount != witness.StackCount", witness)
        pure = (ROOT / "mod/Observation/ThingProjection.cs").read_text()
        self.assertIn("eligible_observed_direct_spawned_stack_units", pure)
        self.assertIn("IdentityComparer.Instance", pure)
        self.assertIn("revision != initial", pure)
        self.assertIn("version = changed ? 1 : 0", pure)


if __name__ == "__main__":
    unittest.main()
