// The benchmark's director: plays the benchmark's script in one game session and measures it.
//
// KSA Metal Hooks start it (src/hooks/BenchmarkRun.cs): when the game starts with the variables below set, which
// benchmark.sh in the port folder sets, or with the same options from the Mac Port tab in the game's settings.
//   KSA_BENCH_SCRIPT  the script (benchmark.txt)
//   KSA_BENCH_OUT     the folder for the results (results.json, frames.csv) and the screenshots
//   KSA_BENCH_ONLY    optional: what to play (space separated): sections, or places as <section>_<shot> (the section
//                     up to that checkpoint, its other checkpoints only keeping the clocks in step)
//   KSA_BENCH_FAST    optional: fast mode (below)
//   KSA_BENCH_NO_SHOTS optional: no screenshots (the checkpoints still keep the game's clock in step)
//   KSA_BENCH_HOLD    optional: <section>_<shot>: stop at that checkpoint for good, the game paused, until it is quit
//   KSA_BENCH_RUN     optional: a suffix for the results' file names (results-2.json, frames-2.csv), for repeats
//   KSA_BENCH_PROFILE optional: profile that many frames of every measured part with KSA Metal's GPU profiler (the
//                     game needs KSA_MTL_GPUPROF=<csv>)
//   KSA_BENCH_CAPTURE optional: an Xcode GPU frame capture of the view at every checkpoint, <section>_<shot>.gputrace
//                     (the game needs MTL_CAPTURE_ENABLED=1)
//   KSA_BENCH_WAIT    optional: a file to wait for in the game before playing (up to a minute), so that a tool the
//                     script starts once the game is up (an Instruments trace) sees the whole run
//   KSA_BENCH_TRACE   optional: write the camera's place every frame to trace.csv, to check its moves are smooth
//   KSA_BENCH_FILM    optional: a screenshot every that many seconds of the script's clock, into film/, to see moves
//   KSA_BENCH_EXPLORE optional: no script, the explore mode's console command instead (Explore.cs)
//   KSA_BENCH_END     optional: "stay": the game goes on when the script ends (Done), instead of quitting
//   KSA_BENCH_START   optional: "game": the run plays in a game that was running already (no loading time)
// Without the first two there is nothing to play. While it plays, it touches the file "heartbeat" in the results' folder
// every second, so the script can tell a game that hangs from one that is busy. Every frame goes into frames.csv, and
// every measured part is a signpost interval (KSA Metal's ksaMtlMarker), so an Instruments trace shows the parts.
//
// The script is a series of sections. "section <name> [needs <celestial>...]" starts one; a section whose celestials
// the loaded system lacks is skipped. Each section is a list of "<seconds> <command>" steps timed from its start. A
// command is a console command or one of the directives in Directives.cs. Other lines are comments.
//
// Two clocks run the script. The script's clock counts the seconds of play: the steps, the camera's moves and the
// measured parts follow it. The game's clock is the simulation's, which @warp speeds up or pauses. The script says
// where the game's clock should be at every moment, but a slower Mac falls behind it (the game slows the simulation
// down when it cannot keep up). Every @shot is a checkpoint that puts the two back in step: the simulation eases to a
// stop as the script reaches it, the script's clock stops, the simulation catches up with the game time the script
// expects and pauses there, the screenshot is taken once the world has settled, and both clocks go on, the simulation
// easing back to its speed. So every Mac takes its screenshots at the same moments of the game.
//
// Fast mode skips the way between in the sections of the camera's flight alone (@key, @shot and @measure steps only):
// the game stays paused all along them, and the script's clock jumps from one chosen checkpoint to the next (the light
// and the clouds stay as they were at the start). Sections with vehicles play as written, as in the cinematic run. At
// each checkpoint the mod waits until the view stops changing (terrain and textures streaming in after a jump),
// measures a few seconds of the view standing there (instead of the @measure parts) and takes the screenshot. Its
// numbers compare only with other fast runs.
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
    // How close a checkpoint gets to the game time it waits for (game seconds), how far ahead of it the game may be
    // (the clock only goes forward), and how long it lets the world settle (terrain and textures streaming in) before
    // its screenshot
    private const double Tolerance = 0.02;
    private const double AheadLimit = 0.5;
    private const double SettleSeconds = 1.0;
    private const int SettleFrames = 10;
    // How long the simulation takes to ease to a stop at a checkpoint, and back to its speed after it
    private const double ShotEase = 0.75;
    // While the script warps, how many seconds the game takes to make up what it fell behind
    private const double WarpCatchUp = 0.5;
    // Fast mode: how often it looks whether the view still changes (seconds), how much of it may change between two
    // looks to count as still (percent of its blocks, see Images.cs), the longest it waits, and how long it measures
    private const double ProbeEvery = 0.5;
    private const double StillPercent = 0.5;
    private const double MaxSettleSeconds = 20;
    private const double FastMeasureSeconds = 2.0;

    // A step: its time in the section, its command and the command's words, and its line in the script
    private record Step(double At, string Command, int Line)
    {
        public string[] Words { get; } = Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private class Section
    {
        public string Name = "";
        public string[] Needs = Array.Empty<string>();
        public readonly List<Step> Steps = new();
        public double Length => Steps.Count > 0 ? Steps.Max(s => s.At) : 0;
        // Only the camera's flight: no vehicles, nothing that needs the game's clock
        public bool CameraOnly => Steps.All(s => s.Words[0] is "@key" or "@shot" or "@measure");
    }

    private enum Phase { Play, CatchUp, Settle, Measure, Capture, Shoot, Hold }

    // What a catch-up of the game's clock leads to: a checkpoint's screenshot, or the end of a jump of @time
    private enum After { Shot, Time }

    private readonly List<Section> _sections = new();
    private readonly Stopwatch _wall = new();
    private string _outDir = "";
    // KSA_BENCH_ONLY: the sections to play, each with the checkpoints chosen in it (null: all of them)
    private Dictionary<string, HashSet<string>?>? _only;
    private string _resultsFile = "results.json";
    private FrameLog? _frames;
    private uint _profileFrames;
    private bool _capture;
    private bool _active;
    private Results? _results;
    private bool _fast, _shots = true;
    private string? _hold;
    private double _nextBeat;

    // The section playing, its steps and where it started on the script's clock
    private int _section = -1;
    private int _next;
    private double _sectionStart;
    private SectionResult? _sectionResult;

    private double _lastWall;
    private double _scriptTime;
    // The game time at the start and the game time the script expects now
    private double _gameStart;
    private double _gameTarget;
    // The simulation speed the script asks for: @warp moves it from one speed to another over some seconds
    private double _warpFrom = 1, _warpTo = 1, _warpStart, _warpSeconds;
    private double _speedSent = double.NaN;
    // The checkpoints' times on the script's clock, and the time of the step running
    private readonly List<double> _shotTimes = new();
    private double _stepAt;
    private Phase _phase = Phase.Play;
    private After _after;
    private string _shotName = "";
    private double _phaseEnds;
    private int _phaseFrames;
    // Fast mode's wait for a still view: when it started, the last probe screenshot, and the one being taken
    private double _settleStart;
    private Image? _lastProbe;
    private string? _probe;
    private double _probeTaken;

    private Window? _window;
    // Fast mode's measurement of the view standing at a checkpoint
    private Window? _still;
    // The vehicle @explode blew up
    private string? _exploded;
    private StreamWriter? _trace;
    private double _filmEvery, _nextFilm;
    private string? _waitFor;
    private readonly Stopwatch _waiting = new();

    private readonly Func<string, string?> _option;

    // Whether the script has ended (played to its end, or stopped)
    public bool Done { get; private set; }

    // Starts a run with these options (the variables above, by name), or null when there is nothing to play. The
    // caller calls OnFrame after every frame of the game.
    public static Benchmark? Begin(Func<string, string?> option)
    {
        var benchmark = new Benchmark(option);
        return benchmark._active || benchmark._exploring ? benchmark : null;
    }

    private Benchmark(Func<string, string?> option)
    {
        _option = option;
        var script = _option("KSA_BENCH_SCRIPT");
        _outDir = _option("KSA_BENCH_OUT") ?? "";
        if (string.IsNullOrEmpty(_outDir))
            return;
        StartExplore();
        if (_exploring || string.IsNullOrEmpty(script))
            return;
        if (!File.Exists(script))
        {
            Log($"script {script} not found");
            return;
        }
        _fast = _option("KSA_BENCH_FAST") is { Length: > 0 };
        _shots = _option("KSA_BENCH_NO_SHOTS") is not { Length: > 0 };
        _hold = _option("KSA_BENCH_HOLD") is { Length: > 0 } hold ? hold : null;
        var lines = File.ReadAllLines(script);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0] == "section")
            {
                int needs = Array.IndexOf(words, "needs");
                _sections.Add(new Section { Name = words[1], Needs = needs > 0 ? words[(needs + 1)..] : Array.Empty<string>() });
                continue;
            }
            var space = line.IndexOf(' ');
            if (space > 0 && _sections.Count > 0 &&
                double.TryParse(line[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                _sections[^1].Steps.Add(new Step(at, line[(space + 1)..].Trim(), i + 1));
        }
        // Steps with the same time run in the order they are written
        foreach (var section in _sections)
        {
            var ordered = section.Steps.OrderBy(s => s.At).ThenBy(s => s.Line).ToList();
            section.Steps.Clear();
            section.Steps.AddRange(ordered);
        }
        var only = _option("KSA_BENCH_ONLY");
        if (!string.IsNullOrWhiteSpace(only))
        {
            _only = new Dictionary<string, HashSet<string>?>();
            foreach (var name in only.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (_sections.Any(s => s.Name == name))
                {
                    _only[name] = null;
                    continue;
                }
                var section = _sections.FirstOrDefault(s => name.StartsWith(s.Name + "_", StringComparison.Ordinal) &&
                    s.Steps.Any(step => step.Words is ["@shot", var shot, ..] && $"{s.Name}_{shot}" == name));
                if (section == null)
                {
                    Log($"no section or place '{name}' in the script");
                    continue;
                }
                if (!_only.TryGetValue(section.Name, out var shots))
                    _only[section.Name] = shots = new HashSet<string>();
                shots?.Add(name[(section.Name.Length + 1)..]);
            }
        }
        _resultsFile = $"results{_option("KSA_BENCH_RUN")}.json";
        uint.TryParse(_option("KSA_BENCH_PROFILE"), out _profileFrames);
        _capture = _option("KSA_BENCH_CAPTURE") is { Length: > 0 };
        _waitFor = _option("KSA_BENCH_WAIT") is { Length: > 0 } wait ? wait : null;
        Directory.CreateDirectory(_outDir);
        _frames = new FrameLog(Path.Combine(_outDir, $"frames{_option("KSA_BENCH_RUN")}.csv"));
        if (!string.IsNullOrEmpty(_option("KSA_BENCH_TRACE")))
        {
            _trace = new StreamWriter(Path.Combine(_outDir, "trace.csv"));
            _trace.WriteLine("wall,script,phase,following,x,y,z,fx,fy,fz,ux,uy,uz,lat,lon,alt,ground");
        }
        if (double.TryParse(_option("KSA_BENCH_FILM"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out _filmEvery) && _filmEvery > 0)
            Directory.CreateDirectory(Path.Combine(_outDir, "film"));
        _results = new Results(DriverStats.Available) { Mode = _fast ? "fast" : "cinematic", Shots = _shots };
        _active = true;
        Log($"{_sections.Count} sections, {_results.Mode}{(_shots ? "" : ", no screenshots")}{(_hold != null ? $", holding at {_hold}" : "")}, " +
            $"GPU times {(DriverStats.Available ? "available" : "not available")}{(_profileFrames > 0 ? $", profiling {_profileFrames} frames a part" : "")}" +
            $"{(_capture ? ", capturing a frame at each checkpoint" : "")}");
    }

    public void OnFrame()
    {
        if (_exploring)
            ExploreFrame();
        if (!_active)
            return;
        if (!_wall.IsRunning && _waitFor != null)
        {
            // Up in the game: the heartbeat tells the script, which starts its tool and then makes the file
            if (!_waiting.IsRunning || _waiting.Elapsed.TotalSeconds >= _nextBeat)
            {
                File.WriteAllText(Path.Combine(_outDir, "heartbeat"), "waiting 0\n");
                _nextBeat = _waiting.Elapsed.TotalSeconds + 1;
            }
            _waiting.Start();
            if (!File.Exists(_waitFor) && _waiting.Elapsed.TotalSeconds < 60)
                return;
            Log(FormattableString.Invariant($"waited {_waiting.Elapsed.TotalSeconds:F1} s for {Path.GetFileName(_waitFor)}"));
            _waitFor = null;
            _nextBeat = 0;
        }
        if (!_wall.IsRunning)
            Start();
        double wall = _wall.Elapsed.TotalSeconds;
        double dt = wall - _lastWall;
        _lastWall = wall;
        if (wall >= _nextBeat)
        {
            File.WriteAllText(Path.Combine(_outDir, "heartbeat"),
                FormattableString.Invariant($"{_sectionResult?.Name} {_scriptTime - _sectionStart:F1}\n"));
            _nextBeat = wall + 1;
        }
        if (_phase == Phase.Hold)
            return;
        // The game's messages stay in its log, off the screen
        Program.ConsoleWindow.ClearConsole();
        _frames?.Add(_scriptTime - _sectionStart, _sectionResult?.Name, _window?.Name ?? _still?.Name, _phase.ToString(), dt * 1000,
            DriverStats.Read());

        if (_phase != Phase.Play)
        {
            UpdateCamera(0);
            Checkpoint(wall, dt);
            return;
        }
        if (Paused)
        {
            // Fast mode: straight on to the next checkpoint
            dt = NextStop() - _scriptTime;
            _scriptTime += dt;
        }
        else
        {
            _scriptTime += dt;
            double speed = SpeedAt(_scriptTime);
            _gameTarget += speed * dt;
            // The game keeps a little under the speed it is asked for (about 12% here), which adds up to hours over a
            // time-lapse, and takes a new speed a frame late, so a fast warp easing down overshoots by minutes: while
            // the script warps, it asks for more as the game falls behind and for less as it gets ahead
            if (speed > 2)
                speed = Math.Max(speed + (_gameTarget - Universe.GetElapsedSeconds()) / WarpCatchUp, 0);
            SetSpeed(speed);
            if (!_fast)
                _window?.Add(dt * 1000, DriverStats.Read());
        }
        if (_window != null && _scriptTime >= _window.EndsAt)
            CloseWindow();
        UpdateCamera(dt);
        Trace(wall);
        if (_filmEvery > 0 && _scriptTime >= _nextFilm)
        {
            DriverStats.Screenshot(Path.Combine(_outDir, "film", FormattableString.Invariant($"{_scriptTime:000.00}.png")));
            _nextFilm = _scriptTime + _filmEvery;
        }

        while (_phase == Phase.Play && _active)
        {
            if (_section < 0 || _next >= _sections[_section].Steps.Count)
            {
                if (_section >= 0 && _scriptTime < _sectionStart + _sections[_section].Length)
                    break;
                NextSection();
                continue;
            }
            var step = _sections[_section].Steps[_next];
            if (_sectionStart + step.At > _scriptTime)
                break;
            _next++;
            _stepAt = _sectionStart + step.At;
            Run(step);
        }
    }

    private void Trace(double wall)
    {
        if (_trace == null || Program.GetHoveredCamera() is not { } camera)
            return;
        var p = camera.PositionEcl;
        var f = camera.GetForwardEcl();
        var u = camera.GetUpEcl();
        // Where the camera is over the celestial it follows, and the ground under it
        string over = ",,,";
        if (camera.Following is Celestial body)
        {
            var (lat, lon, alt) = Place(body, p);
            over = FormattableString.Invariant($"{lat:F6},{lon:F6},{alt:F1},{Ground(body, Frame(body, lat, lon).up):F1}");
        }
        _trace.WriteLine(FormattableString.Invariant(
            $"{wall:F4},{_scriptTime:F4},{_phase},{(camera.Following as Astronomical)?.Id},{p.X:F1},{p.Y:F1},{p.Z:F1},{f.X:F7},{f.Y:F7},{f.Z:F7},{u.X:F7},{u.Y:F7},{u.Z:F7},{over}"));
    }

    // The simulation speed the script asks for at a time of its clock: @warp's, eased to a stop around checkpoints
    private double SpeedAt(double t)
    {
        double warp = _warpTo;
        if (t < _warpStart + _warpSeconds)
        {
            double u = Smooth(Math.Clamp((t - _warpStart) / _warpSeconds, 0, 1));
            // Between two speeds, by the same factor every second (from 1 to 100 passes 10 halfway)
            warp = _warpFrom > 0 && _warpTo > 0
                ? Math.Exp(Math.Log(_warpFrom) + (Math.Log(_warpTo) - Math.Log(_warpFrom)) * u)
                : _warpFrom + (_warpTo - _warpFrom) * u;
        }
        foreach (double shot in _shotTimes)
        {
            double away = Math.Abs(t - shot);
            if (away < ShotEase)
                warp *= Smooth(away / ShotEase);
        }
        return warp;
    }

    // Whether the game stays paused: fast mode, in a section of the camera's flight alone (or before the first section)
    private bool Paused => _fast && (_section < 0 || _sections[_section].CameraOnly);

    // Fast mode: where the camera's flight stops next on the script's clock: its next chosen checkpoint, or its end
    private double NextStop()
    {
        if (_section < 0)
            return _scriptTime;
        var section = _sections[_section];
        for (int i = _next; i < section.Steps.Count; i++)
        {
            if (section.Steps[i].Words is ["@shot", var shot, ..] && IsChosen(shot))
                return _sectionStart + section.Steps[i].At;
        }
        return _sectionStart + section.Length;
    }

    private void Warp(double speed, double seconds)
    {
        _warpFrom = SpeedAt(_scriptTime);
        _warpTo = speed;
        _warpStart = _scriptTime;
        _warpSeconds = seconds;
    }

    private void Start()
    {
        _wall.Start();
        // A run in a running game has no loading of its own
        if (_option("KSA_BENCH_START") != "game")
        {
            _results!.LoadSeconds = Math.Round((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds - _waiting.Elapsed.TotalSeconds, 1);
            Log(FormattableString.Invariant($"first in-game frame after {_results.LoadSeconds:F1} s"));
        }
        // The scene alone, without the game's interface (F2)
        Program.DrawUI = false;
        _gameStart = _gameTarget = Universe.GetElapsedSeconds();
        _warpFrom = _warpTo = Universe.GetSimulationSpeed();
        // Fast mode: paused until a section with vehicles
        if (_fast)
            SetSpeed(0);
        foreach (var section in _sections)
        {
            var missing = section.Needs.Where(id => Universe.CurrentSystem?.All.TryGet(id, out Astronomical? _) != true).ToList();
            if (missing.Count > 0)
                Log($"skipping section {section.Name}: no {string.Join(", ", missing)} in this system");
            else if (_only != null && !_only.ContainsKey(section.Name))
                Log($"skipping section {section.Name}");
            else
                continue;
            section.Steps.Clear();
            section.Name = "";
        }
        _sections.RemoveAll(s => s.Name == "");
        // A section with chosen places ends at the last of them
        foreach (var section in _sections)
        {
            if (_only?.GetValueOrDefault(section.Name) is not { } chosen)
                continue;
            double end = section.Steps.Where(s => s.Words is ["@shot", var shot, ..] && chosen.Contains(shot))
                .Select(s => s.At).DefaultIfEmpty(section.Length).Max();
            section.Steps.RemoveAll(s => s.At > end);
            Log(FormattableString.Invariant($"section {section.Name}: {string.Join(", ", chosen)}, until {end:F1} s"));
        }
        // The sections play one after the other, each as long as its last step's time
        double start = 0;
        foreach (var section in _sections)
        {
            _shotTimes.AddRange(section.Steps.Where(s => s.Words[0] == "@shot").Select(s => start + s.At));
            start += section.Length;
        }
        BuildPaths();
    }

    // Ends the section playing and starts the next one, or ends the run after the last
    private void NextSection()
    {
        EndSection();
        if (_section >= 0)
            _sectionStart += _sections[_section].Length;
        _section++;
        _next = 0;
        if (_section >= _sections.Count)
        {
            _active = false;
            _trace?.Dispose();
            _frames?.Dispose();
            _results!.Completed = true;
            _results.Write(Path.Combine(_outDir, _resultsFile));
            Log(FormattableString.Invariant($"done after {_wall.Elapsed.TotalSeconds:F1} s"));
            // The camera's field of view as it was
            EndRig();
            Done = true;
            if (_option("KSA_BENCH_END") != "stay")
                Program.TerminalInterface.Execute("exit");
            return;
        }
        _sectionResult = new SectionResult(_sections[_section].Name);
        _results!.Sections.Add(_sectionResult);
        Log(FormattableString.Invariant($"section {_sectionResult.Name} (game time {Universe.GetElapsedSeconds():F1} s, {_wall.Elapsed.TotalSeconds:F1} s into the run)"));
    }

    // Ends the run before the script's end (the player stopped it): the results say why, and that it did not complete
    public void Stop(string why)
    {
        if (Done)
            return;
        _sectionResult?.Errors.Add(why);
        EndSection();
        _active = false;
        _trace?.Dispose();
        _frames?.Dispose();
        _results?.Write(Path.Combine(_outDir, _resultsFile));
        Log($"stopped: {why}");
        EndRig();
        Done = true;
    }

    private void EndSection()
    {
        if (_sectionResult == null)
            return;
        CloseWindow();
        if (DriverStats.Read() is { DrawableWidth: > 0 } d)
            _results!.Drawable = $"{d.DrawableWidth}x{d.DrawableHeight}";
        _results!.Write(Path.Combine(_outDir, _resultsFile));
        _sectionResult = null;
    }

    private void Run(Step step)
    {
        bool ok;
        try
        {
            ok = step.Command.StartsWith('@') ? Directive(step) : Console(step);
        }
        catch (Exception e)
        {
            Log($"'{step.Command}' threw {e.GetType().Name}: {e.Message}");
            ok = false;
        }
        double t = _stepAt - _sectionStart;
        Log(FormattableString.Invariant($"{_sectionResult?.Name} {t:F1} s: '{step.Command}' -> {(ok ? "ok" : "failed")}"));
        if (!ok)
            _sectionResult?.Errors.Add(FormattableString.Invariant($"{t:F1} s: '{step.Command}' failed"));
    }

    // A console command. Those that move the camera elsewhere end the directives' hold on it.
    private bool Console(Step step)
    {
        if (step.Words[0] is "follow" or "camera" or "control" or "goto" or "altitude" or "lookat" or "moveto")
            ReleaseCamera();
        return Program.TerminalInterface.Execute(step.Command);
    }

    // Asks the game for a simulation speed (0 pauses it)
    private void SetSpeed(double speed)
    {
        if (speed == _speedSent)
            return;
        _speedSent = speed;
        InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(speed) });
    }

    // Stops the script's clock until the game catches up with the game time it expects, then pauses, lets the world
    // settle and takes the screenshot
    private void StartCheckpoint(string shot)
    {
        // Back to the checkpoint's own time (this frame went a little past it), so the camera stands exactly where the
        // script puts it then
        double over = _scriptTime - _stepAt;
        _scriptTime = _stepAt;
        _gameTarget -= SpeedAt(_stepAt) * over;
        if (Program.MainViewport.OrbitController.View is { } view)
            view.Azimuth -= _rotateRadPerSecond * over;
        if (_rig != null)
            _rig.Spin -= _rotateRadPerSecond * 180 / Math.PI * over;
        UpdateCamera(0);
        if (_window != null && _scriptTime >= _window.EndsAt)
            CloseWindow();
        _shotName = shot;
        _after = After.Shot;
        _phase = Phase.CatchUp;
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

    // Each layer of a celestial's clouds moves its flow and noise on by the simulated time of every frame that draws it,
    // so the clouds stand still while the camera is elsewhere and the same game time looks different after a different
    // tour. @time puts them back to their start: from there on they look the same whatever played before.
    private static void ResetClouds()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var transparencies = typeof(Program).GetField("_planetTransparenciesRenderer", Private)?.GetValue(Program.Instance)
            as PlanetTransparenciesRenderer;
        if (transparencies?.GetCloudRenderer() is not { } clouds)
        {
            Log("@time: no clouds to reset");
            return;
        }
        var fields = new (string Name, object Value)[]
        {
            ("_noiseScroll", default(Brutal.Numerics.double3)), ("_volumetricsFlowMapPhase", 0.0),
            ("_twoDimensionalFlowMapPhase", 0.0)
        };
        // The detail texture's frame: whether it has one, its origin and its offset. Origin and offset at zero are a new
        // layer's: its first frame starts the detail texture where the camera is. KSA 2026.10.25 keeps the frame per view
        // instead of once per layer, and also keeps where the noise was at the start of each frame.
        var scrollAtStart = typeof(CloudLayerRenderData).GetField("_noiseScrollAtFrameStart", Private);
        var views = typeof(CloudLayerRenderData).GetField("_viewportData", Private);
        var viewType = views?.FieldType.GetElementType();
        var detailFrame = new (FieldInfo? Layer, FieldInfo? View, object Value)[]
        {
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameInitialized", Private),
                viewType?.GetField("DetailTextureFrameInitialized"), false),
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameOrigin", Private),
                viewType?.GetField("DetailTextureFrameOrigin"), default(Brutal.Numerics.double3)),
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameUVOffset", Private),
                viewType?.GetField("DetailTextureFrameUVOffset"), default(Brutal.Numerics.double2))
        };
        if (detailFrame.Any(f => f.Layer == null && f.View == null))
            Log("@time: the clouds have no detail texture frame to reset");
        foreach (var layers in clouds._planetToCloudRenderData.Values)
        {
            foreach (var layer in layers)
            {
                foreach (var (name, value) in fields)
                {
                    if (typeof(CloudLayerRenderData).GetField(name, Private) is { } field)
                        field.SetValue(layer, value);
                    else
                        Log($"@time: the clouds have no {name}");
                }
                scrollAtStart?.SetValue(layer, default(Brutal.Numerics.double3));
                var viewData = views?.GetValue(layer) as Array;
                foreach (var (layerField, viewField, value) in detailFrame)
                {
                    layerField?.SetValue(layer, value);
                    if (viewField == null || viewData == null)
                        continue;
                    foreach (var view in viewData)
                    {
                        if (view != null)
                            viewField.SetValue(view, value);
                    }
                }
            }
        }
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
                    double pace = _after == After.Shot ? 2 : 0.25;
                    SetSpeed(left > warp * frame * 2 ? Math.Max(warp, left / pace) : left / (2 * frame));
                    return;
                }
                SetSpeed(0);
                // The game ran past the moment the script expects: what follows plays later than written
                if (-behind > AheadLimit)
                    _sectionResult?.Errors.Add(FormattableString.Invariant(
                        $"{_scriptTime - _sectionStart:F1} s: the game's clock is {-behind:F1} s ahead of the script's"));
                if (_after == After.Time)
                {
                    ResetClouds();
                    Resume();
                    return;
                }
                // Without screenshots the checkpoint ends here, its clocks in step (fast mode still measures the view), and so
                // does a checkpoint that isn't one of the places chosen
                if ((!Chosen || !_shots && !_fast) && !Holding)
                {
                    Resume();
                    return;
                }
                _phase = Phase.Settle;
                _settleStart = wall;
                _phaseEnds = wall + (_fast ? ProbeEvery : SettleSeconds);
                _phaseFrames = 0;
                _lastProbe = null;
                _probe = null;
                return;
            }
            case Phase.Settle:
                if (_fast ? !Settled(wall) : wall < _phaseEnds || ++_phaseFrames < SettleFrames)
                    return;
                if (_fast && !Holding)
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
                EndCheckpoint();
                return;
            }
        }
    }

    private string ShotPath() => Path.Combine(_outDir, $"{_sectionResult?.Name}_{_shotName}.png");

    private string CapturePath() => Path.Combine(_outDir, $"{_sectionResult?.Name}_{_shotName}.gputrace");

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

    // Fast mode: whether the view has stopped changing, from probe screenshots half a second apart (the game is paused, so
    // what still changes is terrain and textures streaming in). Without KSA Metal's screenshots it waits a few seconds.
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
            $"{(_after == After.Time ? "@time" : "checkpoint " + _shotName)}: game time {Universe.GetElapsedSeconds() - _gameStart:F3} s (script {_gameTarget - _gameStart:F3} s)"));
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
        if (!_fast)
        {
            DriverStats.Marker(_window.Name, false);
            _sectionResult?.Windows.Add(_window.Summarize());
            Log($"measured {_window.Name}: {_window.Frames} frames");
        }
        var followed = FollowedVehicle();
        if (_window.Vehicle != null && followed != _window.Vehicle && _window.Vehicle != _exploded)
            _sectionResult?.Errors.Add($"{_window.Name}: '{_window.Vehicle}' broke up (the camera follows '{followed ?? "nothing"}')");
        _window = null;
    }

    private static string? FollowedVehicle() => Program.GetHoveredCamera()?.Following is Vehicle v ? v.Id : null;

    private static void Log(string message)
    {
        // KSA redirects stderr into its own logger, so stdout it is
        System.Console.Out.WriteLine($"[benchmark] {message}");
        System.Console.Out.Flush();
    }
}
