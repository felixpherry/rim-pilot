using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace RimPilot.Protocol
{
    internal sealed class TransportException : Exception
    {
        internal TransportException() : base("Local transport unavailable.") { }
    }

    // No client-supplied closures or game objects. Main-thread dispatcher consumes this finite
    // queue, rechecking cancellation, deadline, readiness and scope immediately before work.
    internal sealed class PendingReads
    {
        private readonly object gate = new object();
        private readonly List<PendingRead> items = new List<PendingRead>();
        private bool stopped;
        internal bool TryAdd(PendingRead item)
        {
            lock (gate)
            {
                items.RemoveAll(value => !value.IsLive);
                if (stopped || !item.IsLive || items.Count == WireLimits.PendingRequests) return false;
                items.Add(item);
                return true;
            }
        }
        internal bool TryTake(out PendingRead? item)
        {
            lock (gate)
            {
                while (items.Count != 0)
                {
                    PendingRead next = items[0]; items.RemoveAt(0);
                    if (next.IsLive) { item = next; return true; }
                }
                item = null; return false;
            }
        }
        internal void Remove(PendingRead item) { lock (gate) items.Remove(item); }
        internal void Invalidate(WireError error)
        {
            lock (gate)
            {
                foreach (PendingRead item in items) item.TryReject(error);
                items.Clear();
            }
        }
        internal void Stop()
        {
            lock (gate)
            {
                stopped = true;
                foreach (PendingRead item in items) item.Cancel();
                items.Clear();
            }
        }
        internal int Count { get { lock (gate) return items.Count; } }
    }

    internal sealed class PendingRead
    {
        private readonly SocketLifetime lifetime;
        private readonly long deadline;
        private readonly TaskCompletionSource<ReadReply> completion =
            new TaskCompletionSource<ReadReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Request Request { get; }
        internal CancellationToken Cancellation => lifetime.Cancellation;
        internal bool IsLive => !completion.Task.IsCompleted && !Cancellation.IsCancellationRequested && Monotonic.Now < deadline;
        internal Task<ReadReply> Completion => completion.Task;
        internal PendingRead(Request request, SocketLifetime lifetime)
        { Request = request; this.lifetime = lifetime; deadline = lifetime.Deadline; }
        // Ownership of primitive-only data transfers to transport. Producer must not mutate it again.
        internal bool TryComplete(JObject data) => IsLive && completion.TrySetResult(new ReadReply(data, null));
        internal bool TryReject(WireError error) => IsLive && completion.TrySetResult(new ReadReply(null, error));
        internal void Cancel() { completion.TrySetCanceled(); }
    }

    internal sealed class ReadReply
    {
        internal readonly JObject? Data;
        internal readonly WireError? Error;
        internal ReadReply(JObject? data, WireError? error) { Data = data; Error = error; }
    }

    internal static class Monotonic
    {
        internal static long Now => Stopwatch.GetTimestamp();
        internal static long After(int milliseconds) => Now + (long)Math.Ceiling(milliseconds * (double)Stopwatch.Frequency / 1000);
    }

    // One watchdog per admitted socket. Absolute stage deadline never extends on partial progress.
    // Timer closes the owned socket even on Mono streams whose pending I/O ignores cancellation.
    internal sealed partial class SocketLifetime : IDisposable
    {
        private readonly object gate = new object();
        private readonly Socket socket;
        private readonly CancellationTokenSource cancelled = new CancellationTokenSource();
        private readonly CancellationToken cancellation;
        private readonly Timer timer;
        private long deadline;
        private bool closed, receiving;
        internal CancellationToken Cancellation => cancellation;
        internal long Deadline { get { lock (gate) return deadline; } }
        internal SocketLifetime(Socket socket, int milliseconds)
        {
            this.socket = socket;
            cancellation = cancelled.Token;
            deadline = Monotonic.After(milliseconds);
            timer = new Timer(Check, null, WireLimits.SocketWatchdogMs, WireLimits.SocketWatchdogMs);
        }
        internal void Begin(int milliseconds, bool keepEarlier = false)
        {
            lock (gate)
            {
                EnsureLive();
                long next = Monotonic.After(milliseconds);
                deadline = keepEarlier ? Math.Min(deadline, next) : next;
            }
        }
        internal void EnsureLive()
        {
            lock (gate)
                if (closed || Monotonic.Now >= deadline) throw new OperationCanceledException();
        }
        // Poll/readability and Available are not an atomic EOF check: a concurrent receive can
        // drain the readable bytes between them. While receiving, EOF belongs to that read;
        // only the absolute deadline watchdog may close it. The gate also excludes new reads
        // while the watchdog checks EOF during queued/executing work or response publication.
        internal void BeginReceive() { lock (gate) { EnsureLive(); receiving = true; } }
        internal void EndReceive() { lock (gate) receiving = false; }
        private void Check(object? unused)
        {
            lock (gate)
            {
                if (closed) return;
                try
                {
                    if (Monotonic.Now < deadline)
                    {
                        if (receiving || !socket.Poll(0, SelectMode.SelectRead)) return;
                        ObserveReadable();
                        if (socket.Available != 0) return;
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                Close();
            }
        }
        internal void Close()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                // Close first: cancellation callbacks must never prevent the native I/O backstop.
                try { socket.Dispose(); }
                finally
                {
                    try { cancelled.Cancel(); }
                    catch (AggregateException) { /* Owned socket is closed even if a consumer callback fails. */ }
                }
            }
        }
        public void Dispose() { Close(); timer.Dispose(); cancelled.Dispose(); }
        partial void ObserveReadable();
    }

    // Token bucket uses monotonic time, with both finite burst and sustained ceilings.
    internal sealed class TransportBucket
    {
        private readonly double burst, perSecond;
        private double remaining;
        private long updated = Monotonic.Now;
        internal TransportBucket(int perSecond, int burst) { this.perSecond = perSecond; this.burst = burst; remaining = burst; }
        internal bool Take(int amount)
        {
            long now = Monotonic.Now;
            remaining = Math.Min(burst, remaining + (now - updated) * perSecond / Stopwatch.Frequency);
            updated = now;
            if (amount < 0 || amount > remaining) return false;
            remaining -= amount; return true;
        }
    }

    // Game-independent transport. Process lifecycle owns startup and cleanup on a background worker.
    internal sealed partial class LoopbackTransport : IDisposable
    {
        private readonly object gate = new object();
        private readonly Socket listener;
        private readonly LinuxPrivateDirectory registry;
        private readonly DiscoveryDescriptor descriptor;
        private readonly HashSet<SocketLifetime> clients = new HashSet<SocketLifetime>();
        private readonly TransportBucket admission = new TransportBucket(WireLimits.AdmissionsPerSecond, WireLimits.AdmissionBurst);
        private readonly Task[] workers = new Task[WireLimits.MaxConnections];
        private LinuxPrivateDirectory.OwnedRecord? record;
        private bool stopping;
        internal PendingReads Pending { get; }
        internal string Session => descriptor.Session;
        internal int Port => descriptor.Port;
        internal bool WorkersStopped => Task.WhenAll(workers).IsCompleted;
        internal int ActiveConnections { get { lock (gate) return clients.Count; } }

        private LoopbackTransport(LinuxPrivateDirectory registry, CancellationToken cancellation, PendingReads? pending)
        {
            this.registry = registry;
            Pending = pending ?? new PendingReads();
            listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                for (int i = 0; i < workers.Length; i++) workers[i] = Task.CompletedTask;
                cancellation.ThrowIfCancellationRequested();
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(WireLimits.ListenBacklog);
                Observe("listener-ready");
                descriptor = new DiscoveryDescriptor(Ids.New("s"), ((IPEndPoint)listener.LocalEndPoint!).Port, Ids.NewToken());
                // Listener bound/listening before atomic publication. Four persistent accept/serve
                // workers are admission slots; excess peers stay in finite kernel backlog, not tasks.
                cancellation.ThrowIfCancellationRequested();
                record = registry.Publish(descriptor);
                Observe("descriptor-published");
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < workers.Length; i++)
                {
                    // Installed Mono EndAccept checks listener disposal before returning its accepted
                    // socket. A close race could strand that socket inside the async result. Fixed
                    // background workers use synchronous Accept so every returned socket is owned.
                    cancellation.ThrowIfCancellationRequested();
                    workers[i] = Task.Factory.StartNew(Worker, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Observe("worker-started");
                }
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                StopSockets();
                DrainWorkers();
                RemoveRecord();
                throw new TransportException(); // Never expose native errors, private paths or secrets.
            }
        }
        // Own registry handle, including when startup fails. Caller supplies only directory policy input.
        internal static LoopbackTransport Start(string? xdgRuntimeDirectory, CancellationToken cancellation = default, PendingReads? pending = null)
        {
            LinuxPrivateDirectory? directory = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                directory = LinuxPrivateDirectory.Open(xdgRuntimeDirectory);
                cancellation.ThrowIfCancellationRequested();
                return new LoopbackTransport(directory, cancellation, pending);
            }
            catch
            {
                directory?.Dispose();
                throw new TransportException();
            }
        }

        private void Worker()
        {
            while (true)
            {
                Socket? socket = null;
                SocketLifetime? lifetime = null;
                try
                {
                    socket = listener.Accept();
                    bool admitted;
                    lock (gate) admitted = !stopping && admission.Take(1);
                    if (!admitted)
                    {
                        socket.Dispose();
                        // No task/worker per rejected peer, and no busy accept loop under churn.
                        Thread.Sleep(WireLimits.AdmissionRetryMs);
                        continue;
                    }
                    socket.NoDelay = true;
                    socket.SendBufferSize = WireLimits.SocketBufferBytes;
                    socket.ReceiveBufferSize = WireLimits.SocketBufferBytes;
                    lock (gate)
                    {
                        if (stopping) { socket.Dispose(); return; }
                        lifetime = new SocketLifetime(socket, WireLimits.AuthTimeoutMs);
                        clients.Add(lifetime);
                    }
                    ServeAsync(socket, lifetime).GetAwaiter().GetResult();
                }
                catch (Exception) { /* Fixed silent failure: never log peer input or exception text. */ }
                finally
                {
                    lifetime?.Dispose();
                    socket?.Dispose();
                    if (lifetime != null) lock (gate) clients.Remove(lifetime);
                }
                lock (gate)
                {
                    // A broken accept socket is terminal, never a busy retry loop.
                    if (stopping) return;
                    if (socket == null) { ThreadPool.QueueUserWorkItem(_ => Dispose()); return; }
                }
            }
        }

        private async Task ServeAsync(Socket socket, SocketLifetime lifetime)
        {
            using (var stream = new NetworkStream(socket, false))
            {
                var incoming = new TransportBucket(WireLimits.RequestBytesPerSecond, WireLimits.RequestByteBurst);
                var outgoing = new TransportBucket(WireLimits.ResponseBytesPerSecond, WireLimits.ResponseByteBurst);
                var rate = new TransportBucket(WireLimits.RequestsPerSecond, WireLimits.RequestBurst);
                try
                {
                    if (!rate.Take(1)) throw new WireException(WireError.AuthenticationFailed);
                    byte[] frame;
                    lifetime.BeginReceive();
                    try
                    {
                        frame = await Framing.ReadAsync(stream, WireLimits.RequestBytes, lifetime.Cancellation,
                            reserveBytes: count => Reserve(incoming, count)).ConfigureAwait(false);
                    }
                    finally { lifetime.EndReceive(); }
                    lifetime.EnsureLive();
                    var authentication = new Authentication(descriptor.Session, descriptor.Token);
                    authentication.Accept(frame);
                    Request auth = Request.Read(frame);
                    if (auth.Id == descriptor.Token) throw new WireException(WireError.AuthenticationFailed);
                    await SendAsync(stream, lifetime, outgoing, WireJson.EncodeResponse(auth.Id, ReadMethod.Auth,
                        new JObject { ["session"] = descriptor.Session, ["protocol"] = WireLimits.Version }), WireLimits.ResponseBytes).ConfigureAwait(false);
                }
                catch
                {
                    await TryErrorAsync(stream, lifetime, outgoing, null, WireError.AuthenticationFailed).ConfigureAwait(false);
                    return;
                }
                while (true)
                {
                    PendingRead? work = null;
                    string? id = null;
                    using (lifetime.Cancellation.Register(() => work?.Cancel()))
                    {
                        try
                        {
                            lifetime.Begin(WireLimits.IdleTimeoutMs);
                            byte[] frame;
                            lifetime.BeginReceive();
                            try
                            {
                                var first = new byte[1];
                                int count = await stream.ReadAsync(first, 0, 1, lifetime.Cancellation).ConfigureAwait(false);
                                lifetime.EnsureLive();
                                if (count == 0) return;
                                lifetime.Begin(WireLimits.RequestTimeoutMs);
                                bool admitted = rate.Take(1);
                                ObserveRate(admitted, socket);
                                // Reject before reading remaining header/body. Closing with unread peer
                                // bytes can reset TCP and truncate the best-effort terminal error frame.
                                if (!admitted) throw new WireException(WireError.RateLimited);
                                frame = await Framing.ReadAsync(stream, WireLimits.RequestBytes, lifetime.Cancellation,
                                    first[0], bytes => Reserve(incoming, bytes)).ConfigureAwait(false);
                            }
                            finally { lifetime.EndReceive(); }
                            lifetime.EnsureLive();
                            Request request = Request.Read(frame);
                            // Valid ASCII correlation IDs are not allowed to echo the active secret.
                            if (request.Id == descriptor.Token) throw new WireException(WireError.InvalidRequest);
                            id = request.Id;
                            if (request.Method == ReadMethod.Auth) throw new WireException(WireError.InvalidRequest);
                            if (request.Scope != null && request.Scope.Session != descriptor.Session) throw new WireException(WireError.StaleScope);
                            if (socket.Available != 0) throw new WireException(WireError.InvalidRequest);
                            work = new PendingRead(request, lifetime);
                            if (!Pending.TryAdd(work)) throw new WireException(WireError.Busy);
                            if (lifetime.Cancellation.IsCancellationRequested) work.Cancel();
                            ReadReply reply = await work.Completion.ConfigureAwait(false);
                            lifetime.EnsureLive();
                            if (socket.Available != 0) throw new WireException(WireError.InvalidRequest);
                            byte[] response = reply.Error.HasValue ? WireJson.Encode(Responses.Error(request.Id, reply.Error.Value))
                                : WireJson.EncodeResponse(request.Id, request.Method, reply.Data!);
                            await SendAsync(stream, lifetime, outgoing, response,
                                request.Method == ReadMethod.Screenshot ? WireLimits.CaptureResponseBytes : WireLimits.ResponseBytes).ConfigureAwait(false);
                        }
                        catch (WireException error)
                        {
                            await TryErrorAsync(stream, lifetime, outgoing, id, error.Code).ConfigureAwait(false);
                            return;
                        }
                        finally { if (work != null) { Pending.Remove(work); work.Cancel(); } }
                    }
                }
            }
        }
        private static void Reserve(TransportBucket bucket, int bytes)
        {
            if (!bucket.Take(bytes)) throw new WireException(WireError.RateLimited);
        }
        private static async Task SendAsync(Stream stream, SocketLifetime lifetime, TransportBucket outgoing, byte[] bytes, int ceiling)
        {
            lifetime.EnsureLive();
            if (!outgoing.Take(bytes.Length + 4)) throw new WireException(WireError.RateLimited);
            lifetime.Begin(WireLimits.WriteTimeoutMs, keepEarlier: true);
            await Framing.WriteAsync(stream, bytes, ceiling, lifetime.Cancellation).ConfigureAwait(false);
            lifetime.EnsureLive();
        }
        private static async Task TryErrorAsync(Stream stream, SocketLifetime lifetime, TransportBucket outgoing, string? id, WireError error)
        {
            try { await SendAsync(stream, lifetime, outgoing, WireJson.Encode(Responses.Error(id, error)), WireLimits.ResponseBytes).ConfigureAwait(false); }
            catch { /* Timeout/disconnect/write failure: close, no second response. */ }
        }
        private void RemoveRecord()
        {
            if (record == null) return;
            try { registry.RemoveOwned(record); }
            catch (DiscoveryException) { /* Uncertain/replaced records remain private, never adopted. */ }
            finally { record.Dispose(); record = null; }
        }
        private void StopSockets()
        {
            lock (gate)
            {
                stopping = true;
                listener.Dispose();
                foreach (SocketLifetime client in clients) client.Close();
                Pending.Stop();
            }
        }
        private void DrainWorkers()
        {
            try { Task.WhenAll(workers).Wait(WireLimits.ShutdownTimeoutMs); }
            catch (AggregateException) { /* Cleanup remains secret-safe if a worker unexpectedly fails. */ }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (stopping) return;
                StopSockets();
            }
            // Bounded drain, never an unbounded game-thread join. Real Mono close latency is a
            // separate runtime proof obligation. All worker exceptions are observed internally.
            DrainWorkers();
            RemoveRecord();
            registry.Dispose();
        }
        // Compiled out in production; failure injection supplies implementation in tests only.
        partial void Observe(string point);
        partial void ObserveRate(bool admitted, Socket socket);
        public override string ToString() => "Private loopback transport.";
    }
}
