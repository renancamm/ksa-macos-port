// Script directives: what the console cannot do.
//
// Timing and measuring
//   @measure <name> <seconds>    measure the next seconds as one part of the section
//   @shot <name> [<limit>]       checkpoint: catch up with the script's game time, pause, save <section>_<name>.png
//                                (fast mode also measures the view there, as the part <name>). The limit is how much of
//                                it may change from run to run (percent of its blocks, see Images.cs) for the comparison
//                                of two runs' screenshots, for views that physics or particles change every time
//   @warp <speed> [seconds]      run the simulation at that speed (1 is real time, 0 pauses it), moving to it over
//                                the seconds
//   @time <seconds>              move the game's clock forward to that many seconds since the start (the clouds drift
//                                with it), so a section looks the same whether the sections before it played or not
//   @sync                        a checkpoint without a screenshot: the simulation eases to a stop, the game's clock
//                                catches up with the script's, and the steps after it act at the same game time on
//                                every Mac (a kitten walking through the slow frames walks differently, though)
//   @gpuprof <name> <frames>     profile the next frames with KSA Metal's GPU profiler (needs KSA_MTL_GPUPROF=<csv>)
//
// Vehicles and kittens
// The vehicle steps act on the vehicle chosen last (@vehicle, @control, @launch, @eva), or the controlled one. None of
// them takes control of a vehicle or moves the camera, except as said.
//   @launch <save> at <pad>      put a saved vehicle (Documents/.../Vehicles) on a launch pad of the home celestial
//                                (e.g. CCSFS LC-39A), as the launch menu does; it becomes the controlled vehicle and the
//                                camera follows it, unless @place put the camera somewhere. @launch <lat> <lon> <save>
//                                puts it anywhere on the surface. With "as <id>" at the end the vehicle is called that
//                                (two of one save). A vehicle that is there already stays as it is.
//   @vehicle <id>                choose that vehicle for the vehicle steps, or the vehicle camera: a vehicle @explode
//                                blew up too (the camera then aims at the place where it blew up)
//   @control <id>                make that vehicle the controlled one, the camera following it (at once, unlike the
//                                console's control)
//   @crew [name]                 seat a kitten of the benchmark's in the vehicle (always with Hunter's looks, so its
//                                fur is the same)
//   @stage [n]                   activate the vehicle's next stage n times (the staging key)
//   @input <action> press|release  send a key action to the vehicle, e.g. MainEngineStartup (ignite) or
//                                MainEngineThrottleUp (held until release; the throttle rises by 0.7 a second of the
//                                Mac's clock, not the game's, to full)
//   @explode                     destroy the vehicle the way a crash does
//   @eva                         send the first kitten aboard the vehicle out on EVA (the crew panel's EVA button); the
//                                kitten becomes the vehicle and the controlled one, and the camera follows it, unless
//                                @place put the camera somewhere
//   @helmet on|off               the helmet of the kitten on EVA (off shows its fur, as in the cockpit)
//   @spin <x> <y> <z>            set the vehicle's rotation rates (rad/s), stabilization off
//   @fly <alt> <speed>           put the vehicle that high above the sea ("km" for kilometres), straight over where it
//                                stands, climbing at that speed (m/s over the ground), as if it had flown up there: a
//                                rocket high in the sky without the minutes of its climb. It keeps its attitude; the
//                                game moves it a frame later
//
// Camera (see Camera.cs and Path.cs). Moves take [<seconds>] on the script's clock and ease in and out; a value "-"
// keeps the current one and "~<x>" adds x to it.
//   @key <celestial> <lat> <lon> <alt> <heading> <pitch>  a place of the free camera's flight over and between
//                                celestials (Path.cs)
//   @place <celestial> <lat> <lon> <alt> <heading> <pitch>  put the free camera at that place at once (as a key has it)
//                                and keep it there while vehicles are placed, started and blown up, until a camera step
//                                moves it
//   @camera orbit|free|iva       the main view's camera mode
//   @orbit <az> <el> <dist> [s]  the orbit camera around what it follows: azimuth and elevation in degrees (negative
//                                elevation is above), the game's distance power (larger is farther)
//   @rotate <degrees/s>          turn the orbit camera or the chase view around what it follows (0 stops it)
//   @chase <az> <el> <dist> [s]  the vehicle camera (Rig.cs) going along with the vehicle: from the direction
//                                az (degrees from north), el degrees above its horizon, dist metres away
//   @watch <az> <dist> <height> [<frame>] [s]  the vehicle camera standing on the ground dist metres from the vehicle
//                                towards az, height metres above the ground; with frame, it zooms so that the view is
//                                that many metres tall at the vehicle
//   @aim <metres> [s]            aim the vehicle camera that far above the vehicle's center
//   @look <yaw> <pitch> [s]      turn the cockpit camera from its seat's forward view, in degrees
//   @sun <celestial> <lat> <lon> log where the sun stands over that point (to plan the light of a key)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The vehicle the vehicle steps act on (null: the controlled one), and the vehicles @explode blew up, with the
    // place where each one blew up in its celestial's fixed frame
    private Vehicle? _vehicle;
    private readonly Dictionary<string, (Vehicle Vehicle, Celestial Body, double3 PlaceCcf)> _blown = new();

    private Vehicle? Target => _vehicle ?? Program.ControlledVehicle;

    private bool Directive(Step step)
    {
        var a = step.Words;
        switch (a[0])
        {
            case "@measure" when a.Length == 3 && double.TryParse(a[2], NumberStyles.Float, Inv, out var seconds):
                CloseWindow();
                _window = new Window(a[1], _scriptTime + seconds) { Vehicle = FollowedVehicle() };
                // Fast mode measures the views standing still instead
                if (!_fast)
                    Opened(_window);
                return true;
            case "@shot" when a.Length is 2 or 3:
            {
                if (a.Length == 3)
                {
                    if (!double.TryParse(a[2], NumberStyles.Float, Inv, out var limit))
                        return false;
                    _sectionResult?.ShotLimits[a[1]] = limit;
                }
                // The game stays paused: nothing to keep in step
                if (Paused && !IsChosen(a[1]))
                    return true;
                StartCheckpoint(a[1]);
                return true;
            }
            case "@warp" when a.Length is 2 or 3 && double.TryParse(a[1], NumberStyles.Float, Inv, out var speed) && speed >= 0:
            {
                double seconds = 0;
                if (a.Length == 3 && !double.TryParse(a[2], NumberStyles.Float, Inv, out seconds))
                    return false;
                Warp(speed, seconds);
                return true;
            }
            case "@time" when a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, Inv, out var time):
                return SkipTo(time);
            case "@sync" when a.Length == 1:
                Sync();
                return true;
            case "@key" when a.Length == 7:
                // The camera's flight (Path.cs) is laid out before the run; the step only marks the key's time
                return true;
            case "@gpuprof" when a.Length == 3 && uint.TryParse(a[2], out var frames):
                return DriverStats.ProfileFrames($"{_sectionResult?.Name}/{a[1]}", frames);
            case "@launch" when a.Length >= 4:
                if (_placed == null)
                    ReleaseCamera();
                return Launch(a);
            case "@vehicle" when a.Length == 2:
            {
                if (_blown.TryGetValue(a[1], out var blown))
                {
                    _vehicle = blown.Vehicle;
                    if (_rig is { } r && r.Target != blown.Vehicle)
                        EndRig();
                    return true;
                }
                if (Universe.CurrentSystem?.Get(a[1]) is not Vehicle v)
                    return false;
                _vehicle = v;
                // The vehicle camera goes to it with the next camera step
                if (_rig is { } rig && rig.Target != v)
                    EndRig();
                return true;
            }
            case "@place" when a.Length == 7:
            {
                if (ParseKey(_scriptTime, a, true) is not { } key)
                    return false;
                SetCameraMode("free");
                Place(key);
                _lastUp = null;
                UpdateCamera(0);
                return true;
            }
            case "@control" when a.Length == 2:
            {
                // The console's control waits for the next frame; this switches at once, so the next steps act on it
                if (Universe.CurrentSystem?.Get(a[1]) is not Vehicle v)
                    return false;
                ReleaseCamera();
                _vehicle = v;
                Program.ControlledVehicle = v;
                Program.MainViewport.MapCamera.SetFollow(v, tidalLocking: false, changeControl: false, alert: false);
                Program.MainViewport.BaseCamera.SetFollow(v, tidalLocking: false, changeControl: false, alert: false);
                return true;
            }
            case "@crew" when a.Length <= 2:
                return Crew(Target, a.Length == 2 ? a[1] : "Benchy");
            case "@stage":
            {
                var v = Target;
                if (v == null)
                    return false;
                int n = a.Length >= 2 && int.TryParse(a[1], out var count) ? count : 1;
                for (int i = 0; i < n; i++)
                    v.Parts.SequenceList.ActivateNextSequence(v);
                return true;
            }
            case "@input" when a.Length == 3 && Enum.TryParse<InputAction>(a[1], out var action):
            {
                var v = Target;
                if (v == null)
                    return false;
                var keyAction = a[2] == "release" ? Brutal.GlfwApi.GlfwKeyAction.Release : Brutal.GlfwApi.GlfwKeyAction.Press;
                v.ProcessInput(action, keyAction, default);
                return true;
            }
            case "@explode":
            {
                var v = Target;
                if (v == null)
                    return false;
                // The game leaves out a vehicle's explosions for 5 s of the Mac's clock after a teleport (so that it
                // doesn't burst on the ground it was put on): a rocket @fly put in the air blows up whole
                const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
                if (typeof(Vehicle).GetField("_lastTeleportTime", Private) is { } teleported)
                    teleported.SetValue(v, double.NegativeInfinity);
                else
                    Log("@explode: the vehicle has no teleport time to clear");
                InputEvents.VehicleExplodeBuffer.Add(new InputEvents.VehicleExplodeData { Vehicle = v });
                _exploded = v.Id;
                if (v.Parent is Celestial body)
                    _blown[v.Id] = (v, body, ToCcf(body, v.GetPositionEcl()));
                return true;
            }
            case "@eva":
            {
                // The crew panel's EVA button, for the first seat with a kitten in it
                var v = Target;
                if (v == null)
                    return false;
                foreach (var seat in v.Parts.Modules.Get<IVASeat>())
                {
                    if (seat.AssignedKittenHash == KeyHash.Zero)
                        continue;
                    if (_placed != null)
                        return EvaInPlace(v, seat);
                    // The game makes the kitten the controlled vehicle a frame later: the steps act on it from then
                    _vehicle = null;
                    return EVADoor.PerformEvaForSeat(v, seat);
                }
                Log("@eva: no kitten aboard");
                return false;
            }
            case "@helmet" when a.Length == 2:
            {
                // Seated kittens wear no helmet (KittenRenderable); this does the same for one on EVA
                if (Target is not KittenEva kitten)
                    return false;
                const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
                var renderable = typeof(KittenEva).GetField("_renderable", Private)?.GetValue(kitten);
                if (renderable?.GetType().GetField("_characterAvatar", Private)?.GetValue(renderable) is not CharacterAvatar avatar)
                    return false;
                avatar.Attachments.Helmet.Enabled = a[1] == "on";
                return true;
            }
            case "@fly" when a.Length == 3:
                return Fly(a);
            case "@spin" when a.Length == 4:
            {
                var v = Target;
                if (v == null || !TryParse3(a, out var rates))
                    return false;
                v.SetStabilization(false);
                // Teleport also sets the vehicle's own rates; the physics state alone is overwritten from them
                v.Teleport(null, null, rates);
                return true;
            }
            case "@camera" when a.Length == 2:
                return SetCameraMode(a[1]);
            case "@orbit" when a.Length is 4 or 5:
                return Orbit(a);
            case "@rotate" when a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, Inv, out var degrees):
                _rotateRadPerSecond = degrees * Math.PI / 180;
                return true;
            case "@chase" when a.Length is 4 or 5:
                return StartChase(a);
            case "@watch" when a.Length is >= 4 and <= 6:
                return StartWatch(a);
            case "@aim" when a.Length is 2 or 3:
                return StartAim(a);
            case "@look" when a.Length is 3 or 4:
                return LookIva(a);
            case "@sun" when a.Length == 4:
                return Sun(a);
        }
        Log($"unknown directive '{step.Command}'");
        return false;
    }

    private static bool TryParse3(string[] a, out double3 v)
    {
        v = default;
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var x) || !double.TryParse(a[2], NumberStyles.Float, Inv, out var y) ||
            !double.TryParse(a[3], NumberStyles.Float, Inv, out var z))
            return false;
        v = new double3(x, y, z);
        return true;
    }

    // Seats one of the benchmark's kittens: Hunter's looks under a name of its own (a new game's roster has random
    // kittens)
    private static bool Crew(Vehicle? v, string name)
    {
        if (v == null)
            return false;
        var kitten = Universe.KittenRoster.Find(name);
        if (kitten == null)
        {
            kitten = new KittenRosterEntryData(name, "HunterKitten");
            Universe.KittenRoster.Kittens.Add(kitten);
        }
        if (!v.AddCrewToFirstAvailableSeat(kitten.NameHash))
        {
            Log($"@crew: no free seat in '{v.Id}'");
            return false;
        }
        kitten.StartMission(v.Id);
        return true;
    }

    // @launch <save> at <pad>: a launch pad of the home celestial (its landmarks in Astronomicals.xml), as the launch menu
    // does; @launch <lat> <lon> <save>: anywhere on it. The vehicle is made here as the game's own CreateVehicleData
    // makes it, without that taking control of it and the camera when @place holds the camera.
    private bool Launch(string[] a)
    {
        if (Universe.CurrentSystem?.HomeBody is not Celestial body)
        {
            Log("@launch: no home celestial");
            return false;
        }
        string name;
        double lat, lon;
        string? id = null;
        int asAt = Array.IndexOf(a, "as");
        if (asAt > 1 && asAt == a.Length - 2)
        {
            id = a[^1];
            a = a[..asAt];
        }
        int at = Array.IndexOf(a, "at");
        if (at > 1)
        {
            name = string.Join(' ', a[1..at]);
            var siteId = string.Join(' ', a[(at + 1)..]);
            var site = body.GetLocation(siteId);
            if (site == null)
            {
                Log($"@launch: '{body.Id}' has no site '{siteId}'");
                return false;
            }
            lat = site.Latitude.ToDegrees();
            lon = site.Longitude.ToDegrees();
        }
        else if (double.TryParse(a[1], NumberStyles.Float, Inv, out lat) && double.TryParse(a[2], NumberStyles.Float, Inv, out lon))
            name = string.Join(' ', a[3..]);
        else
            return false;
        id ??= name;
        if (Universe.CurrentSystem.All.TryGet(id, out Astronomical? placed) && placed is Vehicle there)
        {
            Log($"@launch: '{id}' is there already");
            _vehicle = there;
            return true;
        }
        // The game keeps its vehicle saves in a private property
        var saves = typeof(VehicleSaves).GetProperty("Saves", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as LookupCollection<VehicleSave>;
        if (saves == null || !saves.TryGet(name, out var save) || save == null)
        {
            Log($"@launch: no vehicle save '{name}'");
            return false;
        }
        var tree = save.Load(Program.MainViewport);
        if (tree == null)
            return false;
        var mass = tree.ComputeInertMassPropertiesAsmb() + tree.ComputePropellantMassPropertiesAsmb();
        var (min, max) = tree.ComputeBoundingBoxAsmb();
        var state = Vehicle.GetInitialKinematicStateForLocation(body, Universe.GetNextSimStep().NextTime, lat, lon, min, max,
            double3.Unpack(in mass.Offset), new Random(1).RandomColor());
        var system = Universe.CurrentSystem!;
        var vehicle = Vehicle.CreateVehicle(system, state.Body2Cce, state.BodyRates, system.HomeBody, id, tree.Root,
            state.Orbit);
        state.Orbit.Parent!.Children.Add(vehicle);
        vehicle.UpdatePerFrameData();
        _vehicle = vehicle;
        if (_placed == null)
        {
            Program.ControlledVehicle = vehicle;
            Program.MainViewport.MapCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
            Program.MainViewport.BaseCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
        }
        return true;
    }

    // @fly <alt> <speed>: the vehicle on the trajectory of a vertical climb through that point, the ground's turning
    // included, through the game's own teleport (InputEvents.TeleportInputData, as its debug window does)
    private bool Fly(string[] a)
    {
        if (Target is not { Parent: Celestial body } v || !TryValue(a[1], 0, 1, out var altitude) ||
            !double.TryParse(a[2], NumberStyles.Float, Inv, out var speed))
            return false;
        var upCce = double3.Normalize(v.GetPositionEcl() - body.GetPositionEcl());
        var upCci = upCce.Transform(body.GetCci2Cce().Inverse());
        var positionCci = upCci * (body.MeanRadius + altitude);
        var velocityCci = double3.Cross(((IParentBody)body).GetAngularVelocityCci(), positionCci) + upCci * speed;
        var orbit = KSA.Orbit.CreateFromStateCci(body, Universe.GetNextSimStep().NextTime, positionCci, velocityCci,
            new Random(1).RandomColor());
        InputEvents.TeleportInputBuffer.Add(new InputEvents.TeleportInputData { Vehicle = v, Orbit = orbit });
        return true;
    }

    // @eva with the camera held by @place: the game's EVA (EVADoor.PerformEvaForSeat), with the map's camera to follow
    // the kitten in place of the main view's, so the camera stays where it is. The game makes the kitten a frame later,
    // where it applies the crew panel's buttons, and makes it the controlled vehicle: the steps act on it from then.
    private bool EvaInPlace(Vehicle vehicle, IVASeat seat)
    {
        if (Universe.KittenRoster.Find(seat.AssignedKittenHash) is not { } kitten)
            return false;
        vehicle.Parts.EnsureDerived(DerivedData.EvaSeats);
        EVADoor? door = null;
        foreach (var d in vehicle.Parts.EVADoors.Modules)
        {
            door ??= d;
            if (d.AlignedSeat == seat)
            {
                door = d;
                break;
            }
        }
        if (door == null)
        {
            Log("@eva: no door");
            return false;
        }
        InputEvents.EvaSpawnBuffer.Add(new InputEvents.EvaSpawnData
        {
            Door = door, Vehicle = vehicle, Seat = seat, RosterEntry = kitten, Camera = Program.MainViewport.MapCamera
        });
        _vehicle = null;
        return true;
    }
}
