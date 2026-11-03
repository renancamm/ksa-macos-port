using HarmonyLib;
using KSA;

namespace KsaMetalHooks.Fixes;

// Clutter off. A game bug since KSA 5707: PlanetRenderer.RenderClutterDepthNormalPrePass draws the ground clutter without
// asking whether there is any, as RenderClutterDepthPrePass beside it does. With the game's Ground Clutter off there is no
// clutter renderer, and the first frame near a world ends the game with a NullReferenceException.
[Hook(Phase.Default)]
internal static class ClutterOff
{
    public static string Apply(Harmony harmony)
    {
        var prePass = Find.Method(typeof(PlanetRenderer), nameof(PlanetRenderer.RenderClutterDepthNormalPrePass));
        harmony.Patch(prePass, prefix: new HarmonyMethod(typeof(ClutterOff), nameof(Prefix)));
        return "on";
    }

    private static bool Prefix(PlanetRenderer __instance) => __instance.GroundClutterRenderer != null;
}
