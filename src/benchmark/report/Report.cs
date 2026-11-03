// The benchmark's report and comparison, run by benchmark.sh in the port folder, and by KSA Metal Hooks for a run from
// the game's settings.
//
//   system <dir> <app> [<key>=<value>...]  -> <dir>/system.txt: this Mac, the app, the run's facts given as key=value
//                      (in that order), and the window from <dir>/settings.toml
//   report <dir>       <dir>/results.json (written by the director; results-2.json... for repeats, merged into medians) +
//                      <dir>/system.txt and <dir>/run.txt ("key: value" lines) + <dir>/game.log -> <dir>/report.json
//                      and <dir>/report.md and <dir>/report.html. Exits with 1 when the run had problems (it failed).
//   finish <dir>       the port settings and the hooks from game.log into system.txt (when it has them not), then the
//                      report, then <dir>/<dir's name>.zip to share: the report, every frame's numbers, the logs, crash
//                      reports of the game since the run started, the settings, and the screenshots as JPEG, your home
//                      folder's name replaced by ~. Exits as report does.
//   show <dir>         prints the report for a terminal, up to the facts about the Mac
//   page <dir>         <dir>/report.html from report.json (for a run older than the page)
//   runs <folder> [n]  a table of the last n runs in a benchmarks folder
//   comparable <run> <mode> <sections>  exits with 2 and says why when a run about to play won't compare with that one
//   compare <a> <b>    two runs (folders or shared zips): a verdict, the Macs and settings that differ, per part of each
//                      section b's numbers against a's, and each screenshot of b against a's (marked copies of those that
//                      differ go to <b>/diff/; not when a run played in a running game), as compare.md and compare.html in
//                      b's folder. Exits with 1 when screenshots differ, 2 when the runs don't compare (different modes, or
//                      no part in common).
//   profile <dir>      <dir>/gpuprof.csv (KSA Metal's GPU profiler, one window per measured part) -> <dir>/profile.md:
//                      the encoders that took the most GPU time in each part
//   trace <dir>        <dir>/trace-parts.xml and <dir>/trace-gpu.xml (tables exported from an Instruments trace: the
//                      parts' signposts, metal-gpu-intervals) -> <dir>/trace.md: the same, as Instruments measured it
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KsaMetalBenchmark;

namespace KsaMetalBenchmarkReport;

public static partial class Program
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        // The numbers it reads and writes are the invariant culture's, whatever the Mac's region. This program is a
        // process of its own, so it sets that once, here; the director formats each number itself, since in the game
        // the culture is the player's.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length >= 1 && args[0] == "command")
            return Command(args[1..]);
        if (args.Length >= 3 && args[0] == "system")
            return SystemFacts(args[1], args[2], args[3..]);
        if (args.Length == 2 && args[0] == "report")
            return Report(args[1]);
        if (args.Length == 2 && args[0] == "show")
            return Show(args[1]);
        if (args.Length == 2 && args[0] == "page")
            return Page(args[1]);
        if (args.Length is 2 or 3 && args[0] == "runs")
            return Runs(args[1], args.Length == 3 && int.TryParse(args[2], out var count) ? count : 15);
        if (args.Length == 4 && args[0] == "comparable")
            return Comparable(args[1], args[2], args[3]);
        if (args.Length == 2 && args[0] == "finish")
            return Finish(args[1]);
        if (args.Length == 3 && args[0] == "compare")
            return Compare(args[1], args[2]);
        if (args.Length == 2 && args[0] == "profile")
            return Profile(args[1]);
        if (args.Length == 2 && args[0] == "trace")
            return Trace(args[1]);
        Console.Error.WriteLine("usage: system <dir> <app> [key=value...] | report <dir> | finish <dir> | show <dir> | page <dir> | runs <folder> [n] | comparable <run> <mode> <sections> | compare <a> <b> | profile <dir> | trace <dir>");
        return 2;
    }

    private static int Report(string dir)
    {
        var system = new JsonObject();
        foreach (var (key, value) in KeyValues(Path.Combine(dir, "system.txt")))
            system[key] = value;
        // The run's results, and those of its repeats
        var unreadable = new List<string>();
        var runs = Directory.GetFiles(dir, "results*.json")
            .Select(f => (file: f, n: Path.GetFileNameWithoutExtension(f) is var name && name.StartsWith("results-", StringComparison.Ordinal) && int.TryParse(name[8..], out var k) ? k : 1))
            .OrderBy(r => r.n).Select(r =>
            {
                try
                {
                    return JsonNode.Parse(File.ReadAllText(r.file)) as JsonObject ?? new JsonObject();
                }
                catch (JsonException)
                {
                    unreadable.Add($"{Path.GetFileName(r.file)} can't be read: the game stopped while writing it");
                    return new JsonObject();
                }
            }).ToList();
        var results = runs.Count > 0 ? runs[0] : new JsonObject();
        var problems = Problems(dir, results);
        problems.AddRange(unreadable);
        for (int i = 1; i < runs.Count; i++)
        {
            if (runs[i]["completed"]?.GetValue<bool>() != true)
                problems.Add($"repeat {i + 1} did not play to its end");
            foreach (var section in Sections(runs[i]).OfType<JsonObject>())
                foreach (var e in section["errors"] as JsonArray ?? new JsonArray())
                    problems.Add($"repeat {i + 1}: {section["name"]}: {e}");
        }
        // A run stopped on purpose (Esc, Ctrl-C) isn't a run that went wrong: what it says is that it was stopped
        bool stopped = problems.Any(p => p.EndsWith("stopped with Esc", StringComparison.Ordinal) || p.EndsWith("stopped with Ctrl-C", StringComparison.Ordinal));
        if (stopped)
            problems.RemoveAll(p => p.EndsWith("stopped with Esc", StringComparison.Ordinal) || p.EndsWith("stopped with Ctrl-C", StringComparison.Ordinal) ||
                                    p.EndsWith("did not play to its end", StringComparison.Ordinal) || p == "the run wrote no results");
        var sections = runs.Count > 1 ? Merge(runs) : results["sections"]?.DeepClone() as JsonArray ?? new JsonArray();
        var notes = Notes(dir, system, sections);
        var report = new JsonObject
        {
            ["schema"] = 2, ["headline"] = Headline(sections), ["system"] = system, ["mode"] = (string?)results["mode"] ?? ((string?)system["Mode"] is { } mode && mode.StartsWith("fast", StringComparison.Ordinal) ? "fast" : "cinematic"), ["runs"] = Math.Max(runs.Count, 1),
            ["drawable"] = results["drawable"]?.DeepClone(), ["passed"] = problems.Count == 0 && !stopped, ["stopped"] = stopped,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode?)p).ToArray()),
            ["notes"] = new JsonArray(notes.Select(p => (JsonNode?)p).ToArray()), ["loadSeconds"] = results["loadSeconds"]?.DeepClone(),
            ["sections"] = sections
        };
        File.WriteAllText(Path.Combine(dir, "report.json"), report.ToJsonString(Indented));
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(report));
        Html.WriteReport(dir, report);
        Console.WriteLine(Path.Combine(dir, "report.md"));
        return problems.Count == 0 && !stopped ? 0 : 1;
    }

    // The run's main number in a few words, which the Mac Port tab shows: "30 FPS", "78.4 game days"
    private static string Headline(JsonArray sections)
    {
        var report = new JsonObject { ["sections"] = sections.DeepClone() };
        var (fps, _, _, _, _) = Html.Headline(report);
        double days = Html.WarpDays(report);
        var words = new List<string>();
        if (!double.IsNaN(fps))
            words.Add($"{fps:F0} FPS");
        if (days > 0)
            words.Add($"{days:F1} game days");
        return string.Join(", ", words);
    }

    // system.txt: what the report says about this Mac and the run
    // The run's facts in system.txt, in this order, and what one a writer leaves out means (null: left out). benchmark.sh
    // gives them all; the hooks, for a run from the game's settings, those that aren't the defaults' (they add the port
    // settings and the hooks; finish takes those from the game's log otherwise).
    private static readonly (string Key, string? Default)[] RunFacts =
    [
        ("StarMap", null), ("System", null), ("Test", null), ("Sections", null), ("Mode", null), ("Preset", "none, own settings"),
        ("Settings changed", "none"), ("Start", null), ("KSA Metal switches", "none"), ("Tools", "none"),
        ("port settings", null), ("Hooks", null),
    ];

    private static int SystemFacts(string dir, string app, string[] facts)
    {
        var settings = File.Exists(Path.Combine(dir, "settings.toml")) ? File.ReadAllLines(Path.Combine(dir, "settings.toml")) : [];
        string Setting(string key) => settings.Select(l => l.Trim()).Where(l => l.StartsWith(key + " = ", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 3)..].Trim('"')).FirstOrDefault() ?? "?";
        string Plist(string key) => Run("/usr/libexec/PlistBuddy", "-c", $"Print :{key}", Path.Combine(app, "Contents", "Info.plist")).Trim();
        var displays = Run("/usr/sbin/system_profiler", "SPDisplaysDataType");
        string Display(string key) => string.Join("; ", displays.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith(key + ": ", StringComparison.Ordinal)).Select(l => l[(key.Length + 2)..]));
        var power = Regex.Match(Run("/usr/bin/pmset", "-g", "batt"), "'(.*)'").Groups[1].Value;
        var lowPower = Regex.Match(Run("/usr/bin/pmset", "-g"), @"lowpowermode\s+(\d)").Groups[1].Value == "1" ? "on" : "off";
        long memory = long.TryParse(Sysctl("hw.memsize"), out var bytes) ? bytes / 1073741824 : 0;
        var lines = new List<string>
        {
            $"Mac: {Sysctl("hw.model")}",
            $"Chip: {Sysctl("machdep.cpu.brand_string")}",
            $"CPU cores: {Sysctl("hw.perflevel0.physicalcpu")} performance, {(Sysctl("hw.perflevel1.physicalcpu") is { Length: > 0 } e ? e : "0")} efficiency",
            $"GPU cores: {Display("Total Number of Cores").Split(';')[0]}",
            $"Memory: {memory} GB",
            $"macOS: {Run("/usr/bin/sw_vers", "-productVersion").Trim()} ({Run("/usr/bin/sw_vers", "-buildVersion").Trim()})",
            $"Displays: {Display("Resolution")}",
            $"Power: {power}",
            $"Low Power Mode: {lowPower}",
            $"KSA: {Plist("CFBundleShortVersionString")}",
            $"Port: {Plist("KSAPortVersion")} ({Plist("KSAPortCommit")})"
        };
        var given = facts.Where(f => f.IndexOf('=') > 0).Select(f => (Key: f[..f.IndexOf('=')], Value: f[(f.IndexOf('=') + 1)..])).ToList();
        foreach (var (key, fallback) in RunFacts)
        {
            if (given.FirstOrDefault(f => f.Key == key).Value is { } value)
                lines.Add($"{key}: {value}");
            else if (fallback != null)
                lines.Add($"{key}: {fallback}");
        }
        foreach (var (key, value) in given.Where(f => !RunFacts.Any(r => r.Key == f.Key)))
            lines.Add($"{key}: {value}");
        lines.Add($"Window: {Setting("width")} x {Setting("height")}, {Setting("mode")}, on {Setting("screenName")}");
        lines.Add($"Vsync: {Setting("vsync")}, FPS limit {Setting("fpslimit")}");
        lines.Add($"Anti-aliasing: {Setting("antialiasing")}");
        File.WriteAllLines(Path.Combine(dir, "system.txt"), lines);
        return 0;
    }

    private static string Sysctl(string name) => Run("/usr/sbin/sysctl", "-n", name).Trim();

    // A program's output (empty when it fails)
    private static string Run(string program, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments)
                start.ArgumentList.Add(a);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    // "key: value" lines
    private static IEnumerable<(string key, string value)> KeyValues(string file)
    {
        if (!File.Exists(file))
            yield break;
        foreach (var line in File.ReadAllLines(file))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
                yield return (line[..colon], line[(colon + 2)..]);
        }
    }

    // The numbers that the parts of repeated runs are merged by: the median over the runs (the worst for those that
    // count events), with the range of the medians of frame and GPU time
    private static readonly string[] MedianFields =
        { "averageFps", "frameMsMedian", "frameMsP95", "frameMsP99", "frameMsMax", "gpuMsMedian", "gpuMsP95", "drawsMedian", "gpuMemoryPeakMb", "footprintPeakMb", "gameDays", "speed", "secondsToSpeed" };
    private static readonly string[] MaxFields = { "hitches", "thermalMax", "shadersCompiled", "pipelinesCreated", "simMsMax", "substeps", "bubblesMax", "vehiclesMax" };

    private static JsonArray Merge(List<JsonObject> runs)
    {
        var merged = Sections(runs[0]).DeepClone().AsArray();
        foreach (var section in merged.OfType<JsonObject>())
        {
            foreach (var w in (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var same = runs.Select(r => Sections(r).OfType<JsonObject>().FirstOrDefault(x => (string?)x["name"] == (string?)section["name"]))
                    .Select(x => (x?["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().FirstOrDefault(y => (string?)y["name"] == (string?)w["name"]))
                    .OfType<JsonObject>().ToList();
                double[] Values(string field) => same.Select(x => x[field] is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN)
                    .Where(d => !double.IsNaN(d)).OrderBy(d => d).ToArray();
                foreach (var field in MedianFields)
                {
                    var v = Values(field);
                    if (v.Length > 0)
                        w[field] = Math.Round(v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2, 2);
                }
                foreach (var field in MaxFields)
                {
                    var v = Values(field);
                    if (v.Length > 0)
                        w[field] = v[^1];
                }
                foreach (var (field, range) in new[] { ("frameMsMedian", "frameMsRange"), ("gpuMsMedian", "gpuMsRange"), ("speed", "speedRange") })
                {
                    var v = Values(field);
                    if (v.Length > 1)
                        w[range] = new JsonArray(v[0], v[^1]);
                }
                // A part that didn't get to its speed in one of the runs didn't, whatever the others did
                if (same.Any(x => x["gameDays"] is not null && x["secondsToSpeed"] is null))
                    w["secondsToSpeed"] = null;
                w["runs"] = same.Count;
            }
        }
        return merged;
    }

    // What a reader of the report should know about the conditions of the run: they don't fail it, but the numbers of
    // the parts they concern may not compare with other runs
    private static List<string> Notes(string dir, JsonObject system, JsonArray sections)
    {
        var notes = new List<string>();
        string Sys(string key) => (string?)system[key] ?? "";
        if (Sys("Power").Contains("Battery", StringComparison.OrdinalIgnoreCase))
            notes.Add("ran on battery: a Mac can be slower than when plugged in");
        if (Sys("Low Power Mode") == "on")
            notes.Add("Low Power Mode was on: the Mac runs slower");
        if (Sys("KSA Metal switches") is { Length: > 0 } switches && switches != "none")
            notes.Add($"KSA Metal switches were set ({switches}): they change what the driver does");
        if (Sys("port settings") is { Length: > 0 } port && port != "defaults")
            notes.Add($"port settings were changed ({port}): they change what the game draws or how");
        // --port lines the game doesn't list: a value that is the default, or a setting waiting for another one (its "needs")
        if (Sys("Settings changed") is { Length: > 0 } changed && changed.IndexOf("port-settings: ", StringComparison.Ordinal) is >= 0 and var from_)
        {
            var running = Sys("port settings");
            foreach (var kv in changed[(from_ + 15)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (running.Length > 0 && !running.Contains(kv.Split('=')[0] + "=", StringComparison.Ordinal) && !running.Contains(kv.Split('=')[0] + " =", StringComparison.Ordinal))
                    notes.Add($"--port {kv} is not among the settings the game ran with: it is the setting's default, or it waits for another setting");
        }
        if (Sys("Start") is { Length: > 0 } start && start.StartsWith("in the running game", StringComparison.Ordinal))
            notes.Add("played in a running game, after what was played before: its numbers compare best with runs started the same way");
        if (Sys("Tools") is { Length: > 0 } tools && tools != "none")
            notes.Add($"measured with {tools}: their own cost is in the numbers");
        var run = KeyValues(Path.Combine(dir, "run.txt")).GroupBy(kv => kv.key).ToDictionary(g => g.Key, g => g.Last().value);
        // "warning" is how a Mac with little memory runs this game, nearly always: only the worst level says something
        if (run.TryGetValue("Memory pressure", out var pressure) && pressure == "critical")
            notes.Add($"macOS reported memory pressure ({pressure}): the game may have had to wait for memory, which shows as stutters");
        if (run.TryGetValue("GPU before the run", out var busy))
            notes.Add($"the GPU was busy before the run ({busy}): something else may have shared it");
        if (run.TryGetValue("Swap used", out var swap) && swap.Split(' ') is [var from, "->", var to, ..] &&
            double.TryParse(from, out var f) && double.TryParse(to, out var t) && t - f > 256)
            notes.Add($"the Mac swapped memory to disk during the run ({swap})");
        foreach (var (_, crash) in KeyValues(Path.Combine(dir, "run.txt")).Where(kv => kv.key == "Crash report").TakeLast(3))
            notes.Add($"macOS wrote a crash report, {crash}, in ~/Library/Logs/DiagnosticReports (the zip holds it)");
        var hot = new List<string>();
        var compiled = new List<string>();
        foreach (var section in sections.OfType<JsonObject>())
        {
            foreach (var w in (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                if (w["thermalMax"] is JsonValue th && th.TryGetValue<double>(out var thermal) && thermal >= 2)
                    hot.Add($"{section["name"]} {w["name"]}");
                if (w["shadersCompiled"] is JsonValue sc && sc.TryGetValue<double>(out var shaders) && shaders > 0)
                    compiled.Add($"{section["name"]} {w["name"]} ({shaders:F0})");
            }
        }
        if (hot.Count > 0)
            notes.Add($"the Mac was hot enough to slow down during {string.Join(", ", hot)}");
        if (compiled.Count > 0)
            notes.Add($"shaders compiled while measuring {string.Join(", ", compiled.Take(8))}{(compiled.Count > 8 ? $" and {compiled.Count - 8} more parts" : "")}: " +
                      "the first run after an update of the game or the port compiles them, the next runs don't");
        var liveErrors = LiveErrors(dir, sections);
        if (liveErrors.Count > 0)
        {
            var common = liveErrors.MaxBy(e => e.Value).Key;
            notes.Add($"the game logged {liveErrors.Values.Sum()} errors during the time warp, {liveErrors.Count} kind{(liveErrors.Count > 1 ? "s" : "")}, most often \"{(common.Length > 80 ? common[..80] + "..." : common)}\" (game.log)");
        }
        return notes;
    }

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex GameError = new(@"^\d\d:\d\d:\d\d\s+(ERROR|FATAL)\b");
    private static readonly Regex SectionLine = new(@"^\[benchmark\] section (\S+) ");
    private static readonly Regex EndLine = new(@"^\[benchmark\] (done after|stopped:)");

    private static HashSet<string> LiveSections(JsonObject results) =>
        Sections(results).OfType<JsonObject>().Where(s => s["live"]?.GetValue<bool>() == true).Select(s => (string?)s["name"] ?? "").ToHashSet();

    // The game's own errors while time warp ran at speeds that hold it back, by how often each came: a condition of the
    // Warp Test, not a broken run, at settings that leave its stuck pieces as they are
    private static Dictionary<string, int> LiveErrors(string dir, JsonArray sections)
    {
        var errors = new Dictionary<string, int>();
        var live = LiveSections(new JsonObject { ["sections"] = sections.DeepClone() });
        if (live.Count == 0)
            return errors;
        foreach (var log in Directory.GetFiles(dir, "game*.log"))
        {
            bool inLive = false;
            foreach (var raw in File.ReadLines(log))
            {
                var line = Ansi.Replace(raw, "").Trim();
                if (SectionLine.Match(line) is { Success: true } section)
                    inLive = live.Contains(section.Groups[1].Value);
                else if (EndLine.IsMatch(line))
                    inLive = false;
                else if (inLive && GameError.IsMatch(line))
                {
                    var plain = GameError.Replace(line, "").Trim();
                    errors[plain] = errors.GetValueOrDefault(plain) + 1;
                }
            }
        }
        return errors;
    }

    // What went wrong in a run: how the game ended, the script's steps and screenshots, and errors in the game's log
    // (KSA Metal's, the game's own and exceptions nobody caught)
    private static List<string> Problems(string dir, JsonObject results)
    {
        var problems = new List<string>();
        foreach (var (key, ending) in KeyValues(Path.Combine(dir, "run.txt")).Where(kv => kv.key.StartsWith("Game", StringComparison.Ordinal)))
        {
            if (ending is not ("quit normally" or "kept running"))
                problems.Add($"the game {(key == "Game" ? "" : $"(repeat {key[5..]}) ")}{ending}");
        }
        if (KeyValues(Path.Combine(dir, "run.txt")).Any(kv => kv.key.StartsWith("Alongside", StringComparison.Ordinal)))
            problems.Add("another game ran at the same time, sharing the Mac's GPU and memory");
        // A game gone before its first frame, with nothing in its log to say why: one thing it can't start without
        if (KeyValues(Path.Combine(dir, "run.txt")).Any(kv => kv.value.Contains("before its first frame", StringComparison.Ordinal)) &&
            !Directory.GetFiles(dir, "game*.log").Any(log => File.ReadLines(log).Any(l => l.Contains("Unhandled exception", StringComparison.Ordinal))))
            problems.Add("the display must be awake and the Mac unlocked for the game to start");
        if (results.Count == 0)
            problems.Add("the run wrote no results");
        else if (results["completed"]?.GetValue<bool>() != true)
            problems.Add("the run did not play to its end");
        foreach (var section in Sections(results).OfType<JsonObject>())
        {
            foreach (var e in section["errors"] as JsonArray ?? new JsonArray())
                problems.Add($"{section["name"]}: {e}");
        }
        var blank = Path.Combine(dir, "blank.txt");
        if (File.Exists(blank))
            problems.AddRange(File.ReadAllLines(blank));
        // A hook that was skipped (the game's code it changes has moved) or failed: the run didn't play the port as it is
        var hooks = KeyValues(Path.Combine(dir, "system.txt")).FirstOrDefault(kv => kv.key == "Hooks").value ?? "";
        foreach (var hook in hooks.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (hook.Split(": ", 2) is [_, var status] &&
                (status.StartsWith("skipped", StringComparison.Ordinal) || status.StartsWith("failed", StringComparison.Ordinal)))
                problems.Add($"the hooks' {hook}");
        }
        // The game's log of each session (game-2.log... for repeats)
        var liveSections = LiveSections(results);
        foreach (var log in Directory.GetFiles(dir, "game*.log").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(log);
            var errors = new Dictionary<string, int>();
            bool gpuReset = false;
            bool live = false;
            foreach (var raw in File.ReadLines(log))
            {
                var line = Ansi.Replace(raw, "").Trim();
                if (SectionLine.Match(line) is { Success: true } section)
                    live = liveSections.Contains(section.Groups[1].Value);
                else if (EndLine.IsMatch(line))
                    live = false;
                // The game then runs on its defaults: every number would be of other settings than the run says
                if (line.Contains("failed to load settings file", StringComparison.Ordinal))
                    problems.Add($"{name}: the game could not read the run's settings.toml and played with its defaults");
                if (line.StartsWith("[ksa-metal ERROR]", StringComparison.Ordinal) || GameError.IsMatch(line) && !live ||
                    line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                {
                    gpuReset |= line.Contains("kIOGPUCommandBufferCallbackError", StringComparison.Ordinal);
                    // The same error at another second is the same error
                    var plain = GameError.Replace(line, "$1");
                    var key = plain.Length > 160 ? plain[..160] + "..." : plain;
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                }
            }
            // macOS recovered the GPU from a hang or fault: the game's work since then was thrown away
            if (gpuReset)
                problems.Add($"{name}: the GPU hung or faulted and macOS reset it: from then on the game's frames were thrown away, " +
                             "so the numbers after it are not real");
            foreach (var (line, count) in errors.Take(10))
                problems.Add($"{name}: {line}{(count > 1 ? $" ({count} times)" : "")}");
            if (errors.Count > 10)
                problems.Add($"{name}: {errors.Count - 10} more kinds of errors");
        }
        return problems;
    }

    private static JsonArray Sections(JsonObject report) => report["sections"] as JsonArray ?? new JsonArray();

    // Where a report's facts about the Mac and the run begin: the terminal stops there
    private const string FactsHeading = "### This Mac and the run";

    private static string Markdown(JsonObject report)
    {
        var md = new StringBuilder();
        var system = report["system"] as JsonObject ?? new JsonObject();
        var mode = (string?)report["mode"] == "fast" ? "fast" : "cinematic";
        var test = Html.TestName((string?)system["Test"]);
        md.AppendLine($"## KSA Metal benchmark: {test}{(test == "Warp Test" ? "" : $", {mode}")}");
        md.AppendLine();
        var problems = (report["problems"] as JsonArray ?? new JsonArray()).Select(p => (string?)p).ToList();
        bool stopped = report["stopped"]?.GetValue<bool>() == true;
        if (stopped)
            md.AppendLine(Sections(report).OfType<JsonObject>().Any(x => (x["windows"] as JsonArray)?.Count > 0)
                ? "**Stopped**: you stopped the run. Below is what it measured until then."
                : "**Stopped**: you stopped the run before it finished measuring a part.");
        else if (problems.Count == 0)
            md.AppendLine("**Passed**: the run played to its end without errors.");
        if (problems.Count > 0)
        {
            md.AppendLine($"**Failed**: {problems.Count} problem{(problems.Count > 1 ? "s" : "")} (the parts they concern may not compare with other runs):");
            md.AppendLine();
            foreach (var p in problems)
                md.AppendLine($"- {p}");
        }
        var (fps, gpu, slowest, section, memory) = Html.Headline(report);
        var headline = new List<string>();
        if (!double.IsNaN(fps))
            headline.Add($"**{fps:F0} FPS** across the parts{(double.IsNaN(gpu) ? "" : $", GPU {gpu:F1} ms a frame")}");
        if (slowest != null)
            headline.Add($"slowest {section} {slowest["name"]}, {Num(slowest["averageFps"], "F0")} FPS");
        if (Html.WarpDays(report) is > 0 and var days)
            headline.Add($"**{days:F1} game days** in time warp");
        if (memory > 0)
            headline.Add($"memory peak {memory / 1024:F1} GB");
        if (headline.Count > 0)
        {
            md.AppendLine();
            md.AppendLine(string.Join("; ", headline) + ".");
        }
        var notes = (report["notes"] as JsonArray ?? new JsonArray()).Select(p => (string?)p).ToList();
        if (notes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Conditions worth knowing:");
            md.AppendLine();
            foreach (var n in notes)
                md.AppendLine($"- {n}");
        }
        md.AppendLine();
        if (Sections(report).OfType<JsonObject>().SelectMany(x => (x["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()).Any())
        {
            md.AppendLine("| Section | Part | FPS | Frame ms | p95 ms | GPU ms | Hitches | Worst ms | Draws | Memory MB | GPU memory MB | Shaders |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var part in Sections(report).OfType<JsonObject>())
            {
                string name = (string?)part["name"] ?? "?";
                foreach (var w in (part["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    string frame = Num(w["frameMsMedian"], "F1");
                    if (w["frameMsRange"] is JsonArray range)
                        frame += $" ({Num(range[0], "F1")}-{Num(range[1], "F1")})";
                    md.AppendLine($"| {name} | {w["name"]} | {Num(w["averageFps"], "F1")} | {frame} | " +
                                  $"{Num(w["frameMsP95"], "F1")} | {Num(w["gpuMsMedian"], "F1")} | {w["hitches"]} | " +
                                  $"{Num(w["frameMsMax"], "F0")} | {Num(w["drawsMedian"], "F0")} | {w["footprintPeakMb"]} | {w["gpuMemoryPeakMb"]} | " +
                                  $"{w["shadersCompiled"]} |");
                    name = "";
                }
            }
            md.AppendLine();
            bool anyPlain = Sections(report).OfType<JsonObject>().Any(x => x["live"]?.GetValue<bool>() != true);
            md.AppendLine(mode == "fast" && anyPlain
                ? "Fast mode: each part is the view standing still (the game paused) at the screenshot of that name."
                : "Each part is measured along the way, between two screenshots.");
            md.AppendLine("Frame: time between two frames (median, 95th percentile). GPU: time the GPU worked on a frame. Hitches: frames longer than twice the median. " +
                          "Memory: the game's footprint at its peak, and what KSA Metal allocated for the GPU. Shaders: compiled while measuring (each one a stutter).");
        }
        Warp(md, report);
        md.AppendLine();
        md.AppendLine(FactsHeading);
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        foreach (var (key, value) in system)
            md.AppendLine($"| {key} | {value} |");
        if (report["drawable"] is { } drawable)
            md.AppendLine($"| Drawn | {drawable} pixels |");
        if (report["loadSeconds"] is JsonValue load && load.TryGetValue<double>(out var seconds) && seconds > 0)
            md.AppendLine($"| Loading | {Num(load, "F1")} s |");
        int runs = report["runs"]?.GetValue<int>() ?? 1;
        if (runs > 1)
            md.AppendLine($"| Runs | {runs}: each number is the median of the runs, Frame ms shows their range |");
        return md.ToString();
    }

    // The report for a terminal: the verdict and the numbers, without the facts about the Mac
    private static int Show(string dir)
    {
        var file = Path.Combine(dir, "report.md");
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"no report at {file}");
            return 2;
        }
        var md = File.ReadAllText(file);
        int facts = md.IndexOf(FactsHeading, StringComparison.Ordinal);
        Console.Write(Terminal.Layout(facts > 0 ? md[..facts] : md));
        return 0;
    }

    // report.html for a run that has a report but no page yet
    private static int Page(string dir)
    {
        var report = Load(dir);
        if (report == null)
            return 2;
        Html.WriteReport(dir, report);
        return 0;
    }

    // The last runs of a benchmarks folder, newest first: when, what, how it ended, its main number
    private static int Runs(string folder, int count)
    {
        var all = Directory.Exists(folder)
            ? Directory.GetDirectories(folder).Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d{4}-\d{2}-\d{2}_\d{4}") && File.Exists(Path.Combine(d, "report.json")))
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList()
            : new List<string>();
        if (all.Count == 0)
        {
            Console.WriteLine("No runs yet.");
            return 0;
        }
        var md = new StringBuilder("| Run | Test | Mode | Result | FPS | GPU ms | Warp days | Settings |\n|---|---|---|---|---:|---:|---:|---|\n");
        foreach (var dir in all.Take(count))
        {
            JsonObject? report;
            try
            {
                report = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "report.json"))) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (report == null)
                continue;
            var (fps, gpu, _, _, _) = Html.Headline(report);
            double days = Html.WarpDays(report);
            string Sys(string key) => (string?)report["system"]?[key] ?? "";
            var preset = Sys("Preset") switch { "none, own settings" => "own", "none, a fresh install's settings" => "defaults", var named => named };
            var settings = string.Join(", ", new[] { preset, Sys("Settings changed") is { Length: > 0 } c && c != "none" ? c : "" }.Where(x => x.Length > 0));
            md.AppendLine($"| {Path.GetFileName(dir)} | {Html.TestName(Sys("Test"))} | {(Sys("Test") == "warp" ? "" : report["mode"])} | {(report["passed"]?.GetValue<bool>() == true ? "passed" : report["stopped"]?.GetValue<bool>() == true ? "stopped" : "**failed**")} | " +
                          $"{(double.IsNaN(fps) ? "" : fps.ToString("F0"))} | {(double.IsNaN(gpu) ? "" : gpu.ToString("F1"))} | {(days > 0 ? days.ToString("F1") : "")} | {settings} |");
        }
        Console.Write(Terminal.Layout(md.ToString()));
        Console.WriteLine();
        Console.WriteLine($"{Math.Min(count, all.Count)} of {all.Count} runs.");
        return 0;
    }

}
