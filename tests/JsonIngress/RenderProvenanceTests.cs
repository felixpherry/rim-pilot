using System;
using System.Threading.Tasks;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

internal static class RenderProvenanceTests
{
    private static int passed, failed;
    private static bool mapView;
    internal static int Run()
    {
        RunMode(false); RunMode(true);
        Console.WriteLine($"Render provenance: {passed} passed, {failed} failed"); return failed;
    }
    private static void RunMode(bool map)
    {
        mapView = map;
        Test("strictly newer complete whole native frame", () => { var f = new Fixture(); f.Whole(); var stamp = f.Complete(); Equal(11L, stamp.Frame); f.Release(stamp); });
        Test("same request frame never completes", () => { var f = new Fixture(); f.Frame = new FrameGeometry(10, 800, 600, 1); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("no update marker", () => { var f = new Fixture(); f.Whole(false); Error(WireError.NotReady, () => f.Complete()); });
        Test("no repaint", () => { var f = new Fixture(); f.Whole(true, false); Error(WireError.NotReady, () => f.Complete()); });
        Test("no native fence completion", () => { var f = new Fixture(); f.Whole(); Error(WireError.NotReady, () => f.Complete(false)); });
        Test("non-map view still requires full backbuffer color replacement", () => { var f = new Fixture(false); f.Whole(true, true, false); Error(WireError.NotReady, () => f.Complete()); });
        Test("offscreen portrait then depth-only primary cannot supply a whole-color frame", () =>
        {
            var f = new Fixture(false); f.Begin(); f.P.Updated();
            // FullClear is false for a RenderTexture even with SolidColor, and for Depth.
            f.P.CameraBegin(1, false); f.P.CameraEnd(1, false);
            f.P.CameraBegin(2, false); f.P.CameraEnd(2, false); f.Gui();
            Error(WireError.NotReady, () => f.Hold());
        });
        Test("non-map color clear must remain valid after rendering", () => { var f = new Fixture(false); f.Begin(); f.P.Updated(); f.Gui(); f.P.CameraBegin(1, true); f.P.CameraEnd(1, false); Error(WireError.NotReady, () => f.Complete()); });
        Test("no camera completion cannot certify GUI alone", () => { var f = new Fixture(); f.Begin(); f.P.Updated(); f.Gui(); Error(WireError.NotReady, () => f.Complete()); });
        Test("size scale on off interval never resurrects request", () => { var f = new Fixture(); f.Whole(); f.P.Sample(f.Scope, new FrameGeometry(11, 800, 600, 2), true); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("camera pre without post", () => { var f = new Fixture(); f.Whole(); f.P.CameraBegin(2, false); Error(WireError.NotReady, () => f.Complete()); });
        Test("camera post without pre", () => { var f = new Fixture(); f.Whole(); f.P.CameraEnd(2); Error(WireError.NotReady, () => f.Complete()); });
        Test("repeated manual camera renders unsupported", () => { var f = new Fixture(); f.Whole(); f.P.CameraBegin(1, true); Error(WireError.NotReady, () => f.Complete()); });
        Test("nested camera renders unsupported", () => { var f = new Fixture(); f.Begin(); f.P.CameraBegin(1, true); f.P.CameraBegin(2, false); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("camera table bounded", () => { var f = new Fixture(); f.Begin(); for (int i = 0; i <= RenderProvenance.MaxCameras; i++) { f.P.CameraBegin(i, true); f.P.CameraEnd(i); } f.P.Updated(); f.Gui(); Error(WireError.NotReady, () => f.Complete()); });
        Test("GUI missing exit", () => { var f = new Fixture(); f.Whole(); f.P.GuiBegin(); Error(WireError.NotReady, () => f.Complete()); });
        Test("nested GUI unsupported", () => { var f = new Fixture(); f.Whole(); f.P.GuiBegin(); f.P.GuiBegin(); Error(WireError.NotReady, () => f.Complete()); });
        Test("ordinary boundaries revoke leases not render candidate", () => { var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P); f.Whole(); epochs.BeginBoundary(true); epochs.EndBoundary(true); f.Complete(); });
        foreach (ObservationInvalidation cause in new[] { ObservationInvalidation.Safety, ObservationInvalidation.Readiness, ObservationInvalidation.Source, ObservationInvalidation.All })
            Test("hard load root map developer exception revocation " + cause, () => { var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P); f.Whole(); epochs.Invalidate(cause); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("unsafe on off cannot revive old request", () => { var f = new Fixture(); f.Whole(); f.P.Sample(f.Scope, f.Frame, false); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("missing incomplete budget sample cannot revive old request", () => { var f = new Fixture(); f.Whole(); f.P.Sample(null, null, false); f.Whole(); Error(WireError.NotReady, () => f.Complete()); });
        Test("root game mismatch stale", () => { var f = new Fixture(); f.Whole(); f.Scope = new Scope(f.Scope.Session, Ids.New("r"), Ids.New("g")); Error(WireError.StaleScope, () => f.Complete()); });
        foreach (FrameGeometry changed in new[] { new FrameGeometry(11, 801, 600, 1, mapView), new FrameGeometry(11, 800, 601, 1, mapView), new FrameGeometry(11, 800, 600, 2, mapView), new FrameGeometry(12, 800, 600, 1, mapView), new FrameGeometry(9, 800, 600, 1, mapView) })
            Test("geometry scale frame mismatch " + changed.Frame + "/" + changed.Width + "/" + changed.Height + "/" + changed.Scale, () => { var f = new Fixture(); f.Whole(); f.Frame = changed; Error(WireError.NotReady, () => f.Complete()); });
        Test("developer refusal wins stale and loading", () => { var f = new Fixture(); f.Whole(); f.Scope = new Scope(f.Scope.Session, Ids.New("r"), null); Error(WireError.ObservationRefused, () => f.P.Complete(f.Request, f.Scope, f.Frame, WireError.ObservationRefused, true)); });
        Test("late release next frame rejected", () => { var f = new Fixture(); f.Whole(); var stamp = f.Complete(); f.Frame = new FrameGeometry(12, 800, 600, 1); Error(WireError.NotReady, () => f.Release(stamp)); });
        Test("worker revocation prevents release", () => { var f = new Fixture(); f.Whole(); var stamp = f.Complete(); Task.Run(() => f.P.Invalidate(ObservationInvalidation.All)).GetAwaiter().GetResult(); Error(WireError.NotReady, () => f.Release(stamp)); });
        Test("worker cannot create evidence", () => { var f = new Fixture(); f.Whole(); bool rejected = Task.Run(() => { try { f.Complete(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(); if (!rejected) throw new Exception("Worker admitted."); });
        Test("hold at capture frame, release at later frame after fence pass", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12); var stamp = f.Later(held, true);
            Equal(11L, stamp.Frame); f.Release(stamp);
        });
        Test("held render never releases without a passed fence", () => { var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12); Error(WireError.NotReady, () => f.Later(held, false)); });
        Test("hold itself requires the whole frame but not the fence", () => { var f = new Fixture(); f.Whole(false); Error(WireError.NotReady, () => f.Hold()); });
        Test("hard epoch change between hold and release discards", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.P.Invalidate(ObservationInvalidation.Safety); f.Next(12);
            Error(WireError.NotReady, () => f.Later(held, true));
        });
        Test("unsafe sample between hold and release discards", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12); f.P.Sample(f.Scope, f.Frame, false);
            Error(WireError.NotReady, () => f.Later(held, true));
        });
        Test("scope change between hold and release is stale", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12);
            f.Scope = new Scope(f.Scope.Session, Ids.New("r"), Ids.New("g")); Error(WireError.StaleScope, () => f.Later(held, true));
        });
        foreach (FrameGeometry changed in new[] { new FrameGeometry(12, 801, 600, 1, mapView), new FrameGeometry(12, 800, 601, 1, mapView), new FrameGeometry(12, 800, 600, 2, mapView) })
            Test("size scale change between hold and release " + changed.Width + "/" + changed.Height + "/" + changed.Scale, () =>
            {
                var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Frame = changed; f.P.Begin(f.Scope, changed);
                Error(WireError.NotReady, () => f.Later(held, true));
            });
        Test("developer refusal at release wins", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12);
            Error(WireError.ObservationRefused, () => f.P.Release(f.Request, held, f.Scope, f.Frame, WireError.ObservationRefused, true));
        });
        Test("release before the held frame is rejected", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Frame = new FrameGeometry(10, 800, 600, 1);
            Error(WireError.NotReady, () => f.Later(held, true));
        });
        Test("release check pinned to the releasing frame", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold(); f.Next(12); var stamp = f.Later(held, true);
            f.Frame = new FrameGeometry(13, 800, 600, 1); Error(WireError.NotReady, () => f.Release(stamp));
        });
        Test("render proof mode change while held discards even after restoration", () =>
        {
            var f = new Fixture(); f.Whole(); var held = f.Hold();
            f.P.Sample(f.Scope, new FrameGeometry(11, 800, 600, 1, !mapView), true); f.Next(12);
            Error(WireError.NotReady, () => f.Later(held, true));
        });
        // P4-11-R2: Options > General writes Prefs.DevMode with its current value every GUI frame (installed setter
        // guards all side effects with a changed-value check). That no-op write must not revoke render provenance.
        Test("unchanged DevMode write inside every GUI frame keeps the frame whole", () =>
        {
            var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
            f.Begin(); f.P.Updated(); f.P.CameraBegin(1, !f.Frame.MapView); f.P.CameraEnd(1, !f.Frame.MapView);
            f.P.GuiBegin();
            for (int i = 0; i < 3; i++) { SettingWrite state = SettingWriteBracket.Begin(epochs, false, false); SettingWriteBracket.End(epochs, ref state, false, false); }
            f.P.GuiEnd(true); f.Hold();
        });
        Test("legacy unconditional bracket on the same unchanged write revokes the frame", () =>
        {
            var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
            f.Begin(); f.P.Updated(); f.P.CameraBegin(1, !f.Frame.MapView); f.P.CameraEnd(1, !f.Frame.MapView);
            f.P.GuiBegin(); epochs.BeginBoundary(); epochs.EndBoundary(); f.P.GuiEnd(true);
            Error(WireError.NotReady, () => f.Hold());
        });
        foreach (bool incoming in new[] { false, true })
            Test("changed DevMode write inside the frame still revokes (incoming " + incoming + ")", () =>
            {
                var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
                f.Begin(); f.P.Updated(); f.P.GuiBegin();
                SettingWrite state = SettingWriteBracket.Begin(epochs, incoming, !incoming);
                SettingWriteBracket.End(epochs, ref state, incoming, false);
                f.P.GuiEnd(true); Error(WireError.NotReady, () => f.Hold());
            });
        Test("unchanged write that a foreign patch altered after our prefix revokes retroactively", () =>
        {
            var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
            f.Begin(); f.P.Updated(); f.P.GuiBegin();
            SettingWrite state = SettingWriteBracket.Begin(epochs, false, false);
            SettingWriteBracket.End(epochs, ref state, true, false);
            f.P.GuiEnd(true); Error(WireError.NotReady, () => f.Hold());
        });
        Test("unchanged write whose original threw revokes", () =>
        {
            var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
            f.Begin(); f.P.Updated(); f.P.GuiBegin();
            SettingWrite state = SettingWriteBracket.Begin(epochs, true, true);
            SettingWriteBracket.End(epochs, ref state, true, true);
            f.P.GuiEnd(true); Error(WireError.NotReady, () => f.Hold());
        });
        Test("finalizer without a prefix state invalidates conservatively", () =>
        {
            var f = new Fixture(); var epochs = new ObservationEpochs(); epochs.Register(f.P);
            f.Begin(); f.P.Updated(); f.P.GuiBegin();
            SettingWrite state = SettingWrite.None; SettingWriteBracket.End(epochs, ref state, false, false);
            f.P.GuiEnd(true); Error(WireError.NotReady, () => f.Hold());
        });
        Test("depth-only map proof is explicit, not whole-primary replacement", () =>
        {
            var f = new Fixture(true); f.Whole(); var held = f.Hold();
            Equal(false, held.Evidence.WholeFrameSafe); Equal(true, held.Evidence.MapViewSafe);
            f.Next(12); var stamp = f.Later(held, true); f.Release(stamp);
        });
    }
    private sealed class Fixture
    {
        internal readonly RenderProvenance P = new RenderProvenance();
        internal Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), Ids.New("g"));
        internal FrameGeometry Frame;
        internal readonly CaptureRequestStamp Request;
        internal Fixture(bool? map = null)
        {
            Frame = new FrameGeometry(11, 800, 600, 1, map ?? mapView);
            Request = P.Request(Scope, new FrameGeometry(10, 800, 600, 1, Frame.MapView));
        }
        internal void Begin() => P.Begin(Scope, Frame);
        internal void Gui() { P.GuiBegin(); P.GuiEnd(true); }
        internal void Whole(bool update = true, bool repaint = true, bool clear = true)
        {
            Begin(); if (update) P.Updated(); P.CameraBegin(1, clear && !Frame.MapView); P.CameraEnd(1, clear && !Frame.MapView);
            P.GuiBegin(); P.GuiEnd(repaint);
        }
        internal HeldRender Hold() => P.Hold(Request, Scope, Frame, null);
        internal void Next(long frame) { Frame = new FrameGeometry(frame, 800, 600, 1, Frame.MapView); P.Begin(Scope, Frame); }
        internal CompletedRenderStamp Later(HeldRender held, bool native) => P.Release(Request, held, Scope, Frame, null, native);
        internal CompletedRenderStamp Complete(bool native = true) => P.Complete(Request, Scope, Frame, null, native);
        internal void Release(CompletedRenderStamp stamp) => P.CheckRelease(stamp, Scope, Frame, null);
    }
    private static void Error(WireError expected, Action action)
    { try { action(); } catch (WireException e) when (e.Code == expected) { return; } throw new Exception("Expected " + expected); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Mismatch"); }
    private static void Test(string name, Action action)
    { try { action(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine((mapView ? "map: " : "menu: ") + name + ": " + e); } }
}
