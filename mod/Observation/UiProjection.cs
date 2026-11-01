using System;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    internal static class UiProjection
    {
        internal const int MaxSelection = 64, MaxWindows = 32;
        internal static JObject Selection(string label, string id, bool pawn)
        {
            if (JobProjection.Public(label) == null || !Ids.Is(id, pawn ? "p" : "t"))
                throw new ArgumentException("Invalid selection.");
            return new JObject { ["label"] = label, [pawn ? "pawn" : "thing"] = id };
        }
        // Kinds are bridge constants supplied only after exact window-type admission. Never
        // titles, window contents, control labels, geometry or inferred render visibility.
        internal static JObject Window(string kind)
        {
            if (string.IsNullOrEmpty(kind) || kind.Length > 64) throw new ArgumentException("Invalid window kind.");
            foreach (char c in kind) if (!(c >= 'a' && c <= 'z') && c != '_') throw new ArgumentException("Invalid window kind.");
            return new JObject { ["kind"] = kind };
        }
        internal static JObject Build(SampleStamp sample, JArray? selection, JArray windows)
        {
            if (selection?.Count > MaxSelection || windows.Count > MaxWindows) throw new ArgumentException("UI limit.");
            var result = new JObject { ["sample"] = sample.Json(), ["windows"] = windows.DeepClone() };
            if (selection != null) result["selection"] = selection.DeepClone();
            return result;
        }
    }
}
