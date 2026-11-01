using System.Collections.Generic;
using BepuPhysics;
using BepuUtilities;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Debris settling in warp, debris_settles_in_warp (port-settings.toml). Time warp stops at what the slowest physics
// bubble can do, and a pile of debris that hasn't come to rest costs about 9 ms a game second: about x70-100, whatever is
// asked, for the whole game, until the last piece of the last pile stops, which can take hours of game time. There are
// two reasons. The physics engine sleeps a pile after 255 quiet steps of all its pieces, and one that still jostles
// starts the count again. And a piece asleep on another piece touches no ground, so ConstraintSim makes it Freefall,
// which PhysicsBubble wakes at once in the air. While warp is x8 or more, a piece in the air over a body, not in water,
// sleeps below a higher energy (about 0.7 m/s) after 32 steps, and a piece asleep and that slow is Landed if what it
// touches reaches the ground. A piece is what PartFailure.IsExpendable means: nobody flies it and no crew is on it, which
// also covers spent stages and the parts of a breakup that the game doesn't mark as debris; with no engine firing.
// Pieces stay where they stopped after warp, and in saves. What then still holds warp back, with hundreds of pieces, is
// PhysicsBubble.ApplySurfaceMotion: each step of each one searches the terrain for where its orbit would hit it. A Landed
// piece doesn't, in warp: its flight plan is the one orbit, complete, until warp ends or it moves. And
// PhysicsBubble.ComputeBubbleUnconstrainedTime counts gravity for pairs lying still, so settled neighbours take steps of
// seconds: with every vehicle in a bubble Landed there is no pair to wait for. VehicleUpdateTask also predicts contact
// between neighbouring bubbles, splitting each frame's step at it: two bubbles of nothing but Landed vehicles have none.
internal static class DebrisSettle
{
    private const double MinWarp = 8;
    private const float GameEnergy = 0.005f, Energy = 0.5f;
    private const byte GameSteps = byte.MaxValue, Steps = 32;
    private static AccessTools.FieldRef<FlightPlan, bool> _planComplete = null!;
    private static AccessTools.FieldRef<PhysicsBubble, List<VehicleUpdateState>> _bubbleVehicles = null!;

    public static string Apply(Harmony harmony)
    {
        _planComplete = AccessTools.FieldRefAccess<FlightPlan, bool>(Find.Field(typeof(FlightPlan), "<IsComplete>k__BackingField"));
        _bubbleVehicles = AccessTools.FieldRefAccess<PhysicsBubble, List<VehicleUpdateState>>(Find.Field(typeof(PhysicsBubble), "_vehicleStates"));
        harmony.Patch(Find.Method(typeof(ConstraintSim), nameof(ConstraintSim.UpdateSimFromVehicle)),
            postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(SleepRule)));
        foreach (string method in new[] { nameof(ConstraintSim.UpdateVehicleFromSim), nameof(ConstraintSim.ReadBackFromSim) })
            harmony.Patch(Find.Method(typeof(ConstraintSim), method), postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(Rest)));
        harmony.Patch(Find.Method(typeof(PhysicsBubble), "ComputeBubbleUnconstrainedTime"),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(PairLimitPrefix)));
        harmony.Patch(Find.Method(typeof(VehicleUpdateTask), "HasValidClearance"),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(BubblesClear)));
        harmony.Patch(Find.Method(typeof(VehicleUpdateState), nameof(VehicleUpdateState.SetCurrentOrbit)),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(OrbitBegin)),
            postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(OrbitEnd)));
        return "on";
    }

    private static bool Applies(VehicleUpdateState vehicle)
    {
        var environment = vehicle.GetReadOnlyEnvironment();
        return Universe.GetSimulationSpeed() >= MinWarp && vehicle.Origin.BubFrame.IsCcf() && environment.InPhysicsRadius
            && environment.AtmosphericDensity > 0f && !vehicle.GetReadOnlyProps().Situation.HasOceanContact() && IsPiece(vehicle);
    }

    private static bool IsPiece(VehicleUpdateState vehicle)
    {
        if (vehicle.IsControlled || vehicle.IsKitten || vehicle.AnyActuatorActive())
            return false;
        var parts = vehicle.ReadOnlyVehicle;
        if (parts.IsControllable)
            return false;
        foreach (var seat in parts.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero)
                return false;
        return true;
    }

    private static void SleepRule(ConstraintSim __instance, VehicleUpdateState vehicleState)
    {
        if (vehicleState.IsKitten)
            return;
        var body = __instance.Simulation.Bodies[vehicleState.BepuHandles.Body];
        bool settles = Applies(vehicleState);
        body.Activity.SleepThreshold = settles ? Energy : GameEnergy;
        body.Activity.MinimumTimestepsUnderThreshold = settles ? Steps : GameSteps;
    }

    private static bool PairLimitPrefix(List<VehicleUpdateState> ____vehicleStates, ref double __result, out bool anyPairNearContact)
    {
        anyPairNearContact = false;
        if (Universe.GetSimulationSpeed() < MinWarp || !AllLanded(____vehicleStates))
            return true;
        __result = double.PositiveInfinity;
        return false;
    }

    private static bool BubblesClear(PhysicsBubble bubbleA, PhysicsBubble bubbleB, ref bool __result)
    {
        if (Universe.GetSimulationSpeed() < MinWarp || !AllLanded(_bubbleVehicles(bubbleA)) || !AllLanded(_bubbleVehicles(bubbleB)))
            return true;
        __result = true;
        return false;
    }

    private static bool AllLanded(List<VehicleUpdateState> vehicles)
    {
        foreach (var vehicle in vehicles)
            if (vehicle.GetReadOnlyProps().Situation != Situation.Landed || vehicle.UpdateData.NewFlightComputer.BurnMode == FlightComputerBurnMode.Auto)
                return false;
        return true;
    }

    private static void OrbitBegin(VehicleUpdateState __instance, ref bool computeFirstPatchTransition, out bool __state)
    {
        __state = __instance.GetReadOnlyProps().Situation == Situation.Landed && Universe.GetSimulationSpeed() >= MinWarp && IsPiece(__instance);
        if (__state)
            computeFirstPatchTransition = false;
    }

    private static void OrbitEnd(VehicleUpdateState __instance, bool __state)
    {
        if (__state)
            _planComplete(__instance.UpdateData.NewFlightPlan!) = true;
    }

    private static void Rest(ConstraintSim __instance, VehicleUpdateState vehicleState)
    {
        ref var props = ref vehicleState.GetNewProps();
        if (props.Situation != Situation.Freefall || vehicleState.Kinematic.SleepEnergy > Energy || !Applies(vehicleState))
            return;
        var body = __instance.Simulation.Bodies[vehicleState.BepuHandles.Body];
        if (!body.Awake && ReachesGround(__instance.Simulation, vehicleState.BepuHandles.Body))
            props.Situation = Situation.Landed;
    }

    // Walks the contacts from a body: a constraint of one body holds it to something static, the ground. Two pieces
    // asleep at the top of a bounce touch each other and nothing else.
    private static bool ReachesGround(Simulation simulation, BodyHandle start)
    {
        var seen = new HashSet<int> { start.Value };
        var next = new Stack<BodyHandle>();
        next.Push(start);
        while (next.Count > 0)
        {
            ref var constraints = ref simulation.Bodies[next.Pop()].Constraints;
            for (int i = 0; i < constraints.Count; i++)
            {
                var handle = constraints[i].ConnectingConstraintHandle;
                var location = simulation.Solver.HandleToConstraint[handle.Value];
                if (simulation.Solver.TypeProcessors[location.TypeId].BodiesPerConstraint == 1)
                    return true;
                var bodies = new Connected { Simulation = simulation, Set = location.SetIndex, Seen = seen, Next = next };
                simulation.Solver.EnumerateConnectedRawBodyReferences(handle, ref bodies);
            }
        }
        return false;
    }

    // A constraint of the awake set names its bodies by index, one of a sleeping set by handle
    private struct Connected : IForEach<int>
    {
        public Simulation Simulation;
        public int Set;
        public HashSet<int> Seen;
        public Stack<BodyHandle> Next;

        public void LoopBody(int reference)
        {
            int raw = reference & Bodies.BodyReferenceMask;
            var handle = Set == 0 ? Simulation.Bodies.ActiveSet.IndexToHandle[raw] : new BodyHandle(raw);
            if (Seen.Add(handle.Value))
                Next.Push(handle);
        }
    }
}
