// A run's game sessions (BenchmarkRun, Run.cs): the game started as a bash job is, watched until it ends, hangs or
// is stopped, the tools around it, the report, and the progress line.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    private sealed partial class BenchmarkRun
    {
        // The script stands at one moment on purpose with --hold, and for as long as a tool takes at a checkpoint
        private bool StandsStill => _hold.Length > 0 || _capture || Triggers.HoldCheckpoints(_rules);

        // The passes of each game session, by what the run is for: measure's a warm-up and its measured passes, profile's a
        // warm-up, a measured and a profiled one; one measured pass otherwise
        private List<PassKind> Passes() => _purpose switch
        {
            Purpose.Measure => [PassKind.WarmUp, .. Enumerable.Repeat(PassKind.Measured, _repeat)],
            Purpose.Profile => [PassKind.WarmUp, PassKind.Measured, PassKind.Profiled],
            _ => [PassKind.Measured],
        };

        // The game's environment for the run, on top of this program's
        private ProcessStartInfo Game(Dictionary<string, string> env)
        {
            // Started as bash starts a job with &: SIGINT and SIGQUIT ignored, so that Ctrl-C at the terminal, which goes to
            // the whole process group, stops the run here and reaches the game only as the kill below. Its output goes
            // nowhere and it reads nothing, as a job's
            var start = new ProcessStartInfo("/bin/sh");
            foreach (var a in new[] { "-c", "trap '' INT QUIT; exec \"$0\" \"$@\" < /dev/null > /dev/null 2>&1", Path.Combine(_app, "Contents", "MacOS", "KSA") })
                start.ArgumentList.Add(a);
            start.Environment["STARMAP_INSTANCE_PATH"] = _instance;
            foreach (var (k, v) in env)
                start.Environment[k] = v;
            return start;
        }


        // --explore: the same game, paused, with the benchkey console command; the keys go to a folder of their own
        private int Explore()
        {
            var output = Path.Combine(Docs, "benchmarks", DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + "_explore");
            Directory.CreateDirectory(output);
            Tool.Exec("caffeinate", ["-u", "-t", "1"], out _);
            Output.Step($"Exploring KSA {_installed}: fly the free camera, type benchkey [name] in the console (\\) at each place, quit when done");
            new RunPlan { Play = new() { Explore = true } }.Write(output);
            using (var game = Process.Start(Game(new() { ["KSA_BENCH_OUT"] = output }))!)
            {
                // Ctrl-C here quits the game too, as it did the game in the foreground
                ConsoleCancelEventHandler quit = (_, e) =>
                {
                    e.Cancel = true;
                    try
                    {
                        game.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                };
                Console.CancelKeyPress += quit;
                game.WaitForExit();
                Console.CancelKeyPress -= quit;
            }
            Output.Info(File.Exists(Path.Combine(output, "keys.txt")) ? $"Captured keys: {output}/keys.txt" : "No keys captured.");
            return 0;
        }

        // How busy the GPU is now, in percent (the lower of two looks a second apart, as a window's move is a blip); empty
        // when macOS doesn't say
        private static string GpuBusy()
        {
            static string Look() => Regex.Match(Ask("ioreg", "-r", "-d", "1", "-c", "IOAccelerator"), @"""Device Utilization %""=([0-9]*)").Groups[1].Value;
            var first = Look();
            if (first.Length == 0 || int.Parse(first) < 30)
                return first;
            Thread.Sleep(1000);
            var second = Look();
            return Math.Min(int.Parse(first), second.Length > 0 ? int.Parse(second) : 0).ToString(CultureInfo.InvariantCulture);
        }

        private static string Swap() => Regex.Match(Ask("sysctl", "-n", "vm.swapusage"), @"used = ([0-9.]*)M").Groups[1].Value;

        // macOS's memory pressure level by its name: 1 normal, 2 warning, 4 critical
        private static string Pressure(int level) => level switch { 1 => "normal", 2 => "warning", _ => "critical" };

        private string Starmap() =>
            !File.Exists(Path.Combine(_res, "StarMap", "StarMap.dll")) ? "none"
            : File.Exists(Path.Combine(_res, "StarMap", "version.txt")) ? File.ReadAllText(Path.Combine(_res, "StarMap", "version.txt")).Trim()
            : "in the app";

        // What the run says about itself for its record, which adds the Mac's, the app's and the window
        private List<Fact> HostFacts()
        {
            var home = PortFolder.Home;
            // KSA Metal's switches: this program's environment's, and --env's
            var own = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(k => k.StartsWith("KSA_MTL_", StringComparison.Ordinal))
                .ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k) ?? "");
            foreach (var (k, v) in Env().Where(kv => kv.Key.StartsWith("KSA_MTL_", StringComparison.Ordinal)))
                own[k] = v;
            var switches = string.Join(' ', own.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}".Replace(home, "~")));
            var environment = string.Join(' ', Env().Where(kv => !kv.Key.StartsWith("KSA_MTL_", StringComparison.Ordinal)).Select(kv => $"{kv.Key}={kv.Value}".Replace(home, "~")));
            var tools = _tools.Count > 0 ? string.Join(", ", _tools) : "none";
            var preset = PresetFact;
            var changed = ChangedFact;
            return
            [
                new("StarMap", Starmap()), new("System", SystemId), new("Test", _test.Length > 0 ? _test : "places"), new("Scenarios", Sections),
                new("Mode", $"{Mode}{(_noShots ? ", no screenshots" : "")}"), new("Preset", preset), new("Settings changed", changed),
                new("Start", Start), new("KSA Metal switches", switches.Length > 0 ? switches : "none"), new("Tools", tools),
                .. environment.Length > 0 ? [new Fact("Environment", environment)] : Array.Empty<Fact>(),
                .. _probes.Count > 0 || _knobs.Count > 0
                    ? [new Fact("Probes", string.Join(", ", _probes.Select(p => p.Replace(home, "~"))) + (_knobs.Count > 0 ? $" ({string.Join(' ', _knobs)})" : ""))]
                    : Array.Empty<Fact>(),
                .. _off.Count > 0 ? [new Fact("Channels off", string.Join(", ", _off.Distinct()))] : Array.Empty<Fact>()
            ];
        }

        private const string Start = "a new game, from benchmark.sh";

        // The settings facts of the run: the preset and what changed on top of it
        private string PresetFact => _defaults ? $"{(_preset.Length > 0 ? _preset : "none")}, a fresh install's settings"
            : _preset.Length > 0 ? _preset : "none, own settings";

        private string ChangedFact
        {
            get
            {
                var changed = string.Join(' ', _sets) + (_ports.Count > 0 ? (_sets.Count > 0 ? " " : "") + "port-settings: " + string.Join(' ', _ports) : "");
                if (_settingsFile.Length > 0)
                    changed = Path.GetFileName(_settingsFile) + (changed.Length > 0 ? " " : "") + changed;
                return changed.Length > 0 ? changed : "none";
            }
        }

        // One of the report's steps after the game: a failure (a full disk, a file cut short) counts as one, as a step
        // of the report program that failed did, and the run goes on to its summary
        private static T Soft<T>(Func<T> step, T failed = default!)
        {
            try
            {
                return step();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                           or FormatException or InvalidOperationException or NullReferenceException)
            {
                Output.Warn($"the report program failed: {e.Message}");
                return failed;
            }
        }

        // The scenarios played, each with its length on the script's clock, in the lineup's order
        private List<(string Section, double Seconds)> Lengths()
        {
            var play = _only.Select(p => p.Split('_')[0]).ToHashSet();
            return _lineup.Scenarios.Where(s => play.Contains(s.Name)).Select(s => (s.Name, s.Length)).ToList();
        }

        private static bool _awake;

        // --env's names and values, a name given again taking its last value (a study's variant over its command)
        private Dictionary<string, string> Env()
        {
            var env = new Dictionary<string, string>();
            foreach (var kv in _env)
                env[kv[..kv.IndexOf('=')]] = kv[(kv.IndexOf('=') + 1)..];
            return env;
        }

        // 2. The run
        private int Play()
        {
            long memoryGb = long.Parse(Ask("sysctl", "-n", "hw.memsize")) / 1073741824;
            if (_system.Length == 0 && memoryGb <= 8)
                Output.Info($"This Mac has {memoryGb} GB of memory: if the game runs out of it, --system earth plays Earth and the Moon only.");
            // Results take up to a few hundred MB
            if (FreeGb() < 1)
                Die($"the results need 1 GB free on this disk ({FreeGb()} GB free). ./benchmark.sh clean deletes old runs.");

            if (Into.Length > 0)
                _out = Into;
            else
            {
                _out = Path.Combine(Benchmarks, DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
                if (_hold.Length > 0)
                    _out += "_hold";
                if (Directory.Exists(_out) || File.Exists(_out))
                    _out += "_" + DateTime.Now.ToString("ss", CultureInfo.InvariantCulture);
            }
            Directory.CreateDirectory(_out);
            File.Copy(_settings, Path.Combine(_out, "settings.toml"));
            var events = new EventLog(_out);
            events.Add("start", text: Start);
            // Keeps the display awake while this program lasts (the game needs it; a study's runs are one program), and wakes
            // it now: a display that went to sleep makes the game fail at start
            if (!_awake)
                Process.Start("caffeinate", ["-d", "-i", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
            _awake = true;
            Tool.Exec("caffeinate", ["-u", "-t", "1"], out _);

            // What the tools add to the game's environment, and to the run's plan; --env first, the tools' own over it
            var env = Env();
            var plan = new RunPlan { Passes = Passes() };
            if (_purpose == Purpose.Profile)
            {
                // KSA Metal can split render passes at the game's GPU zones in the profiled pass: memory for every attachment
                env["KSA_MTL_GPU_ZONES"] = "1";
            }
            if (_hud)
            {
                Directory.CreateDirectory(Path.Combine(_out, "hud"));
                foreach (var v in new[] { "MTL_HUD_ENABLED", "MTL_HUD_LOG_ENABLED", "MTL_HUD_LOG_SHADER_ENABLED", "MTL_HUD_ENCODER_TIMING_ENABLED",
                             "MTL_HUD_INSIGHTS_ENABLED", "KSA_MTL_LABELS" })
                    env[v] = "1";
                env["MTL_HUD_REPORT_URL"] = Path.Combine(_out, "hud");
            }
            if (_trace)
            {
                plan.Host.WaitForRecording = true;
                env["KSA_MTL_LABELS"] = "1";
            }
            if (_capture)
            {
                env["MTL_CAPTURE_ENABLED"] = env["KSA_MTL_LABELS"] = "1";
                plan.Record.Capture = true;
            }
            // Encoders named after the game's GPU zones, where the tool shows their names
            plan.Record.ZoneLabels = env.ContainsKey("KSA_MTL_LABELS");
            // The triggers, the defaults among them; catch's own look at every 31st image besides, KSA Metal's watch
            plan.On = _rules.Select(r => r.ToString()).ToList();
            plan.Against = _catch;
            if (_catch.Length > 0)
                env["KSA_MTL_WATCH"] = Path.Combine(_out, "caught");
            if (_rules.Any(r => r.Has(TriggerAction.Capture)))
                env["MTL_CAPTURE_ENABLED"] = env["KSA_MTL_LABELS"] = "1";
            plan.Record.Film = _film;
            plan.Record.CameraLog = _cameraLog;
            plan.Record.Probes = [.. _probes];
            foreach (var kv in _knobs)
                plan.Record.Knobs[kv[..kv.IndexOf('=')]] = kv[(kv.IndexOf('=') + 1)..];
            plan.Record.Off = [.. _off.Distinct()];

            // Loading takes up to a few minutes on a first start (shader compilation), then the script's own length, plus
            // the time slower Macs spend catching up at checkpoints. Once playing, the director writes a heartbeat event
            // every second: a game whose heartbeat gets old has hung (Watchdog.cs).
            var lengths = Lengths();
            // A pass on a slow Mac: a flight or a timed scenario plays its length, twice it with catching up; a views
            // scenario runs its setup in small steps, a few times slower than its length, and each of its views waits
            // up to 20 s to settle before it is measured and shot
            static double Pass(Scenario s) => s.Kind == Kind.Views
                ? s.Length * 10 + 30 * s.Steps.Count(st => st.Words[0] is "@shot" or "@measure")
                : s.Length * 2;
            int pass = (int)lengths.Sum(l => _lineup.Scenario(l.Section) is { } s ? Pass(s) : l.Seconds * 2);
            // Each session plays its plan's passes
            int timeout = pass * plan.Passes.Count + 300;
            // KSA_BENCH_HANG_LIMIT: other seconds (not a number: no hang check)
            var limit = Environment.GetEnvironmentVariable("KSA_BENCH_HANG_LIMIT");
            int HangLimit(int otherwise) => limit is not { Length: > 0 } ? otherwise : int.TryParse(limit, out var s) ? s : int.MaxValue;
            int hang = HangLimit(Watchdog.Limit(_capture || Triggers.TakeMinutes(_rules) ||
                                                _only.Any(p => _lineup.Scenario(p.Split('_')[0])?.Kind == Kind.Timed)));
            int pressure = 1;
            var swapBefore = Swap();
            var tools = _tools.Count > 0 ? string.Join(", ", _tools) : "none";
            if (_hold.Length > 0)
            {
                Output.Step($"Going to {_hold} in KSA {_installed}: it stays there, paused, until you quit the game");
                timeout = 0;
            }
            else
            {
                var what = _test switch
                {
                    "mixed" => "about a minute",
                    "tour" => "about 5 minutes",
                    "warp" => "about 3 minutes",
                    "full" => "about 9 minutes",
                    _ => $"about {((int)lengths.Sum(l => l.Seconds) + 60) / 60} minutes",
                };
                if (_test.Length > 0)
                    what = $"{_test}, {what}";
                int games = _sessions;
                // The passes: "a warm-up and 3 measured passes", "a warm-up, a measured and a profiled pass"
                var passes = Passes();
                if (passes.Count > 1)
                {
                    var said = passes.GroupBy(k => k).Select(g => g.Key == PassKind.WarmUp ? "a warm-up" : $"{(g.Count() == 1 ? "a" : g.Count().ToString(CultureInfo.InvariantCulture))} {RunPlan.Name(g.Key)}").ToList();
                    what = $"{what} a pass, {string.Join(", ", said.SkipLast(1))} and {said[^1]} pass{(passes.Count(k => k == passes[^1]) > 1 ? "es" : "")} " +
                           (games > 1 ? $"in each of {games} game sessions" : "in one game session");
                }
                Output.Step($"Benchmarking KSA {_installed} ({what})");
                Output.Info("The game takes over the screen. Leave the mouse and keyboard alone until it is done, or press Ctrl-C here to stop it.");
            }
            if (_tools.Count > 0)
                Output.Info($"With {tools}.");
            var busy = GpuBusy();
            if (busy.Length > 0 && int.Parse(busy) >= 30)
                Output.Warn($"the GPU is {busy} % busy before the game starts: something else is using it, and this run's numbers will show that.");
            else
                busy = "";

            // Ctrl-C (or TERM) while the game plays: stopped, and what it measured reported; after that, as usual
            ConsoleCancelEventHandler stop = (_, e) =>
            {
                e.Cancel = true;
                _stopped = true;
            };
            Console.CancelKeyPress += stop;
            var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c =>
            {
                c.Cancel = true;
                _stopped = true;
            });
            if (busy.Length > 0)
                events.Add("gpu", value: int.Parse(busy));
            // The sessions: in full each test in a game of its own; measure's sessions
            var groups = _test == "full"
                ? Lineup.Full.Select(t => _lineup.Test(t)!.Scenarios.ToList()).ToList()
                : [_only];
            int session = 0;
            for (int r = 1; r <= _sessions && !_stopped && !StoppedByTrigger(session); r++)
                foreach (var group in groups)
                    if (!_stopped && !StoppedByTrigger(session))
                        PlayOnce(++session, group, env, plan, lengths, timeout, hang, events, ref pressure);
            Console.CancelKeyPress -= stop;
            term.Dispose();
            // --defaults: the settings as the game saved them, with its defaults filled in
            if (_defaults)
                File.Copy(_settings, Path.Combine(_out, "settings.toml"), true);
            var swapAfter = Swap();
            events.Session = 0;
            events.Add("memory", text: $"pressure {Pressure(pressure)}, " +
                                       $"swap {(swapBefore.Length > 0 ? swapBefore : "?")} -> {(swapAfter.Length > 0 ? swapAfter : "?")} MB");

            ToolsOutput();
            if (_hold.Length > 0)
            {
                Output.Step($"Done: {_out}");
                return 0;
            }
            if (_purpose == Purpose.Profile)
                Soft(() => Quiet(() => Profile(_out)));

            // The record, the report and the zip to share: the report, every frame's numbers, the logs and crash reports of
            // the run, the settings, and the screenshots as JPEG; your home folder's name replaced by ~
            int failed = 0;
            var record = Soft(() => Finish.Run(_out));
            if (record?.Status != Status.Passed)
                failed = 1;
            if (!File.Exists(Path.Combine(_out, "report.md")))
                Die($"the run's report could not be written. What the run left is in {_out}");
            // A study's session: a line of its own, the study's report says the rest
            if (Into.Length > 0)
            {
                var h = record?.Headline();
                Output.Info($"{record?.Status.ToString() ?? "No record"}{(h?.GpuMs is { } gpu ? FormattableString.Invariant($", GPU {gpu:F2} ms a frame") : "")}" +
                            $"{(h?.Short() is { Length: > 0 } fps ? $", {fps}" : "")}{(record?.Problems.FirstOrDefault() is { } p ? $": {p.Text}" : "")} ({Path.GetFileName(_out)})");
                return failed;
            }
            Console.WriteLine();
            Show(_out);
            Console.WriteLine();
            if (_reference.Length > 0)
            {
                if (Soft(() => Compare(_reference, _out), 1) != 0)
                    failed = 1;
                Console.WriteLine();
            }

            var zip = Path.Combine(_out, Path.GetFileName(_out) + ".zip");
            Output.Step(_stopped ? $"Stopped: {_out}" : failed == 0 ? $"Done: {_out}" : $"Done, with problems: {_out}");
            Output.Info($"See it, with its pictures: ./benchmark.sh report {Path.GetFileName(_out)}");
            Output.Info($"Compare with another run: ./benchmark.sh compare <other run> {Path.GetFileName(_out)}");
            if (File.Exists(zip))
            {
                Output.Info($"To share it (up to you): {zip} ({Ask("du", "-h", zip).Split('\t')[0].Trim()}) holds the report, the numbers,");
                Output.Info($"the logs and the screenshots. Attach it to a new issue: {Issues}");
            }
            return failed;
        }

        // Whether a trigger's stop ended that game session: the run ends with it
        private bool StoppedByTrigger(int session) => session > 0 &&
            EventLog.Read(_out).Any(e => e.Session == session && e.Type is "stopped" or "error" &&
                                         e.Text?.StartsWith("stopped by the trigger", StringComparison.Ordinal) == true);

        // One game session: its plan in the run's folder, the game watched until it ends, hangs or is stopped, its
        // ending and its log
        private void PlayOnce(int n, List<string> only, Dictionary<string, string> env, RunPlan plan,
            List<(string Section, double Seconds)> lengths, int timeout, int hang, EventLog events, ref int pressure)
        {
            // This session's memory pressure at its worst and the swap at its start
            int sessionPressure = 1;
            var swapStart = Swap();
            if (n > 1)
                Output.Info($"Game {n}");
            events.Session = n;
            events.Add("session");
            plan.Play.Only = string.Join(' ', only);
            plan.Play.Fast = _fast;
            plan.Play.Mode = Mode;
            plan.Play.Hold = _hold;
            // The first session alone takes screenshots: the others measure (in full, each test's own)
            plan.Play.NoShots = _noShots || n > (_test == "full" ? Lineup.Full.Count : 1);
            plan.Host.NoiseFolder = Benchmarks;
            plan.Host.Session = n;
            plan.Play.Test = _test;
            plan.Play.Script = _script;
            plan.Host.App = _app;
            plan.Host.Facts = HostFacts();
            plan.Write(_out);
            using var game = Process.Start(Game(new Dictionary<string, string>(env) { ["KSA_BENCH_OUT"] = _out }))!;
            var started = DateTime.UtcNow;
            string ending = "";
            bool alongside = false;
            Process? tracer = null;
            var progress = new ProgressLine(lengths.Where(l => only.Any(o => o.Split('_')[0] == l.Section)).ToList());
            while (!game.HasExited)
            {
                int elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                var played = EventLog.Read(_out);
                var beat = EventLog.LastHeartbeat(played, n);
                progress.Show(beat, elapsed);
                // --trace: once the game is up (its first heartbeat), Instruments attaches, and the run starts when it records
                if (_trace && tracer == null && beat != null)
                    tracer = StartTrace(game.Id, events);
                // Another game at the same time shares the Mac's GPU and memory (and may be the end of this one)
                if (!alongside && GamesRunning().Any(pid => pid != game.Id))
                {
                    alongside = true;
                    Output.Warn("another game is running at the same time: this run's numbers won't count.");
                }
                // macOS's memory pressure: 1 normal, 2 warning, 4 critical
                if (int.TryParse(Ask("sysctl", "-n", "kern.memorystatus_vm_pressure_level"), out var level))
                {
                    pressure = Math.Max(pressure, level);
                    sessionPressure = Math.Max(sessionPressure, level);
                }
                if (_stopped)
                    ending = "was stopped with Ctrl-C";
                else if (Watchdog.Hung(played, n, hang) is { } stale)
                    ending = $"hung{Watchdog.Where(stale)} (no frame for {hang} s): stopped";
                else if (!StandsStill && Watchdog.Stuck(played, n, hang) is { } still)
                {
                    // What its threads were doing, before it is stopped (the stuck trigger)
                    if (_rules.Any(r => r.When == Condition.Stuck && r.Has(TriggerAction.Sample)) && !Watchdog.Sample(game.Id, _out, n, out var why))
                        Output.Warn($"sample: {why}");
                    ending = $"stuck{Watchdog.Where(still)} (its clock stood still for {hang} s): stopped";
                }
                else if (timeout > 0 && elapsed >= timeout)
                    ending = $"did not finish in {timeout} s: stopped";
                if (ending.Length > 0)
                {
                    progress.End();
                    if (!_stopped)
                        Output.Warn($"the game {ending}.");
                    try
                    {
                        game.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    break;
                }
                game.WaitForExit(1000);
            }
            progress.End();
            game.WaitForExit();
            if (ending.Length == 0)
            {
                // Where the game had got to
                var beat = EventLog.LastHeartbeat(_out, n);
                var where = beat != null ? Watchdog.Where(beat) : " before its first frame";
                int status = game.ExitCode;
                ending = status == 0 ? "quit normally"
                    : status is >= 130 and <= 199 ? $"crashed (signal {status - 128}){where}"
                    : $"quit with status {status}{where}";
            }
            if (_stopped)
                events.Add("stopped", text: "stopped with Ctrl-C");
            events.Add("ended", text: ending);
            var swapEnd = Swap();
            events.Add("session-memory", text: $"pressure {Pressure(sessionPressure)}, swap " +
                                               $"{(swapStart.Length > 0 ? swapStart : "?")} -> {(swapEnd.Length > 0 ? swapEnd : "?")} MB");
            if (alongside)
                events.Add("alongside");
            // Instruments stops recording when the game quits; it then writes the trace
            if (tracer != null)
            {
                Output.Info("Instruments is writing the trace");
                Tool.Exec("kill", ["-INT", tracer.Id.ToString(CultureInfo.InvariantCulture)], out _);
                tracer.WaitForExit();
            }
            // The game's log, without your home folder's name
            var log = Path.Combine(_instance, "logs", "console.log");
            if (File.Exists(log))
                File.WriteAllText(Path.Combine(_out, n > 1 ? $"game-{n}.log" : "game.log"), File.ReadAllText(log).Replace(PortFolder.Home, "~"));
        }

        private Process? StartTrace(int pid, EventLog events)
        {
            var log = Path.Combine(_out, "xctrace.log");
            // Points of Interest holds the parts (KSA Metal's signposts)
            var start = new ProcessStartInfo("/bin/sh");
            foreach (var a in new[] { "-c", "exec xcrun xctrace record \"$@\" > \"$0\" 2>&1", log, "--template", _template, "--instrument",
                         "Points of Interest", "--attach", pid.ToString(CultureInfo.InvariantCulture), "--output", Path.Combine(_out, "trace.trace"),
                         "--no-prompt" })
                start.ArgumentList.Add(a);
            var tracer = Process.Start(start)!;
            for (int waited = 0; waited < 60 && !tracer.HasExited; waited++)
            {
                if (File.Exists(log) && Regex.IsMatch(File.ReadAllText(log), "recording|ctrl-c", RegexOptions.IgnoreCase))
                    break;
                Thread.Sleep(1000);
            }
            if (tracer.HasExited)
                Output.Warn($"Instruments did not start: {string.Join(' ', File.ReadAllLines(log).TakeLast(3))}");
            Thread.Sleep(2000);
            events.Add("recording");
            return tracer;
        }

        // The tools' output
        private void ToolsOutput()
        {
            if (_hud)
            {
                // The HUD's log, from the game's: once a second "metal-HUD: <first frame>,<graphics MB>,<process MB>" and
                // each frame's present interval and GPU time (ms), and the shaders it saw compiled
                var log = Path.Combine(_out, "game.log");
                var lines = File.Exists(log) ? File.ReadLines(log).Where(l => l.Contains("metal-HUD:") || l.Contains("CompileShader")).ToList() : [];
                File.WriteAllLines(Path.Combine(_out, "hud.log"), lines);
                Output.Info($"Metal HUD: {lines.Count(l => l.Contains("metal-HUD"))} log lines in hud.log, reports in hud/");
            }
            if (_trace && Directory.Exists(Path.Combine(_out, "trace.trace")))
            {
                // Its tables of the parts and of the GPU's work, for the report program's summary (trace.md)
                var trace = Path.Combine(_out, "trace.trace");
                Tool.Exec("xcrun", ["xctrace", "export", "--input", trace, "--toc", "--output", Path.Combine(_out, "trace-toc.xml")], out _);
                Tool.Exec("xcrun", ["xctrace", "export", "--input", trace, "--output", Path.Combine(_out, "trace-parts.xml"), "--xpath",
                    "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"OSSignpostIntervals\"]"], out _);
                Tool.Exec("xcrun", ["xctrace", "export", "--input", trace, "--output", Path.Combine(_out, "trace-gpu.xml"), "--xpath",
                    "/trace-toc/run[@number=\"1\"]/data/table[@schema=\"metal-gpu-intervals\"]"], out _);
                if (Soft(() => Quiet(() => Trace(_out)).Status, 1) != 0)
                    Output.Warn("the trace's summary failed (the trace itself is fine).");
                Output.Info($"Trace: {trace} (open it in Instruments; the parts are under Points of Interest), summary in trace.md");
            }
            if (_capture)
                foreach (var c in Directory.GetFileSystemEntries(_out, "*.gputrace").Order(StringComparer.Ordinal))
                    Output.Info($"Frame capture: {c} (open it in Xcode)");
            if (Directory.Exists(Path.Combine(_out, "caught")))
                Output.Info($"Caught: {_out}/caught (what the triggers kept: passes and passes.txt, images, frame captures; watch.txt: when the image looked broken)");
            // What the triggers did, but keep a moment (the run's store has those)
            foreach (var e in EventLog.Read(_out).Where(e => e.Type == "trigger" &&
                         (e.Text ?? "").Split(": ").ElementAtOrDefault(1)?.Split(", ").All(a => a == "moment" || a.StartsWith("max ", StringComparison.Ordinal)) != true).Take(10))
                Output.Info($"Trigger: {(e.Scenario != null ? $"{e.Scenario} {e.Part}: " : "")}{e.Text}");
        }
    }

    // Where the run is: a line that updates itself on a terminal; elsewhere (a log), a line for each section
    private sealed class ProgressLine(List<(string Section, double Seconds)> lengths)
    {
        private readonly bool _terminal = !Console.IsOutputRedirected;
        private string _lastShown = "";

        public void Show(RunEvent? beat, int elapsed)
        {
            // Before the first scenario the heartbeat has no scenario
            string section = beat?.Scenario ?? "";
            double atSeconds = beat?.Value ?? 0;
            if (!_terminal)
            {
                if (section.Length > 0 && section != _lastShown)
                {
                    Output.Info(section);
                    _lastShown = section;
                }
                return;
            }
            if (section.Length == 0)
            {
                Console.Write($"\r\u001b[K    Loading the game  {elapsed / 60}:{elapsed % 60:00}");
                return;
            }
            double total = lengths.Sum(l => l.Seconds), done = 0;
            foreach (var (name, seconds) in lengths)
            {
                if (name == section)
                {
                    done += atSeconds;
                    break;
                }
                done += seconds;
            }
            int percent = (int)Math.Min(100, total > 0 ? 100 * done / total : 0);
            var bar = new string('█', percent / 4) + new string('░', 25 - percent / 4);
            Console.Write($"\r\u001b[K    {bar} {percent,3} %  {section}  {elapsed / 60}:{elapsed % 60:00}");
        }

        public void End()
        {
            if (_terminal)
                Console.Write("\r\u001b[K");
        }
    }
}
