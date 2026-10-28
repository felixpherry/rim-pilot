using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using RimPilot.Protocol;

namespace RimPilot.Mod.Observation
{
    // Pure own-pawn identity/status projection (docs/visibility-audit.md#own-pawns). Inputs are
    // primitives the game adapter read from audited plain fields; nothing here touches the game.
    internal static class PawnProjection
    {
        // Fixed public tokens in fixed order; explicit normal-inspection booleans only.
        internal static readonly string[] StatusTokens = { "downed", "dead", "drafted" };

        // Own colony pawn: player faction, not hosted as a guest or prisoner by anyone. Colony
        // slaves (host = player, slave status) remain own pawns, as in the colonist bar.
        internal static bool Own(bool playerFaction, bool hosted, bool slaveOfPlayer) =>
            playerFaction && (!hosted || slaveOfPlayer);

        // Pawn.LabelShortCap equivalent: mutant name prefix (when turned) + short name, else
        // prefix + best kind label, then GenText.CapitalizeFirst. Markup, tagged-string markers
        // and invalid/oversized text are refused (null), never stripped or truncated.
        internal static string? Label(string? prefix, string? baseLabel)
        {
            if (prefix == null || Plain(baseLabel) == null || (prefix.Length > 0 && Plain(prefix) == null)) return null;
            string text = prefix + baseLabel;
            if (Plain(text) == null) return null;
            if (char.IsUpper(text[0])) return text;
            return text.Length == 1 ? text.ToUpper(CultureInfo.CurrentCulture)
                : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text.Substring(1);
        }

        private static string? Plain(string? text)
        {
            string? bounded = SummaryProjection.Text(text, PrimitiveProjection.MaxLabelChars);
            if (bounded == null || bounded.IndexOf('<') >= 0 || bounded.IndexOf('>') >= 0
                || bounded.IndexOf("(*", StringComparison.Ordinal) >= 0 || bounded.IndexOf("*)", StringComparison.Ordinal) >= 0)
                return null;
            return bounded;
        }

        internal static List<string> Status(bool downed, bool dead, bool drafted)
        {
            var status = new List<string>(StatusTokens.Length);
            if (downed) status.Add(StatusTokens[0]);
            if (dead) status.Add(StatusTokens[1]);
            if (drafted) status.Add(StatusTokens[2]);
            return status;
        }

        // List/detail identity row. Status is fully supported for every admitted pawn, so an
        // empty array means "none of these flags", not unavailable. Detail-only families (needs,
        // health, gear and public reports) are added by Detail and stay absent until supported.
        internal static JObject Item(string pawn, string label, IReadOnlyList<string> status)
        {
            if (!Ids.Is(pawn, "p") || Label("", label) != label || status == null || status.Count > StatusTokens.Length)
                throw new ArgumentException("Invalid pawn.");
            var flags = new JArray();
            foreach (string token in status)
            {
                if (Array.IndexOf(StatusTokens, token) < 0) throw new ArgumentException("Invalid pawn.");
                flags.Add(token);
            }
            return new JObject { ["pawn"] = pawn, ["label"] = label, ["status"] = flags };
        }

        // Detail adds supported families to the item. A null family is omitted (unverified for
        // this pawn), never sent empty; an empty array means the tab shows no needs/conditions/gear.
        internal static JObject Detail(SampleStamp sample, JObject item, JArray? needs = null, JArray? health = null, JArray? gear = null, string? job = null, string? lordReport = null)
        {
            if (sample == null || sample.Scope.Map == null || item == null) throw new ArgumentException("Invalid pawn.");
            var pawn = (JObject)item.DeepClone();
            if (needs != null) pawn["needs"] = needs.DeepClone();
            if (health != null) pawn["health"] = health.DeepClone();
            if (gear != null) pawn["gear"] = gear.DeepClone();
            if (job != null) pawn["job"] = JobProjection.Public(job) ?? throw new ArgumentException("Invalid job report.");
            if (lordReport != null) pawn["lordReport"] = JobProjection.Public(lordReport) ?? throw new ArgumentException("Invalid lord report.");
            return new JObject { ["sample"] = sample.Json(), ["pawn"] = pawn };
        }
    }
}
