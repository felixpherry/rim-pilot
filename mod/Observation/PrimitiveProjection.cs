using System;
using System.Text;

namespace RimPilot.Mod.Observation
{
    internal sealed class NeedBarPrimitive
    {
        internal readonly string Label;
        internal readonly float LevelFraction;
        internal NeedBarPrimitive(string label, float levelFraction) { Label = label; LevelFraction = levelFraction; }
    }

    internal static class PrimitiveProjection
    {
        internal const int MaxLabelChars = 512, MaxLabelBytes = 2048;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        // Validate BEFORE any formatting/label/getter helper is invoked, never truncate a
        // diagnostic or hidden suffix into a seemingly complete public value.
        internal static ProjectionResult Label(string? text, ProjectionBudget budget, out string? value)
        {
            value = null;
            if (!budget.Charge()) return ProjectionResult.BudgetExceeded;
            if (string.IsNullOrEmpty(text) || text!.Length > MaxLabelChars) return ProjectionResult.Unverified;
            int bytes;
            try { bytes = Utf8.GetByteCount(text); }
            catch (EncoderFallbackException) { return ProjectionResult.Unverified; }
            if (bytes > MaxLabelBytes) return ProjectionResult.Unverified;
            if (!budget.Charge(1, bytes)) return ProjectionResult.BudgetExceeded;
            value = text; return ProjectionResult.Value;
        }

        // Equivalent to unpatched Def.LabelCap for plain labels, without populating its cache.
        // Tagged/markup labels require a separate audited resolution path. A pre-existing cache
        // must agree; otherwise a changed definition cannot be called a normal displayed label.
        internal static ProjectionResult DefinitionLabel(string? raw, string? cached, ProjectionBudget budget, out string? value)
        {
            value = null;
            ProjectionResult result = Label(raw, budget, out string? bounded);
            if (result != ProjectionResult.Value) return result;
            if (bounded!.IndexOf('<') >= 0 || bounded.IndexOf('>') >= 0
                || bounded.IndexOf("(*", StringComparison.Ordinal) >= 0 || bounded.IndexOf("*)", StringComparison.Ordinal) >= 0)
                return ProjectionResult.Unverified;
            string capitalized = bounded.Length == 1 ? bounded.ToUpper(System.Globalization.CultureInfo.CurrentCulture)
                : char.ToUpper(bounded[0], System.Globalization.CultureInfo.CurrentCulture) + bounded.Substring(1);
            if (!string.IsNullOrEmpty(cached) && !string.Equals(cached, capitalized, StringComparison.Ordinal))
                return ProjectionResult.Unverified;
            return Label(capitalized, budget, out value);
        }

        internal static ProjectionResult NeedBar(string? label, float current, float maximum,
            ProjectionBudget budget, out NeedBarPrimitive? value)
        {
            value = null;
            if (!budget.Charge()) return ProjectionResult.BudgetExceeded;
            if (!Finite(current) || !Finite(maximum) || maximum <= 0 || current < 0 || current > maximum)
                return ProjectionResult.Unverified;
            float fraction = current / maximum;
            if (!Finite(fraction)) return ProjectionResult.Unverified;
            ProjectionResult result = Label(label, budget, out string? bounded);
            if (result != ProjectionResult.Value) return result;
            // Primitive-only private value. Publication still requires whole-family completeness,
            // current eligibility, shared limits and ObservationEpochs.Release.
            value = new NeedBarPrimitive(bounded!, fraction);
            return ProjectionResult.Value;
        }
        // Normal generic list uses ShowOnNeedList. Mood has an independent normal pane,
        // admitted only for its exact tracker.mood reference, not every hidden need.
        internal static bool NeedShown(bool showOnNeedList, bool exactNormalMoodPane) => showOnNeedList || exactNormalMoodPane;
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
