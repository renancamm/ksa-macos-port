// The checkpoints (Director.cs): the script's clock stopped until the game catches up, @sync, the view left to settle,
// the screenshot with the probes' work around it (Probes.cs), and the measured parts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KSA;
using KSA.Atmosphere.Rendering;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

public partial class Director
{
    // Stops the script's clock until the game catches up with the game time it expects, then pauses, lets the world
    // settle and takes the screenshot
    private void StartCheckpoint(string shot)
    {
        BackToStep();
        if (Live)
            _clock.Resync();
        _shotName = shot;
        _after = After.Shot;
        _phase = Phase.CatchUp;
    }

    // @sync: like a checkpoint, without the screenshot
    private void Sync()
    {
        BackToStep();
        if (Live)
            _clock.Resync();
        _after = After.Sync;
        _phase = Phase.CatchUp;
    }

    // Back to the step's own time (this frame went a little past it), so that the game's clock catches up with the game
    // time of that moment, and the camera stands exactly where the script puts it then
    private void BackToStep()
    {
        _clock.BackTo(_stepAt);
        _camera.UpdateCamera(0);
        if (_window != null && _clock.Script >= _window.EndsAt)
            CloseWindow();
    }

    // Whether the run stops for good at this checkpoint
    private bool Holding => _hold == $"{ScenarioName}_{_shotName}";

    // Whether this checkpoint is one of the places chosen (or all are): the others only keep the clocks in step
    private bool Chosen => IsChosen(_shotName);

    private bool IsChosen(string shot) => _only?.GetValueOrDefault(ScenarioName ?? "") is not { } chosen || chosen.Contains(shot);



    private void Checkpoint(double wall)
    {
        switch (_phase)
        {
            case Phase.CatchUp:
            {
                if (!_clock.CatchUp(CatchUpSeconds))
                    return;
                double behind = _clock.GameTarget - Universe.GetElapsedSeconds();
                // The game ran past the moment the script expects: what follows plays later than written
                if (-behind > AheadLimit && !Live)
                    Error(FormattableString.Invariant(
                        $"{_clock.Script - _sectionStart:F1} s: the game's clock is {-behind:F1} s ahead of the script's"));
                if (_after != After.Shot)
                {
                    Resume();
                    return;
                }
                // Without screenshots the checkpoint ends here, its clocks in step (fast mode still measures the view), and so
                // does a checkpoint that isn't one of the places chosen
                if ((!Chosen || !TakesShots && !Fast) && !Holding)
                {
                    Resume();
                    return;
                }
                _phase = Phase.Settle;
                _retakes = 0;
                _settleStart = wall;
                _phaseEnds = wall + (Fast ? ProbeEvery : SettleSeconds);
                _phaseFrames = 0;
                _lastProbe = null;
                _probe = null;
                return;
            }
            case Phase.Settle:
                if (Fast ? !Settled(wall) : wall < _phaseEnds || ++_phaseFrames < SettleFrames)
                    return;
                if (Fast && !Holding)
                {
                    _phase = Phase.Measure;
                    return;
                }
                CaptureThenShoot(wall);
                return;
            case Phase.Measure:
                // The window opens a frame after the view settled: that frame read and compared the last probe on this
                // thread (about 150 ms at 6 MP), and its time is the frame time of the frame after it
                if (_still == null)
                {
                    _still = new Window(_shotName, double.NaN, null);
                    Opened(_still);
                    _phaseEnds = wall + FastMeasureSeconds;
                    return;
                }
                _measured = _still.Name;
                if (wall < _phaseEnds)
                    return;
                Closed(_still);
                _still = null;
                CaptureThenShoot(wall);
                return;
            case Phase.Shoot:
            {
                // The image is the next presented one: give it a few frames, and a moment more to be written
                var path = ShotPath();
                // (a view standing still reads it whole, to compare it)
                if (++_phaseFrames < 3 || (Fast ? Images.Read(path) == null : !File.Exists(path)) && _phaseFrames < 120)
                    return;
                // A view standing still: the screenshot counts once a look right after it shows the same, so that
                // something passing (a kitten's blink, which runs by the Mac's clock) gets it taken again
                if (Fast && File.Exists(path) && _retakes < MaxRetakes)
                {
                    _probe = ProbePath();
                    File.Delete(_probe);
                    DriverStats.Screenshot(_probe);
                    _phase = Phase.Confirm;
                    _phaseEnds = wall + 5;
                    return;
                }
                ShotTaken(wall);
                return;
            }
            case Phase.Confirm:
            {
                var look = _probe != null ? Images.Read(_probe) : null;
                if (look == null && wall < _phaseEnds)
                    return;
                if (_probe != null)
                    File.Delete(_probe);
                _probe = null;
                var shot = Images.Read(ShotPath());
                double changed = look == null || shot == null ? 0 : Images.Compare(shot, look).ChangedPercent;
                if (changed > StillPercent)
                {
                    _retakes++;
                    Log(FormattableString.Invariant($"{_shotName}: {changed:F2}% changed right after the screenshot: taken again"));
                    File.Delete(ShotPath());
                    if (!DriverStats.Screenshot(ShotPath()))
                    {
                        Error($"{_shotName}: the screenshot could not be taken again");
                        EndCheckpoint();
                        return;
                    }
                    _phase = Phase.Shoot;
                    _phaseFrames = 0;
                    return;
                }
                ShotTaken(wall);
                return;
            }
            case Phase.Probes:
                if (_wait != null && !_wait())
                    return;
                _wait = null;
                _afterWait();
                return;
            case Phase.NewWorld:
                // A few frames for the game to take it in, the game stopped; steps then act on the new world
                if (++_phaseFrames >= 3)
                    _phase = Phase.Play;
                return;
        }
    }

    private string ShotPath() => Path.Combine(_outDir, $"{ScenarioName}_{_shotName}.png");

    // The probes' work on the view before its screenshot (a frame capture), then the screenshot
    private void CaptureThenShoot(double wall) => WaitThen(_probes.Select(p => (Func<Wait?>)(() => p.BeforeShot(new View(ScenarioName ?? "", _shotName, ShotPath())))), Shoot);

    // The probes' work one after the other, each started when the one before is done, holding the checkpoint, then what
    // comes after them
    private void WaitThen(IEnumerable<Func<Wait?>> starts, Action next)
    {
        var left = new Queue<Func<Wait?>>(starts);
        Wait? current = null;
        _wait = () =>
        {
            while (true)
            {
                if (current != null && !current())
                    return false;
                if (left.Count == 0)
                    return true;
                current = left.Dequeue()();
            }
        };
        _afterWait = next;
        _phase = Phase.Probes;
        // No probe with work to do: on at once
        if (_wait())
        {
            _wait = null;
            _phase = Phase.Play;
            next();
        }
    }

    // A measured part opens: its event (with the game's clock and the physics' substeps then, where a timed part's numbers
    // start from), a signpost interval for traces, and the probes (the GPU profiler's frames)
    private void Opened(Window window)
    {
        var sim = ReadSim();
        _events.Add("open", ScenarioName, window.Name, window.Vehicle, sim.GameSeconds, sim.Substeps);
        DriverStats.Marker(window.Name, true);
        foreach (var probe in _probes)
            probe.Opened(ScenarioName ?? "", window.Name);
    }

    // The screenshot is there, or isn't: noted, then the probes' work on it (a pass dump) and the checkpoint's end
    private void ShotTaken(double wall)
    {
        var path = ShotPath();
        if (!File.Exists(path))
        {
            Error($"{_shotName}: no screenshot");
            EndCheckpoint();
            return;
        }
        _events.Add("shot", ScenarioName, _shotName, Path.GetFileName(path), value: _shotLimit);
        WaitThen(_probes.Select(p => (Func<Wait?>)(() => p.AfterShot(new View(ScenarioName ?? "", _shotName, path)))), EndCheckpoint);
    }

    private static string ProbePath() => Path.Combine(Path.GetTempPath(), "ksa-metal-benchmark-probe.png");

    private void Shoot()
    {
        if (!TakesShots && !Holding || !DriverStats.Screenshot(ShotPath()))
        {
            if (TakesShots)
                Log($"no screenshot {_shotName}: needs KSA Metal");
            EndCheckpoint();
            return;
        }
        _phase = Phase.Shoot;
        _phaseFrames = 0;
    }

    // Fast mode: whether the view has stopped changing, from probe screenshots a quarter of a second apart (the game is
    // paused, so what still changes is terrain and textures streaming in). Without KSA Metal's screenshots it waits a
    // few seconds.
    private bool Settled(double wall)
    {
        if (!DriverStats.Available)
            return wall >= _settleStart + 5;
        if (wall >= _settleStart + MaxSettleSeconds)
        {
            Error(FormattableString.Invariant($"{_shotName}: the view still changed after {MaxSettleSeconds:F0} s"));
            return true;
        }
        if (_probe == null)
        {
            if (wall < _phaseEnds)
                return false;
            _probe = ProbePath();
            File.Delete(_probe);
            DriverStats.Screenshot(_probe);
            _probeTaken = wall;
            return false;
        }
        // The driver writes it a few frames later
        var image = Images.Read(_probe);
        if (image == null)
        {
            if (wall < _probeTaken + 2)
                return false;
            Error($"{_shotName}: no probe screenshot to see whether the view stopped changing");
            _probe = null;
            return true;
        }
        File.Delete(_probe);
        _probe = null;
        _phaseEnds = wall + ProbeEvery;
        var last = _lastProbe;
        _lastProbe = image;
        if (last == null)
            return false;
        double changed = Images.Compare(last, image).ChangedPercent;
        if (changed > StillPercent)
            return false;
        Log(FormattableString.Invariant($"{_shotName}: still after {wall - _settleStart:F1} s ({changed:F2}% changed since the last probe)"));
        return true;
    }

    private void EndCheckpoint()
    {
        if (!Holding)
        {
            Resume();
            return;
        }
        _phase = Phase.Hold;
        // The game's interface (F2) and console stay usable
        Log($"holding at {_hold}: quit the game when done");
    }

    private void Resume()
    {
        _events.Add("checkpoint", ScenarioName, _after == After.Sync ? "@sync" : _shotName, game: Universe.GetElapsedSeconds() - _clock.GameStart,
            value: _clock.GameTarget - _clock.GameStart);
        Log(FormattableString.Invariant(
            $"{(_after == After.Sync ? "@sync" : "checkpoint " + _shotName)}: game time {Universe.GetElapsedSeconds() - _clock.GameStart:F3} s (script {_clock.GameTarget - _clock.GameStart:F3} s)"));
        _clock.Resume(Paused);
        _phase = Phase.Play;
    }

    // Closes the open measurement window. When the camera followed a vehicle at its start and follows another one
    // now, the vehicle broke up during it (a breakup changes what is on screen, so the part's numbers don't compare),
    // unless @explode blew it up
    private void CloseWindow()
    {
        if (_window == null)
            return;
        // Fast mode measures the views standing still instead, so it only watches its parts for vehicles that broke up
        if (!Fast)
            Closed(_window);
        var followed = FollowedVehicle();
        if (_window.Vehicle != null && followed != _window.Vehicle && _window.Vehicle != _vehicles.Exploded)
            Error($"{_window.Name}: '{_window.Vehicle}' broke up (the camera follows '{followed ?? "nothing"}')");
        _window = null;
    }

    // A measured part ends: its event, and the end of its signpost interval
    private void Closed(Window window)
    {
        DriverStats.Marker(window.Name, false);
        _frames?.Flush();
        _events.Add("close", ScenarioName, window.Name);
        Log($"measured {window.Name}");
    }

    private static string? FollowedVehicle() => Program.GetHoveredCamera()?.Following is Vehicle v ? v.Id : null;
}
