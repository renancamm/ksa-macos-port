using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Fixes;

// Engine glow. A game bug since KSA 5801: the service modules' service bay (CoreServiceModuleA, <Habitat
// Key="ServiceBay" /> without a storage volume) holds Food and LiOH moles in containers of no volume, whose
// Mole.FilledFraction is 0/0, not a number. FxTemperature.UpdateModules averages every mole's fill for the engines
// without tanks of their own, and that average makes their temperature NaN for good: their heat glow and frost never show,
// and on Apple GPUs, which pass a NaN from the vertex to the fragment stage as a large positive number, they glow
// white-hot all the time. A transpiler has the update read a container of no volume as empty (counted, so these two
// moles weigh the frost average a little toward empty).
[Hook(Phase.Default)]
internal static class EngineGlow
{
    private static MethodInfo _filled = null!, _fill = null!;

    public static string Apply(Harmony harmony)
    {
        var update = Find.Method(typeof(FxTemperature), nameof(FxTemperature.UpdateModules));
        _filled = Find.Method(typeof(Mole), nameof(Mole.FilledFraction), typeof(MoleState).MakeByRefType());
        _fill = Find.Method(typeof(EngineGlow), nameof(Fill));
        int calls = PatchProcessor.GetOriginalInstructions(update).Count(i => i.Calls(_filled));
        if (calls == 0)
            throw new Skip("FxTemperature.UpdateModules: Mole.FilledFraction not found");
        harmony.Patch(update, transpiler: new HarmonyMethod(typeof(EngineGlow), nameof(Transpiler)));
        return "on";
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
            yield return i.Calls(_filled)
                ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = _fill }
                : i;
    }

    // In place of mole.FilledFraction(in state): a container of no volume holds nothing
    private static float Fill(Mole mole, in MoleState state) =>
        mole.ContainerVolume > 0f ? mole.FilledFraction(in state) : 0f;
}
