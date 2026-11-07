// The benchmark's director: plays the benchmark's script in one game session and measures it.
//
// The runs in the game start it (Runs.cs, after every frame the hooks give them) with a run's options (RunOptions, in
// src/shared/benchmark/): when the game starts with KSA_BENCH_OUT naming a run's folder that holds them as options.json,
// which benchmark.sh's program writes, or with the same options from the Mac Port tab in the game's settings. Without a
// folder and a script (or explore mode) there is nothing to play. It measures nothing itself: what happens goes into
// events.jsonl (Events.cs; a heartbeat every second, so a game that hangs shows), every frame into frames.csv
// (Frames.cs), marked with the part it counts in, and the run's record is made from them when it ends (Finish.cs). Every
// measured part is a signpost interval (KSA Metal's ksaMtlMarker), so an Instruments trace shows the parts.
//
// The lineup (src/shared/benchmark/Lineup.cs) is a series of scenarios, each a list of "<seconds> <command>" steps timed
// from its start; a scenario whose celestials the loaded system lacks is skipped. A command is a console command or a
// directive (Directives.cs).
//
// Two clocks run the script. The script's clock counts the seconds of play: the steps, the camera's moves and the
// measured parts follow it. The game's clock is the simulation's, which @warp speeds up or pauses. The script says
// where the game's clock should be at every moment, but a slower Mac falls behind it (the game slows the simulation
// down when it cannot keep up). Every @shot is a checkpoint that puts the two back in step: the simulation eases to a
// stop as the script reaches it, the script's clock stops, the simulation catches up with the game time the script
// expects and pauses there, the screenshot is taken once the world has settled, and both clocks go on, the simulation
// easing back to its speed. So every Mac takes its screenshots at the same moments of the game.
//
// How a scenario keeps time is its kind. A flight plays as above. A views scenario sets its vehicles up, then the game
// stays paused and the camera goes from view to view: at each checkpoint the director waits until the view stops
// changing (terrain and textures streaming in), measures a second of the view standing there and takes the screenshot.
// Places of a flight can be jumped to the same way (RunOptions.Fast): the game stays paused through the camera's
// flight, and the script's clock jumps from one chosen checkpoint to the next. A timed scenario plays by the Mac's clock
// alone: its parts take the time they are given whatever the game gets through in it, the game gets the speed the
// script asks for, as from a player, and nothing waits for it (no catching up; a checkpoint stops where the game has
// got to), so a slow Mac or a stuck simulation can't hold it up. @measure measures how far the game's clock got.
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
    // How far ahead of a checkpoint's moment the game may be (the clock only goes forward), how long a flight's checkpoint
    // lets the world settle (terrain and textures streaming in) before its screenshot, and how many seconds a checkpoint
    // takes to catch up with its moment
    private const double AheadLimit = 0.5;
    private const double SettleSeconds = 1.0;
    private const int SettleFrames = 10;
    private const double CatchUpSeconds = 2;
    // Fast mode: how often it looks whether the view still changes (seconds), how much of it may change between two
    // looks to count as still (percent of its blocks, see Images.cs), the longest it waits, and how long it measures
    private const double ProbeEvery = 0.25;
    private const double StillPercent = 0.5;
    private const double MaxSettleSeconds = 20;
    private const double FastMeasureSeconds = 1.0;

    // A scenario of the lineup as it plays: its steps, cut short when places are chosen
    internal sealed class Section
    {
        public string Name = "";
        public IReadOnlyList<string> Needs = Array.Empty<string>();
        public readonly List<Step> Steps = new();
        public bool Live;
        public Kind Kind;
        public double Length => Steps.Count > 0 ? Steps.Max(s => s.At) : 0;
        // Only the camera's flight: no vehicles, nothing that needs the game's clock
        public bool CameraOnly => Steps.All(s => s.Words[0] is "@key" or "@shot" or "@measure");
    }

    private enum Phase { Play, CatchUp, Settle, Measure, Probes, Shoot, Confirm, Hold, NewWorld }

    // What a catch-up of the game's clock leads to: a checkpoint's screenshot, or the end of a @sync
    private enum After { Shot, Sync }

    private readonly List<Section> _sections = new();
    // Each scenario's test's noise profile: how much its screenshots may change from run to run
    private readonly Dictionary<string, NoiseProfile> _noise = new();
    private readonly Stopwatch _wall = new();
    private string _outDir = "";
    // RunOptions.Only: the sections to play, each with the checkpoints chosen in it (null: all of them)
    private Dictionary<string, HashSet<string>?>? _only;
    private FrameLog? _frames;
    private EventLog _events = null!;
    // KSA Metal's drain (DriverStats.Drain): open while a run plays, its latest present the frame's driver numbers
    private bool _drain;
    private RunStore? _store;
    // The game's profilers (GameProfilers) and the .NET runtime's counters, while a run plays; a frame of a measured part
    // this long or longer is kept as a moment (ZoneLog)
    private GameProfilers? _profilers;
    private RuntimeCounters? _runtime;
    private const double HitchMs = 100;
    private FrameStats? _present;
    private readonly List<DriverRecord> _driverRecords = new();
    // This frame's line of frames.csv, and the part its numbers count in (none: null)
    private (double Script, string? Scenario, string Phase, double FrameMs, FrameStats? Driver, SimSample Sim)? _row;
    private string? _measured;
    // The tools turned on (Probes.cs), and the wait for one's work at a checkpoint with what comes after it
    private List<IProbe> _probes = new();
    private Wait? _wait;
    private Action _afterWait = () => { };
    private bool _active;
    // RunOptions.Fast: the places chosen jumped to, the game paused on the way, instead of played
    private bool _jump, _shots = true;
    // Which time the scenarios play in this session (RunOptions.Repeat): 0 the warm-up pass, then 1, 2...
    private int _pass = 1, _firstPass = 1;

    // Whether this pass takes screenshots: the first alone, which plays as a run's does (a warm-up's too); the others
    // measure
    private bool TakesShots => _shots && _pass == _firstPass;
    private string? _hold;
    private double _nextBeat;

    // The section playing, its steps and where it started on the script's clock
    private int _section = -1;
    private int _next;
    private double _sectionStart;
    // The screenshot limit of the checkpoint under way (the test's noise profile)
    private double _shotLimit;
    // Whether a scenario is playing (between two, and before the first, none is)
    private bool _inScenario;

    private double _lastWall;
    private bool _waited;
    // The script's clock and the game's (Clock.cs), and the time of the step running on the script's clock
    private readonly Clock _clock = new();
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
    // A view standing still: how many times its screenshot was taken again, as something passed in it, and at most
    private int _retakes;
    private const int MaxRetakes = 3;

    // A measured part open: its name, when it ends on the script's clock, the vehicle the camera followed when it opened
    private sealed record Window(string Name, double EndsAt, string? Vehicle);
    private Window? _window;
    // Fast mode's measurement of the view standing at a checkpoint
    private Window? _still;
    // RunOptions.WaitForRecording, and when it last looked for the event
    private bool _waitFor;
    private double _lookedForRecording = -1;
    private readonly Stopwatch _waiting = new();

    private readonly RunOptions _options;
    private readonly CameraOperator _camera;
    // Explore mode (Explore.cs): no script, a console command that captures the camera's place
    private readonly Explorer? _explorer;
    private readonly Vehicles _vehicles;
    private readonly Dictionary<string, Func<string[], bool>> _handlers;



    // Whether the script has ended (played to its end, or stopped)
    public bool Done { get; private set; }

    // Starts a run with these options, or null when there is nothing to play. The caller calls OnFrame after every
    // frame of the game.
    public static Director? Begin(RunOptions options)
    {
        var director = new Director(options);
        return director._active || director._explorer != null ? director : null;
    }

    private Director(RunOptions options)
    {
        _options = options;
        _camera = new CameraOperator(_clock);
        _vehicles = new Vehicles(_camera);
        _camera.Watch(_vehicles);
        _handlers = Handlers();
        var script = options.Script;
        _outDir = options.Out;
        if (string.IsNullOrEmpty(_outDir))
            return;
        _events = new EventLog(_outDir, options.Session);
        if (options.Explore)
            _explorer = new Explorer(_outDir);
        if (_explorer != null || string.IsNullOrEmpty(script))
            return;
        if (!File.Exists(script))
        {
            Log($"script {script} not found");
            return;
        }
        _jump = options.Fast;
        _shots = !options.NoShots;
        _pass = _firstPass = options.WarmUp ? 0 : 1;
        _events.Pass = _pass;
        _hold = options.Hold is { Length: > 0 } hold ? hold : null;
        Lineup lineup;
        try
        {
            lineup = Lineup.Read(script);
        }
        catch (LineupException e)
        {
            Log(e.Message);
            return;
        }
        foreach (var scenario in lineup.Scenarios)
        {
            // benchmark.sh calibrate's profile of the test, if there is one, before the one in the app
            if (lineup.TestOf(scenario.Name) is { } test)
                _noise[scenario.Name] = NoiseProfile.Read(
                    options.NoiseFolder.Length > 0 && File.Exists(NoiseProfile.PathFor(options.NoiseFolder, test.Name)) ? options.NoiseFolder : Path.GetDirectoryName(script)!,
                    test.Name);
            var section = new Section { Name = scenario.Name, Needs = scenario.Needs, Live = scenario.Kind == Kind.Timed, Kind = scenario.Kind };
            section.Steps.AddRange(scenario.Steps);
            _sections.Add(section);
        }
        var only = options.Only;
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
        // Only the scenarios chosen, so that the first one is the first played
        if (_only != null)
            _sections.RemoveAll(section => !_only.ContainsKey(section.Name));
        _waitFor = options.WaitForRecording;
        Directory.CreateDirectory(_outDir);
        _frames = new FrameLog(_outDir, options.Session) { Pass = _pass };
        // Waits from a millisecond on: shorter ones are the driver's ordinary work
        _store = RunStore.For(_outDir);
        _drain = DriverStats.OpenDrain(1);
        _profilers = GameProfilers.Start(_store, Log);
        _runtime = new RuntimeCounters();
        _probes = Probes.For(options, new ProbeContext(_outDir, Error, Log));
        _active = true;
        // A directive without a handler fails the run at once, before anything plays
        foreach (var missing in UnhandledDirectives())
            Error(missing);
        Log($"{_sections.Count} scenarios, {options.Mode}{(_shots ? "" : ", no screenshots")}{(_hold != null ? $", holding at {_hold}" : "")}, " +
            $"GPU times {(DriverStats.Available ? "available" : "not available")}{string.Concat(_probes.Select(p => ", " + p.Name))}");
    }

    public void OnFrame()
    {
        _row = null;
        _measured = null;
        // Every frame, also while waiting or holding, so the drain never fills and a frame's runtime counters are its own
        DrainDriver();
        var runtime = _runtime?.Sample();
        FrameBody();
        // The frame's line, once it is known which part its numbers count in, and its zones there
        PartKey? part = null;
        if (_row is { } r)
        {
            _frames?.Add(r.Script, r.Scenario, _measured, r.Phase, r.FrameMs, r.Driver, r.Sim, runtime);
            if (_measured != null)
                part = new PartKey(_options.Session, _pass, r.Scenario ?? "", _measured);
        }
        _profilers?.Frame(part, part != null && _row is { FrameMs: >= HitchMs } slow ? slow.FrameMs : 0);
    }

    private void FrameBody()
    {
        _explorer?.Frame();
        if (!_active)
            return;
        if (!_wall.IsRunning && _waitFor)
        {
            // Views: the game paused from its first frame, so that the wait leaves nothing behind in the clouds and the sea
            if (Fast)
                _clock.SetSpeed(0);
            _waited = true;
            // Up in the game: the heartbeat tells the script, which starts its tool and then makes the file
            if (!_waiting.IsRunning || _waiting.Elapsed.TotalSeconds >= _nextBeat)
            {
                _events.Add("heartbeat", text: "waiting");
                _nextBeat = _waiting.Elapsed.TotalSeconds + 1;
            }
            _waiting.Start();
            if (_waiting.Elapsed.TotalSeconds < 60 && !Recording())
                return;
            Log(FormattableString.Invariant($"waited {_waiting.Elapsed.TotalSeconds:F1} s for the recording"));
            _waitFor = false;
            _nextBeat = 0;
        }
        if (!_wall.IsRunning)
            Start();
        double wall = _wall.Elapsed.TotalSeconds;
        double dt = wall - _lastWall;
        _lastWall = wall;
        if (wall >= _nextBeat)
        {
            _events.Add("heartbeat", ScenarioName, value: Math.Round(_clock.Script - _sectionStart, 1));
            _frames?.Flush();
            _nextBeat = wall + 1;
        }
        if (Fast)
            World.SettleSea();
        if (_phase == Phase.Hold)
            return;
        // The game's messages stay in its log, off the screen
        Program.ConsoleWindow.ClearConsole();
        _row = (_clock.Script - _sectionStart, ScenarioName, _phase.ToString(), dt * 1000, _drain ? _present ?? DriverStats.Read() : DriverStats.Read(), ReadSim());

        if (_phase != Phase.Play)
        {
            _camera.UpdateCamera(0);
            Checkpoint(wall);
            return;
        }
        if (Paused)
        {
            // Places jumped to: straight on to the next checkpoint
            dt = NextStop() - _clock.Script;
            _clock.JumpTo(NextStop());
        }
        else if (InSetup)
        {
            // The steps went on before the game's step (BeforeStep)
            dt = _clock.TakeSetupAdvance();
        }
        else
        {
            _clock.Play(dt, Live);
            if (!Fast)
                _measured = _window?.Name;
        }
        if (_window != null && _clock.Script >= _window.EndsAt)
            CloseWindow();
        _camera.UpdateCamera(dt);
        foreach (var probe in _probes)
            probe.Frame(wall, _clock.Script, _phase.ToString());

        while (_phase == Phase.Play && _active)
        {
            if (_section < 0 || _next >= _sections[_section].Steps.Count)
            {
                if (_section >= 0 && _clock.Script < _sectionStart + _sections[_section].Length)
                    break;
                NextSection();
                continue;
            }
            var step = _sections[_section].Steps[_next];
            if (_sectionStart + step.At > _clock.Script)
                break;
            _next++;
            _stepAt = _sectionStart + step.At;
            Run(step);
        }
    }

    // Whether benchmark.sh said its tool records (the "recording" event), looked for twice a second
    private bool Recording()
    {
        if (_waiting.Elapsed.TotalSeconds < _lookedForRecording + 0.5)
            return false;
        _lookedForRecording = _waiting.Elapsed.TotalSeconds;
        return EventLog.Read(_outDir).Any(e => e.Type == "recording" && e.Session == _options.Session);
    }

    // Before the game works out its next step of the simulation (the hooks call it from Universe.GetJobSimStep): the
    // game time that step may not pass (Clock.cs). A timed scenario's game runs free.
    public void BeforeStep()
    {
        if (!_active || !_wall.IsRunning || Live || _phase == Phase.Hold || _section < 0)
            return;
        var stepping = _phase == Phase.CatchUp ? Stepping.CatchUp : InSetup ? Stepping.Setup : _phase == Phase.Play && !Paused ? Stepping.Flight : Stepping.Free;
        // A setup goes on up to the next step's time at most, which the frame then plays
        var section = _sections[_section];
        _clock.BeforeStep(stepping, _sectionStart + (_next < section.Steps.Count ? section.Steps[_next].At : section.Length));
    }

    // Whether a views scenario is setting up: its steps play at a fixed pace (Clock.cs) until the game pauses for its views
    private bool InSetup => _phase == Phase.Play && !Paused && !Live && _section >= 0 && _sections[_section].Kind == Kind.Views;

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

    // Whether the views are measured standing still at their checkpoints: a views scenario's, or a flight's places jumped to
    private bool Fast => !Live && _sections.Count > 0 && (_jump || _sections[Math.Max(_section, 0)].Kind == Kind.Views);

    // Whether the game stays paused: a flight's places jumped to, in a section of the camera's flight alone (or before
    // the first section)
    private bool Paused => Fast && (_section < 0 || _sections[_section].CameraOnly);

    // Fast mode: where the camera's flight stops next on the script's clock: its next chosen checkpoint, or its end
    private double NextStop()
    {
        if (_section < 0)
            return _clock.Script;
        var section = _sections[_section];
        for (int i = _next; i < section.Steps.Count; i++)
        {
            if (section.Steps[i].Words is ["@shot", var shot, ..] && IsChosen(shot))
                return _sectionStart + section.Steps[i].At;
        }
        return _sectionStart + section.Length;
    }

    private void Start()
    {
        _wall.Start();
        // A run in a running game has no loading of its own
        if (!_options.InGame)
        {
            double load = Math.Round((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds - _waiting.Elapsed.TotalSeconds, 1);
            _events.Add("loaded", value: load);
            Log(FormattableString.Invariant($"first in-game frame after {load:F1} s"));
        }
        // The scene alone, without the game's interface (F2)
        Program.DrawUI = false;
        // A views run paused while it waited (for a tool) plays at real time until its first @warp, as one that didn't wait
        _clock.Begin(_waited && Fast ? 1 : Universe.GetSimulationSpeed());
        foreach (var section in _sections)
        {
            var missing = section.Needs.Where(id => Universe.CurrentSystem?.All.TryGet(id, out Astronomical? _) != true).ToList();
            if (missing.Count > 0)
                Log($"skipping section {section.Name}: no {string.Join(", ", missing)} in this system");
            else
                continue;
            section.Steps.Clear();
            section.Name = "";
        }
        _sections.RemoveAll(s => s.Name == "");
        // Views and places jumped to: paused until a scenario with vehicles
        if (Fast)
            _clock.SetSpeed(0);
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
            foreach (var stop in section.Steps.Where(s => s.Words[0] is "@shot" or "@sync").Select(s => start + s.At).Distinct())
                _clock.AddStop(stop);
            start += section.Length;
        }
        _camera.BuildPaths(_sections);
    }

    // Ends the section playing and starts the next one, or ends the run after the last
    private void NextSection()
    {
        EndSection();
        // After a live section the game's clock is wherever it got to
        if (Live)
            _clock.Resync();
        if (_section >= 0)
            _sectionStart += _sections[_section].Length;
        _section++;
        _next = 0;
        if (_section >= _sections.Count && NextPass())
            return;
        if (_section >= _sections.Count)
        {
            _active = false;
            _frames?.Dispose();
            _frames = null;
            CloseDrain();
            StopProfilers();
            foreach (var probe in _probes)
                probe.End();
            if (_clock.ShortSteps > 0)
                Log($"the setup's steps: the game fell short of {_clock.ShortSteps} of them (slow frames of physics), finished in the frame after");
            Log(FormattableString.Invariant($"done after {_wall.Elapsed.TotalSeconds:F1} s"));
            // The camera's field of view and the game's explosions as they were
            _camera.Unhold();
            _camera.EndRig();
            Done = true;
            if (!_options.Stay)
                Program.TerminalInterface.Execute("exit");
            return;
        }
        // A flight plays at real time, wherever the scenario before it left the speed (the Mixed Test ends paused)
        if (_sections[_section].Kind == Kind.Flight && !Paused)
            _clock.RealTime();
        _inScenario = true;
        _events.Add("scenario", ScenarioName, text: Lineup.Name(_sections[_section].Kind));
        Log(FormattableString.Invariant($"scenario {ScenarioName} (game time {Universe.GetElapsedSeconds():F1} s, {_wall.Elapsed.TotalSeconds:F1} s into the run)"));
    }

    // At the end of the scenarios: the next pass, if there is one (true), from the script's start. A measured pass ends
    // with "done", the warm-up with "warmed" (the record sets its frames against the measured passes')
    private bool NextPass()
    {
        if (_pass == 0)
        {
            _events.Add("warmed");
            Log(FormattableString.Invariant($"warm-up done after {_wall.Elapsed.TotalSeconds:F1} s"));
        }
        else
            _events.Add("done");
        if (_pass >= _options.Repeat)
            return false;
        _pass++;
        _events.Pass = _pass;
        if (_frames != null)
            _frames.Pass = _pass;
        _section = -1;
        _next = 0;
        _sectionStart = 0;
        _clock.Rewind();
        _camera.Restart();
        Log($"pass {_pass} of {_options.Repeat}");
        NextSection();
        return true;
    }

    // KSA Metal's drain into the run's store (every present, frame shown, wait and large allocation), and its latest
    // present kept as the frame's driver numbers
    private void DrainDriver()
    {
        if (!_drain)
            return;
        _driverRecords.Clear();
        DriverStats.Drain(_driverRecords);
        foreach (var record in _driverRecords)
        {
            _store?.Add(record);
            if (record is PresentRecord p)
            {
                // The newest by its number: the one the drain's opening adds may come after a newer present
                if (_present is not { } latest || p.Stats.Present >= latest.Present)
                    _present = p.Stats;
                _profilers?.Zones.AddPresent(p.T, p.Stats.GpuMs);
            }
            else if (record is WaitRecord w)
                _profilers?.Zones.AddWait(w.T, w.Ms, w.What);
        }
    }

    // The rest of the drain into the store, and the driver stops recording
    private void CloseDrain()
    {
        if (!_drain)
            return;
        DrainDriver();
        if (DriverStats.DrainDropped() is > 0 and var dropped)
            _events.Add("note", text: $"KSA Metal's drain was full: {dropped} of its records lost");
        DriverStats.CloseDrain();
        _drain = false;
    }

    // The game's profilers' last frames and moments written, and their switches as they were
    private void StopProfilers()
    {
        var profilers = _profilers;
        _profilers = null;
        profilers?.Stop();
    }

    // The run failed with an exception: what it switched on in the game and its driver, off again, whatever else fails
    public void Abandon()
    {
        try
        {
            CloseDrain();
        }
        catch (Exception e)
        {
            Log($"the drain: {e.Message}");
        }
        try
        {
            StopProfilers();
        }
        catch (Exception e)
        {
            Log($"the game's profilers: {e.Message}");
        }
    }

    // Ends the run before the script's end: the player stopped it, or it failed (the watch found it stuck); the results
    // say why, and that it did not complete
    public void Stop(string why, bool failed = false)
    {
        if (Done)
            return;
        _events.Add(failed ? "error" : "stopped", ScenarioName, text: why);
        EndSection();
        _active = false;
        _frames?.Dispose();
        _frames = null;
        CloseDrain();
        StopProfilers();
        foreach (var probe in _probes)
            probe.End();
        Log($"stopped: {why}");
        _camera.Unhold();
        _camera.EndRig();
        Done = true;
    }

    private void EndSection()
    {
        if (ScenarioName == null)
            return;
        CloseWindow();
        if (DriverStats.Read() is { DrawableWidth: > 0 } d)
            _events.Add("drawable", ScenarioName, text: $"{d.DrawableWidth}x{d.DrawableHeight}");
        _inScenario = false;
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
        Log(FormattableString.Invariant($"{ScenarioName} {t:F1} s: '{step.Command}' -> {(ok ? "ok" : "failed")}"));
        if (!ok)
            Error(FormattableString.Invariant($"{t:F1} s: '{step.Command}' failed"));
    }

    // A console command. Those that move the camera elsewhere end the directives' hold on it.
    private bool Console(Step step)
    {
        if (step.Words[0] is "follow" or "camera" or "control" or "goto" or "altitude" or "lookat" or "moveto")
            _camera.ReleaseCamera();
        return Program.TerminalInterface.Execute(step.Command);
    }

    // The scenario playing, or null between them
    private string? ScenarioName => _section >= 0 && _section < _sections.Count && _inScenario ? _sections[_section].Name : null;

    // Something went wrong in the scenario playing: the run fails
    private void Error(string text)
    {
        _events.Add("error", ScenarioName, text: text);
        Log(text);
    }

    internal static void Log(string message)
    {
        // KSA redirects stderr into its own logger, so stdout it is
        System.Console.Out.WriteLine($"[benchmark] {message}");
        System.Console.Out.Flush();
    }
}
