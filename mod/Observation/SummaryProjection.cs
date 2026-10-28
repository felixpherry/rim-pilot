using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    internal sealed class MapSummaryRow
    {
        internal readonly string Map, Label;
        internal readonly int? Width, Height, OwnPawns;
        internal MapSummaryRow(string map, string label, int? width, int? height, int? ownPawns)
        { Map = map; Label = label; Width = width; Height = height; OwnPawns = ownPawns; }
    }

    internal sealed class ManifestRow
    {
        internal readonly string Id, Label;
        internal ManifestRow(string id, string label) { Id = id; Label = label; }
    }

    // Pure summary DTO assembly from already-audited primitives (docs/visibility-audit.md#summary). Optional
    // inputs that are null are omitted (unavailable), never replaced by empty/zero values.
    internal static class SummaryProjection
    {
        internal const int MaxMaps = 64, MaxDlcs = 16, MaxMods = 256, MaxDimension = 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // Whole-value check, never truncation: non-empty, bounded chars and valid UTF-8 size.
        internal static string? Text(string? text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || text!.Length > maxChars) return null;
            try { return Utf8.GetByteCount(text) <= maxChars * 4 ? text : null; }
            catch (EncoderFallbackException) { return null; }
        }

        // Plain definition label shown capitalized; markup needs its own audited resolution.
        internal static string? DefinitionLabel(string? raw)
        {
            string? text = Text(raw, PrimitiveProjection.MaxLabelChars);
            if (text == null || text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0) return null;
            return char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        internal static int? Dimension(int value) => value >= 1 && value <= MaxDimension ? value : (int?)null;

        internal static JObject Build(SampleStamp sample, string build, bool? paused, string? currentMap,
            IReadOnlyList<MapSummaryRow>? maps, IReadOnlyList<ManifestRow>? dlcs, IReadOnlyList<ManifestRow>? mods) =>
            Build(sample, build, paused, currentMap, maps, dlcs?.Count, mods?.Count,
                dlcs == null ? null : Manifest(dlcs), mods == null ? null : Manifest(mods));

        // Rows already validated and converted one at a time (ManifestRowJson), so a slow host can
        // spread the conversion over several charged updates. Counts must match the arrays.
        internal static JObject Build(SampleStamp sample, string build, bool? paused, string? currentMap,
            IReadOnlyList<MapSummaryRow>? maps, int? dlcCount, int? modCount, JArray? dlcRows, JArray? modRows)
        {
            if (dlcCount != dlcRows?.Count || modCount != modRows?.Count) throw new ArgumentException("Invalid summary.");
            bool playing = sample.Scope.Game != null;
            if (sample.Scope.Map != null || Text(build, PrimitiveProjection.MaxLabelChars) == null
                || (!playing && (paused.HasValue || currentMap != null || maps != null || sample.Tick.HasValue))
                || (currentMap != null && !Ids.Is(currentMap, "m"))
                || (maps != null && maps.Count > MaxMaps) || (dlcCount > MaxDlcs) || (modCount > MaxMods)) throw new ArgumentException("Invalid summary.");
            var result = new JObject { ["sample"] = sample.Json(), ["scene"] = playing ? "playing" : "menu", ["build"] = build };
            if (paused.HasValue) result["paused"] = paused.Value;
            if (currentMap != null) result["currentMap"] = currentMap;
            if (maps != null)
            {
                var rows = new JArray();
                foreach (MapSummaryRow map in maps)
                {
                    if (map == null || !Ids.Is(map.Map, "m") || Text(map.Label, PrimitiveProjection.MaxLabelChars) == null
                        || (map.Width.HasValue && Dimension(map.Width.Value) == null)
                        || (map.Height.HasValue && Dimension(map.Height.Value) == null)
                        || (map.OwnPawns.HasValue && (map.OwnPawns < 0 || map.OwnPawns > WireLimits.SnapshotItems)))
                        throw new ArgumentException("Invalid summary.");
                    var row = new JObject { ["map"] = map.Map, ["label"] = map.Label };
                    if (map.Width.HasValue) row["width"] = map.Width.Value;
                    if (map.Height.HasValue) row["height"] = map.Height.Value;
                    if (map.OwnPawns.HasValue) row["eligibleOwnPawns"] = map.OwnPawns.Value;
                    rows.Add(row);
                }
                result["maps"] = rows;
            }
            if (dlcRows != null) result["dlcs"] = dlcRows;
            if (modRows != null) result["mods"] = modRows;
            return result;
        }

        private static JArray Manifest(IReadOnlyList<ManifestRow> entries)
        {
            var rows = new JArray();
            foreach (ManifestRow entry in entries) rows.Add(ManifestRowJson(entry));
            return rows;
        }

        // Replacing the property value (unlike removing the property) clears the staged array's
        // parent, so a retry after a yielded charge reuses the rows instead of cloning them.
        internal static void DetachManifests(JObject summary)
        {
            if (summary["dlcs"] != null) summary["dlcs"] = JValue.CreateNull();
            if (summary["mods"] != null) summary["mods"] = JValue.CreateNull();
        }

        internal static JObject ManifestRowJson(ManifestRow entry)
        {
            if (entry == null || Text(entry.Id, 256) == null || Text(entry.Label, PrimitiveProjection.MaxLabelChars) == null)
                throw new ArgumentException("Invalid summary.");
            return new JObject { ["id"] = entry.Id, ["label"] = entry.Label };
        }
    }
}
