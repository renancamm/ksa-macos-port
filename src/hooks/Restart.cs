using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Restart. After a change that needs one, the game's settings offer to restart it (RequiresRestartPopup), and the
// game starts its own executable again, which in the app is .NET's dotnet with no program: it prints its help, and the
// game only quits. The button starts the app again instead, through macOS (open -n), while this game is still running:
// a process this game started itself would be its leftover, which macOS ends right after the game quits when the app
// was started from the Finder. The new game's launcher waits until this one has quit (KSA_PORT_WAIT), and the new game
// reads the port settings as any start does. The benchmark restarts the game the same way.
internal static class Restart
{
    private static readonly MethodInfo ProcessStart = AccessTools.Method(typeof(Process), nameof(Process.Start), [typeof(string)]);

    public static string Apply(Harmony harmony)
    {
        if (Launcher == null)
            return "off (the game was not started by the app)";
        var buttons = typeof(RequiresRestartPopup).GetNestedTypes(AccessTools.all).SelectMany(AccessTools.GetDeclaredMethods)
            .Where(m => !m.IsGenericMethodDefinition && PatchProcessor.GetOriginalInstructions(m).Any(i => i.Calls(ProcessStart)))
            .ToList();
        if (buttons.Count != 1)
            throw new Skip($"the restart button's Process.Start found {buttons.Count} times");
        harmony.Patch(buttons[0], transpiler: new HarmonyMethod(typeof(Restart), nameof(Transpiler)));
        return "on";
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
            yield return i.Calls(ProcessStart) ? new CodeInstruction(i) { operand = AccessTools.Method(typeof(Restart), nameof(StartAgain)) } : i;
    }

    // In place of Process.Start(<the game's executable>): the game quits right after
    private static Process? StartAgain(string _)
    {
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Hooks.Log($"restart: could not start the game again: {e.Message}");
        }
        return null;
    }

    public static string? Launcher => Environment.GetEnvironmentVariable("KSA_PORT_LAUNCHER") is { Length: > 0 } l ? l : null;

    // Starts the app again, to begin once this game has quit, with the game's arguments and environment and these
    // variables; the caller makes the game quit
    public static void Run(IReadOnlyDictionary<string, string>? environment = null)
    {
        var app = Path.GetFullPath(Path.Combine(Launcher ?? throw new InvalidOperationException("no launcher"), "..", "..", ".."));
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        // open hands its own environment to the app: this game's, without what the port settings set (the new game sets
        // them again from the file) and the loader's notes about them. What must arrive goes as --env too.
        var fromFile = (Environment.GetEnvironmentVariable("KSA_PORT_SET") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in fromFile.Concat(["KSA_PORT_SET", "KSA_PORT_SETTINGS", "KSA_PORT_OUTSIDE"]))
            start.Environment.Remove(name);
        var variables = new Dictionary<string, string>(environment ?? new Dictionary<string, string>())
        {
            ["KSA_PORT_WAIT"] = Environment.ProcessId.ToString()
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add(app);
        foreach (var (name, value) in variables)
        {
            start.Environment[name] = value;
            start.ArgumentList.Add("--env");
            start.ArgumentList.Add($"{name}={value}");
        }
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (args.Count > 0)
        {
            start.ArgumentList.Add("--args");
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
        }
        using var open = Process.Start(start)!;
        open.WaitForExit();
        if (open.ExitCode != 0)
            throw new InvalidOperationException($"open exited with {open.ExitCode}");
        Hooks.Log("restart: the game starts again once it has quit");
    }
}
