namespace RimPilot.Protocol
{
    // Evidence contracts only: game-thread adapters (GamePublicEvents, GameNativeRender, GameDeveloperSafety) supply these facts.
    // No game objects, getter evaluation, hooks, epoch tracker, event ring or renderer here.
    internal enum PublicationDisposition
    {
        Attempted, Delayed, Rejected, DuplicateRefresh, RestoredHistory, AcceptedPublic
    }

    internal static class PublicEventPolicy
    {
        internal static bool CanPublish(PublicationDisposition disposition, bool auditedPayload,
            bool firstCanonicalPublication, bool observationSafe)
        {
            return disposition == PublicationDisposition.AcceptedPublic && auditedPayload
                && firstCanonicalPublication && observationSafe;
        }
    }

    internal static class DeveloperObservationPolicy
    {
        // Baseline is the finite explicit manifest in protocol/developer-safety-v1.json.
        // auditComplete includes known-panel ancestry and every required static flag read.
        internal static bool IsUnsafe(bool developerMode, bool knownDeveloperPanelPresent,
            bool debugBaselineMatches, bool auditComplete)
        {
            return developerMode || knownDeveloperPanelPresent || !debugBaselineMatches || !auditComplete;
        }
    }

    internal sealed class CaptureRequestStamp
    {
        internal Scope Scope { get; }
        internal long SafetyEpoch { get; }
        internal long AfterFrame { get; }
        internal CaptureRequestStamp(Scope scope, long safetyEpoch, long afterFrame)
        {
            if (safetyEpoch < 1 || afterFrame < 0) throw new WireException(WireError.InvalidRequest);
            Scope = scope; SafetyEpoch = safetyEpoch; AfterFrame = afterFrame;
        }
    }

    internal sealed class RenderEvidence
    {
        internal Scope Scope { get; }
        internal long SafetyEpoch { get; }
        internal long Frame { get; }
        internal bool Completed { get; }
        internal bool WholeFrameSafe { get; }
        // Approved weaker map-view proof; never describes complete primary-color replacement.
        internal bool MapViewSafe { get; }
        internal RenderEvidence(Scope scope, long safetyEpoch, long frame, bool completed, bool wholeFrameSafe,
            bool mapViewSafe = false)
        {
            if (safetyEpoch < 1 || frame < 0) throw new WireException(WireError.InvalidRequest);
            Scope = scope; SafetyEpoch = safetyEpoch; Frame = frame;
            Completed = completed; WholeFrameSafe = wholeFrameSafe; MapViewSafe = mapViewSafe;
        }
    }

    internal sealed class CompletedRenderStamp
    {
        internal Scope Scope { get; }
        internal string Capture { get; }
        internal long Frame { get; }
        // Private authorization identity, never serialized or derived from frame/tick.
        internal long SafetyEpoch { get; }
        private CompletedRenderStamp(RenderEvidence rendered)
        {
            Scope = rendered.Scope; Frame = rendered.Frame; SafetyEpoch = rendered.SafetyEpoch;
            Capture = Ids.New("c");
        }
        internal static CompletedRenderStamp Complete(CaptureRequestStamp request, RenderEvidence rendered,
            Scope currentScope, long currentSafetyEpoch, WireError? currentReadiness)
        {
            Check(request.Scope, request.SafetyEpoch, currentScope, currentSafetyEpoch, currentReadiness);
            if (!request.Scope.Same(rendered.Scope)) throw new WireException(WireError.StaleScope);
            if (rendered.SafetyEpoch != request.SafetyEpoch || !rendered.Completed
                || !(rendered.WholeFrameSafe || rendered.MapViewSafe && rendered.Scope.Game != null)
                || rendered.Frame <= request.AfterFrame) throw new WireException(WireError.NotReady);
            return new CompletedRenderStamp(rendered);
        }
        internal void CheckRelease(Scope currentScope, long currentSafetyEpoch, WireError? currentReadiness)
        {
            Check(Scope, SafetyEpoch, currentScope, currentSafetyEpoch, currentReadiness);
        }
        private static void Check(Scope expected, long epoch, Scope current, long currentEpoch, WireError? readiness)
        {
            // Developer refusal takes precedence, including while loading. Readiness is sampled
            // again on game thread before completion AND release; not a client-supplied assertion.
            if (readiness == WireError.ObservationRefused) throw new WireException(WireError.ObservationRefused);
            if (!expected.Same(current)) throw new WireException(WireError.StaleScope);
            if (readiness.HasValue) throw new WireException(readiness.Value);
            if (epoch != currentEpoch) throw new WireException(WireError.NotReady);
        }
    }
}
