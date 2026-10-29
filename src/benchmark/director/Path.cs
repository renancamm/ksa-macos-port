// The free camera's flight: "@key <celestial> <lat> <lon> <alt> <heading> <pitch>" steps are the places it passes
// through, at their times, and it flies through them in one smooth move, turning and climbing as it goes. The keys of
// sections played one after the other make one flight; a section without keys ends it. After its last key the camera
// stays there until another camera command takes it. Explore mode's benchkey command (Explore.cs) writes keys.
//
// A key's place over its celestial: latitude and longitude, altitude in metres ("km" for kilometres) above the
// celestial's sea level (its mean radius), or "+<alt>" for that high above the ground at the key, heading in degrees
// from north and pitch in degrees (negative looks down).
//
// Heights: how fast a move looks depends on how high the camera is above what it sees, so the flight works with
// heights above a floor under each key: the ground there (the sea's surface where it covers the ground), lowered by
// Eye metres, since close to the ground the camera mostly sees what lies further away. Between two keys the floor goes
// smoothly from the one key's to the other's. The flight does not follow the ground in between, so it stays smooth
// over mountains; the game's own clamp only lifts it where it would go into the ground.
//
// The camera's place: between keys passed through without stopping, latitude, longitude and height follow a curve
// through the keys around them that never overshoots them (height by the same factor every second). A key with a @shot
// at its time is a stop: the camera eases into it and out of it (the same key again later holds it there, while the
// world goes on moving in front of it). From a stop to the next stop (or between keys far apart for their heights) the
// camera pans as fast as its height allows, the same speed in heights per second all the way, so most of the way is
// covered high up and the ground never streaks by close below (down to a lower key it just slides down). Only a jump
// too far to pan zooms out first, pans while high and zooms back in (van Wijk and Nuij's smooth zooming and panning).
//
// The camera's view: heading and pitch follow the keys' the same way (eased between stops), relative to the ground
// under it, with the camera's up level with the horizon (north up for a key looking straight down). In the middle of a
// zoom it looks down and ahead along its way.
//
// Between keys over two celestials the camera flies through space: it climbs away from the first while turning towards
// the second, crosses the space between them looking at it, and comes down to the second key.
using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    private enum Move { Curve, Pan, Zoom }

    // A key's values: latitude, longitude, ln(height above its floor), heading, pitch (longitude and heading unwrapped
    // along the flight); its floor's altitude
    private record Key(double T, Celestial Body, double Floor, double[] C, bool Stop)
    {
        public double[] Slope = new double[5];
        // How the camera moves from this key to the next
        public Move Next = Move.Curve;
    }

    // Faster than this, in heights per second, a pan from one stop to the next zooms out instead
    private const double PanLimit = 30;
    // How high a zoom climbs: its top is about Rho² / 2 times the way along the ground (van Wijk and Nuij's ρ; their
    // 1.42 climbs about as high as the way is long, which shows too much of the celestial and too little of the way)
    private const double Rho = 0.5;
    // How far below the ground a key's floor is, and the lowest a key is above the ground, in metres
    private const double Eye = 100, MinHeight = 2;

    private readonly List<List<Key>> _paths = new();
    // The flight the camera is on (or stays at the end of), and the last one a camera command released it from
    private List<Key>? _flight, _released;
    // @place's place, where the camera stays until a camera command moves it, and the game's limit on how small an
    // explosion may look from the camera before it is left out, which @place lifts meanwhile
    private Key? _placed;
    private float? _explosionCull;
    // The camera's up in the frame before, which the next frame's up carries on from
    private static double3? _lastUp;

    // Builds the flights from the sections that will play, with their keys' times on the script's clock
    private void BuildPaths()
    {
        double start = 0;
        List<Key>? path = null;
        foreach (var section in _sections)
        {
            var shots = section.Steps.Where(s => s.Words[0] == "@shot").Select(s => s.At).ToHashSet();
            var keys = section.Steps.Where(s => s.Words[0] == "@key").ToList();
            if (keys.Count == 0)
                path = null;
            foreach (var step in keys)
            {
                var key = ParseKey(start + step.At, step.Words, shots.Contains(step.At));
                if (key == null)
                {
                    Log($"bad key at line {step.Line}: '{step.Command}'");
                    continue;
                }
                if (path == null)
                {
                    path = new List<Key>();
                    _paths.Add(path);
                }
                path.Add(key);
            }
            start += section.Length;
        }
        foreach (var p in _paths)
            Prepare(p);
    }

    private static Key? ParseKey(double t, string[] a, bool stop)
    {
        if (a.Length != 7 || Universe.CurrentSystem?.All.TryGet(a[1], out Celestial? body) != true || body == null)
            return null;
        var v = new double[5];
        bool aboveGround = a[4].StartsWith('+');
        for (int i = 0; i < 5; i++)
        {
            if (!TryValue(i == 2 && aboveGround ? a[4][1..] : a[i + 2], 0, 1, out v[i]))
                return null;
        }
        double ground = Ground(body, Frame(body, v[0], v[1]).up);
        double height = Math.Max(aboveGround ? v[2] : v[2] - ground, MinHeight);
        return new Key(t, body, ground - Eye, new[] { v[0], v[1], Math.Log(height + Eye), v[3], v[4] }, stop);
    }

    // The height of the ground above sea level (on a celestial with an ocean, the sea's surface where it covers it)
    private static double Ground(Celestial body, double3 up)
    {
        double ground = body.GetTerrainHeightFromDirCce(up);
        return body.BodyTemplate.OceanReference != null ? Math.Max(ground, 0) : ground;
    }

    // For each stretch over one celestial: unwrapped longitudes, how the camera moves between its keys; then the slopes
    // of the curves (zero at stops, next to pans and zooms, at the ends of a stretch and where a value turns back, else
    // the harmonic mean of the slopes on both sides: a monotone cubic)
    private static void Prepare(List<Key> path)
    {
        for (int first = 0; first < path.Count;)
        {
            int end = first;
            while (end + 1 < path.Count && path[end + 1].Body == path[first].Body)
                end++;
            for (int i = first + 1; i <= end; i++)
            {
                Key a = path[i - 1], k = path[i];
                foreach (int c in new[] { 1, 3 })
                    k.C[c] += 360 * Math.Round((a.C[c] - k.C[c]) / 360);
                double distance = Angle(a.Body, a.C, k.C) * a.Body.MeanRadius;
                double w0 = Math.Exp(a.C[2]), w1 = Math.Exp(k.C[2]);
                bool still = (a.Stop || i - 1 == first) && (k.Stop || i == end);
                if (still || distance > 5 * Math.Max(w0, w1))
                {
                    // It zooms out only when panning would be too fast (the pan's fastest, at the middle of its easing: 1.875
                    // times the distance over its mean altitude)
                    double mean = Math.Abs(w1 - w0) < 1e-6 ? w0 : (w1 - w0) / Math.Log(w1 / w0);
                    a.Next = 1.875 * distance / mean / (k.T - a.T) <= PanLimit ? Move.Pan : Move.Zoom;
                }
            }
            first = end + 1;
        }
        for (int i = 1; i + 1 < path.Count; i++)
        {
            Key a = path[i - 1], k = path[i], b = path[i + 1];
            if (k.Stop || a.Body != k.Body || b.Body != k.Body || a.Next != Move.Curve || k.Next != Move.Curve)
                continue;
            for (int c = 0; c < 5; c++)
            {
                double before = (k.C[c] - a.C[c]) / (k.T - a.T), after = (b.C[c] - k.C[c]) / (b.T - k.T);
                k.Slope[c] = before * after <= 0 ? 0 : 2 / (1 / before + 1 / after);
            }
        }
    }

    // The angle between two keys' places as seen from their celestial's center, in radians
    private static double Angle(Celestial body, double[] a, double[] b) =>
        Math.Acos(Math.Clamp(double3.Dot(body.GetDirCcfFromLatLon(a[0], a[1]), body.GetDirCcfFromLatLon(b[0], b[1])), -1, 1));

    // Puts the free camera on its flight, if it is on one; false when it is not
    private bool UpdatePath()
    {
        var path = _paths.LastOrDefault(p => p[0].T <= _scriptTime);
        if (path == null || path == _released)
            return false;
        if (path != _flight)
            _lastUp = null;
        _flight = path;
        int i = path.FindLastIndex(k => k.T <= _scriptTime);
        if (i == path.Count - 1)
        {
            var last = path[^1];
            Apply(last.Body, Resolve(last.Body, last.C, last.Floor));
            return true;
        }
        Key a = path[i], b = path[i + 1];
        double s = Math.Clamp((_scriptTime - a.T) / (b.T - a.T), 0, 1);
        if (a.Body != b.Body)
        {
            Transfer(a, b, s);
            return true;
        }
        if (a.Next == Move.Curve)
        {
            Apply(a.Body, Resolve(a.Body, Curve(a, b, s), a.Floor + (b.Floor - a.Floor) * s));
            return true;
        }
        // Pans and zooms: heading and pitch eased from one key's to the next's
        double e = Smooth(s);
        var (along, lnHeight) = a.Next == Move.Pan ? Pan(a, b, s) : Zoom(a, b, s);
        var place = Along(a, b, along, lnHeight);
        var c = new[] { place[0], place[1], place[2], a.C[3] + (b.C[3] - a.C[3]) * e, a.C[4] + (b.C[4] - a.C[4]) * e };
        double floor = a.Floor + (b.Floor - a.Floor) * along;
        var view = Resolve(a.Body, c, floor);
        if (a.Next == Move.Zoom)
            view = LookAhead(a, b, s, c, floor, view);
        Apply(a.Body, view);
        return true;
    }

    // The directives' hold on the free camera ends when something else moves it
    private void ReleaseCamera()
    {
        _released = _flight;
        Unplace();
        EndRig();
    }

    // The game leaves out an explosion that looks smaller from the camera than a quarter of a degree or so
    // (ExplosionSystem.PerceptualCullRadians): from @place's camera, far from the vehicles, it would leave out all of
    // them, so @place lets every explosion in until the camera moves on
    private void Place(Key key)
    {
        _placed = key;
        _explosionCull ??= ExplosionSystem.PerceptualCullRadians;
        ExplosionSystem.PerceptualCullRadians = 0;
    }

    private void Unplace()
    {
        _placed = null;
        if (_explosionCull is { } cull)
            ExplosionSystem.PerceptualCullRadians = cull;
        _explosionCull = null;
    }

    private static double[] Curve(Key a, Key b, double s)
    {
        double h = b.T - a.T, s2 = s * s, s3 = s2 * s;
        double h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2, h11 = s3 - s2;
        var c = new double[5];
        for (int i = 0; i < 5; i++)
            c[i] = h00 * a.C[i] + h10 * h * a.Slope[i] + h01 * b.C[i] + h11 * h * b.Slope[i];
        return c;
    }

    // From one stop to the next at the same speed in heights per second: the height changes by the same factor every
    // second, eased in and out, and the way along the ground grows with the height. How far along the way, and
    // ln(height).
    private static (double, double) Pan(Key a, Key b, double s)
    {
        double e = Quintic(s);
        double ratio = Math.Exp(b.C[2] - a.C[2]);
        return (Math.Abs(ratio - 1) < 1e-9 ? e : (Math.Pow(ratio, e) - 1) / (ratio - 1), a.C[2] + (b.C[2] - a.C[2]) * e);
    }

    // A jump too far to pan: van Wijk and Nuij's optimal path over (distance along the great circle, height as the
    // view's width). Its pace is set by how much the view changes: zooming, the ground going by (in heights) and, high
    // up, the celestial swinging round, so the view changes at an even rate (eased in and out) instead of rushing over
    // the ground at the top of a short jump or swinging round all at once at the top of a long one. How far along the
    // way, and ln(height).
    private static (double, double) Zoom(Key a, Key b, double s)
    {
        const double Swing = 4;
        const int Steps = 256;
        double arc = Angle(a.Body, a.C, b.C);
        double d = arc * a.Body.MeanRadius;
        var z = new VanWijk(Math.Exp(a.C[2]), Math.Exp(b.C[2]), d);
        // How much the view has changed along the path: e-folds of zoom, heights of ground and radians of swing
        // (weighted), added up
        var change = new double[Steps + 1];
        for (int i = 1; i <= Steps; i++)
        {
            double x0 = z.Span * (i - 1) / Steps, x1 = z.Span * i / Steps;
            double zoom = z.LnWidth(x1) - z.LnWidth(x0);
            double pan = (z.Ahead(x1) - z.Ahead(x0)) * (d / Math.Exp((z.LnWidth(x0) + z.LnWidth(x1)) / 2) + arc * Swing);
            change[i] = change[i - 1] + Math.Sqrt(zoom * zoom + pan * pan);
        }
        double target = change[Steps] * Quintic(s);
        int j = Math.Clamp(Array.BinarySearch(change, target) is var found && found >= 0 ? found : ~found, 1, Steps);
        double part = change[j] > change[j - 1] ? (target - change[j - 1]) / (change[j] - change[j - 1]) : 0;
        double x = z.Span * (j - 1 + Math.Clamp(part, 0, 1)) / Steps;
        return (z.Ahead(x), z.LnWidth(x));
    }

    // Van Wijk and Nuij's path from width w0 to width w1 over a distance d, by its own parameter x from 0 to Span: the
    // fraction of the way along and ln(width) there, and its highest ln(width)
    private readonly struct VanWijk
    {
        private readonly double _w0, _d, _r0, _r1;
        public readonly double Span;

        public VanWijk(double w0, double w1, double d)
        {
            double rho2 = Rho * Rho;
            double b0 = (w1 * w1 - w0 * w0 + rho2 * rho2 * d * d) / (2 * w0 * rho2 * d);
            double b1 = (w1 * w1 - w0 * w0 - rho2 * rho2 * d * d) / (2 * w1 * rho2 * d);
            (_w0, _d, _r0, _r1) = (w0, d, Math.Log(-b0 + Math.Sqrt(b0 * b0 + 1)), Math.Log(-b1 + Math.Sqrt(b1 * b1 + 1)));
            Span = (_r1 - _r0) / Rho;
        }

        public double Ahead(double x) =>
            Math.Clamp(_w0 / (Rho * Rho) * (Math.Cosh(_r0) * Math.Tanh(Rho * x + _r0) - Math.Sinh(_r0)) / _d, 0, 1);

        public double LnWidth(double x) => Math.Log(_w0 * Math.Cosh(_r0) / Math.Cosh(Rho * x + _r0));

        public double LnPeak => Math.Log(_w0 * Math.Cosh(_r0) / Math.Cosh(Math.Clamp(0, Math.Min(_r0, _r1), Math.Max(_r0, _r1))));
    }

    // In the middle of a zoom, looking down and ahead along the way: rising and falling over the whole zoom's time (so
    // the view turns slowly), and as much as the zoom climbs above its keys (none for one that hardly climbs). Heading
    // and pitch turn as angles, the heading always the same way round for the whole zoom, and the pitch never further
    // down than the look ahead's, so the view never passes over straight down (where it would roll over).
    private static (double3, double3, double3) LookAhead(Key a, Key b, double s, double[] c, double floor,
        (double3, double3, double3) view)
    {
        double low = Math.Max(a.C[2], b.C[2]);
        double top = Math.Max(low, new VanWijk(Math.Exp(a.C[2]), Math.Exp(b.C[2]), Angle(a.Body, a.C, b.C) * a.Body.MeanRadius).LnPeak);
        double ahead = Math.Clamp((top - low) / Math.Log(100), 0, 1) * Math.Pow(Math.Sin(Math.PI * s), 2);
        if (ahead <= 0)
            return view;
        // The way round: the one the turn takes in the middle of the zoom, kept all along
        var half = Along(a, b, 0.5, 0);
        double middle = Wrap(Bearing(a, b, half[0], half[1]) - (a.C[3] + b.C[3]) / 2);
        double delta = middle + Wrap(Bearing(a, b, c[0], c[1]) - c[3] - middle);
        return Resolve(a.Body, new[] { c[0], c[1], c[2], c[3] + delta * ahead, c[4] + (Math.Min(c[4], -60) - c[4]) * ahead }, floor);
    }

    // An angle in degrees brought within -180 to 180
    private static double Wrap(double degrees) => ((degrees % 360) + 540) % 360 - 180;

    // The heading of the way along the great circle from key a to key b, at a place on it (defined all the way, even
    // over key b itself)
    private static double Bearing(Key a, Key b, double lat, double lon)
    {
        var (up, north, east) = Frame(a.Body, lat, lon);
        var ccf2cce = a.Body.GetCcf2Cce();
        var plane = double3.Cross(a.Body.GetDirCcfFromLatLon(a.C[0], a.C[1]).Transform(ccf2cce),
            a.Body.GetDirCcfFromLatLon(b.C[0], b.C[1]).Transform(ccf2cce));
        if (plane.Length() < 1e-9)
            return 0;
        var toward = double3.Cross(plane, up);
        return Math.Atan2(double3.Dot(toward, east), double3.Dot(toward, north)) * 180 / Math.PI;
    }

    // The place that far along the great circle from key a to key b, at that ln(height)
    private static double[] Along(Key a, Key b, double along, double lnHeight)
    {
        var dir = Slerp(a.Body.GetDirCcfFromLatLon(a.C[0], a.C[1]), a.Body.GetDirCcfFromLatLon(b.C[0], b.C[1]), along);
        return new[] { Math.Asin(Math.Clamp(dir.Z, -1, 1)) * 180 / Math.PI, Math.Atan2(dir.Y, dir.X) * 180 / Math.PI, lnHeight };
    }

    // The ground point's frame at a latitude and longitude of a celestial: up, north and east, in its ecliptic axes
    private static (double3 up, double3 north, double3 east) Frame(Celestial body, double lat, double lon)
    {
        var ccf2cce = body.GetCcf2Cce();
        var up = body.GetDirCcfFromLatLon(lat, lon).Transform(ccf2cce);
        double step = lat > 89 ? -0.01 : 0.01;
        var toward = body.GetDirCcfFromLatLon(lat + step, lon).Transform(ccf2cce) - up;
        var north = double3.Normalize(toward - up * double3.Dot(toward, up)) * Math.Sign(step);
        return (up, north, double3.Cross(north, up));
    }

    // A place's latitude, longitude and altitude above sea level over a celestial
    private static (double lat, double lon, double alt) Place(Celestial body, double3 positionEcl)
    {
        var offset = positionEcl - body.GetPositionEcl();
        var dirCcf = double3.Normalize(offset).Transform(body.GetCce2Ccf());
        return (Math.Asin(Math.Clamp(dirCcf.Z, -1, 1)) * 180 / Math.PI, Math.Atan2(dirCcf.Y, dirCcf.X) * 180 / Math.PI,
            offset.Length() - body.MeanRadius);
    }

    // Where a key's values put the camera, over a floor at that altitude: the direction it looks in and its up (square
    // to that direction, level with the horizon)
    private static (double3 position, double3 forward, double3 up) Resolve(Celestial body, double[] c, double floor)
    {
        var (up, north, east) = Frame(body, c[0], c[1]);
        var position = body.GetPositionEcl() + up * (body.MeanRadius + floor + Math.Exp(c[2]));
        double h = c[3] * Math.PI / 180, q = c[4] * Math.PI / 180;
        var level = north * Math.Cos(h) + east * Math.Sin(h);
        return (position, level * Math.Cos(q) + up * Math.Sin(q), up * Math.Cos(q) - level * Math.Sin(q));
    }

    // From a key over one celestial to a key over another, s from 0 to 1. Up from the first key, turning towards the
    // second celestial, to a few times the first's radius; across; down to the second key the same way. The camera's
    // height above the floor behind it (out and across the first half) or ahead of it (the rest) changes by the same
    // factor every second all the way, eased in and out, and the parts meet at the same speed, so it rises off the ground
    // as gently as a climb over the ground does and never speeds up from one part to the next. The view turns to the
    // second celestial at the start, looks at it across, and turns to the second key's view at the end.
    private static void Transfer(Key a, Key b, double s)
    {
        double v = Quintic(s);
        var (_, fa, ua) = Resolve(a.Body, a.C, a.Floor);
        var (_, fb, ub) = Resolve(b.Body, b.C, b.Floor);
        double3 ca = a.Body.GetPositionEcl(), cb = b.Body.GetPositionEcl();
        double span = double3.Distance(ca, cb);
        var toB = (cb - ca) / span;
        // Each key's floor (its distance from its celestial's center), height above it and the place up from it where
        // the camera leaves for (or comes from) the space between
        double ga = a.Body.MeanRadius + a.Floor, gb = b.Body.MeanRadius + b.Floor;
        double ha = Math.Exp(a.C[2]), hb = Math.Exp(b.C[2]);
        double hx = Math.Max(Math.Min(25 * a.Body.MeanRadius, 0.25 * span), ga + ha) - ga;
        double hy = Math.Max(Math.Min(25 * b.Body.MeanRadius, 0.25 * span), gb + hb) - gb;
        var na = Frame(a.Body, a.C[0], a.C[1]).up;
        var nb = Frame(b.Body, b.C[0], b.C[1]).up;
        var outA = Slerp(na, toB, 0.8);
        var outB = Slerp(nb, -toB, 0.8);
        var x = ca + outA * (ga + hx);
        var y = cb + outB * (gb + hy);
        double d = double3.Distance(x, y);
        // Across, the height above the first celestial grows until the height above the second, shrinking, is as fast
        double m = Math.Clamp((d + hy - hx) / 2, 0, d);
        // The four parts in factors of height (e-folds): up from the first, across to where the heights' rates meet,
        // across to the second, down to it; the camera covers them at an even pace
        double l1 = Math.Log(hx / ha), l2 = Math.Log((hx + m) / hx), l3 = Math.Log((hy + d - m) / hy), l4 = Math.Log(hy / hb);
        double c = v * (l1 + l2 + l3 + l4);
        double3 position;
        if (c < l1)
        {
            // Turning towards the second celestial as it rises, by height rather than by factor, so the camera hardly moves
            // over the ground as it leaves it
            double h = ha * Math.Exp(c);
            position = ca + Slerp(na, outA, hx > ha ? Smooth((h - ha) / (hx - ha)) : 1) * (ga + h);
        }
        else if (c < l1 + l2)
            position = x + (y - x) / d * (hx * (Math.Exp(c - l1) - 1));
        else if (c < l1 + l2 + l3)
            position = y + (x - y) / d * (hy * (Math.Exp(l1 + l2 + l3 - c) - 1));
        else
        {
            double h = hb * Math.Exp(l1 + l2 + l3 + l4 - c);
            position = cb + Slerp(nb, outB, hy > hb ? Smooth((h - hb) / (hy - hb)) : 1) * (gb + h);
        }
        const double Turn = 0.3;
        var atB = double3.Normalize(cb - position);
        var forward = v < Turn ? Slerp(fa, atB, Smooth(v / Turn), ua) : v > 1 - Turn ? Slerp(atB, fb, Smooth((v - (1 - Turn)) / Turn), ub) : atB;
        Apply(c < l1 + l2 ? a.Body : b.Body, (position, forward, Slerp(ua, ub, v, fa)));
    }

    // Eases u from 0 to 1: smoothly in and out, the speed too (smoothstep, and quintic smootherstep)
    private static double Smooth(double u) => u * u * (3 - 2 * u);
    private static double Quintic(double u) => u * u * u * (u * (6 * u - 15) + 10);

    // Turns unit vector a towards unit vector b by the fraction w of the angle between them, ending exactly on b. When
    // the two are opposite (within a degree), the way round is the hint's side, so it cannot swap sides from one frame
    // to the next.
    private static double3 Slerp(double3 a, double3 b, double w, double3? hint = null)
    {
        double dot = Math.Clamp(double3.Dot(a, b), -1, 1);
        double angle = Math.Acos(dot);
        if (angle < 1e-9)
            return a;
        var across = b - a * dot;
        if (dot < 0 && across.Length() < 0.02)
        {
            var side = (hint ?? default) - a * double3.Dot(hint ?? default, a);
            if (side.Length() < 1e-9)
                side = double3.Cross(a, Math.Abs(a.X) < 0.9 ? new double3(1, 0, 0) : new double3(0, 1, 0));
            across = side;
        }
        across = double3.Normalize(across - a * double3.Dot(across, a));
        return a * Math.Cos(angle * w) + across * Math.Sin(angle * w);
    }

    // Puts the free camera there, following that celestial (or a vehicle over it), and out of the ground (and the sea) if
    // it would be in it (clearance metres above it).
    // Its up is the one given, square to the view; should the two ever line up, the up of the frame before carries on.
    private static void Apply(Celestial body, (double3 position, double3 forward, double3 up) view, double clearance = 1.5,
        Vehicle? follow = null)
    {
        var camera = Program.GetHoveredCamera();
        if (camera == null || Program.MainViewport is not GameViewport viewport)
            return;
        if (follow != null ? camera.Following != follow : camera.Following != body)
        {
            if (follow != null)
                camera.SetFollow(follow, tidalLocking: false, changeControl: false, alert: false);
            else
                camera.SetFollow(body, tidalLocking: true, changeControl: false, alert: false);
        }
        if (viewport.Mode != CameraMode.Free)
            viewport.SetCameraMode(CameraMode.Free);
        var forward = double3.Normalize(view.forward);
        // The up given, squared to the view; as the two line up, the up of the frame before takes over, gradually
        var wanted = view.up - forward * double3.Dot(view.up, forward);
        var up = wanted;
        if (_lastUp is { } last)
        {
            var carried = last - forward * double3.Dot(last, forward);
            double trust = Math.Clamp((wanted.Length() - 0.02) / 0.1, 0, 1);
            if (carried.Length() > 1e-9)
                up = (wanted.Length() > 1e-9 ? double3.Normalize(wanted) * trust : wanted) + double3.Normalize(carried) * (1 - trust);
        }
        if (up.Length() < 1e-9)
            up = double3.Cross(forward, Math.Abs(forward.X) < 0.9 ? new double3(1, 0, 0) : new double3(0, 1, 0));
        up = double3.Normalize(up - forward * double3.Dot(up, forward));
        _lastUp = up;
        var center = body.GetPositionEcl();
        var position = view.position;
        if (body.BodyTemplate.OceanReference != null && double3.Distance(position, center) < body.MeanRadius + 1)
            position = center + double3.Normalize(position - center) * (body.MeanRadius + 1);
        camera.SetPosition(position);
        if (camera.TryGetSurfaceClampPositionEcl(clearance, out var clamped))
            camera.SetPosition(clamped);
        camera.LocalRotation = Camera.LookAtRotation(forward, up);
        // The free camera re-applies its cached offset every frame: take the new place as that offset
        viewport.FlyController.CacheOffset();
    }
}
