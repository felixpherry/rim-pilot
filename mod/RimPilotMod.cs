using System;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace RimPilot.Mod
{
    // Build skeleton only: no patches, listener, observation or control routes yet.
    public sealed class RimPilotMod : Verse.Mod
    {
        public RimPilotMod(ModContentPack content) : base(content)
        {
        }

        // Keep actual Harmony and Unity references in the compiled compatibility surface.
        internal static Type HarmonyRuntime => typeof(Harmony);
        internal static Type UnityRuntime => typeof(Texture2D);
    }
}
