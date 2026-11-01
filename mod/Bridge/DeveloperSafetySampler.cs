using System;
using System.Threading;

namespace RimPilot.Mod.Bridge
{
    // Instantaneous private evidence only, not an uninterrupted safety interval or render proof.
    internal enum DeveloperSafety { Unknown, Unsafe, Safe }

    // Implementations are bridge-owned, never client-supplied. No arbitrary window getters.
    internal interface IDeveloperSafetySource<TWindow> where TWindow : class
    {
        bool DeveloperMode { get; }
        bool CanInspect { get; }
        int StaticReadCount { get; }
        bool BaselineMatches(int index);
        int WindowCount { get; }
        TWindow WindowAt(int index);
        bool KnownDeveloperWindow(TWindow window);
    }

    internal sealed class DeveloperSafetySampler<TWindow> where TWindow : class
    {
        // Normative ceilings also recorded in developer-safety-v1.json.
        internal const int MaxWindows = 64, MaxStaticReads = 256, RequiredStaticReads = 185;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private readonly IDeveloperSafetySource<TWindow> source;
        internal DeveloperSafetySampler(IDeveloperSafetySource<TWindow> source) { this.source = source; }

        internal DeveloperSafety Sample(UpdateBudget budget)
        {
            // Off-thread callers cannot even read Prefs. Never reuse a prior successful sample.
            if (Thread.CurrentThread.ManagedThreadId != thread) return DeveloperSafety.Unknown;
            try
            {
                if (!budget.Available) return DeveloperSafety.Unknown;
                if (source.DeveloperMode) return DeveloperSafety.Unsafe;
                if (!budget.Available || !source.CanInspect) return DeveloperSafety.Unknown;
                int reads = source.StaticReadCount;
                if (reads != RequiredStaticReads || reads > MaxStaticReads) return DeveloperSafety.Unknown;
                for (int i = 0; i < reads; i++)
                {
                    if (!budget.Available) return DeveloperSafety.Unknown;
                    if (!source.BaselineMatches(i)) return DeveloperSafety.Unsafe;
                }
                if (!budget.Available) return DeveloperSafety.Unknown;
                int windows = source.WindowCount;
                if (windows < 0 || windows > MaxWindows) return DeveloperSafety.Unknown;
                for (int i = 0; i < windows; i++)
                {
                    if (!budget.Available) return DeveloperSafety.Unknown;
                    TWindow window = source.WindowAt(i);
                    if (window == null) return DeveloperSafety.Unknown;
                    if (source.KnownDeveloperWindow(window)) return DeveloperSafety.Unsafe;
                }
                if (!budget.Available || source.WindowCount != windows || !source.CanInspect)
                    return DeveloperSafety.Unknown;
                if (source.DeveloperMode) return DeveloperSafety.Unsafe;
                return budget.Available ? DeveloperSafety.Safe : DeveloperSafety.Unknown;
            }
            catch
            {
                // Missing fields/types, failed binding, incompatible source, or patched reads:
                // no diagnostics, partial evidence, retained objects or guessed safe default.
                return DeveloperSafety.Unknown;
            }
        }
    }
}
