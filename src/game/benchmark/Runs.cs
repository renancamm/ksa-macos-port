using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KSA;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

// The benchmark's runs in the game: the director (Director.cs) plays a test after every frame of the game, when KSA
// Metal Hooks call AfterFrame (src/game/hooks/BenchmarkRun.cs, a postfix on Program.OnFrame). A run starts in one of
// three ways:
// - benchmark.sh in the port folder starts the game with KSA_BENCH_OUT naming the run's folder, which holds the run's
//   options (RunOptions, options.json), in a documents folder of its own (STARMAP_INSTANCE_PATH): the director begins with
//   the game's first frame and quits the game at the end, and the script writes the report.
// - The Mac Port tab's RUN buttons, in the running game: the player's game is saved (to disk, in case the game doesn't
//   survive the run, and in memory), the world becomes a new game's (its clock at the start, no vehicles), the test
//   plays, and the player's game comes back; Esc stops it. The benchmark writes the record and the report (Finish.cs).
// - The same with "Restart for a Clean Run": the player's game is saved, the game starts again for it the way
//   benchmark.sh starts it (a documents folder of its own with a copy of the settings, a new game, none of the
//   player's mods), and at the end the benchmark writes the report, opens its folder and quits the game.
// The benchmark's vehicles are added to the game's vehicle saves from the app (Contents/Resources/Benchmark/vehicles).
// The hooks lend it their log, the summary of what they applied and the game's restart (Install).
public static class Runs
{
    public record Result(string Folder, bool Passed, bool Stopped, string Summary, string Outcome, string Date);

    private enum Mode { None, Script, InGame, Restarted }

    private const string SafetySave = "Before the benchmark";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string _folder = "";
    private static bool _installed;
    private static Mode _mode;
    private static bool _firstFrame = true, _startRequested;
    private static Director? _run;
    private static string _out = "";
    private static string _test = "mixed";
    // In the running game: the player's game, which comes back after the run
    private static PlayerGame? _player;
    private static long _logStart;
    private static Task? _finishing;
    // The system the run plays in, for its record (which asks macOS about the Mac after the run, not during it)
    private static string _system = "";
    // The options of a run that starts with the game (run.json in KSA_BENCH_OUT's folder)
    private static RunOptions? _options;
    private static readonly MemoryWatch Memory = new();
    // A run from the tab has no script to stop a game that has hung or is stuck: a thread of its own does, by the same rules
    private static bool _watching;
    private static volatile StuckRun? _stuck;

    // A run the watch found stuck, and the words for its end
    private sealed record StuckRun(Director Run, string Why);
    private static Result? _last;
    private static bool _lastLooked;

    // What the hooks lend the runs: their log, the summary of what they applied, and the game's restart with an
    // environment (null when the game didn't start from the app's launcher)
    private static Action<string> _log = Console.WriteLine;
    private static Func<string> _hooksSummary = () => "";
    private static Action<IReadOnlyDictionary<string, string>>? _restart;

    private static void Log(string message) => _log(message);

    // The app's Contents/Resources: the benchmark is in its Benchmark folder
    private static string Resources =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(Runs).Assembly.Location)!, ".."));

    // Called by the hooks once they call AfterFrame after every frame: what the hooks lend, and the run that starts
    // with the game (KSA_BENCH_OUT). Returns what the log says.
    public static string Install(Action<string> log, Func<string> hooksSummary,
        Action<IReadOnlyDictionary<string, string>>? restart)
    {
        _log = log;
        RunStore.Log = log;
        _hooksSummary = hooksSummary;
        _restart = restart;
        _folder = Path.Combine(Resources, "Benchmark");
        _installed = true;
        if (Environment.GetEnvironmentVariable("KSA_BENCH_OUT") is not { Length: > 0 } output)
            return "ready";
        _options = RunOptions.Read(output);
        if (_options == null)
            return $"ready (no run in {output.Replace(Home, "~")}: its {RunOptions.FileName} can't be read)";
        _out = output;
        _mode = _options.Restarted ? Mode.Restarted : Mode.Script;
        return _mode == Mode.Restarted ? "a run starts with the game, restarted from its settings" : "a run starts with the game";
    }

    // Why a run can't start from the tab now, or null
    public static string? CannotStart()
    {
        if (!_installed)
            return "The benchmark is not in this app, or this KSA version moved the game's code it needs (logs/console.log says which).";
        if (_run != null || _startRequested || _finishing is { IsCompleted: false })
            return "A benchmark is running.";
        if (_mode == Mode.Script)
            return "This game is a benchmark.sh run.";
        if (Program.EditorFlag)
            return "Leave the vehicle editor first.";
        if (Universe.CurrentSystem == null)
            return "No game is loaded.";
        if (PortSettings.Get("restart_for_a_clean_run")?.Saved == "true" && _restart == null)
            return "The game was not started by the app, so it can't start itself again.";
        try
        {
            Lineup.Read(Path.Combine(_folder, Lineup.FileName));
        }
        catch (Exception e) when (e is LineupException or IOException)
        {
            return $"The benchmark's tests can't be read ({e.Message}): make the app again with port.sh.";
        }
        return null;
    }

    public static void StartFromTab(string test, bool restart)
    {
        _test = test;
        if (!restart)
        {
            // On the next frame, outside the settings window's drawing
            _startRequested = true;
            GameSettings.Close();
            return;
        }
        try
        {
            RestartForRun();
        }
        catch (Exception e)
        {
            Log($"BenchmarkRun: could not restart the game for it: {e}");
            TimedAlert.CreateWarning("The benchmark could not start");
        }
    }

    // What the last run left: this session's, or the newest report in the benchmarks folder
    public static Result? Last
    {
        get
        {
            if (_last == null && !_lastLooked)
            {
                _lastLooked = true;
                var runs = Path.Combine(Constants.DocumentsFolderPath, "benchmarks");
                var newest = Directory.Exists(runs)
                    ? Directory.GetDirectories(runs).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}_\d{4}") && (File.Exists(Path.Combine(d, RunRecord.FileName)) || File.Exists(Path.Combine(d, "report.json"))))
                        .OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
                    : null;
                if (newest != null)
                    _last = ReadResult(newest);
            }
            return _last;
        }
    }

    // Its page in the browser, or the folder of a run that has none
    public static void Show(string folder)
    {
        var page = Path.Combine(folder, "report.html");
        Process.Start("/usr/bin/open", new[] { File.Exists(page) ? page : folder });
    }

    // After every frame of the game
    public static void AfterFrame()
    {
        try
        {
            Frame();
        }
        catch (Exception e)
        {
            Log($"BenchmarkRun: {e}");
            _run = null;
            try
            {
                // The player's game back, or out of the game that started again for the run
                if (_mode == Mode.InGame)
                    PutBack();
                else if (_mode == Mode.Restarted)
                    Program.Instance.RequestExit(1);
            }
            catch (Exception again)
            {
                Log($"BenchmarkRun: the game could not be put back: {again}");
                TimedAlert.CreateWarning($"The benchmark failed: your game is in the save \"{SafetySave}\"");
            }
            _mode = Mode.None;
        }
    }

    // Before each step of the simulation: the director's limit on it
    public static void BeforeStep()
    {
        try
        {
            _run?.BeforeStep();
        }
        catch (Exception e)
        {
            Log($"BenchmarkRun: {e}");
        }
    }

    private static void Frame()
    {
        if (_firstFrame)
        {
            _firstFrame = false;
            if (_mode is Mode.Script or Mode.Restarted)
            {
                if (_options!.Test.Length > 0)
                    _test = _options.Test;
                _system = Universe.CurrentSystem?.Id ?? "?";
                // benchmark.sh says what it did itself; a restart from the tab says it here
                if (_mode == Mode.Restarted)
                    Hosted(_options, "a new game, restarted from the game's settings");
                AddVehicles();
                _run = Director.Begin(_options);
                Memory.Start();
            }
        }
        if (_startRequested)
        {
            _startRequested = false;
            BeginInGame();
        }
        if (_finishing is { IsCompleted: true })
            Finished();
        if (_run == null)
            return;
        if (!_watching && _mode != Mode.Script)
        {
            _watching = true;
            new Thread(WatchForHang) { IsBackground = true, Name = "Benchmark watch" }.Start();
        }
        // The watch found the run stuck
        if (_stuck is { } stuck && stuck.Run == _run)
            _run.Stop(stuck.Why, failed: true);
        // Esc stops a run from the game (it opens the settings)
        else if (_mode != Mode.Script && (GameSettings.Show || EscMenu.Open))
        {
            GameSettings.Close();
            EscMenu.Open = false;
            _run.Stop("stopped with Esc");
        }
        else
            _run.OnFrame();
        Memory.Sample();
        if (_run.Done && _mode != Mode.Script)
            End();
    }

    private static void WatchForHang()
    {
        // A display that sleeps stops the frames (benchmark.sh does the same for its runs)
        try
        {
            Process.Start("/usr/bin/caffeinate", ["-d", "-i", "-w", Environment.ProcessId.ToString(Inv)]);
        }
        catch (Exception e)
        {
            Log($"BenchmarkRun: caffeinate: {e.Message}");
        }
        // Any test of the tab's may have time warp (full has the Warp Test)
        int limit = Watchdog.Limit(_test is "warp" or "full");
        while (true)
        {
            Thread.Sleep(1000);
            if (_run is not { Done: false } run)
                continue;
            var played = EventLog.Read(_out);
            if (Watchdog.Hung(played, 1, limit) is not { } stale)
            {
                // A stuck game still draws its frames: the run stops there, as with Esc, failed, and the player's game comes back
                if (_stuck?.Run != run && Watchdog.Stuck(played, 1, limit) is { } still)
                    _stuck = new StuckRun(run, $"stuck{Watchdog.Where(still)} (its clock stood still for {limit} s)");
                continue;
            }
            // What the director measured is in its events and frames, and the player's game is in the save on disk
            Log($"BenchmarkRun: no frame for {limit} s: stopping the game. Your game is in the save \"{SafetySave}\", the run's results in {_out.Replace(Home, "~")}");
            try
            {
                new EventLog(_out, 1).Add("ended", text: $"hung{Watchdog.Where(stale)} (no frame for {limit} s): stopped");
                CopyLog(_mode);
                Finish.Run(_out);
            }
            catch (Exception e)
            {
                Log($"BenchmarkRun: the report of the stopped run: {e.Message}");
            }
            Environment.Exit(1);
        }
    }

    private static void AddVehicles()
    {
        var vehicles = Path.Combine(_folder, "vehicles");
        foreach (var dir in Directory.Exists(vehicles) ? Directory.GetDirectories(vehicles) : [])
            if (UncompressedVehicleSave.FromDirectory(dir) == null)
                Log($"BenchmarkRun: the vehicle {Path.GetFileName(dir)} could not be read");
    }

    // The director's options for a run from the tab
    private static RunOptions Options(string output)
    {
        var script = Path.Combine(_folder, Lineup.FileName);
        // The test's scenarios; full plays the three in one game here
        var lineup = Lineup.Read(script);
        var tests = (_test == "full" ? Lineup.Full : [_test]).Select(t => lineup.Test(t)!).ToList();
        return new RunOptions
        {
            Script = script, Out = output, Only = string.Join(' ', tests.SelectMany(t => t.Scenarios)), Test = _test, Stay = true,
            Mode = string.Join(' ', tests.Select(t => Lineup.Name(t.Kind))),
            // The noise profiles benchmark.sh calibrate measured on this Mac, next to the runs
            NoiseFolder = Path.GetDirectoryName(output) ?? ""
        };
    }

    private static string NewRunFolder()
    {
        var runs = Path.Combine(Constants.DocumentsFolderPath, "benchmarks");
        var name = DateTime.Now.ToString("yyyy-MM-dd_HHmm", Inv);
        var folder = Path.Combine(runs, name);
        if (Directory.Exists(folder))
            folder += DateTime.Now.ToString("_ss", Inv);
        Directory.CreateDirectory(folder);
        return folder;
    }

    // What a run from the tab says about itself for its record, which adds the Mac's (and the defaults of those left out:
    // no preset, no settings changed, no tools)
    private static List<Fact> HostFacts(RunOptions options, string start)
    {
        var outside = PortSettings.All.Where(s => s.Outside != null).Select(s => s.Outside!.Replace(Home, "~")).ToList();
        var port = PortSettings.All.Where(s => s.Variable.Length > 0 && s.Running != s.Default).Select(s => $"{s.Key} = {s.Running}").ToList();
        return
        [
            new("StarMap", AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "StarMap.Core") ? "in the app" : "none"),
            new("System", Universe.CurrentSystem?.Id ?? _system), new("Test", _test), new("Scenarios", options.Only), new("Mode", options.Mode),
            new("Start", start), new("KSA Metal switches", outside.Count > 0 ? string.Join(' ', outside) : "none"),
            // What this game ran with: the port settings that aren't at their defaults, and what the hooks did
            new("port settings", port.Count > 0 ? string.Join(", ", port) : "defaults"), new("Hooks", _hooksSummary().Replace(Home, "~"))
        ];
    }

    private static string Home => Environment.GetEnvironmentVariable("HOME") ?? "~";
    private static string App => Path.GetFullPath(Path.Combine(Resources, "..", ".."));
    public static string ConsoleLog => Path.Combine(Path.GetDirectoryName(PortSettings.FilePath) ?? Constants.DocumentsFolderPath, "logs", "console.log");

    // In the running game
    // A run the game hosts itself: its options as the record will read them, and its start
    private static void Hosted(RunOptions options, string start)
    {
        options.App = App;
        options.Facts = HostFacts(options, start);
        options.Write(_out);
        var events = new EventLog(_out, 1);
        events.Add("start", text: start);
        events.Add("session");
    }

    private static void BeginInGame()
    {
        _mode = Mode.InGame;
        _out = NewRunFolder();
        var options = _options = Options(_out);
        options.InGame = true;
        GameSettings.Current.Save();
        File.Copy(Path.Combine(Constants.DocumentsFolderPath, "settings.toml"), Path.Combine(_out, "settings.toml"), true);
        _system = Universe.CurrentSystem?.Id ?? "?";
        Hosted(options, "in the running game, from its settings");
        Console.Out.Flush();
        _logStart = File.Exists(ConsoleLog) ? new FileInfo(ConsoleLog).Length : 0;
        Log($"BenchmarkRun: playing in this game, into {_out.Replace(Home, "~")}");

        _player = new PlayerGame();
        World.NewWorld();
        // The test's view: no names over celestials and places (as benchmark.sh sets them)
        var ui = GameSettings.Current.Interface;
        (ui.CelestialNames, ui.LocationNames) = (false, false);
        AddVehicles();
        _run = Director.Begin(options);
        Memory.Start();
        if (_run == null)
        {
            Log("BenchmarkRun: nothing to play");
            PutBack();
        }
    }

    // The player's game as it was, once (also when that fails halfway: the save on disk then has it)
    private static void PutBack()
    {
        if (_player is not { } player)
            return;
        _player = null;
        player.Restore();
    }

    // The player's game as a run in it finds it: the world, in memory to come back and on disk in case the game doesn't
    // survive the run, the speed it ran at, and what the run changes of the settings
    private sealed class PlayerGame
    {
        private readonly bool _celestialNames, _locationNames, _drawUi;
        private readonly UniverseData _world;
        private readonly double _speed;
        private readonly GameSave? _safety;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public PlayerGame()
        {
            var ui = GameSettings.Current.Interface;
            (_celestialNames, _locationNames, _drawUi) = (ui.CelestialNames, ui.LocationNames, Program.DrawUI);
            _world = UniverseData.Create();
            // The settings window pauses the game while it is open, and keeps the speed to go back to
            _speed = typeof(Program).GetField("_menuPauseActive", Static)?.GetValue(null) is true &&
                     typeof(Program).GetField("_menuPauseRestoreSpeed", Static)?.GetValue(null) is double restore
                ? restore : Universe.GetSimulationSpeed();
            _safety = UncompressedSave.Make(SafetySave);
        }

        public void Restore()
        {
            Universe.DeserializeSave(_world);
            Program.OnGameLoaded();
            // Loading a game runs it at real time: the speed it had (paused, perhaps)
            InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(_speed) });
            var ui = GameSettings.Current.Interface;
            (ui.CelestialNames, ui.LocationNames, Program.DrawUI) = (_celestialNames, _locationNames, _drawUi);
            VehicleSaves.Refresh();
            _safety?.Delete();
        }
    }

    // With "Restart for a Clean Run": the player's game saved, the run's documents folder and its settings, then the game
    // starts again with the director's options
    private static void RestartForRun()
    {
        if (UncompressedSave.Make(SafetySave) == null)
            throw new InvalidOperationException($"the game could not be saved as \"{SafetySave}\"");
        var instance = Path.Combine(Home, "Library", "Caches", "ksa-macos-port", "benchmark", "instance");
        if (Directory.Exists(instance))
            Directory.Delete(instance, true);
        Directory.CreateDirectory(instance);
        // The game alone (none of the player's mods), and their port settings
        File.WriteAllText(Path.Combine(instance, "manifest.toml"), "[[mods]]\nid = \"Core\"\nenabled = true\n");
        if (File.Exists(PortSettings.FilePath))
            File.Copy(PortSettings.FilePath, Path.Combine(instance, "port-settings.toml"));
        // Their settings, starting straight into a new testing game (the one that keeps the system's vehicles), without
        // the system menu, the update popup or names: Earth and the Moon alone for what plays only there, as benchmark.sh
        // plays it
        GameSettings.Current.Save();
        var system = _test is "mixed" or "warp" ? "SolLite" : "Sol";
        var settings = File.ReadAllLines(Path.Combine(Constants.DocumentsFolderPath, "settings.toml"));
        foreach (var (key, value) in new[] { ("selectSystemOnStart", "false"), ("lastSystemId", $"\"{system}\""),
                     ("startGameType", "\"Testing\""), ("startVehicle", "\"Rocket\""), ("startParent", "\"Earth\""),
                     ("startSituation", "\"RocketStartingSituation\""), ("checkForUpdates", "false"), ("celestialNames", "false"),
                     ("locationNames", "false") })
        {
            int at = Array.FindIndex(settings, l => l.StartsWith(key + " = ", StringComparison.Ordinal));
            if (at < 0)
                throw new InvalidOperationException($"settings.toml has no {key}");
            settings[at] = $"{key} = {value}";
        }
        File.WriteAllLines(Path.Combine(instance, "settings.toml"), settings);
        var output = NewRunFolder();
        File.Copy(Path.Combine(instance, "settings.toml"), Path.Combine(output, "settings.toml"));
        var options = Options(output);
        options.Restarted = true;
        options.Write(output);
        Log($"BenchmarkRun: the game starts again for it, into {output.Replace(Home, "~")}");
        var restart = _restart ?? throw new InvalidOperationException("the game was not started by the app");
        restart(new Dictionary<string, string> { ["STARMAP_INSTANCE_PATH"] = instance, ["KSA_BENCH_OUT"] = output });
        Program.Instance.RequestExit(0);
    }

    // The run has ended: the player's game comes back, then the record and the report are made in the background
    private static void End()
    {
        var mode = _mode;
        _run = null;
        if (mode == Mode.InGame)
            PutBack();
        var events = new EventLog(_out, 1);
        events.Add("ended", text: mode == Mode.InGame ? "kept running" : "quit normally");
        events.Add("memory", text: Memory.Text());
        CopyLog(mode);
        var output = _out;
        _finishing = Task.Run(() => Finish.Run(output));
        Log("BenchmarkRun: writing the report");
    }

    // The game's log of the run, without the home folder's name (in the running game, from where the run began)
    private static void CopyLog(Mode mode)
    {
        Console.Out.Flush();
        if (!File.Exists(ConsoleLog))
            return;
        using var log = new FileStream(ConsoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        log.Seek(mode == Mode.InGame ? Math.Min(_logStart, log.Length) : 0, SeekOrigin.Begin);
        File.WriteAllText(Path.Combine(_out, "game.log"), new StreamReader(log).ReadToEnd().Replace(Home, "~"));
    }

    private static void Finished()
    {
        if (_finishing?.Exception is { } e)
            Log($"BenchmarkRun: the report could not be written: {e.InnerException ?? e}");
        _finishing = null;
        _last = ReadResult(_out);
        Log($"BenchmarkRun: {(_last.Passed ? "passed" : "done, with problems")}, report in {_out.Replace(Home, "~")}");
        if (_mode == Mode.Restarted)
        {
            Show(_out);
            Program.Instance.RequestExit(0);
            return;
        }
        _mode = Mode.None;
        TimedAlert.Create(_last.Passed ? "Benchmark done: Settings > Mac Port opens its report" : _last.Stopped ? "Benchmark stopped: Settings > Mac Port opens what it measured" : "Benchmark done, with problems: Settings > Mac Port opens its report",
            default, 8);
    }

    private static Result ReadResult(string folder)
    {
        if (RunRecord.Read(folder) is not { } record)
            return new Result(folder, false, false, $"{Path.GetFileName(folder)}: no report (the run did not finish).", "Did not finish", RunDate(folder));
        bool passed = record.Status == Status.Passed, stopped = record.Status == Status.Stopped;
        var problems = record.Problems.Select(p => p.Text).ToList();
        var summary = $"{Path.GetFileName(folder)}: " + (passed ? "passed." : problems.Count == 0 ? "stopped." : "problems: " + string.Join("; ", problems.Take(3)) +
            (problems.Count > 3 ? $" and {problems.Count - 3} more." : "."));
        var outcome = passed ? "Passed" : stopped ? "Stopped" : problems.Count == 1 ? "1 problem" : $"{problems.Count} problems";
        // Its main number: "Passed, 30 FPS"
        if (record.Headline().Short() is { Length: > 0 } headline)
            outcome += $", {headline}";
        return new Result(folder, passed, stopped, summary + " Opens its page, with its numbers and screenshots.", outcome, RunDate(folder));
    }

    // When a run was, from its folder's name: 2026-10-31_0545 is "Oct 31, 05:45"
    private static string RunDate(string folder)
    {
        var name = Path.GetFileName(folder);
        return DateTime.TryParseExact(name.Length >= 15 ? name[..15] : name, "yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)
            : "";
    }

    // macOS's memory pressure during a run (1 normal, 2 warning, 4 critical), sampled once a second, and the swap used
    // before it and after
    private sealed class MemoryWatch
    {
        private readonly Stopwatch _watch = new();
        private int _pressure = 1;
        private double _swapBefore, _nextSample;

        public void Start()
        {
            _pressure = 1;
            _swapBefore = SwapUsedMb();
            _watch.Restart();
            _nextSample = 0;
        }

        public void Sample()
        {
            if (_watch.Elapsed.TotalSeconds < _nextSample)
                return;
            _nextSample = _watch.Elapsed.TotalSeconds + 1;
            _pressure = Math.Max(_pressure, Sysctl("kern.memorystatus_vm_pressure_level"));
        }

        // The memory event's text
        public string Text() =>
            FormattableString.Invariant($"pressure {(_pressure <= 1 ? "normal" : _pressure == 2 ? "warning" : "critical")}, swap {_swapBefore:F2} -> {SwapUsedMb():F2} MB");

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, ref long value, ref nint size, nint newValue, nint newSize);

        [DllImport("libc", EntryPoint = "sysctlbyname")]
        private static extern int SysctlByName(string name, [Out] long[] value, ref nint size, nint newValue, nint newSize);

        private static int Sysctl(string name)
        {
            long value = 0;
            nint size = sizeof(long);
            return SysctlByName(name, ref value, ref size, 0, 0) == 0 ? (int)value : 1;
        }

        // vm.swapusage is a struct xsw_usage: total, available and used bytes, then the page size and a flag
        private static double SwapUsedMb()
        {
            var usage = new long[4];
            nint size = usage.Length * sizeof(long);
            return SysctlByName("vm.swapusage", usage, ref size, 0, 0) == 0 ? usage[2] / 1048576.0 : 0;
        }
    }
}
