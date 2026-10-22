// Scene directives: what the console cannot do.
//
//   @measure <name> <seconds>    measure the next seconds as one part of the scene
//   @shot <name>                 save the next presented image as <name>.png
//   @end                         write the results and quit
//   @launch <lat> <lon> <save>   put a saved vehicle (Documents/.../Vehicles) on the surface of the nearby celestial,
//                                as the launch menu does; it becomes the controlled vehicle and the camera follows it
//   @stage [n]                   activate the controlled vehicle's next stage n times (the staging key)
//   @input <action> press|release  send a key action to the controlled vehicle, e.g. MainEngineStartup (ignite) or
//                                MainEngineThrottleUp (held until release)
//   @explode                     destroy the controlled vehicle the way a crash does
//   @iva                         switch the main view to the cockpit (IVA) camera
//   @spin <x> <y> <z>            set the controlled vehicle's rotation rates (rad/s), stabilization off
//   @lookat <lat> <lon>          aim the camera at that surface point of the followed celestial
using System;
using System.Globalization;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private bool Directive(string command, double now)
    {
        var a = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (a[0])
        {
            case "@measure" when a.Length == 3 && double.TryParse(a[2], NumberStyles.Float, Inv, out var seconds):
                if (_window != null)
                    _results!.Windows.Add(_window.Summarize());
                _window = new Window(a[1], now + seconds);
                return true;
            case "@shot" when a.Length == 2:
                return DriverStats.Screenshot(System.IO.Path.Combine(_outDir, $"{_scene}_{a[1]}.png"));
            case "@end":
                End(now);
                return true;
            case "@launch" when a.Length >= 4:
                return Launch(a);
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
            case "@iva":
                if (Program.MainViewport is not GameViewport viewport)
                    return false;
                viewport.SetCameraMode(CameraMode.IVA);
                return true;
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
            case "@lookat" when a.Length == 3:
                return LookAt(a);
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

    private static bool LookAt(string[] a)
    {
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var lat) || !double.TryParse(a[2], NumberStyles.Float, Inv, out var lon))
            return false;
        var camera = Program.GetHoveredCamera();
        if (camera?.Following is not Celestial body)
        {
            Log("@lookat: the camera does not follow a celestial");
            return false;
        }
        var center = body.GetPositionEcl();
        var dir = double3.Normalize(body.GetPositionEclFromLatLon(lat, lon) - center);
        var target = body.GetSurfacePositionEclFromDirCce(dir);
        camera.LookAt(target, double3.Normalize(camera.PositionEcl - center));
        // The free camera re-applies its cached offset every frame: take the new rotation as that offset
        if (Program.MainViewport is GameViewport viewport)
            viewport.FlyController.CacheOffset();
        return true;
    }

    private static bool Launch(string[] a)
    {
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var lat) || !double.TryParse(a[2], NumberStyles.Float, Inv, out var lon))
            return false;
        var name = string.Join(' ', a[3..]);
        var body = Program.GetNearbyCelestial();
        // The game keeps its vehicle saves in a private property
        var saves = typeof(VehicleSaves).GetProperty("Saves", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null) as LookupCollection<VehicleSave>;
        if (body == null || saves == null || !saves.TryGet(name, out var save) || save == null)
        {
            Log($"@launch: no nearby celestial or no vehicle save '{name}'");
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
            System = Universe.CurrentSystem, Parent = state.Orbit.Parent!, Id = name, Body2Cce = state.Body2Cce,
            BodyRates = state.BodyRates, Orbit = state.Orbit, Root = tree.Root
        });
        return true;
    }
}
