using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks.Patches;

// Debris settling in warp, debris_settles_in_warp (port-settings.toml). Time warp stops at what the slowest physics
// bubble can do, and pieces nobody flies decide that more than anything else: a pile that hasn't come to rest, about x70-100
// whatever is asked, for the whole game; a pair of them coasting in orbit, stalls of tens of seconds. A piece is what
// PartFailure.IsExpendable means: nobody flies it and no crew is on it, which also covers spent stages and the parts of a
// breakup that the game doesn't mark as debris; with no engine firing. Pieces stay where they stopped after warp, and in
// saves. The rules, from what each one was measured to hold back:
// Settling, x8 or more. The physics engine sleeps a pile after 255 quiet steps of all its pieces, and one that still jostles
// starts the count again. And a piece asleep on another piece touches no ground, so ConstraintSim makes it Freefall, which
// PhysicsBubble wakes at once in the air. So a piece over a body, not in water, sleeps below a higher energy (about 0.7 m/s)
// after 32 steps, and a piece asleep and that slow is Landed if what it touches reaches the ground.
// Touching, x64 or more, where a frame's step is about a second or more: 6 game seconds after its first touch, a piece
// touching the ground is Landed and asleep, unless a camera follows it or a vehicle somebody flies touches it. Fragments rolling to a stop on Luna held warp at x3 for a minute
// (ConstraintSim.UploadGuardQuads, the craters' height for each moving terrain patch).
// Settled pieces: PhysicsBubble.ApplySurfaceMotion searches the terrain for where each one's orbit would hit it, each step.
// A Landed piece doesn't, in warp: its flight plan is the one orbit, complete, until warp ends or it moves.
// Pairs in a bubble: ComputeBubbleUnconstrainedTime holds it to 1/60 s for two vehicles side by side, on rails too, even when
// a frame's step is an hour. Pieces on rails and Landed vehicles have no pair to wait for, and from x1024 nor have any
// vehicles on rails, such as a kitten out beside its ship.
// Pairs of bubbles: VehicleUpdateTask predicts contact over each frame's step, splitting it at the contact or merging them.
// With long steps every crossing of orbits counts, the merged bubble is wider and reaches more, and one frame ends with
// hundreds of vehicles over 800 km in one bubble, in 1/60 s steps. In warp a piece meets only what is near it now, and from
// 2 s steps, too long to dock in, so does everything else.
// Sunk pieces: one that passes the ground at speed sails on inside the planet at the spin cap and in 1 ms steps, the game
// hanging for as long as it lasts. Below the lowest terrain, it is removed, at any speed.
internal static class DebrisSettle
{
    private const double MinWarp = 8, TouchWarp = 64, RailsPairWarp = 1024, TouchSeconds = 6, TouchGap = 2;
    private const float GameEnergy = 0.005f, Energy = 0.5f;
    private const byte GameSteps = byte.MaxValue, Steps = 32;
    private static AccessTools.FieldRef<FlightPlan, bool> _planComplete = null!;
    private const double NearDistance = 5000, VehicleNearDistance = 50000, LongStep = 2, SunkDepth = 100;
    private static readonly List<Vehicle> _sunk = new();
    private static AccessTools.FieldRef<PhysicsBubble, List<VehicleUpdateState>> _bubbleVehicles = null!;
    private static AccessTools.FieldRef<VehicleUpdateTask, SimStep> _simStep = null!;

    public static string Apply(Harmony harmony)
    {
        _planComplete = AccessTools.FieldRefAccess<FlightPlan, bool>(Find.Field(typeof(FlightPlan), "<IsComplete>k__BackingField"));
        _bubbleVehicles = AccessTools.FieldRefAccess<PhysicsBubble, List<VehicleUpdateState>>(Find.Field(typeof(PhysicsBubble), "_vehicleStates"));
        _simStep = AccessTools.FieldRefAccess<VehicleUpdateTask, SimStep>(Find.Field(typeof(VehicleUpdateTask), "_simStep"));
        harmony.Patch(Find.Method(typeof(Universe), nameof(Universe.ApplyVehicleSolvers)), postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(RemoveSunk)));
        harmony.Patch(Find.Method(typeof(ConstraintSim), nameof(ConstraintSim.UpdateSimFromVehicle)),
            postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(SleepRule)));
        foreach (string method in new[] { nameof(ConstraintSim.UpdateVehicleFromSim), nameof(ConstraintSim.ReadBackFromSim) })
            harmony.Patch(Find.Method(typeof(ConstraintSim), method), postfix: new HarmonyMethod(typeof(DebrisSettle), nameof(Rest)));
        harmony.Patch(Find.Method(typeof(PhysicsBubble), "DetectStructuralFailure"),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(StopIsNoFailure)));
        harmony.Patch(Find.Method(typeof(PhysicsBubble), "ComputeBubbleUnconstrainedTime"),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(PairLimitPrefix)));
        harmony.Patch(Find.Method(typeof(VehicleUpdateTask), "MergeBubbles"),
            prefix: new HarmonyMethod(typeof(DebrisSettle), nameof(ForgetKinds)));
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
            && !vehicle.GetReadOnlyProps().Situation.HasOceanContact() && IsPiece(vehicle);
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
        if (Universe.GetSimulationSpeed() < MinWarp || !AllOnRails(____vehicleStates))
            return true;
        __result = double.PositiveInfinity;
        return false;
    }

    private static bool BubblesClear(VehicleUpdateTask __instance, PhysicsBubble bubbleA, PhysicsBubble bubbleB, ref bool __result)
    {
        if (Universe.GetSimulationSpeed() < MinWarp)
            return true;
        double dt = _simStep(__instance).DeltaTime;
        var kindA = KindOf(bubbleA);
        var kindB = KindOf(bubbleB);
        bool clear = kindA == Kind.Piece && kindB == Kind.Piece || kindA == Kind.Landed && kindB == Kind.Landed
            || (kindA == Kind.Piece || kindB == Kind.Piece ? !Near(bubbleA, bubbleB, Math.Min(2 * dt, 1), NearDistance)
                : dt >= LongStep && !Near(bubbleA, bubbleB, 2 * dt, VehicleNearDistance));
        if (clear)
            __result = true;
        return !clear;
    }

    private static bool Near(PhysicsBubble a, PhysicsBubble b, double lead, double distance)
    {
        var stateA = Reach(a);
        var stateB = Reach(b);
        double closing = (stateA.VelocityCci - stateB.VelocityCci).Length() + stateA.VelocitySpread + stateB.VelocitySpread;
        // A wide bubble counts for little: its width is what would pull in everything around it
        double widths = Math.Min(stateA.EnvelopeRadius, NearDistance) + Math.Min(stateB.EnvelopeRadius, NearDistance);
        return (stateA.PositionCci - stateB.PositionCci).Length() < widths + distance + Math.Min(closing * lead, distance);
    }

    private static BubbleMergePredicate.BubbleState Reach(PhysicsBubble bubble)
    {
        if (!_reach.TryGetValue(bubble, out var state))
            _reach[bubble] = state = bubble.ComputeMergeState();
        return state;
    }

    // What a bubble is, once a frame by its id: MergeBubbles asks for every pair of bubbles, hundreds of thousands
    private enum Kind { Piece, Landed, Other }

    private static int[] _kinds = new int[1024];
    private static int _frame;
    private static readonly Dictionary<PhysicsBubble, BubbleMergePredicate.BubbleState> _reach = new();

    private static void ForgetKinds()
    {
        _frame++;
        _reach.Clear();
    }

    private static Kind KindOf(PhysicsBubble bubble)
    {
        int id = bubble.BubbleId;
        if (id >= _kinds.Length)
            Array.Resize(ref _kinds, Math.Max(id + 1, 2 * _kinds.Length));
        int known = _kinds[id];
        if (known >> 2 == _frame)
            return (Kind)(known & 3);
        var kind = FindKind(_bubbleVehicles(bubble));
        _kinds[id] = _frame << 2 | (int)kind;
        return kind;
    }

    private static Kind FindKind(List<VehicleUpdateState> vehicles)
    {
        bool pieces = true, landed = true;
        foreach (var vehicle in vehicles)
        {
            if (vehicle.UpdateData.NewFlightComputer.BurnMode == FlightComputerBurnMode.Auto)
                return Kind.Other;
            pieces &= IsPiece(vehicle);
            landed &= vehicle.GetReadOnlyProps().Situation == Situation.Landed;
        }
        return pieces ? Kind.Piece : landed ? Kind.Landed : Kind.Other;
    }

    private static bool AllOnRails(List<VehicleUpdateState> vehicles)
    {
        bool anyVehicle = Universe.GetSimulationSpeed() >= RailsPairWarp;
        foreach (var vehicle in vehicles)
        {
            var situation = vehicle.GetReadOnlyProps().Situation;
            if (!situation.IsOnRails() || vehicle.UpdateData.NewFlightComputer.BurnMode == FlightComputerBurnMode.Auto
                || situation != Situation.Landed && !anyVehicle && !IsPiece(vehicle))
                return false;
        }
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

    private static void RemoveSunk()
    {
        if (Universe.CurrentSystem is not { } system)
            return;
        _sunk.Clear();
        foreach (var body in system.All.AsSpan())
            if (body is Vehicle { IsDisposed: false } vehicle && vehicle.Parent is { } parent
                && vehicle.GetPositionCci().Length() < parent.MinTerrainRadius - vehicle.BoundingSphereRadiusBody - SunkDepth && IsLoose(vehicle))
                _sunk.Add(vehicle);
        foreach (var vehicle in _sunk)
            Universe.DestroyVehicle(vehicle);
    }

    private static bool IsLoose(Vehicle vehicle)
    {
        if (vehicle is KittenEva || vehicle.IsControllable || vehicle == Program.ControlledVehicle)
            return false;
        foreach (var seat in vehicle.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero)
                return false;
        return true;
    }

    private static void Rest(ConstraintSim __instance, VehicleUpdateState vehicleState)
    {
        ref var props = ref vehicleState.GetNewProps();
        if (Universe.GetSimulationSpeed() >= TouchWarp && !props.Situation.IsOnRails() && __instance.BodyHasFreshTerrainContact.Contains(vehicleState.BepuHandles.Body.Value)
            && Applies(vehicleState) && !Followed(vehicleState.ReadOnlyVehicle) && TouchedLongEnough(vehicleState) && OnlyPiecesTouch(__instance, vehicleState.BepuHandles.Body))
        {
            props.Situation = Situation.Landed;
            vehicleState.Kinematic.VelocityPhys = double3.Zero;
            vehicleState.Kinematic.AngularVelocityPhys = double3.Zero;
            // Asleep, as the game has a Floating vehicle: an awake body would keep the bubble in contact steps of 1/60 s for
            // all of a long frame, and ReadBackFromSim would take the piece off rails again
            var touching = __instance.Simulation.Bodies[vehicleState.BepuHandles.Body];
            touching.Velocity = default;
            touching.Awake = false;
            return;
        }
        if (props.Situation != Situation.Freefall || vehicleState.Kinematic.SleepEnergy > Energy || !Applies(vehicleState))
            return;
        var body = __instance.Simulation.Bodies[vehicleState.BepuHandles.Body];
        if (!body.Awake && ReachesGround(__instance.Simulation, vehicleState.BepuHandles.Body))
            props.Situation = Situation.Landed;
    }

    // Long enough to fall over: stopped at the first touch, whole rockets stay standing on their nozzles. A touch after a
    // bounce or a flight starts the time again
    private sealed class Touch { public bool Started; public UniverseTime First, Last; }

    private static readonly ConditionalWeakTable<VehicleUpdateState, Touch> _touches = new();

    private static bool TouchedLongEnough(VehicleUpdateState vehicle)
    {
        var now = vehicle.Origin.Time;
        var touch = _touches.GetOrCreateValue(vehicle);
        if (!touch.Started || (now - touch.Last).Seconds() > Math.Max(TouchGap, Universe.GetSimulationSpeed() / 10))
            touch.First = now;
        touch.Started = true;
        touch.Last = now;
        return (now - touch.First).Seconds() >= TouchSeconds;
    }

    // The stop isn't an impact: it would read as one in the step's measured acceleration, and the game exempts only debris
    private static bool StopIsNoFailure(VehicleUpdateState vehicleState) => !(Universe.GetSimulationSpeed() >= TouchWarp
        && vehicleState.GetReadOnlyProps().Situation == Situation.Landed && IsPiece(vehicleState));

    private static bool Followed(Vehicle vehicle)
    {
        foreach (var view in ViewportRegistry.GameViews)
            if (view.BaseCamera.Following == vehicle || view.MapCamera.Following == vehicle)
                return true;
        return false;
    }

    // Walks the contacts from a body: a constraint of one body holds it to something static, the ground. Two pieces
    // asleep at the top of a bounce touch each other and nothing else.
    private static bool ReachesGround(Simulation simulation, BodyHandle start)
    {
        bool ground = false;
        Walk(simulation, start, _ => { }, ref ground);
        return ground;
    }

    // Sleeping a body sleeps all it touches: never a vehicle somebody flies or a camera follows
    private static bool OnlyPiecesTouch(ConstraintSim sim, BodyHandle start)
    {
        bool only = true, ground = false;
        Walk(sim.Simulation, start, handle => only &= !sim.HandleToState.TryGetValue(handle, out var vehicle) || IsPiece(vehicle) && !Followed(vehicle.ReadOnlyVehicle), ref ground);
        return only;
    }

    private static void Walk(Simulation simulation, BodyHandle start, Action<BodyHandle> visit, ref bool ground)
    {
        var seen = new HashSet<int> { start.Value };
        var next = new Stack<BodyHandle>();
        next.Push(start);
        while (next.Count > 0)
        {
            var at = next.Pop();
            visit(at);
            ref var constraints = ref simulation.Bodies[at].Constraints;
            for (int i = 0; i < constraints.Count; i++)
            {
                var handle = constraints[i].ConnectingConstraintHandle;
                var location = simulation.Solver.HandleToConstraint[handle.Value];
                if (simulation.Solver.TypeProcessors[location.TypeId].BodiesPerConstraint == 1)
                {
                    ground = true;
                    continue;
                }
                var bodies = new Connected { Simulation = simulation, Set = location.SetIndex, Seen = seen, Next = next };
                simulation.Solver.EnumerateConnectedRawBodyReferences(handle, ref bodies);
            }
        }
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
