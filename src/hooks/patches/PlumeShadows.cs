using System.Globalization;
using System.Linq;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Plume shadow tuning, "low" (port-settings.toml): with the game's Plume trail shadows on, the game draws the smoke's
// density as seen from the sun into 8 shadow cascades of 512^2 out to 20 km, which the raymarch reads beside its own
// self-shadow samples and which cast the smoke's shadows on the ground. Low makes them 4 of 128^2 out to 3 km: the
// giga rocket's smoke draws about 20 % faster on an M5, its ground shadows are coarser and end 3 km from the camera.
// The self-shadow samples stay, so the smoke beyond the cascades is still shaded.
// The developer setting trail_shadows (<cascades>,<resolution>,<max distance m>) sets them directly.
internal static class PlumeShadows
{
    private static double[] _shadows = [];

    public static string Apply(Harmony harmony)
    {
        bool low = Settings.Value("plume_shadow_tuning") == "low";
        _shadows = Settings.Numbers("trail_shadows");
        if (_shadows.Length == 0 && low)
            _shadows = [4, 128, 3000];
        if (_shadows.Length == 0)
            return "off";
        harmony.Patch(Find.Method(typeof(VolumetricTrailShadowCascades), "CreateResources"),
            prefix: new HarmonyMethod(typeof(PlumeShadows), nameof(Prefix)));
        string spec = string.Join(',', _shadows.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        return Settings.Value("trail_shadows").Length == 0 ? $"low, {spec}" : spec;
    }

    private static void Prefix(VolumetricTrailShadowCascades __instance)
    {
        __instance.ShadowCascadeCount = (int)_shadows[0];
        __instance.ShadowCascadeResolution = (int)_shadows[1];
        __instance.ShadowCascadeMaxDistance = (float)_shadows[2];
    }
}
