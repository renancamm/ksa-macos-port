using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Lighter Smoke March, lighter_smoke_march (port-settings.toml): RaymarchVolumetricTrail.comp steps through rocket
// smoke at 0.075 of the plume's radius (ADAPTIVE_STEP_SIZE_RADIUS_FRACTION) plus a share of the distance, and marches
// toward the sun for the smoke's self-shadow at every step. The port's shader edit (smoke-march.edits) makes the radius
// share 0.1125, 1.5 times as large, where the trail data's spare padding0 (fireLightPadding0 in the shader) is 1, which
// a postfix on VolumetricTrailShadowCascades.Fit sets each frame: Fit takes the frame's data by reference just before
// the game writes it to the GPU. Explosions scale that step down by their own factor, so theirs grows by the same
// share.
[Hook(Phase.Default, Switch = nameof(Settings.LighterSmokeMarch))]
internal static class SmokeMarch
{
    private static AccessTools.StructFieldRef<VolumetricTrailData, int> _flag = null!;

    public static string Apply(Harmony harmony)
    {
        var fit = Find.Method(typeof(VolumetricTrailShadowCascades), nameof(VolumetricTrailShadowCascades.Fit));
        var field = Find.Field(typeof(VolumetricTrailData), "padding0");
        if (field.FieldType != typeof(int))
            throw new Skip("VolumetricTrailData.padding0 not an int");
        _flag = AccessTools.StructFieldRefAccess<VolumetricTrailData, int>(field);
        harmony.Patch(fit, postfix: new HarmonyMethod(typeof(SmokeMarch), nameof(Postfix)));
        return "steps x1.5 of the radius";
    }

    private static void Postfix(ref VolumetricTrailData trailData) => _flag(ref trailData) = 1;
}
