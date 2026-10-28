using System.Reflection;
using HarmonyLib;

namespace RimPilot.Mod.Observation
{
    internal sealed class GameProjectionPatchAudit : IProjectionPatchAudit
    {
        public bool IsUnpatched(MethodBase method)
        {
            // All active profile owners, not only Assembly-CSharp or a preferred Harmony ID.
            // Never read Patch.PatchMethod/GetMethod: those can resolve or execute factories.
            Patches? info = Harmony.GetPatchInfo(method);
            return info == null || PatchCategories.Unpatched(info.Prefixes.Count, info.Postfixes.Count,
                info.Transpilers.Count, info.Finalizers.Count, info.InnerPrefixes.Count, info.InnerPostfixes.Count);
        }
    }
}
