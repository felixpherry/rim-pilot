from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class NativeRenderWiringTests(unittest.TestCase):
    def test_native_completion_is_more_than_a_marker(self):
        source = (ROOT / "mod/Observation/GameNativeRender.cs").read_text()
        for witness in ("Camera.onPreCull += BeforeCull", "Camera.onPreRender += BeforeCamera",
                        "Camera.onPostRender += AfterCamera", "WaitForEndOfFrame",
                        "SystemInfo.supportsGraphicsFence", "GraphicsFenceType.CPUSynchronisation",
                        "SynchronisationStageFlags.PixelProcessing", "fence.passed",
                        "ticket?.Close()", "Application.logMessageReceivedThreaded += OnLog"):
            self.assertIn(witness, source)
        self.assertIn("camera.clearFlags == CameraClearFlags.Color", source)
        self.assertNotIn("CameraClearFlags.Skybox", source)
        self.assertNotIn("CaptureScreenshot", source)
        self.assertNotIn("ReadPixels", source)
        self.assertNotIn("EncodeToPNG", source)
        self.assertNotIn("Thread.Sleep", source)

    def test_root_boundaries_and_shared_work_accounting(self):
        source = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        for witness in ("NativeRender?.BeginFrame()", "NativeRender?.BeginGui()",
                        "NativeRender?.EndUpdate(__exception == null && __runOriginal)",
                        "NativeRender?.EndGui(__exception == null && __runOriginal",
                        "dispatcher?.BeginUpdateAllowance()",
                        'Exact(typeof(Game), "set_CurrentMap", typeof(Map))'):
            self.assertIn(witness, source)
        self.assertIn("routes.Add((ReadMethod.Screenshot, new ScreenshotProducer<Map>(screenshots)))", source)
        self.assertIn("Time.frameCount <= request!.AfterFrame", (ROOT / "mod/Observation/GameNativeRender.cs").read_text())
        dispatcher = (ROOT / "mod/Bridge/ReadDispatcher.cs").read_text()
        self.assertIn("SpentTicks + (ExtraWorkTicks?.Invoke() ?? 0)", dispatcher)
        self.assertIn("elapsed - sampleTicks", dispatcher)
        self.assertIn("UpdateBudget.Boundary(now)", dispatcher)
        self.assertIn("UpdateBudget.Boundary(() => Stopwatch.GetTimestamp())", (ROOT / "mod/Observation/GameNativeRender.cs").read_text())
        self.assertNotIn("(NativeRender?.SpentTicks ?? 0)", source)

    def test_map_view_proof_is_trusted_scene_sample_not_clear_claim(self):
        bootstrap = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        self.assertIn("game != null && game.CurrentMap != null && RimWorld.Planet.WorldRendererUtility.DrawingMap", bootstrap)
        source = (ROOT / "mod/Bridge/RenderProvenance.cs").read_text()
        self.assertIn("complete && cleared, complete && frame.MapView", source)
        self.assertEqual(2, source.count("frame.MapView != geometry.MapView"))
        self.assertIn("frame.MapView == mapView", source)
        self.assertIn("ok && held.Evidence.WholeFrameSafe", source)
        self.assertIn("ok && held.Evidence.MapViewSafe", source)

    def test_hard_epoch_and_same_frame_release(self):
        source = (ROOT / "mod/Bridge/RenderProvenance.cs").read_text()
        for witness in ("IObservationBoundarySink", "epoch == long.MaxValue", "epoch++",
                        "frame.Frame != releaseFrame", "nativeFencePassed", "held.SameSurface(frame)", "frame.Scale != geometry.Scale"):
            self.assertIn(witness, source)
        self.assertIn("RenderProvenanceTests.Run()", (ROOT / "tests/JsonIngress/Program.cs").read_text())
