using System.Linq;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Fixes;

// Cascade count. A game bug since KSA 5909: the shadow cascade count (the start popup's Cascade Count, 3 to 6) is a
// field of GameSettings.GraphicsSettings that its copy constructor leaves out. The settings window edits a copy of the
// settings, and its APPLY makes that copy the current settings and saves them, so any change applied there, a Mac Port
// tab preset too, saves the count as 6, its field's default, which the next start uses (the running game builds its
// shadows once). A postfix on the copy constructor copies it.
[Hook(Phase.Default)]
internal static class CascadeCount
{
    public static string Apply(Harmony harmony)
    {
        var copy = Find.Constructor(typeof(GameSettings.GraphicsSettings), typeof(GameSettings.GraphicsSettings));
        var count = Find.Field(typeof(GameSettings.GraphicsSettings),
            nameof(GameSettings.GraphicsSettings.ShadowMapCascadeCount));
        // A read of the field, not a store: the field's initializer (6) is compiled into every constructor, this one too
        if (PatchProcessor.GetOriginalInstructions(copy).Any(i => i.LoadsField(count)))
            throw new Skip("the game's copy constructor copies the cascade count");
        harmony.Patch(copy, postfix: new HarmonyMethod(typeof(CascadeCount), nameof(Postfix)));
        return "on";
    }

    private static void Postfix(GameSettings.GraphicsSettings __instance, GameSettings.GraphicsSettings source) =>
        __instance.ShadowMapCascadeCount = source.ShadowMapCascadeCount;
}
