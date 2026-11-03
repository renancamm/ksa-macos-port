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
        Measures how KSA runs on this Mac: plays a fixed test of the game in one session and writes a report, and a zip of it
        in the run's folder to share if you like.

        Usage: ./benchmark.sh [options] [mixed | tour | warp | full]
               ./benchmark.sh [options] <scenario or place...>
               ./benchmark.sh list | runs [n] | report [run] | compare [run] [run] | clean [--keep n] [--yes]
          mixed      the Mixed Test (the default, about a minute): rockets, smoke, an explosion, a kitten and a cockpit, each
                     view standing still, the game paused
          tour       the Planetary Tour (about 5 minutes): the flight from Earth to Jupiter
          warp       the Warp Test (about 3 minutes): how far time warp gets in a fixed time, a ship alone and with debris
          full       all three, one after the other
          All with your own settings. A place is a screenshot of the test (earth4): its scenario up to there.
          The game takes over the screen: leave the mouse and keyboard alone until it is done, or press Ctrl-C here to stop it
          and keep what it measured. The game runs from its own documents folder, with a copy of your settings: your files stay
          untouched. Exits with 1 when the run failed (the game crashed or hung, a step failed, errors in its log).

          list       the tests, their scenarios and their places
          runs       your last runs (15, or n), the newest first
          report     a run's report here, and its page in your browser (default: the last run)
          compare    how the second run differs from the first: a verdict, the Macs, settings, numbers part by part, and
                     screenshots (exits with 1 when screenshots differ), and a page to see them side by side (with one run:
                     against the last; with none: the last against the one before it)
          clean      delete all but your last runs (30, or n), after asking (--yes: without asking)
          A run is a run's folder, its zip, the folder's name in your benchmarks folder, "last" or "previous".

        Options:
          --preset <name>    low, medium or high: fixed settings in a 1280x800 window, the same on every Mac (default: yours)
          --defaults         the settings of a fresh install instead of yours: the game's own and the port's defaults
          --system <name>    sol (the solar system) or earth (Earth and the Moon only: for Macs with 8 GB of memory)
                             (default: earth when what plays is all on Earth or the Moon, otherwise sol)
          --fast             with places: jump to them, the game paused on the way, instead of flying there
          --no-shots         take no screenshots
          --set <key=value>  change one setting of settings.toml for the run (again for more), on top of the preset;
                             <section>.<key>=<value> adds it to that section when the file has no such key
          --settings <file>  change the settings in that file ("key = value" lines, as in settings.toml; a bare word
                             needs no quotes), on top of the preset and before --set
          --port <key=value>  change one setting of port-settings.toml for the run (again for more), on top of yours (with
                             a preset or --defaults: on top of the defaults)
          --compare <run>    compare with that run at the end (and fail when screenshots differ); checked before the game starts
          --app <path>       the app to measure (default: ~/Applications/Kitten Space Agency.app)

        For work on the port (see the benchmark's README):
          --hold <place>     go to that place fast and stay there, the game paused, until you quit it
          --repeat <n>       play it n times, one game session each, and report the median and range of each number
          --profile          KSA Metal's GPU profiler on every measured part: profile.md, the encoders that cost the most
          --hud              Apple's Metal Performance HUD over the game, its per-frame log, and its reports (started from
                             its menu) in the run folder
          --trace            record an Instruments trace (Metal System Trace) of the run, parts marked; needs Xcode
          --trace-template <name>  another Instruments template for --trace (xcrun xctrace list templates)
          --capture          an Xcode GPU frame capture of the view at each place played (.gputrace, to open in Xcode)
          --catch <run>      for a picture that goes wrong now and then: KSA Metal watches the image, and a screenshot is compared
                             with that clean run's; a broken one gets a pass dump in caught/ (at most 3 each); needs 1 GB free
          --catch-capture    with --catch: an Xcode GPU frame capture after each pass dump (needs capture mode and 40 GB free)
          --passes           the pass dump at every place played: the clean run a caught one is compared with (needs places)
          --film <seconds>   a screenshot every that many seconds of the script's clock, in film/, to see the camera's moves
          --camera-log       the camera's place every frame in trace.csv, to check its moves are smooth
          --explore          start the benchmark's game paused, to fly the free camera around and capture places for the
                             tour (@key lines, for benchmark.txt) with benchkey [name] in its console; quit it when done

        The benchmark is in the app (Contents/Resources/Benchmark/), built for the KSA version this port folder was made for:
        Its director reads the run's options (run.json in the run's folder) when the game starts, in a documents folder of
        its own. The Mac Port tab in the game's settings runs it too.
        """;

    // Presets: fixed settings, the same on every Mac, so reports compare. Without one, your own settings are used. All run
    // in a 1280x800 window without Vsync (frame times are then the game's, not the display's).
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
                ["report", .. var run] => ReportCommand(run.Length > 0 ? run[0] : "last"),
                ["compare", .. var runs] => CompareCommand(runs),
                ["clean", .. var rest] => Clean(rest),
                _ => new BenchmarkRun().Run(args),
            };
        }
        catch (PortException e)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return e.Data["exit"] is int code ? code : 1;
        }
        // A run the report can't read (its report.json cut short), a full disk: said as a problem, not as a crash
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

    // The folder or zip a run's name stands for: a path, a folder's name in the benchmarks folder, "last" or "previous"
    private static string RunFolder(string run)
    {
        var name = run.TrimEnd('/');
        if (name is "last" or "previous")
        {
            int skip = name == "last" ? 0 : 1;
            var dated = (Directory.Exists(Benchmarks) ? Directory.GetFileSystemEntries(Benchmarks) : [])
                .Select(Path.GetFileName).Where(d => Regex.IsMatch(d!, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{4}"))
                .OrderDescending(StringComparer.Ordinal);
            foreach (var d in dated)
            {
                if (!File.Exists(Path.Combine(Benchmarks, d!, "report.json")))
                    continue;
                if (skip-- == 0)
                    return Path.Combine(Benchmarks, d!);
            }
            Die($"there is no {name} run in {Benchmarks}.");
        }
        if (File.Exists(Path.Combine(name, "report.json")) || File.Exists(name))
            return name;
        if (File.Exists(Path.Combine(Benchmarks, name, "report.json")))
            return Path.Combine(Benchmarks, name);
        Die($"no run '{name}' (no report.json there, and not a zip). ./benchmark.sh runs lists your runs.");
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

    private static int ReportCommand(string name)
    {
        var run = RunFolder(name);
        if (!Directory.Exists(run))
            Die("report takes a run's folder, not its zip.");
        if (!File.Exists(Path.Combine(run, "report.html")))
            Page(run);
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
            first = RunFolder(a);
            second = RunFolder(b);
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
        var names = Directory.GetFileSystemEntries(Benchmarks).Select(p => Path.GetFileName(p)!)
            .Where(d => Regex.IsMatch(d, @"^[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{4}(_[0-9]{2}|_hold)?$"))
            .OrderDescending(StringComparer.Ordinal).Skip(keep).ToList();
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
        foreach (var d in old)
            Directory.Delete(d, true);
        Output.Info("Deleted.");
        return 0;
    }
}
