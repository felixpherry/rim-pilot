using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimPilot.Protocol
{
    internal enum CapabilityState { Available, Unsupported, Unverified }

    internal sealed class Capability
    {
        internal string Name { get; }
        internal CapabilityState State { get; }
        internal string Schema { get; }
        internal Capability(string name, CapabilityState state, string schema)
        {
            Name = Text(name, 64); State = state; Schema = Text(schema, 64);
        }
        internal JObject Json()
        {
            string state = State == CapabilityState.Available ? "available" : State == CapabilityState.Unsupported ? "unsupported" : "unverified";
            return new JObject { ["name"] = Name, ["state"] = state, ["schema"] = Schema };
        }
        internal static string Text(string value, int limit)
        {
            if (value == null || value.Length > limit) throw new WireException(WireError.ResponseTooLarge);
            return value;
        }
    }

    internal static class Responses
    {
        internal static JObject Success(string id, JObject data)
        {
            if (!Request.ValidRequestId(id)) throw new WireException(WireError.InvalidRequest);
            return new JObject { ["v"] = WireLimits.Version, ["id"] = id, ["ok"] = true, ["data"] = data };
        }
        internal static JObject Error(string? id, WireError code)
        {
            // Only validated correlation IDs may be echoed, NEVER token/input/target/exception text.
            var error = new JObject { ["code"] = ErrorName(code) };
            var result = new JObject { ["v"] = WireLimits.Version, ["ok"] = false, ["error"] = error };
            if (id != null && Request.ValidRequestId(id)) result["id"] = id;
            return result;
        }
        internal static string ErrorName(WireError code)
        {
            switch (code)
            {
                case WireError.InvalidFrame: return "invalid_frame";
                case WireError.InvalidJson: return "invalid_json";
                case WireError.InvalidRequest: return "invalid_request";
                case WireError.VersionMismatch: return "version_mismatch";
                case WireError.AuthenticationFailed: return "authentication_failed";
                case WireError.ForbiddenMethod: return "forbidden_method";
                case WireError.UnsupportedMethod: return "unsupported_method";
                case WireError.NotReady: return "not_ready";
                case WireError.ObservationRefused: return "observation_refused";
                case WireError.Unavailable: return "unavailable";
                case WireError.StaleScope: return "stale_scope";
                case WireError.CursorExpired: return "cursor_expired";
                case WireError.EventGap: return "event_gap";
                case WireError.Busy: return "busy";
                case WireError.RateLimited: return "rate_limited";
                case WireError.Timeout: return "timeout";
                case WireError.ResponseTooLarge: return "response_too_large";
                case WireError.InternalError: return "internal_error";
                default: throw new ArgumentOutOfRangeException(nameof(code));
            }
        }
        internal static JObject Capabilities(IReadOnlyList<Capability> capabilities)
        {
            if (capabilities.Count > 64) throw new WireException(WireError.ResponseTooLarge);
            var items = new JArray();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (Capability capability in capabilities)
            {
                if (!names.Add(capability.Name)) throw new WireException(WireError.InvalidRequest);
                items.Add(capability.Json());
            }
            return new JObject { ["capabilities"] = items };
        }
        internal static JObject Page(SampleStamp sample, string snapshot, JArray eligibleItems, string? next)
        {
            if (!Ids.Is(snapshot, "q") || (next != null && !Ids.Is(next, "q")) || eligibleItems.Count > WireLimits.PageItems)
                throw new WireException(WireError.InvalidRequest);
            // Items MUST already be projected by an audited named DTO mapper. Not a generic serializer.
            // No source count, scanned count, offset, hidden ID or raw lister revision is permitted here.
            var result = new JObject { ["sample"] = sample.Json(), ["snapshot"] = snapshot,
                ["basis"] = "eligible_observed_snapshot", ["items"] = eligibleItems.DeepClone(), ["complete"] = next == null };
            if (next != null) result["next"] = next;
            return result;
        }
        internal static JObject Capture(CompletedRenderStamp completed, int width, int height, double scale, byte[] png)
        {
            if (width < 1 || height < 1 || width > WireLimits.CaptureWidth
                || height > WireLimits.CaptureHeight || (long)width * height > WireLimits.CapturePixels
                || double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0 || scale > 8
                || png == null || png.Length < 8 || png.Length > WireLimits.CapturePngBytes)
                throw new WireException(WireError.ResponseTooLarge);
            // Stamp requires safe completion evidence, not proof that an adapter collected it correctly.
            // Future adapter must CheckRelease again after encoding and before publication.
            string encoded = Convert.ToBase64String(png);
            var chunks = new JArray();
            for (int i = 0; i < encoded.Length; i += WireLimits.CaptureChunkChars)
                chunks.Add(encoded.Substring(i, Math.Min(WireLimits.CaptureChunkChars, encoded.Length - i)));
            return new JObject { ["scope"] = completed.Scope.Json(), ["capture"] = completed.Capture, ["frame"] = completed.Frame,
                ["phase"] = "completed_render", ["width"] = width, ["height"] = height, ["scale"] = scale,
                ["encoding"] = "png_base64_chunks", ["bytes"] = png.Length, ["chunks"] = chunks };
        }
    }

    // Private traversal accounting only. No raw counters/positions are wire fields or cursor material.
    // An adapter must also enforce global wall time, cancellation and main-thread slice deadline.
    internal sealed class ScanBudget
    {
        private int scanned;
        private int slice;
        internal void BeginUpdate() { slice = 0; }
        internal bool TryExamine()
        {
            if (slice == WireLimits.CandidatesPerUpdate || scanned == WireLimits.ScanCandidates) return false;
            slice++; scanned++; return true;
        }
    }
}
