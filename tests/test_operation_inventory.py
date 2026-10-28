"""Asset-free inventory guards, not product-route or actual-game acceptance tests.

Markdown is the single inventory source. These checks allow later evidence-backed
promotion; they reject empty/premature claims, not assert that a game route works.
"""
import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parent.parent
INVENTORY = ROOT / "tests/coverage/operations.md"
TEXT = INVENTORY.read_text()
METHODS = set(json.loads((ROOT / "protocol/wire-v1.schema.json").read_text())[
    "$defs"]["request"]["properties"]["method"]["enum"])
AREAS = {
    "Menus and setup": "MENU",
    "Colony management": "COLONY",
    "Pawns and combat": "PAWN",
    "World and progression": "WORLD",
    "Generic UI": "UI",
    "Royalty": "ROYALTY",
    "Ideology": "IDEOLOGY",
    "Biotech": "BIOTECH",
    "Anomaly": "ANOMALY",
    "Odyssey": "ODYSSEY",
}
LINK = re.compile(r"\[[^\]]+\]\(([^)]+)\)")


def rows(text):
    """Reject malformed operation rows instead of silently omitting them."""
    area = None
    result = []
    for line in text.splitlines():
        if line.startswith("## "):
            area = line[3:]
        if not line.startswith("|"):
            continue
        cells = [cell.strip() for cell in line.strip("|").split("|")]
        if cells[0] == "ID" or all(re.fullmatch(r"-+", cell) for cell in cells):
            continue
        if len(cells) != 6 or area not in AREAS:
            raise ValueError("malformed inventory row")
        result.append((area, *cells))
    return result


def valid_relative_link(target):
    """No private absolute paths, escapes, network links or broken targets."""
    if ":" in target or "#" in target or "?" in target or "\\" in target:
        return False
    path = Path(target)
    if path.is_absolute():
        return False
    resolved = (INVENTORY.parent / path).resolve()
    return resolved.is_relative_to(ROOT) and resolved.is_file()


def validate(text):
    parsed = rows(text)
    seen = set()
    covered = set()
    for area, ident, operation, plan, state, evidence, proof in parsed:
        if not re.fullmatch(AREAS[area] + r"-[0-9]{2}", ident) or ident in seen:
            raise ValueError("invalid or duplicate stable ID")
        seen.add(ident)
        covered.add(area)
        if not operation or not proof:
            raise ValueError("operation and proof obligation required")
        if plan != "screenshot-only":
            if not plan.startswith("baseline: ") or plan[10:] not in METHODS:
                raise ValueError("unknown observation plan")
            if plan[10:] in {"auth", "capabilities", "screenshot"}:
                raise ValueError("not an operation baseline")
        if state not in {"implemented", "unverified"}:
            raise ValueError("route state must distinguish evidence from observation plan")
        if state == "implemented":
            links = LINK.findall(evidence)
            if "pending" in evidence or not links or not all(map(valid_relative_link, links)):
                raise ValueError("implemented observation needs public-safe evidence links")
        elif evidence != "pending":
            links = LINK.findall(evidence)
            if not links or not all(map(valid_relative_link, links)):
                raise ValueError("partial/unverified evidence needs public-safe links")
    if covered != set(AREAS):
        raise ValueError("missing required area or DLC")
    return parsed


class OperationInventoryTests(unittest.TestCase):
    def test_inventory_rows_have_unique_ids_explicit_status_and_proof(self):
        self.assertTrue(validate(TEXT))

    def test_each_milestone_operation_family_has_named_entry(self):
        operations = {}
        for area, _, operation, *_ in rows(TEXT):
            operations[area] = operations.get(area, "") + " " + operation.lower()
        required = {
            "Menus and setup": ["scenario", "storyteller", "difficulty", "generation", "landing",
                                "colonist", "options", "load", "save", "quit"],
            "Colony management": ["build", "zones", "stockpiles", "work", "schedule", "policies",
                                  "research", "bills", "power", "food", "animals"],
            "Pawns and combat": ["inspection", "health", "medical", "gear", "draft", "movement",
                                 "attack", "abilities", "target"],
            "World and progression": ["maps", "caravans", "trade", "factions", "quests", "letters",
                                      "settlement", "endgame"],
            "Generic UI": ["tabs", "popups", "tooltips", "scroll", "unicode", "drag", "camera",
                           "zoom", "without shared widget metadata"],
            "Royalty": ["title", "psycast", "setup", "target", "endgame"],
            "Ideology": ["setup", "roles", "ritual", "target", "archonexus"],
            "Biotech": ["setup", "children", "gene", "mech", "target"],
            "Anomaly": ["setup", "entity", "containment", "target", "endgame"],
            "Odyssey": ["setup", "gravship", "travel", "target", "progression"],
        }
        for area, fragments in required.items():
            for fragment in fragments:
                with self.subTest(area=area, operation=fragment):
                    self.assertIn(fragment, operations[area])

    def test_all_links_resolve_inside_repository(self):
        targets = LINK.findall(TEXT)
        self.assertTrue(targets)
        for target in targets:
            with self.subTest(target=target):
                self.assertTrue(valid_relative_link(target))

    def test_screenshot_and_baseline_plans_remain_separate_from_runtime_state(self):
        parsed = validate(TEXT)
        for area in AREAS:
            self.assertTrue(any(row[0] == area and row[3] == "screenshot-only" for row in parsed))
        candidates = {row[3][10:] for row in parsed if row[3].startswith("baseline: ")}
        self.assertEqual(candidates, {"summary", "health", "pawns.list", "pawn.get", "things.list",
                                      "resources", "ui", "events.read"})
        for phrase in ["No operation row has that game evidence yet", "Zero game-proven operation rows",
                       "zero game-proven product screenshots", "no control route", "M1 cannot navigate",
                       "not the final exhaustive M4 inventory", "Co-play is deferred to M3"]:
            self.assertIn(phrase, TEXT)

    def test_reject_missing_area_duplicate_id_bad_method_and_premature_pass(self):
        first = next(line for line in TEXT.splitlines() if line.startswith("| MENU-01 |"))
        mutations = [
            "\n".join(line for line in TEXT.splitlines() if not line.startswith("| ODYSSEY-")),
            TEXT.replace(first, first + "\n" + first),
            TEXT.replace("baseline: things.list", "baseline: input.click"),
            TEXT.replace(first, first.replace("| unverified |", "| implemented |")),
            TEXT.replace(first, first.replace("| pending |", "| passed |")),
            TEXT.replace(first, first.replace("| pending |", "| [proof](/private/proof.md) |")),
            TEXT.replace(first, first.replace("| unverified |", "| screenshot-only |")),
            TEXT.replace(first, first.rsplit("|", 2)[0] + " |"),
        ]
        for mutation in mutations:
            with self.subTest(mutation=mutations.index(mutation)):
                with self.assertRaises(ValueError):
                    validate(mutation)

    def test_link_guard_rejects_private_external_missing_and_escaping_paths(self):
        for target in ["/tmp/proof.png", "https://example.invalid/proof", "../../../../proof.md",
                       "missing.md", "../../docs/build.md#not-a-proof", "C:\\private\\proof.md"]:
            with self.subTest(target=target):
                self.assertFalse(valid_relative_link(target))
        self.assertTrue(valid_relative_link("../../docs/build.md"))


if __name__ == "__main__":
    unittest.main()
