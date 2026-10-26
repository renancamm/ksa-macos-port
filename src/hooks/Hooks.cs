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
    // What each hook did, by its key: the Mac Port tab shows a setting whose hook is unavailable as such
    public static readonly Dictionary<string, HookStatus> Status = new();
    // The hooks in the order they were applied: name and key
    private static readonly List<(string Name, string Key)> Applied = new();

    // What every hook did, as the log said it ("terrain cache: on; ...")
    public static string Summary => string.Join("; ", Applied.Select(h => $"{h.Name}: {Status[h.Key].Text}"));

    // The app's Contents/Resources: the hooks are in its Hooks folder
    public static string Resources => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(typeof(Hooks).Assembly.Location)!, ".."));

    public static void Apply()
    {
        Settings.Load();
        // Off (KSA_HOOKS=0), they load only for a benchmark run, which they start
        if (!Settings.On("hooks"))
        {
            Log("off, except for the benchmark");
            Run("documents folder", "documents", true, Documents.Apply);
            Run("benchmark", "benchmark", true, BenchmarkRun.Apply);
            return;
        }
        Run("terrain cache", "terrain_cache", Settings.On("terrain_cache"), TerrainCache.Apply);
        Run("outline skip", "outline_skip", Settings.On("outline_skip"), OutlineSkip.Apply);
        Run("shadow search", "shadow_search", Settings.On("shadow_search"), ShadowSearch.Apply);
        Run("cloud shadow tuning", "cloud_shadow_tuning", true, CloudShadows.Apply);
        Run("plume shadow tuning", "plume_shadow_tuning", true, PlumeShadows.Apply);
        Run("crew portraits", "crew_portraits", true, CrewPortraits.Apply);
        Run("worker parking", "worker_park", Settings.On("worker_parking"), WorkerPark.Apply);
        Run("loading pace", "loading_pace", Settings.On("loading_pace"), LoadingPace.Apply);
        Run("render scale", "render_scale", true, RenderScale.Apply);
        Run("HUD layout", "hud_layout", true, HudLayout.Apply);
        Run("trail caches", "trail_caches", true, TrailCaches.Apply);
        // The port's own parts of the game
        Run("documents folder", "documents", true, Documents.Apply);
        Run("restart", "restart", true, Restart.Apply);
        Run("Mac Port tab", "mac_port_tab", true, MacPortTab.Apply);
        Run("benchmark", "benchmark", true, BenchmarkRun.Apply);
    }

    // apply(harmony) patches and returns what it did ("on", "off", ...); it throws Skip when the game's code isn't what
    // it expects. Either way the hook's patches are undone, and only those: a hook changes the game's state, when it
    // does, after its last lookup.
    private static void Run(string name, string key, bool enabled, Func<Harmony, string> apply)
    {
        Applied.Add((name, key));
        if (!enabled)
        {
            Status[key] = new HookStatus(HookState.Off, "off");
            Log($"{name}: off");
            return;
        }
        var harmony = new Harmony($"io.github.ksa-macos-port.{key}");
        Exception? failure = null;
        try
        {
            Status[key] = new HookStatus(HookState.On, apply(harmony));
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(harmony.Id);
            if (Skip.In(e) is { } skip)
            {
                Status[key] = new HookStatus(HookState.Skipped, $"skipped, the game's code changed ({skip.Message})");
            }
            else
            {
                failure = e;
                Status[key] = new HookStatus(HookState.Failed, $"failed ({Describe(e)})");
            }
        }
        Log($"{name}: {Status[key].Text}");
        if (failure != null)
            Console.WriteLine(failure);
    }

    // An exception in a hook's code while the game runs, caught where handing the game its own behaviour from then on
    // is whole (the caller does that): the hook fails as it would when the game starts, once. A hook that changes how
    // the whole frame is drawn (the render scale) can't hand it back halfway, and lets the exception through.
    public static void Fail(string key, Exception e)
    {
        if (Status.GetValueOrDefault(key)?.State == HookState.Failed)
            return;
        Status[key] = new HookStatus(HookState.Failed, $"failed while the game ran ({Describe(e)})");
        Log($"{Applied.FirstOrDefault(h => h.Key == key).Name ?? key}: {Status[key].Text}");
        Console.WriteLine(e);
    }

    // The error a failure comes down to: its innermost exception's type and message
    public static string Describe(Exception e)
    {
        while (e.InnerException != null)
            e = e.InnerException;
        return $"{e.GetType().Name}: {e.Message}";
    }

    public static void Log(string message) => Console.WriteLine($"[hooks] {message}");
}

// What a hook did when the game started: on (Text says how), off, skipped (the game's code it changes has moved) or
// failed (an error of the hook's own). Text is what the log says.
public enum HookState { On, Off, Skipped, Failed }

public sealed record HookStatus(HookState State, string Text)
{
    public bool Unavailable => State is HookState.Skipped or HookState.Failed;
}

// Why a patch can't apply: its target or the code it changes isn't what it expects
public class Skip(string message) : Exception(message)
{
    // The Skip behind an exception (Harmony and type initializers wrap it), or null
    public static Skip? In(Exception? e)
    {
        for (; e != null; e = e.InnerException)
            if (e is Skip skip)
                return skip;
        return null;
    }
}

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
