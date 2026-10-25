using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HarmonyLib;
using KSA;
using KsaMetalBenchmark;

namespace KsaMetalHooks;

// The benchmark in the game (src/benchmark/): its director plays the tour after every frame of the game, and starts in
// one of three ways:
// - benchmark.sh in the port folder starts the game with KSA_BENCH_* set, in a documents folder of its own
//   (STARMAP_INSTANCE_PATH): the director begins with the game's first frame and quits the game at the end, and the
//   script writes the report.
// - The Mac Port tab's Run benchmark, in the running game: the player's game is saved (to disk, in case the game
//   doesn't survive the run, and in memory), the world becomes a new game's (its clock at the start, no vehicles), the
//   tour plays, and the player's game comes back; Esc stops it. The hooks write the report.
// - The same with "Restart the game first": the game starts again for it the way benchmark.sh starts it (a documents
//   folder of its own with a copy of the settings, a new game, none of the player's mods), and at the end the hooks
//   write the report, open its folder and quit the game.
// The benchmark's vehicles are added to the game's vehicle saves from the app (Contents/Resources/Benchmark/vehicles).
internal static class BenchmarkRun
{
    public record Result(string Folder, bool Passed, string Summary);

    private enum Mode { None, Script, InGame, Restarted }

    private const string SafetySave = "Before the benchmark";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string _folder = "";
    private static bool _installed;
    private static Mode _mode;
    private static bool _firstFrame = true, _startRequested;
    private static Benchmark? _run;
    private static string _out = "";
    private static bool _fast;
    // In the running game: what comes back after the run
    private static UniverseData? _saved;
    private static GameSave? _safety;
    private static bool _celestialNames, _locationNames, _drawUi;
    private static double _speed;
    private static long _logStart;
    private static Task? _finishing;
    // What system.txt says about the run (the report program asks macOS about the Mac after the run, not during it)
    private static string _system = "", _start = "", _sections = "";
    // macOS's memory pressure during the run (1 normal, 2 warning, 4 critical) and the swap used before it
    private static int _pressure = 1;
    private static double _swapBefore;
    private static readonly Stopwatch Watch = new();
    private static double _nextSample;
    private static Result? _last;
    private static bool _lastLooked;

    public static string Apply(Harmony harmony)
    {
        _folder = Path.Combine(Hooks.Resources, "Benchmark");
        if (!File.Exists(Path.Combine(_folder, "benchmark.txt")))
            return "off (not in the app)";
        harmony.Patch(Find.Method(typeof(Program), "OnFrame", typeof(double), typeof(double)),
            postfix: new HarmonyMethod(typeof(BenchmarkRun), nameof(FramePostfix)));
        _installed = true;
        if (Environment.GetEnvironmentVariable("KSA_BENCH_OUT") is not { Length: > 0 } output)
            return "ready";
        _out = output;
        _mode = Environment.GetEnvironmentVariable("KSA_BENCH_FINISH") == "1" ? Mode.Restarted : Mode.Script;
        return _mode == Mode.Restarted ? "a run starts with the game, restarted from its settings" : "a run starts with the game";
    }

    // Why a run can't start from the tab now, or null
    public static string? CannotStart()
    {
        if (!_installed)
            return "The benchmark is not in this app, or its hook was left out (logs/console.log says which).";
        if (_run != null || _startRequested || _finishing is { IsCompleted: false })
            return "A benchmark is running.";
        if (_mode == Mode.Script)
            return "This game is a benchmark.sh run.";
        if (Program.EditorFlag)
            return "Leave the vehicle editor first.";
        if (Universe.CurrentSystem == null)
            return "No game is loaded.";
        if (PortSettings.Get("benchmark_restart")?.Saved == "true" && Restart.Launcher == null)
            return "The game was not started by the app, so it can't start itself again.";
        return null;
    }

    public static void StartFromTab(bool full, bool restart)
    {
        _fast = !full;
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
            Hooks.Log($"benchmark: could not restart the game for it: {e}");
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
                    ? Directory.GetDirectories(runs).Where(d => File.Exists(Path.Combine(d, "report.json"))).OrderBy(d => d, StringComparer.Ordinal).LastOrDefault()
                    : null;
                if (newest != null)
                    _last = ReadResult(newest);
            }
            return _last;
        }
    }

    public static void Show(string folder)
    {
        var report = Path.Combine(folder, "report.md");
        Process.Start("/usr/bin/open", File.Exists(report) ? new[] { "-R", report } : new[] { folder });
    }

    private static void FramePostfix()
    {
        try
        {
            Frame();
        }
        catch (Exception e)
        {
            Hooks.Log($"benchmark: {e}");
            _run = null;
            // The player's game back, or out of the game that started again for the run
            if (_mode == Mode.InGame)
                PutBack();
            else if (_mode == Mode.Restarted)
                Program.Instance.RequestExit(1);
            _mode = Mode.None;
        }
    }

    private static void Frame()
    {
        if (_firstFrame)
        {
            _firstFrame = false;
            if (_mode is Mode.Script or Mode.Restarted)
            {
                _fast = Environment.GetEnvironmentVariable("KSA_BENCH_FAST") is { Length: > 0 };
                _system = Universe.CurrentSystem?.Id ?? "?";
                _start = "a new game, restarted from the game's settings";
                AddVehicles();
                _run = Benchmark.Begin(Environment.GetEnvironmentVariable);
                StartWatching();
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
        // Esc stops a run the hooks started (it opens the settings)
        if (_mode != Mode.Script && (GameSettings.Show || EscMenu.Open))
        {
            GameSettings.Close();
            EscMenu.Open = false;
            _run.Stop("stopped with Esc");
        }
        else
            _run.OnFrame();
        Sample();
        if (_run.Done && _mode != Mode.Script)
            End();
    }

    private static void AddVehicles()
    {
        var vehicles = Path.Combine(_folder, "vehicles");
        foreach (var dir in Directory.Exists(vehicles) ? Directory.GetDirectories(vehicles) : [])
            if (UncompressedVehicleSave.FromDirectory(dir) == null)
                Hooks.Log($"benchmark: the vehicle {Path.GetFileName(dir)} could not be read");
    }

    // The director's options for a run from the tab
    private static Dictionary<string, string> Options(string output)
    {
        var script = Path.Combine(_folder, "benchmark.txt");
        // Fast mode's short test, or the cinematic tour: the tour's "fast" and "tour" lines
        var only = File.ReadLines(script).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.Length >= 2 && w[0] == (_fast ? "fast" : "tour")).Select(w => string.Join(' ', w[1..])).FirstOrDefault() ?? "";
        return new Dictionary<string, string>
        {
            ["KSA_BENCH_SCRIPT"] = script, ["KSA_BENCH_OUT"] = output, ["KSA_BENCH_ONLY"] = only,
            ["KSA_BENCH_FAST"] = _fast ? "1" : "", ["KSA_BENCH_END"] = "stay"
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

    // system.txt's facts about the run, for the report program, which adds the Mac's
    private static string[] SystemFacts()
    {
        var outside = PortSettings.All.Where(s => s.Outside != null).Select(s => s.Outside!.Replace(Home, "~")).ToList();
        var port = PortSettings.All.Where(s => s.Variable.Length > 0 && s.Running != s.Default).Select(s => $"{s.Key} = {s.Running}").ToList();
        return
        [
            $"StarMap={(AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "StarMap.Core") ? "in the app" : "none")}",
            $"System={_system}", $"Sections={Environment.GetEnvironmentVariable("KSA_BENCH_ONLY") ?? _sections}",
            $"Mode={(_fast ? "fast" : "cinematic")}", "Preset=none, own settings", "Settings changed=none", $"Start={_start}",
            $"KSA Metal switches={(outside.Count > 0 ? string.Join(' ', outside) : "none")}", "Tools=none",
            // What this game ran with: the port settings that aren't at their defaults, and what the hooks did
            $"port settings={(port.Count > 0 ? string.Join(", ", port) : "defaults")}", $"Hooks={Hooks.Summary.Replace(Home, "~")}"
        ];
    }

    private static string Home => Environment.GetEnvironmentVariable("HOME") ?? "~";
    private static string App => Path.GetFullPath(Path.Combine(Hooks.Resources, "..", ".."));
    private static string ConsoleLog => Path.Combine(Path.GetDirectoryName(PortSettings.FilePath) ?? Constants.DocumentsFolderPath, "logs", "console.log");

    // In the running game
    private static void BeginInGame()
    {
        _mode = Mode.InGame;
        _out = NewRunFolder();
        var options = Options(_out);
        options["KSA_BENCH_START"] = "game";
        GameSettings.Current.Save();
        File.Copy(Path.Combine(Constants.DocumentsFolderPath, "settings.toml"), Path.Combine(_out, "settings.toml"), true);
        (_system, _sections, _start) = (Universe.CurrentSystem?.Id ?? "?", options["KSA_BENCH_ONLY"], "in the running game, from its settings");
        Console.Out.Flush();
        _logStart = File.Exists(ConsoleLog) ? new FileInfo(ConsoleLog).Length : 0;
        Hooks.Log($"benchmark: playing in this game, into {_out.Replace(Home, "~")}");

        // The player's game: on disk in case the game doesn't survive the run, and in memory to come back, with what
        // the run changes of the settings
        var ui = GameSettings.Current.Interface;
        (_celestialNames, _locationNames, _drawUi) = (ui.CelestialNames, ui.LocationNames, Program.DrawUI);
        _saved = UniverseData.Create();
        // The speed the player had: the settings window pauses the game while it is open, and keeps the speed to go back to
        _speed = AccessTools.Field(typeof(Program), "_menuPauseActive")?.GetValue(null) is true &&
                 AccessTools.Field(typeof(Program), "_menuPauseRestoreSpeed")?.GetValue(null) is double restore
            ? restore : Universe.GetSimulationSpeed();
        _safety = UncompressedSave.Make(SafetySave);
        // A new game's world: the clock at its start, no vehicles, no kittens on missions
        var fresh = UniverseData.Create();
        fresh.GameTime = new UniverseTimeReference(UniverseTime.Zero);
        foreach (var system in fresh.CelestialSystems)
        {
            system.Vehicles.Clear();
            system.GroundClutter.Clear();
        }
        fresh.KittenRoster = KittenRosterData.Create();
        Universe.DeserializeSave(fresh);
        Program.OnGameLoaded();
        // The tour's view: no names over celestials and places (as benchmark.sh sets them)
        (ui.CelestialNames, ui.LocationNames) = (false, false);
        AddVehicles();
        _run = Benchmark.Begin(name => options.GetValueOrDefault(name));
        StartWatching();
        if (_run == null)
        {
            Hooks.Log("benchmark: nothing to play");
            PutBack();
        }
    }

    // The player's game as it was
    private static void PutBack()
    {
        if (_saved == null)
            return;
        Universe.DeserializeSave(_saved);
        Program.OnGameLoaded();
        _saved = null;
        // Loading a game runs it at real time: the speed it had (paused, perhaps)
        InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(_speed) });
        var ui = GameSettings.Current.Interface;
        (ui.CelestialNames, ui.LocationNames, Program.DrawUI) = (_celestialNames, _locationNames, _drawUi);
        VehicleSaves.Refresh();
        _safety?.Delete();
        _safety = null;
    }

    // With "Restart the game first": the run's documents folder, its settings and system.txt, then the game starts
    // again with the director's options
    private static void RestartForRun()
    {
        var instance = Path.Combine(Home, "Library", "Caches", "ksa-macos-port", "benchmark", "instance");
        if (Directory.Exists(instance))
            Directory.Delete(instance, true);
        Directory.CreateDirectory(instance);
        // The game alone (none of the player's mods), and their port settings
        File.WriteAllText(Path.Combine(instance, "manifest.toml"), "[[mods]]\nid = \"Core\"\nenabled = true\n");
        if (File.Exists(PortSettings.FilePath))
            File.Copy(PortSettings.FilePath, Path.Combine(instance, "port-settings.toml"));
        // Their settings, starting straight into a new testing game (the one that keeps the system's vehicles), without
        // the system menu, the update popup or names: Earth and the Moon alone for fast mode, as benchmark.sh plays it
        GameSettings.Current.Save();
        var system = _fast ? "SolLite" : "Sol";
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
        options["STARMAP_INSTANCE_PATH"] = instance;
        options["KSA_BENCH_FINISH"] = "1";
        Hooks.Log($"benchmark: the game starts again for it, into {output.Replace(Home, "~")}");
        Restart.Run(options);
        Program.Instance.RequestExit(0);
    }

    private static void StartWatching()
    {
        _pressure = 1;
        _swapBefore = SwapUsedMb();
        Watch.Restart();
        _nextSample = 0;
    }

    // Once a second: macOS's memory pressure
    private static void Sample()
    {
        if (Watch.Elapsed.TotalSeconds < _nextSample)
            return;
        _nextSample = Watch.Elapsed.TotalSeconds + 1;
        _pressure = Math.Max(_pressure, Sysctl("kern.memorystatus_vm_pressure_level"));
    }

    // The run has ended: the player's game comes back, then the report is written in the background
    private static void End()
    {
        var mode = _mode;
        _run = null;
        if (mode == Mode.InGame)
            PutBack();
        var run = new List<string> { mode == Mode.InGame ? "Game: kept running" : "Game: quit normally",
            $"Memory pressure: {(_pressure <= 1 ? "normal" : _pressure == 2 ? "warning" : "critical")}",
            FormattableString.Invariant($"Swap used: {_swapBefore:F2} -> {SwapUsedMb():F2} MB") };
        File.WriteAllLines(Path.Combine(_out, "run.txt"), run);
        File.Delete(Path.Combine(_out, "heartbeat"));
        // The game's log of the run, without the home folder's name
        Console.Out.Flush();
        if (File.Exists(ConsoleLog))
        {
            using var log = new FileStream(ConsoleLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            log.Seek(mode == Mode.InGame ? Math.Min(_logStart, log.Length) : 0, SeekOrigin.Begin);
            File.WriteAllText(Path.Combine(_out, "game.log"), new StreamReader(log).ReadToEnd().Replace(Home, "~"));
        }
        // system.txt now (asking macOS about the Mac takes a moment, which the run doesn't measure), the report after
        var output = _out;
        string[] facts = ["system", output, App, .. SystemFacts()];
        _finishing = Task.Run(() =>
        {
            Report(facts);
            Report(["finish", output]);
        });
        Hooks.Log("benchmark: writing the report");
    }

    private static void Finished()
    {
        _finishing = null;
        _last = ReadResult(_out);
        Hooks.Log($"benchmark: {(_last.Passed ? "passed" : "done, with problems")}, report in {_out.Replace(Home, "~")}");
        if (_mode == Mode.Restarted)
        {
            Process.Start("/usr/bin/open", new[] { _out });
            Program.Instance.RequestExit(0);
            return;
        }
        _mode = Mode.None;
        TimedAlert.Create(_last.Passed ? "Benchmark done: Settings > Mac Port shows its report" : "Benchmark done, with problems: Settings > Mac Port shows its report",
            default, 8);
    }

    private static Result ReadResult(string folder)
    {
        try
        {
            var report = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "report.json")))!;
            bool passed = report["passed"]?.GetValue<bool>() == true;
            var problems = (report["problems"] as JsonArray ?? new JsonArray()).Select(p => (string?)p).OfType<string>().ToList();
            var summary = $"{Path.GetFileName(folder)}: " + (passed ? "passed." : "problems: " + string.Join("; ", problems.Take(3)) +
                (problems.Count > 3 ? $" and {problems.Count - 3} more." : "."));
            return new Result(folder, passed, summary + " Shows report.md in the Finder.");
        }
        catch (Exception)
        {
            return new Result(folder, false, $"{Path.GetFileName(folder)}: no report (the run did not finish).");
        }
    }

    // The report program (KsaMetalBenchmarkReport.dll), on the app's .NET runtime
    private static void Report(string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Hooks.Resources, "..", "MacOS", "dotnet"))
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(_folder, "KsaMetalBenchmarkReport.dll"));
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (errors.Length > 0)
            Hooks.Log($"benchmark: the report program said: {errors.Trim()}");
        _ = output.Result;
    }

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
