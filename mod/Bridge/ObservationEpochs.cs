using System;
using System.Threading;
using RimPilot.Protocol;

namespace RimPilot.Mod.Bridge
{
    [Flags]
    internal enum ObservationInvalidation { Safety = 1, Readiness = 2, Source = 4, All = 7 }

    // Implementations must only cancel/clear bounded managed state here. Hooks can run on a
    // loading worker: Unity resource disposal belongs to later game-thread cleanup, not this call.
    internal interface IObservationInvalidationSink
    {
        void Invalidate(ObservationInvalidation cause);
    }

    // Historical public text needs fresh availability, not a continuously valid render lease.
    // Ordinary Update/GUI brackets revoke leases but preserve this history; actual unsafe,
    // source/readiness/geometry changes and explicit mutation/transition brackets clear it.
    // These owners supply their own interval witnesses instead of reusing revoked leases.
    // History uses availability witnesses; native render uses explicit callback endpoints.
    internal interface IObservationBoundarySink : IObservationInvalidationSink { }
    internal interface IObservationHistorySink : IObservationBoundarySink { }

    internal sealed class FrameGeometry
    {
        internal readonly long Frame;
        internal readonly int Width, Height;
        internal readonly float Scale;
        // Trusted scene sample, never client-supplied. Only ordinary map view permits depth-only pixels.
        internal readonly bool MapView;
        internal FrameGeometry(long frame, int width, int height, float scale, bool mapView = false)
        { Frame = frame; Width = width; Height = height; Scale = scale; MapView = mapView; }
        internal bool Valid => Frame >= 0 && Width > 0 && Height > 0 && Scale > 0
            && !float.IsNaN(Scale) && !float.IsInfinity(Scale);
    }

    internal sealed class ObservationLease
    {
        internal readonly ObservationEpochs Owner;
        internal readonly Scope Scope;
        internal readonly long Safety, Readiness, Source;
        internal ObservationLease(ObservationEpochs owner, Scope scope, long safety, long readiness, long source)
        { Owner = owner; Scope = scope; Safety = safety; Readiness = readiness; Source = source; }
    }

    // Private process-local authorization, never a wire identity or native-render witness.
    // All reads/releases and invalidations serialize here, including transition-worker edges.
    internal sealed class ObservationEpochs
    {
        internal const int MaxOwners = 8;
        private readonly object gate = new object();
        private readonly IObservationInvalidationSink?[] owners = new IObservationInvalidationSink?[MaxOwners];
        private long safetyEpoch = 1, readinessEpoch = 1, sourceEpoch = 1;
        private int active, sampleThread;
        private bool ready, sampled, failed;
        private DeveloperSafety safety;
        private Scope? scope;
        private FrameGeometry? geometry;

        internal void Register(IObservationInvalidationSink owner)
        {
            lock (gate)
            {
                if (owner == null) throw new ArgumentNullException(nameof(owner));
                for (int i = 0; i < owners.Length; i++)
                    if (ReferenceEquals(owners[i], owner)) throw new InvalidOperationException("Owner already registered.");
                for (int i = 0; i < owners.Length; i++)
                    if (owners[i] == null) { owners[i] = owner; return; }
                throw new InvalidOperationException("Observation owner limit.");
            }
        }
        internal void Unregister(IObservationInvalidationSink owner)
        {
            lock (gate)
                for (int i = 0; i < owners.Length; i++)
                    if (ReferenceEquals(owners[i], owner)) { owners[i] = null; return; }
        }
        internal void BeginBoundary(bool preserveHistory = false)
        {
            lock (gate)
            {
                if (active == int.MaxValue) failed = true;
                else active++;
                InvalidateLocked(ObservationInvalidation.All, preserveHistory && !failed);
            }
        }
        internal void EndBoundary(bool preserveHistory = false)
        {
            lock (gate)
            {
                if (active == 0) failed = true;
                else active--;
                InvalidateLocked(ObservationInvalidation.All, preserveHistory && !failed);
            }
        }
        internal void Invalidate(ObservationInvalidation cause = ObservationInvalidation.All)
        { lock (gate) InvalidateLocked(cause); }

        private void InvalidateLocked(ObservationInvalidation cause, bool preserveHistory = false)
        {
            // Revocation precedes callbacks, including callbacks that throw. Never wrap/recycle.
            if (cause == 0 || (cause & ~ObservationInvalidation.All) != 0) cause = ObservationInvalidation.All;
            sampled = false;
            Advance(ref safetyEpoch, (cause & ObservationInvalidation.Safety) != 0);
            Advance(ref readinessEpoch, (cause & ObservationInvalidation.Readiness) != 0);
            Advance(ref sourceEpoch, (cause & ObservationInvalidation.Source) != 0);
            for (int i = 0; i < owners.Length; i++)
                try { if (!preserveHistory || !(owners[i] is IObservationBoundarySink)) owners[i]?.Invalidate(cause); }
                catch { failed = true; } // Still notify every other owner; permanently close admission.
        }
        private void Advance(ref long epoch, bool changed)
        {
            if (!changed) return;
            if (epoch == long.MaxValue) failed = true;
            else epoch++;
        }
        internal void Observe(bool isReady, DeveloperSafety evidence, Scope? current, FrameGeometry? frame, bool withinBudget)
        {
            lock (gate) ObserveLocked(isReady, evidence, current, frame, withinBudget);
        }
        private void ObserveLocked(bool isReady, DeveloperSafety evidence, Scope? current, FrameGeometry? frame, bool withinBudget)
        {
            int thread = Thread.CurrentThread.ManagedThreadId;
            if (sampleThread == 0) sampleThread = thread;
            if (sampleThread != thread)
            {
                InvalidateLocked(ObservationInvalidation.All);
                ready = false; safety = DeveloperSafety.Unknown; scope = null; geometry = null;
                throw new InvalidOperationException("Game thread required.");
            }
            ObservationInvalidation change = 0;
            if (ready != isReady) change |= ObservationInvalidation.Readiness | ObservationInvalidation.Safety;
            if (safety != evidence || evidence != DeveloperSafety.Safe) change |= ObservationInvalidation.Safety;
            if (scope == null || current == null || !scope.Same(current))
                change |= ObservationInvalidation.Source | ObservationInvalidation.Safety;
            bool validFrame = frame != null && frame.Valid;
            if (!validFrame || geometry == null || frame!.Frame < geometry.Frame
                || frame.Frame - geometry.Frame > 1 || frame.Width != geometry.Width
                || frame.Height != geometry.Height || frame.Scale != geometry.Scale)
                change |= ObservationInvalidation.Source | ObservationInvalidation.Safety;
            if (!withinBudget || !isReady || current == null) change |= ObservationInvalidation.All;
            if (change != 0) InvalidateLocked(change);
            ready = isReady; safety = evidence; scope = current; geometry = validFrame ? frame : null;
            sampled = withinBudget && isReady && evidence == DeveloperSafety.Safe && current != null && validFrame && active == 0 && !failed;
        }
        internal WireError? Refusal
        {
            get { lock (gate) return RefusalLocked(); }
        }
        private WireError? RefusalLocked()
        {
            // Known developer evidence wins even during loading or after scope replacement.
            if (safety == DeveloperSafety.Unsafe) return WireError.ObservationRefused;
            if (failed || active != 0 || !ready) return WireError.NotReady;
            if (safety != DeveloperSafety.Safe) return WireError.ObservationRefused;
            return sampled ? (WireError?)null : WireError.NotReady;
        }
        internal ObservationLease Acquire(Scope requested)
        {
            lock (gate)
            {
                if (sampleThread != 0 && sampleThread != Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Game thread required.");
                WireError? error = RefusalLocked();
                if (error.HasValue) throw new WireException(error.Value);
                if (scope == null || !scope.Same(requested)) throw new WireException(WireError.StaleScope);
                return new ObservationLease(this, requested, safetyEpoch, readinessEpoch, sourceEpoch);
            }
        }
        // Trusted producer supplies a fresh game-thread sample under its shared deadline and
        // transition read fence, then transfers primitives while that same fence is still held.
        // This is not permission to reuse cached samples or to serialize game objects.
        internal void Release(ObservationLease lease, bool isReady, DeveloperSafety evidence,
            Scope? current, FrameGeometry? frame, bool withinBudget, Action transfer)
        {
            lock (gate)
            {
                ObserveLocked(isReady, evidence, current, frame, withinBudget);
                WireError? error = RefusalLocked();
                if (error.HasValue) throw new WireException(error.Value);
                if (current == null || !lease.Scope.Same(current)) throw new WireException(WireError.StaleScope);
                if (!ReferenceEquals(lease.Owner, this) || lease.Safety != safetyEpoch
                    || lease.Readiness != readinessEpoch || lease.Source != sourceEpoch)
                    throw new WireException(WireError.NotReady);
                // Trusted bounded primitive transfer only, never client-supplied game work.
                // Worker invalidation cannot race between final admission and ownership transfer.
                transfer();
            }
        }
    }
    // State carried from a setter prefix to its finalizer. None = prefix never ran (skipped original).
    internal enum SettingWrite : byte { None = 0, Bracketed = 1, UnchangedOff = 2, UnchangedOn = 3 }

    // Bracket for a developer-relevant boolean setter. A write that stores the value the setting already holds
    // cannot change sampled state (installed Prefs.DevMode guards every side effect with a changed-value check),
    // and Options > General writes it every GUI frame, which would otherwise revoke render provenance forever.
    // Fail-closed: if anything differs after the original ran (foreign patch, exception) or the prefix never
    // ran, the bracket is applied retroactively exactly like the unconditional one.
    internal static class SettingWriteBracket
    {
        internal static SettingWrite Begin(ObservationEpochs epochs, bool incoming, bool current)
        {
            if (incoming == current) return current ? SettingWrite.UnchangedOn : SettingWrite.UnchangedOff;
            epochs.BeginBoundary(); return SettingWrite.Bracketed;
        }
        internal static void End(ObservationEpochs epochs, ref SettingWrite state, bool after, bool failed)
        {
            SettingWrite entered = state; state = SettingWrite.None;
            switch (entered)
            {
                case SettingWrite.Bracketed: epochs.EndBoundary(); return;
                case SettingWrite.UnchangedOff:
                case SettingWrite.UnchangedOn:
                    if (failed || after != (entered == SettingWrite.UnchangedOn)) { epochs.BeginBoundary(); epochs.EndBoundary(); }
                    return;
                default: epochs.Invalidate(); return;
            }
        }
    }
}
