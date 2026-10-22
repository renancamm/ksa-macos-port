// Scene directives: what the console cannot do.
//
//   @measure <name> <seconds>    measure the next seconds as one part of the scene
//   @shot <name>                 save the next presented image as <name>.png
//   @gpuprof <name> <frames>     profile the next frames with KSA Metal's GPU profiler (needs KSA_MTL_GPUPROF=<csv>)
//   @end                         write the results and quit
//   @launch <save> at <site>     put a saved vehicle (Documents/.../Vehicles) on a launch site of the nearby celestial
//                                (e.g. CCSFS LC-39A), as the launch menu does; it becomes the controlled vehicle and
//                                the camera follows it. @launch <lat> <lon> <save> puts it anywhere on the surface.
//   @stage [n]                   activate the controlled vehicle's next stage n times (the staging key)
//   @input <action> press|release  send a key action to the controlled vehicle, e.g. MainEngineStartup (ignite) or
//                                MainEngineThrottleUp (held until release)
//   @explode                     destroy the controlled vehicle the way a crash does
//   @eva                         send the first kitten aboard the controlled vehicle out on EVA (the crew panel's
//                                EVA button); the kitten becomes the controlled vehicle and the camera follows it
//   @helmet on|off               the helmet of the kitten on EVA (off shows its fur, as in the cockpit)
//   @iva                         switch the main view to the cockpit (IVA) camera
//   @spin <x> <y> <z>            set the controlled vehicle's rotation rates (rad/s), stabilization off
//   @lookat <lat> <lon>          aim the camera at that surface point of the followed celestial
//   @height <m>                  put the camera that high above the followed celestial's sea level (the console's
//                                altitude counts from the terrain, which under the sea is its floor)
//   @sunlit <sun el> <alt m> <km> <bearing>  fly the camera over the followed celestial where the sun stands that
//                                high in the west, that many metres up, looking at the point that far away that way
//   @orbit [<az> <el> <dist>]    place the orbit camera around what it follows: azimuth and elevation in degrees, the
//                                game's distance power (larger is farther); "-" keeps a value; without arguments,
//                                log where it is
//   @rotate <degrees/s>          turn the orbit camera around what it follows, every frame (0 stops it)
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
                CloseWindow();
                _window = new Window(a[1], now + seconds) { Vehicle = FollowedVehicle() };
                return true;
            case "@gpuprof" when a.Length == 3 && uint.TryParse(a[2], out var frames):
                return DriverStats.ProfileFrames($"{_scene}/{a[1]}", frames);
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
            case "@eva":
            {
                // The crew panel's EVA button, for the first seat with a kitten in it
                var v = Program.ControlledVehicle;
                if (v == null)
                    return false;
                foreach (var seat in v.Parts.Modules.Get<IVASeat>())
                {
                    if (seat.AssignedKittenHash == KeyHash.Zero)
                        continue;
                    // A fresh game's roster has random kittens and not the save's own: seat Banjo, always the same
                    if (Universe.KittenRoster.Find(seat.AssignedKittenHash) == null)
                    {
                        var banjo = Universe.KittenRoster.Find("Banjo");
                        if (banjo == null)
                        {
                            banjo = new KittenRosterEntryData("Banjo", "BanjoKitten");
                            Universe.KittenRoster.Kittens.Add(banjo);
                        }
                        seat.AssignedKittenHash = KeyHash.Make("Banjo");
                    }
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
            case "@sunlit" when a.Length == 5:
                return Sunlit(a);
            case "@height" when a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, Inv, out var height):
            {
                // The console's altitude counts from the terrain, which under the sea is its floor
                var camera = Program.GetHoveredCamera();
                if (camera?.Following is not Celestial body)
                    return false;
                var up = body.GetPositionCce(camera).Normalized();
                camera.SetPosition(body.GetPositionEcl() + up * (body.MeanRadius + height));
                return true;
            }
            case "@orbit":
            {
                var view = Program.MainViewport.OrbitController.View;
                if (view == null)
                    return false;
                if (a.Length == 1)
                {
                    Log(FormattableString.Invariant(
                        $"@orbit: azimuth {view.Azimuth * 180 / Math.PI:F1}, elevation {view.Elevation * 180 / Math.PI:F1}, distance {view.DistancePower:F2}"));
                    return true;
                }
                if (a.Length != 4)
                    return false;
                // "-" keeps a value
                double Arg(int i, double current, double scale) =>
                    a[i] == "-" ? current : double.Parse(a[i], NumberStyles.Float, Inv) * scale;
                view.Azimuth = Arg(1, view.Azimuth, Math.PI / 180);
                view.Elevation = Arg(2, view.Elevation, Math.PI / 180);
                view.DistancePower = Arg(3, view.DistancePower, 1);
                return true;
            }
            case "@rotate" when a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, Inv, out var degrees):
                _rotateRadPerSecond = degrees * Math.PI / 180;
                return true;
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

    // Above the point of the followed celestial where the sun stands <elevation> degrees high in the west (its local
    // afternoon, wherever the system's clock has put it), <altitude> metres up, looking at the surface point
    // <distance> km away towards <bearing> (degrees from north). Each move waits for the frame after the one before.
    private bool Sunlit(string[] a)
    {
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var elevation) || !double.TryParse(a[2], NumberStyles.Float, Inv, out var altitude) ||
            !double.TryParse(a[3], NumberStyles.Float, Inv, out var distanceKm) || !double.TryParse(a[4], NumberStyles.Float, Inv, out var bearing))
            return false;
        var sun = Universe.CurrentSystem?.GetWorldSun();
        if (Program.GetHoveredCamera()?.Following is not Celestial body || sun == null)
        {
            Log("@sunlit: the camera does not follow a celestial");
            return false;
        }
        var sunCcf = (sun.GetPositionEcl() - body.GetPositionEcl()).Normalized().Transform(body.GetCce2Ccf());
        double lat = Math.Asin(Math.Clamp(sunCcf.Z, -1, 1)) * 180 / Math.PI;
        double lon = Math.Atan2(sunCcf.Y, sunCcf.X) * 180 / Math.PI + (90 - elevation);
        double step = distanceKm / body.MeanRadius * 1000 * 180 / Math.PI;
        double b = bearing * Math.PI / 180;
        double lookLat = lat + Math.Cos(b) * step;
        double lookLon = lon + Math.Sin(b) * step / Math.Max(Math.Cos(lat * Math.PI / 180), 0.01);
        // goto needs the camera near the celestial: come down first (far out, the orbit camera may be millions of km away)
        _deferred.Enqueue("camera Free");
        _deferred.Enqueue(FormattableString.Invariant($"altitude {altitude:F0} m"));
        _deferred.Enqueue(FormattableString.Invariant($"goto {lat:F5} {lon:F5}"));
        _deferred.Enqueue(FormattableString.Invariant($"altitude {altitude:F0} m"));
        _deferred.Enqueue(FormattableString.Invariant($"@lookat {lookLat:F5} {lookLon:F5}"));
        Log(FormattableString.Invariant($"@sunlit: {body.Id} at {lat:F2} {lon:F2}, looking at {lookLat:F2} {lookLon:F2}"));
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

    // @launch <save> at <site>: a launch site of the nearby celestial (Astronomicals.xml), as the launch menu does;
    // @launch <lat> <lon> <save>: anywhere on it
    private static bool Launch(string[] a)
    {
        var body = Program.GetNearbyCelestial();
        if (body == null)
            return false;
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
