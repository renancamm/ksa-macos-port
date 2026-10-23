// The vehicle camera: the free camera keeping the controlled vehicle in view, either going along with it (@chase) or
// standing on the ground (@watch). It aims at the vehicle's center, raised by @aim. Once the vehicle breaks up or blows
// up (it is no longer the controlled one), it goes on aiming at the place where the vehicle was.
//
// Every change is a move over some seconds of the script's clock, eased in and out. Between two chase views the
// camera turns, climbs and pulls back around the vehicle (the distance by the same factor every second); between a
// chase view and the ground, or from wherever the camera was before (the orbit camera, say), it flies from the one
// place to the other, still aiming at the vehicle, so there is never a cut.
using System;
using System.Globalization;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    // A chase view: azimuth (degrees from north, where the camera is seen from the vehicle), elevation (degrees above
    // the vehicle's horizon) and ln(distance in metres)
    private record ChaseMove(double3 From, double3 To, double Start, double Seconds);

    // A ground view: the camera's place over the celestial, its height above the ground, and how tall the view is at
    // the vehicle in metres (0 keeps the field of view)
    private record GroundView(double Lat, double Lon, double Height, double Frame);

    private class Rig(Celestial body, Vehicle target, double3 anchorCcf)
    {
        public readonly Celestial Body = body;
        public readonly Vehicle Target = target;
        // The vehicle's center over the celestial (in its fixed frame), held once it broke up
        public double3 AnchorCcf = anchorCcf;
        public double AimUp;
        public bool FollowVehicle;
        public (double From, double To, double Start, double Seconds)? AimMove;
        // The view now: a chase view (with @rotate's turn added to its azimuth) or a ground view
        public ChaseMove? Chase;
        public double Spin;
        public GroundView? Ground;
        // The place and field of view the camera flies from, while it goes from one kind of view to the other
        public Func<(double3 position, double fov)>? From;
        public double FromStart, FromSeconds;
    }

    // The game's clamp keeps the camera this high above the ground (metres): low enough for a kitten's face
    private const double RigClearance = 0.2;
    // Faster than this over the ground (m/s), the camera follows the vehicle itself
    private const double FollowSpeed = 20;

    private Rig? _rig;
    // The camera's field of view before the rig changed it (radians)
    private float? _fieldOfView;

    // @chase <az> <el> <dist> [<seconds>]
    private bool StartChase(string[] a)
    {
        if (!TryRig(out var rig))
            return false;
        // Where the camera is now, as a chase view (it carries on from there without a jump)
        var now = rig.Chase is { } c ? ChaseAt(rig, c) : CameraAsChase(rig);
        if (!TryValue(a[1], now.X, 1, out var az) || !TryValue(a[2], now.Y, 1, out var el) || !TryValue(a[3], Math.Exp(now.Z), 1, out var dist) ||
            !TrySeconds(a, 4, out var seconds))
            return false;
        var to = new double3(a[1] == "-" ? double.NaN : az, a[2] == "-" ? double.NaN : el, a[3] == "-" ? double.NaN : Math.Log(Math.Max(dist, 0.1)));
        if (rig.Chase == null)
        {
            // From the ground or another camera: fly from there to the chase view
            StartBlend(rig, seconds);
            rig.Ground = null;
            rig.Chase = new ChaseMove(now, Fill(to, now), _scriptTime, 0);
        }
        else
            rig.Chase = new ChaseMove(now, to, _scriptTime, seconds);
        rig.Spin = 0;
        UpdateRig(0);
        return true;
    }

    // @watch <az> <dist> <height> [<frame>] [<seconds>]: the camera's place on the ground, dist metres from the vehicle
    // in the direction az, height metres above the ground there
    private bool StartWatch(string[] a)
    {
        if (!TryRig(out var rig) || !double.TryParse(a[1], NumberStyles.Float, Inv, out var az) ||
            !double.TryParse(a[2], NumberStyles.Float, Inv, out var dist) || !double.TryParse(a[3], NumberStyles.Float, Inv, out var height))
            return false;
        double frame = 0;
        if (a.Length >= 5 && !double.TryParse(a[4], NumberStyles.Float, Inv, out frame) || !TrySeconds(a, 5, out var seconds))
            return false;
        var (lat, lon, _) = Place(rig.Body, AnchorEcl(rig));
        var (up, north, east) = Frame(rig.Body, lat, lon);
        double h = az * Math.PI / 180, angle = dist / rig.Body.MeanRadius;
        var level = north * Math.Cos(h) + east * Math.Sin(h);
        var (groundLat, groundLon, _) = Place(rig.Body, rig.Body.GetPositionEcl() + (up * Math.Cos(angle) + level * Math.Sin(angle)) * rig.Body.MeanRadius);
        StartBlend(rig, seconds);
        rig.Chase = null;
        rig.Ground = new GroundView(groundLat, groundLon, height, frame);
        UpdateRig(0);
        return true;
    }

    // @aim <metres> [<seconds>]: aim that far above the vehicle's center (a kitten's face, say)
    private bool StartAim(string[] a)
    {
        if (!TryRig(out var rig) || !TryValue(a[1], rig.AimUp, 1, out var up) || !TrySeconds(a, 2, out var seconds))
            return false;
        rig.AimMove = (rig.AimUp, up, _scriptTime, seconds);
        UpdateRig(0);
        return true;
    }

    private static bool TrySeconds(string[] a, int index, out double seconds)
    {
        seconds = 0;
        return a.Length <= index || double.TryParse(a[index], NumberStyles.Float, Inv, out seconds);
    }

    // The rig on the controlled vehicle, started when the camera is elsewhere
    private bool TryRig(out Rig rig)
    {
        rig = _rig!;
        if (_rig != null)
            return true;
        if (Program.ControlledVehicle is not { } v || v.Parent is not Celestial body)
        {
            Log("the vehicle camera needs a controlled vehicle on or near a celestial");
            return false;
        }
        ReleaseCamera();
        _look = null;
        _lookMove = null;
        rig = _rig = new Rig(body, v, ToCcf(body, v.GetPositionEcl()));
        _lastUp = null;
        if (Program.GetHoveredCamera() is { } camera)
            _fieldOfView ??= camera.GetFieldOfView();
        return true;
    }

    // From here the camera flies from where it is now (a ground view stays put; a chase view keeps going along)
    private void StartBlend(Rig rig, double seconds)
    {
        if (seconds <= 0)
        {
            rig.From = null;
            return;
        }
        if (rig.Ground is { } ground)
            rig.From = () => GroundPlace(rig, ground);
        else if (rig.Chase is { } chase)
        {
            var spin = rig.Spin;
            var view = ChaseAt(rig, chase);
            rig.From = () => (ChasePlace(rig, view + new double3(rig.Spin - spin, 0, 0)), DefaultFov());
        }
        else
        {
            var view = CameraAsChase(rig);
            rig.From = () => (ChasePlace(rig, view), DefaultFov());
        }
        rig.FromStart = _scriptTime;
        rig.FromSeconds = seconds;
    }

    private void UpdateRig(double dt)
    {
        if (_rig is not { } rig)
            return;
        bool whole = !rig.Target.IsDisposed && Program.ControlledVehicle == rig.Target;
        if (whole)
            rig.AnchorCcf = ToCcf(rig.Body, rig.Target.GetPositionEcl());
        // The camera follows a fast vehicle itself: the game draws the camera relative to the vehicle's place at the
        // moment of drawing, so it keeps up with it however fast it goes. A slow one it leaves alone: the game turns
        // the camera's place with the vehicle it follows, so a walking kitten's sway would swing it around.
        if (!whole)
            rig.FollowVehicle = false;
        else if (rig.Target.GetSurfaceSpeed() > FollowSpeed)
            rig.FollowVehicle = true;
        else if (rig.Target.GetSurfaceSpeed() < FollowSpeed / 2)
            rig.FollowVehicle = false;
        if (rig.AimMove is { } m)
        {
            rig.AimUp = m.From + (m.To - m.From) * Ease(m.Start, m.Seconds);
            if (_scriptTime >= m.Start + m.Seconds)
                rig.AimMove = null;
        }
        rig.Spin += _rotateRadPerSecond * 180 / Math.PI * dt;
        var (position, fov) = rig.Ground is { } ground ? GroundPlace(rig, ground) : (ChasePlace(rig, ChaseAt(rig, rig.Chase!)), DefaultFov());
        if (rig.From is { } from)
        {
            double u = Ease(rig.FromStart, rig.FromSeconds);
            var (p0, fov0) = from();
            position = p0 + (position - p0) * u;
            fov = fov0 + (fov - fov0) * u;
            if (u >= 1)
                rig.From = null;
        }
        var aim = AnchorEcl(rig);
        var (lat, lon, _) = Place(rig.Body, aim);
        Apply(rig.Body, (position, aim - position, Frame(rig.Body, lat, lon).up), RigClearance, rig.FollowVehicle ? rig.Target : null);
        if (Program.GetHoveredCamera() is { } camera)
            camera.SetFieldOfView((float)fov);
    }

    private void EndRig()
    {
        _rig = null;
        if (_fieldOfView is { } fov && Program.GetHoveredCamera() is { } camera)
            camera.SetFieldOfView((float)(fov * 180 / Math.PI));
        _fieldOfView = null;
    }

    // The chase view at this moment of its move, with @rotate's turn
    private double3 ChaseAt(Rig rig, ChaseMove m)
    {
        double u = Ease(m.Start, m.Seconds);
        var v = Fill(m.From + (m.To - m.From) * u, m.From);
        if (u >= 1)
            rig.Chase = m with { From = v, To = v };
        return v + new double3(rig.Spin, 0, 0);
    }

    // A move's target with the values it keeps ("-") filled in from where it starts
    private static double3 Fill(double3 to, double3 from) =>
        new(double.IsNaN(to.X) ? from.X : to.X, double.IsNaN(to.Y) ? from.Y : to.Y, double.IsNaN(to.Z) ? from.Z : to.Z);

    private (double3, double) GroundPlace(Rig rig, GroundView g)
    {
        var up = Frame(rig.Body, g.Lat, g.Lon).up;
        var position = rig.Body.GetPositionEcl() + up * (rig.Body.MeanRadius + Ground(rig.Body, up) + g.Height);
        double fov = g.Frame > 0
            ? Math.Clamp(2 * Math.Atan(g.Frame / 2 / Math.Max(double3.Distance(position, AnchorEcl(rig)), 1)) * 180 / Math.PI, 1, 60)
            : DefaultFov();
        return (position, fov);
    }

    private static double3 ChasePlace(Rig rig, double3 view)
    {
        var anchor = AnchorEcl(rig);
        var (lat, lon, _) = Place(rig.Body, anchor);
        var (up, north, east) = Frame(rig.Body, lat, lon);
        double az = view.X * Math.PI / 180, el = view.Y * Math.PI / 180;
        return anchor + ((north * Math.Cos(az) + east * Math.Sin(az)) * Math.Cos(el) + up * Math.Sin(el)) * Math.Exp(view.Z);
    }

    // Where the camera is now, as a chase view of the rig's vehicle
    private static double3 CameraAsChase(Rig rig)
    {
        var anchor = AnchorEcl(rig);
        var offset = (Program.GetHoveredCamera()?.PositionEcl ?? anchor) - anchor;
        double dist = Math.Max(offset.Length(), 0.1);
        var (lat, lon, _) = Place(rig.Body, anchor);
        var (up, north, east) = Frame(rig.Body, lat, lon);
        var d = offset / dist;
        return new double3(Math.Atan2(double3.Dot(d, east), double3.Dot(d, north)) * 180 / Math.PI,
            Math.Asin(Math.Clamp(double3.Dot(d, up), -1, 1)) * 180 / Math.PI, Math.Log(dist));
    }

    // The point the camera aims at
    private static double3 AnchorEcl(Rig rig)
    {
        var center = rig.Body.GetPositionEcl() + rig.AnchorCcf.Transform(rig.Body.GetCcf2Cce());
        return center + double3.Normalize(center - rig.Body.GetPositionEcl()) * rig.AimUp;
    }

    private static double3 ToCcf(Celestial body, double3 positionEcl) => (positionEcl - body.GetPositionEcl()).Transform(body.GetCce2Ccf());

    private double DefaultFov() => (_fieldOfView ?? Program.GetHoveredCamera()?.GetFieldOfView() ?? 0.87266463f) * 180 / Math.PI;
}
