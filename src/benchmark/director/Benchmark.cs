// The benchmark's director: plays the benchmark's script in one game session and measures it.
//
// KSA Metal Hooks start it (src/hooks/app/BenchmarkRun.cs): when the game starts with the variables below set, which
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
//   KSA_BENCH_CATCH   optional: a clean run's folder: after a screenshot under half the size of that run's same screenshot,
//                     KSA Metal's pass dump of the next frame in caught/<section>_<shot>/ and an image of a frame after it
//                     in caught/, at most MaxCatches a run. A screenshot also counts when it has far more pure black than
//                     that run's, or is 30 % pure white
//   KSA_BENCH_CATCH_CAPTURE optional, with KSA_BENCH_CATCH: an Xcode GPU frame capture after the pass dump, in
//                     caught/<section>_<shot>.gputrace (the game needs MTL_CAPTURE_ENABLED=1)
//   KSA_BENCH_PASSES  optional: the same pass dump and image at every checkpoint, broken or not: a clean run's, to compare
//                     a caught one with
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
// The script is a series of sections. "section <name> [live] [needs <celestial>...]" starts one; a section whose celestials
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
//
// A live section is the time warp's: it plays the same in both modes, by the Mac's clock alone. Its parts take the time
// they are given whatever the game gets through in it: the game gets the speed the script asks for, as from a player, and
// nothing waits for it (no catching up; a checkpoint stops where the game has got to), so a slow Mac or a stuck
// simulation can't hold it up. @measure measures how far the game's clock got.
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
    private const double Tolerance = 0.002;
    private const double AheadLimit = 0.5;
    private const double SettleSeconds = 1.0;
    private const int SettleFrames = 10;
    // How long the simulation takes to ease to a stop at a checkpoint, and back to its speed after it
    private const double ShotEase = 0.75;
    // While the script warps, how many seconds the game takes to make up what it fell behind
    private const double WarpCatchUp = 0.5;
    // Fast mode: how often it looks whether the view still changes (seconds), how much of it may change between two
    // looks to count as still (percent of its blocks, see Images.cs), the longest it waits, and how long it measures
    private const double ProbeEvery = 0.25;
    private const double StillPercent = 0.5;
    private const double MaxSettleSeconds = 20;
    private const double FastMeasureSeconds = 1.0;

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
        public bool Live;
        public double Length => Steps.Count > 0 ? Steps.Max(s => s.At) : 0;
        // Only the camera's flight: no vehicles, nothing that needs the game's clock
        public bool CameraOnly => Steps.All(s => s.Words[0] is "@key" or "@shot" or "@measure");
    }

    private enum Phase { Play, CatchUp, Settle, Measure, Capture, Shoot, Catch, CatchPasses, CatchCapture, CaughtImage, Hold, NewWorld }

    // What a catch-up of the game's clock leads to: a checkpoint's screenshot, the end of a jump of @time, or the end of
    // a @sync
    private enum After { Shot, Time, Sync }

    private readonly List<Section> _sections = new();
    private readonly Stopwatch _wall = new();
    private string _outDir = "";
    // KSA_BENCH_ONLY: the sections to play, each with the checkpoints chosen in it (null: all of them)
    private Dictionary<string, HashSet<string>?>? _only;
    private string _resultsFile = "results.json";
    private FrameLog? _frames;
    private uint _profileFrames;
    private bool _capture;
    // KSA_BENCH_CATCH: the clean run, and the captures taken so far (each several GB)
    private const int MaxCatches = 3;
    private const double CatchBelow = 0.5, CatchMoreBlack = 25, CatchWhite = 30;
    private string? _catchAgainst;
    private bool _catchCapture, _passesEverywhere;
    private int _catches;
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
    // The times of the checkpoints and @sync steps on the script's clock, where the simulation eases to a stop, and the
    // time of the step running
    private readonly List<double> _stopTimes = new();
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
                _sections.Add(new Section
                {
                    Name = words[1], Needs = needs > 0 ? words[(needs + 1)..] : Array.Empty<string>(), Live = words.Length > 2 && words[2] == "live"
                });
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
        _catchAgainst = _option("KSA_BENCH_CATCH") is { Length: > 0 } clean ? clean : null;
        _catchCapture = _option("KSA_BENCH_CATCH_CAPTURE") == "1";
        _passesEverywhere = _option("KSA_BENCH_PASSES") == "1";
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
        if (_fast)
            SettleSea();
        if (_phase == Phase.Hold)
            return;
        // The game's messages stay in its log, off the screen
        Program.ConsoleWindow.ClearConsole();
        var sim = ReadSim();
        _frames?.Add(_scriptTime - _sectionStart, _sectionResult?.Name, _window?.Name ?? _still?.Name, _phase.ToString(), dt * 1000,
            DriverStats.Read(), sim);

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
            _gameTarget += GameSeconds(_scriptTime, _scriptTime + dt);
            _scriptTime += dt;
            double speed = SpeedAt(_scriptTime);
            // The game keeps a little under the speed it is asked for (about 12% here), which adds up to hours over a
            // time-lapse, and takes a new speed a frame late, so a fast warp easing down overshoots by minutes: while
            // the script warps, it asks for more as the game falls behind and for less as it gets ahead. Coming out of
            // a warp the game can end up ahead (tens of milliseconds at the speed of the frame before): below a warp
            // it only asks for less, until the game is back in step
            if (!Live && (speed > 2 || speed > 0 && Universe.GetElapsedSeconds() > _gameTarget))
                speed = Math.Max(speed + (_gameTarget - Universe.GetElapsedSeconds()) / WarpCatchUp, 0);
            SetSpeed(speed);
            if (!_fast || Live)
                _window?.Add(dt * 1000, DriverStats.Read());
            if (Live)
                _window?.AddSim(dt * 1000, sim);
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

    // The simulation speed the script asks for at a time of its clock: @warp's, eased to a stop around checkpoints and
    // @sync steps (arriving at full speed, the game would run a frame past them)
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
        foreach (double stop in _stopTimes)
        {
            double away = Math.Abs(t - stop);
            if (away < ShotEase)
                warp *= Smooth(away / ShotEase);
        }
        return warp;
    }

    // The game seconds the script asks for between two times of its clock: the speed summed in steps of a millisecond
    // or less (Simpson's rule), so the script's game time is the same at the same moment of its clock, whatever the
    // frame times on the way
    private double GameSeconds(double from, double to)
    {
        int n = 2 * Math.Max(1, (int)Math.Ceiling((to - from) / 0.002));
        double h = (to - from) / n, sum = SpeedAt(from) + SpeedAt(to);
        for (int i = 1; i < n; i++)
            sum += SpeedAt(from + i * h) * (i % 2 == 1 ? 4 : 2);
        return sum * h / 3;
    }

    // Whether the section playing is a live one
    private bool Live => _section >= 0 && _section < _sections.Count && _sections[_section].Live;

    private static readonly System.Reflection.FieldInfo? UpdateTask = typeof(Universe).GetField("_vehicleUpdateTask", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly System.Reflection.FieldInfo? Bubbles = typeof(VehicleUpdateTask).GetField("_bubbles", BindingFlags.NonPublic | BindingFlags.Instance);

    private static SimSample ReadSim()
    {
        // Fields of the game's: a later version may move them, which only leaves the count out
        var bubbles = UpdateTask?.GetValue(null) is { } task ? Bubbles?.GetValue(task) as System.Collections.ICollection : null;
        int vehicles = 0;
        if (Universe.CurrentSystem is { } system)
        {
            foreach (var body in system.All.AsSpan())
                vehicles += body is Vehicle ? 1 : 0;
        }
        return new SimSample(Universe.GetElapsedSeconds(), Universe.GetSimulationSpeed(), JobSystems.VehicleSolver?.GetMaxLastTickTime() ?? 0,
            PhysicsStepDiagnostics.Substeps, bubbles?.Count ?? 0, vehicles);
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

    // The new speed starts at the step's time: the game time this frame already added after it, at the old speed, is
    // made up at the new one
    private void Warp(double speed, double seconds)
    {
        double after = GameSeconds(_stepAt, _scriptTime);
        _warpFrom = SpeedAt(_stepAt);
        _warpTo = speed;
        _warpStart = _stepAt;
        _warpSeconds = seconds;
        _gameTarget += GameSeconds(_stepAt, _scriptTime) - after;
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
            // Each time once: views one after the other at one time ease to it once
            _stopTimes.AddRange(section.Steps.Where(s => s.Words[0] is "@shot" or "@sync").Select(s => start + s.At)
                .Distinct());
            start += section.Length;
        }
        BuildPaths();
    }

    // Ends the section playing and starts the next one, or ends the run after the last
    private void NextSection()
    {
        EndSection();
        // After a live section the game's clock is wherever it got to
        if (Live)
            _gameTarget = Universe.GetElapsedSeconds();
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
            // The camera's field of view and the game's explosions as they were
            Unhold();
            EndRig();
            Done = true;
            if (_option("KSA_BENCH_END") != "stay")
                Program.TerminalInterface.Execute("exit");
            return;
        }
        _sectionResult = new SectionResult(_sections[_section].Name) { Live = Live };
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
        Unhold();
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

    private static void Log(string message)
    {
        // KSA redirects stderr into its own logger, so stdout it is
        System.Console.Out.WriteLine($"[benchmark] {message}");
        System.Console.Out.Flush();
    }
}
