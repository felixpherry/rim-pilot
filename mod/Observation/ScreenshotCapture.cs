using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // One ticket per eligible EOF callback. Hold runs only at the capture EOF (after the private
    // readback); Release runs at a later (or the same) EOF once THIS capture's fence has passed.
    // Any of them throws NotReady when the frame could not be witnessed safe, so a refusal is prompt.
    internal interface ICaptureTicket
    {
        bool Witnessed { get; }
        HeldRender Hold();
        bool FencePassed { get; }
        CompletedRenderStamp Release(HeldRender held);
        void CheckRelease(CompletedRenderStamp stamp);
    }
    internal interface IScreenshotBackend
    {
        CaptureRequestStamp Arm(Action<ICaptureTicket> consume);
        void Cancel();
        FrameGeometry Geometry { get; }
        // Owns/destroys its one texture even if readback fails. Never changes render target.
        byte[] ReadRgb(CaptureSize size);
    }

    // One session-wide flight, including failed attempts and EOF waiting. All Unity work and
    // release stay on the game thread; worker cancellation only changes PendingRead liveness.
    internal sealed class ScreenshotCapture
    {
        internal const int StageLimitMs = 100; // cooperative; NOT a bound on synchronous Unity
        // Whole flight (EOF wait + held bytes + fence wait) answers before the 3-s request deadline.
        internal const int FlightLimitMs = 2000;
        // Before any bytes exist, an unwitnessed frame (e.g. first-use cost exhausting the shared
        // allowance and revoking the epoch) may re-arm a FRESH request this many times. Same flight,
        // same cadence charge, no native work until a frame is witnessed; never after Hold.
        internal const int MaxRearms = 2;
        private readonly IScreenshotBackend backend;
        private readonly TransitionFence fence;
        private readonly Func<long> now;
        private Flight? active;
        private long next;
        internal long SpentTicks { get; private set; }
        internal bool Active => active != null;
        internal ScreenshotCapture(IScreenshotBackend backend, TransitionFence fence, Func<long>? now = null)
        { this.backend = backend; this.fence = fence; this.now = now ?? (() => Stopwatch.GetTimestamp()); }
        internal void BeginUpdate() { SpentTicks = 0; Sweep(); }
        internal void Sweep()
        {
            Flight? flight = active;
            if (flight == null) return;
            if (!flight.Live()) { Cancel(flight); return; }
            // Never leave a client waiting for the transport deadline: refuse clearly, drop held bytes.
            if (now() - flight.Started >= Ticks(FlightLimitMs)) { flight.Reject(WireError.Timeout); Cancel(flight); }
        }
        internal sealed class Flight
        {
            internal readonly Scope Scope;
            internal readonly Func<bool> Live;
            internal readonly Action<JObject> Complete;
            internal readonly Action<WireError> Reject;
            internal readonly long Started;
            internal CaptureRequestStamp? Request;
            internal int Rearms;
            // Private unreleased capture: dropped on completion, failure, cancel and timeout.
            internal byte[]? Png;
            internal CaptureSize? Size;
            internal HeldRender? Held;
            internal Flight(Scope scope, long started, Func<bool> live, Action<JObject> complete, Action<WireError> reject)
            { Scope = scope; Started = started; Live = live; Complete = complete; Reject = reject; }
            internal void Discard() { Png = null; Size = null; Held = null; }
        }
        internal Flight Start(Scope scope, Func<bool> live, Action<JObject> complete, Action<WireError> reject)
        {
            Sweep();
            long current = now();
            if (active != null || current < next) throw new WireException(WireError.Busy);
            if (!live()) throw new WireException(WireError.Timeout);
            // Failed arms/unsupported sizes consume cadence too; no repeated native attempts.
            next = current + Ticks(WireLimits.CaptureIntervalMs);
            var flight = new Flight(scope, current, live, complete, reject); active = flight;
            try
            {
                FrameGeometry frame = backend.Geometry;
                if (!frame.Valid) throw new WireException(WireError.NotReady);
                _ = new CaptureSize(frame.Width, frame.Height); // before texture/PNG allocations
                Arm(flight);
                return flight;
            }
            catch { Cancel(flight); throw; }
        }
        private void Arm(Flight flight)
        {
            flight.Request = backend.Arm(ticket => Consume(flight, ticket));
            if (!flight.Request.Scope.Same(flight.Scope)) throw new WireException(WireError.StaleScope);
        }
        internal void Cancel(Flight flight)
        {
            if (!ReferenceEquals(active, flight)) return;
            flight.Discard(); active = null; backend.Cancel();
        }
        private void Consume(Flight flight, ICaptureTicket ticket)
        {
            if (!ReferenceEquals(active, flight)) return;
            long start = now(), end = start + Ticks(StageLimitMs);
            bool held = fence.TryRead(), settled = true;
            try
            {
                if (!held) throw new WireException(WireError.NotReady);
                Check(flight, end);
                FrameGeometry geometry = backend.Geometry;
                if (!geometry.Valid) throw new WireException(WireError.NotReady);
                if (flight.Request == null || geometry.Frame <= flight.Request.AfterFrame)
                    throw new WireException(WireError.NotReady);
                if (flight.Held == null && !ticket.Witnessed)
                {
                    if (flight.Rearms++ >= MaxRearms) throw new WireException(WireError.NotReady);
                    backend.Cancel(); Arm(flight); settled = false; return; // nothing read, nothing held
                }
                if (flight.Held == null)
                {
                    var captured = new CaptureSize(geometry.Width, geometry.Height);
                    // Synchronous readback; bytes stay private AND unreleased. Hold proves the
                    // whole safe frame and only then creates this capture's native fence.
                    byte[] raw = backend.ReadRgb(captured);
                    Check(flight, end);
                    HeldRender heldRender = ticket.Hold();
                    if (heldRender.Evidence.Frame != geometry.Frame) throw new WireException(WireError.NotReady);
                    // Encode the still-unreleased bytes now, while the fence catches up: the release
                    // frame then only builds the DTO. Raw copy is dropped right after.
                    byte[] encodedPng = BoundedCapturePng.Encode(captured, raw, () => flight.Live() && now() < end);
                    Check(flight, end);
                    flight.Png = encodedPng; flight.Size = captured; flight.Held = heldRender;
                }
                CaptureSize size = flight.Size!;
                if (geometry.Width != size.Width || geometry.Height != size.Height)
                    throw new WireException(WireError.NotReady);
                // Throws NotReady when this frame cannot be witnessed; false just means keep waiting.
                if (!ticket.FencePassed) { settled = false; return; }
                CompletedRenderStamp completed = ticket.Release(flight.Held!);
                byte[] png = flight.Png!;
                Check(flight, end);
                JObject result = Responses.Capture(completed, size.Width, size.Height, geometry.Scale, png);
                Check(flight, end);
                ticket.CheckRelease(completed); // AFTER encoding and base64/DTO construction
                Check(flight, end);
                // Transition fence spans final sample AND primitive ownership transfer.
                flight.Complete(result);
            }
            catch (WireException error) { flight.Reject(error.Code); }
            catch { flight.Reject(WireError.Unavailable); }
            finally
            {
                if (held) fence.ReleaseRead();
                SpentTicks += Math.Max(0, now() - start);
                if (settled) Cancel(flight);
            }
        }
        private void Check(Flight flight, long end)
        {
            if (!ReferenceEquals(active, flight) || !flight.Live() || now() >= end
                || now() - flight.Started >= Ticks(FlightLimitMs)) throw new WireException(WireError.Timeout);
        }
        private static long Ticks(int ms) => (long)Math.Ceiling(ms * (double)Stopwatch.Frequency / 1000);
    }

    // Scheduler retains cancellation/active-slot ownership while the same-EOF consumer does
    // release. It must NEVER return Complete to ordinary next-Update lease publication.
    internal sealed class ScreenshotProducer<TMap> : IReadProducer<TMap> where TMap : class
    {
        private readonly ScreenshotCapture capture;
        internal ScreenshotProducer(ScreenshotCapture capture) { this.capture = capture; }
        public bool SnapshotBuild => false;
        public IReadContinuation<TMap> Start(Request request) => new Continuation(capture);
        private sealed class Continuation : IReadContinuation<TMap>
        {
            private readonly ScreenshotCapture owner;
            private ScreenshotCapture.Flight? flight;
            internal Continuation(ScreenshotCapture owner) { this.owner = owner; }
            public StepResult Step(ProducerWork<TMap> work)
            {
                if (flight == null && work.TryNestedFields(1))
                {
                    try
                    {
                        PendingRead read = work.Read;
                        flight = owner.Start(work.Scope, () => read.IsLive,
                            result => read.TryComplete(result), error => read.TryReject(error));
                    }
                    catch (WireException error) { work.Fail(error.Code); }
                }
                return StepResult.Continue;
            }
            public bool Eligible(ProducerWork<TMap> work) => false;
            public JObject Result() => throw new InvalidOperationException();
            public void Discard() { if (flight != null) owner.Cancel(flight); flight = null; }
        }
    }
}
