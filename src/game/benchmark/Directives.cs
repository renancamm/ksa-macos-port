// The lineup's directives, as the director acts on them: what the game's console can't do. What each one takes and does
// is in their table (src/shared/benchmark/Directives.cs), which the lineup's parser checks every step against.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Brutal.Numerics;
using KSA;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

public partial class Director
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
                if (!Fast)
                    Opened(_window);
                // From this moment, so that a first frame that stalls counts
                if (Live)
                    _window.AddSim(0, ReadSim());
                return true;
            case "@shot" when a.Length == 2:
            {
                // How much it may change from run to run: the test's noise profile, or a flight's when its places were jumped
                // to (the clouds and the sea there depend on the way the camera came, which the places chosen change)
                if (_sections[_section] is var playing)
                    _sectionResult?.ShotLimits[a[1]] = _noise.GetValueOrDefault(playing.Name)?.ShotLimit(a[1], playing.Kind)
                                                       ?? NoiseProfile.DefaultShotLimit(playing.Kind);
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
            case "@sync" when a.Length == 1:
                Sync();
                return true;
            case "@reset" when a.Length == 1:
                Reset();
                return true;
            case "@key" when a.Length == 7:
                // The camera's flight (Path.cs) is laid out before the run; the step only marks the key's time
                return true;
            case "@launch" when a.Length >= 4:
                if (!Held)
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
            case "@kitten" when a.Length is 4 or 6 && double.TryParse(a[1], NumberStyles.Float, Inv, out var kittenLat)
                && double.TryParse(a[2], NumberStyles.Float, Inv, out var kittenLon):
            {
                double? orbit = null;
                if (a.Length == 6)
                {
                    if (a[5] != "orbit" || !TryValue(a[4], 0, 1, out var alt))
                        return false;
                    orbit = alt;
                }
                return PlaceKitten(kittenLat, kittenLon, a[3], orbit);
            }
            case "@face" when a.Length == 4 || a.Length == 6 && a[3] == "sun":
                return Face(a);
            case "@fly" when a.Length == 3:
                return Fly(a);
            case "@camera" when a.Length == 2:
                return SetCameraMode(a[1]);
            case "@front" when a.Length is 4 or 5 or 7:
                return StartFront(a);
            case "@chase" when a.Length is 4 or 5:
                return StartChase(a);
            case "@watch" when a.Length is >= 4 and <= 6:
                return StartWatch(a);
            case "@aim" when a.Length is 2 or 3:
                return StartAim(a);
            case "@fov" when a.Length == 2 && double.TryParse(a[1], NumberStyles.Float, Inv, out var fov) && fov > 0:
            {
                if (Program.GetHoveredCamera() is not { } camera)
                    return false;
                _fieldOfView ??= camera.GetFieldOfView();
                camera.SetFieldOfView((float)fov);
                return true;
            }
            case "@look" when a.Length is 3 or 4:
                return LookIva(a);
            case "@sun" when a.Length == 4:
                return Sun(a);
        }
        Log($"unknown directive '{step.Command}'");
        return false;
    }

    // @reset
    private void Reset()
    {
        CloseWindow();
        ReleaseCamera();
        EndRig();
        _vehicle = null;
        _exploded = null;
        _blown.Clear();
        NewWorld();
        _gameStart = _gameTarget = Universe.GetElapsedSeconds();
        _warpFrom = _warpTo = Live ? 0 : 1;
        _warpSeconds = 0;
        _speedSent = double.NaN;
        SetSpeed(0);
        _phase = Phase.NewWorld;
        _phaseFrames = 0;
    }

    // The world of a new game: its clock at the start, no vehicles, no kittens on missions
    public static void NewWorld()
    {
        JobSystems.WaitAll();
        var fresh = UniverseData.Create();
        fresh.GameTime = new UniverseTimeReference(UniverseTime.Zero);
        foreach (var system in fresh.CelestialSystems)
        {
            system.Vehicles.Clear();
            system.GroundClutter.Clear();
        }
        fresh.KittenRoster = KittenRosterData.Create();
        Universe.DeserializeSave(fresh);
        Program.OnGameLoaded();
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
    // makes it, without that taking control of it and the camera when @place or @front holds the camera.
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
        if (!Held)
        {
            Program.ControlledVehicle = vehicle;
            Program.MainViewport.MapCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
            Program.MainViewport.BaseCamera.SetFollow(vehicle, tidalLocking: false, changeControl: true, alert: false);
        }
        return true;
    }

    // @fly <alt> <speed>|orbit: the vehicle on the trajectory of a vertical climb through that point, the ground's
    // turning included, or on a circular orbit through it going east, through the game's own teleport
    // (InputEvents.TeleportInputData, as its debug window does)
    private bool Fly(string[] a)
    {
        double speed = 0;
        bool orbiting = a[2] == "orbit";
        if (Target is not { Parent: Celestial body } v || !TryValue(a[1], 0, 1, out var altitude) ||
            !orbiting && !double.TryParse(a[2], NumberStyles.Float, Inv, out speed))
            return false;
        var upCce = double3.Normalize(v.GetPositionEcl() - body.GetPositionEcl());
        if (FlightOver(body, upCce, altitude, orbiting ? null : speed) is not { } orbit)
            return false;
        InputEvents.TeleportInputBuffer.Add(new InputEvents.TeleportInputData { Vehicle = v, Orbit = orbit });
        return true;
    }

    // The trajectory through the point that high over the direction up (CCE): a vertical climb at that speed, the
    // ground's turning included, or with no speed a circular orbit going east (none over a pole)
    private static Orbit? FlightOver(Celestial body, double3 upCce, double altitude, double? climb)
    {
        var upCci = upCce.Transform(body.GetCci2Cce().Inverse());
        var positionCci = upCci * (body.MeanRadius + altitude);
        var spin = ((IParentBody)body).GetAngularVelocityCci();
        if (climb == null && double3.Cross(spin, upCci).Length() < 1e-9 * spin.Length())
            return null;
        var velocityCci = climb is { } speed
            ? double3.Cross(spin, positionCci) + upCci * speed
            : double3.Normalize(double3.Cross(spin, upCci)) * Math.Sqrt(((IParentBody)body).Mu / positionCci.Length());
        return KSA.Orbit.CreateFromStateCci(body, Universe.GetNextSimStep().NextTime, positionCci, velocityCci,
            new Random(1).RandomColor());
    }

    // @kitten: a kitten on EVA made as the game's EVA makes one (EVADoor.CreateKittenEva and SpawnEva: a backpack part
    // for its body, its roster entry moved to "EVA"), standing on the ground as @launch puts a vehicle there, or with
    // an altitude made on that orbit at once (its first frame is already there)
    private bool PlaceKitten(double lat, double lon, string name, double? orbitAltitude)
    {
        if (Universe.CurrentSystem is not { HomeBody: Celestial body } system)
            return false;
        if (system.All.TryGet(name, out Astronomical? there) && there is Vehicle placed)
        {
            Log($"@kitten: '{name}' is there already");
            _vehicle = placed;
            return true;
        }
        // The game keeps its part templates and a tree's resource managers to itself
        const BindingFlags Internal = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var parts = typeof(ModLibrary).GetField("AllParts", Internal)?.GetValue(null)
            as SerializedCollection<PartTemplate>;
        var recreate = typeof(PartTree).GetMethod("RecreateResourceManagers", Internal, Type.EmptyTypes);
        if (parts?.Find(KeyHash.Make("KittenBackPackPart")) is not { } backpack || recreate == null)
        {
            Log("@kitten: no backpack part to make a kitten with");
            return false;
        }
        var root = new Part(backpack.Id, backpack);
        var tree = root.CreateOwnTree();
        tree.ReinitializeDerivedValues();
        recreate.Invoke(tree, null);
        if (tree.ReconfigureTanks())
            recreate.Invoke(tree, null);
        tree.RefillConsumables();
        var kitten = Universe.KittenRoster.Find(name);
        if (kitten == null)
        {
            kitten = new KittenRosterEntryData(name, "HunterKitten");
            Universe.KittenRoster.Kittens.Add(kitten);
        }
        kitten.TransferToVehicle("EVA");
        // A kitten's parts have no geometry to stand it on: it is made on the ground with a metre's box, then the
        // game's own teleport (Vehicle.TeleportToLocation, from its debug window) stands it there by its body's box, a
        // frame later
        var box = new double3(0.5, 0.5, 0.5);
        var state = Vehicle.GetInitialKinematicStateForLocation(body, Universe.GetNextSimStep().NextTime, lat, lon,
            -box, box, double3.Zero, new Random(1).RandomColor());
        var orbit = state.Orbit;
        if (orbitAltitude is { } alt)
        {
            var upCce = body.GetDirCcfFromLatLon(lat, lon).Transform(body.GetCcf2Cce());
            if (FlightOver(body, upCce, alt, null) is not { } up)
                return false;
            orbit = up;
        }
        var eva = new KittenEva(system: system, characterId: kitten.Character, body2Cce: state.Body2Cce,
            bodyRates: state.BodyRates, parent: state.Orbit.Parent!, id: name, root: root, orbit: orbit);
        // SpawnEva also calls UpdateAfterPartTreeModification here (the constructor set the vehicle up before the
        // kitten's character was there: its distant glint). Left out: with it the kitten settles a little differently
        // every run (kitten view 2-8 % changed between runs, 0.1 % without)
        state.Orbit.Parent!.Children.Add(eva);
        eva.UpdatePerFrameData();
        if (orbitAltitude == null)
            eva.TeleportToLocation(body, lat, lon);
        _vehicle = eva;
        return true;
    }

    // @face <yaw> <pitch> <elev>|sun: the vehicle turned about the axis square to the seat's view and the one it should
    // look along, through the game's teleport (attitude only)
    private bool Face(string[] a)
    {
        double elevation = 0;
        bool atSun = a[3] == "sun";
        if (Target is not { Parent: Celestial body } v || !TryValue(a[1], 0, 1, out var yaw) ||
            !TryValue(a[2], 0, 1, out var pitch) || !atSun && !TryValue(a[3], 0, 1, out elevation))
            return false;
        var sun = Universe.CurrentSystem?.GetWorldSun();
        if (atSun && sun == null)
            return false;
        IVASeat? seat = null;
        foreach (var s in v.Parts.Modules.Get<IVASeat>())
        {
            if (s.AssignedKittenHash != KeyHash.Zero)
            {
                seat = s;
                break;
            }
        }
        if (seat == null)
        {
            Log("@face: no kitten seated");
            return false;
        }
        var (view, _) = SeatView(v, seat, new double2(yaw, pitch));
        var up = double3.Normalize(v.GetPositionEcl() - body.GetPositionEcl());
        var level = view - up * double3.Dot(view, up);
        if (level.Length() < 1e-6)
            return false;
        double e = elevation * Math.PI / 180;
        var wanted = atSun ? double3.Normalize(sun!.GetPositionEcl() - v.GetPositionEcl())
            : double3.Normalize(level) * Math.Cos(e) + up * Math.Sin(e);
        var turn = Turn(view, wanted);
        // With a second direction, where the celestial's center is from the seat: then turned about the sun's
        // direction until that one is there too
        if (a.Length == 6)
        {
            if (!TryValue(a[4], 0, 1, out var downYaw) || !TryValue(a[5], 0, 1, out var downPitch))
                return false;
            var (seen, _) = SeatView(v, seat, new double2(downYaw, downPitch));
            var down = double3.Transform(seen, turn);
            var across = down - wanted * double3.Dot(down, wanted);
            var target = -up - wanted * double3.Dot(-up, wanted);
            if (across.Length() > 1e-9 && target.Length() > 1e-9)
                turn = doubleQuat.Concatenate(turn, Turn(double3.Normalize(across), double3.Normalize(target)));
        }
        InputEvents.TeleportInputBuffer.Add(new InputEvents.TeleportInputData
        {
            Vehicle = v, Body2Cce = doubleQuat.Concatenate(v.Body2Cce, turn), BodyRates = double3.Zero
        });
        return true;
    }

    // The turn that takes the unit vector from onto the unit vector to, about the axis square to both
    private static doubleQuat Turn(double3 from, double3 to)
    {
        var axis = double3.Cross(from, to);
        if (axis.Length() < 1e-9)
            return doubleQuat.Identity;
        double angle = Math.Acos(Math.Clamp(double3.Dot(from, to), -1, 1));
        return doubleQuat.CreateFromAxisAngle(double3.Normalize(axis), angle);
    }
}
