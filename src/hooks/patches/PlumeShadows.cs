using System;
using System.Globalization;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Plume shadow tuning, "low" (port-settings.toml): with the game's Plume trail shadows on, the game draws the smoke's
// density as seen from the sun into 8 shadow cascades of 512^2 out to 20 km, which the raymarch reads beside its own
// self-shadow samples and which cast the smoke's shadows on the ground. Low makes them 4 of 128^2 out to 3 km: the
// giga rocket's smoke draws about 20 % faster on an M5, its ground shadows are coarser and end 3 km from the camera.
// The self-shadow samples stay, so the smoke beyond the cascades is still shaded.
// KSA_TRAIL_SHADOWS=<cascades>,<resolution>,<max distance m> (developer setting trail_shadows) sets them directly.
internal static class PlumeShadows
{
    private static float[] _shadows = [];

    public static string Apply(Harmony harmony)
    {
        string spec = Environment.GetEnvironmentVariable("KSA_TRAIL_SHADOWS") ?? "";
        if (spec.Length == 0 && Settings.LowPlumeShadows)
            spec = "4,128,3000";
        if (spec.Length == 0)
            return "off";
        _shadows = Array.ConvertAll(spec.Split(','), v => float.Parse(v, CultureInfo.InvariantCulture));
        if (_shadows.Length != 3)
            throw new Skip("KSA_TRAIL_SHADOWS takes 3 numbers");
        harmony.Patch(Find.Method(typeof(VolumetricTrailShadowCascades), "CreateResources"),
            prefix: new HarmonyMethod(typeof(PlumeShadows), nameof(Prefix)));
        return Settings.LowPlumeShadows ? $"low, {spec}" : spec;
    }

    private static void Prefix(VolumetricTrailShadowCascades __instance)
    {
        __instance.ShadowCascadeCount = (int)_shadows[0];
        __instance.ShadowCascadeResolution = (int)_shadows[1];
        __instance.ShadowCascadeMaxDistance = _shadows[2];
    }
}
