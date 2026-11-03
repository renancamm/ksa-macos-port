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
    // One benchmark run (benchmark.sh with a test, sections or places): its options and checks here, the run's documents
    // folder and settings (RunSettings.cs), the game session or sessions and the report (RunGame.cs)
    private sealed partial class BenchmarkRun
    {
        private readonly List<string> _places = [], _sets = [], _ports = [], _tools = [];
        private string _preset = "", _system = "", _hold = "", _settingsFile = "", _reference = "", _catch = "";
        private string _template = "Metal System Trace", _test = "";
        private bool _defaults, _explore, _fast, _noShots, _profile, _hud, _trace, _capture, _catchCapture, _passes, _cameraLog;
        private string _repeatText = "1", _filmText = "0";
        private double _film;
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
                "--repeat", "--trace-template", "--film" };
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
                    case "--film": _filmText = args[++i]; break;
                    case "--camera-log": _cameraLog = true; break;
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
            if (!double.TryParse(_filmText, NumberStyles.Float, CultureInfo.InvariantCulture, out _film) || _film < 0)
                Die($"--film needs a number of seconds, not '{_filmText}'.");
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
            if (_film > 0)
                _tools.Add($"film every {_film.ToString(CultureInfo.InvariantCulture)} s");
            if (_cameraLog)
                _tools.Add("camera log");
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
    }
}
