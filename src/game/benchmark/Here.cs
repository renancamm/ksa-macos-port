// The director's here mode (RunPlan.Play.Here): the player's own game recorded as they play it, from the Mac Port tab. No
// script and nothing of the game touched: a screenshot of the view first, then one scenario and one part, both named here,
// measured for the seconds asked with everything a run records (the store, KSA Metal's drain, the game's profilers, the
// triggers). Nothing of it runs before the player asks for it.
using System;
using System.Diagnostics;
using System.IO;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

public partial class Director
{
    private const string HereName = "here";
    private bool _hereOpen;
    // The screenshot before the part opens, so that its work is in no measured frame (2 s at most)
    private readonly Stopwatch _hereShot = new();

    private string HereShot => Path.Combine(_outDir, $"{HereName}_{HereName}.png");

    private void HereFrame()
    {
        if (!_hereShot.IsRunning)
        {
            DriverStats.Screenshot(HereShot);
            _hereShot.Start();
            return;
        }
        if (!_wall.IsRunning)
        {
            if (!Images.Written(HereShot) && _hereShot.Elapsed.TotalSeconds < 2)
                return;
            _wall.Start();
            _events.Add("scenario", HereName, text: Lineup.Name(Kind.Flight));
            var sim = ReadSim();
            _events.Add("open", HereName, HereName, (KSA.Program.GetHoveredCamera()?.Following as KSA.Astronomical)?.Id, sim.GameSeconds, sim.Substeps);
            DriverStats.Marker(HereName, true);
            _hereOpen = true;
            return;
        }
        double wall = _wall.Elapsed.TotalSeconds;
        double dt = wall - _lastWall;
        _lastWall = wall;
        if (wall >= _nextBeat)
        {
            _events.Add("heartbeat", HereName, value: Math.Round(wall, 1));
            _frames?.Flush();
            _nextBeat = wall + 1;
        }
        _row = (wall, HereName, Phase.Measure.ToString(), dt * 1000, _drain ? _present ?? DriverStats.Read() : DriverStats.Read(), ReadSim());
        _measured = HereName;
        if (wall < _plan.Play.Here)
            return;
        CloseHere();
        _events.Add("done");
        _active = false;
        _frames = null;
        CloseDrain();
        StopProfilers();
        Probes.Each(_probes, p => p.End());
        Log(FormattableString.Invariant($"recorded {wall:F1} s of the game"));
        Done = true;
    }

    // The part ends, and its screenshot counts once it is there
    private void CloseHere()
    {
        if (!_hereOpen)
            return;
        _hereOpen = false;
        _events.Add("close", HereName, HereName);
        DriverStats.Marker(HereName, false);
        if (DriverStats.Read() is { DrawableWidth: > 0 } d)
            _events.Add("drawable", HereName, text: $"{d.DrawableWidth}x{d.DrawableHeight}");
        if (File.Exists(HereShot))
            _events.Add("shot", HereName, HereName, Path.GetFileName(HereShot), value: NoiseProfile.DefaultShotLimit(Kind.Flight));
    }
}
