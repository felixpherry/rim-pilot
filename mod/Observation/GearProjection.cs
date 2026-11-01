using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimPilot.Mod.Observation
{
    // One gear-tab row already mirrored by the game adapter: capitalized Thing.LabelCap and the
    // tab section it is drawn in (equipment includes worn belts).
    internal sealed class GearEntry
    {
        internal readonly string Label;
        internal readonly bool Equipment;
        internal GearEntry(string label, bool equipment) { Label = label; Equipment = equipment; }
    }

    // Pure gear-tab projection (docs/visibility-audit.md#gear). No game types.
    internal static class GearProjection
    {
        internal const int MaxItems = 32;

        // Inputs StatWorker.GetValue combines for MaxHitPoints on a non-pawn thing once every
        // comp is proven neutral and no ability/stat-factor/part/post-process input exists.
        internal sealed class MaxHitPointsInputs
        {
            internal float Base, StuffFactor = 1f, StuffOffset, ScenarioFactor = 1f;
            internal bool HasStuff, ApplyFactorsIfNegative;
            internal float RoundToFiveOver = float.MaxValue, Min = float.MinValue, Max = float.MaxValue;
            internal bool RoundValue;
        }

        // GetValueUnfinalized (stuff block; quality-dependent stuff modifiers for the stat are
        // refused by the adapter, so their factor 1 / offset 0 are omitted) + FinalizeValue, then Thing.MaxHitPoints' RoundToInt.
        // Mathf equivalents: Round = Math.Round (banker's), RoundToInt = (int)Math.Round.
        internal static int? MaxHitPoints(MaxHitPointsInputs input)
        {
            if (input == null) return null;
            float num = input.Base;
            if (input.HasStuff)
            {
                if (num > 0f || input.ApplyFactorsIfNegative) num *= input.StuffFactor;
                num += input.StuffOffset;
            }
            num *= input.ScenarioFactor;
            if (Math.Abs(num) > input.RoundToFiveOver) num = (float)Math.Round(num / 5f) * 5f;
            if (input.RoundValue) num = (int)Math.Round(num);
            if (num < input.Min) num = input.Min;
            else if (num > input.Max) num = input.Max;
            if (!PrimitiveProjection.Finite(num) || Math.Abs(num) > int.MaxValue / 2) return null;
            return (int)Math.Round(num);
        }

        // Both installed worker caches store the float before Thing.MaxHitPoints' RoundToInt.
        // Never replace a bad cache value with a recomputed value the normal UI would not use.
        internal static int? CachedHitPoints(float value) => !PrimitiveProjection.Finite(value) || Math.Abs(value) > int.MaxValue / 2
            ? (int?)null : (int)Math.Round(value);

        // GenText.ToStringPercent(f) = (f * 100).ToStringDecimalIfSmall() + "%".
        internal static string Percent(float fraction)
        {
            float f = fraction * 100f;
            string text = Math.Abs(f) < 1f ? Math.Round(f, 2).ToString("0.##")
                : Math.Abs(f) < 10f ? Math.Round(f, 1).ToString("0.#")
                : ((int)Math.Round(f)).ToString();
            return text + "%";
        }

        // GenLabel.LabelExtras: " (quality hp% T)" with the present parts, or "".
        internal static string Extras(string? quality, string? hitPoints, string? wornByCorpse)
        {
            if (quality == null && hitPoints == null && wornByCorpse == null) return "";
            var parts = new List<string>(3);
            if (quality != null) parts.Add(quality);
            if (hitPoints != null) parts.Add(hitPoints);
            if (wornByCorpse != null) parts.Add(wornByCorpse);
            return " (" + string.Join(" ", parts) + ")";
        }

        // Equipment section first (equipment list, then worn belts in worn order), then apparel
        // ordered by first body-part group listOrder descending (stable, like OrderByDescending).
        internal static List<int> ApparelOrder(IReadOnlyList<int> listOrders)
        {
            var order = new List<int>(listOrders.Count);
            for (int i = 0; i < listOrders.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                int by = listOrders[b].CompareTo(listOrders[a]);
                return by != 0 ? by : a.CompareTo(b);
            });
            return order;
        }

        // Wire rows in tab order. Null = not exactly projectable (omit the family).
        internal static JArray? Rows(IReadOnlyList<GearEntry> entries)
        {
            if (entries == null || entries.Count > MaxItems) return null;
            var rows = new JArray();
            bool apparelSeen = false;
            foreach (GearEntry entry in entries)
            {
                if (entry == null || HealthProjection.Plain(entry.Label) == null || (entry.Equipment && apparelSeen)) return null;
                if (!entry.Equipment) apparelSeen = true;
                rows.Add(new JObject { ["label"] = entry.Label, ["slot"] = entry.Equipment ? "equipment" : "apparel" });
            }
            return rows;
        }
    }
}
