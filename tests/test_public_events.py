"""Finite installed-source hook wiring guards; not executable Mono/game proof."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parent.parent


class PublicEventWiringTests(unittest.TestCase):
    def test_only_canonical_delivery_and_bounded_availability_hooks(self):
        source = (ROOT / "mod/Observation/GamePublicEvents.cs").read_text()
        for target in (
            'Exact(typeof(LetterStack), "ReceiveLetter", typeof(Letter), typeof(string), typeof(int), typeof(bool))',
            'Exact(typeof(Messages), "Message", typeof(Message), typeof(bool))',
            'Exact(typeof(Archive), "Add", typeof(IArchivable))',
            'Exact(typeof(Archive), "Remove", typeof(IArchivable))',
            'Exact(typeof(LetterStack), "RemoveLetter", typeof(Letter))',
            'Exact(typeof(Messages), "Update")',
            'Exact(typeof(Messages), "Clear")',
        ):
            self.assertIn(target, source)
        code = re.sub(r"//[^\n]*", "", source)
        for forbidden in ("AcceptsMessage", "CanShowInLetterStack", "debugInfo", "lookTargets", "ArchivablesListForReading", "OpenLetter", "ResetTimer", "Flash"):
            self.assertNotIn(forbidden, code)
        self.assertNotRegex(code, r"\.(?:Contains|Add|Remove|ReceiveLetter|Message|GetMouseoverText)\(")
        self.assertIn("state.Archived", source)
        self.assertIn("__exception == null", source)
        self.assertIn("depth != 1", source)
        self.assertIn("delayTicks > 0", source)
        self.assertIn("Scribe.mode != LoadSaveMode.Inactive", source)
        self.assertIn("msg.GetType() != typeof(Message)", source)
        self.assertIn("let.GetType() != typeof(StandardLetter)", source)

    def test_public_text_only_after_acceptance_and_fresh_safety(self):
        source = (ROOT / "mod/Observation/GamePublicEvents.cs").read_text()
        finish = source.split("private void Finish(", 1)[1].split("private static bool Present", 1)[0]
        self.assertLess(finish.index("!member"), finish.index("string? rawLabel = label()"))
        self.assertLess(finish.index("Scope? now = Safe(budget)"), finish.index("string? rawLabel = label()"))
        self.assertIn("ticket.Version != version", finish)
        self.assertIn("!ticket.Scope.Same(now)", finish)
        self.assertIn("PublicEventText.Sanitize(text(), 2048)", finish)
        self.assertIn('"liveMessages"', source)
        self.assertIn('"destroyed"', source)
        self.assertNotRegex(source, r"\.(?:GetValue|SetValue|GetFields|GetProperties)\(")

    def test_route_uses_shared_scheduler_budget_not_direct_transport(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        self.assertIn("routes.Add((ReadMethod.Events, publicEvents.Producer))", source)
        self.assertIn("result.ExtraWorkTicks = () => (publicEvents?.SpentTicks ?? 0) + (screenshots?.SpentTicks ?? 0)", source)
        self.assertIn("publicEvents?.BeginUpdate()", source)
        events = (ROOT / "mod/Observation/GamePublicEvents.cs").read_text()
        self.assertIn("new EventProducer<Map>(ring, Prepare)", events)
        self.assertNotIn("TryComplete", events)
        self.assertIn("work.TryNestedFields(280)", events)
        self.assertIn("spent += Math.Max(0, Monotonic.Now - start)", events)
        self.assertIn("messages.Count > 12", events)


if __name__ == "__main__":
    unittest.main()
