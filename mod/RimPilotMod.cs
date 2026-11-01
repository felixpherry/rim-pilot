using RimPilot.Mod.Bridge;
using Verse;

namespace RimPilot.Mod
{
    // Verse constructs mods during asynchronous loading. Bootstrap is metadata/BCL-only.
    public sealed class RimPilotMod : Verse.Mod
    {
        public RimPilotMod(ModContentPack content) : base(content)
        {
            ModBootstrap.Bootstrap();
        }
    }
}
