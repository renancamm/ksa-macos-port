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

// benchmark.sh: the port folder's command, which starts this program from the folder (command <args...>) with
// KSA_PORT_FOLDER set. It plays a test of the game in one session, in a documents folder of its own, and writes the
// report; its other commands list the tests, the runs, a run's report, two runs compared, and clean old runs. The rest of
// this program (Report.cs) is the report, which it calls in the same process.
public static partial class Program
{
    private const string CommandUsage = """
        Measures how KSA runs on this Mac: plays a test of the game in one session and writes a report, and a zip of it in the
        run's folder to share if you like.

        Usage: ./benchmark.sh [run] [options] [mixed | tour | warp | full | <places...>]
               ./benchmark.sh list | runs [n] | report [run] [test] [--again] | compare [run] [run] | clean [--keep n] [--yes]
          mixed      the Mixed Test (the default, about a minute): rockets, smoke, an explosion, a kitten and a cockpit, each
                     view standing still, the game paused
          tour       the Planetary Tour (about 5 minutes): the flight from Earth to Jupiter
          warp       the Warp Test (about 3 minutes): how far time warp gets in a fixed time, a ship alone and with debris
          full       all three, one after the other, each in a game of its own
          A place is a screenshot of a test (earth4): its scenario up to there. All with your own settings. The game takes
          over the screen: leave the mouse and keyboard alone until it is done, or press Ctrl-C here to stop it and keep
          what it measured. The game runs from its own documents folder, with a copy of your settings: your files stay
          untouched. Exits with 1 when the run failed (the game crashed or hung, a step failed, errors in its log).

          list       the tests, their scenarios and their places
          runs       your last runs (15, or n), the newest first
          report     a run's report here, and its page in your browser (default: the last run); --again: made
                     again from the run's store first, by this version of the benchmark
          compare    how the second run differs from the first: a verdict, the Macs, settings, numbers part by part, and
                     screenshots (exits with 1 when screenshots differ), and a page to see them side by side (with one run:
                     against the last; with none: the last against the one before it)
          clean      delete all but your last runs (30, or n), after asking (--yes: without asking)
          A run is a run's folder, its zip, the folder's name in your benchmarks folder, "last", "previous", or a
          baseline's name (below).

        Options, what plays:
          --system <name>    sol (the solar system) or earth (Earth and the Moon only: for Macs with 8 GB of memory)
                             (default: earth when what plays is all on Earth or the Moon, otherwise sol)
          --fast             with places: jump to them, the game paused on the way, instead of flying there
          --app <path>       the app to measure (default: ~/Applications/Kitten Space Agency.app)
        With what settings:
          --preset <name>    low, medium or high: fixed settings in a 1280x800 window, the same on every Mac (default: yours)
          --defaults         the settings of a fresh install instead of yours: the game's own and the port's defaults
          --set <key=value>  change one setting of settings.toml for the run (again for more), on top of the preset;
                             <section>.<key>=<value> adds it to that section when the file has no such key
          --settings <file>  change the settings in that file ("key = value" lines, as in settings.toml; a bare word
                             needs no quotes), on top of the preset and before --set
          --port <key=value>  change one setting of port-settings.toml for the run (again for more), on top of yours (with
                             a preset or --defaults: on top of the defaults)
        What it records, and what follows:
          --no-shots         take no screenshots
          --compare <run>    compare with that run at the end (and fail when screenshots differ); checked before the game starts

        For work on the port (see the benchmark's README):
          ./benchmark.sh check [test] [options]
                             the standard test of a change: the test (default mixed) at a fresh install's settings, compared
                             with the baseline main of that test (or the last run of it that passed); exits with 1 when
                             screenshots differ
          ./benchmark.sh measure [test] [--repeat n] [--sessions n] [--ab key=value] [options]
                             numbers to judge a change by: a warm-up pass first (left out), then n passes (3) in one game
                             session, each part's median and range; --sessions plays more games of it; --ab is a study of two
                             variants, the port setting key=value (B) and as the run has it (A), --sessions games each
          ./benchmark.sh study <file> | study resume [study] | study report [study]
                             variants of one measure (or profile) command, each in game sessions of its own (5), played in
                             turn and set against the baseline variant by their sessions; the file's lines: measure
                             <options>, sessions <n>, variant <name> <options>, axis <option> <value>..., baseline <name>.
                             It waits while another game runs; Ctrl-C stops the session playing and the study, resume
                             plays the rest (default: the last study not done). Its folder: the benchmarks folder's studies/
          ./benchmark.sh profile [test | places...] [options]
                             where the time goes: a warm-up, a measured pass and a profiled pass in one game session; the
                             profiled pass names the game's GPU zones in each part (profile.md), its own cost kept apart
          ./benchmark.sh calibrate [test] [n]
                             how much the test moves between runs of the same build on this Mac: n runs (3) at a fresh
                             install's settings, the spread of each screenshot and part, written as the test's noise profile
                             (the benchmarks folder's noise/), which comparisons hold each screenshot and part to
          ./benchmark.sh investigate <tool> <places...> [options]
                             a tool on places jumped to (or a whole test): trace (an Instruments trace;
                             --trace-template <name>), capture (an Xcode GPU frame capture at
                             each place), hud (Apple's Metal Performance HUD), catch <clean run> (a pass dump of a picture
                             that goes wrong now and then; --catch-capture adds a frame capture), passes (a pass dump at every
                             place: a clean run for catch), film <seconds> (screenshots along the way), camera-log (the
                             camera's place every frame), hold (stay at one place, paused, until you quit the game)
          --env <NAME=value> the game's environment for the run (again for more): KSA Metal's switches (KSA_MTL_...)
          --save-plan <file> with measure or profile: the command line as a study's file, to add variants to; plays nothing
          --probe <name | path>
                             a probe in the run (again for more): memory (the game's VRAM, KSA Metal's memory and the .NET
                             heap, and who holds them at each checkpoint), or a probe's assembly (.dll)
          --knob <probe>.<key>=<value>
                             a value for a probe (again for more)
          --channels <list>  -zones (the game's profilers), -runtime (.NET's counters), -power (the GPU's power and
                             clock): off; +memory: the probe added
          --on '<rule>'      with run, check, measure, profile or investigate: a trigger, '<condition>: <action>, ...' (again for
                             more). Conditions: frame>N ms, broken, checkpoint, stuck, gpu-error; actions: moment,
                             screenshot, passes, capture, sample, stop, max N ('frame>50ms: moment, max 5')
          ./benchmark.sh query [run] '<sql>'
                             SQL on a run's store (run.db, with host.db as host): frames, presents, shown, waits,
                             memory, zone_stats, moments, slices, events (default: the last run)
          ./benchmark.sh timeline [run]
                             the run in Perfetto's viewer, in your browser: its parts, frame and GPU times, each frame's way
                             to the screen, KSA Metal's waits, the game's zones around slow frames, the counters
          ./benchmark.sh baseline [set <name> [run...] | remove <name>]
                             name runs to compare with, one of each test (check compares with main)
          ./benchmark.sh explore
                             start the benchmark's game paused, to fly the free camera around and capture places for the
                             tour (@key lines, for benchmark.txt) with benchkey [name] in its console; quit it when done

        The benchmark is in the app (Contents/Resources/Benchmark/), built for the KSA version this port folder was made for.
        The Mac Port tab in the game's settings runs it too.
        """;

    // The benchmark's own probes (--probe memory)
    internal static readonly string[] OwnProbes = ["memory"];

    // Presets: fixed settings on a fresh install's, the same on every Mac, so reports compare. Without one, your own
    // settings are used. All run in a 1280x800 window without Vsync (frame times are then the game's, not the display's).
    private static readonly string[] PresetCommon = ["mode=\"Windowed\"", "width=1280", "height=800", "vsync=false", "fpslimit=240"];
    private static readonly Dictionary<string, string[]> Presets = new()
    {
        ["low"] = ["antialiasing=\"Disabled\"", "cloudUpscale=\"Low\"", "exhaustUpscale=\"Low\"", "ambientOcclusion=false",
            "terrainTessellation=false", "groundClutter=false", "kittenFurQuality=\"Low\"", "plumeTrailShadows=false",
            "explosionQuality=\"Low\"", "oceanSimQuality=\"Low\"", "particleCountQuality=\"Low\"", "IVARayTracing=false",
            "celestialTextureCap=1024", "vesselTextureCap=1024", "cascadedShadowMapSize=1024", "terrainShadowMapSize=1024"],
        ["medium"] = ["antialiasing=\"MultisampleX2\"", "cloudUpscale=\"Medium\"", "exhaustUpscale=\"Medium\"", "ambientOcclusion=true",
            "ambientOcclusionQuality=\"Medium\"", "terrainTessellation=true", "groundClutter=true", "kittenFurQuality=\"Medium\"",
            "plumeTrailShadows=false", "explosionQuality=\"Medium\"", "oceanSimQuality=\"Medium\"", "particleCountQuality=\"Medium\"",
            "IVARayTracing=false", "celestialTextureCap=2048", "vesselTextureCap=1024", "cascadedShadowMapSize=2048",
            "terrainShadowMapSize=1024"],
        ["high"] = ["antialiasing=\"MultisampleX4\"", "cloudUpscale=\"High\"", "exhaustUpscale=\"High\"", "ambientOcclusion=true",
            "ambientOcclusionQuality=\"High\"", "terrainTessellation=true", "groundClutter=true", "kittenFurQuality=\"High\"",
            "plumeTrailShadows=true", "explosionQuality=\"High\"", "oceanSimQuality=\"High\"", "particleCountQuality=\"High\"",
            "IVARayTracing=true", "celestialTextureCap=2048", "vesselTextureCap=2048", "cascadedShadowMapSize=2048",
            "terrainShadowMapSize=2048"],
    };

    private static string Docs => Path.Combine(PortFolder.Home, "Documents", "My Games", "Kitten Space Agency");
    private static string Benchmarks => Path.Combine(Docs, "benchmarks");
    // The game's documents folder during the run (STARMAP_INSTANCE_PATH, which the hooks and StarMap both follow)
    private static string Instance => Path.Combine(PortFolder.Home, "Library", "Caches", "ksa-macos-port", "benchmark", "instance");
    private static string Issues => $"https://github.com/{PortFolder.ReleasesRepo}/issues/new?template=benchmark-report.md";
    // The port folder's benchmark: an app not updated yet still gets the current reports and the current tests
    private static string FolderScript => Path.Combine(PortFolder.Skeleton, "Resources", "Benchmark", "benchmark.txt");

    private static int Command(string[] args)
    {
        try
        {
            return args switch
            {
                ["list", ..] => ListTests(),
                ["runs", .. var n] => RunsCommand(n.Take(1).ToArray()),
                ["report", .. var run] => ReportCommand(run.Where(a => a != "--again").ToArray(), run.Contains("--again")),
                ["compare", .. var runs] => CompareCommand(runs),
                ["query", .. var rest] => QueryCommand(rest),
                ["timeline", .. var rest] => TimelineCommand(rest),
                ["clean", .. var rest] => Clean(rest),
                ["baseline", .. var rest] => BaselineCommand(rest),
                ["run", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Run),
                ["check", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Check),
                ["measure", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Measure),
                ["profile", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Profile),
                ["calibrate", .. var rest] => Calibrate(rest),
                ["study", .. var rest] => StudyCommand(rest),
                ["investigate", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Investigate),
                ["explore", .. var rest] => new BenchmarkRun().Run(rest, Purpose.Explore),
                // A player's ./benchmark.sh and ./benchmark.sh tour: a run
                _ => new BenchmarkRun().Run(args, Purpose.Run),
            };
        }
        catch (PortException e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return e.Data["exit"] is int code ? code : 1;
        }
        // A run the report can't read (its record cut short), a full disk: said as a problem, not as a crash
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException
                                       or InvalidOperationException or NullReferenceException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static void Die(string message) => throw new PortException(message);

    // The output of one of the report's commands, which print what the command line would show
    private static (int Status, string Output) Quiet(Func<int> command)
    {
        var console = Console.Out;
        var text = new StringWriter();
        Console.SetOut(text);
        try
        {
            return (command(), text.ToString());
        }
        finally
        {
            Console.SetOut(console);
        }
    }

    // A tool's text output, empty when it fails
    private static string Ask(string tool, params string[] args) =>
        Tool.Exec(tool, args, out var output) == 0 ? output.Trim() : "";

    // What a command does
    private enum Purpose { Run, Check, Measure, Profile, Investigate, Calibrate, Explore }

    // The folder or zip a run's name stands for: a path, a folder's name in the benchmarks folder, "last" or "previous", or
    // a baseline's name (its run of that test)
    private static string RunFolder(string run, string? test = null)
    {
        var name = run.TrimEnd('/');
        if (ReadBaselines().GetValueOrDefault(name) is { } baseline)
        {
            var tests = baseline.Keys.ToList();
            var chosen = test ?? (tests.Count == 1 ? tests[0] : null);
            if (chosen == null)
                Die($"the baseline {name} has a run of each of {string.Join(", ", tests)}: name the run itself here (./benchmark.sh baseline lists them).");
            return Baseline(name, chosen!) ?? throw new PortException($"the baseline {name} has no run of the {Pages.TestName(chosen)}.");
        }
        if (name is "last" or "previous")
        {
            int skip = name == "last" ? 0 : 1;
            var dated = (Directory.Exists(Benchmarks) ? Directory.GetFileSystemEntries(Benchmarks) : [])
                .Select(Path.GetFileName).Where(d => Regex.IsMatch(d!, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{4}"))
                .OrderDescending(StringComparer.Ordinal);
            foreach (var d in dated)
            {
                if (!IsRun(Path.Combine(Benchmarks, d!)))
                    continue;
                if (skip-- == 0)
                    return Path.Combine(Benchmarks, d!);
            }
            Die($"there is no {name} run in {Benchmarks}.");
        }
        // A run with a record, a zip, or a run with only its store (an older record: report --again makes a new one)
        static bool Known(string dir) => HasRecord(dir) || File.Exists(Path.Combine(dir, RunStore.GameFile));
        if (Known(name) || File.Exists(name))
            return name;
        if (Known(Path.Combine(Benchmarks, name)))
            return Path.Combine(Benchmarks, name);
        Die($"no run '{name}' (no record there, and not a zip). ./benchmark.sh runs lists your runs.");
        return "";
    }

    // list: what can be played, from the lineup
    private static int ListTests()
    {
        var lineup = Lineup.Read(FolderScript);
        var o = new StringBuilder();
        foreach (var test in lineup.Tests)
            o.Append($"{test.Name} ({Lineup.Name(test.Kind)})\n    {string.Join(' ', test.Scenarios)}\n");
        o.Append($"full\n    {string.Join(", ", Lineup.Full)}, one after the other\n\nScenarios and their places:\n");
        foreach (var scenario in lineup.Scenarios)
            o.Append($"    {scenario.Name}: {string.Join(' ', scenario.Shots)}\n");
        Console.Write(o);
        return 0;
    }

    private static int RunsCommand(string[] n)
    {
        if (n.Length > 1 || (n.Length == 1 && !Regex.IsMatch(n[0], "^[1-9][0-9]*$")))
            Die($"runs takes a number of runs, not '{string.Join(' ', n)}'.");
        int status = Runs(Benchmarks, n.Length == 1 ? int.Parse(n[0]) : 15);
        if (Directory.Exists(Benchmarks))
            Output.Info($"They take {Ask("du", "-sh", Benchmarks).Split('\t')[0].Trim()} in {Benchmarks}");
        return status;
    }

    // A run, or a baseline's run of a test; --again: made again from its store first
    private static int ReportCommand(string[] args, bool again)
    {
        if (args.Length > 2 || args.Any(a => a.StartsWith('-')))
            Die("report [run] [test] [--again].");
        var run = RunFolder(args.Length > 0 ? args[0] : "last", args.Length > 1 ? args[1] : null);
        if (!Directory.Exists(run))
            Die("report takes a run's folder, not its zip.");
        if (again)
        {
            if (!File.Exists(Path.Combine(run, RunStore.GameFile)))
                Die($"{Path.GetFileName(run.TrimEnd('/'))} has no store (run.db) to make it again from: a run older than the store has none.");
            Finish.Again(run);
        }
        else if (!File.Exists(Path.Combine(run, "report.html")) && RunRecord.Read(run) is { } record)
            Pages.WriteReport(run, record);
        Show(run);
        Console.WriteLine();
        Output.Info($"The page: {run}/report.html");
        if (!Console.IsOutputRedirected)
            Tool.Exec("open", [Path.Combine(run, "report.html")], out _);
        return 0;
    }

    private static int CompareCommand(string[] runs)
    {
        if (runs.Length > 2)
        {
            Console.WriteLine(CommandUsage);
            return 1;
        }
        string a = runs.Length == 2 ? runs[0] : runs.Length == 1 ? runs[0] : "previous", b = runs.Length == 2 ? runs[1] : "last";
        string first, second;
        try
        {
            // A baseline's name stands for its run of the other run's test: that one first
            static string? TestOf(string run) => RunRecord.Read(Unpacked(run)) is { } record ? record.Test : null;
            if (ReadBaselines().ContainsKey(b) && !ReadBaselines().ContainsKey(a))
            {
                first = RunFolder(a);
                second = RunFolder(b, TestOf(first));
            }
            else
            {
                second = RunFolder(b);
                first = RunFolder(a, TestOf(second));
            }
        }
        catch (PortException e)
        {
            e.Data["exit"] = 2;
            throw;
        }
        return Compare(first, second);
    }

    // clean [--keep n] [--yes]: all but the last n runs deleted, after asking. Only what a run leaves counts as a run: a
    // folder named by its date and time (not an --explore's, which holds the places captured by hand); anything else
    // there stays.
    private static int Clean(string[] args)
    {
        string keepText = "30";
        bool yes = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--keep":
                    keepText = i + 1 < args.Length ? args[++i] : "";
                    break;
                case "--yes":
                    yes = true;
                    break;
                default:
                    Console.WriteLine(CommandUsage);
                    return 1;
            }
        }
        if (!Regex.IsMatch(keepText, "^[0-9]+$"))
            Die($"--keep needs a number of runs, not '{keepText}'.");
        int keep = int.Parse(keepText);
        if (!Directory.Exists(Benchmarks))
        {
            Output.Info("No runs.");
            return 0;
        }
        // Studies keep their runs: said, not deleted
        if (Directory.Exists(Studies) && Directory.GetDirectories(Studies).Where(d => File.Exists(Path.Combine(d, Study.FileName))).ToArray() is { Length: > 0 } studies)
            Output.Info($"{studies.Length} stud{(studies.Length > 1 ? "ies" : "y")} in {Studies} ({Ask("du", ["-shc", .. studies]).Split('\n')[^1].Split('\t')[0].Trim()}) stay: " +
                        "delete a study's folder when you are done with it.");
        // A baseline's runs stay, whatever their age
        var named = ReadBaselines().Values.SelectMany(t => t.Values).ToHashSet();
        var names = Directory.GetFileSystemEntries(Benchmarks).Select(p => Path.GetFileName(p)!)
            .Where(d => Regex.IsMatch(d, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{4}(_[0-9]{2}|_hold)?$"))
            .OrderDescending(StringComparer.Ordinal).Skip(keep).Where(n => !named.Contains(n)).ToList();
        var old = names.Select(n => Path.Combine(Benchmarks, n)).Where(Directory.Exists).ToList();
        if (old.Count == 0)
        {
            Output.Info($"Nothing to delete: you have {keep} runs or fewer.");
            return 0;
        }
        var size = Ask("du", ["-shc", .. old]).Split('\n')[^1].Split('\t')[0].Trim();
        Output.Info($"{old.Count} runs, {names[^1]} to {names[0]}, take {size}.");
        if (!yes)
        {
            if (Console.IsInputRedirected)
                Die("this deletes runs, so it asks first: run it from a terminal, or add --yes.");
            Console.Write($"    Delete them, and keep the last {keep}? [y/N] ");
            if (Console.ReadLine()?.Trim() is not ("y" or "Y" or "yes"))
            {
                Output.Info("Nothing deleted.");
                return 0;
            }
        }
        // Not while a run plays: its folder would go with the others
        using (BenchmarkRun.Playing() ?? throw new PortException("a benchmark run is playing: clean when it is done."))
            foreach (var d in old)
                Directory.Delete(d, true);
        Output.Info("Deleted.");
        return 0;
    }
}
