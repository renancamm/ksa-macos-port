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
    // One benchmark run (benchmark.sh with a test, sections or places): its options and checks here, the run's documents
    // folder and settings (RunSettings.cs), the game session or sessions and the report (RunGame.cs)
    private sealed partial class BenchmarkRun
    {
        private readonly List<string> _places = [], _sets = [], _ports = [], _tools = [];
        private string _preset = "", _system = "", _hold = "", _settingsFile = "", _reference = "", _catch = "";
        private string _template = "Metal System Trace", _test = "";
        private bool _defaults, _explore, _fast, _noShots, _profile, _hud, _trace, _capture, _catchCapture, _passes, _cameraLog, _holding;
        private string _repeatText = "", _sessionsText = "1", _ab = "", _filmText = "0";
        private double _film;
        // measure: the measured passes in each game session, the sessions (taken in turn for A and B with --ab)
        private int _repeat = 1, _sessions = 1;
        private string _app = PortFolder.DefaultApp, _res = "", _script = "", _installed = "";
        private List<string> _only = [];
        private string _setKeys = " ";
        private string _settings = "", _out = "";
        // Ctrl-C (or TERM): the game is stopped, and what it measured is reported
        private volatile bool _stopped;

        // How it plays (RunOptions.Mode): the kinds of its scenarios, or "jump"
        private string Mode = "";
        private Lineup _lineup = null!;

        // What the run is for: the verb that started it
        private Purpose _purpose = Purpose.Run;

        // The run's folder once it played, and whether it was stopped (calibrate reads them)
        public string Folder => _out;
        public bool Stopped => _stopped;

        public int Run(string[] args, Purpose purpose)
        {
            _purpose = purpose;
            if (!Parse(args, out int status))
                return status;
            Check();
            Prepare();
            if (_explore)
                return Explore();
            return Play();
        }

        // Each verb's options: the player's settings for every one, the rest as the verb has them
        private bool Parse(string[] args, out int status)
        {
            status = 0;
            _explore = _purpose == Purpose.Explore;
            int i = 0;
            if (_purpose == Purpose.Investigate)
            {
                if (args.Length == 0)
                    Die("investigate <tool> <places...>: profile, trace, capture, hud, catch <clean run>, passes, film <seconds>, camera-log or hold.");
                switch (args[i++])
                {
                    case "profile": _profile = true; break;
                    case "trace": _trace = true; break;
                    case "capture": _capture = true; break;
                    case "hud": _hud = true; break;
                    case "passes": _passes = true; break;
                    case "camera-log": _cameraLog = true; break;
                    case "hold": _holding = true; break;
                    case "catch" when i < args.Length: _catch = args[i++]; break;
                    case "film" when i < args.Length: _filmText = args[i++]; break;
                    case "catch" or "film":
                        Die($"investigate {args[0]} needs {(args[0] == "catch" ? "a clean run" : "a number of seconds")} after it.");
                        break;
                    case "-h" or "--help":
                        Console.WriteLine(CommandUsage);
                        status = 0;
                        return false;
                    case var tool:
                        Die($"no tool '{tool}' (profile, trace, capture, hud, catch <clean run>, passes, film <seconds>, camera-log or hold).");
                        break;
                }
            }
            var withValue = new[] { "--set", "--port", "--settings", "--compare", "--app", "--preset", "--system", "--repeat", "--sessions", "--ab",
                "--trace-template" };
            for (; i < args.Length; i++)
            {
                var a = args[i];
                if (withValue.Contains(a) && i + 1 >= args.Length)
                    Die($"{a} needs a value. ./benchmark.sh --help lists the options.");
                switch (a)
                {
                    // The player's settings
                    case "--set": _sets.Add(args[++i]); break;
                    case "--port": _ports.Add(args[++i]); break;
                    case "--settings": _settingsFile = args[++i]; break;
                    case "--app": _app = args[++i].TrimEnd('/'); break;
                    case "--preset": _preset = args[++i]; break;
                    case "--defaults": _defaults = true; break;
                    case "--system": _system = args[++i]; break;
                    // What each verb adds
                    case "--compare" when _purpose is Purpose.Run or Purpose.Check or Purpose.Measure: _reference = args[++i]; break;
                    case "--no-shots" when _purpose is Purpose.Run: _noShots = true; break;
                    case "--fast" when _purpose is Purpose.Run: _fast = true; break;
                    case "--repeat" when _purpose is Purpose.Measure: _repeatText = args[++i]; break;
                    case "--sessions" when _purpose is Purpose.Measure: _sessionsText = args[++i]; break;
                    case "--ab" when _purpose is Purpose.Measure: _ab = args[++i]; break;
                    case "--trace-template" when _trace: _template = args[++i]; break;
                    case "--catch-capture" when _catch.Length > 0: _catchCapture = true; break;
                    case "-h" or "--help":
                        Console.WriteLine(CommandUsage);
                        return false;
                    // A developer's option of before the verbs: said where it went
                    case "--profile" or "--trace" or "--capture" or "--hud" or "--passes" or "--camera-log" or "--catch" or "--film" or "--hold":
                        Die($"{a} is a tool now: ./benchmark.sh investigate {a[2..]}{a switch { "--catch" => " <clean run>", "--film" => " <seconds>", _ => "" }} " +
                            $"{(a == "--hold" ? "<place>" : "<places...>")}.");
                        break;
                    case "--repeat":
                        Die("--repeat is for numbers: ./benchmark.sh measure [test] --repeat <n>.");
                        break;
                    case "--explore":
                        Die("explore is a command now: ./benchmark.sh explore.");
                        break;
                    case var o when o.StartsWith('-'):
                        Die($"no option {o} for {_purpose.ToString().ToLowerInvariant()}. ./benchmark.sh --help lists the options.");
                        break;
                    default: _places.Add(a); break;
                }
            }
            if (_purpose == Purpose.Explore && _places.Count > 0)
                Die("explore takes no test or places.");
            if (_holding)
            {
                if (_places.Count != 1)
                    Die("investigate hold <place>: one place, where it stays.");
                _hold = _places[0];
                _places.Clear();
            }
            // The standard test: a fresh install's settings, compared with the baseline main
            if (_purpose == Purpose.Check)
            {
                _defaults = true;
                if (_places.Count > 1 || _places.Count == 1 && !LineupHasTest(_places[0]))
                    Die("check takes one test (mixed, tour, warp or full; default mixed).");
            }
            // A tool looks at places: jumped to in a flight, the game paused on the way
            if (_purpose == Purpose.Investigate && _places.Count > 0 && !(_places.Count == 1 && LineupHasTest(_places[0])))
                _fast = true;
            return true;
        }

        // The last run of a test that passed played as check plays it: the same settings, screenshots, no tools and no KSA
        // Metal switches; or null
        private string? LastPassed(string test, string mode) =>
            (Directory.Exists(Benchmarks) ? Directory.GetDirectories(Benchmarks) : []).Where(IsRun).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault(d => RunRecord.Read(d) is { Status: Status.Passed } r && r.Test == test && r.Mode == mode &&
                                     r.Fact("Mode") == mode && r.Fact("Preset") == PresetFact && r.Fact("Settings changed") == ChangedFact &&
                                     r.Fact("Tools") == "none" && r.Fact("KSA Metal switches") == "none" && r.Fact("A/B") == null);

        // The one test what plays belongs to (for a baseline's name): the test, or the test of the places; null for several
        private string? TestPlayed =>
            _test.Length > 0 ? _test
            : _only.Select(p => _lineup.TestOf(p.Split('_')[0])?.Name).Distinct().ToList() is [{ } one] ? one : null;

        // Whether a word is a test of the app's lineup (before Check reads it)
        private bool LineupHasTest(string word) => word is "mixed" or "tour" or "warp" or "full";

        // Everything that can stop the run, checked before the game starts
        private void Check()
        {
            // A relative path would not reach the game, which starts from elsewhere
            if (!_app.StartsWith('/'))
                _app = Path.Combine(Environment.CurrentDirectory, _app);
            if (_preset.Length > 0 && !Presets.ContainsKey(_preset))
                Die($"unknown preset '{_preset}' (low, medium or high).");
            // measure: three passes unless said
            if (_repeatText.Length == 0)
                _repeatText = _purpose == Purpose.Measure ? "3" : "1";
            if (!Regex.IsMatch(_repeatText, "^[1-9][0-9]*$"))
                Die($"--repeat needs a number of passes, not '{_repeatText}'.");
            _repeat = int.Parse(_repeatText);
            if (!Regex.IsMatch(_sessionsText, "^[1-9][0-9]*$"))
                Die($"--sessions needs a number of game sessions, not '{_sessionsText}'.");
            _sessions = int.Parse(_sessionsText);
            if (_ab.Length > 0 && !_ab.Contains('='))
                Die($"--ab needs a port setting as key=value, not '{_ab}'.");
            if (!double.TryParse(_filmText, NumberStyles.Float, CultureInfo.InvariantCulture, out _film) || _film < 0)
                Die($"film needs a number of seconds, not '{_filmText}'.");
            if (_settingsFile.Length > 0 && !File.Exists(_settingsFile))
                Die($"no settings file {_settingsFile}.");
            _installed = PortFolder.AppVersion(_app);
            if (_installed.Length == 0)
                Die($"no app made by this port at {_app}. Use ./port.sh to make one (or pass --app).");
            _res = Path.Combine(_app, "Contents", "Resources");
            _script = Path.Combine(_res, "Benchmark", "benchmark.txt");
            if (!File.Exists(_script) || !File.Exists(Path.Combine(_res, "Hooks", "KsaMetal.Hooks.dll")))
                Die($"the app at {_app} has no benchmark: update it with ./port.sh.");

            try
            {
                _lineup = Lineup.Read(_script);
            }
            catch (LineupException e)
            {
                Die($"the app's benchmark can't be read ({e.Message}): update the app with ./port.sh.");
            }
            // A test by its name alone, in place of scenarios or places
            if (_places.Count == 1 && _hold.Length == 0 && (_places[0] == "full" || _lineup.Test(_places[0]) != null))
            {
                _test = _places[0];
                _places.Clear();
            }
            else if (_places.Count == 0 && _hold.Length == 0)
            {
                _test = "mixed";
            }
            if (_fast && _places.Count == 0 && _hold.Length == 0)
                Die("--fast jumps to the places named (e.g. --fast earth4 jupiter2): a test plays its one form.");

            _only = _places.Select(Place).ToList();
            // --hold <place>: its scenario up to there, jumped to
            if (_hold.Length > 0)
            {
                _hold = Place(_hold);
                if (!Shots().Contains(_hold))
                    Die("investigate hold needs a screenshot's place, not a scenario.");
                _only = [_hold];
                _fast = true;
                _repeat = _sessions = 1;
            }
            // Places named (the tools that take GB at each place need some)
            Named = _only.Count;
            // A test: its scenarios, full's the three tests' in their order
            if (_test.Length > 0)
                _only = (_test == "full" ? Lineup.Full : [_test]).SelectMany(t => _lineup.Test(t)!.Scenarios).ToList();
            Sections = string.Join(' ', _only);
            // In the lineup's order, as the director plays them; a flight's places jumped to are "jump"
            var names = _only.Select(p => p.Split('_')[0]).ToHashSet();
            var played = _lineup.Scenarios.Where(s => names.Contains(s.Name)).ToList();
            Mode = string.Join(' ', played.Select(s => _fast && s.Kind == Kind.Flight ? "jump" : Lineup.Name(s.Kind)).Distinct());
            // Earth's small system when the scenarios need no other celestial than the Moon
            if (_system.Length == 0)
                _system = played.Any(s => s.Needs.Any(n => n != "Luna")) ? "sol" : "earth";
            SystemId = _system switch
            {
                "sol" => "Sol",
                "earth" => "SolLite",
                _ => throw new PortException($"unknown system '{_system}' (sol or earth)."),
            };
            // check: compared with the baseline main of its test, or the last run of the test that passed at the defaults
            if (_purpose == Purpose.Check && _reference.Length == 0)
            {
                if (Baseline("main", _test) is { } main)
                    _reference = main;
                else if (LastPassed(_test, Mode) is { } last)
                {
                    _reference = last;
                    Output.Info($"No baseline main for the {Pages.TestName(_test)}: compared with the last run of it that passed, {Path.GetFileName(last)} " +
                                "(./benchmark.sh baseline set main <run> names one).");
                }
                else
                    Output.Info($"Nothing to compare with yet: this run of the {Pages.TestName(_test)} can be the baseline (./benchmark.sh baseline set main).");
            }
            // --compare: said now, not after the minutes the run takes
            if (_reference.Length > 0)
            {
                _reference = RunFolder(_reference, TestPlayed);
                if (NotComparable(_reference, Mode, _only) is { } why)
                    Die($"--compare: {why}");
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

        // Every place, as the director names it: <scenario>_<shot>
        private List<string> Shots() => _lineup.Scenarios.SelectMany(s => s.Shots.Select(shot => $"{s.Name}_{shot}")).ToList();

        private static string[] Fields(string line) => line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

        // A scenario's name, or a place (a screenshot's name, one across the lineup) as <scenario>_<shot>
        private string Place(string name)
        {
            if (_lineup.Scenario(name) != null)
                return name;
            if (_lineup.ScenarioOf(name) is { } scenario)
                return $"{scenario.Name}_{name}";
            Die($"no test, scenario or screenshot '{name}'. ./benchmark.sh list shows them.");
            return "";
        }

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
            // Each game session of full would write over the one before
            if (_test == "full" && (_profile || _hud || _trace || _capture || _catch.Length > 0 || _passes || _film > 0 || _cameraLog))
                Die("the tools look at one test: choose mixed, tour or warp (or places).");
            if (_profile || _hud || _trace || _capture || _catch.Length > 0 || _passes)
            {
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
                    Die("a trace needs Xcode (from the App Store), not only the Command Line Tools. With Xcode installed: sudo xcode-select -s /Applications/Xcode.app");
                if (!Ask("xcrun", "xctrace", "list", "templates").Split('\n').Any(l => l == _template))
                    Die($"Instruments has no template '{_template}' (xcrun xctrace list templates).");
                if (Named == 0)
                    Die("a trace of a whole test takes tens of GB: choose places (./benchmark.sh investigate trace jupiter2).");
                if (FreeGb() < 10)
                    Die($"a trace needs 10 GB free on this disk ({FreeGb()} GB free).");
                _tools.Add($"Instruments ({_template})");
            }
            if (_catch.Length > 0)
            {
                if (_capture)
                    Die("--catch plays the game with no capture mode, --capture needs it: choose one.");
                if (!DriverHas("ksaMtlPassDump"))
                    Die("this app's KSA Metal can't dump passes: update it with ./port.sh.");
                _catch = RunFolder(_catch, TestPlayed);
                if (_noShots)
                    Die("--catch compares screenshots: it can't go with --no-shots.");
                if (!Directory.Exists(_catch))
                    Die("catch needs a clean run's folder, with its screenshots, not a zip.");
                if (!Directory.GetFiles(_catch, "*_*.png").Any())
                    Die($"--catch: {_catch} has no screenshots to compare with.");
                _catch = Path.GetFullPath(_catch);  // absolute: the game starts from elsewhere
                if (NotComparable(_catch, Mode, _only) is { } why)
                    Die($"--catch: {why}");
                if (FreeGb() < 1)
                    Die($"catch needs 1 GB free on this disk ({FreeGb()} GB free).");
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
                if (Named == 0 && !_fast && _test != "mixed")
                    Die("passes take minutes at every screenshot: choose places (./benchmark.sh investigate passes earth4 jupiter2), or the Mixed Test.");
                _tools.Add("pass dumps at every place");
            }
            if (_capture)
            {
                if (!DriverHas("ksaMtlCapture"))
                    Die("this app's KSA Metal can't capture frames: update it with ./port.sh.");
                if (Named == 0)
                    Die("a capture takes several GB at every screenshot: choose places (./benchmark.sh investigate capture jupiter2).");
                if (FreeGb() < 20)
                    Die($"--capture needs 20 GB free on this disk ({FreeGb()} GB free).");
                if (!Directory.Exists("/Applications/Xcode.app"))
                    Output.Warn("the captures open in Xcode, which isn't in /Applications.");
                _tools.Add("GPU frame capture");
            }
        }
    }
}
