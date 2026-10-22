// Script directives: what the console cannot do.
//
// Timing and measuring
//   @measure <name> <seconds>    measure the next seconds as one part of the section
//   @shot <name>                 checkpoint: catch up with the script's game time, pause, save <section>_<name>.png
//   @warp <speed> [seconds]      run the simulation at that speed (1 is real time, 0 pauses it), moving to it over
//                                the seconds
//   @gpuprof <name> <frames>     profile the next frames with KSA Metal's GPU profiler (needs KSA_MTL_GPUPROF=<csv>)
//
// Vehicles and kittens
//   @launch <save> at <pad>      put a saved vehicle (Documents/.../Vehicles) on a launch pad of the celestial the camera
//                                follows (e.g. CCSFS LC-39A), as the launch menu does; it becomes the controlled vehicle
//                                and the camera follows it. @launch <lat> <lon> <save> puts it anywhere on the surface.
//   @crew                        seat the benchmark's kitten in the controlled vehicle (always the same one, so its
//                                fur is)
//   @stage [n]                   activate the controlled vehicle's next stage n times (the staging key)
//   @input <action> press|release  send a key action to the controlled vehicle, e.g. MainEngineStartup (ignite),
//                                MainEngineThrottleUp (held until release) or TranslateForward (a kitten walks)
//   @explode                     destroy the controlled vehicle the way a crash does
//   @eva                         send the first kitten aboard the controlled vehicle out on EVA (the crew panel's
//                                EVA button); the kitten becomes the controlled vehicle and the camera follows it
//   @helmet on|off               the helmet of the kitten on EVA (off shows its fur, as in the cockpit)
//   @spin <x> <y> <z>            set the controlled vehicle's rotation rates (rad/s), stabilization off
//
// Camera (see Camera.cs and Path.cs). Moves take [<seconds>] on the script's clock and ease in and out; a value "-"
// keeps the current one and "~<x>" adds x to it.
//   @key <celestial> <lat> <lon> <alt> <heading> <pitch>  a place of the free camera's flight over and between
//                                celestials (Path.cs)
//   @camera orbit|free|iva       the main view's camera mode
//   @orbit <az> <el> <dist> [s]  the orbit camera around what it follows: azimuth and elevation in degrees (negative
//                                elevation is above), the game's distance power (larger is farther)
//   @rotate <degrees/s>          turn the orbit camera around what it follows (0 stops it)
//   @look <yaw> <pitch> [s]      turn the cockpit camera from its seat's forward view, in degrees
//   @sun <celestial> <lat> <lon> log where the sun stands over that point (to plan the light of a key)
using System;
using System.Globalization;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private bool Directive(string command)
    {
        var a = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (a[0])
        {
            case "@measure" when a.Length == 3 && double.TryParse(a[2], NumberStyles.Float, Inv, out var seconds):
                CloseWindow();
                _window = new Window(a[1], _scriptTime + seconds) { Vehicle = FollowedVehicle() };
                return true;
            case "@shot" when a.Length == 2:
                StartCheckpoint(a[1]);
                return true;
            case "@warp" when a.Length is 2 or 3 && double.TryParse(a[1], NumberStyles.Float, Inv, out var speed) && speed >= 0:
            {
                double seconds = 0;
                if (a.Length == 3 && !double.TryParse(a[2], NumberStyles.Float, Inv, out seconds))
                    return false;
                Warp(speed, seconds);
                return true;
            }
            case "@key" when a.Length == 7:
                // The camera's flight (Path.cs) is laid out before the run; the step only marks the key's time
                return true;
            case "@gpuprof" when a.Length == 3 && uint.TryParse(a[2], out var frames):
                return DriverStats.ProfileFrames($"{_sectionResult?.Name}/{a[1]}", frames);
            case "@launch" when a.Length >= 4:
                ReleaseCamera();
                return Launch(a);
            case "@crew":
                return Crew();
            case "@stage":
            {
                var v = Program.ControlledVehicle;
                if (v == null)
                    return false;
                int n = a.Length >= 2 && int.TryParse(a[1], out var count) ? count : 1;
                for (int i = 0; i < n; i++)
                    v.Parts.SequenceList.ActivateNextSequence(v);
                return true;
            }
            case "@input" when a.Length == 3 && Enum.TryParse<InputAction>(a[1], out var action):
            {
                var v = Program.ControlledVehicle;
                if (v == null)
                    return false;
                var keyAction = a[2] == "release" ? Brutal.GlfwApi.GlfwKeyAction.Release : Brutal.GlfwApi.GlfwKeyAction.Press;
                v.ProcessInput(action, keyAction, default);
                return true;
            }
            case "@explode":
            {
                var v = Program.ControlledVehicle;
                if (v == null)
                    return false;
                InputEvents.VehicleExplodeBuffer.Add(new InputEvents.VehicleExplodeData { Vehicle = v });
                return true;
            }
            case "@eva":
            {
                // The crew panel's EVA button, for the first seat with a kitten in it
                var v = Program.ControlledVehicle;
                if (v == null)
                    return false;
                foreach (var seat in v.Parts.Modules.Get<IVASeat>())
                {
                    if (seat.AssignedKittenHash != KeyHash.Zero)
                        return EVADoor.PerformEvaForSeat(v, seat);
                }
                Log("@eva: no kitten aboard");
                return false;
            }
            case "@helmet" when a.Length == 2:
            {
                // Seated kittens wear no helmet (KittenRenderable); this does the same for one on EVA
                if (Program.ControlledVehicle is not KittenEva kitten)
                    return false;
                const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
                var renderable = typeof(KittenEva).GetField("_renderable", Private)?.GetValue(kitten);
                if (renderable?.GetType().GetField("_characterAvatar", Private)?.GetValue(renderable) is not CharacterAvatar avatar)
                    return false;
                avatar.Attachments.Helmet.Enabled = a[1] == "on";
                return true;
            }
            case "@spin" when a.Length == 4:
            {
                var v = Program.ControlledVehicle;
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
            case "@look" when a.Length is 3 or 4:
                return LookIva(a);
            case "@sun" when a.Length == 4:
                return Sun(a);
        }
        Log($"unknown directive '{command}'");
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

    // Seats the benchmark's kitten: Banjo's looks under its own name (the game has a Banjo of its own out on EVA, and
    // a new game's roster has random kittens)
    private static bool Crew()
    {
        var v = Program.ControlledVehicle;
        if (v == null)
            return false;
        var kitten = Universe.KittenRoster.Find("Benchy");
        if (kitten == null)
        {
            kitten = new KittenRosterEntryData("Benchy", "BanjoKitten");
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

    // @launch <save> at <pad>: a launch pad of the celestial the camera follows (its landmarks in Astronomicals.xml), as
    // the launch menu does; @launch <lat> <lon> <save>: anywhere on it
    private static bool Launch(string[] a)
    {
        var body = Program.GetHoveredCamera()?.Following as Celestial ?? Program.GetNearbyCelestial();
        if (body == null)
        {
            Log("@launch: the camera is near no celestial");
            return false;
        }
        string name;
        double lat, lon;
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
        InputEvents.CreateVehicleBuffer.Add(new InputEvents.CreateVehicleData
        {
            System = Universe.CurrentSystem!, Parent = state.Orbit.Parent!, Id = name, Body2Cce = state.Body2Cce,
            BodyRates = state.BodyRates, Orbit = state.Orbit, Root = tree.Root
        });
        return true;
    }
}
