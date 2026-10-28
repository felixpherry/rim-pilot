from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class ScreenshotCaptureWiringTests(unittest.TestCase):
    def test_owned_texture_and_no_render_or_ui_mutation(self):
        source = (ROOT / "mod/Observation/GameScreenshot.cs").read_text()
        for witness in ("TextureFormat.RGB24, false", "texture.ReadPixels(", "0, 0, false)",
                        "texture.GetRawTextureData()", "raw.Length != size.RawBytes", "finally",
                        "UnityEngine.Object.DestroyImmediate(texture)", "RenderTexture.active != null"):
            self.assertIn(witness, source)
        for forbidden in (".Apply(", "EncodeToPNG", "CaptureScreenshot", "RenderTexture.active =",
                          "Camera.Render", "Screen.SetResolution", "File.", "Directory.", "Input."):
            self.assertNotIn(forbidden, source)

    def test_readback_then_hold_then_later_fenced_release(self):
        source = (ROOT / "mod/Observation/ScreenshotCapture.cs").read_text()
        consume = source.split("private void Consume(", 1)[1]
        steps = ("fence.TryRead()", "new CaptureSize(", "backend.ReadRgb(captured)",
                 "ticket.Hold()", "BoundedCapturePng.Encode(", "ticket.FencePassed",
                 "ticket.Release(flight.Held!)", "Responses.Capture(",
                 "ticket.CheckRelease(completed)", "flight.Complete(result)", "fence.ReleaseRead()")
        offsets = [consume.index(step) for step in steps]
        self.assertEqual(offsets, sorted(offsets))
        render = (ROOT / "mod/Observation/GameNativeRender.cs").read_text()
        # Bytes are never released on the fence alone; the fence is created after readback in Hold.
        self.assertIn("provenance.Release(wanted, held,", render)
        self.assertIn("Read(); spent = 0;", render)
        self.assertIn("!provenance.Active", render)
        self.assertIn("Graphics.CreateGraphicsFence", render.split("internal HeldRender Hold(", 1)[1])
        self.assertNotIn("Graphics.CreateGraphicsFence", render.split("private IEnumerator EndFrames", 1)[1].split("internal bool FencePassed", 1)[0])
        for witness in ("WireLimits.CaptureIntervalMs", "StageLimitMs = 100", "FlightLimitMs = 2000", "MaxRearms = 2", "!ticket.Witnessed", "Sweep()", "flight.Discard()",
                        "SpentTicks +=", "ReferenceEquals(active, flight)", "read.IsLive",
                        "StepResult.Continue", "owner.Cancel(flight)"):
            self.assertIn(witness, source)
        wiring = (ROOT / "mod/Bridge/ModBootstrap.cs").read_text()
        self.assertIn("screenshots?.BeginUpdate()", wiring)
        self.assertIn("(screenshots?.SpentTicks ?? 0)", wiring)
        self.assertIn("ScreenshotCaptureTests.Run()", (ROOT / "tests/JsonIngress/Program.cs").read_text())

    def test_exact_output_preflight_not_post_native_encoder_cap(self):
        source = (ROOT / "mod/Observation/BoundedCapturePng.cs").read_text()
        self.assertIn("png > WireLimits.CapturePngBytes", source)
        self.assertIn("pixels > WireLimits.CapturePixels", source)
        self.assertIn("new byte[size.PngBytes]", source)
        self.assertIn("if (!live())", source)
        self.assertNotIn("MemoryStream", source)
        self.assertNotIn("EncodeToPNG", source)
