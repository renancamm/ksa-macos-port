using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace KsaMetal.Hooks;

// KSA Metal Hooks: changes to KSA's code made in memory with Harmony when the game starts (KSA.dll stays as it is on
// disk). Each patch finds its targets by name and its code by pattern; when a KSA update moved either, it is skipped
// and the log says so, and the game runs as it would without it. A patch is applied whole or not at all.
public static class Hooks
{
    // What each hook did, by its name (its class's): the Mac Port tab shows a setting whose hook is skipped or failed as
    // unavailable. Hooks that fail while the game runs can do so on worker threads (Fail)
    public static readonly ConcurrentDictionary<string, HookStatus> Status = new();
    // The hooks in the order they were applied
    private static readonly List<string> Applied = new();
    private static readonly object FailLock = new();

    // What every hook did, as the log said it ("TerrainCache: on; ..."), and whether they were off but for the benchmark
    public static string Summary => string.Join("; ", (Settings.Hooks ? Array.Empty<string>() : ["off, except for the benchmark"])
        .Concat(Applied.Select(h => $"{h}: {Status[h].Text}")));

    // The app's Contents/Resources: the hooks are in its Hooks folder
    public static string Resources => System.IO.Path.GetFullPath(System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(typeof(Hooks).Assembly.Location)!, ".."));

    public static void Apply()
    {
        Settings.Load();
        // Off (KSA_MTL_HOOKS=0), they load only for a benchmark run, which they start
        if (!Settings.Hooks)
            Log("off, except for the benchmark");
        foreach (var (type, hook) in All())
        {
            if (!Settings.Hooks && !hook.WithHooksOff)
                continue;
            Func<Harmony, string> apply;
            bool on;
            try
            {
                apply = type.GetMethod("Apply", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        [typeof(Harmony)])?.CreateDelegate<Func<Harmony, string>>() ??
                    throw new InvalidOperationException("it has no static string Apply(Harmony)");
                on = hook.Switch == null || Switched(hook.Switch);
            }
            catch (Exception e)
            {
                // A mistake in a hook's mark fails that hook alone
                Failed(type.Name, $"failed ({Describe(e)})");
                continue;
            }
            Run(apply, on);
        }
    }

    // The hooks, the classes marked [Hook], in the order they apply: by phase, then by name
    private static IEnumerable<(Type Type, HookAttribute Hook)> All()
    {
        Type?[] types;
        try
        {
            types = typeof(Hooks).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            // A class that names a type a KSA update removed doesn't load: the others still apply, and the summary
            // says failed, so the tab and a benchmark run see it
            var why = string.Join("; ", e.LoaderExceptions.OfType<Exception>().Select(x => x.Message).Distinct());
            Failed("Hooks", $"failed (a class of the hooks didn't load: {why})");
            types = e.Types;
        }
        return types.OfType<Type>().Select(t => (Type: t, Hook: t.GetCustomAttribute<HookAttribute>()!)).Where(h => h.Hook != null)
            .OrderBy(h => h.Hook.Phase).ThenBy(h => h.Type.Name, StringComparer.Ordinal).ToList();
    }

    private static void Failed(string name, string text)
    {
        Applied.Add(name);
        Status[name] = new HookStatus(HookState.Failed, text);
        Log($"{name}: {text}");
    }

    // A hook's switch: the bool setting of Settings it names
    private static bool Switched(string setting) =>
        typeof(Settings).GetProperty(setting)?.GetValue(null) as bool? ??
        throw new InvalidOperationException($"Settings.{setting} isn't a switch");

    // A hook, its class's Apply, named after the class: apply(harmony) patches and returns what it did ("on", ...; "off"
    // or "off, <why>" when it found nothing to do); it throws Skip when the game's code isn't what it expects. Either
    // way the hook's patches are undone, and only those: any other change a hook makes to the game's state comes after
    // its last lookup.
    private static void Run(Func<Harmony, string> apply, bool enabled)
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
            var text = apply(harmony);
            Status[name] = new HookStatus(text == "off" || text.StartsWith("off ", StringComparison.Ordinal) ||
                text.StartsWith("off,", StringComparison.Ordinal) ? HookState.Off : HookState.On, text);
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
        var failed = new HookStatus(HookState.Failed, $"failed while the game ran ({Describe(e)})");
        // Once, also when two threads fail it at the same time
        lock (FailLock)
        {
            if (Status.GetValueOrDefault(name)?.State == HookState.Failed)
                return;
            Status[name] = failed;
        }
        Log($"{name}: {failed.Text}");
        Console.WriteLine(e);
    }

    // A hook that is skipped or failed, with what it did; null when it applied, is off or doesn't exist
    public static HookStatus? Unavailable(string name) => Status.GetValueOrDefault(name) is { Unavailable: true } status ? status : null;

    // A part of a hook that the hook runs on without: whether it applied. One the game's code doesn't fit (Skip) is
    // off, with why; any other error is a bug of the part's own, said as a failure with where it happened. The part
    // undoes its own patches when it stops halfway
    public static bool Part(string what, Action apply)
    {
        try
        {
            apply();
            return true;
        }
        catch (Exception e) when (Skip.In(e) is { } skip)
        {
            Log($"{what}: off ({skip.Message})");
        }
        catch (Exception e)
        {
            Log($"{what}: failed ({Describe(e)})");
            Console.WriteLine(e);
        }
        return false;
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

// When a hook applies. The hooks apply phase by phase, and in a phase in the order of their names: a rule of order
// between hooks is a phase, and its reason is said here.
public enum Phase
{
    // GenericDetours: it changes how every detour made after it is built, the other hooks' and the mods'
    First,
    // The hooks that patch small methods the targets of other hooks call: the JIT can inline a small method into a
    // caller compiled before its patch, and a patch reaches only code compiled after it (Documents: GameSettings.Save,
    // which MacPortTab patches, calls Constants.DocumentsFolderPath; WindowOrigin: the windows' and displays' positions,
    // which GameSettings.ApplyTo, patched by Hdr, reads)
    BeforeCallers,
    // The hooks no rule of order holds for
    Default,
    // After every hook that patches a method Program.RenderGame or RenderEditor call: patching those two compiles them
    // again, and the JIT can inline a small method they call (RenderAaPreparePasses) as it is then, so a patch made on
    // it later never runs from them (MetalFX Spatial drew nothing). UiViewSize's transpiler on Program.OnFrame, which
    // calls them, compiles it again the same way
    AfterRenderPatches,
    // The port's own parts of the game (Restart, HDR, the Mac Port tab and its presets, the benchmark's runs), after the
    // patches: the tab's transpiler on GameSettings.OnDrawUi works on the code ResolutionList's made of it. Restart and
    // HDR have no rule of order, and stay with the other parts in the log
    PortParts,
}

// A hook: a class whose static string Apply(Harmony), public or not, patches the game and returns what the log says
// (Run, in Hooks). Switch names the setting that turns it on, a bool of Settings (nameof(Settings.TerrainCache));
// without one it applies always. WithHooksOff: it applies with the hooks setting off too, for a benchmark run.
[AttributeUsage(AttributeTargets.Class)]
internal sealed class HookAttribute(Phase phase) : Attribute
{
    public Phase Phase { get; } = phase;
    public string? Switch { get; set; }
    public bool WithHooksOff { get; set; }
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

    public static ConstructorInfo Constructor(Type type, params Type[] parameters) =>
        AccessTools.Constructor(type, parameters.Length > 0 ? parameters : null) ??
        throw new Skip($"{type.Name}'s constructor not found");

    public static MethodInfo Getter(Type type, string name) =>
        AccessTools.PropertyGetter(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    public static MethodInfo Setter(Type type, string name) =>
        AccessTools.PropertySetter(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    public static FieldInfo Field(Type type, string name) =>
        AccessTools.Field(type, name) ?? throw new Skip($"{type.Name}.{name} not found");

    // A field of this type: code that reads it by reflection casts it
    public static FieldInfo Field(Type type, string name, Type of) =>
        Field(type, name) is var field && field.FieldType == of ? field : throw new Skip($"{type.Name}.{name} not a {of.Name}");

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

// Changes to a method's code that more than one patch makes
public static class Il
{
    // The step's vehicle: the local the List<VehicleUpdateState> item read last before the call is stored in
    public static int StepVehicle(List<CodeInstruction> code, int call)
    {
        var item = AccessTools.Method(typeof(List<KSA.VehicleUpdateState>), "get_Item");
        int at = code.FindLastIndex(call, c => c.Calls(item));
        if (at < 0 || !code[at + 1].IsStloc())
            throw new Skip("the step's vehicle not found");
        return code[at + 1].LocalIndex();
    }

    // The call becomes a call of to, with these arguments after the call's own; a jump to the call lands on the first of
    // them
    public static void SwapCall(List<CodeInstruction> code, int call, MethodInfo to, params CodeInstruction[] arguments)
    {
        arguments[0].MoveLabelsFrom(code[call]).MoveBlocksFrom(code[call]);
        code[call].opcode = OpCodes.Call;
        code[call].operand = to;
        code.InsertRange(call, arguments);
    }
}
