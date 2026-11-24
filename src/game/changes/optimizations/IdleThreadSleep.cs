using System;
using System.Globalization;
using HarmonyLib;
using KSA.Concurrency;

namespace KsaMetal.Hooks.Optimizations;

// Idle Thread Sleep, idle_thread_sleep (port-settings.toml). Every frame, Program sets the job pools' park timeout to
// half the frame time: a worker with nothing to do spins that long before it sleeps until work arrives, so the four
// workers spend about 1.5 cores spinning. The timeout is capped at idle_thread_sleep_ms (0.25 ms): a worker then sleeps
// soon after its last job and is woken when the next arrives. The game draws the same frames as fast, with about 1.6
// cores less of CPU time.
[Hook(Phase.Default, Switch = nameof(Settings.IdleThreadSleep))]
internal static class IdleThreadSleep
{
    private static double CapSeconds;

    public static string Apply(Harmony harmony)
    {
        CapSeconds = Settings.IdleThreadSleepMs / 1000.0;
        harmony.Patch(Find.Method(typeof(DynamicWorkerPool), nameof(DynamicWorkerPool.SetParkTimeout)),
            prefix: new HarmonyMethod(typeof(IdleThreadSleep), nameof(Prefix)));
        return "on, " + (CapSeconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms";
    }

    private static void Prefix(ref double seconds) => seconds = Math.Min(seconds, CapSeconds);
}
