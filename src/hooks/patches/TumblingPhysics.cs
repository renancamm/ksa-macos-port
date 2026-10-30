using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepuPhysics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Tumbling physics. PhysicsStates.ComputeTimestep limits a vehicle's step so that it turns 30 degrees at most, but as
// (pi/6)^2 / w^2, the square of the time it means: above 16.5 rad/s every step is the 1 ms minimum, for the whole
// physics bubble. With the square root that takes 523 rad/s, but the square root alone allows steps long enough for
// falling pieces to pass through the ground (about 0.2 s at 2.5 rad/s: 5 landings of 12), so it gives at most 1/60 s,
// the game's own step at normal speed. The game's value stays where it is longer: below about 4 rad/s nothing changes.
// And KinematicStates.ClampVelocities caps a spin at 5236 rad/s, which a piece of a breaking booster can reach, and then
// each step costs more than ten times as much. The cap becomes 200 rad/s, above any tumble measured on its own (about
// 170). The three methods are marked for inlining, so a patch on them wouldn't reach their callers: the callers call
// changed copies of them instead.
internal static class TumblingPhysics
{
    private const double SpinCap = 200, GameSpinCap = 5235.987755982988;
    private const float Arc = 0.5235988f;
    private const double LongestStep = 1.0 / 60;

    public static string Apply(Harmony harmony)
    {
        var timestep = Find.Method(typeof(PhysicsStates), "ComputeTimestep");
        var clamp = Find.Method(typeof(KinematicStates), nameof(KinematicStates.ClampVelocities));
        var fromBepu = Find.Method(typeof(KinematicStates), nameof(KinematicStates.UpdateFromBepu));
        harmony.CreateReversePatcher(timestep, new HarmonyMethod(typeof(TumblingPhysics), nameof(Timestep))).Patch(HarmonyReversePatchType.Original);
        harmony.CreateReversePatcher(clamp, new HarmonyMethod(typeof(TumblingPhysics), nameof(Clamp))).Patch(HarmonyReversePatchType.Original);
        harmony.CreateReversePatcher(fromBepu, new HarmonyMethod(typeof(TumblingPhysics), nameof(FromBepu))).Patch(HarmonyReversePatchType.Original);
        Redirect(harmony, Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.UpdateSituationAndComputeTimestep)), nameof(ToTimestep));
        Redirect(harmony, Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateVelocityVerlet)), nameof(ToClamp));
        Redirect(harmony, Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.IntegrateRk8)), nameof(ToClamp));
        Redirect(harmony, Find.Method(typeof(ConstraintSim), nameof(ConstraintSim.UpdateVehicleFromSim)), nameof(ToFromBepu));
        Redirect(harmony, Find.Method(typeof(ConstraintSim), nameof(ConstraintSim.ReadBackFromSim)), nameof(ToFromBepu));
        return "on";
    }

    private static void Redirect(Harmony harmony, MethodInfo caller, string transpiler) =>
        harmony.Patch(caller, transpiler: new HarmonyMethod(typeof(TumblingPhysics), transpiler));

    private static IEnumerable<CodeInstruction> ToTimestep(IEnumerable<CodeInstruction> code) =>
        Swap(code, Find.Method(typeof(PhysicsStates), "ComputeTimestep"), nameof(Timestep));

    private static IEnumerable<CodeInstruction> ToClamp(IEnumerable<CodeInstruction> code) =>
        Swap(code, Find.Method(typeof(KinematicStates), nameof(KinematicStates.ClampVelocities)), nameof(Clamp));

    private static IEnumerable<CodeInstruction> ToFromBepu(IEnumerable<CodeInstruction> code) =>
        Swap(code, Find.Method(typeof(KinematicStates), nameof(KinematicStates.UpdateFromBepu)), nameof(FromBepu));

    // The one call of a method becomes a call of its copy
    private static List<CodeInstruction> Swap(IEnumerable<CodeInstruction> instructions, MethodInfo from, string to)
    {
        var code = instructions.ToList();
        int call = Find.Once(code, c => c.Calls(from), from.Name + "'s call");
        code[call].opcode = OpCodes.Call;
        code[call].operand = AccessTools.Method(typeof(TumblingPhysics), to);
        return code;
    }

    private static float SpinStep(float gameStep) => (float)Math.Max(gameStep, Math.Min(Math.Sqrt(gameStep), LongestStep));

    // ComputeTimestep with SpinStep after (pi/6)^2 / w^2: "ldc Arc, dup, mul, ldloc, div"
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    private static double Timestep(in VehicleProperties props, in KinematicStates kinematic, in PhysicsEnvironment environment, double airSpeed,
        ReadOnlySpan<ActiveChute> chutes, double minDt, double maxDt, double freeDt)
    {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int arc = Find.Once(code, c => c.opcode == OpCodes.Ldc_R4 && c.operand is float f && f == Arc, "the spin arc");
            if (arc + 4 >= code.Count || code[arc + 1].opcode != OpCodes.Dup || code[arc + 2].opcode != OpCodes.Mul || code[arc + 4].opcode != OpCodes.Div)
                throw new Skip("the spin limit's code changed");
            code.Insert(arc + 5, new CodeInstruction(OpCodes.Call, Find.Method(typeof(TumblingPhysics), nameof(SpinStep))));
            return code;
        }
        _ = Transpiler(null!);
        throw new NotImplementedException();
    }

    // ClampVelocities with the spin cap (two constants: the test and the scale)
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    private static void Clamp(ref KinematicStates self)
    {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            var caps = Enumerable.Range(0, code.Count).Where(i => code[i].opcode == OpCodes.Ldc_R8 && code[i].operand is double d && d == GameSpinCap).ToList();
            if (caps.Count != 2)
                throw new Skip($"the spin cap found {caps.Count} times");
            foreach (int i in caps)
                code[i].operand = SpinCap;
            return code;
        }
        _ = Transpiler(null!);
        throw new NotImplementedException();
    }

    // UpdateFromBepu, which ClampVelocities is inlined into, with the copy above
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    private static void FromBepu(ref KinematicStates self, in BodyReference bodyRef)
    {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => ToClamp(instructions);
        _ = Transpiler(null!);
        throw new NotImplementedException();
    }
}
