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

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    private sealed partial class BenchmarkRun
    {
        // The game's environment for the run, on top of this program's
        private ProcessStartInfo Game(Dictionary<string, string> env)
        {
            // Started as bash starts a job with &: SIGINT and SIGQUIT ignored, so that Ctrl-C at the terminal, which goes to
            // the whole process group, stops the run here and reaches the game only as the kill below. Its output goes
            // nowhere and it reads nothing, as a job's
            var start = new ProcessStartInfo("/bin/sh");
            foreach (var a in new[] { "-c", "trap '' INT QUIT; exec \"$0\" \"$@\" < /dev/null > /dev/null 2>&1", Path.Combine(_app, "Contents", "MacOS", "KSA") })
                start.ArgumentList.Add(a);
            start.Environment["STARMAP_INSTANCE_PATH"] = Instance;
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
            new RunOptions { Explore = true }.Write(output);
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

        private string Starmap() =>
            !File.Exists(Path.Combine(_res, "StarMap", "StarMap.dll")) ? "none"
            : File.Exists(Path.Combine(_res, "StarMap", "version.txt")) ? File.ReadAllText(Path.Combine(_res, "StarMap", "version.txt")).Trim()
            : "in the app";

        // system.txt, with the window from the run's settings.toml
        private int WriteSystemFacts()
        {
            var home = PortFolder.Home;
            var switches = string.Join(' ', Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("KSA_MTL_", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}".Replace(home, "~")));
            var tools = _tools.Count > 0 ? string.Join(", ", _tools) : "none";
            var changed = string.Join(' ', _sets) + (_ports.Count > 0 ? (_sets.Count > 0 ? " " : "") + "port-settings: " + string.Join(' ', _ports) : "");
            if (_settingsFile.Length > 0)
                changed = Path.GetFileName(_settingsFile) + (changed.Length > 0 ? " " : "") + changed;
            var preset = _defaults ? $"{(_preset.Length > 0 ? _preset : "none")}, a fresh install's settings"
                : _preset.Length > 0 ? _preset : "none, own settings";
            return Quiet(() => SystemFacts(_out, _app, [$"StarMap={Starmap()}", $"System={SystemId}", $"Test={(_test.Length > 0 ? _test : "places")}",
                $"Sections={Sections}", $"Mode={Mode}{(_noShots ? ", no screenshots" : "")}", $"Preset={preset}",
                $"Settings changed={(changed.Length > 0 ? changed : "none")}", "Start=a new game, from benchmark.sh",
                $"KSA Metal switches={(switches.Length > 0 ? switches : "none")}", $"Tools={tools}"])).Status;
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

        // The sections played, each with its length on the script's clock, in the script's order
        private List<(string Section, double Seconds)> Lengths()
        {
            var play = " " + string.Join(' ', _only.Select(p => p.Split('_')[0])) + " ";
            var lengths = new List<(string, double)>();
            string name = "";
            double max = 0;
            bool on = false;
            foreach (var f in File.ReadLines(_script).Select(Fields))
            {
                if (f.Length == 0)
                    continue;
                if (f[0] == "section")
                {
                    if (on)
                        lengths.Add((name, max));
                    name = f.Length > 1 ? f[1] : "";
                    max = 0;
                    on = play.Contains($" {name} ");
                }
                else if (on && Regex.IsMatch(f[0], "^[0-9.]+$") && double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > max)
                {
                    max = t;
                }
            }
            if (on)
                lengths.Add((name, max));
            return lengths;
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

            _out = Path.Combine(Benchmarks, DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture));
            if (_hold.Length > 0)
                _out += "_hold";
            if (Directory.Exists(_out) || File.Exists(_out))
                _out += "_" + DateTime.Now.ToString("ss", CultureInfo.InvariantCulture);
            Directory.CreateDirectory(_out);
            File.Copy(_settings, Path.Combine(_out, "settings.toml"));
            try
            {
                if (WriteSystemFacts() != 0)
                    Die("the report program did not run.");
            }
            catch (Exception e) when (e is not PortException)
            {
                Die("the report program did not run.");
            }
            // Keeps the display awake while the run lasts (the game needs it), and wakes it now: a display that went to
            // sleep makes the game fail at start
            Process.Start("caffeinate", ["-d", "-i", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
            Tool.Exec("caffeinate", ["-u", "-t", "1"], out _);

            // What the tools add to the game's environment, and to the run's options
            var env = new Dictionary<string, string>();
            var options = new RunOptions();
            if (_profile)
            {
                // Nothing profiled until the director opens a part
                env["KSA_MTL_GPU_PROFILE"] = Path.Combine(_out, "gpuprof.csv");
                env["KSA_MTL_GPU_PROFILE_FRAMES"] = "0,0";
                options.Profile = 60;
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
                options.Wait = Path.Combine(_out, ".recording");
                env["KSA_MTL_LABELS"] = "1";
            }
            if (_capture)
            {
                env["MTL_CAPTURE_ENABLED"] = env["KSA_MTL_LABELS"] = "1";
                options.Capture = true;
            }
            if (_catch.Length > 0)
            {
                options.Catch = _catch;
                env["KSA_MTL_WATCH"] = Path.Combine(_out, "caught");
                if (_catchCapture)
                {
                    env["MTL_CAPTURE_ENABLED"] = env["KSA_MTL_LABELS"] = "1";
                    options.CatchCapture = true;
                }
            }
            if (_passes)
                options.Passes = true;
            options.Film = _film;
            options.Trace = _cameraLog;

            // Loading takes up to a few minutes on a first start (shader compilation), then the script's own length, plus
            // the time slower Macs spend catching up at checkpoints. Once playing, the director touches its heartbeat file
            // every second: a game that stops touching it has hung.
            var lengths = Lengths();
            int length = (int)lengths.Sum(l => l.Seconds);
            int timeout = length * 2 + 300;
            // KSA_BENCH_HANG_LIMIT: other seconds (not a number: no hang check)
            var limit = Environment.GetEnvironmentVariable("KSA_BENCH_HANG_LIMIT");
            int HangLimit(int otherwise) => limit is not { Length: > 0 } ? otherwise : int.TryParse(limit, out var s) ? s : int.MaxValue;
            int hang = HangLimit(60);
            if (_capture || _catch.Length > 0 || _passes)
                hang = 180;
            // Time warp can stall a frame for most of a minute before it goes on: past that, the sections played are kept
            foreach (var p in _only)
                if (File.ReadLines(_script).Any(l => l.StartsWith($"section {p.Split('_')[0]} live")))
                    hang = HangLimit(180);
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
                var what = $"about {(length + 60) / 60} minutes";
                if (_fast)
                    what = _test switch
                    {
                        "mixed" => "about a minute",
                        "tour" => "about 2 minutes",
                        "warp" => "about 3 minutes",
                        "full" => "about 4 minutes",
                        _ => "fast",
                    };
                if (_test.Length > 0)
                    what = $"{_test}, {what}";
                if (_repeat != 1)
                    what = $"{_repeat} times, {what} each";
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
            File.WriteAllText(Path.Combine(_out, "run.txt"), "");
            for (int n = 1; n <= _repeat; n++)
            {
                PlayOnce(n, env, options, lengths, timeout, hang, ref pressure);
                if (_stopped)
                    break;
            }
            Console.CancelKeyPress -= stop;
            term.Dispose();
            var runTxt = new StringBuilder();
            if (busy.Length > 0)
                runTxt.Append($"GPU before the run: {busy} %\n");
            // --defaults: the settings as the game saved them, with its defaults filled in
            if (_defaults)
            {
                File.Copy(_settings, Path.Combine(_out, "settings.toml"), true);
                Soft(() => WriteSystemFacts());
            }
            var swapAfter = Swap();
            runTxt.Append($"Memory pressure: {pressure switch { 1 => "normal", 2 => "warning", _ => "critical" }}\n");
            runTxt.Append($"Swap used: {(swapBefore.Length > 0 ? swapBefore : "?")} -> {(swapAfter.Length > 0 ? swapAfter : "?")} MB\n");
            File.AppendAllText(Path.Combine(_out, "run.txt"), runTxt.ToString());

            ToolsOutput();
            if (_hold.Length > 0)
            {
                Output.Step($"Done: {_out}");
                return 0;
            }

            // The report, then the zip to share (finish): the report, every frame's numbers, the logs and crash reports of
            // the run, the settings, and the screenshots as JPEG; your home folder's name replaced by ~
            int failed = 0;
            var (finished, said) = Soft(() => Quiet(() => Finish(_out)), (1, ""));
            var zip = said.TrimEnd('\n').Split('\n')[^1];
            if (finished != 0)
                failed = 1;
            if (_profile)
                Soft(() => Quiet(() => Profile(_out)));
            if (!File.Exists(Path.Combine(_out, "report.md")))
                Die($"the report program did not write the report. What the run left is in {_out}");
            Console.WriteLine();
            Show(_out);
            Console.WriteLine();
            if (_reference.Length > 0)
            {
                if (Soft(() => Compare(_reference, _out), 1) != 0)
                    failed = 1;
                Console.WriteLine();
            }

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

        // One game session; its ending goes to run.txt
        private void PlayOnce(int n, Dictionary<string, string> env, RunOptions options, List<(string Section, double Seconds)> lengths, int timeout, int hang,
            ref int pressure)
        {
            string suffix = n > 1 ? $"-{n}" : "", label = n > 1 ? $"Game {n}" : "Game";
            if (n > 1)
                Output.Info($"Run {n} of {_repeat}");
            var heartbeat = Path.Combine(_out, "heartbeat");
            var recording = Path.Combine(_out, ".recording");
            File.Delete(heartbeat);
            File.Delete(recording);
            // This game's options, in the run's folder (a repeat's after the first's)
            options.Only = string.Join(' ', _only);
            options.Fast = _fast;
            options.Hold = _hold;
            options.NoShots = _noShots || n > 1;
            options.Suffix = suffix;
            options.Script = _script;
            options.Write(_out);
            using var game = Process.Start(Game(new Dictionary<string, string>(env) { ["KSA_BENCH_OUT"] = _out }))!;
            var started = DateTime.UtcNow;
            string ending = "";
            bool alongside = false;
            Process? tracer = null;
            var progress = new ProgressLine(lengths);
            while (!game.HasExited)
            {
                int elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
                progress.Show(heartbeat, elapsed);
                // --trace: once the game is up (its first heartbeat), Instruments attaches, and the run starts when it records
                if (_trace && tracer == null && File.Exists(heartbeat))
                    tracer = StartTrace(game.Id, recording);
                // Another game at the same time shares the Mac's GPU and memory (and may be the end of this one)
                if (!alongside && GamesRunning().Any(pid => pid != game.Id))
                {
                    alongside = true;
                    Output.Warn("another game is running at the same time: this run's numbers won't count.");
                }
                // macOS's memory pressure: 1 normal, 2 warning, 4 critical
                if (int.TryParse(Ask("sysctl", "-n", "kern.memorystatus_vm_pressure_level"), out var level) && level > pressure)
                    pressure = level;
                if (_stopped)
                    ending = "was stopped with Ctrl-C";
                else if (File.Exists(heartbeat) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat)).TotalSeconds > hang)
                    ending = $"hung{Where(heartbeat, false)} (no frame for {hang} s): stopped";
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
                var where = File.Exists(heartbeat) ? Where(heartbeat, true) : " before its first frame";
                int status = game.ExitCode;
                ending = status == 0 ? "quit normally"
                    : status is >= 130 and <= 199 ? $"crashed (signal {status - 128}){where}"
                    : $"quit with status {status}{where}";
            }
            var run = new StringBuilder($"{label}: {ending}\n");
            if (alongside)
                run.Append($"Alongside{suffix}: another game\n");
            File.AppendAllText(Path.Combine(_out, "run.txt"), run.ToString());
            // Instruments stops recording when the game quits; it then writes the trace
            if (tracer != null)
            {
                Output.Info("Instruments is writing the trace");
                Tool.Exec("kill", ["-INT", tracer.Id.ToString(CultureInfo.InvariantCulture)], out _);
                tracer.WaitForExit();
            }
            File.Delete(heartbeat);
            File.Delete(recording);
            // The game's log, without your home folder's name
            var log = Path.Combine(Instance, "logs", "console.log");
            if (File.Exists(log))
                File.WriteAllText(Path.Combine(_out, $"game{suffix}.log"), File.ReadAllText(log).Replace(PortFolder.Home, "~"));
        }

        // Where the heartbeat says the game is: " in <section> at <seconds> s", or nothing before the first section (or,
        // after the run, while the director waited)
        private static string Where(string heartbeat, bool afterTheRun)
        {
            string[] f;
            try
            {
                f = Fields(File.ReadAllText(heartbeat).Split('\n')[0]);
            }
            catch (IOException)
            {
                return "";
            }
            return f.Length == 2 && !(afterTheRun && f[0] == "waiting") ? $" in {f[0]} at {f[1]} s" : "";
        }

        private Process? StartTrace(int pid, string recording)
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
            File.WriteAllText(recording, "");
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
            if ((_catch.Length > 0 || _passes) && Directory.Exists(Path.Combine(_out, "caught")))
                Output.Info($"Caught: {_out}/caught (each place's passes and passes.txt; watch.txt: when the image looked broken)");
        }
    }

    // Where the run is: a line that updates itself on a terminal; elsewhere (a log), a line for each section
    private sealed class ProgressLine(List<(string Section, double Seconds)> lengths)
    {
        private readonly bool _terminal = !Console.IsOutputRedirected;
        private string _lastShown = "";

        public void Show(string heartbeat, int elapsed)
        {
            string section = "", at = "";
            try
            {
                var f = File.Exists(heartbeat) ? File.ReadAllText(heartbeat).Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
                (section, at) = (f.ElementAtOrDefault(0) ?? "", f.ElementAtOrDefault(1) ?? "");
            }
            catch (IOException)
            {
            }
            // Before the first section the heartbeat has the time alone
            if (at.Length == 0 || section == "waiting")
                section = "";
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
            double.TryParse(at, NumberStyles.Float, CultureInfo.InvariantCulture, out var atSeconds);
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
