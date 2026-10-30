using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Editor clutter. Since KSA 5707 the depth and normal pre-pass draws the ground clutter, and the Vehicle Editor runs
// that pass too, with the clutter of the place it was opened from. The editor draws no clutter in colour, but the
// light pre-pass lights those pixels, and the walls of the editor's enclosure read that light by screen position: a
// ghost of the grass and trees on the walls. The clutter's pre-pass is now skipped while the editor is open.
internal static class EditorClutter
{
    public static string Apply(Harmony harmony)
    {
        var depthNormal = Find.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.RenderGroundClutterDepthNormal));
        harmony.Patch(depthNormal, prefix: new HarmonyMethod(typeof(EditorClutter), nameof(Prefix)));
        return "on";
    }

    private static bool Prefix() => !Program.EditorFlag;
}
