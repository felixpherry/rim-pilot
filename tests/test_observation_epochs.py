"""Boundary wiring guards only; host execution/source audit, never Mono/render proof."""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parent.parent


class ObservationEpochWiringTests(unittest.TestCase):
    def test_exact_finite_boundary_hooks_and_no_generic_dispatch(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        for target in (
            'Exact(typeof(Prefs), "set_DevMode", typeof(bool))',
            'Exact(typeof(Prefs), "set_UIScale", typeof(float))',
            'Exact(typeof(WindowStack), nameof(WindowStack.Add), typeof(Window))',
            'Exact(typeof(WindowStack), nameof(WindowStack.TryRemove), typeof(Window), typeof(bool))',
            'Exact(typeof(Root), nameof(Root.OnGUI), Type.EmptyTypes)',
        ):
            self.assertIn(target, source)
        self.assertIn('new[] { typeof(Root_Entry), typeof(Root_Play) }', source)
        self.assertNotIn('PatchAll', re.sub(r'//[^\n]*', '', source))
        self.assertNotIn('postfix:', source)
        self.assertNotIn('AfterRootUpdate', source)
        for code in (source, (ROOT / "mod/Bridge/ObservationEpochs.cs").read_text()):
            self.assertNotRegex(code, r'new (?:RenderEvidence|CompletedRenderStamp)\b')
            self.assertNotRegex(re.sub(r'//[^\n]*', '', code),
                                r'\b(?:SendInput|SendKeys|InputSimulator|GetFields|GetProperties|GetValue|SetValue)\b')

    def test_devmode_setter_uses_changed_value_bracket_not_unconditional_one(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        targets = source.split("private static MethodInfo[] ObservationBoundaryTargets()", 1)[1].split("};", 1)[0]
        self.assertNotIn("set_DevMode", targets)
        self.assertIn('Exact(typeof(Prefs), "set_UIScale", typeof(float))', targets)
        patch = source.split('harmony.Patch(Exact(typeof(Prefs), "set_DevMode", typeof(bool)),', 1)[1].split(");", 1)[0]
        self.assertIn("prefix: Hook(nameof(BeginDevModeWrite), Priority.First)", patch)
        self.assertIn("finalizer: Hook(nameof(EndDevModeWrite), Priority.Last)", patch)
        self.assertIn("SettingWriteBracket.Begin(transitions.Observations, value, Prefs.DevMode)", source)
        self.assertIn("SettingWriteBracket.End(transitions.Observations, ref __state, Prefs.DevMode, __exception != null)", source)
        epochs = (ROOT / "mod/Bridge/ObservationEpochs.cs").read_text()
        self.assertIn("default: epochs.Invalidate(); return;", epochs)
        self.assertIn("if (failed || after != (entered == SettingWrite.UnchangedOn)) { epochs.BeginBoundary(); epochs.EndBoundary(); }", epochs)

    def test_gui_sampling_never_multiplies_dispatch_allowance(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        gui = source.split('private static void AfterRootGui(', 1)[1].split(
            'private static DeveloperSafety SampleDeveloperSafety(', 1)[0]
        self.assertIn('dispatcher.SampleBoundary(', gui)
        self.assertNotIn('dispatcher.Pump(', gui)
        dispatcher = (ROOT / "mod/Bridge/ReadDispatcher.cs").read_text()
        self.assertIn('SampleBoundary(Func<UpdateBudget, ReadFrame<TMap>> sample) => Pump(sample, false)', dispatcher)
        self.assertIn('var budget = UpdateBudget.Boundary(now);', dispatcher)
        self.assertIn('budget = new UpdateBudget(now, SpentTicks + (ExtraWorkTicks?.Invoke() ?? 0));', dispatcher)
        self.assertIn('while (processRequests && budget.Available', dispatcher)
        self.assertIn('new UpdateAllowance(budget)', dispatcher)
        self.assertNotIn('ReserveBoundaryTime', dispatcher)

    def test_installed_adapter_registers_only_audited_routes(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        routes = source.split("private static ReadDispatcher<Map> CreateDispatcher(", 1)[1].split("\n        }\n", 1)[0]
        self.assertEqual(routes.count("routes.Add("), 8)
        self.assertIn("routes.Add((ReadMethod.Events, publicEvents.Producer))", routes)
        self.assertIn("transitions.Observations.Register(publicEvents)", routes)
        self.assertIn("routes.Add((ReadMethod.Ui, new GameUiProducer(access, reader, labels, patches)))", routes)
        self.assertIn("routes.Add((ReadMethod.Things, new GameThingsProducer(access, reader, labels, patches, false)))", routes)
        self.assertIn("routes.Add((ReadMethod.Resources, new GameThingsProducer(access, reader, labels, patches, true)))", routes)
        self.assertIn("routes.Add((ReadMethod.Summary, new GameSummaryProducer(access", routes)
        self.assertIn("routes.Add((ReadMethod.Pawns, list)); routes.Add((ReadMethod.Pawn, detail));", routes)
        dispatcher = (ROOT / "mod/Bridge/ReadDispatcher.cs").read_text()
        self.assertIn("route.Method <= ReadMethod.Capabilities", dispatcher)
        self.assertIn("Active.Invalidate(WireError.NotReady);", dispatcher)
        self.assertNotIn("ScanBudget", (ROOT / "shared/Protocol/Responses.cs").read_text())

    def test_developer_sampling_precedes_readiness_without_loading_collection_access(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text().split(
            'private static ReadFrame<Map> Sample(', 1)[1]
        safety = source.index('DeveloperSafety safety = SampleDeveloperSafety(budget);')
        readiness = source.index('Readiness.Check(')
        maps = source.index('game.Maps')
        self.assertLess(safety, readiness)
        self.assertLess(readiness, maps)
        self.assertIn('new FrameGeometry(Time.frameCount, Screen.width, Screen.height, Prefs.UIScale)', source)


if __name__ == "__main__":
    unittest.main()
