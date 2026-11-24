using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuUtilities;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Debris settling in warp, debris_settles_in_warp (port-settings.toml). Time warp stops at what the slowest physics
// bubble can do, and pieces nobody flies decide that more than anything else: a pile that hasn't come to rest, about
// x70-100 whatever is asked, for the whole game; a pair of them coasting in orbit, stalls of tens of seconds. A piece
// is what PartFailure.IsExpendable means: nobody flies it and no crew is on it, which also covers spent stages and the
// parts of a breakup that the game doesn't mark as debris; with no engine firing. Pieces stay where they stopped after
// warp, and in saves. The rules, from what each one was measured to hold back: Settling, x8 or more. The physics engine
// sleeps a pile after 255 quiet steps of all its pieces, and one that still jostles starts the count again. And a piece
// asleep on another piece touches no ground, so ConstraintSim makes it Freefall, which PhysicsBubble wakes at once in
// the air. So a piece over a body, not in water, sleeps below a higher energy (about 0.7 m/s) after 32 steps, and a
// piece asleep and that slow is Landed if what it touches reaches the ground. Touching, x64 or more, where a frame's
// step is about a second or more: 6 game seconds after its first touch, a piece touching the ground is Landed and
// asleep, unless a camera follows it or a vehicle somebody flies touches it. Fragments rolling to a stop on Luna held
// warp at x3 for a minute (ConstraintSim.UploadGuardQuads, the craters' height for each moving terrain patch). At sea,
// x64 or more: at its first touch of the water, a piece is Floating, at rest on the surface, or Bottomed on the seabed
// if it is denser than the water, the water is too shallow to float it or it already touches the bottom. The game
// floats a vehicle only once it has stopped on the water, and in long steps a piece never does: it skims at hundreds of
// m/s, dives under within a step, buoyancy spins it up and its bubble runs in 1 ms steps, a frame of physics taking
// seconds; a dense piece sinks on through the seabed. Even 6 game seconds in the water, as on the ground, are 6000 such
// steps. Settled pieces: PhysicsBubble.ApplySurfaceMotion searches the terrain for where each one's orbit would hit it,
// each step. A piece at rest on a surface doesn't, in warp: its flight plan is the one orbit, complete, until warp ends
// or it moves. Pairs in a bubble: ComputeBubbleUnconstrainedTime holds it to 1/60 s for two vehicles side by side, on
// rails too, even when a frame's step is an hour. Pieces on rails and Landed vehicles have no pair to wait for, and
// from x1024 nor have any vehicles on rails, such as a kitten out beside its ship. Pairs of bubbles: VehicleUpdateTask
// predicts contact over each frame's step, splitting it at the contact or merging them. With long steps every crossing
// of orbits counts, the merged bubble is wider and reaches more, and one frame ends with hundreds of vehicles over 800
// km in one bubble, in 1/60 s steps. In warp a piece meets only what is near it now, and from 2 s steps, too long to
// dock in, so does everything else. Falling pieces, x8 or more: the game steps a vehicle in flight for 1-2 s whatever
// its height, and a piece falling at 20-50 m/s passes the ground within one step, then slides on under it in 1 ms
// contact steps for tens of game seconds. A piece in flight steps at most a quarter of its clearance (its height over
// the ground or sea, in the water over the seabed, less its size) at its speed, a limit never under 1/60 s. A bubble
// whose constraint simulation runs is left alone: it steps against the terrain in 1/60 s anyway. Sunk pieces: one that
// still passes the ground at speed sails on inside the planet at the spin cap and in 1 ms steps, the game hanging for
// as long as it lasts. Below the lowest terrain, it is removed, at any speed.
[Hook(Phase.Default, Switch = nameof(Settings.DebrisSettlesInWarp))]
internal static class DebrisSettle
{
    internal const double MinWarp = 8;
    private const double TouchWarp = 64, RailsPairWarp = 1024, TouchSeconds = 6, TouchGap = 2;
    private const float GameEnergy = 0.005f, Energy = 0.5f;
    private const byte GameSteps = byte.MaxValue, Steps = 32;
    private static AccessTools.FieldRef<FlightPlan, bool> _planComplete = null!;
    private const double NearDistance = 5000, VehicleNearDistance = 50000, LongStep = 2, SunkDepth = 100;
    private const double MaxDynamicPressure = 200000;  // DetectStructuralFailure's limit
    private const double GroundShare = 0.25, ShortestGroundStep = 1.0 / 60;
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
        harmony.Patch(Find.Method(typeof(PhysicsBubble), "FullPhysicsStepUntil"), transpiler: new HarmonyMethod(typeof(DebrisSettle), nameof(ToAtSea)));
        // Without it falling pieces take the game's steps, as before the row
        Hooks.Part("Debris Settles in Warp's ground step", () => harmony.Patch(Find.Method(typeof(PhysicsBubble), "FullPhysicsStepUntil"),
            transpiler: new HarmonyMethod(typeof(DebrisSettle), nameof(ToGroundStep))));
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

    internal static bool IsPiece(VehicleUpdateState vehicle)
    {
        if (vehicle.IsControlled || vehicle.IsKitten || vehicle.AnyActuatorActive())
            return false;
        var parts = vehicle.ReadOnlyVehicle;
        if (parts.HasControlModule)  // as PartFailure.IsExpendable: since KSA 5901, IsControllable is false for an empty pod or an unpowered probe
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
        __state = __instance.GetReadOnlyProps().Situation.IsAsleepOnSurface() && Universe.GetSimulationSpeed() >= MinWarp && IsPiece(__instance);
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
        if (vehicle is KittenEva || vehicle.HasControlModule || vehicle == Program.ControlledVehicle)
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
            // Asleep, as the game has a Floating vehicle: an awake body would keep the bubble in contact steps of 1/60
            // s for all of a long frame, and ReadBackFromSim would take the piece off rails again
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

    // The step a vehicle in flight is given, by the game or by Lighter Tumbling Physics' PieceStep, goes through
    // GroundStep(dt, vehicle, bubble, ref detail): inserted after the call, so the two rows' transpilers find it in either
    // order
    private static IEnumerable<CodeInstruction> ToGroundStep(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var step = Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.UpdateSituationAndComputeTimestep));
        var tumbling = AccessTools.Method(typeof(TumblingPhysics), "PieceStep");
        int call = Find.Once(code, c => c.Calls(step) || tumbling != null && c.Calls(tumbling), "UpdateSituationAndComputeTimestep's call");
        int detail = code.FindLastIndex(call, c => c.opcode == OpCodes.Ldloca_S || c.opcode == OpCodes.Ldloca
            ? c.operand is LocalBuilder local && local.LocalType == typeof(PhysicsStepDetail) : false);
        if (detail < 0)
            throw new Skip("the step's detail not found");
        code.InsertRange(call + 1, [
            CodeInstruction.LoadLocal(Il.StepVehicle(code, call)),
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(code[detail].opcode, code[detail].operand),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DebrisSettle), nameof(GroundStep)))]);
        return code;
    }

    // A piece in flight steps at most a quarter of its clearance at its speed, a limit never under 1/60 s; in the water
    // its clearance is over the seabed. A bubble whose constraint simulation runs steps against the terrain in 1/60 s
    // anyway
    private static double GroundStep(double dt, VehicleUpdateState vehicle, PhysicsBubble bubble, ref PhysicsStepDetail detail)
    {
        if (!(dt > ShortestGroundStep) || Universe.GetSimulationSpeed() < MinWarp || (bubble.ConstraintSim?.IsAnyConstrained ?? false))
            return dt;
        var self = vehicle.GetNewStates();
        if (self.Props.Situation is not (Situation.Maneuvering or Situation.Sailing) || !self.Environment.InPhysicsRadius)
            return dt;
        double surface = self.Environment.OceanVolume > 0 ? self.Environment.TerrainRadius
            : Math.Max(self.Environment.TerrainRadius, self.Environment.OceanRadius);
        double clearance = self.GetPositionClosestParentBub(self.GetPositionBub()).Length() - surface - self.Props.BoundingSphereRadiusBody;
        // A piece already below the ground's height keeps the floor: the ground's contact can still catch it
        double cap = Math.Max(ShortestGroundStep, GroundShare * clearance / (self.GetVelocityBub().Length() + 1));
        if (!(cap < dt) || !IsPiece(vehicle))
            return dt;
        detail.Timestep = cap;
        detail.Limit = PhysicsStepLimit.CollisionPrediction;
        return cap;
    }

    // The game's try to float a vehicle on rails becomes AtSea(..., vehicle, bubble)
    private static IEnumerable<CodeInstruction> ToAtSea(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var floats = Find.Method(typeof(PhysicsStates), nameof(PhysicsStates.CanInstantlyFloatOnRails));
        int call = Find.Once(code, c => c.Calls(floats), "CanInstantlyFloatOnRails's call");
        Il.SwapCall(code, call, AccessTools.Method(typeof(DebrisSettle), nameof(AtSea)), CodeInstruction.LoadLocal(Il.StepVehicle(code, call)),
            new CodeInstruction(OpCodes.Ldarg_0));
        return code;
    }

    // True puts the vehicle on rails and stops it, which makes one only in the water Floating, one also on the seabed
    // Bottomed
    private static bool AtSea(ref PhysicsStates self, VehicleUpdateState vehicle, PhysicsBubble bubble)
    {
        if (self.CanInstantlyFloatOnRails())
            return true;
        if (Universe.GetSimulationSpeed() < TouchWarp || !self.Props.Situation.HasOceanContact() || !self.Environment.InPhysicsRadius
            || self.Environment.OceanDensity <= 0 || !vehicle.Origin.BubFrame.IsCcf() || !IsPiece(vehicle) || Followed(vehicle.ReadOnlyVehicle))
            return false;
        // In the water, as the game measures it: the ocean contact of a long step reaches as far as the step's travel
        var position = self.GetPositionBub();
        var up = position.NormalizeOrZero();
        var geometry = double3.Unpack(self.Props.MassToGeometryAsmb).Transform(self.Kinematic.Body2Phys);
        float radius = self.Props.ComputeBoundingSphereRadiusAsmb();
        if (self.Environment.OceanRadius - ((position + geometry).Length() - radius) <= 0)
            return false;
        // Sleeping a body sleeps all it touches, as on the ground
        if (bubble.ConstraintSim is { } sim && !OnlyPiecesTouch(sim, vehicle.BepuHandles.Body))
            return false;
        float density = self.Props.TotalVolume > 0 ? self.Props.TotalMass / self.Props.TotalVolume : float.PositiveInfinity;
        float draft = 2 * radius * density / self.Environment.OceanDensity;
        if (self.Props.Situation == Situation.Sailing)
        {
            // Where it rests, its geometry's bottom at the draft or on the seabed: UpdateActivity takes the draft from
            // where it is, which the game moves a Floating vehicle to only when it wakes
            bool floats = density < self.Environment.OceanDensity && self.Environment.OceanRadius - draft > self.Environment.TerrainRadius;
            double bottom = floats ? self.Environment.OceanRadius - draft : self.Environment.TerrainRadius;
            if (floats)
                self.Props.Draft = draft;
            else
                self.Props.Situation = Situation.Dragging;
            self.Kinematic.PositionPhys = (bottom - double3.Dot(up, geometry) + radius) * up - self.Origin.PositionBub;
        }
        // Asleep, as Rest leaves a piece on the ground
        if (bubble.ConstraintSim is { } constraints)
        {
            var body = constraints.Simulation.Bodies[vehicle.BepuHandles.Body];
            body.Velocity = default;
            body.Awake = false;
        }
        return true;
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

    // The stop isn't an impact: it would read as one in the step's measured acceleration, and the game's g-load check
    // exempts only debris. The dynamic pressure is the loads' own: a piece that hits the water hard still breaks up
    private static bool StopIsNoFailure(VehicleUpdateState vehicleState) => !(Universe.GetSimulationSpeed() >= TouchWarp
        && vehicleState.GetReadOnlyProps().Situation.IsAsleepOnSurface() && IsPiece(vehicleState))
        || vehicleState.PeakDynamicPressure >= MaxDynamicPressure;

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
