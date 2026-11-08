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
        private readonly List<string> _places = [], _sets = [], _ports = [], _tools = [], _env = [];
        // --probe, --knob, --channels: the probes (the benchmark's by name, assemblies by absolute path), their knobs, the
        // channels turned off
        private readonly List<string> _probes = [], _knobs = [], _off = [];
        private string _preset = "", _system = "", _hold = "", _settingsFile = "", _reference = "", _catch = "";
        private string _template = "Metal System Trace", _test = "";
        private bool _defaults, _explore, _fast, _noShots, _hud, _trace, _capture, _catchCapture, _passes, _cameraLog, _holding;
        private string _repeatText = "", _sessionsText = "1", _ab = "", _filmText = "0", _savePlan = "";
        private double _film;
        // measure: the measured passes in each game session, the sessions (taken in turn for A and B with --ab)
        private int _repeat = 1, _sessions = 1;
        private string _app = PortFolder.DefaultApp, _res = "", _script = "", _installed = "";
        private List<string> _only = [];
        // --on: the run's own triggers as written, and all its rules with the defaults (Triggers.cs)
        private readonly List<string> _on = [];
        private List<Rule> _rules = [];
        private string _setKeys = " ";
        private string _settings = "", _out = "";
        // Ctrl-C (or TERM): the game is stopped, and what it measured is reported
        private volatile bool _stopped;

        // How it plays (RunPlan.Play.Mode): the kinds of its scenarios, or "jump"
        private string Mode = "";
        private Lineup _lineup = null!;

        // What the run is for: the verb that started it
        private Purpose _purpose = Purpose.Run;

        // The run's folder once it played, and whether it was stopped (calibrate and study read them)
        public string Folder => _out;
        public bool Stopped => _stopped;

        // A study's session: the run's folder (the study's), and whether it takes screenshots (its variant's first round)
        public string Into { get; init; } = "";
        public bool TakesShots { get; init; } = true;
        // Where your settings.toml and port-settings.toml are: your documents folder, or a study's copy of them (made when
        // it began, so that a game you play meanwhile changes none of its sessions)
        public string Yours { get; init; } = Docs;
        // Only checked, not played: a game running is no reason to refuse
        private bool _checking;
        // The game's documents folder for the run (the benchmark's; aside while only checked)
        private string _instance = Instance;

        public int Run(string[] args, Purpose purpose)
        {
            _purpose = purpose;
            if (!Parse(args, out int status))
                return status;
            // measure --ab: a study of two variants, the setting as the run has it (A) and changed (B)
            if (_ab.Length > 0)
                return AB(args);
            if (_savePlan.Length > 0)
                return SavePlan(args);
            // One benchmark game at a time, whoever starts it (two runs, a study and a run): held until this one is over
            using var playing = Playing() ?? throw new PortException("another benchmark run is starting or playing: wait for it to end.");
            Check();
            Prepare();
            if (_explore)
                return Explore();
            return Play();
        }

        // A study's variant: its options read and checked as a run's are, its settings made in a folder of their own (a game
        // may be playing in the benchmark's), before any game starts
        public void Validate(string[] args, Purpose purpose)
        {
            _purpose = purpose;
            if (!Parse(args, out _))
                Die("a study's command takes no --help.");
            Checked();
        }

        // Checked and its settings made aside, nothing played
        private void Checked()
        {
            _checking = true;
            Check();
            _instance = Path.Combine(Benchmarks, "studies", $".check-{Environment.ProcessId}");
            try
            {
                Prepare();
            }
            finally
            {
                if (Directory.Exists(_instance))
                    Directory.Delete(_instance, true);
                _instance = Instance;
            }
        }

        // The command line as it was, less the options of its own that a study plays itself
        private static List<string> Without(string[] args, params string[] own)
        {
            var kept = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (own.Contains(args[i]))
                    i++;
                else
                    kept.Add(args[i]);
            }
            return kept;
        }

        // A study's file for this command line, its sessions and its variants (A and B with --ab)
        private List<string> StudyLines(string[] args)
        {
            var verb = _purpose == Purpose.Profile ? "profile" : "measure";
            var lines = new List<string>
            {
                $"{verb} {string.Join(' ', Without(args, "--ab", "--sessions", "--save-plan").Select(Study.Quote))}".TrimEnd(),
                $"sessions {(_sessionsText != "1" || _ab.Length > 0 ? _sessionsText : Study.DefaultSessions.ToString(CultureInfo.InvariantCulture))}",
            };
            if (_ab.Length > 0)
                lines.AddRange(["variant A", $"variant B --port {Study.Quote(_ab)}"]);
            return lines;
        }

        // measure --ab key=value [--sessions n]: the study of A (the port setting as the run has it) and B (key=value), n
        // sessions each
        private int AB(string[] args)
        {
            if (_reference.Length > 0)
                Die("--compare: an A/B run sets its two sides against each other in its own report.");
            if (!_ab.Contains('='))
                Die($"--ab needs a port setting as key=value, not '{_ab}'.");
            if (!Regex.IsMatch(_sessionsText, "^[1-9][0-9]*$"))
                Die($"--sessions needs a number of game sessions, not '{_sessionsText}'.");
            // A's port settings, made as its sessions will have them, against B's
            Checked();
            var lines = StudyLines(args);
            if (_savePlan.Length > 0)
                return SaveStudy(lines, args);
            Study study;
            try
            {
                study = Study.Parse(lines);
            }
            catch (StudyException e)
            {
                Die(e.Message);
                return 1;
            }
            var text = $"# ./benchmark.sh measure {string.Join(' ', args.Select(Study.Quote))}\n{string.Join('\n', lines)}\n";
            return PlayStudy(NewStudy(study, text, "ab " + _ab[.._ab.IndexOf('=')]));
        }

        // --save-plan <file>: the command line as a study's file, to add variants to, nothing played
        private int SavePlan(string[] args)
        {
            if (_reference.Length > 0)
                Die("--compare: a study sets its variants against each other in its own report.");
            Checked();
            return SaveStudy(StudyLines(args), args);
        }

        private int SaveStudy(List<string> lines, string[] args)
        {
            if (File.Exists(_savePlan))
                Die($"{_savePlan} is there already: name another file.");
            if (args.FirstOrDefault(a => a.Contains('\'') && a.Contains('"')) is { } both)
                Die($"--save-plan: a study's file can't hold {both} (both kinds of quote).");
            var verb = _purpose == Purpose.Profile ? "profile" : "measure";
            File.WriteAllText(_savePlan, $"""
                # A study made from ./benchmark.sh {verb} {string.Join(' ', args.Select(Study.Quote))}
                # Add variants (variant <name> <options>) or a grid (axis <option> <value>...), then: ./benchmark.sh study {_savePlan}
                {string.Join('\n', lines)}{(_ab.Length > 0 ? "" : "\nvariant base")}

                """);
            Output.Info($"The study's file: {_savePlan} (./benchmark.sh study {_savePlan} plays it)");
            return 0;
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
                    Die("investigate <tool> <places...>: trace, capture, hud, catch <clean run>, passes, film <seconds>, camera-log or hold.");
                switch (args[i++])
                {
                    case "profile":
                        Die("profile is a command of its own: ./benchmark.sh profile [test | places].");
                        break;
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
                        Die($"no tool '{tool}' (trace, capture, hud, catch <clean run>, passes, film <seconds>, camera-log or hold).");
                        break;
                }
            }
            var withValue = new[] { "--set", "--port", "--settings", "--compare", "--app", "--preset", "--system", "--repeat", "--sessions", "--ab",
                "--trace-template", "--on", "--env", "--save-plan", "--probe", "--knob", "--channels" };
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
                    case "--env": _env.Add(args[++i]); break;
                    case "--probe" when _purpose is not Purpose.Explore: _probes.Add(args[++i]); break;
                    case "--knob" when _purpose is not Purpose.Explore: _knobs.Add(args[++i]); break;
                    case "--channels" when _purpose is not Purpose.Explore:
                        foreach (var c in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (c.Length > 1 && c[0] == '-' && c[1..] is "zones" or "runtime")
                                _off.Add(c[1..]);
                            else if (c.Length > 1 && c[0] == '+' && Program.OwnProbes.Contains(c[1..]))
                                _probes.Add(c[1..]);
                            else
                                Die($"--channels takes -zones, -runtime or +{string.Join(", +", Program.OwnProbes)}, not '{c}'.");
                        }
                        break;
                    // What each verb adds
                    case "--compare" when _purpose is Purpose.Run or Purpose.Check or Purpose.Measure or Purpose.Profile: _reference = args[++i]; break;
                    case "--no-shots" when _purpose is Purpose.Run: _noShots = true; break;
                    case "--fast" when _purpose is Purpose.Run: _fast = true; break;
                    case "--repeat" when _purpose is Purpose.Measure: _repeatText = args[++i]; break;
                    case "--sessions" when _purpose is Purpose.Measure: _sessionsText = args[++i]; break;
                    case "--ab" when _purpose is Purpose.Measure && Into.Length == 0: _ab = args[++i]; break;
                    case "--save-plan" when _purpose is Purpose.Measure or Purpose.Profile && Into.Length == 0: _savePlan = args[++i]; break;
                    case "--trace-template" when _trace: _template = args[++i]; break;
                    case "--catch-capture" when _catch.Length > 0: _catchCapture = true; break;
                    case "--on" when _purpose is not Purpose.Explore: _on.Add(args[++i]); break;
                    case "-h" or "--help":
                        Console.WriteLine(CommandUsage);
                        return false;
                    // A developer's option of before the verbs: said where it went
                    case "--profile":
                        Die("--profile is a command of its own: ./benchmark.sh profile [test | places].");
                        break;
                    case "--trace" or "--capture" or "--hud" or "--passes" or "--camera-log" or "--catch" or "--film" or "--hold":
                        Die($"{a} is a tool of investigate: ./benchmark.sh investigate {a[2..]}{a switch { "--catch" => " <clean run>", "--film" => " <seconds>", _ => "" }} " +
                            $"{(a == "--hold" ? "<place>" : "<places...>")}.");
                        break;
                    case "--repeat":
                        Die("--repeat is for numbers: ./benchmark.sh measure [test] --repeat <n>.");
                        break;
                    case "--explore":
                        Die("explore is a command of its own: ./benchmark.sh explore.");
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
            // A tool and a profile look at places: jumped to in a flight, the game paused on the way
            if (_purpose is Purpose.Investigate or Purpose.Profile && _places.Count > 0 && !(_places.Count == 1 && LineupHasTest(_places[0])))
                _fast = true;
            return true;
        }

        // The last run of a test that passed played as check plays it: the same settings, screenshots, no tools and no KSA
        // Metal switches; or null
        private string? LastPassed(string test, string mode) =>
            (Directory.Exists(Benchmarks) ? Directory.GetDirectories(Benchmarks) : []).Where(IsRun).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
                .FirstOrDefault(d => RunRecord.Read(d) is { Status: Status.Passed } r && r.Test == test && r.Mode == mode &&
                                     r.Fact("Mode") == mode && r.Fact("Preset") == PresetFact && r.Fact("Settings changed") == ChangedFact &&
                                     r.Fact("Tools") == "none" && r.Fact("KSA Metal switches") == "none" && r.Fact("Environment") == null &&
                                     r.WarmUp == null && r.Fact("Warm-up") == null && r.Passes == 1);

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
            foreach (var kv in _env)
            {
                var key = kv.Split('=')[0];
                if (!Regex.IsMatch(kv, "^[A-Za-z_][A-Za-z0-9_]*=") )
                    Die($"--env needs NAME=value, not '{kv}'.");
                if (key is "KSA_BENCH_OUT" or "STARMAP_INSTANCE_PATH" or "KSA_BENCH_HANG_LIMIT")
                    Die($"--env: {key} is the benchmark's own.");
            }
            if (!TakesShots)
                _noShots = true;
            // The probes: the benchmark's by name, an assembly by its path (absolute: the game starts from elsewhere)
            for (int p = 0; p < _probes.Count; p++)
            {
                if (OwnProbes.Contains(_probes[p]))
                    continue;
                if (!_probes[p].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    Die($"--probe takes {string.Join(", ", OwnProbes)} or a probe's assembly (.dll), not '{_probes[p]}'.");
                _probes[p] = Path.GetFullPath(_probes[p]);
                if (!File.Exists(_probes[p]))
                    Die($"--probe: no {_probes[p]}.");
            }
            if (_probes.Distinct().Count() < _probes.Count)
                Die("--probe: the same probe twice.");
            foreach (var kv in _knobs)
                if (!System.Text.RegularExpressions.Regex.IsMatch(kv, @"^[A-Za-z0-9_-]+\.[A-Za-z0-9_.-]+="))
                    Die($"--knob needs <probe>.<key>=<value>, not '{kv}'.");
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
            // The app's director reads the run's plan (plan.json): one made before reads the options this program no
            // longer writes, and would play nothing
            if (BenchmarkOlder())
                Die($"the app at {_app} has an older benchmark than this port folder's: update it with ./port.sh.");

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

            if (!File.Exists(Path.Combine(Yours, "settings.toml")))
                Die("start the game once before benchmarking it (it has no settings yet).");
            if (!_checking && GamesRunning().Count > 0)
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

        // The lock of a benchmark run that starts a game, or null when another run holds it
        public static FileStream? Playing()
        {
            Directory.CreateDirectory(Benchmarks);
            try
            {
                return new FileStream(Path.Combine(Benchmarks, ".playing"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return null;
            }
        }

        // The games running on this Mac, by process id
        public static List<int> GamesRunning() =>
            Ask("pgrep", "-f", "dotnet (KSA|StarMap).dll").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();

        // Whether the app's director is older than plans (its assembly names no RunPlan); a stand-in without one is not
        private bool BenchmarkOlder()
        {
            var director = Path.Combine(_res, "Benchmark", "KsaMetal.Benchmark.dll");
            return File.Exists(director) && !Encoding.Latin1.GetString(File.ReadAllBytes(director)).Contains("RunPlan", StringComparison.Ordinal);
        }

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
            if (_test == "full" && (_purpose == Purpose.Profile || _hud || _trace || _capture || _catch.Length > 0 || _passes || _film > 0 || _cameraLog))
                Die("the tools look at one test: choose mixed, tour or warp (or places).");
            if (_purpose == Purpose.Profile || _hud || _trace || _capture || _catch.Length > 0 || _passes)
            {
                if (!DriverHas("ksaMtlFrameStats"))
                    Die("the tools need the app's KSA Metal driver, which this app doesn't have.");
            }
            if (_purpose == Purpose.Profile)
            {
                if (!DriverHas("ksaMtlZoneMode"))
                    Die("this app's KSA Metal can't time the game's GPU zones: update it with ./port.sh.");
                _tools.Add(RunPlan.ProfiledTool);
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
            CheckTriggers();
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

        // The run's triggers: --on's rules, investigate catch's and passes', and the defaults; what their actions need
        private void CheckTriggers()
        {
            var own = new List<string>(_on);
            // A picture that goes wrong now and then: dumped when it looks broken against the clean run
            if (_catch.Length > 0)
                own.Add(_catchCapture ? "broken: passes, capture, screenshot" : "broken: passes, screenshot");
            if (_passes)
                own.Add("checkpoint: passes, screenshot");
            try
            {
                _rules = Triggers.For(own);
            }
            catch (FormatException e)
            {
                Die($"--on {e.Message}.");
            }
            foreach (var rule in _rules.Take(_on.Count))
            {
                if (rule.When is Condition.Broken or Condition.Checkpoint && _noShots)
                    Die($"--on '{rule}' acts at the screenshots: it can't go with --no-shots.");
                if (_test == "full" && rule.Actions.Any(Triggers.Heavy))
                    Die("triggers that dump or capture look at one test: choose mixed, tour or warp (or places).");
                if (rule.When is Condition.Checkpoint && rule.Has(TriggerAction.Capture) && Named == 0)
                    Die("a capture takes several GB at every screenshot: choose places (./benchmark.sh investigate capture jupiter2).");
                if (rule.When is Condition.Checkpoint && rule.Has(TriggerAction.Passes) && Named == 0 && !_fast && _test != "mixed")
                    Die("passes take minutes at every screenshot: choose places, or the Mixed Test.");
                // A moment needs the game's profilers alone
                if (rule.Actions.Any(a => a is TriggerAction.Screenshot or TriggerAction.Passes or TriggerAction.Capture) &&
                    !DriverHas("ksaMtlFrameStats"))
                    Die("triggers need the app's KSA Metal driver, which this app doesn't have.");
                if (rule.Has(TriggerAction.Passes) && !DriverHas("ksaMtlPassDump"))
                    Die("this app's KSA Metal can't dump passes: update it with ./port.sh.");
                if (rule.Has(TriggerAction.Passes) && FreeGb() < 1)
                    Die($"a trigger that dumps passes needs 1 GB free on this disk ({FreeGb()} GB free).");
                if (rule.Has(TriggerAction.Capture) && _capture)
                    Die("investigate capture captures at every place already: a trigger's capture can't go with it.");
                if (rule.Has(TriggerAction.Capture))
                {
                    if (!DriverHas("ksaMtlCapture"))
                        Die("this app's KSA Metal can't capture frames: update it with ./port.sh.");
                    if (FreeGb() < 20)
                        Die($"a trigger that captures frames needs 20 GB free on this disk ({FreeGb()} GB free).");
                }
                if (rule.Actions.Any(Triggers.Heavy))
                    _tools.Add($"the trigger {rule}");
            }
        }
    }
}
