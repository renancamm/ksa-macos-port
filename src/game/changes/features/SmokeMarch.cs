using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Fewer Smoke Steps, fewer_smoke_steps (port-settings.toml): RaymarchVolumetricTrail.comp steps through rocket
// smoke at 0.075 of the plume's radius (ADAPTIVE_STEP_SIZE_RADIUS_FRACTION) plus a share of the distance, and marches
// toward the sun for the smoke's self-shadow at every step. The port's shader edit (smoke-march.edits) makes the
// radius share 0.15, twice as large, through trails and 0.1125, 1.5 times as large, through explosion gas (twice as
// large broke up a fireball's skin), where the trail data's spare padding0 (fireLightPadding0 in the shader) is 1,
// which a postfix on VolumetricTrailShadowCascades.Fit sets each frame: Fit takes the frame's data by reference just
// before the game writes it to the GPU. Explosions scale that step down by their own factor.
[Hook(Phase.Default, Switch = nameof(Settings.FewerSmokeSteps))]
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
        return "steps x2 of the radius in trails, x1.5 in explosion gas";
    }

    private static void Postfix(ref VolumetricTrailData trailData) => _flag(ref trailData) = 1;
}
