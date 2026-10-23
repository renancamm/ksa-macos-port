// The benchmark's report and comparison, run by benchmark.sh in the port folder.
//
//   report <dir>       <dir>/results.json (written by the mod) + <dir>/system.txt and <dir>/run.txt (written by the
//                      script, "key: value" lines) + <dir>/game.log -> <dir>/report.json and <dir>/report.md (to paste
//                      into a GitHub issue). Exits with 1 when the run had problems (it failed).
//   compare <a> <b>    two run folders: the settings that differ, per part of each section b's numbers against a's, and
//                      each screenshot of b against a's (marked copies of those that differ go to <b>/diff/). Exits
//                      with 1 when screenshots differ, 2 when the runs don't compare (different modes).
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

public static class Program
{
    // Changes smaller than this are within run-to-run noise (two runs on one Mac differ by 1% in most parts, 4% at most)
    private const double Significant = 0.05;
    // A screenshot differs from another when more of its blocks than this changed (percent; see Images.cs): fast mode
    // takes the same screenshots every time, the cinematic tour's vary a little with the frames the Mac happened to draw
    private const double FastShotLimit = 1.0, CinematicShotLimit = 3.0;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 2 && args[0] == "report")
            return Report(args[1]);
        if (args.Length == 3 && args[0] == "compare")
            return Compare(args[1], args[2]);
        Console.Error.WriteLine("usage: report <dir> | compare <a> <b>");
        return 2;
    }

    private static int Report(string dir)
    {
        var system = new JsonObject();
        foreach (var (key, value) in KeyValues(Path.Combine(dir, "system.txt")))
            system[key] = value;
        var resultsFile = Path.Combine(dir, "results.json");
        var results = File.Exists(resultsFile) ? JsonNode.Parse(File.ReadAllText(resultsFile)) as JsonObject ?? new JsonObject() : new JsonObject();
        var problems = Problems(dir, results);
        var report = new JsonObject
        {
            ["system"] = system, ["mode"] = (string?)results["mode"] ?? "cinematic", ["passed"] = problems.Count == 0,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode?)p).ToArray()), ["loadSeconds"] = results["loadSeconds"]?.DeepClone(),
            ["sections"] = results["sections"]?.DeepClone()
        };
        File.WriteAllText(Path.Combine(dir, "report.json"), report.ToJsonString(Indented));
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(report));
        Console.WriteLine(Path.Combine(dir, "report.md"));
        return problems.Count == 0 ? 0 : 1;
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

    // What went wrong in a run: how the game ended, the script's steps and screenshots, and errors in the game's log
    // (KSA Metal's, the game's own and exceptions nobody caught)
    private static List<string> Problems(string dir, JsonObject results)
    {
        var problems = new List<string>();
        var ending = KeyValues(Path.Combine(dir, "run.txt")).Where(kv => kv.key == "Game").Select(kv => kv.value).FirstOrDefault();
        if (ending != null && ending != "quit normally")
            problems.Add($"the game {ending}");
        if (KeyValues(Path.Combine(dir, "run.txt")).Any(kv => kv.key == "Alongside"))
            problems.Add("another game ran at the same time, sharing the Mac's GPU and memory");
        if (results.Count == 0)
            problems.Add("the run wrote no results");
        else if (results["completed"]?.GetValue<bool>() != true)
            problems.Add("the run did not play to its end");
        foreach (var section in Sections(results).OfType<JsonObject>())
        {
            foreach (var e in section["errors"] as JsonArray ?? new JsonArray())
                problems.Add($"{section["name"]}: {e}");
        }
        var log = Path.Combine(dir, "game.log");
        if (File.Exists(log))
        {
            var errors = new Dictionary<string, int>();
            var ansi = new Regex(@"\x1b\[[0-9;]*m");
            var gameError = new Regex(@"^\d\d:\d\d:\d\d\s+(ERROR|FATAL)\b");
            foreach (var raw in File.ReadLines(log))
            {
                var line = ansi.Replace(raw, "").Trim();
                if (line.StartsWith("[ksa-metal ERROR]", StringComparison.Ordinal) || gameError.IsMatch(line) ||
                    line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                {
                    var key = line.Length > 160 ? line[..160] + "..." : line;
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                }
            }
            foreach (var (line, count) in errors.Take(10))
                problems.Add($"game.log: {line}{(count > 1 ? $" ({count} times)" : "")}");
            if (errors.Count > 10)
                problems.Add($"game.log: {errors.Count - 10} more kinds of errors");
        }
        return problems;
    }

    private static JsonArray Sections(JsonObject report) => report["sections"] as JsonArray ?? new JsonArray();

    private static string Markdown(JsonObject report)
    {
        var md = new StringBuilder();
        md.AppendLine("## KSA Metal benchmark");
        md.AppendLine();
        var problems = (report["problems"] as JsonArray ?? new JsonArray()).Select(p => (string?)p).ToList();
        if (problems.Count == 0)
            md.AppendLine("**Passed**: the run played to its end without errors.");
        else
        {
            md.AppendLine($"**Failed**: {problems.Count} problem{(problems.Count > 1 ? "s" : "")} (the parts they concern may not compare with other runs):");
            md.AppendLine();
            foreach (var p in problems)
                md.AppendLine($"- {p}");
        }
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        foreach (var (key, value) in report["system"] as JsonObject ?? new JsonObject())
            md.AppendLine($"| {key} | {value} |");
        md.AppendLine($"| Loading | {Num(report["loadSeconds"], "F1")} s |");
        md.AppendLine();
        md.AppendLine((string?)report["mode"] == "fast"
            ? "Fast mode: each part is the view standing still (the game paused) at the screenshot of that name."
            : "Each part is measured along the way, between two screenshots.");
        md.AppendLine("Frame: time between two frames (median, 95th percentile). GPU: time the GPU worked on a frame. Hitches: frames longer than twice the median.");
        md.AppendLine();
        md.AppendLine("| Section | Part | FPS | Frame ms | p95 ms | GPU ms | Hitches | Worst ms | Draws | GPU memory MB |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var section in Sections(report).OfType<JsonObject>())
        {
            string name = (string?)section["name"] ?? "?";
            foreach (var w in (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                md.AppendLine($"| {name} | {w["name"]} | {Num(w["averageFps"], "F1")} | {Num(w["frameMsMedian"], "F1")} | " +
                              $"{Num(w["frameMsP95"], "F1")} | {Num(w["gpuMsMedian"], "F1")} | {w["hitches"]} | " +
                              $"{Num(w["frameMsMax"], "F0")} | {Num(w["drawsMedian"], "F0")} | {w["gpuMemoryPeakMb"]} |");
                name = "";
            }
        }
        return md.ToString();
    }

    private static string Num(JsonNode? node, string format) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    private static int Compare(string a, string b)
    {
        var (ra, rb) = (Load(a), Load(b));
        if (ra == null || rb == null)
            return 2;
        Console.WriteLine($"a: {a}");
        Console.WriteLine($"b: {b}");
        Console.WriteLine();
        string modeA = (string?)ra["mode"] ?? "cinematic", modeB = (string?)rb["mode"] ?? "cinematic";
        if (modeA != modeB)
        {
            Console.WriteLine($"The runs played in different modes ({modeA}, {modeB}): neither their numbers nor their screenshots compare.");
            return 2;
        }
        foreach (var (run, report) in new[] { ("a", ra), ("b", rb) })
        {
            if (report["passed"]?.GetValue<bool>() == false)
                Console.WriteLine($"Run {run} failed (see its report.md): the parts its problems concern may not compare.");
        }
        Settings(Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, Directory.Exists(b) ? b : Path.GetDirectoryName(b)!);
        Numbers(ra, rb);
        return Screenshots(Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, Directory.Exists(b) ? b : Path.GetDirectoryName(b)!,
            modeA == "fast" ? FastShotLimit : CinematicShotLimit);
    }

    // The settings that differ between the two runs (settings.toml in their folders)
    private static void Settings(string a, string b)
    {
        Dictionary<string, string> Read(string dir)
        {
            var file = Path.Combine(dir, "settings.toml");
            var all = new Dictionary<string, string>();
            if (!File.Exists(file))
                return all;
            foreach (var line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq > 0 && !line.StartsWith('#') && !line.StartsWith('['))
                    all[line[..eq].Trim()] = line[(eq + 3)..].Trim();
            }
            return all;
        }
        var (sa, sb) = (Read(a), Read(b));
        var differ = sa.Keys.Union(sb.Keys).Where(k => sa.GetValueOrDefault(k) != sb.GetValueOrDefault(k)).OrderBy(k => k).ToList();
        if (differ.Count == 0)
        {
            Console.WriteLine("Same settings.");
            Console.WriteLine();
            return;
        }
        Console.WriteLine("| Setting | a | b |");
        Console.WriteLine("|---|---|---|");
        foreach (var k in differ)
            Console.WriteLine($"| {k} | {sa.GetValueOrDefault(k, "")} | {sb.GetValueOrDefault(k, "")} |");
        Console.WriteLine();
    }

    private static void Numbers(JsonObject ra, JsonObject rb)
    {
        var parts = Windows(ra);
        var after = Windows(rb);
        Console.WriteLine("| Section | Part | Frame ms a | Frame ms b | Change | GPU ms a | GPU ms b | Change |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var (key, wa) in parts)
        {
            if (!after.TryGetValue(key, out var wb))
                continue;
            Console.WriteLine($"| {key.section} | {key.part} | {Num(wa["frameMsMedian"], "F1")} | {Num(wb["frameMsMedian"], "F1")} | " +
                              $"{Change(wa["frameMsMedian"], wb["frameMsMedian"])} | {Num(wa["gpuMsMedian"], "F1")} | " +
                              $"{Num(wb["gpuMsMedian"], "F1")} | {Change(wa["gpuMsMedian"], wb["gpuMsMedian"])} |");
        }
        Console.WriteLine();
        Console.WriteLine($"Changes over {Significant:P0} are marked: faster (+) or slower (-). Smaller ones are within run-to-run noise.");
        Console.WriteLine();
    }

    // Each screenshot of a against b's of the same name; marked copies of b's that differ go to b/diff/
    private static int Screenshots(string a, string b, double shotLimit)
    {
        var shots = Directory.GetFiles(a, "*_*.png").Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (shots.Count == 0)
        {
            Console.WriteLine("No screenshots in a to compare.");
            return 0;
        }
        // The limits of screenshots that change from run to run (in either run's sections)
        var limits = new Dictionary<string, double>();
        foreach (var dir in new[] { a, b })
        {
            foreach (var section in Sections(Load(dir) ?? new JsonObject()).OfType<JsonObject>())
            {
                foreach (var (shot, limit) in section["shotLimits"] as JsonObject ?? new JsonObject())
                {
                    var name = $"{section["name"]}_{shot}.png";
                    limits[name] = Math.Max(limits.GetValueOrDefault(name, shotLimit), limit?.GetValue<double>() ?? 0);
                }
            }
        }
        var diffDir = Path.Combine(b, "diff");
        if (Directory.Exists(diffDir))
            Directory.Delete(diffDir, true);
        int differ = 0;
        Console.WriteLine("| Screenshot | Changed | Mean change | |");
        Console.WriteLine("|---|---:|---:|---|");
        foreach (var name in shots)
        {
            var pathB = Path.Combine(b, name);
            if (!File.Exists(pathB))
            {
                // A run without screenshots compares by its numbers only
                continue;
            }
            var (ia, ib) = (Images.Read(Path.Combine(a, name)), Images.Read(pathB));
            if (ia == null || ib == null)
            {
                Console.WriteLine($"| {name} | | | **unreadable** |");
                differ++;
                continue;
            }
            var d = Images.Compare(ia, ib);
            double allowed = limits.GetValueOrDefault(name, shotLimit);
            bool same = d.ChangedPercent <= allowed;
            Console.WriteLine($"| {name} | {d.ChangedPercent:F2}% | {d.MeanChange:F1} | {(same ? "same" : "**differs**")}{(allowed > shotLimit ? $" (up to {allowed:F0}%)" : "")} |");
            if (same)
                continue;
            differ++;
            Directory.CreateDirectory(diffDir);
            Images.Write(Path.Combine(diffDir, name), Images.Mark(ib, d));
        }
        Console.WriteLine();
        Console.WriteLine($"Changed: the share of the screenshot's {Images.Block}-pixel blocks whose colour moved by more than {Images.BlockLimit:F0} " +
                          $"(of 255); over {shotLimit:F0}% it differs (more for views that physics, particles or the sea change every time).");
        if (differ > 0)
            Console.WriteLine($"{differ} screenshot{(differ > 1 ? "s differ" : " differs")}: {diffDir} shows where (in red).");
        return differ > 0 ? 1 : 0;
    }

    private static JsonObject? Load(string path)
    {
        if (Directory.Exists(path))
            path = Path.Combine(path, "report.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"no report at {path}");
            return null;
        }
        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    private static Dictionary<(string section, string part), JsonObject> Windows(JsonObject report)
    {
        var all = new Dictionary<(string, string), JsonObject>();
        foreach (var section in Sections(report).OfType<JsonObject>())
            foreach (var w in (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                all[((string?)section["name"] ?? "?", (string?)w["name"] ?? "?")] = w;
        return all;
    }

    private static string Change(JsonNode? a, JsonNode? b)
    {
        if (a is not JsonValue va || b is not JsonValue vb || !va.TryGetValue<double>(out var x) || !vb.TryGetValue<double>(out var y) || x <= 0)
            return "";
        double change = (y - x) / x;
        string text = change.ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);
        if (Math.Abs(change) < Significant)
            return text;
        // Less time is faster
        return change < 0 ? $"**{text} (+)**" : $"**{text} (-)**";
    }
}
