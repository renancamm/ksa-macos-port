// The camera directives for vehicles and the cockpit: moves on the script's clock, so a move ends where the script
// says on every Mac, whatever its frame rate, and holds still while a checkpoint stops the clock. The free camera's
// flights over and between celestials are in Path.cs.
using System;
using System.Globalization;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    // Azimuth, elevation (radians) and distance power of the orbit camera, from and to; NaN leaves one alone
    private record OrbitMove(double3 From, double3 To, double Start, double Seconds);

    // Yaw and pitch (degrees) of the cockpit camera from its seat's forward view
    private record LookMove(double2 From, double2 To, double Start, double Seconds);

    private double _rotateRadPerSecond;
    private OrbitMove? _orbitMove;
    private LookMove? _lookMove;
    private double2? _look;

    private void UpdateCamera(double dt)
    {
        var view = Program.MainViewport.OrbitController.View;
        if (_orbitMove is { } m && view != null)
        {
            double u = Ease(m.Start, m.Seconds);
            double3 v = m.From + (m.To - m.From) * u;
            if (!double.IsNaN(v.X))
                view.Azimuth = v.X;
            if (!double.IsNaN(v.Y))
                view.Elevation = v.Y;
            if (!double.IsNaN(v.Z))
                view.DistancePower = v.Z;
            if (u >= 1)
                _orbitMove = null;
        }
        if (_rotateRadPerSecond != 0 && view != null)
            view.Azimuth += _rotateRadPerSecond * dt;
        // A later flight takes the camera from @place too (@place releases the flight it was on)
        if (UpdatePath())
        {
            EndRig();
            Unhold();
        }
        // @place's camera stays where it is, whatever else turns to the vehicles meanwhile
        if (_placed is { } placed)
            Apply(placed.Body, Resolve(placed.Body, placed.C, placed.Floor));
        if (_front is { } front)
            FrontView(front);
        UpdateRig(dt);
        if (_lookMove is { } l)
        {
            double u = Ease(l.Start, l.Seconds);
            _look = l.From + (l.To - l.From) * u;
            if (u >= 1)
                _lookMove = null;
        }
        if (_look is { } look)
            Look(look);
    }

    // @front <axis> <dist> <up> [<aim>]: the free camera dist metres out along one of the vehicle's own axes (x, y or
    // z, "-" for the other way), looking back at it, with another of its axes up in the view (a kitten's head is -z,
    // its face +x), aim metres along that axis from its center: the vehicle upright in the view whatever the horizon
    // does, as in space. It holds the camera as @place does. With <from> <seconds> it comes in from that far out over
    // that many seconds of the game's clock (the distance by the same factor every second)
    private record Front(Vehicle Vehicle, double3 AxisBody, double3 UpBody, double Distance, double Aim, double From,
        double GameStart, double GameSeconds);
    private Front? _front;

    private static double3? BodyAxis(string s) =>
        (s.StartsWith('-') ? -1 : 1) * s.TrimStart('-') switch
        {
            "x" => new double3(1, 0, 0), "y" => new double3(0, 1, 0), "z" => new double3(0, 0, 1), _ => (double3?)null
        };

    private bool StartFront(string[] a)
    {
        double aim = 0, from = 0, seconds = 0;
        if (Target is not { Parent: Celestial } v || BodyAxis(a[1]) is not { } axis || BodyAxis(a[3]) is not { } up ||
            Math.Abs(double3.Dot(axis, up)) > 0.5 || !TryValue(a[2], 0, 1, out var dist) ||
            a.Length >= 5 && !TryValue(a[4], 0, 1, out aim) ||
            a.Length == 7 && (!TryValue(a[5], 0, 1, out from) || !TryValue(a[6], 0, 1, out seconds)))
            return false;
        if (a.Length != 7)
            from = dist;
        ReleaseCamera();
        _look = null;
        _lookMove = null;
        _lastUp = null;
        _front = new Front(v, axis, up, dist, aim, from, Universe.GetElapsedSeconds(), seconds);
        Hold();
        UpdateCamera(0);
        return true;
    }

    private static void FrontView(Front f)
    {
        if (f.Vehicle.IsDisposed || f.Vehicle.Parent is not Celestial body)
            return;
        var axis = double3.Normalize(f.AxisBody.Transform(f.Vehicle.Body2Cce));
        var up = double3.Normalize(f.UpBody.Transform(f.Vehicle.Body2Cce));
        var aim = f.Vehicle.GetPositionEcl() + up * f.Aim;
        // The zoom goes by the game's clock, which runs on while the script's waits for it: smooth however fast the Mac
        double u = f.GameSeconds > 0
            ? Smooth(Math.Clamp((Universe.GetElapsedSeconds() - f.GameStart) / f.GameSeconds, 0, 1)) : 1;
        var position = aim + axis * Math.Exp(Math.Log(f.From) + (Math.Log(f.Distance) - Math.Log(f.From)) * u);
        Apply(body, (position, aim - position, up), RigClearance, f.Vehicle);
    }

    // How far along a move is (0 to 1), eased in and out
    private double Ease(double start, double seconds)
    {
        double u = seconds > 0 ? Math.Clamp((_scriptTime - start) / seconds, 0, 1) : 1;
        return u * u * (3 - 2 * u);
    }

    // Turns the cockpit camera from its seat's forward view (the game keeps it within the seat's half of the cabin)
    private static void Look(double2 look)
    {
        if (Program.MainViewport is not GameViewport viewport || viewport.Mode != CameraMode.IVA ||
            Program.GetHoveredCamera() is not { Following: Vehicle vehicle } camera || viewport.IvaController.Seat is not { } seat)
            return;
        var (direction, up) = SeatView(vehicle, seat, look);
        camera.LocalRotation = Camera.LookAtRotation(direction, up);
    }

    // A direction from a cockpit seat, yaw and pitch in degrees from its forward view, and the seat's up
    private static (double3 direction, double3 up) SeatView(Vehicle vehicle, IVASeat seat, double2 look)
    {
        var seat2Cce = vehicle.Asmb2Cce * seat.Parent.Asmb2VehicleAsmb;
        var forward = seat.ForwardAxisAsmb.Transform(seat2Cce).Normalized();
        var up = seat.UpAxisAsmb.Transform(seat2Cce).Normalized();
        var right = double3.Normalize(double3.Cross(forward, up));
        double yaw = look.X * Math.PI / 180, pitch = look.Y * Math.PI / 180;
        return ((forward * Math.Cos(yaw) + right * Math.Sin(yaw)) * Math.Cos(pitch) + up * Math.Sin(pitch), up);
    }

    private bool SetCameraMode(string mode)
    {
        if (Program.MainViewport is not GameViewport viewport)
            return false;
        CameraMode? m = mode switch { "orbit" => CameraMode.Orbit, "free" => CameraMode.Free, "iva" => CameraMode.IVA, _ => null };
        if (m == null)
            return false;
        ReleaseCamera();
        if (m != CameraMode.IVA)
        {
            _look = null;
            _lookMove = null;
        }
        if (viewport.Mode != m)
            viewport.SetCameraMode(m.Value);
        return true;
    }

    // A value of a move: "-" keeps the current one, "~<x>" adds to it, anything else replaces it; "km" for kilometres
    private static bool TryValue(string s, double current, double scale, out double value)
    {
        value = current;
        if (s == "-")
            return true;
        bool relative = s.StartsWith('~');
        if (relative)
            s = s[1..];
        double unit = 1;
        if (s.EndsWith("km", StringComparison.Ordinal))
        {
            unit = 1000;
            s = s[..^2];
        }
        if (!double.TryParse(s, NumberStyles.Float, Inv, out var x))
            return false;
        value = relative ? current + x * unit * scale : x * unit * scale;
        return true;
    }

    private bool Orbit(string[] a)
    {
        if (!SetCameraMode("orbit"))
            return false;
        // Back to the controlled vehicle after the vehicle camera or a flight left the camera on a celestial
        if (Program.GetHoveredCamera() is { Following: Celestial } camera && Program.ControlledVehicle is { } vehicle)
            camera.SetFollow(vehicle, tidalLocking: false, changeControl: false, alert: false);
        var view = Program.MainViewport.OrbitController.View;
        if (view == null)
            return false;
        const double Deg = Math.PI / 180;
        var from = new double3(view.Azimuth, view.Elevation, view.DistancePower);
        if (!TryValue(a[1], from.X, Deg, out var az) || !TryValue(a[2], from.Y, Deg, out var el) || !TryValue(a[3], from.Z, 1, out var dist))
            return false;
        double seconds = 0;
        if (a.Length == 5 && !double.TryParse(a[4], NumberStyles.Float, Inv, out seconds))
            return false;
        // A kept value stays out of the move, so @rotate goes on turning a kept azimuth
        var to = new double3(a[1] == "-" ? double.NaN : az, a[2] == "-" ? double.NaN : el, a[3] == "-" ? double.NaN : dist);
        _orbitMove = new OrbitMove(new double3(a[1] == "-" ? double.NaN : from.X, a[2] == "-" ? double.NaN : from.Y,
            a[3] == "-" ? double.NaN : from.Z), to, _scriptTime, seconds);
        UpdateCamera(0);
        return true;
    }

    private bool LookIva(string[] a)
    {
        if (Program.MainViewport is not GameViewport { Mode: CameraMode.IVA })
        {
            Log("@look: needs the cockpit camera (@camera iva)");
            return false;
        }
        var from = _look ?? default;
        if (!TryValue(a[1], from.X, 1, out var yaw) || !TryValue(a[2], from.Y, 1, out var pitch))
            return false;
        double seconds = 0;
        if (a.Length == 4 && !double.TryParse(a[3], NumberStyles.Float, Inv, out seconds))
            return false;
        _lookMove = new LookMove(from, new double2(yaw, pitch), _scriptTime, seconds);
        UpdateCamera(0);
        return true;
    }

    // Logs where the sun stands over a surface point of a celestial (to plan the light of a key)
    private static bool Sun(string[] a)
    {
        var sun = Universe.CurrentSystem?.GetWorldSun();
        if (Universe.CurrentSystem?.All.TryGet(a[1], out Celestial? body) != true || body == null || sun == null ||
            !double.TryParse(a[2], NumberStyles.Float, Inv, out var lat) || !double.TryParse(a[3], NumberStyles.Float, Inv, out var lon))
            return false;
        var (up, north, east) = Frame(body, lat, lon);
        var d = double3.Normalize(sun.GetPositionEcl() - body.GetPositionEcl());
        double elevation = Math.Asin(Math.Clamp(double3.Dot(d, up), -1, 1)) * 180 / Math.PI;
        double azimuth = Math.Atan2(double3.Dot(d, east), double3.Dot(d, north)) * 180 / Math.PI;
        Log(FormattableString.Invariant(
            $"@sun {body.Id} {lat} {lon}: elevation {elevation:F1}, azimuth {azimuth:F0} (game time {Universe.GetElapsedSeconds():F0} s, speed {Universe.GetSimulationSpeed():F0})"));
        return true;
    }
}
