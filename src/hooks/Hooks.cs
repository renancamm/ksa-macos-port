using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KsaMetalHooks.App;
using KsaMetalHooks.Patches;
using KsaMetalHooks.Tab;

namespace KsaMetalHooks;

// KSA Metal Hooks: changes to KSA's code made in memory with Harmony when the game starts (KSA.dll stays as it is on
// disk). Each patch finds its targets by name and its code by pattern; when a KSA update moved either, it is skipped
// and the log says so, and the game runs as it would without it. A patch is applied whole or not at all.
public static class Hooks
{
    // What each hook did, by its name (its class's): the Mac Port tab shows a setting whose hook is unavailable so
    public static readonly Dictionary<string, HookStatus> Status = new();
    // The hooks in the order they were applied
    private static readonly List<string> Applied = new();

    // What every hook did, as the log said it ("TerrainCache: on; ...")
    public static string Summary => string.Join("; ", Applied.Select(h => $"{h}: {Status[h].Text}"));

    // The app's Contents/Resources: the hooks are in its Hooks folder
    public static string Resources => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(typeof(Hooks).Assembly.Location)!, ".."));

    public static void Apply()
    {
        Settings.Load();
        // Off (KSA_MTL_HOOKS=0), they load only for a benchmark run, which they start
        if (!Settings.On("hooks"))
        {
            Log("off, except for the benchmark");
            Run(Documents.Apply);
            Run(BenchmarkRun.Apply);
            return;
        }
        // First, and so before any mod patches
        Run(GenericDetours.Apply, Settings.On("harmony_generic_detours"));
        Run(WindowOrigin.Apply);
        Run(ExtraWindows.Apply);
        Run(TerrainCache.Apply, Settings.On("terrain_cache"));
        Run(OutlineSkip.Apply, Settings.On("skip_unused_outlines"));
        Run(OverlaySkip.Apply, Settings.On("skip_unused_overlays"));
        Run(OceanBlending.Apply, Settings.On("skip_unused_ocean_blending"));
        Run(CloudShadows.Apply);
        Run(WarpCloudShadows.Apply, Settings.On("lighter_warp_cloud_shadows"));
        Run(PlumeShadows.Apply);
        Run(ShadowSearch.Apply, Settings.On("simpler_pcss_shadows"));
        Run(MilkyWayBake.Apply, Settings.On("baked_milky_way"));
        Run(CrewPortraits.Apply);
        Run(WorkerPark.Apply, Settings.On("idle_thread_sleep"));
        Run(FadedSmoke.Apply, Settings.On("clear_faded_smoke"));
        Run(TumblingPhysics.Apply, Settings.On("lighter_tumbling_physics"));
        Run(DebrisSettle.Apply, Settings.On("debris_settles_in_warp"));
        Run(LoadingPace.Apply, Settings.On("loading_screen_fps_cap"));
        Run(RenderScale.Apply);
        Run(HudLayout.Apply);
        Run(ResolutionList.Apply);
        Run(TrailCaches.Apply);
        Run(EditorClutter.Apply);
        Run(ClutterOff.Apply);
        Run(DescriptorRefresh.Apply);
        // After every hook that patches a method Program.RenderGame or RenderEditor call: patching those two compiles
        // them again, and the JIT can inline a small method they call (RenderAaPreparePasses) as it is then, so a patch
        // made on it later never runs from them (MetalFX Spatial drew nothing)
        Run(HiddenPartsSkip.Apply, Settings.On("skip_hidden_vessel_parts"));
        Run(UiViewSize.Apply);
        // The port's own parts of the game
        Run(Documents.Apply);
        Run(Restart.Apply);
        Run(Hdr.Apply);
        Run(Presets.Apply);
        Run(MacPortTab.Apply);
        Run(BenchmarkRun.Apply);
        Run(FrameCaptureCommand.Apply);
    }

    // A hook, its class's Apply, named after the class: apply(harmony) patches and returns what it did ("on", "off",
    // ...); it throws Skip when the game's code isn't what it expects. Either way the hook's patches are undone, and
    // only those: a hook changes the game's state, when it does, after its last lookup.
    private static void Run(Func<Harmony, string> apply, bool enabled = true)
    {
        var name = apply.Method.DeclaringType!.Name;
        Applied.Add(name);
        if (!enabled)
        {
            Status[name] = new HookStatus(HookState.Off, "off");
            Log($"{name}: off");
            return;
        }
        var harmony = new Harmony($"io.github.ksa-macos-port.{name}");
        Exception? failure = null;
        try
        {
            Status[name] = new HookStatus(HookState.On, apply(harmony));
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(harmony.Id);
            if (Skip.In(e) is { } skip)
            {
                Status[name] = new HookStatus(HookState.Skipped, $"skipped, the game's code changed ({skip.Message})");
            }
            else
            {
                failure = e;
                Status[name] = new HookStatus(HookState.Failed, $"failed ({Describe(e)})");
            }
        }
        Log($"{name}: {Status[name].Text}");
        if (failure != null)
            Console.WriteLine(failure);
    }

    // An exception in a hook's code while the game runs, caught where handing the game its own behaviour from then on
    // is whole (the caller does that): the hook fails as it would when the game starts, once. A hook that changes how
    // the whole frame is drawn (the render scale) can't hand it back halfway, and lets the exception through.
    public static void Fail(string name, Exception e)
    {
        if (Status.GetValueOrDefault(name)?.State == HookState.Failed)
            return;
        Status[name] = new HookStatus(HookState.Failed, $"failed while the game ran ({Describe(e)})");
        Log($"{name}: {Status[name].Text}");
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
