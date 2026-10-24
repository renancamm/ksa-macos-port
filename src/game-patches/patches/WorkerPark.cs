using System;
using System.Globalization;
using HarmonyLib;
using KSA.Concurrency;

namespace KsaPatches;

// Worker parking. Every frame, Program sets the job pools' park timeout to half the frame time: a worker with nothing
// to do spins that long before it sleeps until work arrives, so the four workers spend about 1.5 cores spinning. The
// timeout is capped at 0.25 ms (KSA_PARK_MS overrides): a worker then sleeps soon after its last job and is woken
// when the next arrives. The game draws the same frames as fast, with about 1.6 cores less of CPU time.
internal static class WorkerPark
{
    private static readonly double CapSeconds =
        (double.TryParse(Environment.GetEnvironmentVariable("KSA_PARK_MS"), NumberStyles.Float, CultureInfo.InvariantCulture,
            out double ms) && ms >= 0 ? ms : 0.25) / 1000.0;

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(DynamicWorkerPool), nameof(DynamicWorkerPool.SetParkTimeout)),
            prefix: new HarmonyMethod(typeof(WorkerPark), nameof(Prefix)));
        return "on, " + (CapSeconds * 1000).ToString("0.###", CultureInfo.InvariantCulture) + " ms";
    }

    private static void Prefix(ref double seconds) => seconds = Math.Min(seconds, CapSeconds);
}
