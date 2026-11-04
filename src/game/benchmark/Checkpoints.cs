// The checkpoints (Director.cs): the script's clock stopped until the game catches up, @sync, the view
// left to settle, the screenshot, the catch of a broken picture, the frame capture, and the measured parts.
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

namespace KsaMetal.Benchmark;

public partial class Director
{
    // Stops the script's clock until the game catches up with the game time it expects, then pauses, lets the world
    // settle and takes the screenshot
    private void StartCheckpoint(string shot)
    {
        BackToStep();
        if (Live)
            _gameTarget = Universe.GetElapsedSeconds();
        _shotName = shot;
        _after = After.Shot;
        _phase = Phase.CatchUp;
    }

    // @sync: like a checkpoint, without the screenshot
    private void Sync()
    {
        BackToStep();
        if (Live)
            _gameTarget = Universe.GetElapsedSeconds();
        _after = After.Sync;
        _phase = Phase.CatchUp;
    }

    // Back to the step's own time (this frame went a little past it), so that the game's clock catches up with the game
    // time of that moment, and the camera stands exactly where the script puts it then
    private void BackToStep()
    {
        _limitStop = double.NaN;
        _gameTarget -= GameSeconds(_stepAt, _scriptTime);
        _scriptTime = _stepAt;
        UpdateCamera(0);
        if (_window != null && _scriptTime >= _window.EndsAt)
            CloseWindow();
    }

    // Whether the run stops for good at this checkpoint
    private bool Holding => _hold == $"{ScenarioName}_{_shotName}";

    // Whether this checkpoint is one of the places chosen (or all are): the others only keep the clocks in step
    private bool Chosen => IsChosen(_shotName);

    private bool IsChosen(string shot) => _only?.GetValueOrDefault(ScenarioName ?? "") is not { } chosen || chosen.Contains(shot);



    private void Checkpoint(double wall, double dt)
    {
        switch (_phase)
        {
            case Phase.CatchUp:
            {
                double behind = _gameTarget - Universe.GetElapsedSeconds();
                if (behind > Tolerance)
                {
                    // At least the scripted speed, more while far behind: the step limit (BeforeStep) lands the game on
                    // the moment itself
                    SetSpeed(Math.Max(Math.Max(_warpTo, 1), behind / CatchUpSeconds));
                    return;
                }
                SetSpeed(0);
                // The game ran past the moment the script expects: what follows plays later than written
                if (-behind > AheadLimit && !Live)
                    Error(FormattableString.Invariant(
                        $"{_scriptTime - _sectionStart:F1} s: the game's clock is {-behind:F1} s ahead of the script's"));
                if (_after != After.Shot)
                {
                    Resume();
                    return;
                }
                // Without screenshots the checkpoint ends here, its clocks in step (fast mode still measures the view), and so
                // does a checkpoint that isn't one of the places chosen
                if ((!Chosen || !_shots && !Fast) && !Holding)
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
                // thread (about 150 ms at 6 MP), and its time is the dt of the frame after it
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
            case Phase.Capture:
            {
                if (DriverStats.Capturing() && wall < _phaseEnds)
                    return;
                var path = CapturePath();
                if (Directory.Exists(path) || File.Exists(path))
                    Log($"{_shotName}: frame capture {Path.GetFileName(path)}");
                else
                    Error($"{_shotName}: no frame capture");
                Shoot();
                return;
            }
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
            case Phase.Catch:
                // A dump the watch started takes a moment; this view's own comes after it
                if (DriverStats.PassDumping() && wall < _phaseEnds)
                    return;
                // The size counts once the driver has written all of the file
                bool written = Images.Read(ShotPath()) != null;
                if (!written && wall < _phaseEnds)
                    return;
                if ((!written ? null : _passesEverywhere ? "a clean run's passes" : LooksBroken()) is { } reason)
                {
                    if (DriverStats.GpuErrors() > 0)
                        Log($"{_shotName}: {reason}, no pass dump: the GPU has failed and refuses this game's work");
                    else if (!_passesEverywhere && _catches >= MaxCatches)
                        Log($"{_shotName}: {reason}, no pass dump: limit reached");
                    else if (DriverStats.PassDump(Path.Combine(_outDir, "caught", Path.GetFileNameWithoutExtension(ShotPath()))))
                    {
                        _catches++;
                        Log($"{_shotName}: {reason}, its passes in caught/ (present {DriverStats.Read()?.Present})");
                        _phase = Phase.CatchPasses;
                        _phaseEnds = wall + 120;
                        return;
                    }
                    else
                        Log($"{_shotName}: {reason}, no pass dump: KSA Metal could not start one");
                }
                EndCheckpoint();
                return;
            case Phase.CatchPasses:
                if (DriverStats.PassDumping() && wall < _phaseEnds)
                    return;
                if (DriverStats.PassDumping())
                    Log($"{_shotName}: the pass dump did not finish");
                // A capture of a frame that the GPU ran, or none: one taken after a hang replays as nonsense
                if (_catchCapture && DriverStats.GpuErrors() == 0 && DriverStats.Capture(CaughtCapturePath(), 1))
                {
                    _phase = Phase.CatchCapture;
                    _phaseEnds = wall + 120;
                    return;
                }
                CaughtImage(wall);
                return;
            case Phase.CatchCapture:
                if (DriverStats.Capturing() && wall < _phaseEnds)
                    return;
                Log($"{_shotName}: frame capture {(Directory.Exists(CaughtCapturePath()) || File.Exists(CaughtCapturePath()) ? Path.GetFileName(CaughtCapturePath()) : "missing")}");
                CaughtImage(wall);
                return;
            case Phase.CaughtImage:
                if (Images.Read(CaughtImagePath()) == null && wall < _phaseEnds)
                    return;
                EndCheckpoint();
                return;
            case Phase.NewWorld:
                // A few frames for the game to take it in, the game stopped; steps then act on the new world
                if (++_phaseFrames >= 3)
                    _phase = Phase.Play;
                return;
        }
    }

    private string ShotPath() => Path.Combine(_outDir, $"{ScenarioName}_{_shotName}.png");

    private string CapturePath() => Path.Combine(_outDir, $"{ScenarioName}_{_shotName}.gputrace");

    private string CaughtImagePath() => Path.Combine(_outDir, "caught", Path.GetFileName(ShotPath()));

    private string CaughtCapturePath() => Path.Combine(_outDir, "caught", Path.GetFileNameWithoutExtension(ShotPath()) + ".gputrace");

    // Whether the fault lasted through the dumped frame; in a folder of its own, not one of the run's screenshots
    private void CaughtImage(double wall)
    {
        DriverStats.Screenshot(CaughtImagePath());
        _phase = Phase.CaughtImage;
        _phaseEnds = wall + 10;
    }

    // What is wrong with the screenshot, or null. A real view reaches about a fifth of pure white, whatever the clean run shows.
    // Against the clean run's, at the same size in pixels (at another, the bytes say nothing): far smaller, or far more pure
    // black, which a dark view has anyway
    private string? LooksBroken()
    {
        var shot = Images.Read(ShotPath());
        if (shot == null)
            return null;
        if (Images.WhitePercent(shot) >= CatchWhite)
            return "pure white";
        var clean = new FileInfo(Path.Combine(_catchAgainst!, Path.GetFileName(ShotPath())));
        var reference = clean.Exists ? Images.Read(clean.FullName) : null;
        if (reference == null || shot.Width != reference.Width || shot.Height != reference.Height)
            return null;
        if (new FileInfo(ShotPath()).Length < clean.Length * CatchBelow)
            return "far smaller than in the clean run";
        return PureBlackPercent(shot) > PureBlackPercent(reference) + CatchMoreBlack ? "far more black than in the clean run" : null;
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

    // With RunOptions.Capture, an Xcode frame capture of the view (one frame, the game paused) before the screenshot
    private void CaptureThenShoot(double wall)
    {
        if (_capture)
        {
            if (DriverStats.Capture(CapturePath(), 1))
            {
                _phase = Phase.Capture;
                _phaseEnds = wall + 120;
                return;
            }
            Error($"{_shotName}: KSA Metal could not capture a frame (does the game have MTL_CAPTURE_ENABLED=1?)");
        }
        Shoot();
    }

    // A measured part opens: its event (with the game's clock and the physics' substeps then, where a timed part's numbers
    // start from), a signpost interval for traces, and the GPU profiler's frames with RunOptions.Profile
    private void Opened(Window window)
    {
        var sim = ReadSim();
        _events.Add("open", ScenarioName, window.Name, window.Vehicle, sim.GameSeconds, sim.Substeps);
        DriverStats.Marker(window.Name, true);
        if (_profileFrames > 0 && !DriverStats.ProfileFrames($"{ScenarioName}/{window.Name}", _profileFrames))
        {
            Error($"{window.Name}: KSA Metal's GPU profiler is off (does the game have KSA_MTL_GPU_PROFILE=<csv>?)");
            _profileFrames = 0;
        }
    }

    // The screenshot is there, or isn't: noted, then the catch of a broken picture or the checkpoint's end
    private void ShotTaken(double wall)
    {
        var path = ShotPath();
        if (File.Exists(path))
            _events.Add("shot", ScenarioName, _shotName, Path.GetFileName(path), value: _shotLimit);
        else
            Error($"{_shotName}: no screenshot");
        if ((_catchAgainst != null || _passesEverywhere) && File.Exists(path))
        {
            _phase = Phase.Catch;
            _phaseEnds = wall + 30;
            return;
        }
        EndCheckpoint();
    }

    private static string ProbePath() => Path.Combine(Path.GetTempPath(), "ksa-metal-benchmark-probe.png");

    private void Shoot()
    {
        if (!_shots && !Holding || !DriverStats.Screenshot(ShotPath()))
        {
            if (_shots)
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
        _events.Add("checkpoint", ScenarioName, _after == After.Sync ? "@sync" : _shotName, game: Universe.GetElapsedSeconds() - _gameStart,
            value: _gameTarget - _gameStart);
        Log(FormattableString.Invariant(
            $"{(_after == After.Sync ? "@sync" : "checkpoint " + _shotName)}: game time {Universe.GetElapsedSeconds() - _gameStart:F3} s (script {_gameTarget - _gameStart:F3} s)"));
        SetSpeed(Paused ? 0 : SpeedAt(_scriptTime));
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
        if (_window.Vehicle != null && followed != _window.Vehicle && _window.Vehicle != _exploded)
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
