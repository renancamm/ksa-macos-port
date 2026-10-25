using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace KsaMetalHooks;

// KSA Metal Hooks: changes to KSA's code made in memory with Harmony when the game starts (KSA.dll stays as it is on
// disk). Each patch finds its targets by name and its code by pattern; when a KSA update moved either, it is skipped
// and the log says so, and the game runs as it would without it. A patch is applied whole or not at all.
public static class Hooks
{
    // What each hook did, by its key ("on, ...", "off", "skipped, ..."): the Mac Port tab shows a setting whose hook was
    // skipped as unavailable
    public static readonly Dictionary<string, string> Status = new();
    // The hooks in the order they were applied: name and key
    private static readonly List<(string Name, string Key)> Applied = new();

    // What every hook did, as the log said it ("terrain cache: on; ...")
    public static string Summary => string.Join("; ", Applied.Select(h => $"{h.Name}: {Status[h.Key]}"));

    // The app's Contents/Resources: the hooks are in its Hooks folder
    public static string Resources => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(typeof(Hooks).Assembly.Location)!, ".."));

    public static void Apply()
    {
        // Off (KSA_HOOKS=0), they load only for a benchmark run, which they start
        if (Environment.GetEnvironmentVariable("KSA_HOOKS") == "0")
        {
            Log("off, except for the benchmark");
            Run("documents folder", "documents", true, Documents.Apply);
            Run("benchmark", "benchmark", true, BenchmarkRun.Apply);
            return;
        }
        Run("terrain cache", "terrain_cache", Settings.On("KSA_TERRAIN_CACHE"), TerrainCache.Apply);
        Run("outline skip", "outline_skip", Settings.On("KSA_OUTLINE_SKIP"), OutlineSkip.Apply);
        Run("cloud shadows", "cloud_shadows", true, CloudShadows.Apply);
        Run("crew portraits", "crew_portraits", true, CrewPortraits.Apply);
        Run("worker parking", "worker_park", Settings.On("KSA_WORKER_PARKING"), WorkerPark.Apply);
        Run("render scale", "render_scale", true, RenderScale.Apply);
        Run("trail caches", "trail_caches", true, TrailCaches.Apply);
        // The port's own parts of the game
        Run("documents folder", "documents", true, Documents.Apply);
        Run("restart", "restart", true, Restart.Apply);
        Run("Mac Port tab", "mac_port_tab", true, MacPortTab.Apply);
        Run("benchmark", "benchmark", true, BenchmarkRun.Apply);
    }

    // apply(harmony) patches and returns what it did ("on", "off", ...); throws Skip (or anything) when it can't
    private static void Run(string name, string key, bool enabled, Func<Harmony, string> apply)
    {
        Applied.Add((name, key));
        if (!enabled)
        {
            Status[key] = "off";
            Log($"{name}: off");
            return;
        }
        var harmony = new Harmony($"io.github.ksa-macos-port.{key}");
        try
        {
            Status[key] = apply(harmony);
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(harmony.Id);
            while (e.InnerException != null && e is not Skip)
                e = e.InnerException;
            Status[key] = $"skipped, the game's code changed ({e.Message})";
        }
        Log($"{name}: {Status[key]}");
    }

    public static void Log(string message) => Console.WriteLine($"[hooks] {message}");
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
