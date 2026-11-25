using System.Linq;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Fixes;

// Cascade count. A game bug since KSA 5909: the shadow cascade count (the start popup's Cascade Count, 3 to 6) is a
// field of GameSettings.GraphicsSettings that its copy constructor leaves out. The settings window edits a copy of the
// settings, and its APPLY makes that copy the current settings and saves them, so any change applied there, a Mac Port
// tab preset too, saves the count as 6, its field's default, which the next start uses (the running game builds its
// shadows once). A postfix on the copy constructor copies it. The settings window doesn't show the count, and its
// restart check (GameSettings.RequiresRestartOnApply) leaves it out, so a preset that changes it alone would apply
// without the restart popup: a postfix there asks for the restart.
[Hook(Phase.Default)]
internal static class CascadeCount
{
    public static string Apply(Harmony harmony)
    {
        var copy = Find.Constructor(typeof(GameSettings.GraphicsSettings), typeof(GameSettings.GraphicsSettings));
        var count = Find.Field(typeof(GameSettings.GraphicsSettings),
            nameof(GameSettings.GraphicsSettings.ShadowMapCascadeCount));
        var restart = Find.Method(typeof(GameSettings), "RequiresRestartOnApply");
        // Each fix on its own, while the game's code still lacks it. A read of the field, not a store: the field's
        // initializer (6) is compiled into every constructor, this one too
        bool copies = PatchProcessor.GetOriginalInstructions(copy).Any(i => i.LoadsField(count));
        bool restarts = PatchProcessor.GetOriginalInstructions(restart).Any(i => i.LoadsField(count));
        if (copies && restarts)
            throw new Skip("the game copies the cascade count and asks for a restart when it changes");
        if (!copies)
            harmony.Patch(copy, postfix: new HarmonyMethod(typeof(CascadeCount), nameof(Postfix)));
        if (!restarts)
            harmony.Patch(restart, postfix: new HarmonyMethod(typeof(CascadeCount), nameof(RestartPostfix)));
        return copies ? "the restart only" : restarts ? "the copy only" : "on";
    }

    private static void RestartPostfix(GameSettings __instance, ref bool __result)
    {
        if (__instance.Graphics.ShadowMapCascadeCount != GameSettings.Current.Graphics.ShadowMapCascadeCount)
            __result = true;
    }

    private static void Postfix(GameSettings.GraphicsSettings __instance, GameSettings.GraphicsSettings source) =>
        __instance.ShadowMapCascadeCount = source.ShadowMapCascadeCount;
}
