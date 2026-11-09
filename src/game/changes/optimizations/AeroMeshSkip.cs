using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA;
using KSA.Aero;

namespace KsaMetal.Hooks.Optimizations;

// Aero mesh skip. Since KSA 5801 every vessel builds an aerodynamic mesh from its colliders (AeroMeshBuilder.Refresh,
// from PartTree's derived data) each time its parts change: a launch, a split, a staging, a docking, every piece of a
// breakup. The game's aerodynamics don't use it yet; only its debug tools read it: Draw Aero Mesh, the vessel debug
// window's readout and the dumpaero command. The build runs on the vessels' worker pool, where any thread that waits
// for that pool picks it up, the physics' own threads included. It is built in the Vehicle Editor and while Draw Aero
// Mesh is on, and for a vessel the debug tools ask for when they ask; the game is the same. The hook rests on nothing
// else reading the mesh: a KSA update that reads it (its aerodynamics) needs the hook changed or removed.
[Hook(Phase.Default, Switch = nameof(Settings.SkipUnusedAeroMeshes))]
internal static class AeroMeshSkip
{
    // The builders whose build was skipped, with their vessel's parts, for the debug tools to build on demand
    private static readonly ConditionalWeakTable<AeroMeshBuilder, PartTree> Skipped = new();
    private static bool _building;

    public static string Apply(Harmony harmony)
    {
        var refresh = Find.Method(typeof(AeroMeshBuilder), nameof(AeroMeshBuilder.Refresh));
        var draw = Find.Method(typeof(AeroMeshDebugDraw), nameof(AeroMeshDebugDraw.Draw));
        var readout = Find.Method(typeof(AeroMeshDebugDraw), nameof(AeroMeshDebugDraw.DrawReadout));
        var dump = Find.Method(typeof(AeroDump), nameof(AeroDump.WriteDump));
        harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(AeroMeshSkip), nameof(RefreshPrefix)));
        harmony.Patch(draw, prefix: new HarmonyMethod(typeof(AeroMeshSkip), nameof(BuilderPrefix)));
        harmony.Patch(readout, prefix: new HarmonyMethod(typeof(AeroMeshSkip), nameof(BuilderPrefix)));
        harmony.Patch(dump, prefix: new HarmonyMethod(typeof(AeroMeshSkip), nameof(DumpPrefix)));
        return "on";
    }

    private static bool RefreshPrefix(AeroMeshBuilder __instance, PartTree tree)
    {
        if (_building || Program.EditorFlag || Program.DrawAeroMesh)
        {
            Skipped.Remove(__instance);
            return true;
        }
        Skipped.AddOrUpdate(__instance, tree);
        return false;
    }

    // Draw Aero Mesh, the readout: the mesh a skipped builder never made, built now (it shows once built)
    private static void BuilderPrefix(AeroMeshBuilder builder) => Build(builder);

    // dumpaero: waits for the build it asks for
    private static void DumpPrefix(PartTree tree) => Build(tree.AeroMesh);

    private static void Build(AeroMeshBuilder builder)
    {
        if (!Skipped.TryGetValue(builder, out var tree))
            return;
        _building = true;
        try
        {
            builder.Refresh(tree, JobSystems.VehicleWorkerPool);
        }
        finally
        {
            _building = false;
        }
    }
}
