using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using RimPilot.Mod.Bridge;
using RimPilot.Protocol;

[SupportedOSPlatform("linux")]
internal static class ProcessLifecycleTests
{
    private static int passed, failed;
    internal static int Run()
    {
        Test("loading-worker bootstrap only installs metadata/BCL hooks once", () =>
        {
            using var f = new Fixture();
            int caller = 0;
            Task.Run(() => { caller = Environment.CurrentManagedThreadId; f.Owner.Bootstrap(); }).GetAwaiter().GetResult();
            Equal(caller, f.PatchThread); Equal(caller, f.FallbackThread);
            Equal(1, f.Patches); Equal(1, f.Fallbacks); Equal(0, f.Quits); Equal(0, f.Starts);
            True(f.Owner.Session == null); True(!f.Owner.IsGameThread);
        });
        Test("concurrent/repeated constructors do not duplicate patch registration", () =>
        {
            using var f = new Fixture();
            Parallel.For(0, 64, _ => f.Owner.Bootstrap());
            Equal(1, f.Patches); Equal(1, f.Fallbacks); Equal(0, f.Starts);
        });
        Test("callback cannot initialize before bootstrap success", () =>
        {
            using var f = new Fixture(); f.Owner.InitializeOnGameThread();
            Equal(0, f.Quits); Equal(0, f.Starts); True(!f.Owner.IsGameThread);
            f.Start(); Equal(1, f.Starts);
        });
        Test("quitting registration on game thread precedes background listener startup", () =>
        {
            using var f = new Fixture(); f.Start();
            Equal(Environment.CurrentManagedThreadId, f.QuitThread);
            True(f.StartThread != f.QuitThread); True(f.Owner.IsGameThread);
            True(Task.Run(() => !f.Owner.IsGameThread).GetAwaiter().GetResult());
            Equal(1, f.Quits); Equal(1, f.Starts);
        });
        Test("many root callbacks preserve exact session and process subscription", () =>
        {
            using var f = new Fixture(); f.Start(); var session = f.Owner.Session;
            for (int i = 0; i < 128; i++) f.Owner.InitializeOnGameThread();
            True(ReferenceEquals(session, f.Owner.Session)); Equal(1, f.Starts); Equal(1, f.Quits);
            Equal(0, f.Resource.Disposals);
        });
        Test("quit disposes once on owner worker and never restarts", () =>
        {
            using var f = new Fixture(); f.Start(); f.Quit!();
            Equal(1, f.Resource.Disposals); Equal(f.StartThread, f.Resource.DisposeThread);
            True(f.Owner.Session == null); f.Quit(); f.Fallback!(); f.Owner.Bootstrap(); f.Owner.InitializeOnGameThread();
            Equal(1, f.Patches); Equal(1, f.Starts); Equal(1, f.Resource.Disposals);
        });
        Test("process-exit/domain-unload fallback uses same owner without Unity callback", () =>
        {
            using var f = new Fixture(); f.Start();
            Task.Run(() => f.Fallback!()).GetAwaiter().GetResult();
            Equal(1, f.Resource.Disposals); Equal(1, f.Quits); True(f.Owner.Session == null);
        });
        Test("fallback before first frame prevents quitting registration and startup", () =>
        {
            using var f = new Fixture(); f.Owner.Bootstrap(); f.Fallback!(); f.Owner.InitializeOnGameThread();
            Equal(0, f.Quits); Equal(0, f.Starts); True(f.Owner.Session == null);
        });
        Test("stop before constructor is terminal", () =>
        {
            using var f = new Fixture(); f.Owner.Stop(); f.Owner.Bootstrap(); f.Owner.InitializeOnGameThread();
            Equal(0, f.Patches); Equal(0, f.Fallbacks); Equal(0, f.Starts);
        });
        foreach (string failure in new[] { "fallback", "patch", "quit", "start" })
            Test(failure + " failure is contained, terminal and never retried", () =>
            {
                using var f = new Fixture(failure); f.Owner.Bootstrap(); f.Owner.InitializeOnGameThread();
                Until(() => f.Owner.Failed); f.Owner.Stop();
                int patches = f.Patches, quits = f.Quits, starts = f.Starts;
                f.Owner.Bootstrap(); f.Owner.InitializeOnGameThread();
                Equal(patches, f.Patches); Equal(quits, f.Quits); Equal(starts, f.Starts);
                True(f.Owner.Session == null); Equal(0, f.Resource.Disposals);
            });
        Test("synchronous stop inside quitting registration prevents start", () =>
        {
            var owner = new ProcessLifecycle<Resource>(() => { }, _ => { }, stop => stop(),
                _ => throw new Exception("Must not start"));
            owner.Bootstrap(); owner.InitializeOnGameThread(); True(owner.Session == null); True(!owner.Failed);
        });
        Test("cleanup exception contained without resurrecting session", () =>
        {
            using var f = new Fixture("dispose"); f.Start(); f.Owner.Stop();
            True(f.Owner.Failed); True(f.Owner.Session == null); Equal(1, f.Resource.Disposals);
        });
        Test("concurrent quit/fallback calls have single cleanup owner", () =>
        {
            using var f = new Fixture(); f.Start(); Parallel.For(0, 32, _ => f.Owner.Stop());
            Equal(1, f.Resource.Disposals); True(f.Owner.Session == null);
        });
        Test("stop during blocked startup is bounded; late resource is disposed, never exposed", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var disposed = new ManualResetEventSlim(); var resource = new Resource();
            CancellationToken observed = default;
            var owner = new ProcessLifecycle<Resource>(() => { }, _ => { }, _ => { }, token =>
            {
                observed = token; entered.Set(); release.Wait(); return resource;
            });
            resource.AfterDispose = () => disposed.Set();
            try
            {
                owner.Bootstrap(); owner.InitializeOnGameThread(); True(entered.Wait(2000));
                var watch = Stopwatch.StartNew(); owner.Stop();
                True(watch.ElapsedMilliseconds < WireLimits.ShutdownTimeoutMs + 750);
                True(observed.IsCancellationRequested); True(owner.Session == null);
                owner.InitializeOnGameThread(); release.Set(); True(disposed.Wait(2000));
                True(owner.Session == null); Equal(1, resource.Disposals);
            }
            finally { release.Set(); owner.Stop(); True(disposed.Wait(2000)); }
        });
        Test("stop during slow cleanup returns after finite grace, repeated stop does not join", () =>
        {
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            var resource = new Resource { AfterDispose = () => { entered.Set(); release.Wait(); completed.Set(); } };
            var owner = new ProcessLifecycle<Resource>(() => { }, _ => { }, _ => { }, _ => resource);
            try
            {
                owner.Bootstrap(); owner.InitializeOnGameThread(); Until(() => owner.Session != null);
                var watch = Stopwatch.StartNew(); owner.Stop();
                True(entered.IsSet); True(watch.ElapsedMilliseconds < WireLimits.ShutdownTimeoutMs + 750);
                watch.Restart(); owner.Stop(); True(watch.ElapsedMilliseconds < 500);
                True(owner.Session == null); release.Set(); True(completed.Wait(2000));
            }
            finally { release.Set(); owner.Stop(); True(completed.Wait(2000)); }
        });
        Test("pre-cancelled listener startup creates no registry", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "rim-pilot-cancelled-" + Guid.NewGuid().ToString("N"));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { LoopbackTransport.Start(root, cancel.Token); throw new Exception("Accepted cancelled start"); }
            catch (TransportException) { }
            True(!Directory.Exists(root));
        });
        foreach (string boundary in new[] { "listener-ready", "descriptor-published", "worker-started" })
            Test("listener cancellation cleans partial startup at " + boundary, () =>
            {
                string root = Directory.CreateTempSubdirectory("rim-pilot-start-cancel-").FullName;
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                using var cancellation = new CancellationTokenSource();
                LoopbackTransport? observed = null;
                LoopbackTransport.TestHook = (transport, point) =>
                {
                    observed = transport;
                    if (point == boundary) cancellation.Cancel();
                };
                try
                {
                    try { LoopbackTransport.Start(root, cancellation.Token); throw new Exception("Accepted cancelled startup"); }
                    catch (TransportException) { }
                    True(observed != null && observed.WorkersStopped);
                    True(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0);
                }
                finally { LoopbackTransport.TestHook = null; Directory.Delete(root, true); }
            });
        Test("real host transport retained across roots, quit removes only owned descriptor", () =>
        {
            string root = Directory.CreateTempSubdirectory("rim-pilot-lifecycle-").FullName;
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var owner = new ProcessLifecycle<LoopbackTransport>(() => { }, _ => { }, _ => { }, token => LoopbackTransport.Start(root, token));
            LoopbackTransport? transport = null;
            try
            {
                owner.Bootstrap(); owner.InitializeOnGameThread(); Until(() => owner.Session != null);
                transport = owner.Session!;
                for (int i = 0; i < 32; i++) owner.InitializeOnGameThread();
                True(ReferenceEquals(transport, owner.Session));
                string record = Path.Combine(root, "rim-pilot", "v1", transport.Session + ".json");
                True(File.Exists(record)); owner.Stop(); Until(() => transport.WorkersStopped && !File.Exists(record));
                True(owner.Session == null);
            }
            finally { owner.Stop(); Directory.Delete(root, true); }
        });
        Console.WriteLine($"process lifecycle: {passed} passed, {failed} failed (.NET/host only; Harmony/Mono/game NOT RUN)");
        return failed;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly ProcessLifecycle<Resource> Owner;
        internal readonly Resource Resource = new Resource();
        internal Action? Quit, Fallback;
        internal int Patches, Fallbacks, Quits, Starts, PatchThread, FallbackThread, QuitThread, StartThread;
        internal Fixture(string? failure = null)
        {
            Owner = new ProcessLifecycle<Resource>(() =>
            {
                Interlocked.Increment(ref Patches); PatchThread = Environment.CurrentManagedThreadId;
                if (failure == "patch") throw new Exception("private failure");
            }, stop =>
            {
                Interlocked.Increment(ref Fallbacks); FallbackThread = Environment.CurrentManagedThreadId; Fallback = stop;
                if (failure == "fallback") throw new Exception("private failure");
            }, stop =>
            {
                Interlocked.Increment(ref Quits); QuitThread = Environment.CurrentManagedThreadId; Quit = stop;
                if (failure == "quit") throw new Exception("private failure");
            }, token =>
            {
                True(Quits == 1); token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref Starts); StartThread = Environment.CurrentManagedThreadId;
                if (failure == "start") throw new Exception("private failure");
                return Resource;
            });
            if (failure == "dispose") Resource.AfterDispose = () => throw new Exception("private failure");
        }
        internal void Start() { Owner.Bootstrap(); Owner.InitializeOnGameThread(); Until(() => Owner.Session != null); }
        public void Dispose() => Owner.Stop();
    }
    private sealed class Resource : IDisposable
    {
        internal int Disposals, DisposeThread;
        internal Action? AfterDispose;
        public void Dispose() { DisposeThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref Disposals); AfterDispose?.Invoke(); }
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > 3000) throw new Exception("Lifecycle deadline exceeded."); Thread.Sleep(1); }
    }
    private static void Test(string name, Action test)
    {
        try { test(); passed++; }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL lifecycle " + name + ": " + error); }
    }
    private static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Values differ."); }
}
