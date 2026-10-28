using System;
using System.Threading;
using RimPilot.Protocol;

namespace RimPilot.Mod.Bridge
{
    // Independent render authorization: ordinary Update/GUI lease revocation is not a hard
    // render change. Every such callback must instead be witnessed at BOTH endpoints below.
    // Hard epoch notifications (including worker transitions) irreversibly cancel the frame.
    internal sealed class RenderProvenance : IObservationBoundarySink
    {
        internal const int MaxCameras = 32;
        private readonly object gate = new object();
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly int[] cameras = new int[MaxCameras];
        private readonly bool[] ended = new bool[MaxCameras];
        private long epoch = 1;
        private bool failed, active, updated, repaint, cleared;
        private int cameraCount, guiDepth;
        private bool cameraClear;
        private long releaseFrame = -1;
        private Scope? scope;
        private FrameGeometry? geometry;

        internal bool Active { get { lock (gate) return active; } }
        internal void CheckThread()
        { if (thread != Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Game thread required."); }
        public void Invalidate(ObservationInvalidation cause)
        {
            lock (gate)
            {
                active = false;
                if (epoch == long.MaxValue) failed = true; else epoch++;
            }
        }
        internal CaptureRequestStamp Request(Scope current, FrameGeometry frame)
        {
            CheckThread(); lock (gate)
            {
                if (failed || !frame.Valid) throw new WireException(WireError.NotReady);
                scope = current; geometry = frame;
                return new CaptureRequestStamp(current, epoch, frame.Frame);
            }
        }
        internal void Begin(Scope current, FrameGeometry frame)
        {
            CheckThread(); lock (gate)
            {
                if (geometry == null || scope == null || !scope.Same(current) || !frame.Valid
                    || frame.Frame <= geometry.Frame || frame.Frame - geometry.Frame != 1
                    || frame.Width != geometry.Width || frame.Height != geometry.Height || frame.Scale != geometry.Scale
                    || frame.MapView != geometry.MapView)
                    Invalidate(ObservationInvalidation.All);
                active = !failed && frame.Valid; scope = current; geometry = frame;
                updated = repaint = cleared = false; cameraCount = guiDepth = 0;
                Array.Clear(ended, 0, ended.Length);
            }
        }
        internal bool Sample(Scope? current, FrameGeometry? frame, bool safe)
        {
            CheckThread(); lock (gate)
            {
                if (!active) return false;
                if (!safe || current == null || scope == null || !scope.Same(current) || frame == null
                    || geometry == null || !frame.Valid || frame.Frame != geometry.Frame
                    || frame.Width != geometry.Width || frame.Height != geometry.Height || frame.Scale != geometry.Scale
                    || frame.MapView != geometry.MapView)
                { Invalidate(ObservationInvalidation.All); return false; }
                return true;
            }
        }
        internal void Updated() { CheckThread(); lock (gate) { if (active) updated = true; } }
        internal void GuiBegin() { CheckThread(); lock (gate) { if (active && ++guiDepth != 1) Invalidate(ObservationInvalidation.All); } }
        internal void GuiEnd(bool isRepaint)
        {
            CheckThread(); lock (gate)
            {
                if (!active) return;
                if (guiDepth != 1) { Invalidate(ObservationInvalidation.All); return; }
                guiDepth = 0; repaint |= isRepaint;
            }
        }
        internal void CameraBegin(int id, bool fullColorClear)
        {
            CheckThread(); lock (gate)
            {
                if (!active) return;
                // Repeated/manual/nested renders are unsupported, not silently merged.
                for (int i = 0; i < cameraCount; i++)
                    if (cameras[i] == id || !ended[i]) { Invalidate(ObservationInvalidation.All); return; }
                if (cameraCount == MaxCameras) { Invalidate(ObservationInvalidation.All); return; }
                cameras[cameraCount++] = id; cameraClear = fullColorClear;
            }
        }
        internal void CameraEnd(int id, bool fullColorClear = true)
        {
            CheckThread(); lock (gate)
            {
                if (!active) return;
                if (cameraCount == 0 || cameras[cameraCount - 1] != id || ended[cameraCount - 1])
                { Invalidate(ObservationInvalidation.All); return; }
                ended[cameraCount - 1] = true;
                cleared |= cameraClear && fullColorClear;
            }
        }
        // Capture EOF: menu whole-color proof OR approved map-view displayed-frame proof.
        // Map view does not prove every primary pixel was replaced. Bytes stay UNRELEASED.
        // Nothing here asks the native fence: Release later proves it for a later frame.
        internal HeldRender Hold(CaptureRequestStamp request, Scope current, FrameGeometry frame, WireError? refusal)
        {
            CheckThread(); lock (gate)
            {
                // Refusal precedence retained even after the candidate was revoked.
                if (refusal == WireError.ObservationRefused) throw new WireException(refusal.Value);
                if (!request.Scope.Same(current)) throw new WireException(WireError.StaleScope);
                Sample(current, frame, !refusal.HasValue);
                bool complete = active && updated && repaint && guiDepth == 0 && cameraCount > 0
                    && ended[cameraCount - 1];
                var evidence = new RenderEvidence(current, epoch, frame.Frame, complete,
                    complete && cleared, complete && frame.MapView);
                // Validates request/epoch/scope/readiness now; the throwaway stamp is never used.
                CompletedRenderStamp.Complete(request, evidence, current, epoch, refusal);
                return new HeldRender(evidence, frame);
            }
        }
        // Later (or same) EOF: native fence for THIS capture passed AND the same hard epoch,
        // scope and size/scale still hold with no unsafe interval (any of those bumps the epoch).
        internal CompletedRenderStamp Release(CaptureRequestStamp request, HeldRender held, Scope current,
            FrameGeometry frame, WireError? refusal, bool nativeFencePassed)
        {
            CheckThread(); lock (gate)
            {
                if (refusal == WireError.ObservationRefused) throw new WireException(refusal.Value);
                if (!request.Scope.Same(current)) throw new WireException(WireError.StaleScope);
                Sample(current, frame, !refusal.HasValue);
                if (frame.Frame < held.Evidence.Frame || !held.SameSurface(frame)) throw new WireException(WireError.NotReady);
                bool ok = active && nativeFencePassed;
                var evidence = new RenderEvidence(held.Evidence.Scope, held.Evidence.SafetyEpoch,
                    held.Evidence.Frame, ok, ok && held.Evidence.WholeFrameSafe,
                    ok && held.Evidence.MapViewSafe);
                CompletedRenderStamp stamp = CompletedRenderStamp.Complete(request, evidence, current, epoch, refusal);
                releaseFrame = frame.Frame;
                return stamp;
            }
        }
        // Same-EOF convenience (fence already passed): hold plus release in one callback.
        internal CompletedRenderStamp Complete(CaptureRequestStamp request, Scope current, FrameGeometry frame,
            WireError? refusal, bool nativeFencePassed)
        {
            CheckThread(); lock (gate)
                return Release(request, Hold(request, current, frame, refusal), current, frame, refusal, nativeFencePassed);
        }
        internal void CheckRelease(CompletedRenderStamp stamp, Scope current, FrameGeometry frame, WireError? refusal)
        {
            CheckThread(); lock (gate)
            {
                // Release must still be in the very same callback that completed, never another frame's pixels.
                Sample(current, frame, !refusal.HasValue);
                stamp.CheckRelease(current, epoch, refusal);
                if (!active || frame.Frame != releaseFrame) throw new WireException(WireError.NotReady);
            }
        }
    }

    // Private evidence for bytes read at one EOF and still unreleased.
    internal sealed class HeldRender
    {
        internal readonly RenderEvidence Evidence;
        private readonly int width, height;
        private readonly float scale;
        private readonly bool mapView;
        internal HeldRender(RenderEvidence evidence, FrameGeometry frame)
        { Evidence = evidence; width = frame.Width; height = frame.Height; scale = frame.Scale; mapView = frame.MapView; }
        internal bool SameSurface(FrameGeometry frame) => frame.Width == width && frame.Height == height
            && frame.Scale == scale && frame.MapView == mapView;
    }
}
