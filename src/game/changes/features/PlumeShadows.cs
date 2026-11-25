using System.Globalization;
using System.Linq;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Lighter Plume Shadows, lighter_plume_shadows (port-settings.toml): with the game's Plume trail shadows on, the game
// draws the smoke's density as seen from the sun into 8 shadow cascades of 384^2 out to 20 km, which the raymarch reads
// beside its own self-shadow samples and which cast the smoke's shadows on the ground. Lighter makes them 4 of 128^2 out
// to 3 km: on an M5, five games a side at the Balanced preset, the Mixed Test's liftoff, view from above and explosion
// 2-4 % faster (the other views within noise); the ground shadows are coarser and end 3 km from the camera. The
// self-shadow samples stay, so the smoke beyond the cascades is still shaded. The setting trail_shadows, of the file's
// Experiments (<cascades>,<resolution>,<max distance m>), sets them directly.
[Hook(Phase.Default)]
internal static class PlumeShadows
{
    private static double[] _shadows = [];

    public static string Apply(Harmony harmony)
    {
        bool lighter = Settings.LighterPlumeShadows;
        _shadows = Settings.TrailShadows;
        if (_shadows.Length == 0 && lighter)
            _shadows = [4, 128, 3000];
        if (_shadows.Length == 0)
            return "off";
        harmony.Patch(Find.Method(typeof(VolumetricTrailShadowCascades), "CreateResources"),
            prefix: new HarmonyMethod(typeof(PlumeShadows), nameof(Prefix)));
        string spec = string.Join(',', _shadows.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        return Settings.TrailShadows.Length == 0 ? $"lighter, {spec}" : spec;
    }

    private static void Prefix(VolumetricTrailShadowCascades __instance)
    {
        __instance.ShadowCascadeCount = (int)_shadows[0];
        __instance.ShadowCascadeResolution = (int)_shadows[1];
        __instance.ShadowCascadeMaxDistance = (float)_shadows[2];
    }
}
