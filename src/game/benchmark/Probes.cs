// The tools for work on the port, plugged into a run (RunOptions turns them on, benchmark.sh's program gives the game
// what each needs from KSA Metal): each one watches the run's parts, its checkpoints or its frames, and none of them
// changes how the run plays. A probe that works on a checkpoint's view (a frame capture before the screenshot, a pass
// dump after it) holds the checkpoint until it is done: it returns a wait, which the director asks every frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

// A checkpoint's view as a probe sees it: the scenario, the screenshot's name and file
internal sealed record View(string Scenario, string Shot, string Path);

// Done: true once the work it waits for is over (or has taken too long), asked once a frame
internal delegate bool Wait();

internal interface IProbe
{
    // A measured part opened or closed
    void Opened(string scenario, string part) { }

    // The view of a checkpoint stands still, before its screenshot
    Wait? BeforeShot(View view) => null;

    // The screenshot is there
    Wait? AfterShot(View view) => null;

    // A frame of the script playing: the seconds since the run's first frame, the time of the script's clock, the
    // director's phase
    void Frame(double wall, double script, string phase) { }

    // The run ended
    void End() { }

    // What it says it does, for the log
    string Name { get; }
}

// What a probe has to tell: a problem of the run, or a line in the game's log
internal sealed record ProbeContext(string Dir, Action<string> Error, Action<string> Log);

internal static class Probes
{
    // The probes these options turn on
    public static List<IProbe> For(RunOptions options, ProbeContext context)
    {
        var probes = new List<IProbe>();
        if (options.Profile > 0)
            probes.Add(new ProfileProbe(options.Profile, context));
        if (options.Capture)
            probes.Add(new CaptureProbe(context));
        if (options.Catch.Length > 0 || options.Passes)
            probes.Add(new CatchProbe(options.Catch.Length > 0 ? options.Catch : null, options.CatchCapture, options.Passes, context));
        if (options.Film > 0)
            probes.Add(new FilmProbe(options.Film, context));
        if (options.Trace)
            probes.Add(new CameraLogProbe(context));
        return probes;
    }

    // Waits for a condition, at most so many seconds of the Mac's clock
    public static Wait Until(Func<bool> done, double seconds)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        return () => done() || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds;
    }

    // Several waits, one after the other
    public static Wait Then(Wait first, Func<Wait?> next)
    {
        Wait? second = null;
        bool firstDone = false;
        return () =>
        {
            if (!firstDone)
            {
                if (!first())
                    return false;
                firstDone = true;
                second = next();
            }
            return second == null || second();
        };
    }
}

// KSA Metal's GPU profiler on the first frames of every measured part (the game needs KSA_MTL_GPU_PROFILE=<csv>)
internal sealed class ProfileProbe(uint frames, ProbeContext context) : IProbe
{
    private bool _off;

    public string Name => $"profiling {frames} frames a part";

    public void Opened(string scenario, string part)
    {
        if (_off || DriverStats.ProfileFrames($"{scenario}/{part}", frames))
            return;
        context.Error($"{part}: KSA Metal's GPU profiler is off (does the game have KSA_MTL_GPU_PROFILE=<csv>?)");
        _off = true;
    }
}

// An Xcode frame capture of the view at every checkpoint, before its screenshot: <scenario>_<shot>.gputrace (the game
// needs MTL_CAPTURE_ENABLED=1)
internal sealed class CaptureProbe(ProbeContext context) : IProbe
{
    public string Name => "capturing a frame at each checkpoint";

    public Wait? BeforeShot(View view)
    {
        var path = Path.ChangeExtension(view.Path, ".gputrace");
        if (!DriverStats.Capture(path, 1))
        {
            context.Error($"{view.Shot}: KSA Metal could not capture a frame (does the game have MTL_CAPTURE_ENABLED=1?)");
            return null;
        }
        return Probes.Then(Probes.Until(() => !DriverStats.Capturing(), 120), () =>
        {
            if (Directory.Exists(path) || File.Exists(path))
                context.Log($"{view.Shot}: frame capture {Path.GetFileName(path)}");
            else
                context.Error($"{view.Shot}: no frame capture");
            return null;
        });
    }
}

// For a picture that goes wrong now and then (RunOptions.Catch): a screenshot that looks broken against a clean run's
// gets KSA Metal's pass dump of the next frame in caught/<scenario>_<shot>/, an image of a frame after it in caught/, and
// with CatchCapture an Xcode frame capture, at most MaxCatches a run. With Passes, the same at every checkpoint, broken
// or not: a clean run's, to compare a caught one with
internal sealed class CatchProbe(string? against, bool capture, bool everywhere, ProbeContext context) : IProbe
{
    private const int MaxCatches = 3;
    // Far smaller than the clean run's file, far more pure black (percent), or this much pure white (percent): broken
    private const double Below = 0.5, MoreBlack = 25, White = 30;
    private int _catches;

    public string Name => everywhere ? "a pass dump at every checkpoint" : "pass dumps of broken pictures";

    public Wait? AfterShot(View view)
    {
        var caught = Path.Combine(context.Dir, "caught", Path.GetFileNameWithoutExtension(view.Path));
        var image = Path.Combine(context.Dir, "caught", Path.GetFileName(view.Path));
        var gputrace = caught + ".gputrace";
        // A dump the watch started takes a moment; this view's own comes after it. The size counts once the driver has
        // written all of the file
        return Probes.Then(Probes.Until(() => !DriverStats.PassDumping() && Images.Read(view.Path) != null, 30), () =>
        {
            if (Images.Read(view.Path) == null || (everywhere ? "a clean run's passes" : LooksBroken(view.Path)) is not { } reason)
                return null;
            if (DriverStats.GpuErrors() > 0)
            {
                context.Log($"{view.Shot}: {reason}, no pass dump: the GPU has failed and refuses this game's work");
                return null;
            }
            if (!everywhere && _catches >= MaxCatches)
            {
                context.Log($"{view.Shot}: {reason}, no pass dump: limit reached");
                return null;
            }
            if (!DriverStats.PassDump(caught))
            {
                context.Log($"{view.Shot}: {reason}, no pass dump: KSA Metal could not start one");
                return null;
            }
            _catches++;
            context.Log($"{view.Shot}: {reason}, its passes in caught/ (present {DriverStats.Read()?.Present})");
            return Probes.Then(Probes.Until(() => !DriverStats.PassDumping(), 120), () =>
            {
                if (DriverStats.PassDumping())
                    context.Log($"{view.Shot}: the pass dump did not finish");
                // A capture of a frame that the GPU ran, or none: one taken after a hang replays as nonsense
                Wait? captured = capture && DriverStats.GpuErrors() == 0 && DriverStats.Capture(gputrace, 1)
                    ? Probes.Then(Probes.Until(() => !DriverStats.Capturing(), 120), () =>
                    {
                        context.Log($"{view.Shot}: frame capture {(Directory.Exists(gputrace) || File.Exists(gputrace) ? Path.GetFileName(gputrace) : "missing")}");
                        return null;
                    })
                    : null;
                // Whether the fault lasted through the dumped frame: an image of a frame after it
                return Probes.Then(captured ?? (() => true), () =>
                {
                    DriverStats.Screenshot(image);
                    return Probes.Until(() => Images.Read(image) != null, 10);
                });
            });
        });
    }

    // What is wrong with the screenshot, or null. A real view reaches about a fifth of pure white, whatever the clean run
    // shows. Against the clean run's, at the same size in pixels (at another, the bytes say nothing): far smaller, or far
    // more pure black, which a dark view has anyway
    private string? LooksBroken(string path)
    {
        var shot = Images.Read(path);
        if (shot == null || against == null)
            return null;
        if (Images.WhitePercent(shot) >= White)
            return "pure white";
        var clean = new FileInfo(Path.Combine(against, Path.GetFileName(path)));
        var reference = clean.Exists ? Images.Read(clean.FullName) : null;
        if (reference == null || shot.Width != reference.Width || shot.Height != reference.Height)
            return null;
        if (new FileInfo(path).Length < clean.Length * Below)
            return "far smaller than in the clean run";
        return PureBlackPercent(shot) > PureBlackPercent(reference) + MoreBlack ? "far more black than in the clean run" : null;
    }

    private static double PureBlackPercent(Image image)
    {
        long black = 0;
        var rgb = image.Rgb;
        for (int i = 0; i < rgb.Length; i += 3)
            if (rgb[i] <= 1 && rgb[i + 1] <= 1 && rgb[i + 2] <= 1)
                black++;
        return 100.0 * black / Math.Max(1, rgb.Length / 3);
    }
}

// A screenshot every that many seconds of the script's clock, into film/, to see the camera's moves
internal sealed class FilmProbe(double every, ProbeContext context) : IProbe
{
    private double _next;

    public string Name => $"a film frame every {every.ToString(CultureInfo.InvariantCulture)} s";

    public void Frame(double wall, double script, string phase)
    {
        if (script < _next)
            return;
        Directory.CreateDirectory(Path.Combine(context.Dir, "film"));
        DriverStats.Screenshot(Path.Combine(context.Dir, "film", FormattableString.Invariant($"{script:000.00}.png")));
        _next = script + every;
    }
}

// The camera's place every frame in trace.csv, to check its moves are smooth: where it is, where it looks, its up, and over
// the celestial it follows, its latitude, longitude and altitude and the ground under it
internal sealed class CameraLogProbe : IProbe
{
    private readonly StreamWriter _out;

    public CameraLogProbe(ProbeContext context)
    {
        _out = new StreamWriter(Path.Combine(context.Dir, "trace.csv"));
        _out.WriteLine("wall,script,phase,following,x,y,z,fx,fy,fz,ux,uy,uz,lat,lon,alt,ground");
    }

    public string Name => "the camera's place every frame";

    public void Frame(double wall, double script, string phase)
    {
        if (KSA.Program.GetHoveredCamera() is not { } camera)
            return;
        var p = camera.PositionEcl;
        var f = camera.GetForwardEcl();
        var u = camera.GetUpEcl();
        string over = ",,,";
        if (camera.Following is KSA.Celestial body)
        {
            var (lat, lon, alt) = CameraOperator.Place(body, p);
            over = FormattableString.Invariant($"{lat:F6},{lon:F6},{alt:F1},{CameraOperator.Ground(body, CameraOperator.Frame(body, lat, lon).up):F1}");
        }
        _out.WriteLine(FormattableString.Invariant(
            $"{wall:F4},{script:F4},{phase},{(camera.Following as KSA.Astronomical)?.Id},{p.X:F1},{p.Y:F1},{p.Z:F1},{f.X:F7},{f.Y:F7},{f.Z:F7},{u.X:F7},{u.Y:F7},{u.Z:F7},{over}"));
    }

    public void End() => _out.Dispose();
}
