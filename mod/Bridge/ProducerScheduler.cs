using System;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Mod.Bridge
{
    internal enum StepResult { Continue, Complete }

    // Trusted adapter code fixed at dispatcher construction, never client data or closures.
    internal interface IReadProducer<TMap> where TMap : class
    {
        // At most one snapshot build is active per session.
        bool SnapshotBuild { get; }
        // Must not touch game state; traversal starts in the first charged Step.
        IReadContinuation<TMap> Start(Request request);
    }

    // Game thread only, inside the dispatcher's transition read fence. Charge every candidate,
    // nested field and retained byte through ProducerWork BEFORE examining or keeping it.
    internal interface IReadContinuation<TMap> where TMap : class
    {
        StepResult Step(ProducerWork<TMap> work);
        // Fresh whole-result safety/scope/eligibility audit in the releasing update. False never
        // publishes a partial prefix. A charge that yields retries in the next update.
        bool Eligible(ProducerWork<TMap> work);
        // Primitive-only tree. Ownership passes to transport on release; never mutate it again.
        JObject Result();
        // Drop partial DTOs/references. Managed state only; always called exactly once.
        void Discard();
    }

    // One Update's session-wide allowance, shared by every producer step in that update.
    internal sealed class UpdateAllowance
    {
        internal readonly UpdateBudget Time;
        internal int Candidates = WireLimits.CandidatesPerUpdate, NestedFields = WireLimits.NestedFieldsPerUpdate;
        internal UpdateAllowance(UpdateBudget time) { Time = time; }
    }

    // Owns work taken out of PendingReads. Transition workers reject it immediately; partial
    // state is discarded on the game thread at the next pump. Never waits for game work.
    internal sealed class ActiveReads
    {
        private readonly object gate = new object();
        private readonly PendingRead?[] reads = new PendingRead?[WireLimits.ActiveReads];
        private readonly bool[] revoked = new bool[WireLimits.ActiveReads];
        internal int HeldBytes; // Game thread only.
        internal int Claim(PendingRead read)
        {
            lock (gate)
                for (int i = 0; i < reads.Length; i++)
                    if (reads[i] == null) { reads[i] = read; revoked[i] = false; return i; }
            return -1;
        }
        internal bool Revoked(int slot) { lock (gate) return revoked[slot]; }
        internal void Free(int slot) { lock (gate) { reads[slot] = null; revoked[slot] = false; } }
        internal void Invalidate(WireError error)
        {
            lock (gate)
                for (int i = 0; i < reads.Length; i++)
                    if (reads[i] != null) { reads[i]!.TryReject(error); revoked[i] = true; }
        }
        internal int Count
        {
            get { lock (gate) { int n = 0; foreach (PendingRead? read in reads) if (read != null) n++; return n; } }
        }
    }

    // Nested-field charging seen by pure gates; false = yielded or failed, never ineligible.
    internal interface INestedWork { bool TryNestedFields(int count); }

    // Private per-read accounting. Totals never become wire fields, cursor material or hints.
    internal sealed class ProducerWork<TMap> : INestedWork where TMap : class
    {
        private readonly ActiveReads owner;
        private UpdateAllowance? update;
        private int share;
        internal readonly PendingRead Read;
        internal readonly IReadContinuation<TMap> Continuation;
        internal readonly bool SnapshotBuild;
        internal readonly Scope Scope;
        internal readonly GenerationTracker<TMap> Generations;
        internal ReadFrame<TMap>? Frame { get; private set; }
        internal Request Request => Read.Request;
        internal int Candidates { get; private set; }
        internal int NestedFields { get; private set; }
        internal int Bytes { get; private set; }
        internal WireError? Failure { get; private set; }
        internal bool Yielded { get; private set; }
        internal bool Completed;
        internal ProducerWork(ActiveReads owner, PendingRead read, IReadContinuation<TMap> continuation,
            bool snapshotBuild, Scope scope, GenerationTracker<TMap> generations)
        {
            this.owner = owner; Read = read; Continuation = continuation;
            SnapshotBuild = snapshotBuild; Scope = scope; Generations = generations;
        }
        // Fair share of this update's remaining candidates among works not yet stepped.
        internal void Begin(ReadFrame<TMap> frame, UpdateAllowance allowance, int sharers)
        {
            Frame = frame; update = allowance; Yielded = false;
            share = allowance.Candidates == 0 ? 0 : (allowance.Candidates + sharers - 1) / sharers;
        }
        internal void End() { Frame = null; update = null; share = 0; }
        internal bool TryCandidate()
        {
            if (!Charging()) return false;
            if (Candidates == WireLimits.ScanCandidates) return Fail(WireError.Unavailable);
            if (share == 0 || update!.Candidates == 0) return Yield();
            share--; update.Candidates--; Candidates++; return true;
        }
        public bool TryNestedFields(int count)
        {
            if (!Charging()) return false;
            if (count < 1 || count > WireLimits.NestedFieldsPerUpdate
                || count > WireLimits.NestedFieldsPerRead - NestedFields) return Fail(WireError.Unavailable);
            if (count > update!.NestedFields) return Yield();
            update.NestedFields -= count; NestedFields += count; return true;
        }
        // Charge before retaining encoded DTO bytes. Per-read cap and session-wide active memory.
        internal bool TryRetain(int bytes)
        {
            if (!Charging()) return false;
            int cap = SnapshotBuild ? WireLimits.SnapshotBytes : WireLimits.ResponseBytes;
            if (bytes < 0 || bytes > cap - Bytes) return Fail(WireError.Unavailable);
            if (bytes > WireLimits.ActiveWorkBytes - owner.HeldBytes) return Fail(WireError.Busy);
            owner.HeldBytes += bytes; Bytes += bytes; return true;
        }
        // Uniform failure; discards everything. Never encodes hidden counts or causes.
        internal bool Fail(WireError error) { if (!Failure.HasValue) Failure = error; return false; }
        // Continuation discarded its completed candidate and needs a fresh bounded Step.
        // Accounting, deadline, scope and revocation remain attached to the same read.
        internal bool RestartStep() { Completed = false; return Yield(); }
        private bool Yield() { Yielded = true; return false; }
        private bool Charging()
        {
            if (Failure.HasValue || update == null) return false;
            if (!Read.IsLive) return Fail(WireError.Timeout);
            return update.Time.Available || Yield();
        }
        internal void ReleaseMemory() { owner.HeldBytes -= Bytes; Bytes = 0; }
    }
}
