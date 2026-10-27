using System;
using System.Threading;
using RimPilot.Protocol;

namespace RimPilot.Mod.Bridge
{
    // Process-scoped, BCL-only owner. Constructors may call Bootstrap on a loading worker;
    // only an audited game-thread callback may call InitializeOnGameThread. No scene owns it.
    internal sealed class ProcessLifecycle<T> where T : class, IDisposable
    {
        private readonly object gate = new object();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly Action installPatches;
        private readonly Action<Action> registerFallback;
        private readonly Action<Action> registerQuitting;
        private readonly Func<CancellationToken, T> start;
        private bool bootstrapAttempted, patched, initializationAttempted, stopped, failed;
        private Thread? worker;
        private T? session;
        private int gameThreadId;

        internal ProcessLifecycle(Action installPatches, Action<Action> registerFallback,
            Action<Action> registerQuitting, Func<CancellationToken, T> start)
        {
            this.installPatches = installPatches;
            this.registerFallback = registerFallback;
            this.registerQuitting = registerQuitting;
            this.start = start;
        }

        internal T? Session { get { lock (gate) return stopped ? null : session; } }
        internal bool Failed { get { lock (gate) return failed; } }
        internal bool IsGameThread => Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref gameThreadId);

        internal void Bootstrap()
        {
            lock (gate)
            {
                if (bootstrapAttempted || stopped) return;
                bootstrapAttempted = true;
            }
            try
            {
                // Register BCL fallbacks before patches can admit a main-thread callback.
                registerFallback(Stop);
                installPatches();
                lock (gate) { if (!stopped) patched = true; }
            }
            catch { Fail(); } // No exception text, private paths or credentials cross into game logs.
        }

        internal void InitializeOnGameThread()
        {
            lock (gate)
            {
                if (!patched || initializationAttempted || stopped) return;
                initializationAttempted = true;
                Volatile.Write(ref gameThreadId, Thread.CurrentThread.ManagedThreadId);
            }
            try
            {
                // Unity event registration happens here, never in a mod constructor/loading worker.
                // Keep this process-lifetime subscription; fallback cleanup never calls Unity APIs.
                registerQuitting(Stop);
                lock (gate)
                {
                    if (stopped) return;
                    worker = new Thread(Run) { IsBackground = true, Name = "RimPilot session owner" };
                    worker.Start();
                }
            }
            catch { Fail(); }
        }

        private void Run()
        {
            T? owned = null;
            try
            {
                stopping.Token.ThrowIfCancellationRequested();
                // Native filesystem/startup and disposal never execute on the game thread.
                owned = start(stopping.Token);
                lock (gate)
                {
                    if (!stopped) session = owned;
                    while (!stopped) Monitor.Wait(gate);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (TransportException) when (stopping.IsCancellationRequested) { }
            catch { lock (gate) failed = true; }
            finally
            {
                lock (gate) { session = null; stopped = true; Monitor.PulseAll(gate); }
                try { owned?.Dispose(); }
                catch { lock (gate) failed = true; }
            }
        }

        private void Fail()
        {
            lock (gate) failed = true;
            Stop();
        }

        internal void Stop()
        {
            Thread? owner;
            lock (gate)
            {
                if (stopped) return;
                stopped = true;
                session = null;
                owner = worker;
                Monitor.PulseAll(gate);
            }
            // Production start only polls this token; it installs no cancellation callbacks.
            try { stopping.Cancel(); }
            catch (AggregateException) { lock (gate) failed = true; }
            // One bounded wait, independent of roots and pending native filesystem/socket work.
            // If native startup/disposal stalls, background owner retains eventual cleanup duty.
            if (owner != null && owner != Thread.CurrentThread && owner.IsAlive)
                owner.Join(WireLimits.ShutdownTimeoutMs);
        }
    }
}
