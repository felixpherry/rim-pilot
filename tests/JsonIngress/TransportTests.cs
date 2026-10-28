using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Protocol
{
    internal sealed partial class SocketLifetime
    {
        internal Action? TestReadable;
        internal void TestCheck() => Check(null);
        internal bool TestReceiving { get { lock (gate) return receiving; } }
        partial void ObserveReadable() { TestReadable?.Invoke(); }
    }

    internal sealed partial class LoopbackTransport
    {
        internal static Action<LoopbackTransport, string>? TestHook;
        internal static Action<bool, Socket>? TestRateHook;
        internal int TestListenerPort => ((IPEndPoint)listener.LocalEndPoint!).Port;
        partial void Observe(string point) { TestHook?.Invoke(this, point); }
        partial void ObserveRate(bool admitted, Socket socket) { TestRateHook?.Invoke(admitted, socket); }
    }
}

[SupportedOSPlatform("linux")]
internal static class TransportTests
{
    private static int passed, failed;
    private static string? filter;
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static int Run(string? nameFilter = null)
    {
        filter = nameFilter;
        passed = failed = 0;
        Test("private publication, exact auth, typed dispatch and unchanged-owned shutdown", async () =>
        {
            using var f = new Fixture();
            using var peer = await f.Connect();
            await f.Auth(peer);
            await Write(peer, RequestJson("health", "h"));
            PendingRead work = await Take(f.Transport);
            Equal(ReadMethod.Health, work.Request.Method);
            True(work.IsLive && work.TryComplete(new JObject { ["state"] = "bootstrap" }));
            JObject response = ResponseContracts.Read(await Read(peer), ReadMethod.Health, "h");
            Equal("bootstrap", (string)response["data"]!["state"]!);
            f.Transport.Dispose();
            True(f.Transport.WorkersStopped && f.Transport.ActiveConnections == 0 && f.Transport.Pending.Count == 0);
            True(!File.Exists(f.RecordPath));
            await Closed(peer);
            True(!f.Transport.ToString().Contains(f.Descriptor.Token, StringComparison.Ordinal));
        });
        foreach (string kind in new[] { "wrong token", "wrong session", "wrong version", "not auth", "bad json", "token id", "extra property" })
            Test("uniform pre-auth failure: " + kind, async () =>
            {
                using var f = new Fixture();
                using var peer = await f.Connect();
                JObject auth = f.AuthJson();
                if (kind == "wrong token") auth["params"]!["token"] = new string('0', 64);
                if (kind == "wrong session") auth["params"]!["session"] = Ids.New("s");
                if (kind == "wrong version") auth["v"] = 2;
                if (kind == "not auth") auth = RequestJson("health");
                if (kind == "token id") auth["id"] = f.Descriptor.Token;
                if (kind == "extra property") auth["raw"] = "private-marker";
                await Write(peer, kind == "bad json" ? Encoding.UTF8.GetBytes("{\"token\":\"" + f.Descriptor.Token) : WireJson.Encode(auth));
                Equal("{\"v\":1,\"ok\":false,\"error\":{\"code\":\"authentication_failed\"}}", Encoding.UTF8.GetString(await Read(peer)));
                True(f.Transport.Pending.Count == 0);
                await Closed(peer);
            });
        foreach (uint length in new uint[] { 0, 65537, uint.MaxValue })
            Test("pre-auth invalid header has uniform error before allocation " + length, async () =>
            {
                using var f = new Fixture(); using var peer = await f.Connect();
                await peer.GetStream().WriteAsync(Header(length));
                Equal("authentication_failed", Error(await Read(peer)));
                await Closed(peer);
            });
        foreach (uint length in new uint[] { 0, 65537, uint.MaxValue })
            Test("post-auth invalid length never dispatches: " + length, async () =>
            {
                using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
                // Header only: oversized allocation/body read would stall instead of rejecting.
                await peer.GetStream().WriteAsync(Header(length));
                Equal("invalid_frame", Error(await Read(peer)));
                Equal(0, f.Transport.Pending.Count); await Closed(peer);
            });
        foreach (bool authenticated in new[] { false, true })
            foreach (int cut in new[] { 0, 1, 3, 4, -1 })
                Test("half-close truncated frame releases admission without dispatch: " + authenticated + "/" + cut, async () =>
                {
                    using var f = new Fixture(); using var peer = await f.Connect();
                    await Until(() => f.Transport.ActiveConnections == 1);
                    if (authenticated) await f.Auth(peer);
                    byte[] body = WireJson.Encode(authenticated ? RequestJson("health") : f.AuthJson());
                    byte[] frame = Header((uint)body.Length).Concat(body).ToArray();
                    int count = cut < 0 ? frame.Length - 1 : cut;
                    if (count != 0) await peer.GetStream().WriteAsync(frame.AsMemory(0, count));
                    // Keep read side open: EOF must interrupt receive, not rely on client disposal
                    // or auth/request timeout. Terminal error delivery after EOF is best-effort.
                    peer.Client.Shutdown(SocketShutdown.Send);
                    await Until(() => f.Transport.ActiveConnections == 0, "truncated receive cleanup");
                    True(f.Transport.Pending.Count == 0 && !f.Transport.Pending.TryTake(out _));
                    using var next = await f.Connect(); await f.Auth(next);
                    Equal(0, f.Transport.Pending.Count);
                });
        Test("partial auth header/body accepted without per-byte deadline renewal", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect();
            byte[] bytes = WireJson.Encode(f.AuthJson());
            byte[] frame = Header((uint)bytes.Length).Concat(bytes).ToArray();
            foreach (byte b in frame) await peer.GetStream().WriteAsync(new[] { b });
            ResponseContracts.Read(await Read(peer), ReadMethod.Auth, "a");
        });
        foreach (string method in new[] { "auth", "click", "key", "text", "scroll", "drag", "pause", "select",
            "camera", "save", "load", "debug", "eval", "action", "grant", "reflect", "unknown", "input.click", "reflection.get" })
            Test("repeated auth and forbidden/unknown methods never dispatch: " + method, async () =>
            {
                using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
                await Write(peer, method == "auth" ? f.AuthJson() : RequestJson(method));
                string expected = method == "auth" ? "invalid_request" : method == "unknown" || method == "input.click" || method == "reflection.get" ? "unsupported_method" : "forbidden_method";
                Equal(expected, Error(await Read(peer))); True(f.Transport.Pending.Count == 0); await Closed(peer);
            });
        foreach (string method in new[] { "click", "key", "text", "scroll", "drag", "pause", "select", "camera",
            "save", "load", "debug", "eval", "action", "grant", "reflect" })
            Test("read-only denial recovers without latent dispatch: explicit " + method, async () =>
            {
                using var f = new Fixture();
                JObject request = RequestJson(method, "denial");
                request["params"] = new JObject { ["private-marker"] = f.Descriptor.Token,
                    ["target"] = "private-target", ["value"] = new JObject { ["method"] = "health" } };
                await DeniedThenHealthy(f, request, WireError.ForbiddenMethod);
            });
        foreach (string method in new[] { "input.click", "ui.click", "pawn.draft", "game.pause", "save.load",
            "debug.eval", "reflection.get", "GetType", "invoke", "set", "camera.move", "grant.create",
            "Click", "ACTION", " pause", "pause ", "pause\u0000", "pa\u200buse", "\uff50ause", "" })
            Test("read-only denial recovers without latent dispatch: alias " + method, async () =>
            {
                using var f = new Fixture();
                JObject request = RequestJson(method, "denial");
                request["params"] = new JObject { ["private-marker"] = f.Descriptor.Token };
                await DeniedThenHealthy(f, request, WireError.UnsupportedMethod);
            });
        foreach (string method in new[] { "health", "capabilities", "summary", "pawns.list", "pawn.get",
            "things.list", "resources", "ui", "screenshot", "events.read" })
            Test("read-only denial recovers without latent dispatch: smuggled action in " + method, async () =>
            {
                using var f = new Fixture();
                JObject request = RequestJson(method, "denial");
                var args = (JObject)request["params"]!;
                if (method != "health" && method != "capabilities")
                    args["scope"] = new Scope(f.Descriptor.Session, Ids.New("r"), Ids.New("g"), Ids.New("m")).Json();
                if (method == "pawns.list" || method == "things.list" || method == "events.read") args["limit"] = 1;
                if (method == "pawn.get") args["pawn"] = Ids.New("p");
                // First prove the base read is well-formed; a missing required field must not
                // accidentally make the subsequent action-smuggling assertion pass.
                Request.Read(WireJson.Encode(request));
                args["action"] = new JObject { ["method"] = "pause", ["private-marker"] = f.Descriptor.Token };
                await DeniedThenHealthy(f, request, WireError.InvalidRequest);
            });
        foreach (string method in new[] { "health", "capabilities" })
            Test("bootstrap extra params never dispatch: " + method, async () =>
            {
                using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
                JObject request = RequestJson(method); request["params"] = new JObject { ["raw"] = "private-marker" };
                await Write(peer, request); Equal("invalid_request", Error(await Read(peer)));
                Equal(0, f.Transport.Pending.Count); await Closed(peer);
            });
        Test("post-auth secret correlation ID never echoed", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, RequestJson("health", f.Descriptor.Token));
            string error = Encoding.UTF8.GetString(await Read(peer));
            True(!error.Contains(f.Descriptor.Token, StringComparison.Ordinal) && !error.Contains("\"id\"", StringComparison.Ordinal));
        });
        Test("strict post-auth JSON errors never dispatch or echo parser text", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, Encoding.UTF8.GetBytes("{\"v\":1,\"v\":1,\"private-marker\":\"" + f.Descriptor.Token + "\"}"));
            string error = Encoding.UTF8.GetString(await Read(peer));
            True(error.Contains("invalid_json", StringComparison.Ordinal) && !error.Contains("private-marker", StringComparison.Ordinal));
            True(f.Transport.Pending.Count == 0);
        });
        Test("wrong request session never enters dispatcher", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            JObject request = RequestJson("summary");
            request["params"] = new JObject { ["scope"] = new Scope(Ids.New("s"), Ids.New("r")).Json() };
            await Write(peer, request);
            Equal("stale_scope", Error(await Read(peer))); True(f.Transport.Pending.Count == 0);
        });
        Test("producer response validates typed shape before any success bytes", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, RequestJson("health")); PendingRead work = await Take(f.Transport);
            True(work.TryComplete(new JObject { ["state"] = "bootstrap", ["private-marker"] = "must-not-serialize" }));
            byte[] bytes = await Read(peer);
            Equal("invalid_request", Error(bytes));
            True(!Encoding.UTF8.GetString(bytes).Contains("private-marker", StringComparison.Ordinal));
            await Closed(peer);
        });
        Test("framing reserves validated byte count before body allocation/read", async () =>
        {
            using var stream = new MemoryStream(Header(65536));
            bool charged = false;
            try
            {
                await Framing.ReadAsync(stream, WireLimits.RequestBytes, CancellationToken.None, reserveBytes: bytes =>
                { Equal(65540, bytes); charged = true; throw new WireException(WireError.RateLimited); });
                throw new Exception("Expected byte budget refusal.");
            }
            catch (WireException error) { Equal(WireError.RateLimited, error.Code); }
            True(charged && stream.Position == 4);
        });
        Test("request absolute deadline cancels dequeued work and closes socket", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, RequestJson("health")); PendingRead work = await Take(f.Transport);
            await Closed(peer, WireLimits.RequestTimeoutMs + 2000);
            True(!work.IsLive && work.Cancellation.IsCancellationRequested && !work.TryComplete(new JObject { ["state"] = "bootstrap" }));
            await Until(() => f.Transport.Pending.Count == 0);
        });
        Test("disconnect cancels queued intent promptly without latent backlog", async () =>
        {
            using var f = new Fixture(); var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, RequestJson("health"));
            await Until(() => f.Transport.Pending.Count == 1);
            peer.Dispose();
            await Until(() => f.Transport.Pending.Count == 0 && f.Transport.ActiveConnections == 0);
            True(!f.Transport.Pending.TryTake(out _));
        });
        Test("disconnect cancels already-dequeued intent", async () =>
        {
            using var f = new Fixture(); var peer = await f.Connect(); await f.Auth(peer);
            await Write(peer, RequestJson("health")); PendingRead work = await Take(f.Transport);
            peer.Dispose(); await Until(() => work.Cancellation.IsCancellationRequested);
            True(!work.TryReject(WireError.NotReady));
        });
        Test("one disconnected reader cannot cancel another admitted request", async () =>
        {
            using var f = new Fixture(); using var doomed = await f.Connect(); using var healthy = await f.Connect();
            await f.Auth(doomed); await f.Auth(healthy);
            await Write(doomed, RequestJson("health", "doomed")); PendingRead cancelled = await Take(f.Transport);
            await Write(healthy, RequestJson("health", "healthy")); PendingRead live = await Take(f.Transport);
            doomed.Dispose(); await Until(() => cancelled.Cancellation.IsCancellationRequested);
            True(!cancelled.TryComplete(new JObject { ["state"] = "bootstrap" }) && live.IsLive);
            True(live.TryComplete(new JObject { ["state"] = "bootstrap" }));
            ResponseContracts.Read(await Read(healthy), ReadMethod.Health, "healthy");
            Equal(0, f.Transport.Pending.Count);
        });
        Test("pipelining cannot dispatch a second outstanding request", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            byte[] bytes = WireJson.Encode(RequestJson("health"));
            byte[] frame = Header((uint)bytes.Length).Concat(bytes).ToArray();
            await peer.GetStream().WriteAsync(frame.Concat(frame).ToArray());
            // Both frames arrive together: pre-dispatch rejects; if first entered queue before second
            // arrived, release it and the pre-publication check still rejects the buffered pipeline.
            await Task.Delay(100);
            if (f.Transport.Pending.TryTake(out PendingRead? work)) work!.TryReject(WireError.NotReady);
            Equal("invalid_request", Error(await Read(peer))); await Closed(peer);
        });
        Test("admission slots cap active sockets while additional peers wait in backlog", async () =>
        {
            using var f = new Fixture();
            var peers = new TcpClient[WireLimits.MaxConnections + 1];
            try
            {
                for (int i = 0; i < peers.Length; i++) peers[i] = await f.Connect();
                await Until(() => f.Transport.ActiveConnections == WireLimits.MaxConnections);
                await Write(peers[4], f.AuthJson());
                await Task.Delay(100);
                True(peers[4].Available == 0 && f.Transport.ActiveConnections == WireLimits.MaxConnections);
                f.Transport.Dispose();
                True(f.Transport.WorkersStopped && f.Transport.ActiveConnections == 0);
            }
            finally { foreach (TcpClient? peer in peers) peer?.Dispose(); }
        });
        Test("per-connection burst original split frames retain complete rate_limited response", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            bool limited = false;
            for (int i = 0; i < 40 && !limited; i++)
            {
                await Write(peer, RequestJson("health"));
                var read = Read(peer);
                while (!read.IsCompleted)
                {
                    if (f.Transport.Pending.TryTake(out PendingRead? work)) work!.TryReject(WireError.NotReady);
                    await Task.Delay(1);
                }
                string code = Error(await read);
                limited = code == "rate_limited";
                True(limited || code == "not_ready");
            }
            True(limited); await Closed(peer);
        });
        foreach (bool fullFrame in new[] { false, true })
            Test("per-connection burst rejects before remaining header/body: " + (fullFrame ? "unread full frame" : "first byte only"), async () =>
            {
                using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
                if (!fullFrame) peer.NoDelay = true; // Staging must not turn Nagle delay into rate refill.
                int attempts = 0, rejected = 0, unread = 0;
                LoopbackTransport.TestRateHook = (admitted, socket) =>
                {
                    if (!admitted)
                    {
                        unread = socket.Available;
                        Volatile.Write(ref rejected, 1);
                    }
                    Interlocked.Increment(ref attempts);
                };
                try
                {
                    int dispatched = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        byte[] body = WireJson.Encode(RequestJson("health"));
                        byte[] frame = Header((uint)body.Length).Concat(body).ToArray();
                        if (fullFrame) await Write(peer, body); // Preserve original split header/body writes.
                        else await peer.GetStream().WriteAsync(frame.AsMemory(0, 1));
                        await Until(() => Volatile.Read(ref attempts) == i + 1, "rate decision");
                        if (Volatile.Read(ref rejected) != 0)
                        {
                            // The denied frame never reaches the queue, even though its header/body
                            // is still unread (or deliberately withheld by the staged control).
                            Equal(0, f.Transport.Pending.Count);
                            True(!f.Transport.Pending.TryTake(out _));
                            // Auth spent the first credit; token refill can permit more than 15 reads.
                            True(dispatched >= WireLimits.RequestBurst - 1);
                            True((await Read(peer)).SequenceEqual(WireJson.Encode(Responses.Error(null, WireError.RateLimited))));
                            if (!fullFrame) Equal(0, unread);
                            else True(unread >= 3);
                            await Closed(peer);
                            await Until(() => f.Transport.ActiveConnections == 0);
                            True(!f.Transport.Pending.TryTake(out _));
                            Console.WriteLine("rate rejection: " + (fullFrame ? "unread frame" : "withheld frame") +
                                ", error complete, dispatched " + dispatched);
                            return;
                        }
                        if (!fullFrame) await peer.GetStream().WriteAsync(frame.AsMemory(1));
                        PendingRead work = await Take(f.Transport);
                        True(work.TryReject(WireError.NotReady)); dispatched++;
                        Equal("not_ready", Error(await Read(peer)));
                    }
                    throw new Exception("Burst did not reach rate rejection.");
                }
                finally { LoopbackTransport.TestRateHook = null; }
            });
        Test("auth absolute deadline closes slow partial headers", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect();
            await peer.GetStream().WriteAsync(new byte[] { 0 });
            await Task.Delay(1100);
            await peer.GetStream().WriteAsync(new byte[] { 0 });
            var elapsed = Stopwatch.StartNew(); await Closed(peer, 2500);
            True(elapsed.ElapsedMilliseconds < 1800);
        });
        Test("authenticated partial frame shares request deadline across header/body", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await peer.GetStream().WriteAsync(Header(50));
            await Task.Delay(1700);
            await peer.GetStream().WriteAsync(new byte[] { (byte)'{' });
            var elapsed = Stopwatch.StartNew(); await Closed(peer, 3000);
            True(elapsed.ElapsedMilliseconds < 2200);
        });
        Test("idle authenticated peer closes within finite idle deadline", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
            await Closed(peer, WireLimits.IdleTimeoutMs + 2000);
        });
        Test("endpoint slow screenshot reader releases connection within write deadline", async () =>
        {
            using var f = new Fixture(); using var peer = await f.Connect();
            peer.ReceiveBufferSize = 1024;
            await f.Auth(peer);
            var scope = new Scope(f.Descriptor.Session, Ids.New("r"));
            JObject request = RequestJson("screenshot"); request["params"] = new JObject { ["scope"] = scope.Json() };
            await Write(peer, request); PendingRead work = await Take(f.Transport);
            var stamp = CompletedRenderStamp.Complete(new CaptureRequestStamp(scope, 1, 11),
                new RenderEvidence(scope, 1, 12, true, true), scope, 1, null);
            // Synthetic bytes/evidence only: exercises response profile/write bounds, not PNG or rendering.
            True(work.TryComplete(Responses.Capture(stamp, 1920, 1080, 1, new byte[WireLimits.CapturePngBytes])));
            await UntilLong(() => f.Transport.ActiveConnections == 0, 4500);
            True(!work.IsLive && f.Transport.Pending.Count == 0);
        });
        Test("watchdog never mistakes bytes drained by active receive for peer EOF", () =>
        {
            using var pair = new SocketPair();
            using var lifetime = new SocketLifetime(pair.Server, 1000);
            lifetime.BeginReceive();
            pair.Client.Send(new byte[] { 42 });
            int drained = 0;
            lifetime.TestReadable = () =>
            {
                // Reproduce the old TOCTOU exactly: readability was true, then the active
                // receiver drains the byte before watchdog's Available test. Peer stays open.
                Equal(1, pair.Server.Receive(new byte[1]));
                drained++;
            };
            lifetime.TestCheck();
            True(!lifetime.Cancellation.IsCancellationRequested);
            Equal(0, drained); // Guard must prevent the racy EOF inspection, not retry it.
            Equal(1, pair.Server.Receive(new byte[1]));
            lifetime.EndReceive(); lifetime.TestReadable = null;
            lifetime.TestCheck(); True(!lifetime.Cancellation.IsCancellationRequested);
            pair.Client.Shutdown(SocketShutdown.Send);
            lifetime.TestCheck(); True(lifetime.Cancellation.IsCancellationRequested);
            return Task.CompletedTask;
        });
        Test("socket-close backstop aborts pending write to slow reader", async () =>
        {
            using var pair = new SocketPair();
            pair.Server.SendBufferSize = 1024; pair.Client.ReceiveBufferSize = 1024;
            using var lifetime = new SocketLifetime(pair.Server, 150);
            using var stream = new NetworkStream(pair.Server, false);
            var watch = Stopwatch.StartNew();
            try
            {
                // Deliberately pass None: cancellation cooperation is not what this test proves.
                await stream.WriteAsync(new byte[WireLimits.CaptureResponseBytes], 0, WireLimits.CaptureResponseBytes, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                throw new Exception("Slow write unexpectedly completed.");
            }
            catch (IOException) { }
            catch (SocketException) { }
            True(lifetime.Cancellation.IsCancellationRequested && watch.ElapsedMilliseconds < 1500);
        });
        Test("bounded pending queue prunes cancellation and rejects late completion", () =>
        {
            using var pair = new SocketPair(); using var lifetime = new SocketLifetime(pair.Server, 1000);
            var queue = new PendingReads();
            for (int i = 0; i < WireLimits.PendingRequests; i++) True(queue.TryAdd(new PendingRead(Request.Read(WireJson.Encode(RequestJson("health"))), lifetime)));
            True(!queue.TryAdd(new PendingRead(Request.Read(WireJson.Encode(RequestJson("health"))), lifetime)));
            lifetime.Close(); True(!queue.TryTake(out _) && queue.Count == 0);
            queue.Stop(); True(!queue.TryAdd(new PendingRead(Request.Read(WireJson.Encode(RequestJson("health"))), lifetime)));
            return Task.CompletedTask;
        });
        Test("byte buckets have independent finite burst/sustained ceilings", () =>
        {
            var bytes = new TransportBucket(WireLimits.ResponseBytesPerSecond, WireLimits.ResponseByteBurst);
            True(bytes.Take(WireLimits.ResponseByteBurst)); True(!bytes.Take(4096)); True(!bytes.Take(-1));
            var admissions = new TransportBucket(WireLimits.AdmissionsPerSecond, WireLimits.AdmissionBurst);
            for (int i = 0; i < WireLimits.AdmissionBurst; i++) True(admissions.Take(1));
            True(!admissions.Take(1)); return Task.CompletedTask;
        });
        foreach (string boundary in new[] { "listener-ready", "descriptor-published", "worker-started" })
            Test("startup failure cleans listener/workers/owned publication at " + boundary, async () =>
            {
                string root = Directory.CreateTempSubdirectory("rim-pilot-transport-start-").FullName;
                File.SetUnixFileMode(root, Private);
                int port = 0; LoopbackTransport? observed = null;
                try
                {
                    LoopbackTransport.TestHook = (transport, point) =>
                    {
                        if (point != boundary) return;
                        observed = transport; port = transport.TestListenerPort;
                        throw new IOException("private-marker " + root);
                    };
                    try { using var unexpected = LoopbackTransport.Start(root); throw new Exception("Expected startup refusal."); }
                    catch (TransportException error) { True(!error.ToString().Contains(root, StringComparison.Ordinal) && error.InnerException == null); }
                    True(port != 0 && observed!.WorkersStopped && observed.ActiveConnections == 0);
                    using var probe = new TcpClient();
                    try { await probe.ConnectAsync(IPAddress.Loopback, port); throw new Exception("Listener leaked."); }
                    catch (SocketException) { }
                    True(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0);
                }
                finally { LoopbackTransport.TestHook = null; Directory.Delete(root, true); }
            });
        Test("native publication refusal closes already-listening socket without secret artifact", async () =>
        {
            string root = Directory.CreateTempSubdirectory("rim-pilot-transport-refuse-").FullName;
            File.SetUnixFileMode(root, Private);
            int port = 0;
            try
            {
                LoopbackTransport.TestHook = (transport, point) =>
                {
                    if (point != "listener-ready") return;
                    port = transport.TestListenerPort;
                    File.SetUnixFileMode(root + "/rim-pilot/v1", (UnixFileMode)511);
                };
                try { using var unexpected = LoopbackTransport.Start(root); throw new Exception("Expected native refusal."); }
                catch (TransportException) { }
                True(port != 0 && Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0);
                using var probe = new TcpClient();
                try { await probe.ConnectAsync(IPAddress.Loopback, port); throw new Exception("Listener leaked."); }
                catch (SocketException) { }
            }
            finally { LoopbackTransport.TestHook = null; Directory.Delete(root, true); }
        });
        Test("shutdown preserves replaced descriptor and foreign files", async () =>
        {
            using var f = new Fixture();
            File.Delete(f.RecordPath);
            File.WriteAllText(f.RecordPath, "replacement", Encoding.UTF8);
            File.SetUnixFileMode(f.RecordPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string sentinel = Path.Combine(Path.GetDirectoryName(f.RecordPath)!, "foreign"); File.WriteAllText(sentinel, "keep");
            f.Transport.Dispose(); f.Transport.Dispose();
            Equal("replacement", File.ReadAllText(f.RecordPath)); Equal("keep", File.ReadAllText(sentinel));
            True(f.Transport.WorkersStopped); await Task.CompletedTask;
        });
        Test("concurrent accept/shutdown race returns or closes every owned peer", async () =>
        {
            using (var warm = new Fixture()) { using var peer = await warm.Connect(); await warm.Auth(peer); }
            int baseline = Directory.GetFileSystemEntries("/proc/self/fd").Length;
            for (int round = 0; round < 12; round++)
            {
                using var f = new Fixture();
                Task[] connects = Enumerable.Range(0, 8).Select(async _ =>
                {
                    using var peer = new TcpClient();
                    try { await peer.ConnectAsync(IPAddress.Loopback, f.Transport.Port).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (SocketException) { }
                    catch (TimeoutException) { }
                }).ToArray();
                await Task.Run(() => f.Transport.Dispose());
                await Task.WhenAll(connects);
                True(f.Transport.WorkersStopped && f.Transport.ActiveConnections == 0);
            }
            True(Directory.GetFileSystemEntries("/proc/self/fd").Length <= baseline + 3);
        });
        Test("repeated startup/shutdown keeps worker and fd counts bounded", async () =>
        {
            // Warm socket/async runtime before fd baseline.
            using (var warm = new Fixture()) { using var peer = await warm.Connect(); await warm.Auth(peer); }
            int baseline = Directory.GetFileSystemEntries("/proc/self/fd").Length;
            for (int i = 0; i < 12; i++)
            {
                using var f = new Fixture(); using var peer = await f.Connect(); await f.Auth(peer);
                f.Transport.Dispose(); True(f.Transport.WorkersStopped && f.Transport.ActiveConnections == 0);
            }
            True(Directory.GetFileSystemEntries("/proc/self/fd").Length <= baseline + 3);
        });
        Console.WriteLine($"transport: {passed} passed, {failed} failed (.NET/host sockets only; Mono/game NOT RUN)");
        return passed == 0 ? 1 : failed;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal readonly LoopbackTransport Transport;
        internal readonly DiscoveryDescriptor Descriptor;
        internal string RecordPath => Root + "/rim-pilot/v1/" + Descriptor.RecordName;
        internal Fixture()
        {
            Root = Directory.CreateTempSubdirectory("rim-pilot-transport-").FullName;
            File.SetUnixFileMode(Root, Private);
            Transport = LoopbackTransport.Start(Root);
            using var registry = LinuxPrivateDirectory.Open(Root);
            Descriptor = registry.ReadRecord(Transport.Session + ".json", new DiscoveryBudget());
            Equal(Transport.Port, Descriptor.Port);
        }
        internal JObject AuthJson() => new JObject { ["v"] = 1, ["id"] = "a", ["method"] = "auth", ["params"] = new JObject { ["session"] = Descriptor.Session, ["token"] = Descriptor.Token } };
        internal async Task<TcpClient> Connect()
        { var peer = new TcpClient(); await peer.ConnectAsync(IPAddress.Loopback, Transport.Port); return peer; }
        internal async Task Auth(TcpClient peer)
        { await Write(peer, AuthJson()); ResponseContracts.Read(await Read(peer), ReadMethod.Auth, "a"); }
        public void Dispose() { Transport.Dispose(); Directory.Delete(Root, true); }
    }
    // Exercises the actual listener/auth/parser/queue boundary, not Request.Read alone.
    // Closed denial peer + zero active sockets fences its worker before the valid request;
    // no sleeps/retries or observation producers can hide a wrongly enqueued intent.
    private static async Task DeniedThenHealthy(Fixture f, JObject request, WireError expected)
    {
        using (var denied = await f.Connect())
        {
            await f.Auth(denied);
            await Write(denied, request);
            Task<byte[]> reply = Read(denied);
            await Until(() => reply.IsCompleted || f.Transport.Pending.Count != 0, "denial or forbidden dispatch");
            Equal(0, f.Transport.Pending.Count);
            string response = Encoding.UTF8.GetString(await reply);
            Equal(Responses.Error(null, expected).ToString(Newtonsoft.Json.Formatting.None), response);
            True(!response.Contains(f.Descriptor.Token, StringComparison.Ordinal)
                && !response.Contains("private-marker", StringComparison.Ordinal)
                && !response.Contains("private-target", StringComparison.Ordinal));
            Equal(0, f.Transport.Pending.Count);
            await Closed(denied);
        }
        await Until(() => f.Transport.ActiveConnections == 0, "denied peer worker released");
        True(!f.Transport.Pending.TryTake(out _));
        using var healthy = await f.Connect();
        await f.Auth(healthy);
        await Write(healthy, RequestJson("health", "recovery"));
        PendingRead work = await Take(f.Transport);
        Equal("recovery", work.Request.Id);
        Equal(ReadMethod.Health, work.Request.Method);
        True(work.TryComplete(new JObject { ["state"] = "bootstrap" }));
        ResponseContracts.Read(await Read(healthy), ReadMethod.Health, "recovery");
        healthy.Dispose();
        await Until(() => f.Transport.ActiveConnections == 0 && f.Transport.Pending.Count == 0, "healthy peer released");
        True(!f.Transport.Pending.TryTake(out _));
    }
    private sealed class SocketPair : IDisposable
    {
        internal readonly Socket Server, Client;
        internal SocketPair()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            Client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); Client.Connect(listener.LocalEndPoint!);
            Server = listener.Accept();
        }
        public void Dispose() { Server.Dispose(); Client.Dispose(); }
    }
    private static JObject RequestJson(string method, string id = "h") => new JObject { ["v"] = 1, ["id"] = id, ["method"] = method, ["params"] = new JObject() };
    private static byte[] Header(uint size) => new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size };
    private static Task Write(TcpClient peer, JObject value) => Write(peer, WireJson.Encode(value));
    private static Task Write(TcpClient peer, byte[] bytes) => Framing.WriteAsync(peer.GetStream(), bytes, WireLimits.RequestBytes, CancellationToken.None);
    private static Task<byte[]> Read(TcpClient peer) => Framing.ReadAsync(peer.GetStream(), WireLimits.ResponseBytes, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    private static string Error(byte[] bytes) => (string)JObject.Parse(Encoding.UTF8.GetString(bytes))["error"]!["code"]!;
    private static async Task<PendingRead> Take(LoopbackTransport transport)
    {
        PendingRead? result = null;
        await Until(() => transport.Pending.TryTake(out result)); return result!;
    }
    private static Task Until(Func<bool> condition, string boundary = "condition") => UntilLong(condition, 2000, boundary);
    private static async Task UntilLong(Func<bool> condition, int milliseconds, string boundary = "condition")
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > milliseconds) throw new Exception("Condition deadline exceeded: " + boundary); await Task.Delay(2); }
    }
    private static async Task Closed(TcpClient peer, int milliseconds = 2000)
    {
        try
        {
            int n = await peer.GetStream().ReadAsync(new byte[1], 0, 1).WaitAsync(TimeSpan.FromMilliseconds(milliseconds));
            Equal(0, n);
        }
        catch (IOException) { }
    }
    private static void Test(string name, Func<Task> action)
    {
        if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        try { action().WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL transport " + name + ": " + error.GetType().Name + " " + error.Message + (error is WireException wire ? " " + wire.Code : "") + "\n" + error.StackTrace); }
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Values differ."); }
}
