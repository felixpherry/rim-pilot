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
using RimPilot.Cli;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

[SupportedOSPlatform("linux")]
internal static class SessionDiscoveryTests
{
    private static int passed, failed;
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static int Run()
    {
        Test("empty registry is explicit, no fallback scanning", async () =>
        {
            using var f = new Fixture();
            using (var discovery = f.Scan()) Equal(0, ((JArray)(await discovery.ListAsync())["sessions"]!).Count);
            using (var discovery = f.Scan()) await Reject(SessionError.NoLiveSession, () => discovery.ConnectAsync(null));
            var result = await f.Command("sessions");
            Equal(69, result.Exit); Equal("rim-pilot: no_live_session.\n", result.Error);
        });
        Test("live CLI listing is authenticated and secret/path/endpoint free", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            DiscoveryDescriptor descriptor = f.Read(server.Session);
            var result = await f.Command("sessions");
            Equal(0, result.Exit); Equal("", result.Error);
            JObject item = (JObject)JObject.Parse(result.Output)["sessions"]![0]!;
            Equal(3, item.Properties().Count()); Equal(server.Session, (string)item["session"]!); Equal("live", (string)item["status"]!);
            True(!result.Output.Contains(descriptor.Token, StringComparison.Ordinal) && !result.Output.Contains(f.Root, StringComparison.Ordinal));
            True(item["port"] == null && item["host"] == null);
        });
        Test("selection transfers same authenticated socket, not second connection", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            using var discovery = f.Scan(); using var peer = await discovery.ConnectAsync(null);
            using var stream = peer.BeginRequest();
            await Framing.WriteAsync(stream, WireJson.Encode(new JObject { ["v"] = 1, ["id"] = "h", ["method"] = "health", ["params"] = new JObject() }),
                WireLimits.RequestBytes, peer.Lifetime.Cancellation);
            var watch = Stopwatch.StartNew(); PendingRead? work = null;
            while (!server.Pending.TryTake(out work)) { True(watch.ElapsedMilliseconds < 1000); await Task.Delay(2); }
            True(work!.TryComplete(new JObject { ["state"] = "bootstrap" }));
            ResponseContracts.Read(await Framing.ReadAsync(stream, WireLimits.ResponseBytes, peer.Lifetime.Cancellation), ReadMethod.Health, "h");
            Equal(server.Session, peer.Session); Equal(1, server.ActiveConnections);
            await Reject(SessionError.SessionUnavailable, () => discovery.ConnectAsync(server.Session));
        });
        Test("CLI pending response owns receive guard against watchdog EOF race", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            using var discovery = f.Scan(); using var peer = await discovery.ConnectAsync(server.Session);
            Task<JObject> response = peer.ReadBootstrapAsync(ReadMethod.Health);
            var watch = Stopwatch.StartNew(); PendingRead? work = null;
            while (!server.Pending.TryTake(out work)) { True(watch.ElapsedMilliseconds < 1000); await Task.Delay(2); }
            // Same socket, real CLI exchange, blocked waiting for a response. EOF polling
            // must not race its receive; the absolute deadline remains active throughout.
            True(peer.Lifetime.TestReceiving);
            peer.Lifetime.TestCheck(); True(!peer.Lifetime.Cancellation.IsCancellationRequested);
            True(work!.TryComplete(new JObject { ["state"] = "bootstrap" }));
            Equal("bootstrap", (string)(await response)["data"]!["state"]!);
            True(!peer.Lifetime.TestReceiving);
        });
        Test("two live instances require explicit selection, no first-match", async () =>
        {
            using var f = new Fixture(); using var a = LoopbackTransport.Start(f.Root); using var b = LoopbackTransport.Start(f.Root);
            using (var discovery = f.Scan()) await Reject(SessionError.SelectionRequired, () => discovery.ConnectAsync(null));
            Equal(0, a.ActiveConnections); Equal(0, b.ActiveConnections);
            using (var discovery = f.Scan()) using (var peer = await discovery.ConnectAsync(b.Session)) Equal(b.Session, peer.Session);
            using (var discovery = f.Scan()) Equal(2, ((JArray)(await discovery.ListAsync())["sessions"]!).Count(value => (string?)value["status"] == "live"));
            var selected = await f.Command("sessions", "--session", a.Session); Equal(0, selected.Exit);
            True(!selected.Output.Contains(b.Session, StringComparison.Ordinal));
        });
        Test("stale uncertain record plus live requires explicit selection; retain bytes", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            var stale = f.Add(ClosedPort()); byte[] original = File.ReadAllBytes(f.PathFor(stale.Session));
            using (var discovery = f.Scan()) await Reject(SessionError.SelectionRequired, () => discovery.ConnectAsync(null));
            using (var discovery = f.Scan())
            {
                JArray items = (JArray)(await discovery.ListAsync())["sessions"]!;
                Equal("unavailable", (string)items.Single(value => (string?)value["session"] == stale.Session)["status"]!);
            }
            using (var discovery = f.Scan()) await Reject(SessionError.SessionUnavailable, () => discovery.ConnectAsync(stale.Session));
            True(original.SequenceEqual(File.ReadAllBytes(f.PathFor(stale.Session))));
            using (var discovery = f.Scan()) using (var peer = await discovery.ConnectAsync(server.Session)) Equal(server.Session, peer.Session);
        });
        Test("conflicting endpoint records refused before any probe", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root); f.Add(server.Port);
            ExpectScanError(f, SessionError.ConflictingRecords); Equal(0, server.ActiveConnections);
            Equal(2, Directory.GetFiles(f.Registry, "*.json").Length); await Task.CompletedTask;
        });
        foreach (string kind in new[] { "alias", "duplicate identity", "bad JSON", "unsafe mode", "symlink" })
            Test("invalid/duplicate registry fails whole discovery: " + kind, async () =>
            {
                using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
                string target = f.PathFor(Ids.New("s"));
                if (kind == "symlink") File.CreateSymbolicLink(target, f.PathFor(server.Session));
                else
                {
                    File.WriteAllBytes(target, kind == "bad JSON" ? Encoding.UTF8.GetBytes("{}") : File.ReadAllBytes(f.PathFor(server.Session)));
                    File.SetUnixFileMode(target, kind == "unsafe mode" ? UnixFileMode.UserRead | UnixFileMode.GroupRead : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                ExpectScanError(f, SessionError.RegistryRejected); Equal(0, server.ActiveConnections);
                True(File.Exists(target)); await Task.CompletedTask;
            });
        Test("unknown explicit identity does not probe or reveal rejected value", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            using (var discovery = f.Scan()) await Reject(SessionError.SessionNotFound, () => discovery.ConnectAsync(Ids.New("s")));
            var result = await f.Command("sessions", "--session", "private-marker");
            Equal(69, result.Exit); Equal("", result.Output); Equal("rim-pilot: session_not_found.\n", result.Error); Equal(0, server.ActiveConnections);
        });
        foreach (string kind in new[] { "wrong session", "wrong token", "wrong version", "wrong id", "error", "duplicate key", "oversized", "truncated", "slow header", "slow body" })
            Test("probe rejects and retains uncertain descriptor: " + kind, async () =>
            {
                using var f = new Fixture(); using var listener = Listen();
                DiscoveryDescriptor descriptor = f.Add(((IPEndPoint)listener.LocalEndPoint!).Port);
                Task serve = Task.Run(async () =>
                {
                    using Socket socket = await listener.AcceptAsync(); using var stream = new NetworkStream(socket, false);
                    JObject request = JObject.Parse(Encoding.UTF8.GetString(await Framing.ReadAsync(stream, WireLimits.RequestBytes, CancellationToken.None)));
                    Equal(descriptor.Session, (string)request["params"]!["session"]!); Equal(descriptor.Token, (string)request["params"]!["token"]!);
                    if (kind.StartsWith("slow", StringComparison.Ordinal))
                    {
                        byte[] fragment = kind == "slow header" ? new byte[] { 0 } : new byte[] { 0, 0, 0, 99, (byte)'{' };
                        await stream.WriteAsync(fragment); await Task.Delay(1200); return;
                    }
                    if (kind == "oversized") { await stream.WriteAsync(new byte[] { 0, 4, 0, 1 }); return; }
                    if (kind == "truncated") { await stream.WriteAsync(new byte[] { 0, 0, 0, 10, (byte)'{' }); return; }
                    JObject response = Responses.Success("discovery", new JObject { ["session"] = descriptor.Session, ["protocol"] = 1 });
                    if (kind == "wrong session") response["data"]!["session"] = Ids.New("s");
                    if (kind == "wrong token") response = Responses.Error(null, WireError.AuthenticationFailed);
                    if (kind == "wrong version") response["v"] = 2;
                    if (kind == "wrong id") response["id"] = "other";
                    if (kind == "error") response = Responses.Error(null, WireError.Busy);
                    byte[] bytes = kind == "duplicate key" ? Encoding.UTF8.GetBytes("{\"v\":1,\"v\":1}") : WireJson.Encode(response);
                    await Framing.WriteAsync(stream, bytes, WireLimits.ResponseBytes, CancellationToken.None);
                });
                var watch = Stopwatch.StartNew();
                using (var discovery = f.Scan()) await Reject(SessionError.SessionUnavailable, () => discovery.ConnectAsync(descriptor.Session));
                True(watch.ElapsedMilliseconds < 1500); await serve;
                True(File.Exists(f.PathFor(descriptor.Session))); True(!listener.Poll(0, SelectMode.SelectRead));
            });
        // P4-19a-R1: real loading windows showed an events.read exit 69 with no reply and fixed session_unavailable at
        // ~1.4 s. A late authentication reply (beyond the 1-s discovery probe allowance) must fail closed exactly so.
        foreach (string late in new[] { "late auth reply, silent", "late auth reply, then valid" })
            Test("observation command with " + late + " exceeds probe allowance: exit 69, no stdout, descriptor retained", async () =>
            {
                using var f = new Fixture(); using var listener = Listen();
                DiscoveryDescriptor descriptor = f.Add(((IPEndPoint)listener.LocalEndPoint!).Port);
                Task serve = Task.Run(async () =>
                {
                    using Socket socket = await listener.AcceptAsync(); using var stream = new NetworkStream(socket, false);
                    await Framing.ReadAsync(stream, WireLimits.RequestBytes, CancellationToken.None);
                    await Task.Delay(DiscoveryLimits.ProbeTimeoutMs + 300);
                    if (late.EndsWith("valid", StringComparison.Ordinal))
                    {
                        try
                        {
                            await Framing.WriteAsync(stream, WireJson.EncodeResponse("discovery", ReadMethod.Auth,
                                new JObject { ["session"] = descriptor.Session, ["protocol"] = 1 }), WireLimits.ResponseBytes, CancellationToken.None);
                        }
                        catch (IOException) { }
                    }
                });
                var watch = Stopwatch.StartNew();
                var result = await f.Command("events.read", "--session", descriptor.Session, "--root", Ids.New("r"), "--game", Ids.New("g"),
                    "--cursor", Ids.New("e"), "--limit", "8");
                long elapsed = watch.ElapsedMilliseconds; await serve;
                Equal(69, result.Exit); Equal("", result.Output); Equal("rim-pilot: session_unavailable.\n", result.Error);
                True(elapsed >= DiscoveryLimits.ProbeTimeoutMs - 50 && elapsed < WireLimits.RequestTimeoutMs);
                True(!result.Error.Contains(descriptor.Token, StringComparison.Ordinal));
                True(File.Exists(f.PathFor(descriptor.Session)));
            });
        Test("port reuse by different authenticated instance never retargets", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            DiscoveryDescriptor current = f.Read(server.Session);
            File.Delete(f.PathFor(current.Session));
            DiscoveryDescriptor old = f.Add(current.Port);
            using (var discovery = f.Scan()) await Reject(SessionError.SessionUnavailable, () => discovery.ConnectAsync(old.Session));
            Equal(0, server.Pending.Count); True(File.Exists(f.PathFor(old.Session)));
        });
        Test("descriptor replacement after scan cannot retarget cached exact identity", async () =>
        {
            using var f = new Fixture(); using var a = LoopbackTransport.Start(f.Root); using var b = LoopbackTransport.Start(f.Root);
            File.Delete(f.PathFor(b.Session));
            using var discovery = f.Scan();
            File.Delete(f.PathFor(a.Session));
            f.Add(b.Port, a.Session);
            using var peer = await discovery.ConnectAsync(a.Session);
            Equal(a.Session, peer.Session); Equal(0, b.ActiveConnections);
        });
        Test("expired selected connection cannot revive or reconnect", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            using var discovery = f.Scan(); using var peer = await discovery.ConnectAsync(null);
            await Task.Delay(DiscoveryLimits.ProbeTimeoutMs + 100);
            try { peer.BeginRequest(); throw new Exception("Revived expired socket."); }
            catch (OperationCanceledException) { }
            await Reject(SessionError.SessionUnavailable, () => discovery.ConnectAsync(null));
        });
        Test("probe total deadline fails whole listing, no partial results or deletes", async () =>
        {
            using var f = new Fixture();
            Socket[] listeners = Enumerable.Range(0, 6).Select(_ => Listen()).ToArray();
            try
            {
                foreach (Socket listener in listeners) f.Add(((IPEndPoint)listener.LocalEndPoint!).Port);
                var watch = Stopwatch.StartNew();
                using var discovery = f.Scan(); await Reject(SessionError.LimitExceeded, () => discovery.ListAsync());
                True(watch.ElapsedMilliseconds >= 4900 && watch.ElapsedMilliseconds < 6500);
                Equal(6, Directory.GetFiles(f.Registry, "*.json").Length);
            }
            finally { foreach (Socket listener in listeners) listener.Dispose(); }
        });
        foreach (string kind in new[] { "records", "entries" })
            Test("bounded scan rejects entire overfull registry: " + kind, async () =>
            {
                using var f = new Fixture();
                int count = kind == "records" ? 17 : 65;
                for (int i = 0; i < count; i++)
                    if (kind == "records") f.Add(20000 + i); else File.WriteAllText(f.Registry + "/unrelated-" + i, "keep");
                ExpectScanError(f, SessionError.LimitExceeded); Equal(count, Directory.GetFiles(f.Registry).Length); await Task.CompletedTask;
            });
        Test("native private workspace artifacts retained and ignored", async () =>
        {
            using var f = new Fixture(); Directory.CreateDirectory(f.Registry + "/.private-workspace");
            File.WriteAllText(f.Registry + "/.private-workspace/record", "uncertain");
            using var discovery = f.Scan(); Equal(0, ((JArray)(await discovery.ListAsync())["sessions"]!).Count);
            Equal("uncertain", File.ReadAllText(f.Registry + "/.private-workspace/record"));
        });
        foreach (string[] args in new[] { Array.Empty<string>(), new[] { "unsupported" }, new[] { "sessions", "--token", "secret" }, new[] { "sessions", "--session" } })
            Test("strict CLI usage, no secret arguments echoed: " + args.Length, async () =>
            {
                using var f = new Fixture(); var result = await f.Command(args);
                Equal(64, result.Exit); Equal("", result.Output); True(result.Error.StartsWith("rim-pilot: usage: ", StringComparison.Ordinal));
                True(!result.Error.Contains("secret", StringComparison.Ordinal));
            });
        foreach (string state in new[] { "menu", "playing", "loading" })
            Test("CLI health/capabilities against real dispatcher: " + state, async () =>
            {
                using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
                var fence = new TransitionFence(server.Pending);
                var dispatcher = new ReadDispatcher<object>(server.Session, server.Pending, fence, () => 0);
                object root = new(), game = new(); var maps = new System.Collections.Generic.List<object> { new() };
                var frame = new ReadFrame<object>(state == "loading" ? null : root,
                    state == "playing" ? game : null, state == "playing" ? maps : null, state != "loading");
                foreach (string method in new[] { "health", "capabilities" })
                {
                    Task<(int Exit, string Output, string Error)> command = f.Command(method, "--session", server.Session);
                    var clock = Stopwatch.StartNew();
                    while (!command.IsCompleted)
                    {
                        True(clock.ElapsedMilliseconds < 4000); dispatcher.Pump(() => frame); Thread.Sleep(1);
                    }
                    var result = await command; Equal(0, result.Exit); Equal("", result.Error);
                    JObject wrapper = JObject.Parse(result.Output); Equal(server.Session, (string)wrapper["session"]!);
                    JObject data = (JObject)wrapper["response"]!["data"]!;
                    if (method == "health") { Equal(state == "loading" ? "loading" : "ready", (string)data["state"]!); Equal(1, data.Count); }
                    else
                    {
                        var available = ((JArray)data["capabilities"]!).Where(value => (string?)value["state"] == "available");
                        Equal("auth,health,capabilities", string.Join(",", available.Select(value => (string)value["name"]!)));
                    }
                    True(!result.Output.Contains(f.Read(server.Session).Token, StringComparison.Ordinal));
                    True(!result.Output.Contains(f.Root, StringComparison.Ordinal));
                }
            });
        Test("bootstrap commands require explicit choice, never first live instance", async () =>
        {
            using var f = new Fixture(); using var a = LoopbackTransport.Start(f.Root); using var b = LoopbackTransport.Start(f.Root);
            foreach (string method in new[] { "health", "capabilities" })
            {
                var result = await f.Command(method); Equal(69, result.Exit); Equal("", result.Output);
                Equal("rim-pilot: selection_required.\n", result.Error);
            }
            Equal(0, a.Pending.Count); Equal(0, b.Pending.Count); Equal(0, a.ActiveConnections); Equal(0, b.ActiveConnections);
        });
        foreach (string kind in new[] { "valid health", "valid capabilities", "valid error", "wrong id", "wrong version", "missing id",
            "duplicate key", "bad JSON", "extra field", "invalid state", "invalid capability", "duplicate capability", "wrong scope session",
            "mismatched method", "unknown error", "oversized", "truncated", "slow body" })
            Test("CLI validates bootstrap response, no raw output/reconnect: " + kind, async () =>
            {
                using var f = new Fixture(); using var listener = Listen();
                DiscoveryDescriptor descriptor = f.Add(((IPEndPoint)listener.LocalEndPoint!).Port);
                string method = kind.Contains("capabilit", StringComparison.Ordinal) ? "capabilities" : "health";
                Task serve = Task.Run(async () =>
                {
                    using Socket socket = await listener.AcceptAsync(); using var stream = new NetworkStream(socket, false);
                    byte[] auth = await Framing.ReadAsync(stream, WireLimits.RequestBytes, CancellationToken.None);
                    new Authentication(descriptor.Session, descriptor.Token).Accept(auth);
                    await Framing.WriteAsync(stream, WireJson.EncodeResponse("discovery", ReadMethod.Auth,
                        new JObject { ["session"] = descriptor.Session, ["protocol"] = 1 }), WireLimits.ResponseBytes, CancellationToken.None);
                    Request request = Request.Read(await Framing.ReadAsync(stream, WireLimits.RequestBytes, CancellationToken.None));
                    Equal(method == "health" ? ReadMethod.Health : ReadMethod.Capabilities, request.Method);
                    if (kind == "oversized") { await stream.WriteAsync(new byte[] { 0, 4, 0, 1 }); return; }
                    if (kind == "truncated") { await stream.WriteAsync(new byte[] { 0, 0, 0, 10, (byte)'{' }); return; }
                    if (kind == "slow body")
                    { await stream.WriteAsync(new byte[] { 0, 0, 0, 99, (byte)'{' }); await Task.Delay(WireLimits.RequestTimeoutMs + 200); return; }
                    JObject response = Responses.Success(method, method == "health" ? Responses.Health(true) : Responses.BootstrapCapabilities());
                    if (kind == "valid error") response = Responses.Error(method, WireError.NotReady);
                    if (kind == "wrong id") response["id"] = "other";
                    if (kind == "wrong version") response["v"] = 2;
                    if (kind == "missing id") response.Remove("id");
                    if (kind == "extra field") response["data"]!["private-marker"] = descriptor.Token;
                    if (kind == "invalid state") response["data"]!["state"] = "private-marker";
                    if (kind == "invalid capability") response["data"]!["capabilities"]![0]!["state"] = "private-marker";
                    if (kind == "duplicate capability") ((JArray)response["data"]!["capabilities"]!).Add(response["data"]!["capabilities"]![0]!.DeepClone());
                    if (kind == "wrong scope session") response["data"]!["scope"] = new Scope(Ids.New("s"), Ids.New("r")).Json();
                    if (kind == "mismatched method") response["data"] = Responses.BootstrapCapabilities();
                    if (kind == "unknown error") response = new JObject { ["v"] = 1, ["id"] = method, ["ok"] = false,
                        ["error"] = new JObject { ["code"] = "private-marker" } };
                    byte[] bytes = kind == "duplicate key" ? Encoding.UTF8.GetBytes("{\"v\":1,\"v\":1}")
                        : kind == "bad JSON" ? Encoding.UTF8.GetBytes("private-marker") : WireJson.Encode(response);
                    await Framing.WriteAsync(stream, bytes, WireLimits.ResponseBytes, CancellationToken.None);
                    // Keep peer live until response read; command owns disconnect. No second request.
                    var tail = new byte[1]; Equal(0, await stream.ReadAsync(tail));
                });
                var result = await f.Command(method); await serve;
                if (kind.StartsWith("valid", StringComparison.Ordinal))
                {
                    Equal(kind == "valid error" ? 69 : 0, result.Exit);
                    JObject wrapper = JObject.Parse(result.Output); Equal(descriptor.Session, (string)wrapper["session"]!);
                    Equal(kind != "valid error", (bool)wrapper["response"]!["ok"]!);
                    Equal(kind == "valid error" ? "rim-pilot: not_ready.\n" : "", result.Error);
                }
                else { Equal(69, result.Exit); Equal("", result.Output); Equal("rim-pilot: session_unavailable.\n", result.Error); }
                True(!result.Output.Contains(descriptor.Token, StringComparison.Ordinal));
                True(!result.Error.Contains("private-marker", StringComparison.Ordinal));
                True(!listener.Poll(0, SelectMode.SelectRead)); True(File.Exists(f.PathFor(descriptor.Session)));
            });
        Test("repeated scans/probes release native fds and sockets", async () =>
        {
            using var f = new Fixture(); using var server = LoopbackTransport.Start(f.Root);
            using (var warm = f.Scan()) await warm.ListAsync();
            int baseline = Directory.GetFileSystemEntries("/proc/self/fd").Length;
            for (int i = 0; i < 10; i++) { using var discovery = f.Scan(); await discovery.ListAsync(); await Task.Delay(30); }
            True(Directory.GetFileSystemEntries("/proc/self/fd").Length <= baseline + 3);
        });
        Console.WriteLine($"CLI sessions: {passed} passed, {failed} failed (asset-free .NET/native loopback only; Mono/game NOT RUN)");
        return failed;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root;
        internal string Registry => Root + "/rim-pilot/v1";
        internal Fixture()
        {
            Root = Directory.CreateTempSubdirectory("rim-pilot-sessions-").FullName; File.SetUnixFileMode(Root, Private);
            using var registry = LinuxPrivateDirectory.Open(Root);
        }
        internal SessionDiscovery Scan() => new(Root);
        internal string PathFor(string session) => Registry + "/" + session + ".json";
        internal DiscoveryDescriptor Read(string session)
        { using var registry = LinuxPrivateDirectory.Open(Root); return registry.ReadRecord(session + ".json", new DiscoveryBudget()); }
        internal DiscoveryDescriptor Add(int port, string? session = null)
        {
            var descriptor = new DiscoveryDescriptor(session ?? Ids.New("s"), port, Ids.NewToken());
            using var registry = LinuxPrivateDirectory.Open(Root); using var receipt = registry.Publish(descriptor); return descriptor;
        }
        internal async Task<(int Exit, string Output, string Error)> Command(params string[] args)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            int exit = await CliCommands.RunAsync(args, Root, output, error); return (exit, output.ToString(), error.ToString());
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static Socket Listen()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); socket.Listen(1); return socket;
    }
    private static int ClosedPort() { using var socket = Listen(); return ((IPEndPoint)socket.LocalEndPoint!).Port; }
    private static void ExpectScanError(Fixture fixture, SessionError expected)
    {
        try { using var discovery = fixture.Scan(); throw new Exception("Accepted invalid registry."); }
        catch (SessionException error) { Equal(expected, error.Code); }
    }
    private static async Task Reject(SessionError expected, Func<Task> action)
    {
        try { await action(); throw new Exception("Expected rejection."); }
        catch (SessionException error) { Equal(expected, error.Code); True(error.InnerException == null && error.Message == "Local session unavailable."); }
    }
    private static void Test(string name, Func<Task> action)
    {
        try { action().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL CLI sessions " + name + ": " + error.GetType().Name + " " + error.Message); }
    }
    private static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Values differ."); }
}
