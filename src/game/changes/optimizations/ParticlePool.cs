using HarmonyLib;
using KSA.Rendering.Particles;

namespace KsaMetal.Hooks.Optimizations;

// Particle pool. The game makes its particle system with room for 1024 emitters of 512 particles each, five GPU buffers
// sized for all of them (about 300 MB) whatever its particle settings. An effect takes a few emitters while it lives,
// and one that finds too few free isn't spawned. The most measured at once: 36 in the Mixed Test's explosion, 56 in a
// launch of the benchmark's giga rocket and its breakup. A prefix on the system's constructor gives it room for 256
// (about 75 MB): the Extra Performance row Fewer Effects at Once, on in Performance and with 8 GB of memory or less.
[Hook(Phase.Default, Switch = nameof(Settings.FewerEffectsAtOnce))]
internal static class ParticlePool
{
    private const int Emitters = 256;

    public static string Apply(Harmony harmony)
    {
        var system = Find.Constructor(typeof(ParticleSystem<ParticleUpdateData, ParticleRenderData>), typeof(int), typeof(int));
        harmony.Patch(system, prefix: new HarmonyMethod(typeof(ParticlePool), nameof(Making)));
        return $"on, {Emitters} emitters";
    }

    private static void Making(ref int emitterCount)
    {
        if (emitterCount > Emitters)
        {
            Hooks.Log($"{nameof(ParticlePool)}: room for {Emitters} emitters, not {emitterCount}");
            emitterCount = Emitters;
        }
    }
}
