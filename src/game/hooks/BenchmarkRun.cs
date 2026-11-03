using System.IO;
using HarmonyLib;
using KSA;
using KsaMetal.Benchmark;
using KsaMetal.Hooks.Fixes;

namespace KsaMetal.Hooks;

// The benchmark's runs in the game (src/game/benchmark/Runs.cs): a postfix on Program.OnFrame gives them every
// frame. They start from benchmark.sh (KSA_BENCH_OUT) or the Mac Port tab, and borrow the hooks' log, the summary of
// what the hooks applied and the game's restart (Restart.cs).
[Hook(Phase.PortParts, WithHooksOff = true)]
internal static class BenchmarkRun
{
    public static string Apply(Harmony harmony)
    {
        if (!File.Exists(Path.Combine(Hooks.Resources, "Benchmark", "benchmark.txt")))
            return "off (not in the app)";
        harmony.Patch(Find.Method(typeof(Program), "OnFrame", typeof(double), typeof(double)),
            postfix: new HarmonyMethod(typeof(Runs), nameof(Runs.AfterFrame)));
        return Runs.Install(Hooks.Log, () => Hooks.Summary, Restart.Launcher != null ? Restart.Run : null);
    }
}
