// The checkpoints (Benchmark.cs): the script's clock stopped until the game catches up, @sync and @time, the view
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

namespace KsaMetalBenchmark;

public partial class Benchmark
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
        double over = _scriptTime - _stepAt;
        _gameTarget -= GameSeconds(_stepAt, _scriptTime);
        _scriptTime = _stepAt;
        if (Program.MainViewport.OrbitController.View is { } view)
            view.Azimuth -= _rotateRadPerSecond * over;
        if (_rig != null)
            _rig.Spin -= _rotateRadPerSecond * 180 / Math.PI * over;
        UpdateCamera(0);
        if (_window != null && _scriptTime >= _window.EndsAt)
            CloseWindow();
    }

    // Whether the run stops for good at this checkpoint
    private bool Holding => _hold == $"{_sectionResult?.Name}_{_shotName}";

    // Whether this checkpoint is one of the places chosen (or all are): the others only keep the clocks in step
    private bool Chosen => IsChosen(_shotName);

    private bool IsChosen(string shot) => _only?.GetValueOrDefault(_sectionResult?.Name ?? "") is not { } chosen || chosen.Contains(shot);

    // @time: moves the game's clock forward to that many seconds since the start, the script's clock stopped meanwhile (a
    // checkpoint without a screenshot), so that a section looks the same (its clouds drift with the game's time) whether
    // the sections before it played or not
    private bool SkipTo(double seconds)
    {
        BackToStep();
        double target = _gameStart + seconds;
        if (_gameTarget > target + Tolerance)
        {
            Log(FormattableString.Invariant($"@time {seconds}: the script's game time is already {_gameTarget - _gameStart:F1} s"));
            return false;
        }
        _gameTarget = target;
        _after = After.Time;
        _phase = Phase.CatchUp;
        return true;
    }

    private void Checkpoint(double wall, double dt)
    {
        switch (_phase)
        {
            case Phase.CatchUp:
            {
                double behind = _gameTarget - Universe.GetElapsedSeconds();
                if (behind > Tolerance)
                {
                    // The game takes a new speed a frame late: the frame under way still runs at the speed sent last,
                    // which at a time-lapse's speed covers minutes
                    double frame = Math.Max(dt, 1.0 / 240);
                    double left = behind - (double.IsNaN(_speedSent) ? 0 : _speedSent) * frame;
                    if (left <= Tolerance)
                    {
                        SetSpeed(0);
                        return;
                    }
                    // Fast while far behind (at least the scripted speed), then half of what is left every frame
                    double warp = Math.Max(_warpTo, 1);
                    // A jump of @time covers half of what is left every quarter of a second or so
                    double pace = _after == After.Time ? 0.25 : 2;
                    SetSpeed(left > warp * frame * 2 ? Math.Max(warp, left / pace) : left / (2 * frame));
                    return;
                }
                SetSpeed(0);
                // The game ran past the moment the script expects: what follows plays later than written
                if (-behind > AheadLimit && !Live)
                    _sectionResult?.Errors.Add(FormattableString.Invariant(
                        $"{_scriptTime - _sectionStart:F1} s: the game's clock is {-behind:F1} s ahead of the script's"));
                if (_after != After.Shot)
                {
                    if (_after == After.Time)
                        ResetClouds();
                    Resume();
                    return;
                }
                // Without screenshots the checkpoint ends here, its clocks in step (fast mode still measures the view), and so
                // does a checkpoint that isn't one of the places chosen
                if ((!Chosen || !_shots && (!_fast || Live)) && !Holding)
                {
                    Resume();
                    return;
                }
                if (_fast && !Live)
                {
                    ResetClouds(detailOnly: true);
                    ResetSeaFrame();
                    OpenEyes();
                }
                _phase = Phase.Settle;
                _settleStart = wall;
                _phaseEnds = wall + (_fast && !Live ? ProbeEvery : SettleSeconds);
                _phaseFrames = 0;
                _lastProbe = null;
                _probe = null;
                return;
            }
            case Phase.Settle:
                if (_fast && !Live ? !Settled(wall) : wall < _phaseEnds || ++_phaseFrames < SettleFrames)
                    return;
                if (_fast && !Live && !Holding)
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
                    _still = new Window(_shotName, double.NaN);
                    Opened(_still);
                    _phaseEnds = wall + FastMeasureSeconds;
                    return;
                }
                _still.Add(dt * 1000, DriverStats.Read());
                if (wall < _phaseEnds)
                    return;
                DriverStats.Marker(_still.Name, false);
                _sectionResult?.Windows.Add(_still.Summarize());
                _results?.Write(Path.Combine(_outDir, _resultsFile));
                Log($"measured {_still.Name}: {_still.Frames} frames");
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
                    _sectionResult?.Errors.Add($"{_shotName}: no frame capture");
                Shoot();
                return;
            }
            case Phase.Shoot:
            {
                // The image is the next presented one: give it a few frames, and a moment more to be written
                var path = ShotPath();
                if (++_phaseFrames < 3 || !File.Exists(path) && _phaseFrames < 120)
                    return;
                if (File.Exists(path))
                    _sectionResult?.Screenshots.Add(_shotName);
                else
                    _sectionResult?.Errors.Add($"{_shotName}: no screenshot");
                if ((_catchAgainst != null || _passesEverywhere) && File.Exists(path))
                {
                    _phase = Phase.Catch;
                    _phaseEnds = wall + 30;
                    return;
                }
                EndCheckpoint();
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

    private string ShotPath() => Path.Combine(_outDir, $"{_sectionResult?.Name}_{_shotName}.png");

    private string CapturePath() => Path.Combine(_outDir, $"{_sectionResult?.Name}_{_shotName}.gputrace");

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

    // With KSA_BENCH_CAPTURE, an Xcode frame capture of the view (one frame, the game paused) before the screenshot
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
            _sectionResult?.Errors.Add($"{_shotName}: KSA Metal could not capture a frame (does the game have MTL_CAPTURE_ENABLED=1?)");
        }
        Shoot();
    }

    // A measured part opens: a signpost interval for traces, and the GPU profiler's frames with KSA_BENCH_PROFILE
    private void Opened(Window window)
    {
        DriverStats.Marker(window.Name, true);
        if (_profileFrames > 0 && !DriverStats.ProfileFrames($"{_sectionResult?.Name}/{window.Name}", _profileFrames))
        {
            _sectionResult?.Errors.Add($"{window.Name}: KSA Metal's GPU profiler is off (does the game have KSA_MTL_GPUPROF=<csv>?)");
            _profileFrames = 0;
        }
    }

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
            _sectionResult?.Errors.Add(FormattableString.Invariant($"{_shotName}: the view still changed after {MaxSettleSeconds:F0} s"));
            return true;
        }
        if (_probe == null)
        {
            if (wall < _phaseEnds)
                return false;
            _probe = Path.Combine(Path.GetTempPath(), "ksa-metal-benchmark-probe.png");
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
            _sectionResult?.Errors.Add($"{_shotName}: no probe screenshot to see whether the view stopped changing");
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
        _results!.Write(Path.Combine(_outDir, _resultsFile));
    }

    private void Resume()
    {
        Log(FormattableString.Invariant(
            $"{_after switch { After.Time => "@time", After.Sync => "@sync", _ => "checkpoint " + _shotName }}: game time {Universe.GetElapsedSeconds() - _gameStart:F3} s (script {_gameTarget - _gameStart:F3} s)"));
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
        if (!_fast || Live)
        {
            DriverStats.Marker(_window.Name, false);
            _sectionResult?.Windows.Add(_window.Summarize());
            _results?.Write(Path.Combine(_outDir, _resultsFile));
            Log($"measured {_window.Name}: {_window.Frames} frames");
        }
        var followed = FollowedVehicle();
        if (_window.Vehicle != null && followed != _window.Vehicle && _window.Vehicle != _exploded)
            _sectionResult?.Errors.Add($"{_window.Name}: '{_window.Vehicle}' broke up (the camera follows '{followed ?? "nothing"}')");
        _window = null;
    }

    private static string? FollowedVehicle() => Program.GetHoveredCamera()?.Following is Vehicle v ? v.Id : null;
}
