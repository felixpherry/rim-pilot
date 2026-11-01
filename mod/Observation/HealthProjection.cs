using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RimPilot.Mod.Observation
{
    // One normal health-card condition, already mirrored by the game adapter: capitalized row
    // label (Hediff.LabelCap), its UIGroupKey and its body part (Part < 0 = whole body).
    internal sealed class HealthEntry
    {
        internal readonly int Part, GroupKey;
        internal readonly float Priority;
        internal readonly string? PartLabel;
        internal readonly string Label;
        internal HealthEntry(int part, float priority, string? partLabel, int groupKey, string label)
        { Part = part; Priority = priority; PartLabel = partLabel; GroupKey = groupKey; Label = label; }
    }

    // What the health reader does with one condition before any label or comp is looked at.
    // Evaluate: the normal card may show it (visibility, comps and labels decide). Skip: the card
    // definitively hides it. Refuse: the card might show it but it is not exactly mirrored.
    internal enum HealthFate { Evaluate, Skip, Refuse }

    // Pure health-card projection (docs/visibility-audit.md#health-rows). Mirrors
    // HediffSet.CacheMissingPartsCommonAncestors and HealthCardUtility's grouping/ordering
    // without touching the game or filling its caches.
    internal static class HealthProjection
    {
        internal const int MaxEntries = 256, MaxRows = 64, MaxVisits = 512, MaxDepth = 64;
        // HealthCardUtility.GetListPriority(null).
        internal const float WholeBodyPriority = 9999999f;

        // Hidden state must never change field presence: a condition whose card visibility is
        // exactly mirrored (concealableShape) and currently hidden is skipped without looking at
        // its labels or comps; only a condition the card could show and that is not exactly
        // mirrored refuses the family. Missing/added parts (structural) feed the ancestor walk
        // and are never skipped.
        internal static HealthFate Fate(bool structural, bool admittedClass, bool concealableShape, bool hiddenByState)
        {
            if (!structural && concealableShape && hiddenByState) return HealthFate.Skip;
            return admittedClass ? HealthFate.Evaluate : HealthFate.Refuse;
        }

        // !Hediff.Visible for the base getter: Visible = visible flag || no current stage || stage.becomeVisible.
        internal static bool HiddenByFields(bool visibleFlag, bool hasStage, bool stageBecomesVisible) =>
            !visibleFlag && hasStage && !stageBecomesVisible;

        // !Hediff_RotStinkExposure.Visible: base visibility and the pawn lacking a lung rot condition.
        internal static bool RotStinkHidden(bool baseHidden, bool hasLungRot) => baseHidden || hasLungRot;

        // GetListPriority(rec) = (float)((int)rec.height * 10000) + rec.coverageAbsWithChildren.
        internal static float? Priority(int height, float coverage)
        {
            float value = (float)(height * 10000) + coverage;
            return PrimitiveProjection.Finite(value) ? value : (float?)null;
        }

        // Breadth-first from the core part: skip any part that has, or has an ancestor with, a
        // directly added part; stop at the first missing-part condition on a part; otherwise
        // queue its children in order. Null when a bound is exceeded or the tree is malformed.
        internal static List<THediff>? CommonAncestors<TPart, THediff>(TPart core, Func<TPart, TPart?> parent,
            Func<TPart, IList<TPart>?> children, Func<TPart, bool> addedHere, Func<TPart, THediff?> firstMissing)
            where TPart : class where THediff : class
        {
            if (core == null) return null;
            var result = new List<THediff>();
            var queue = new Queue<TPart>();
            queue.Enqueue(core);
            int visits = 0;
            while (queue.Count != 0)
            {
                if (++visits > MaxVisits) return null;
                TPart part = queue.Dequeue();
                bool? covered = AddedAbove(part, parent, addedHere);
                if (covered == null) return null;
                if (covered.Value) continue;
                THediff? missing = firstMissing(part);
                if (missing != null) { result.Add(missing); continue; }
                IList<TPart>? next = children(part);
                if (next == null) return null;
                foreach (TPart child in next)
                {
                    if (child == null) return null;
                    queue.Enqueue(child);
                }
            }
            return result;
        }

        // HediffSet.PartOrAnyAncestorHasDirectlyAddedParts for a non-null part.
        internal static bool? AddedAbove<TPart>(TPart part, Func<TPart, TPart?> parent, Func<TPart, bool> addedHere)
            where TPart : class
        {
            TPart? current = part;
            for (int depth = 0; current != null; depth++)
            {
                if (depth > MaxDepth) return null;
                if (addedHere(current)) return true;
                current = parent(current);
            }
            return false;
        }

        // VisibleHediffGroupsInOrder + DrawHediffRow: group by part in first-appearance order,
        // stable-sort groups by descending part priority, then group each part's entries by
        // UIGroupKey (first appearance) into "label" or "label xN". Null = not exactly projectable.
        internal static JArray? Rows(IReadOnlyList<HealthEntry> entries)
        {
            if (entries == null || entries.Count > MaxEntries) return null;
            var parts = new List<List<HealthEntry>>();
            var byPart = new Dictionary<int, int>();
            foreach (HealthEntry entry in entries)
            {
                if (entry == null || !PrimitiveProjection.Finite(entry.Priority) || Plain(entry.Label) == null
                    || (entry.Part < 0) != (entry.PartLabel == null)
                    || (entry.Part < 0 && entry.Priority != WholeBodyPriority)
                    || (entry.PartLabel != null && Plain(entry.PartLabel) == null)) return null;
                int key = entry.Part < 0 ? -1 : entry.Part;
                if (!byPart.TryGetValue(key, out int index)) { index = parts.Count; byPart[key] = index; parts.Add(new List<HealthEntry>()); }
                parts[index].Add(entry);
            }
            var order = new List<int>(parts.Count);
            for (int i = 0; i < parts.Count; i++) order.Add(i);
            // Enumerable.OrderByDescending is stable; equal priorities keep first appearance.
            order.Sort((a, b) =>
            {
                int byPriority = parts[b][0].Priority.CompareTo(parts[a][0].Priority);
                return byPriority != 0 ? byPriority : a.CompareTo(b);
            });
            var rows = new JArray();
            foreach (int index in order)
            {
                List<HealthEntry> group = parts[index];
                var keys = new List<int>(); var counts = new Dictionary<int, int>(); var firsts = new Dictionary<int, HealthEntry>();
                foreach (HealthEntry entry in group)
                {
                    if (counts.TryGetValue(entry.GroupKey, out int count)) { counts[entry.GroupKey] = count + 1; continue; }
                    keys.Add(entry.GroupKey); counts[entry.GroupKey] = 1; firsts[entry.GroupKey] = entry;
                }
                foreach (int key in keys)
                {
                    int count = counts[key];
                    string label = firsts[key].Label + (count != 1 ? " x" + count.ToString(CultureInfo.InvariantCulture) : "");
                    if (Plain(label) == null || rows.Count >= MaxRows) return null;
                    var row = new JObject { ["label"] = label };
                    if (group[0].PartLabel != null) row["part"] = group[0].PartLabel;
                    rows.Add(row);
                }
            }
            return rows;
        }

        // Bounded plain text that is already in its displayed (capitalized) form.
        internal static string? Plain(string? text) =>
            text != null && PawnProjection.Label("", text) == text ? text : null;

        // GrammarResolverSimple.Formatted over a keyed template, restricted to plain "{n}" /
        // "{NAME}" / "{NAME_sub}" symbols resolving a string or int ("" sub-symbol) or a def's
        // "label". Escapes, functions, "?" arguments, nested or unclosed braces, unresolved
        // symbols and markup are refused (null), as are language workers other than the base
        // post-processing or English. Then CapitalizeSentences(capitalizeFirstSentence: false)
        // and the worker's PostProcessed, exactly as the game does.
        internal static string? Formatted(string? template, IReadOnlyList<FormatArg> args, bool english)
        {
            if (string.IsNullOrEmpty(template) || template!.Length > PrimitiveProjection.MaxLabelChars || args == null) return null;
            var result = new StringBuilder();
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c != '{') { result.Append(c); continue; }
                int close = template.IndexOf('}', i + 1);
                if (close < 0) return null;
                string symbol = template.Substring(i + 1, close - i - 1);
                if (symbol.Length == 0 || symbol.IndexOfAny(new[] { '{', '?', ':' }) >= 0) return null;
                int underscore = symbol.IndexOf('_');
                string name = underscore < 0 ? symbol : symbol.Substring(0, underscore);
                string sub = underscore < 0 ? "" : symbol.Substring(underscore + 1);
                FormatArg? arg = null;
                if (int.TryParse(name, out int index)) { if (index >= 0 && index < args.Count) arg = args[index]; }
                else foreach (FormatArg candidate in args) if (candidate.Name == name) { arg = candidate; break; }
                string? resolved = arg?.Resolve(sub);
                if (resolved == null) return null;
                result.Append(resolved);
                i = close;
            }
            string text = PostProcessed(CapitalizeSentences(result.ToString()), english);
            return text.Length > PrimitiveProjection.MaxLabelChars || text.IndexOf('<') >= 0 || text.IndexOf("(*", StringComparison.Ordinal) >= 0 ? null : text;
        }

        // GenText.CapitalizeSentences(input, capitalizeFirstSentence: false).
        internal static string CapitalizeSentences(string input)
        {
            if (string.IsNullOrEmpty(input) || input.Length == 1) return input;
            bool capitalize = false, tag = false, brace = false, star = false;
            var text = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (capitalize && char.IsLetterOrDigit(c) && !tag && !brace && !star) { text.Append(char.ToUpper(c)); capitalize = false; }
                else text.Append(c);
                bool last = i == input.Length - 1;
                if (c == '\r' || c == '\n' || (c == '.' && !last && input[i + 1] == ' ') || c == '!' || c == '?' || c == ':') capitalize = true;
                else if (c == '<' && !last && input[i + 1] != '/') tag = true;
                else if (tag && c == '>') tag = false;
                else if (c == '(' && !last && input[i + 1] == '*') star = true;
                else if (star && c == ')') star = false;
                else if (c == '{') { brace = true; capitalize = false; }
                else if (brace && c == '}') { brace = false; capitalize = false; }
            }
            return text.ToString();
        }

        // LanguageWorker.PostProcessed (MergeMultipleSpaces) and LanguageWorker_English's article fix-ups.
        internal static string PostProcessed(string text, bool english)
        {
            text = MergeMultipleSpaces(text);
            if (!english) return text;
            if (text.StartsWith("a ", StringComparison.OrdinalIgnoreCase) && text.Length >= 3)
            {
                bool hour = text.Substring(2) == "hour";
                if (hour || "aeiou".IndexOf(text[2]) >= 0) text = text.Insert(1, "n");
                if (hour || "AEIOU".IndexOf(text[2]) >= 0) text = text.Insert(1, "n");
            }
            if (text.StartsWith("an unique ", StringComparison.OrdinalIgnoreCase)) text = text.Remove(1, 1);
            foreach (KeyValuePair<string, string> pair in ArticleFixes) text = text.Replace(pair.Key, pair.Value);
            return text;
        }

        // The English worker's ordered Replace list: lowercase vowels, "hour" and "unique" after
        // " "/"\n" + "a"/"A", then uppercase vowels.
        private static readonly List<KeyValuePair<string, string>> ArticleFixes = BuildArticleFixes();
        private static List<KeyValuePair<string, string>> BuildArticleFixes()
        {
            var fixes = new List<KeyValuePair<string, string>>();
            string[] separators = { " ", "\n" }, articles = { "a", "A" };
            foreach (string separator in separators)
                foreach (string article in articles)
                {
                    foreach (char vowel in "aeiou") fixes.Add(new KeyValuePair<string, string>(separator + article + " " + vowel, separator + article + "n " + vowel));
                    fixes.Add(new KeyValuePair<string, string>(separator + article + " hour", separator + article + "n hour"));
                    fixes.Add(new KeyValuePair<string, string>(separator + article + "n unique", separator + article + " unique"));
                }
            foreach (string separator in separators)
                foreach (string article in articles)
                    foreach (char vowel in "AEIOU") fixes.Add(new KeyValuePair<string, string>(separator + article + " " + vowel, separator + article + "n " + vowel));
            return fixes;
        }

        // GenText.MergeMultipleSpaces(leaveMultipleSpacesAtLineBeginning: true).
        internal static string MergeMultipleSpaces(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("  ", StringComparison.Ordinal) < 0) return text;
            bool lineStart = true;
            var merged = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' || text[i] == '\n') lineStart = true;
                if (lineStart || text[i] != ' ' || i == 0 || text[i - 1] != ' ') merged.Append(text[i]);
                if (!char.IsWhiteSpace(text[i])) lineStart = false;
            }
            return merged.ToString();
        }

        // Exact Disappears comp: one tick lost per tick. Its short period formatter uses
        // seconds below one hour, then rounded hours/days/years (optional F1 decimals).
        // No getters, translations with arguments, noise, simulation or cache writes.
        internal static string? DisappearingTime(int ticks, bool decimals, Func<string, string> keyed)
        {
            if (ticks < 0 || keyed == null) return null;
            if (ticks < 2500) return ((float)ticks / 60f).ToString("F0") + " " + keyed("SecondsLower");
            float value; string unit;
            if (ticks < 60000) { value = (float)ticks / 2500f; unit = "LetterHour"; }
            else if (ticks < 3600000) { value = (float)ticks / 60000f; unit = "LetterDay"; }
            else { value = (float)ticks / 3600000f; unit = "LetterYear"; }
            return (decimals ? value.ToString("F1") : ((int)Math.Round(value)).ToString()) + keyed(unit);
        }

        // Hediff_Addiction bracket percentage: GenText.ToStringPercent(1 - severity, "F0").
        internal static string AddictionPercent(float severity)
        {
            float remaining = 1f - severity;
            return ((remaining + 1E-05f) * 100f).ToString("F0") + "%";
        }
    }

    // One NamedArgument for the restricted formatter: a string, an int, or a def's label.
    internal sealed class FormatArg
    {
        internal readonly string? Name;
        private readonly string? text, defLabel;
        private readonly int? number;
        private FormatArg(string? name, string? text, int? number, string? defLabel) { Name = name; this.text = text; this.number = number; this.defLabel = defLabel; }
        internal static FormatArg Text(string value, string? name = null) => new FormatArg(name, value, null, null);
        internal static FormatArg Number(int value) => new FormatArg(null, null, value, null);
        internal static FormatArg Def(string? name, string label) => new FormatArg(name, null, null, label);

        internal string? Resolve(string sub)
        {
            if (text != null) return sub.Length == 0 ? text : null;
            if (number != null) return sub.Length == 0 ? number.Value.ToString() : null;
            return sub == "label" ? defLabel : null;
        }
    }
}
