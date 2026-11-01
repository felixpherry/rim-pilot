using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace RimPilot.Mod.Observation
{
    // Pure shown-needs projection (docs/visibility-audit.md#own-pawns). The game adapter
    // supplies primitives read from audited fields; nothing here touches the game.
    internal static class NeedsProjection
    {
        // Normal list sorts with List.Sort (installed Mono introsort). Up to 16 entries it uses
        // fixed swaps for 2-3 items and insertion sort for 4-16; larger lists would need the
        // partition/heap path, so they have no exact equivalent here.
        internal const int MaxShown = 16, MaxNeeds = 32;
        // Need_Food.MaxLevel asks the stat worker with cacheStaleAfterTicks = 15.
        internal const int StatCacheStaleTicks = 15;

        // Indexes of shown needs (AllNeeds order) rearranged exactly as
        // PawnNeedsUIUtility.SortInDisplayOrder would: comparer(a, b) = b.listPriority vs a.
        // Note 3 equal-priority-adjacent items are NOT kept stable by the installed sort.
        internal static int[]? DisplayOrder(IReadOnlyList<int> listPriority)
        {
            if (listPriority == null || listPriority.Count > MaxShown) return null;
            var keys = new int[listPriority.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = i;
            int Compare(int a, int b) => listPriority[b].CompareTo(listPriority[a]);
            void SwapIfGreater(int a, int b)
            {
                if (a != b && Compare(keys[a], keys[b]) > 0) { int t = keys[a]; keys[a] = keys[b]; keys[b] = t; }
            }
            int hi = keys.Length - 1;
            switch (keys.Length)
            {
                case 0: case 1: break;
                case 2: SwapIfGreater(0, 1); break;
                case 3: SwapIfGreater(0, 1); SwapIfGreater(0, 2); SwapIfGreater(1, 2); break;
                default:
                    for (int i = 0; i < hi; i++)
                    {
                        int j = i, value = keys[i + 1];
                        while (j >= 0 && Compare(value, keys[j]) < 0) { keys[j + 1] = keys[j]; j--; }
                        keys[j + 1] = value;
                    }
                    break;
            }
            return keys;
        }

        // StatWorker.GetValue(thing, applyPostProcess, 15): a cached entry younger than 15 ticks
        // is returned as-is. Older/absent entries are recomputed by the normal UI.
        internal static bool CacheFresh(int ticksGame, int entryTick) =>
            (long)ticksGame - entryTick < StatCacheStaleTicks;

        // Inputs that StatWorker.GetValueUnfinalized + FinalizeValue combine for MaxNutrition
        // once every other source is proven not to reference the stat (adding 0 and multiplying
        // by 1 leave a float unchanged). Same operation order as the installed worker.
        internal sealed class MaxNutritionInputs
        {
            internal float Base, NoSkillOffset = 0f, NoSkillFactor = 1f;
            internal bool NoSkills;
            internal float[] HediffOffsets = new float[0], HediffFactors = new float[0];
            internal float BodySizeFactor = 1f, BaseBodySize = 1f, FoodMaxFactor = 1f, ScenarioFactor = 1f;
            internal float RoundToFiveOver = float.MaxValue, Min = float.MinValue, Max = float.MaxValue;
            internal bool RoundValue;
        }

        internal static float? MaxNutrition(MaxNutritionInputs input)
        {
            if (input == null || input.HediffOffsets == null || input.HediffFactors == null) return null;
            float num = input.Base;
            if (input.NoSkills) num += input.NoSkillOffset;
            foreach (float offset in input.HediffOffsets) num += offset;
            foreach (float factor in input.HediffFactors) num *= factor;
            if (input.NoSkills) num *= input.NoSkillFactor;
            // FinalizeValue: StatPart_BodySize (Pawn.BodySize product first), StatPart_LifeStageMaxFood,
            // scenario factor, round-to-five, roundValue, clamp. Mathf equivalents: Round =
            // Math.Round (banker's), RoundToInt = (int)Math.Round, Clamp = min-then-max test.
            float bodySize = input.BodySizeFactor * input.BaseBodySize;
            num *= bodySize;
            num *= input.FoodMaxFactor;
            num *= input.ScenarioFactor;
            if (Math.Abs(num) > input.RoundToFiveOver) num = (float)Math.Round(num / 5f) * 5f;
            if (input.RoundValue) num = (int)Math.Round(num);
            if (num < input.Min) num = input.Min;
            else if (num > input.Max) num = input.Max;
            return PrimitiveProjection.Finite(num) ? num : (float?)null;
        }

        // One shown bar: Need.LabelCap (= Def.LabelCap, mirrored without filling its cache) and
        // CurLevelPercentage = CurLevel / MaxLevel. A present label cache must agree; markup,
        // non-finite, non-positive maximum or out-of-range level is unknown, never clamped.
        internal static NeedBarPrimitive? Row(string? rawLabel, string? cachedLabel, float current, float maximum)
        {
            string? label = PawnProjection.Label("", rawLabel);
            if (label == null || (!string.IsNullOrEmpty(cachedLabel) && !string.Equals(cachedLabel, label, StringComparison.Ordinal)))
                return null;
            if (!PrimitiveProjection.Finite(current) || !PrimitiveProjection.Finite(maximum) || maximum <= 0
                || current < 0 || current > maximum) return null;
            float fraction = current / maximum;
            return PrimitiveProjection.Finite(fraction) && fraction >= 0 && fraction <= 1 ? new NeedBarPrimitive(label, fraction) : null;
        }

        // Wire rows: localized label and finite fraction in [0, 1], in display order.
        internal static JArray Json(IReadOnlyList<NeedBarPrimitive> needs)
        {
            if (needs == null || needs.Count > MaxNeeds) throw new ArgumentException("Invalid needs.");
            var rows = new JArray();
            foreach (NeedBarPrimitive need in needs)
            {
                if (need == null || string.IsNullOrEmpty(need.Label) || !PrimitiveProjection.Finite(need.LevelFraction)
                    || need.LevelFraction < 0 || need.LevelFraction > 1) throw new ArgumentException("Invalid needs.");
                rows.Add(new JObject { ["label"] = need.Label, ["level"] = (double)need.LevelFraction });
            }
            return rows;
        }
    }
}
