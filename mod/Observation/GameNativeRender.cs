using System;
using System.Collections;
using System.Diagnostics;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;
using UnityEngine;
using UnityEngine.Rendering;

namespace RimPilot.Mod.Observation
{
    internal sealed class RenderSample
    {
        internal readonly Scope? Scope;
        internal readonly FrameGeometry? Geometry;
        internal readonly WireError? Refusal;
        internal RenderSample(Scope? scope, FrameGeometry? geometry, WireError? refusal)
        { Scope = scope; Geometry = geometry; Refusal = refusal; }
    }

    // Bridge-owned component: no drawing, camera changes, input or game coroutine control.
    // An armed consumer runs synchronously at each eligible EOF with one short-lived ticket. The
    // capture EOF holds privately read bytes UNRELEASED; only a later EOF whose fence for that
    // capture passed, with the same hard epoch/scope/size, may release. A yield/API return alone never stamps.
    internal sealed class NativeRenderTicket : ICaptureTicket
    {
        private readonly GameNativeRender owner;
        private readonly CaptureRequestStamp request;
        private readonly bool witnessed;
        private bool open = true;
        internal NativeRenderTicket(GameNativeRender owner, CaptureRequestStamp request, bool witnessed)
        { this.owner = owner; this.request = request; this.witnessed = witnessed; }
        private void Require() { if (!open || !witnessed) throw new WireException(WireError.NotReady); }
        public bool Witnessed => open && witnessed;
        public HeldRender Hold() { Require(); return owner.Hold(request); }
        public bool FencePassed { get { Require(); return owner.FencePassed; } }
        public CompletedRenderStamp Release(HeldRender held) { Require(); return owner.Release(request, held); }
        public void CheckRelease(CompletedRenderStamp stamp)
        {
            if (!open) throw new WireException(WireError.NotReady);
            owner.CheckRelease(stamp);
        }
        internal void Close() => open = false;
    }

    internal sealed class GameNativeRender : MonoBehaviour
    {
        private RenderProvenance? provenance;
        private Func<UpdateBudget, RenderSample>? sample;
        private CaptureRequestStamp? request;
        private Action<NativeRenderTicket>? consume;
        private bool installed;
        private GraphicsFence fence;
        private bool hasFence;
        private long spent;
        internal long BoundarySampleTicks => spent;
        internal bool Armed => request != null;

        internal void Initialize(ObservationEpochs epochs, Func<UpdateBudget, RenderSample> sampler)
        {
            if (installed) throw new InvalidOperationException();
            provenance = new RenderProvenance(); sample = sampler;
            epochs.Register(provenance);
            Camera.onPreCull += BeforeCull;
            Camera.onPreRender += BeforeCamera;
            Camera.onPostRender += AfterCamera;
            // Root catches its own Update/OnGUI exceptions. Logging is only a revocation
            // witness: never inspect/copy log text, never suppress or rewrite a game error.
            Application.logMessageReceivedThreaded += OnLog;
            this.epochs = epochs;
            installed = true;
            // One discarded sample at install: first-use JIT of the sampling path (several ms on Mono)
            // must not land inside the first capture's shared 2-ms allowance and kill its provenance.
            // Result unused, nothing armed, no request exists; the debit is not carried forward.
            Read(); spent = 0;
            StartCoroutine(EndFrames());
        }
        private ObservationEpochs? epochs;
        internal CaptureRequestStamp Arm(Action<NativeRenderTicket> consumer)
        {
            if (!installed || provenance == null || Armed) throw new WireException(WireError.Busy);
            provenance.CheckThread();
            RenderSample current = Read();
            if (current.Refusal.HasValue) throw new WireException(current.Refusal.Value);
            if (current.Scope == null || current.Geometry == null) throw new WireException(WireError.NotReady);
            request = provenance.Request(current.Scope, current.Geometry); consume = consumer;
            return request;
        }
        internal void Cancel()
        {
            provenance?.CheckThread(); request = null; consume = null; hasFence = false; fence = default;
            provenance?.Invalidate(ObservationInvalidation.All);
        }
        private RenderSample Read()
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (sample == null) return new RenderSample(null, null, WireError.NotReady);
                return sample(UpdateBudget.Boundary(() => Stopwatch.GetTimestamp()));
            }
            catch { provenance?.Invalidate(ObservationInvalidation.All); return new RenderSample(null, null, WireError.NotReady); }
            finally { spent += Math.Max(0, Stopwatch.GetTimestamp() - start); }
        }
        private bool Witness()
        {
            // Not yet witnessed by this frame's Begin: nothing to certify, so no sample is spent.
            if (!Armed || provenance == null || !provenance.Active) return false;
            RenderSample current = Read();
            return provenance.Sample(current.Scope, current.Geometry, !current.Refusal.HasValue);
        }
        internal void BeginFrame()
        {
            spent = 0;
            if (!Armed || provenance == null) return;
            RenderSample current = Read();
            if (current.Refusal.HasValue || current.Scope == null || current.Geometry == null)
            { provenance.Invalidate(ObservationInvalidation.All); return; }
            provenance.Begin(current.Scope, current.Geometry);
        }
        internal void EndUpdate(bool succeeded)
        {
            if (!succeeded) provenance?.Invalidate(ObservationInvalidation.All);
            else if (Witness()) provenance!.Updated();
        }
        internal void BeginGui() { if (Witness()) provenance!.GuiBegin(); }
        internal void EndGui(bool succeeded, bool repaint)
        {
            if (!succeeded) provenance?.Invalidate(ObservationInvalidation.All);
            else if (Witness()) provenance!.GuiEnd(repaint);
        }
        private void BeforeCull(Camera camera) => Witness();
        private static bool FullClear(Camera camera)
        {
            Rect rect = camera.pixelRect;
            return camera.targetTexture == null && camera.targetDisplay == 0
                && rect.x == 0 && rect.y == 0 && rect.width == Screen.width && rect.height == Screen.height
                && camera.clearFlags == CameraClearFlags.Color;
        }
        private void BeforeCamera(Camera camera)
        {
            try
            {
                if (!Witness()) return;
                // Menu requires full backbuffer color replacement. Approved map view instead
                // uses displayed-frame proof; depth-only clear never proves replacement.
                provenance!.CameraBegin(camera.GetInstanceID(), FullClear(camera));
            }
            catch { provenance?.Invalidate(ObservationInvalidation.All); }
        }
        private void AfterCamera(Camera camera)
        {
            try { if (Witness()) provenance!.CameraEnd(camera.GetInstanceID(), FullClear(camera)); }
            catch { provenance?.Invalidate(ObservationInvalidation.All); }
        }
        private void OnLog(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                provenance?.Invalidate(ObservationInvalidation.All);
        }
        private IEnumerator EndFrames()
        {
            var end = new WaitForEndOfFrame();
            while (installed)
            {
                yield return end;
                // Arm occurs after Update. Its same-frame EOF has no whole-frame witness;
                // keep the request for the strictly newer frame instead of canceling it.
                if (!Armed || Time.frameCount <= request!.AfterFrame) continue;
                NativeRenderTicket? ticket = null;
                try
                {
                    // No waiting/spin/GPU fallback. A frame that cannot be witnessed safe, or an
                    // unsupported fence, makes the ticket refuse NotReady so the capture owner answers
                    // promptly instead of waiting out the transport deadline.
                    ticket = new NativeRenderTicket(this, request!, Witness());
                    if (consume == null) Cancel(); else consume(ticket);
                }
                catch { provenance?.Invalidate(ObservationInvalidation.All); Cancel(); }
                finally { ticket?.Close(); }
            }
        }
        internal bool FencePassed => hasFence && fence.passed;
        internal HeldRender Hold(CaptureRequestStamp wanted)
        {
            RenderSample current = Read();
            if (current.Refusal == WireError.ObservationRefused) throw new WireException(WireError.ObservationRefused);
            if (current.Scope == null || current.Geometry == null || provenance == null) throw new WireException(WireError.NotReady);
            HeldRender held = provenance.Hold(wanted, current.Scope, current.Geometry, current.Refusal);
            if (!SystemInfo.supportsGraphicsFence) throw new WireException(WireError.NotReady);
            // Created after the synchronous readback; bytes stay unreleased until a later EOF sees it passed.
            fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.PixelProcessing);
            hasFence = true;
            return held;
        }
        internal CompletedRenderStamp Release(CaptureRequestStamp wanted, HeldRender held)
        {
            RenderSample current = Read();
            if (current.Refusal == WireError.ObservationRefused) throw new WireException(WireError.ObservationRefused);
            if (current.Scope == null || current.Geometry == null || provenance == null) throw new WireException(WireError.NotReady);
            return provenance.Release(wanted, held, current.Scope, current.Geometry, current.Refusal, FencePassed);
        }
        internal void CheckRelease(CompletedRenderStamp stamp)
        {
            RenderSample current = Read();
            if (current.Refusal == WireError.ObservationRefused) throw new WireException(WireError.ObservationRefused);
            if (current.Scope == null || current.Geometry == null || provenance == null) throw new WireException(WireError.NotReady);
            provenance.CheckRelease(stamp, current.Scope, current.Geometry, current.Refusal);
        }
        private void OnDestroy()
        {
            installed = false;
            Camera.onPreCull -= BeforeCull; Camera.onPreRender -= BeforeCamera; Camera.onPostRender -= AfterCamera;
            Application.logMessageReceivedThreaded -= OnLog;
            Cancel();
            if (provenance != null) epochs?.Unregister(provenance);
        }
    }
}
