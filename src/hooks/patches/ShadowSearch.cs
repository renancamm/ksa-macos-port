using Brutal.Memory;
using HarmonyLib;
using KSA.Rendering.Lighting;

namespace KsaMetalHooks;

// Shadow search limit. With the game's soft shadows (Shadow filter: Pcss), each pixel looks for what shades it as far
// as the widest penumbra the sun can cast, 1 m (CascadedShadowSystem.Settings.MaxPenumbraWorld). In the shadow
// cascades near the camera that is hundreds of texels, and 16 taps that far apart miss the GPU's caches. The search
// now stops at 32 texels; cascades whose texels are 3 cm or more search as before. A penumbra is about 1/200 of the
// height of what casts it, so a shadow within about 2 m of the camera cast from more than about 8 m above it (30 m
// within 8 m of the camera, 85 m within 24 m) can find fewer of its casters and get a sharper edge; no view tried
// showed it. The shadow edit of convert-shaders.sh reads the limit from the cascades' data, in its spare _pad1 (0
// without this patch: the game's search); a prefix on CascadedShadowMap.UploadCascades sets it before the upload.
internal static class ShadowSearch
{
    private const float Texels = 32f;

    private static AccessTools.FieldRef<CascadedShadowMap, PtrOwner<CascadedShadowMap.CascadeUboData>> _data = null!;
    private static AccessTools.StructFieldRef<CascadedShadowMap.CascadeUboData, float> _limit = null!;

    public static string Apply(Harmony harmony)
    {
        _data = AccessTools.FieldRefAccess<CascadedShadowMap, PtrOwner<CascadedShadowMap.CascadeUboData>>(
            Find.Field(typeof(CascadedShadowMap), "_cascadeData"));
        _limit = AccessTools.StructFieldRefAccess<CascadedShadowMap.CascadeUboData, float>(
            Find.Field(typeof(CascadedShadowMap.CascadeUboData), "_pad1"));
        harmony.Patch(Find.Method(typeof(CascadedShadowMap), nameof(CascadedShadowMap.UploadCascades)),
            prefix: new HarmonyMethod(typeof(ShadowSearch), nameof(UploadPrefix)));
        return "on";
    }

    private static void UploadPrefix(CascadedShadowMap __instance) => _limit(ref _data(__instance).Value) = Texels;
}
