// The tools for work on the port, plugged into a run (RunPlan turns them on, benchmark.sh's program gives the game
// what each needs from KSA Metal): each one watches the run's parts, its checkpoints or its frames, and none of them
// changes how the run plays. A probe that works on a checkpoint's view (a frame capture before the screenshot, a
// trigger's pass dump after it) holds the checkpoint until it is done: it returns a wait, which the director asks every frame.
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
    // The probes this plan turns on
    public static List<IProbe> For(RunPlan plan, ProbeContext context)
    {
        var probes = new List<IProbe>();
        if (plan.Record.Capture)
            probes.Add(new CaptureProbe(context));
        if (plan.Record.Film > 0)
            probes.Add(new FilmProbe(plan.Record.Film, context));
        if (plan.Record.CameraLog)
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

// The camera's place every frame in camera-log.csv, to check its moves are smooth: where it is, where it looks, its up, and over
// the celestial it follows, its latitude, longitude and altitude and the ground under it
internal sealed class CameraLogProbe : IProbe
{
    private readonly StreamWriter _out;

    public CameraLogProbe(ProbeContext context)
    {
        _out = new StreamWriter(Path.Combine(context.Dir, "camera-log.csv"));
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
