using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimPilot.Mod.Bridge;
using RimPilot.Mod.Observation;
using RimPilot.Protocol;

internal static class ScreenshotCaptureTests
{
    private static int passed, failed;
    private static bool mapView;
    internal static int Run()
    {
        RunMode(false); RunMode(true);
        Console.WriteLine($"Screenshot capture: {passed} passed, {failed} failed (host only; no Unity/Mono pixels or native latency proof)");
        return failed;
    }
    private static void RunMode(bool map)
    {
        mapView = map;
        Test("preflight bounds before image allocation", () =>
        {
            foreach (var pair in new[] { (0, 1), (1, 0), (-1, 1), (1921, 1), (1, 1081), (1920, 1080), (int.MaxValue, int.MaxValue) })
                Error(WireError.ResponseTooLarge, () => new CaptureSize(pair.Item1, pair.Item2));
            True(new CaptureSize(1280, 720).PngBytes < WireLimits.CapturePngBytes);
            var f = new Fixture(); f.Backend.Frame = new FrameGeometry(10, 1920, 1080, 1);
            Error(WireError.ResponseTooLarge, () => f.Start()); Equal(0, f.Backend.Arms); Equal(0, f.Backend.Reads); True(!f.Capture.Active);
        });
        Test("stored PNG roundtrip, CRC, exact lengths and bottom-up orientation", () =>
        {
            foreach (var pair in new[] { (1, 1), (2, 2), (257, 89), (1280, 720) })
            {
                var size = new CaptureSize(pair.Item1, pair.Item2);
                byte[] rgb = Enumerable.Range(0, size.RawBytes).Select(n => (byte)(n * 37)).ToArray();
                byte[] png = BoundedCapturePng.Encode(size, rgb, () => true);
                Equal(size.PngBytes, png.Length); ValidatePng(png, size, rgb);
            }
        });
        Test("encoder bounds raw input and cancellation between blocks", () =>
        {
            var size = new CaptureSize(1280, 720);
            Error(WireError.Unavailable, () => BoundedCapturePng.Encode(size, new byte[1], () => true));
            int checks = 0;
            Error(WireError.Timeout, () => BoundedCapturePng.Encode(size, new byte[size.RawBytes], () => ++checks < 3));
            Equal(3, checks);
        });
        Test("whole newer frame releases valid typed screenshot after encoding", () =>
        {
            var f = new Fixture(); f.Start(); f.Offer(); True(!f.Capture.Active); True(f.Result != null);
            Equal("completed_render", (string?)f.Result!["phase"]); Equal(11L, (long)f.Result["frame"]!);
            Equal(1, f.Backend.Reads); Equal(1, f.Backend.Completions); Equal(1, f.Backend.Releases);
            ResponseContracts.Read(WireJson.EncodeResponse("capture", ReadMethod.Screenshot, f.Result), ReadMethod.Screenshot, "capture");
            var size = new CaptureSize(2, 2);
            ValidatePng(Convert.FromBase64String(string.Concat(((JArray)f.Result["chunks"]!).Values<string>())), size, new byte[size.RawBytes]);
        });
        Test("single flight and cadence include unsuccessful attempts", () =>
        {
            var f = new Fixture(); var first = f.Start(); Error(WireError.Busy, () => f.Start()); f.Capture.Cancel(first);
            Error(WireError.Busy, () => f.Start()); f.Clock += Stopwatch.Frequency; f.Start();
            Equal(2, f.Backend.Arms);
        });
        Test("late discard cannot cancel replacement flight", () =>
        {
            var f = new Fixture(); var old = f.Start(); f.Capture.Cancel(old); f.Clock += Stopwatch.Frequency;
            f.Start(); int cancels = f.Backend.Cancels; f.Capture.Cancel(old); Equal(cancels, f.Backend.Cancels); True(f.Capture.Active);
        });
        Test("cancel or deadline settles without any offered ticket", () =>
        {
            var f = new Fixture(); f.Start(); f.Live = false; f.Capture.BeginUpdate();
            True(!f.Capture.Active); Equal(1, f.Backend.Cancels); Equal(0, f.Backend.Reads);
            f.Offer(); True(f.Result == null);
        });
        Test("same-frame offer cannot read or release", () =>
        {
            var f = new Fixture(); f.Start(); f.Backend.Offer(); Equal(WireError.NotReady, f.Error); Equal(0, f.Backend.Reads); True(f.Result == null);
        });
        Test("fence not passed holds bytes unreleased, then times out and discards", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; var flight = f.Start(); f.Offer();
            Equal(1, f.Backend.Reads); Equal(0, f.Backend.Releases); True(f.Result == null && f.Error == null);
            True(f.Capture.Active && flight.Png != null && flight.Held != null);
            f.Clock += 3 * Stopwatch.Frequency; f.Capture.BeginUpdate();
            Equal(WireError.Timeout, f.Error); True(!f.Capture.Active && f.Result == null);
            True(flight.Png == null && flight.Held == null); Equal(1, f.Backend.Cancels);
            f.Offer(); Equal(1, f.Backend.Reads); True(f.Result == null);
        });
        Test("later frame fence pass releases the earlier private read once", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; f.Start(); f.Offer();
            f.Backend.Whole(); f.Backend.Offer(); // frame 12, fence still false
            Equal(1, f.Backend.Reads); True(f.Result == null && f.Error == null && f.Capture.Active);
            Error(WireError.Busy, () => f.Start()); // one flight while holding
            f.Backend.FencePassed = true; f.Backend.Whole(); f.Backend.Offer(); // frame 13
            True(f.Result != null && !f.Capture.Active); Equal(11L, (long)f.Result!["frame"]!);
            Equal(1, f.Backend.Reads); Equal(1, f.Backend.Completions); Equal(1, f.Backend.Releases);
            ResponseContracts.Read(WireJson.EncodeResponse("capture", ReadMethod.Screenshot, f.Result), ReadMethod.Screenshot, "capture");
            var size = new CaptureSize(2, 2);
            ValidatePng(Convert.FromBase64String(string.Concat(((JArray)f.Result["chunks"]!).Values<string>())), size, new byte[size.RawBytes]);
            f.Backend.Whole(); f.Backend.Offer(); Equal(1, f.Backend.Reads); // ticket consumed once
        });
        Test("held bytes discarded when the safety epoch changes", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; var flight = f.Start(); f.Offer();
            f.Backend.P.Invalidate(ObservationInvalidation.Safety); f.Backend.FencePassed = true; f.Offer();
            Equal(WireError.NotReady, f.Error); True(f.Result == null && !f.Capture.Active && flight.Png == null);
            Equal(0, f.Backend.Releases);
        });
        Test("held bytes discarded when root/game scope changes", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; var flight = f.Start(); f.Offer();
            f.Backend.Scope = new Scope(f.Scope.Session, Ids.New("r"), null); f.Backend.FencePassed = true; f.Offer();
            Equal(WireError.StaleScope, f.Error); True(f.Result == null && !f.Capture.Active && flight.Png == null);
        });
        foreach (var changed in new[] { new FrameGeometry(12, 4, 2, 1, mapView), new FrameGeometry(12, 2, 4, 1, mapView), new FrameGeometry(12, 2, 2, 2, mapView) })
            Test("held bytes discarded when render size/scale changes " + changed.Width + "x" + changed.Height + "@" + changed.Scale, () =>
            {
                var f = new Fixture(); f.Backend.FencePassed = false; var flight = f.Start(); f.Offer();
                f.Backend.Frame = changed; f.Backend.P.Begin(f.Scope, changed); f.Backend.FencePassed = true; f.Offer();
                Equal(WireError.NotReady, f.Error); True(f.Result == null && !f.Capture.Active && flight.Png == null);
            });
        Test("unsafe interval or developer refusal between frames discards held bytes", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; f.Start(); f.Offer();
            f.Backend.Whole(); f.Backend.P.Sample(f.Scope, f.Backend.Frame, false); // unsafe sample mid-hold
            f.Backend.FencePassed = true; f.Backend.Offer(); Equal(WireError.NotReady, f.Error); True(f.Result == null);
            var g = new Fixture(); g.Backend.FencePassed = false; g.Start(); g.Offer();
            g.Backend.Whole(); g.Backend.Refusal = WireError.ObservationRefused; g.Backend.FencePassed = true; g.Backend.Offer();
            Equal(WireError.ObservationRefused, g.Error); True(g.Result == null && !g.Capture.Active);
        });
        Test("unwitnessed frames re-arm a bounded number of times, then refuse promptly with no native read", () =>
        {
            var f = new Fixture(); f.Backend.Witness = false; f.Start();
            for (int i = 0; i < ScreenshotCapture.MaxRearms; i++) { f.Backend.Whole(); f.Backend.Offer(); True(f.Capture.Active && f.Error == null); }
            f.Backend.Whole(); f.Backend.Offer();
            Equal(WireError.NotReady, f.Error); True(!f.Capture.Active && f.Result == null);
            Equal(1 + ScreenshotCapture.MaxRearms, f.Backend.Arms); Equal(0, f.Backend.Reads);
            var g = new Fixture(); g.Backend.FencePassed = false; g.Start(); g.Offer(); g.Backend.Witness = false;
            g.Backend.Whole(); g.Backend.Offer(); Equal(WireError.NotReady, g.Error); True(!g.Capture.Active && g.Result == null);
            Equal(1, g.Backend.Arms); // never re-armed once bytes are held
        });
        Test("one unwitnessed frame before any read re-arms a fresh request and still succeeds", () =>
        {
            var f = new Fixture(); f.Backend.Witness = false; f.Start();
            f.Backend.Whole(); f.Backend.Offer(); Equal(2, f.Backend.Arms); Equal(0, f.Backend.Reads); True(f.Capture.Active);
            f.Backend.Witness = true; f.Backend.Whole(); f.Backend.Offer();
            True(f.Result != null && !f.Capture.Active); Equal(1, f.Backend.Reads); Equal(12L, (long)f.Result!["frame"]!);
        });
        Test("cancel or disconnect while holding drops bytes without release", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; var flight = f.Start(); f.Offer();
            f.Live = false; f.Capture.BeginUpdate(); True(!f.Capture.Active && flight.Png == null && flight.Held == null);
            f.Backend.FencePassed = true; f.Offer(); Equal(0, f.Backend.Releases); True(f.Result == null);
        });
        Test("hold timeout also bounds a late EOF without a sweep", () =>
        {
            var f = new Fixture(); f.Backend.FencePassed = false; f.Start(); f.Offer();
            f.Clock += 3 * Stopwatch.Frequency; f.Backend.FencePassed = true; f.Offer();
            Equal(WireError.Timeout, f.Error); True(f.Result == null && !f.Capture.Active);
        });
        Test("native readback error clears owner and never encodes success", () =>
        {
            var f = new Fixture(); f.Backend.OnRead = () => throw new Exception("private error"); f.Start(); f.Offer();
            Equal(WireError.Unavailable, f.Error); True(!f.Capture.Active); True(f.Result == null); Equal(0, f.Backend.Completions);
        });
        Test("wrong native byte length refused", () =>
        {
            var f = new Fixture(); f.Backend.WrongLength = true; f.Start(); f.Offer(); Equal(WireError.Unavailable, f.Error); True(f.Result == null);
        });
        Test("disconnect during native work never transfers", () =>
        {
            var f = new Fixture(); f.Backend.OnRead = () => f.Live = false; f.Start(); f.Offer(); Equal(WireError.Timeout, f.Error); True(f.Result == null);
        });
        Test("native latency is measured not preempted; overdue stage refused", () =>
        {
            var f = new Fixture(); f.Backend.OnRead = () => f.Clock += Stopwatch.Frequency; f.Start(); f.Offer();
            Equal(WireError.Timeout, f.Error); True(f.Result == null); Equal(Stopwatch.Frequency, f.Capture.SpentTicks);
            f.Capture.BeginUpdate(); Equal(0L, f.Capture.SpentTicks);
        });
        Test("unsafe on-off during readback permanently revokes ticket", () =>
        {
            var f = new Fixture(); f.Backend.OnRead = () => f.Backend.P.Invalidate(ObservationInvalidation.Safety);
            f.Start(); f.Offer(); Equal(WireError.NotReady, f.Error); True(f.Result == null);
        });
        Test("post-encode fresh release refusal wins; no partial result", () =>
        {
            var f = new Fixture(); f.Backend.OnRelease = () => throw new WireException(WireError.ObservationRefused);
            f.Start(); f.Offer(); Equal(WireError.ObservationRefused, f.Error); True(f.Result == null); True(!f.Capture.Active);
        });
        Test("geometry changes after readback revoke complete", () =>
        {
            var f = new Fixture(); f.Backend.OnRead = () => f.Backend.Frame = new FrameGeometry(11, 2, 2, 2, mapView);
            f.Start(); f.Offer(); Equal(WireError.NotReady, f.Error); True(f.Result == null);
        });
        Test("transition before EOF refuses without native readback", () =>
        {
            var f = new Fixture(); f.Start(); f.Fence.Begin(); f.Offer(); f.Fence.End();
            Equal(WireError.NotReady, f.Error); Equal(0, f.Backend.Reads); True(f.Result == null);
        });
        Test("scope mismatch cannot arm replacement session", () =>
        {
            var f = new Fixture(); f.Backend.Scope = new Scope(Ids.New("s"), Ids.New("r"), null);
            Error(WireError.StaleScope, () => f.Start()); True(!f.Capture.Active);
        });
        Test("scheduler holds cancellation slot; EOF releases without next-update lease", () =>
        {
            using var f = new DispatchFixture(); PendingRead read = f.Enqueue(); f.Pump();
            Equal(1, f.Dispatch.ActiveCount); True(!read.Completion.IsCompleted);
            f.Backend.Whole(); f.Backend.Offer(); True(read.Completion.IsCompletedSuccessfully);
            True(read.Completion.Result.Data != null); True(!f.Capture.Active);
            f.Pump(); Equal(0, f.Dispatch.ActiveCount); Equal(0, f.Fence.Active.HeldBytes);
        });
        Test("scheduler rejects overlapping capture without canceling first", () =>
        {
            using var f = new DispatchFixture(); PendingRead first = f.Enqueue(), second = f.Enqueue(); f.Pump();
            True(!first.Completion.IsCompleted); Equal(WireError.Busy, second.Completion.Result.Error);
            f.Backend.Whole(); f.Backend.Offer(); True(first.Completion.Result.Data != null);
        });
        Test("scheduler disconnect cancels armed EOF owner and active slot", () =>
        {
            using var f = new DispatchFixture(); PendingRead read = f.Enqueue(); f.Pump();
            f.Lifetime.Close(); f.Pump(); True(read.Completion.IsCanceled); True(!f.Capture.Active); Equal(0, f.Dispatch.ActiveCount);
            f.Backend.Whole(); f.Backend.Offer(); Equal(0, f.Backend.Reads);
        });
        Test("scheduler deadline expires without offered ticket", () =>
        {
            using var f = new DispatchFixture(); f.Lifetime.Begin(30); PendingRead read = f.Enqueue(); f.Pump();
            Thread.Sleep(60); f.Pump(); True(read.Completion.IsCanceled); True(!f.Capture.Active); Equal(0, f.Dispatch.ActiveCount);
        });
        Test("worker transition rejects taken capture immediately; next pump drops owner", () =>
        {
            using var f = new DispatchFixture(); PendingRead read = f.Enqueue(); f.Pump();
            Task.Run(() => { f.Fence.Begin(); f.Fence.End(); }).GetAwaiter().GetResult();
            Equal(WireError.NotReady, read.Completion.Result.Error); f.Pump(); True(!f.Capture.Active); Equal(0, f.Dispatch.ActiveCount);
        });
        Test("scheduler keeps slot while holding, releases at later fence pass", () =>
        {
            using var f = new DispatchFixture(); f.Backend.FencePassed = false; PendingRead read = f.Enqueue(); f.Pump();
            f.Backend.Whole(); f.Backend.Offer(); f.Pump();
            Equal(1, f.Dispatch.ActiveCount); True(!read.Completion.IsCompleted && f.Capture.Active);
            f.Backend.FencePassed = true; f.Backend.Whole(); f.Backend.Offer();
            True(read.Completion.IsCompletedSuccessfully && read.Completion.Result.Data != null);
            f.Pump(); Equal(0, f.Dispatch.ActiveCount); Equal(0, f.Fence.Active.HeldBytes); Equal(1, f.Backend.Reads);
        });
        Test("scheduler worker transition while holding rejects and drops bytes", () =>
        {
            using var f = new DispatchFixture(); f.Backend.FencePassed = false; PendingRead read = f.Enqueue(); f.Pump();
            f.Backend.Whole(); f.Backend.Offer();
            Task.Run(() => { f.Fence.Begin(); f.Fence.End(); }).GetAwaiter().GetResult();
            Equal(WireError.NotReady, read.Completion.Result.Error); f.Pump();
            True(!f.Capture.Active); Equal(0, f.Dispatch.ActiveCount);
            f.Backend.FencePassed = true; f.Backend.Whole(); f.Backend.Offer(); Equal(1, f.Backend.Reads); Equal(0, f.Backend.Releases);
        });
        Test("developer refusal during waiting cancels capture before native stage", () =>
        {
            using var f = new DispatchFixture(); PendingRead read = f.Enqueue(); f.Pump();
            f.Safety = DeveloperSafety.Unsafe; f.Pump(); Equal(WireError.ObservationRefused, read.Completion.Result.Error);
            True(!f.Capture.Active); f.Backend.Whole(); f.Backend.Offer(); Equal(0, f.Backend.Reads);
        });
    }
    private sealed class Fixture
    {
        internal readonly Scope Scope = new Scope(Ids.New("s"), Ids.New("r"), mapView ? Ids.New("g") : null);
        internal readonly TransitionFence Fence = new TransitionFence(new PendingReads());
        internal readonly Backend Backend;
        internal readonly ScreenshotCapture Capture;
        internal long Clock = 10;
        internal bool Live = true;
        internal JObject? Result;
        internal WireError? Error;
        internal Fixture()
        {
            Backend = new Backend(Scope, mapView); Fence.Observations.Register(Backend.P);
            Capture = new ScreenshotCapture(Backend, Fence, () => Clock);
        }
        internal ScreenshotCapture.Flight Start() => Capture.Start(Scope, () => Live, result => Result = result, error => Error = error);
        internal void Offer() { Backend.Whole(); Backend.Offer(); }
    }
    private sealed class Backend : IScreenshotBackend, ICaptureTicket
    {
        internal readonly RenderProvenance P = new RenderProvenance();
        internal Scope Scope;
        internal FrameGeometry Frame = new FrameGeometry(10, 2, 2, 1);
        internal int Arms, Cancels, Reads, Completions, Releases;
        internal bool FencePassed = true, WrongLength, Witness = true;
        internal WireError? Refusal;
        internal Action? OnRead, OnRelease;
        private Action<ICaptureTicket>? consume;
        private CaptureRequestStamp? request;
        internal Backend(Scope scope, bool map = false)
        { Scope = scope; Frame = new FrameGeometry(10, 2, 2, 1, map); }
        public FrameGeometry Geometry => Frame;
        public CaptureRequestStamp Arm(Action<ICaptureTicket> callback)
        { Arms++; consume = callback; return request = P.Request(Scope, Frame); }
        public void Cancel() { Cancels++; consume = null; P.Invalidate(ObservationInvalidation.All); }
        public byte[] ReadRgb(CaptureSize size) { Reads++; OnRead?.Invoke(); return new byte[WrongLength ? 1 : size.RawBytes]; }
        internal void Whole()
        {
            Frame = new FrameGeometry(Frame.Frame + 1, Frame.Width, Frame.Height, Frame.Scale, Frame.MapView);
            P.Begin(Scope, Frame); P.Updated(); P.GuiBegin(); P.GuiEnd(true); P.CameraBegin(1, !Frame.MapView); P.CameraEnd(1, !Frame.MapView);
        }
        internal void Offer() => consume?.Invoke(this);
        private void Require() { if (!Witness) throw new WireException(WireError.NotReady); }
        bool ICaptureTicket.Witnessed => Witness;
        public HeldRender Hold() { Require(); return P.Hold(request!, Scope, Frame, Refusal); }
        bool ICaptureTicket.FencePassed { get { Require(); return FencePassed; } }
        public CompletedRenderStamp Release(HeldRender held)
        { Require(); Completions++; return P.Release(request!, held, Scope, Frame, Refusal, FencePassed); }
        public void CheckRelease(CompletedRenderStamp stamp) { Releases++; OnRelease?.Invoke(); P.CheckRelease(stamp, Scope, Frame, Refusal); }
    }
    private sealed class DispatchFixture : IDisposable
    {
        internal readonly PendingReads Pending = new PendingReads();
        internal readonly TransitionFence Fence;
        internal readonly ReadDispatcher<object> Dispatch;
        internal readonly Backend Backend;
        internal readonly ScreenshotCapture Capture;
        internal readonly SocketLifetime Lifetime;
        internal DeveloperSafety Safety = DeveloperSafety.Safe;
        private readonly Socket peer;
        private readonly object root = new object();
        internal DispatchFixture()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); peer.Connect(listener.LocalEndPoint!);
            Lifetime = new SocketLifetime(listener.Accept(), 10000);
            Fence = new TransitionFence(Pending);
            string session = Ids.New("s"); Backend = new Backend(new Scope(session, Ids.New("r"), null));
            Capture = new ScreenshotCapture(Backend, Fence, () => 10);
            Dispatch = new ReadDispatcher<object>(session, Pending, Fence, () => 10,
                (ReadMethod.Screenshot, new ScreenshotProducer<object>(Capture)));
            Fence.Observations.Register(Backend.P); Pump(); Backend.Scope = Dispatch.Generations.Scope!;
        }
        internal void Pump() => Dispatch.Pump(() => new ReadFrame<object>(root, null, null, true, Safety, Backend.Frame));
        internal PendingRead Enqueue()
        {
            var json = new JObject { ["v"] = 1, ["id"] = "capture", ["method"] = "screenshot",
                ["params"] = new JObject { ["scope"] = Dispatch.Generations.Scope!.Json() } };
            var read = new PendingRead(Request.Read(WireJson.Encode(json)), Lifetime); True(Pending.TryAdd(read)); return read;
        }
        public void Dispose() { Pending.Stop(); Lifetime.Dispose(); peer.Dispose(); }
    }
    private static void ValidatePng(byte[] png, CaptureSize size, byte[] rgb)
    {
        True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        int p = 8; byte[]? idat = null;
        foreach (string kind in new[] { "IHDR", "IDAT", "IEND" })
        {
            int length = (int)U32(png, p); p += 4; int start = p;
            Equal(kind, System.Text.Encoding.ASCII.GetString(png, p, 4)); p += 4;
            if (kind == "IHDR") { Equal((uint)size.Width, U32(png, p)); Equal((uint)size.Height, U32(png, p + 4)); Equal((byte)2, png[p + 9]); }
            if (kind == "IDAT") idat = png.AsSpan(p, length).ToArray();
            p += length;
            uint crc = uint.MaxValue;
            for (int i = start; i < p; i++) { crc ^= png[i]; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320U : crc >> 1; }
            Equal(crc ^ uint.MaxValue, U32(png, p)); p += 4;
        }
        Equal(png.Length, p);
        using var source = new MemoryStream(idat!); using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var decoded = new MemoryStream(); zlib.CopyTo(decoded); byte[] rows = decoded.ToArray();
        int stride = size.Width * 3; Equal(size.RawBytes + size.Height, rows.Length);
        for (int row = 0; row < size.Height; row++)
        {
            Equal((byte)0, rows[row * (stride + 1)]);
            True(rows.AsSpan(row * (stride + 1) + 1, stride).SequenceEqual(rgb.AsSpan((size.Height - row - 1) * stride, stride)));
        }
    }
    private static uint U32(byte[] bytes, int p) => (uint)(bytes[p] << 24 | bytes[p + 1] << 16 | bytes[p + 2] << 8 | bytes[p + 3]);
    private static void Error(WireError code, Action run)
    { try { run(); } catch (WireException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static void Test(string name, Action run)
    { try { run(); passed++; } catch (Exception e) { failed++; Console.Error.WriteLine((mapView ? "map: " : "menu: ") + name + ": " + e); } }
}
