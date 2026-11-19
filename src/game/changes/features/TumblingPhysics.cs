using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Tumbling physics. PhysicsStates.ComputeTimestep limits a vehicle's step so that it turns 30 degrees at most, but as
// (pi/6)^2 / w^2, the square of the time it means: above 16.5 rad/s every step is the 1 ms minimum, for the whole
// physics bubble. With the square root that takes 523 rad/s, but the square root alone allows steps long enough for
// falling pieces to pass through the ground (about 0.2 s at 2.5 rad/s: 5 landings of 12), so it gives at most 1/60 s,
// the game's own step at normal speed. The game's value stays where it is longer: below about 4 rad/s nothing changes.
// It also stays for a vehicle touching the ground or water: a contact's jolt reaches the g-load that breaks a vehicle
// through its step, and a longer step averages it away (the giga rocket stayed whole on its pad at 50 rad/s).
// And KinematicStates.ClampVelocities caps a spin at 5236 rad/s, which a piece of a breaking booster can reach, and then
// each step costs more than ten times as much. The cap becomes 200 rad/s, above any tumble measured on its own (about
// 170). The three methods are marked for inlining, so a patch on them wouldn't reach their callers: the callers call
// changed copies of them instead.
// Pieces in warp. Since KSA 5901 the parts' surface aerodynamics act on every step, and they barely slow the spin a blast
// gives a small piece (100-200 rad/s, in thin air on the way down), so with the square root a bubble still steps every
// 3-5 ms while such pieces fall. From x8, a piece (DebrisSettle.IsPiece: nobody flies it, no crew, no engine firing)
// whose spin sets its step, in free flight with no chute, in a bubble whose constraint simulation doesn't run, takes the
// step it would take without the spin: at most 1 s and a quarter of its clearance at its speed (its height over the
// ground or sea less its bounding radius and 50 m, the margin of the game's own clearance check). Its loads are still
// worked out each step. Three things keep such steps stable, each found as a runaway to the spin cap: the constraint
// simulation crashed with them (an access violation in Bepu's island sleeper), so a bubble whose constraint simulation
// runs keeps the square root's step, and the clearance keeps a piece from reaching the ground within one; on a step that
// turns a vehicle more than 1.5 rad (only these do: the square root keeps a turn to 30 degrees, the game's rule to about
// 0.74 rad), the panels' moment, sampled once a step at whatever phase of the spin, would add up to a random walk (a
// piece 110 -> 262 rad/s in dense air), so it keeps only its part along the angular momentum, against the spin and at
// most what stops it; and the integration keeps the angular momentum's length but drifts the attitude over long steps,
// so a long piece slides onto its minor axis, the same momentum at many times the spin, in vacuum too, so after such a
// step the piece's rotational energy is no higher than before it. The steps are chosen in
// PhysicsBubble.FullPhysicsStepUntil, the loads worked out in PhysicsStates.ComputeAerodynamicLoads, the step integrated
// in PhysicsBubble.RunUnconstrainedVehicleStep: each one's single call is swapped for a method here.
[Hook(Phase.Default, Switch = nameof(Settings.LighterTumblingPhysics))]
internal static class TumblingPhysics
{
    private const double SpinCap = 200, GameSpinCap = 5235.987755982988;
    private const float Arc = 0.5235988f;
    private const double LongestStep = 1.0 / 60;
    private const double LongestPieceStep = 1, ClearanceShare = 0.25, ClearanceMargin = 50, LongTurn = 1.5;

    // A piece's longer step: when it started and the piece's rotational energy then, pending until an integration of a
    // step that started then takes it. One that went through the constraint simulation instead stays pending until the
    // piece's next step is chosen, and an integration of a step that started later doesn't take it
    private sealed class LongerStep
    {
        public bool Pending;
        public UniverseTime Start;
        public double Energy;
    }

    private static readonly ConditionalWeakTable<VehicleUpdateState, LongerStep> Longer = new();

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
        Redirect(harmony, Find.Method(typeof(PhysicsBubble), "FullPhysicsStepUntil"), nameof(ToPieceStep));
        Redirect(harmony, Find.Method(typeof(PhysicsBubble), "RunUnconstrainedVehicleStep"), nameof(ToPieceActivity));
        Redirect(harmony, Find.Method(typeof(PhysicsStates), "ComputeAerodynamicLoads"), nameof(ToSpinMoment));
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

    // The call becomes PieceStep(..., vehicle, bubble)
    private static IEnumerable<CodeInstruction> ToPieceStep(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var step = Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.UpdateSituationAndComputeTimestep));
        int call = Find.Once(code, c => c.Calls(step), "UpdateSituationAndComputeTimestep's call");
        Il.SwapCall(code, call, AccessTools.Method(typeof(TumblingPhysics), nameof(PieceStep)), CodeInstruction.LoadLocal(Il.StepVehicle(code, call)),
            new CodeInstruction(OpCodes.Ldarg_0));
        return code;
    }

    // The call becomes PieceActivity(..., vehicle)
    private static IEnumerable<CodeInstruction> ToPieceActivity(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var activity = Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.UpdateActivity));
        int call = Find.Once(code, c => c.Calls(activity), "UpdateActivity's call");
        Il.SwapCall(code, call, AccessTools.Method(typeof(TumblingPhysics), nameof(PieceActivity)), CodeInstruction.LoadLocal(Il.StepVehicle(code, call)));
        return code;
    }

    // The moment's call becomes SpinMoment(..., kinematic, props, dt): the method's arguments 1, 2 and 7
    private static IEnumerable<CodeInstruction> ToSpinMoment(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var torque = Find.Method(typeof(Disturbances), nameof(Disturbances.AddPureTorqueBody));
        int call = Find.Once(code, c => c.Calls(torque), "the moment's AddPureTorqueBody");
        Il.SwapCall(code, call, AccessTools.Method(typeof(TumblingPhysics), nameof(SpinMoment)), new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Ldarg_S, (byte)7));
        return code;
    }

    private static double PieceStep(ref PhysicsStates self, ActionSphere sphere, double minDt, double maxDt, ReadOnlySpan<ActiveChute> chutes,
        out PhysicsStepDetail detail, VehicleUpdateState vehicle, PhysicsBubble bubble)
    {
        double dt = self.UpdateSituationAndComputeTimestep(sphere, minDt, maxDt, chutes, out detail);
        if (Longer.TryGetValue(vehicle, out var previous))
            previous.Pending = false;
        if (detail.Limit != PhysicsStepLimit.AngularArc || Universe.GetSimulationSpeed() < DebrisSettle.MinWarp || chutes.Length > 0
            || self.Props.Situation != Situation.Maneuvering || (bubble.ConstraintSim?.IsAnyConstrained ?? false)
            || !DebrisSettle.IsPiece(vehicle))
            return dt;
        double clearance = self.GetPositionClosestParentBub(self.GetPositionBub()).Length()
            - Math.Max(self.Environment.TerrainRadius, self.Environment.OceanRadius) - self.Props.BoundingSphereRadiusBody - ClearanceMargin;
        double longer = Math.Min(Math.Min(detail.MaxDtIn, detail.FreeDt),
            Math.Min(LongestPieceStep, ClearanceShare * clearance / (self.GetVelocityBub().Length() + 1)));
        if (!(longer > dt))
            return dt;
        var planned = Longer.GetValue(vehicle, _ => new LongerStep());
        planned.Start = self.Origin.Time;
        planned.Energy = RotationalEnergy(in self.Kinematic, in self.Props);
        planned.Pending = true;
        detail.Timestep = longer;
        detail.Limit = PhysicsStepLimit.ShortStep;
        return longer;
    }

    private static void PieceActivity(ref PhysicsStates self, double dt, VehicleUpdateState vehicle)
    {
        if (Longer.TryGetValue(vehicle, out var planned) && planned.Pending)
        {
            planned.Pending = false;
            // The origin is at this step's end now: this step started when the longer one was planned (to a microsecond:
            // the game's clock counts whole nanoseconds)
            double after = RotationalEnergy(in self.Kinematic, in self.Props);
            if (Math.Abs((self.Origin.Time - planned.Start).Seconds() - dt) <= 1e-6 && after > planned.Energy && after > 0)
                self.Kinematic.AngularVelocityPhys *= Math.Sqrt(Math.Max(planned.Energy, 0) / after);
        }
        self.UpdateActivity(dt);
    }

    private static void SpinMoment(ref Disturbances disturbances, double3 torqueBody, in KinematicStates kinematic, in VehicleProperties props, double dt)
    {
        var rates = kinematic.AngularVelocityPhys.Transform(kinematic.Body2Phys.Inverse());
        if (rates.Length() * dt <= LongTurn)
        {
            disturbances.AddPureTorqueBody(torqueBody);
            return;
        }
        var momentum = Times(in VehiclePropertiesEx.get_TotalInertiaBody(in props), rates);
        double length = momentum.Length();
        if (!(length > 0))
            return;
        var axis = momentum / length;
        disturbances.AddPureTorqueBody(Math.Max(-length / dt, Math.Min(0, double3.Dot(torqueBody, axis))) * axis);
    }

    private static double RotationalEnergy(in KinematicStates kinematic, in VehicleProperties props)
    {
        var rates = kinematic.AngularVelocityPhys.Transform(kinematic.Body2Phys.Inverse());
        return 0.5 * double3.Dot(rates, Times(in VehiclePropertiesEx.get_TotalInertiaBody(in props), rates));
    }

    private static double3 Times(in Symmetric3x3 inertia, double3 v) => new(
        inertia.XX * v.X + inertia.YX * v.Y + inertia.ZX * v.Z,
        inertia.YX * v.X + inertia.YY * v.Y + inertia.ZY * v.Z,
        inertia.ZX * v.X + inertia.ZY * v.Y + inertia.ZZ * v.Z);

    private static float SpinStep(float gameStep, Situation situation) => situation.HasAnyContact()
        ? gameStep
        : (float)Math.Max(gameStep, Math.Min(Math.Sqrt(gameStep), LongestStep));

    // ComputeTimestep with SpinStep after (pi/6)^2 / w^2: "ldc Arc, dup, mul, ldloc, div"
    [HarmonyReversePatch(HarmonyReversePatchType.Original)]
    private static double Timestep(in VehicleProperties props, in KinematicStates kinematic, in PhysicsEnvironment environment, double airSpeed,
        ReadOnlySpan<ActiveChute> chutes, double minDt, double maxDt, double freeDt, PhysicsStepLimit freeLimit, out PhysicsStepDetail detail)
    {
        IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int arc = Find.Once(code, c => c.opcode == OpCodes.Ldc_R4 && c.operand is float f && f == Arc, "the spin arc");
            if (arc + 4 >= code.Count || code[arc + 1].opcode != OpCodes.Dup || code[arc + 2].opcode != OpCodes.Mul || code[arc + 4].opcode != OpCodes.Div)
                throw new Skip("the spin limit's code changed");
            code.InsertRange(arc + 5, [
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, Find.Field(typeof(VehicleProperties), nameof(VehicleProperties.Situation))),
                new CodeInstruction(OpCodes.Call, Find.Method(typeof(TumblingPhysics), nameof(SpinStep)))]);
            return code;
        }
        _ = Transpiler(null!);
        detail = default;
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
