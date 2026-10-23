// ksa-metal-benchmark: plays the benchmark's script in one game session and measures it.
//
// extras/benchmark/benchmark.sh starts the game with this mod installed and these variables set:
//   KSA_BENCH_SCRIPT  the script (extras/benchmark/benchmark.txt)
//   KSA_BENCH_OUT     the folder for the results (results.json) and the screenshots
//   KSA_BENCH_ONLY    optional: the sections to play (space separated), to work on a few of them
//   KSA_BENCH_TRACE   optional: write the camera's place every frame to trace.csv, to check its moves are smooth
//   KSA_BENCH_FILM    optional: a screenshot every that many seconds of the script's clock, into film/, to see moves
//   KSA_BENCH_EXPLORE optional: no script, the explore mode's console command instead (Explore.cs)
// Without the first two the mod does nothing.
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
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KSA;
using KSA.Atmosphere.Rendering;
using StarMap.API;

namespace KsaMetalBenchmark;

[StarMapMod]
public partial class Benchmark
{
    // How close a checkpoint gets to the game time it waits for (game seconds), and how long it lets the world settle
    // (terrain and textures streaming in) before its screenshot
    private const double Tolerance = 0.02;
    private const double SettleSeconds = 1.0;
    private const int SettleFrames = 10;
    // How long the simulation takes to ease to a stop at a checkpoint, and back to its speed after it
    private const double ShotEase = 0.75;
    // While the script warps, how many seconds the game takes to make up what it fell behind
    private const double WarpCatchUp = 0.5;

    private record Step(double At, string Command, int Line);

    private class Section
    {
        public string Name = "";
        public string[] Needs = Array.Empty<string>();
        public readonly List<Step> Steps = new();
        public double Length => Steps.Count > 0 ? Steps.Max(s => s.At) : 0;
    }

    private enum Phase { Play, CatchUp, Settle, Shoot }

    private readonly List<Section> _sections = new();
    private readonly Stopwatch _wall = new();
    private string _outDir = "";
    private HashSet<string>? _only;
    private bool _active;
    private Results? _results;

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
    private string _shotName = "";
    private double _phaseEnds;
    private int _phaseFrames;

    private Window? _window;
    // The vehicle @explode blew up
    private string? _exploded;
    private StreamWriter? _trace;
    private double _filmEvery, _nextFilm;

    // StarMap only calls an ImmediateLoad method with the signature void(Mod)
    [StarMapImmediateLoad]
    public void Init(Mod mod)
    {
        var script = Environment.GetEnvironmentVariable("KSA_BENCH_SCRIPT");
        _outDir = Environment.GetEnvironmentVariable("KSA_BENCH_OUT") ?? "";
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
        var only = Environment.GetEnvironmentVariable("KSA_BENCH_ONLY");
        if (!string.IsNullOrWhiteSpace(only))
            _only = only.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
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
        Directory.CreateDirectory(_outDir);
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KSA_BENCH_TRACE")))
        {
            _trace = new StreamWriter(Path.Combine(_outDir, "trace.csv"));
            _trace.WriteLine("wall,script,phase,following,x,y,z,fx,fy,fz,ux,uy,uz,lat,lon,alt,ground");
        }
        if (double.TryParse(Environment.GetEnvironmentVariable("KSA_BENCH_FILM"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out _filmEvery) && _filmEvery > 0)
            Directory.CreateDirectory(Path.Combine(_outDir, "film"));
        _results = new Results(DriverStats.Available);
        _active = true;
        Log($"{_sections.Count} sections, GPU times {(DriverStats.Available ? "available" : "not available")}");
    }

    [StarMapAfterOnFrame]
    public void OnFrame(double currentPlayerTime, double dtPlayer)
    {
        if (_exploring)
            ExploreFrame();
        if (!_active)
            return;
        if (!_wall.IsRunning)
            Start();
        double wall = _wall.Elapsed.TotalSeconds;
        double dt = wall - _lastWall;
        _lastWall = wall;
        // The game's messages stay in its log, off the screen
        Program.ConsoleWindow.ClearConsole();

        if (_phase != Phase.Play)
        {
            UpdateCamera(0);
            Checkpoint(wall, dt);
            return;
        }
        _scriptTime += dt;
        double speed = SpeedAt(_scriptTime);
        _gameTarget += speed * dt;
        // The game keeps a little under the speed it is asked for (about 12% here), which adds up to hours over a
        // time-lapse: while the script warps, it asks for a little more as the game falls behind
        if (speed > 2)
            speed += Math.Max(_gameTarget - Universe.GetElapsedSeconds(), 0) / WarpCatchUp;
        SetSpeed(speed);
        _window?.Add(dt * 1000, DriverStats.Read());
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
            Run(step.Command);
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
        _results!.LoadSeconds = Math.Round((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds, 1);
        Log(FormattableString.Invariant($"first in-game frame after {_results.LoadSeconds:F1} s"));
        // The scene alone, without the game's interface (F2)
        Program.DrawUI = false;
        _gameStart = _gameTarget = Universe.GetElapsedSeconds();
        _warpFrom = _warpTo = Universe.GetSimulationSpeed();
        foreach (var section in _sections)
        {
            var missing = section.Needs.Where(id => Universe.CurrentSystem?.All.TryGet(id, out Astronomical? _) != true).ToList();
            if (missing.Count > 0)
                Log($"skipping section {section.Name}: no {string.Join(", ", missing)} in this system");
            else if (_only != null && !_only.Contains(section.Name))
                Log($"skipping section {section.Name}");
            else
                continue;
            section.Steps.Clear();
            section.Name = "";
        }
        _sections.RemoveAll(s => s.Name == "");
        // The sections play one after the other, each as long as its last step's time
        double start = 0;
        foreach (var section in _sections)
        {
            _shotTimes.AddRange(section.Steps.Where(s => s.Command.StartsWith("@shot ")).Select(s => start + s.At));
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
            Log("done");
            Program.TerminalInterface.Execute("exit");
            return;
        }
        _sectionResult = new SectionResult(_sections[_section].Name);
        _results!.Sections.Add(_sectionResult);
        Log(FormattableString.Invariant($"section {_sectionResult.Name} (game time {Universe.GetElapsedSeconds():F1} s)"));
    }

    private void EndSection()
    {
        if (_sectionResult == null)
            return;
        CloseWindow();
        _results!.Write(Path.Combine(_outDir, "results.json"));
        _sectionResult = null;
    }

    private void Run(string command)
    {
        bool ok;
        try
        {
            ok = command.StartsWith('@') ? Directive(command) : Console(command);
        }
        catch (Exception e)
        {
            Log($"'{command}' threw {e.GetType().Name}: {e.Message}");
            ok = false;
        }
        double t = _stepAt - _sectionStart;
        Log(FormattableString.Invariant($"{_sectionResult?.Name} {t:F1} s: '{command}' -> {(ok ? "ok" : "failed")}"));
        if (!ok)
            _sectionResult?.Errors.Add(FormattableString.Invariant($"{t:F1} s: '{command}' failed"));
    }

    // A console command. Those that move the camera elsewhere end the directives' hold on it.
    private bool Console(string command)
    {
        var word = command.Split(' ')[0];
        if (word is "follow" or "camera" or "control" or "goto" or "altitude" or "lookat" or "moveto")
            ReleaseCamera();
        return Program.TerminalInterface.Execute(command);
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
        _phase = Phase.CatchUp;
    }

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
        _shotName = "";
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
            ("_twoDimensionalFlowMapPhase", 0.0), ("_detailTextureFrameInitialized", false)
        };
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
                    // Fast while far behind (at least the scripted speed), then just what the next frame needs
                    double frame = Math.Max(dt, 1.0 / 240);
                    double warp = Math.Max(_warpTo, 1);
                    // A jump of @time covers half of what is left every quarter of a second or so
                    double pace = _shotName == "" ? 0.25 : 2;
                    SetSpeed(behind > warp * frame * 2 ? Math.Max(warp, behind / pace) : behind / frame);
                    return;
                }
                SetSpeed(0);
                if (_shotName == "")
                {
                    ResetClouds();
                    Resume();
                    return;
                }
                _phase = Phase.Settle;
                _phaseEnds = wall + SettleSeconds;
                _phaseFrames = 0;
                return;
            }
            case Phase.Settle:
                if (wall < _phaseEnds || ++_phaseFrames < SettleFrames)
                    return;
                var path = Path.Combine(_outDir, $"{_sectionResult?.Name}_{_shotName}.png");
                if (!DriverStats.Screenshot(path))
                    Log($"no screenshot {_shotName}: needs KSA Metal");
                _phase = Phase.Shoot;
                _phaseFrames = 0;
                return;
            case Phase.Shoot:
                // The image is the next presented one: give it a few frames
                if (++_phaseFrames >= 3)
                    Resume();
                return;
        }
    }

    private void Resume()
    {
        Log(FormattableString.Invariant(
            $"{(_shotName == "" ? "@time" : "checkpoint " + _shotName)}: game time {Universe.GetElapsedSeconds() - _gameStart:F3} s (script {_gameTarget - _gameStart:F3} s)"));
        SetSpeed(SpeedAt(_scriptTime));
        _phase = Phase.Play;
    }

    // Closes the open measurement window. When the camera followed a vehicle at its start and follows another one
    // now, the vehicle broke up during it (a breakup changes what is on screen, so the part's numbers don't compare),
    // unless @explode blew it up
    private void CloseWindow()
    {
        if (_window == null)
            return;
        _sectionResult?.Windows.Add(_window.Summarize());
        Log($"measured {_window.Name}: {_window.Frames} frames");
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
