using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Restart. After a change that needs one, the game's settings offer to restart it (RequiresRestartPopup), and the
// game starts its own executable again, which in the app is .NET's dotnet with no program: it prints its help, and the
// game only quits. The button starts the app's launcher instead (KSA_PORT_LAUNCHER, which the launcher sets), once this
// game has quit, so that the new game reads the port settings as any start does. The benchmark restarts the game the
// same way.
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
        Run();
        return null;
    }

    public static string? Launcher => Environment.GetEnvironmentVariable("KSA_PORT_LAUNCHER") is { Length: > 0 } l ? l : null;

    // Starts the launcher again once this game has quit, with the game's arguments and these environment variables
    // added; the caller makes the game quit
    public static void Run(IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var arg in new[] { "-c", "while kill -0 \"$1\" 2> /dev/null; do sleep 0.2; done; shift; exec \"$@\"", "sh",
                     Environment.ProcessId.ToString(), Launcher ?? throw new InvalidOperationException("no launcher") })
            start.ArgumentList.Add(arg);
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            start.ArgumentList.Add(arg);
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        Hooks.Log("restart: the game starts again once it has quit");
        Process.Start(start);
    }
}
