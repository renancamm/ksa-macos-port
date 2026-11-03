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
using KsaPorter;

namespace KsaMetalBenchmarkReport;

public static partial class Program
{
    // One benchmark run (benchmark.sh with a test, sections or places): its options and checks, the run's documents
    // folder and settings, the game session or sessions, and the report
    private sealed class BenchmarkRun
    {
        private readonly List<string> _places = [], _sets = [], _ports = [], _tools = [];
        private string _preset = "", _system = "", _hold = "", _settingsFile = "", _reference = "", _catch = "";
        private string _template = "Metal System Trace", _test = "";
        private bool _defaults, _explore, _fast, _noShots, _profile, _hud, _trace, _capture, _catchCapture, _passes;
        private string _repeatText = "1";
        private int _repeat = 1;
        private string _app = PortFolder.DefaultApp, _res = "", _script = "", _installed = "";
        private List<string> _only = [];
        private string _setKeys = " ";
        private string _settings = "", _out = "";
        // Ctrl-C (or TERM): the game is stopped, and what it measured is reported
        private volatile bool _stopped;

        private string Mode => _fast ? "fast" : "cinematic";

        public int Run(string[] args)
        {
            if (!Parse(args, out int status))
                return status;
            Check();
            Prepare();
            if (_explore)
                return Explore();
            return Play();
        }

        private bool Parse(string[] args, out int status)
        {
            status = 0;
            var withValue = new[] { "--hold", "--set", "--port", "--settings", "--compare", "--catch", "--app", "--preset", "--system",
                "--repeat", "--trace-template" };
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (withValue.Contains(a) && i + 1 >= args.Length)
                    Die($"{a} needs a value. ./benchmark.sh --help lists the options.");
                switch (a)
                {
                    case "--fast": _fast = true; break;
                    case "--no-shots": _noShots = true; break;
                    case "--hold": _hold = args[++i]; break;
                    case "--set": _sets.Add(args[++i]); break;
                    case "--port": _ports.Add(args[++i]); break;
                    case "--settings": _settingsFile = args[++i]; break;
                    case "--compare": _reference = args[++i]; break;
                    case "--app": _app = args[++i].TrimEnd('/'); break;
                    case "--preset": _preset = args[++i]; break;
                    case "--defaults": _defaults = true; break;
                    case "--system": _system = args[++i]; break;
                    case "--repeat": _repeatText = args[++i]; break;
                    case "--profile": _profile = true; break;
                    case "--hud": _hud = true; break;
                    case "--trace": _trace = true; break;
                    case "--trace-template": _trace = true; _template = args[++i]; break;
                    case "--capture": _capture = true; break;
                    case "--catch": _catch = args[++i]; break;
                    case "--catch-capture": _catchCapture = true; break;
                    case "--passes": _passes = true; break;
                    case "--explore": _explore = true; break;
                    case "-h" or "--help":
                        Console.WriteLine(CommandUsage);
                        return false;
                    case var o when o.StartsWith('-'):
                        Die($"no option {o}. ./benchmark.sh --help lists the options.");
                        break;
                    default: _places.Add(a); break;
                }
            }
            return true;
        }

        // Everything that can stop the run, checked before the game starts
        private void Check()
        {
            // A relative path would not reach the game, which starts from elsewhere
            if (!_app.StartsWith('/'))
                _app = Path.Combine(Environment.CurrentDirectory, _app);
            if (_preset.Length > 0 && !Presets.ContainsKey(_preset))
                Die($"unknown preset '{_preset}' (low, medium or high).");
            if (!Regex.IsMatch(_repeatText, "^[1-9][0-9]*$"))
                Die($"--repeat needs a number of runs, not '{_repeatText}'.");
            _repeat = int.Parse(_repeatText);
            if (_settingsFile.Length > 0 && !File.Exists(_settingsFile))
                Die($"no settings file {_settingsFile}.");
            _installed = PortFolder.AppVersion(_app);
            if (_installed.Length == 0)
                Die($"no app made by this port at {_app}. Use ./port.sh to make one (or pass --app).");
            _res = Path.Combine(_app, "Contents", "Resources");
            _script = Path.Combine(_res, "Benchmark", "benchmark.txt");
            if (!File.Exists(_script) || !File.Exists(Path.Combine(_res, "Hooks", "KsaMetalHooks.dll")))
                Die($"the app at {_app} has no benchmark: update it with ./port.sh.");

            // A test by its name alone, in place of sections or places
            if (_places.Count == 1 && _hold.Length == 0 && (_places[0] == "full" || TestSections(_script, _places[0], "fast").Length > 0))
            {
                _test = _places[0];
                _places.Clear();
            }
            else if (_places.Count == 0 && _hold.Length == 0)
            {
                _test = "mixed";
            }
            // The Warp Test has one form, the short one's system
            if (_test == "warp")
                _fast = true;

            _only = _places.Select(Place).ToList();
            // --hold <place>: its section up to there, fast
            if (_hold.Length > 0)
            {
                _hold = Place(_hold);
                if (!Shots().Contains(_hold))
                    Die("--hold needs a screenshot's place, not a section.");
                _only = [_hold];
                _fast = true;
                _repeat = 1;
            }
            // Places named (the tools that take GB at each place need some)
            Named = _only.Count;
            Sections = string.Join(' ', _only);
            // A test: its sections, full mode's the three tests' in the tour's order
            if (_test.Length > 0)
            {
                var tests = _test == "full" ? new[] { "tour", "mixed", "warp" } : [_test];
                _only = string.Join(' ', tests.Select(t => TestSections(_script, t, Mode))).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                Sections = string.Join(' ', _only);
            }
            // Fast mode plays in Earth's small system when its sections need no other celestial than the Moon
            if (_system.Length == 0 && _fast)
            {
                _system = "earth";
                foreach (var p in _only)
                    if (Needs(p.Split('_')[0]).Any(n => n != "Luna"))
                        _system = "sol";
            }
            SystemId = (_system.Length > 0 ? _system : "sol") switch
            {
                "sol" => "Sol",
                "earth" => "SolLite",
                _ => throw new PortException($"unknown system '{_system}' (sol or earth)."),
            };
            // --compare: said now, not after the minutes the run takes
            if (_reference.Length > 0)
            {
                _reference = RunFolder(_reference);
                var (ok, why) = Quiet(() => Comparable(_reference, Mode, string.Join(' ', _only)));
                if (ok != 0)
                    Die($"--compare: {why.Trim()}");
            }

            if (!File.Exists(Path.Combine(Docs, "settings.toml")))
                Die("start the game once before benchmarking it (it has no settings yet).");
            if (GamesRunning().Count > 0)
                Die("the game is running. Quit it first.");
            CheckTools();
            PortFolder.CheckKsaMatch(_installed);
        }

        private int Named;
        private string Sections = "", SystemId = "";

        // Places: a section, <section>_<shot>, or a screenshot's name alone where only one section has it
        private List<string> Shots()
        {
            var shots = new List<string>();
            string section = "";
            foreach (var line in File.ReadLines(_script))
            {
                var f = Fields(line);
                if (f.Length >= 2 && f[0] == "section")
                    section = f[1];
                if (f.Length >= 3 && f[1] == "@shot")
                    shots.Add($"{section}_{f[2]}");
            }
            return shots;
        }

        private static string[] Fields(string line) => line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        private string Place(string name)
        {
            var shots = Shots();
            if (File.ReadLines(_script).Any(l => l == $"section {name}" || l.StartsWith($"section {name} ")) || shots.Contains(name))
                return name;
            var matches = shots.Where(s => s.EndsWith("_" + name)).ToList();
            if (matches.Count == 0)
                Die($"no test, section or screenshot '{name}'. ./benchmark.sh list shows them.");
            if (matches.Count > 1)
                Die($"more than one screenshot '{name}': {string.Join(' ', matches)}.");
            return matches[0];
        }

        // The celestials a section needs besides Earth ("section <name> needs <celestial>...")
        private IEnumerable<string> Needs(string section) =>
            File.ReadLines(_script).Select(Fields).Where(f => f.Length >= 3 && f[0] == "section" && f[1] == section && f[2] == "needs")
                .SelectMany(f => f[3..]);

        // The games running on this Mac, by process id
        private static List<int> GamesRunning() =>
            Ask("pgrep", "-f", "dotnet (KSA|StarMap).dll").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

        // Whether the app's KSA Metal has that benchmark function
        private bool DriverHas(string function)
        {
            var lib = Path.Combine(_res, "libvulkan.1.dylib");
            return File.Exists(lib) && Encoding.Latin1.GetString(File.ReadAllBytes(lib)).Contains(function, StringComparison.Ordinal);
        }

        // Free space where the results go, in GB
        private static long FreeGb() => long.TryParse(Ask("df", "-g", Docs).Split('\n').ElementAtOrDefault(1)?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(3), out var gb) ? gb : 0;

        // The tools for work on the port, each checked before the game starts: they may run on a Mac without them
        private void CheckTools()
        {
            if (_profile || _hud || _trace || _capture || _catch.Length > 0 || _passes)
            {
                if (_repeat != 1)
                    Die("--repeat measures without tools: use --profile, --hud, --trace, --capture, --catch or --passes in a run of their own.");
                if (!DriverHas("ksaMtlFrameStats"))
                    Die("the tools need the app's KSA Metal driver, which this app doesn't have.");
            }
            if (_profile)
            {
                if (!DriverHas("ksaMtlProfileFrames"))
                    Die("this app's KSA Metal has no GPU profiler: update it with ./port.sh.");
                if (_hud)
                    Die("--profile and --hud can't run together: both use the GPU's counters.");
                _tools.Add("GPU profiler");
            }
            if (_hud)
                _tools.Add("Metal HUD");
            if (_trace)
            {
                if (Tool.Exec("xcrun", ["-f", "xctrace"], out _) != 0)
                    Die("--trace needs Xcode (from the App Store), not only the Command Line Tools. With Xcode installed: sudo xcode-select -s /Applications/Xcode.app");
                if (!Ask("xcrun", "xctrace", "list", "templates").Split('\n').Any(l => l == _template))
                    Die($"Instruments has no template '{_template}' (xcrun xctrace list templates).");
                if (Named == 0)
                    Die("a trace of a whole test takes tens of GB: choose sections or places (e.g. --fast jupiter2 --trace).");
                if (FreeGb() < 10)
                    Die($"--trace needs 10 GB free on this disk ({FreeGb()} GB free).");
                _tools.Add($"Instruments ({_template})");
            }
            if (_catch.Length > 0)
            {
                if (_capture)
                    Die("--catch plays the game with no capture mode, --capture needs it: choose one.");
                if (!DriverHas("ksaMtlPassDump"))
                    Die("this app's KSA Metal can't dump passes: update it with ./port.sh.");
                _catch = RunFolder(_catch);
                if (_noShots)
                    Die("--catch compares screenshots: it can't go with --no-shots.");
                if (!Directory.Exists(_catch))
                    Die("--catch needs a run's folder, with its screenshots, not a zip.");
                if (!Directory.GetFiles(_catch, "*_*.png").Any())
                    Die($"--catch: {_catch} has no screenshots to compare with.");
                _catch = Path.GetFullPath(_catch);  // absolute: the game starts from elsewhere
                var (ok, why) = Quiet(() => Comparable(_catch, Mode, string.Join(' ', _only)));
                if (ok != 0)
                    Die($"--catch: {why.Trim()}");
                if (FreeGb() < 1)
                    Die($"--catch needs 1 GB free on this disk ({FreeGb()} GB free).");
                _tools.Add("pass dumps of broken pictures");
                if (_catchCapture)
                {
                    if (!DriverHas("ksaMtlCapture"))
                        Die("this app's KSA Metal can't capture frames: update it with ./port.sh.");
                    if (FreeGb() < 40)
                        Die($"--catch-capture needs 40 GB free on this disk ({FreeGb()} GB free).");
                    _tools.Add("frame captures of broken pictures");
                }
            }
            if (_catchCapture && _catch.Length == 0)
                Die("--catch-capture goes with --catch.");
            if (_passes)
            {
                if (_catch.Length > 0 || _capture)
                    Die("--passes is a clean run's dump at every place: it can't go with --catch or --capture.");
                if (!DriverHas("ksaMtlPassDump"))
                    Die("this app's KSA Metal can't dump passes: update it with ./port.sh.");
                if (_noShots)
                    Die("--passes dumps at the screenshots: it can't go with --no-shots.");
                if (Named == 0 && !_fast)
                    Die("--passes takes minutes at every screenshot: choose places, or --fast.");
                _tools.Add("pass dumps at every place");
            }
            if (_capture)
            {
                if (!DriverHas("ksaMtlCapture"))
                    Die("this app's KSA Metal can't capture frames: update it with ./port.sh.");
                if (Named == 0)
                    Die("--capture takes several GB at every screenshot: choose places (e.g. --fast jupiter2 --capture).");
                if (FreeGb() < 20)
                    Die($"--capture needs 20 GB free on this disk ({FreeGb()} GB free).");
                if (!Directory.Exists("/Applications/Xcode.app"))
                    Output.Warn("the captures open in Xcode, which isn't in /Applications.");
                _tools.Add("GPU frame capture");
            }
        }

        // 1. The game's documents folder for the run, made fresh: the game alone (none of your mods, so they neither change
        //    the numbers nor stop the game from starting; the hooks add the benchmark's vehicles from the app)
        private void Prepare()
        {
            if (Directory.Exists(Instance))
                Directory.Delete(Instance, true);
            Directory.CreateDirectory(Instance);
            File.WriteAllText(Path.Combine(Instance, "manifest.toml"), "[[mods]]\nid = \"Core\"\nenabled = true\n");

            // The run's settings: yours, with the preset on top
            _settings = Path.Combine(Instance, "settings.toml");
            File.Copy(Path.Combine(Docs, "settings.toml"), _settings);
            // The game starts straight into a new game of the chosen system, without its system menu, update popup,
            // changelog or name labels, with the camera on the game's test vessel in orbit (a testing start, the one that
            // keeps the system's vehicles)
            var start = new List<string> { "selectSystemOnStart=false", $"lastSystemId=\"{SystemId}\"", "startGameType=\"Testing\"",
                "startVehicle=\"Rocket\"", "startParent=\"Earth\"", "startSituation=\"RocketStartingSituation\"", "checkForUpdates=false",
                "displayChangelogOnChange=false", "celestialNames=false", "locationNames=false" };
            if (_preset.Length > 0)
                start.AddRange(PresetCommon.Concat(Presets[_preset]));
            foreach (var kv in start)
                SetSetting(kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
            // --settings: its "key = value" lines
            if (_settingsFile.Length > 0)
            {
                foreach (var line in File.ReadLines(_settingsFile))
                {
                    if (line.Length == 0 || line[0] is '#' or '[')
                        continue;
                    int eq = line.IndexOf('=');
                    var key = eq < 0 ? "" : Regex.Replace(line[..eq], "[ \t]", "");
                    if (key.Length == 0)
                        Die($"{_settingsFile}: not a 'key = value' line: {line}");
                    SetSetting(key, TomlValue(line[(eq + 1)..].Trim()));
                }
            }
            // --set
            foreach (var kv in _sets)
            {
                if (!kv.Contains('='))
                    Die($"--set needs key=value, not '{kv}'.");
                SetSetting(kv[..kv.IndexOf('=')], TomlValue(kv[(kv.IndexOf('=') + 1)..]));
            }
            // --defaults: only the lines set above, in their sections. The game gives every setting the file leaves out its
            // default, as on a fresh install, and saves the whole file when it starts.
            if (_defaults)
            {
                var kept = new StringBuilder();
                string section = "";
                int n = 0;
                foreach (var line in File.ReadAllLines(_settings))
                {
                    if (line.StartsWith('['))
                    {
                        section = line;
                        continue;
                    }
                    var f = Fields(line);
                    if (f.Length >= 2 && f[1] == "=" && _setKeys.Contains($" {f[0]} "))
                    {
                        if (section.Length > 0)
                        {
                            kept.Append(n++ > 0 ? "\n" : "").Append(section).Append('\n');
                            section = "";
                        }
                        kept.Append(line).Append('\n');
                    }
                }
                File.WriteAllText(_settings, kept.ToString());
            }

            // The run's port settings (the app's port-settings.toml template): yours unless a preset is played or
            // --defaults, --port on top
            var portSettings = Path.Combine(Instance, "port-settings.toml");
            var yours = Path.Combine(Docs, "port-settings.toml");
            var template = Path.Combine(_res, "port-settings.toml");
            if (_preset.Length == 0 && !_defaults && File.Exists(yours))
                File.Copy(yours, portSettings);
            else if (File.Exists(template))
                File.Copy(template, portSettings);
            else
                File.WriteAllText(portSettings, "");
            var table = Path.Combine(_res, "port-settings.txt");
            foreach (var kv in _ports)
            {
                if (!kv.Contains('='))
                    Die($"--port needs key=value, not '{kv}'.");
                string key = kv[..kv.IndexOf('=')], value = kv[(kv.IndexOf('=') + 1)..];
                // A key the game doesn't know would run an unchanged game that looks like an A/B test
                if (File.Exists(table))
                {
                    if (!File.ReadLines(table).Any(l => l.StartsWith(key + " |")))
                        Die($"--port: there is no setting '{key}' in port-settings.toml.");
                    CheckPortValue(table, key, value);
                }
                var lines = File.ReadAllLines(portSettings).ToList();
                var pattern = new Regex($"^{Regex.Escape(key)} *=");
                if (lines.Any(pattern.IsMatch))
                    lines = lines.Select(l => pattern.IsMatch(l) ? $"{key} = {value}" : l).ToList();
                else
                    lines.Add($"{key} = {value}");
                File.WriteAllText(portSettings, string.Join('\n', lines) + "\n");
            }
        }

        // One line of settings.toml (keys are unique across its sections). As <section>.<key>, the key in that section,
        // added when the file lacks it (one a newer KSA added; the section must be there)
        private void SetSetting(string name, string value)
        {
            int dot = name.LastIndexOf('.');
            string key = name[(dot + 1)..], section = dot < 0 ? "" : name[..dot];
            var lines = File.ReadAllLines(_settings).ToList();
            var line = $"{key} = {value}";
            if (section.Length == 0)
            {
                if (!lines.Any(l => l.StartsWith(key + " = ")))
                    Die($"settings.toml has no '{key}' (does this KSA version still match?).");
                lines = lines.Select(l => l.StartsWith(key + " = ") ? line : l).ToList();
            }
            else
            {
                int header = lines.IndexOf($"[{section}]");
                int end = header < 0 ? -1 : lines.FindIndex(header + 1, l => l.StartsWith('['));
                if (end < 0)
                    end = lines.Count;
                var inside = header < 0 ? [] : Enumerable.Range(header + 1, end - header - 1).Where(i => lines[i].StartsWith(key + " = ")).ToList();
                if (inside.Count > 0)
                {
                    foreach (var i in inside)
                        lines[i] = line;
                }
                else
                {
                    if (header < 0)
                        Die($"settings.toml has no [{section}] section.");
                    Output.Info($"settings.toml has no {key} in [{section}]: added");
                    lines.Insert(header + 1, line);
                }
            }
            File.WriteAllText(_settings, string.Join('\n', lines) + "\n");
            _setKeys += key + " ";
        }

        // A bare word is a string, quoted as TOML wants it (Pcf -> "Pcf"); anything else as written
        private static string TomlValue(string value) =>
            Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_-]*$") && value is not ("true" or "false" or "inf" or "nan") ? $"\"{value}\"" : value;

        // Stops at a value the game would refuse: it would run an unchanged game with only a line in its log to say so
        private static void CheckPortValue(string table, string key, string value)
        {
            var row = File.ReadLines(table).FirstOrDefault(l => l.Split('|')[0].Trim() == key);
            var type = row == null ? "" : row.Split('|').ElementAtOrDefault(1)?.Trim() ?? "";
            var parts = type.Split(':');
            string[] values;
            switch (parts[0])
            {
                case "bool":
                    values = ["true", "false"];
                    break;
                case "choice":
                    values = parts[1].Split('/').Select(v => v.Split('=')[0]).ToArray();
                    break;
                case "scale" or "int" or "number":
                    if (!Regex.IsMatch(value, @"^-?[0-9.]+$") || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
                        v < double.Parse(parts[1], CultureInfo.InvariantCulture) || v > double.Parse(parts[2], CultureInfo.InvariantCulture))
                        Die($"--port {key}={value}: it takes a number from {parts[1]} to {parts[2]}.");
                    return;
                default:
                    return;
            }
            if (!values.Contains(value))
                Die($"--port {key}={value}: it takes {string.Join(" or ", values)}.");
        }

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
            using (var game = Process.Start(Game(new() { ["KSA_BENCH_EXPLORE"] = "1", ["KSA_BENCH_OUT"] = output }))!)
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
                .Where(k => Regex.IsMatch(k, "^KSA_(MTL_|HOOKS)")).Order(StringComparer.Ordinal)
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

            // What the tools add to the game's environment
            var env = new Dictionary<string, string>();
            if (_profile)
            {
                // Nothing profiled until the director opens a part
                env["KSA_MTL_GPUPROF"] = Path.Combine(_out, "gpuprof.csv");
                env["KSA_MTL_GPUPROF_FRAMES"] = "0,0";
                env["KSA_BENCH_PROFILE"] = "60";
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
                env["KSA_BENCH_WAIT"] = Path.Combine(_out, ".recording");
                env["KSA_MTL_LABELS"] = "1";
            }
            if (_capture)
            {
                env["MTL_CAPTURE_ENABLED"] = env["KSA_BENCH_CAPTURE"] = env["KSA_MTL_LABELS"] = "1";
            }
            if (_catch.Length > 0)
            {
                env["KSA_BENCH_CATCH"] = _catch;
                env["KSA_MTL_WATCH"] = Path.Combine(_out, "caught");
                if (_catchCapture)
                    env["MTL_CAPTURE_ENABLED"] = env["KSA_BENCH_CATCH_CAPTURE"] = env["KSA_MTL_LABELS"] = "1";
            }
            if (_passes)
                env["KSA_BENCH_PASSES"] = "1";

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
                PlayOnce(n, env, lengths, timeout, hang, ref pressure);
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
        private void PlayOnce(int n, Dictionary<string, string> tools, List<(string Section, double Seconds)> lengths, int timeout, int hang,
            ref int pressure)
        {
            string suffix = n > 1 ? $"-{n}" : "", label = n > 1 ? $"Game {n}" : "Game";
            if (n > 1)
                Output.Info($"Run {n} of {_repeat}");
            var heartbeat = Path.Combine(_out, "heartbeat");
            var recording = Path.Combine(_out, ".recording");
            File.Delete(heartbeat);
            File.Delete(recording);
            var env = new Dictionary<string, string>(tools)
            {
                ["KSA_BENCH_ONLY"] = string.Join(' ', _only),
                ["KSA_BENCH_FAST"] = _fast ? "1" : "",
                ["KSA_BENCH_HOLD"] = _hold,
                ["KSA_BENCH_NO_SHOTS"] = _noShots || n > 1 ? "1" : "",
                ["KSA_BENCH_RUN"] = suffix,
                ["KSA_BENCH_SCRIPT"] = _script,
                ["KSA_BENCH_OUT"] = _out,
            };
            using var game = Process.Start(Game(env))!;
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
