using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace KsaPatches;

// The game patches: changes to KSA's code made in memory with Harmony when the game starts (KSA.dll stays as it is on
// disk). Each patch finds its targets by name and its code by pattern; when a KSA update moved either, it is skipped
// and the log says so, and the game runs as it would without it. A patch is applied whole or not at all.
public static class Patches
{
    public static void Apply()
    {
        Run("terrain cache", "terrain_cache", TerrainCache.Apply);
        Run("outline skip", "outline_skip", OutlineSkip.Apply);
        Run("cloud shadows", "cloud_shadows", CloudShadows.Apply);
        Run("render scale", "render_scale", RenderScale.Apply);
    }

    // apply(harmony) patches and returns what it did ("on", "off", ...); throws Skip (or anything) when it can't
    private static void Run(string name, string key, Func<Harmony, string> apply)
    {
        if (Settings.Disabled(key))
        {
            Log($"{name}: off (KSA_PATCHES_OFF)");
            return;
        }
        var harmony = new Harmony($"io.github.ksa-macos-port.{key}");
        try
        {
            Log($"{name}: {apply(harmony)}");
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(harmony.Id);
            while (e.InnerException != null && e is not Skip)
                e = e.InnerException;
            Log($"{name}: skipped, the game's code changed ({e.Message})");
        }
    }

    public static void Log(string message) => Console.WriteLine($"[patches] {message}");
}

// Why a patch can't apply: its target or the code it changes isn't what it expects
public class Skip(string message) : Exception(message);

// Lookups that throw Skip instead of returning null
public static class Find
{
    public static MethodInfo Method(Type type, string name, params Type[] parameters) =>
        AccessTools.Method(type, name, parameters.Length > 0 ? parameters : null) ??
        throw new Skip($"{type.Name}.{name} not found");

    public static MethodInfo Getter(Type type, string name) =>
        AccessTools.PropertyGetter(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    public static MethodInfo Setter(Type type, string name) =>
        AccessTools.PropertySetter(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    public static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    // The index of the one instruction that matches, from start on
    public static int Once(List<CodeInstruction> code, Func<CodeInstruction, bool> match, string what, int start = 0)
    {
        int found = -1;
        for (int i = start; i < code.Count; i++)
        {
            if (!match(code[i]))
                continue;
            if (found >= 0)
                throw new Skip($"{what} found more than once");
            found = i;
        }
        return found >= 0 ? found : throw new Skip($"{what} not found");
    }

    // The index of the first instruction from start on that matches
    public static int Next(List<CodeInstruction> code, Func<CodeInstruction, bool> match, string what, int start)
    {
        for (int i = start; i < code.Count; i++)
            if (match(code[i]))
                return i;
        throw new Skip($"{what} not found");
    }
}
