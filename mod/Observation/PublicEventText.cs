using System;
using System.Text;

namespace RimPilot.Mod.Observation
{
    internal static class PublicEventText
    {
        // Strip presentation-only Unity/Verse color tags without resolving faction/ideo IDs,
        // consulting world objects or populating ColoredText caches. Unknown markup refuses.
        internal static string? Sanitize(string? raw, int max)
        {
            if (string.IsNullOrEmpty(raw) || raw!.Length > 4096) return null;
            var output = new StringBuilder(Math.Min(raw.Length, max));
            int tags = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '<' || (c == '(' && i + 1 < raw.Length && (raw[i + 1] == '*' || raw[i + 1] == '/')))
                {
                    char end = c == '<' ? '>' : ')'; int close = raw.IndexOf(end, i + 1);
                    if (close < 0 || close - i > 128 || ++tags > 64) return null;
                    string tag = raw.Substring(i + 1, close - i - 1);
                    bool valid = c == '<' ? tag == "b" || tag == "/b" || tag == "i" || tag == "/i"
                        || tag == "/color" || tag.StartsWith("color=#", StringComparison.Ordinal)
                        : KnownVerseTag(tag);
                    if (!valid) return null;
                    i = close; continue;
                }
                if (c == '>' || (char.IsControl(c) && c != '\n' && c != '\t')) return null;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 == raw.Length || !char.IsLowSurrogate(raw[i + 1]) || output.Length + 2 > max) return null;
                    output.Append(c); output.Append(raw[++i]); continue;
                }
                if (char.IsLowSurrogate(c) || output.Length == max) return null;
                output.Append(c);
            }
            return output.Length == 0 ? null : output.ToString();
        }
        private static bool KnownVerseTag(string tag)
        {
            if (tag.Length < 2) return false;
            string name = tag.Substring(1); int equals = name.IndexOf('=');
            if (equals >= 0) name = name.Substring(0, equals);
            return name == "Name" || name == "Faction" || name == "Ideo" || name == "Settlement"
                || name == "DateTime" || name == "ColonistCount" || name == "Threat" || name == "Currency"
                || name == "SectionTitle" || name == "Red" || name == "Reward" || name == "Gray";
        }
    }
}
