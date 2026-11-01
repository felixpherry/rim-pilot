using System;

namespace RimPilot.Mod.Observation
{
    internal static class JobProjection
    {
        // Own-pawn normal inspect gates; alwaysShowReport overrides mental state, not mutant.
        internal static bool LordShown(bool mental, bool mutantOverride) => !mental && !mutantOverride;
        internal static bool JobShown(bool mental, bool mutantOverride, bool always) => !mutantOverride && (always || !mental);

        // Exact installed Wait.GetReport calls base unless Wait_Combat. The combat branch
        // reads violence/work-tag capability, so it stays unverified rather than guessed.
        internal static bool WaitUsesBase(bool exactWait, bool combatJob) => exactWait && !combatJob;

        internal static string? Public(string? text) => string.IsNullOrEmpty(text) ? null : PawnProjection.Label("", text);

        // Already-admitted public short definition text, preserving its displayed case.
        // Validation must not turn unknown/markup text into an empty or healthy substitute.
        internal static string? PlainLegacyTarget(string? text) => Public(text) == null ? null : text;

        // Legacy TargetA/B/C replacement follows Formatted, not before it.
        internal static string? Resolved(string? formatted, string a, string b, string c) => formatted == null ? null
            : Public(formatted.Replace("TargetA", a).Replace("TargetB", b).Replace("TargetC", c));
    }
}
