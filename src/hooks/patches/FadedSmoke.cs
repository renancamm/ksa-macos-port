using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Faded smoke. A rocket's plumes that merge into a group's end at a handoff age, not at their lifetime, and
// GetEntrainedRadiusAge makes that age grow with the cube of the spacing, up to the plume's expanded radius: boosters set
// wide apart get hours of game time (13,500 to 312,000 s measured, for a lifetime of 420 s). The density reaches zero at the lifetime whatever the handoff
// age, so the plume is gone from view while PlumeSegmentMaintenance keeps its segments. Warp's long steps stretch them
// and MoveVertices splits them, up to the 65,536 a body can hold, and the smoke's shadow cascades draw them: 1-3 fps
// with nothing in the sky. ApplySegmentsAge now removes a segment at the shorter of the two ages. What it removes has no
// density left; near live smoke it can still shift the raymarch's samples a little, as small as the plume's own flicker.
internal static class FadedSmoke
{
    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(PlumeSegmentMaintenance), "ApplySegmentsAge"),
            transpiler: new HarmonyMethod(typeof(FadedSmoke), nameof(Transpiler)));
        return "on";
    }

    // Both reads of "vertex.HandoffAgeSeconds", the segment's two ends
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var handoff = Find.Field(typeof(PlumeVertex), nameof(PlumeVertex.HandoffAgeSeconds));
        var reads = code.Where(c => c.LoadsField(handoff)).ToList();
        if (reads.Count != 2)
            throw new Skip($"the handoff age read {reads.Count} times");
        foreach (var read in reads)
        {
            read.opcode = OpCodes.Call;
            read.operand = Find.Method(typeof(FadedSmoke), nameof(LastAge));
        }
        return code;
    }

    private static float LastAge(PlumeVertex vertex) => Math.Min(vertex.HandoffAgeSeconds, vertex.LifetimeSeconds);
}
