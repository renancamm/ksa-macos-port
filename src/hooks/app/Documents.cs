using System;
using System.Linq;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// The game's documents folder (saves, settings, mods, logs) moved elsewhere with STARMAP_INSTANCE_PATH, as StarMap does
// it, also when the game starts without StarMap: the benchmark plays from a folder of its own this way. With StarMap,
// StarMap moves it itself.
internal static class Documents
{
    private static string _folder = "";

    public static string Apply(Harmony harmony)
    {
        if (Environment.GetEnvironmentVariable("STARMAP_INSTANCE_PATH") is not { Length: > 0 } folder)
            return "off";
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "StarMap.Core"))
            return "StarMap's";
        _folder = folder;
        harmony.Patch(Find.Getter(typeof(Constants), nameof(Constants.DocumentsFolderPath)),
            prefix: new HarmonyMethod(typeof(Documents), nameof(Prefix)));
        return folder.Replace(Environment.GetEnvironmentVariable("HOME") ?? "~", "~");
    }

    private static bool Prefix(ref string __result)
    {
        __result = _folder;
        return false;
    }
}
