using System;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Smoother SRB Flames, smoother_srb_flames (port-settings.toml): the flames of engines that leave a smoke trail (the
// SRBs) are drawn by the trail march, one dithered ray per cloud upscaling block, and the cloud upscaler hides the dither
// by blending each pixel with its history, which it finds through the march's motion. The game gives every exhaust
// segment no motion (TrailSegmentMotionData.Stationary: the gas stays where it was released), so the flames, which ride
// with the nozzle, are reprojected as gas left behind; in motion their history is rejected and the dither shows. Here
// the hot ends of segments near the followed vessel move with it, each by its heat against the frame's hottest (fresh
// exhaust glows; the glow fades as the gas cools, and cooler smoke keeps standing still): a prefix on UpdateBuffers,
// which uploads the frame's segments and their motion. The port's shader edit (srb-flames.edits) keeps that motion
// within 60 m of the camera, where the game fades a moving segment's motion out. A still-burning booster just staged
// near the followed core rides with the core too, which is close at first and fades as they part.
[Hook(Phase.Default, Switch = nameof(Settings.SmootherSrbFlames))]
internal static class SrbFlames
{
    // Full share of the vessel's motion within this many bounding radii of its centre, none past twice that
    private const double Near = 4.0;
    // The heat a share is measured against at least: once the engines stop, the warm smoke left is the hottest but
    // falls behind (fresh exhaust's heat is about 1)
    private const float HeatFloor = 0.5f;

    private static AccessTools.FieldRef<VolumetricTrailRenderer, TrailSegmentData[]> _segments = null!;
    private static AccessTools.FieldRef<VolumetricTrailRenderer, TrailSegmentMotionData[]> _motion = null!;
    private static IViewport? _viewport;
    private static Vehicle? _vehicle;
    private static Celestial? _parent;
    private static ulong _frame;
    private static double4x4 _previousAsmbToFrame;
    private static bool _failed;  // the game's motion from then on (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        _segments = AccessTools.FieldRefAccess<VolumetricTrailRenderer, TrailSegmentData[]>(
            Find.Field(typeof(VolumetricTrailRenderer), "_trailSegments"));
        _motion = AccessTools.FieldRefAccess<VolumetricTrailRenderer, TrailSegmentMotionData[]>(
            Find.Field(typeof(VolumetricTrailRenderer), "_trailSegmentMotion"));
        harmony.Patch(Find.Method(typeof(VolumetricTrailRenderer), "UpdateBuffers"),
            prefix: new HarmonyMethod(typeof(SrbFlames), nameof(Prefix)));
        return "hot exhaust near the followed vessel moves with it";
    }

    // An explosion record (FLAG_EXPLOSION_VOLUME) or lobe (FLAG_EXPLOSION_LOBE): its motion is its own
    private static bool IsExplosion(in TrailSegmentData segment) =>
        (segment.Colors.W & (TrailSegmentData.FLAG_EXPLOSION_VOLUME | TrailSegmentData.FLAG_EXPLOSION_LOBE)) != 0;

    // A trail segment's heat at its start or end (TrailSegmentData.Colors.Z, two halves)
    private static float Heat(in TrailSegmentData segment, bool end) =>
        (float)BitConverter.UInt16BitsToHalf((ushort)(end ? segment.Colors.Z >> 16 : segment.Colors.Z & 0xFFFF));

    private static void Prefix(VolumetricTrailRenderer __instance, IViewport viewport, Celestial parent, int segmentCount)
    {
        if (_failed)
            return;
        try
        {
            MoveFlames(__instance, viewport, parent, segmentCount);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail(nameof(SrbFlames), e);
        }
    }

    private static void MoveFlames(VolumetricTrailRenderer __instance, IViewport viewport, Celestial parent, int segmentCount)
    {
        Camera camera = viewport.GetCamera();
        if (camera.Following is not Vehicle vehicle)
        {
            _vehicle = null;
            return;
        }
        // Ego space -> the body's rotating frame, the frame the segments stand still in, and the vessel's assembly space
        // -> that frame
        double4x4 egoToFrame = Views.EgoToFrame(camera, parent);
        double4x4 asmbToEgo = vehicle.GetMatrixAsmb2Ego(camera);
        double4x4 asmbToFrame = asmbToEgo * egoToFrame;
        // Last frame's, not one from before a stretch without trails (the game skips this call then)
        bool same = ReferenceEquals(viewport, _viewport) && vehicle == _vehicle && parent == _parent &&
                    _frame + 1 == Program.FrameNumber;
        double4x4 previousAsmbToFrame = _previousAsmbToFrame;
        _viewport = viewport;
        _vehicle = vehicle;
        _parent = parent;
        _previousAsmbToFrame = asmbToFrame;
        _frame = Program.FrameNumber;
        if (!same)
            return;
        // A point riding with the vessel: where it was last frame, in this frame's ego space (where the segments are)
        double4x4.Invert(asmbToEgo, out var egoToAsmb);
        double4x4.Invert(egoToFrame, out var frameToEgo);
        double4x4 ride = egoToAsmb * previousAsmbToFrame * frameToEgo;
        double3 centre = camera.GetPositionEgo(vehicle);
        double radius = vehicle.BoundingSphereRadiusBody;
        TrailSegmentData[] segments = _segments(__instance);
        TrailSegmentMotionData[] motion = _motion(__instance);
        int count = Math.Min(segmentCount, Math.Min(segments.Length, motion.Length));
        float hottest = 0f;
        for (int i = 0; i < count; i++)
        {
            if (!IsExplosion(in segments[i]))
                hottest = Math.Max(hottest, Math.Max(Heat(in segments[i], false), Heat(in segments[i], true)));
        }
        if (!(hottest > 0f) || float.IsInfinity(hottest) || !(radius > 0.0))
            return;
        hottest = Math.Max(hottest, HeatFloor);
        for (int i = 0; i < count; i++)
        {
            ref TrailSegmentData s = ref segments[i];
            if (IsExplosion(in s))
                continue;
            motion[i].PreviousStart = Ride(s.Start, Heat(in s, false) / hottest, in ride, centre, radius);
            motion[i].PreviousEnd = Ride(s.End, Heat(in s, true) / hottest, in ride, centre, radius);
        }
    }

    // Where a segment's end was last frame: standing still (the game's), or riding with the vessel by its weight
    private static float4 Ride(float4 p, float heat, in double4x4 ride, double3 centre, double radius)
    {
        var at = new double3(p.X, p.Y, p.Z);
        double near = 1.0 - Smooth(Near * radius, 2.0 * Near * radius, (at - centre).Length());
        double w = (heat > 0f ? Math.Min(heat, 1f) : 0.0) * near;
        if (!(w > 0.0))
            return p;
        double3 was = double3.Transform(at, ride);
        double3 r = at + (was - at) * w;
        return new float4((float)r.X, (float)r.Y, (float)r.Z, p.W);
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }
}
