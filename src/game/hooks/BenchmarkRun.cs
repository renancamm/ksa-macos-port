using System.IO;
using HarmonyLib;
using KSA;
using KsaMetal.Benchmark;
using KsaMetal.Hooks.Fixes;

namespace KsaMetal.Hooks;

// The benchmark's runs in the game (src/game/benchmark/Runs.cs): a postfix on Program.OnFrame gives them every
// frame, and a prefix on Universe.GetJobSimStep the moment before each step of the simulation (the director's step
// limit). They start from benchmark.sh (KSA_BENCH_OUT) or the Mac Port tab, and borrow the hooks' log, the summary of
// what the hooks applied, the game's restart (Restart.cs) and Harmony, for the postfixes a run puts in when it asks for
// them (the game's GPU zones' labels).
[Hook(Phase.PortParts, WithHooksOff = true)]
internal static class BenchmarkRun
{
    public static string Apply(Harmony harmony)
    {
        if (!File.Exists(Path.Combine(Hooks.Resources, "Benchmark", "benchmark.txt")))
            return "off (not in the app)";
        harmony.Patch(Find.Method(typeof(Program), "OnFrame", typeof(double), typeof(double)),
            postfix: new HarmonyMethod(typeof(Runs), nameof(Runs.AfterFrame)));
        harmony.Patch(Find.Method(typeof(Universe), "GetJobSimStep", typeof(double)),
            prefix: new HarmonyMethod(typeof(Runs), nameof(Runs.BeforeStep)));
        static HarmonyMethod? Of(System.Reflection.MethodInfo? m) => m != null ? new HarmonyMethod(m) : null;
        return Runs.Install(Hooks.Log, () => Hooks.Summary, Restart.Launcher != null ? Restart.Run : null,
            (target, prefix, postfix, transpiler) => harmony.Patch(target, Of(prefix), Of(postfix), Of(transpiler)),
            (type, name, parameters) => Find.Method(type, name, parameters));
    }
}
