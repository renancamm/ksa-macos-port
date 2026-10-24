// The benchmark's report and comparison, run by benchmark.sh in the port folder.
//
//   report <dir>       <dir>/results.json (written by the mod; results-2.json... for repeats, merged into medians) +
//                      <dir>/system.txt and <dir>/run.txt (written by the script, "key: value" lines) + <dir>/game.log
//                      -> <dir>/report.json and <dir>/report.md. Exits with 1 when the run had problems (it failed).
//   compare <a> <b>    two runs (folders or shared zips): the Macs and settings that differ, per part of each section
//                      b's numbers against a's, and each screenshot of b against a's (marked copies of those that
//                      differ go to <b>/diff/). Exits with 1 when screenshots differ, 2 when the runs don't compare
//                      (different modes).
//   profile <dir>      <dir>/gpuprof.csv (KSA Metal's GPU profiler, one window per measured part) -> <dir>/profile.md:
//                      the encoders that took the most GPU time in each part
//   trace <dir>        <dir>/trace-parts.xml and <dir>/trace-gpu.xml (tables exported from an Instruments trace: the
//                      parts' signposts, metal-gpu-intervals) -> <dir>/trace.md: the same, as Instruments measured it
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KsaMetalBenchmark;

namespace KsaMetalBenchmarkReport;

public static class Program
{
    // Changes smaller than this are within run-to-run noise (two runs on one Mac differ by 1% in most parts, 4% at most).
    // With repeats on both sides, a change counts when the ranges of the repeats don't overlap and it is over the
    // smaller threshold.
    private const double Significant = 0.05, SignificantRepeated = 0.02;
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
        if (args.Length == 2 && args[0] == "profile")
            return Profile(args[1]);
        if (args.Length == 2 && args[0] == "trace")
            return Trace(args[1]);
        Console.Error.WriteLine("usage: report <dir> | compare <a> <b> | profile <dir> | trace <dir>");
        return 2;
    }

    private static int Report(string dir)
    {
        var system = new JsonObject();
        foreach (var (key, value) in KeyValues(Path.Combine(dir, "system.txt")))
            system[key] = value;
        // The run's results, and those of its repeats
        var runs = Directory.GetFiles(dir, "results*.json")
            .Select(f => (file: f, n: Path.GetFileNameWithoutExtension(f) is var name && name.StartsWith("results-") && int.TryParse(name[8..], out var k) ? k : 1))
            .OrderBy(r => r.n).Select(r => JsonNode.Parse(File.ReadAllText(r.file)) as JsonObject ?? new JsonObject()).ToList();
        var results = runs.Count > 0 ? runs[0] : new JsonObject();
        var problems = Problems(dir, results);
        for (int i = 1; i < runs.Count; i++)
        {
            if (runs[i]["completed"]?.GetValue<bool>() != true)
                problems.Add($"repeat {i + 1} did not play to its end");
            foreach (var section in Sections(runs[i]).OfType<JsonObject>())
                foreach (var e in section["errors"] as JsonArray ?? new JsonArray())
                    problems.Add($"repeat {i + 1}: {section["name"]}: {e}");
        }
        var sections = runs.Count > 1 ? Merge(runs) : results["sections"]?.DeepClone() as JsonArray ?? new JsonArray();
        var notes = Notes(dir, system, sections);
        var report = new JsonObject
        {
            ["schema"] = 2, ["system"] = system, ["mode"] = (string?)results["mode"] ?? "cinematic", ["runs"] = Math.Max(runs.Count, 1),
            ["drawable"] = results["drawable"]?.DeepClone(), ["passed"] = problems.Count == 0,
            ["problems"] = new JsonArray(problems.Select(p => (JsonNode?)p).ToArray()),
            ["notes"] = new JsonArray(notes.Select(p => (JsonNode?)p).ToArray()), ["loadSeconds"] = results["loadSeconds"]?.DeepClone(),
            ["sections"] = sections
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

    // The numbers that the parts of repeated runs are merged by: the median over the runs (the worst for those that
    // count events), with the range of the medians of frame and GPU time
    private static readonly string[] MedianFields =
        { "averageFps", "frameMsMedian", "frameMsP95", "frameMsP99", "frameMsMax", "gpuMsMedian", "gpuMsP95", "drawsMedian", "gpuMemoryPeakMb", "footprintPeakMb" };
    private static readonly string[] MaxFields = { "hitches", "thermalMax", "shadersCompiled", "pipelinesCreated" };

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
                foreach (var (field, range) in new[] { ("frameMsMedian", "frameMsRange"), ("gpuMsMedian", "gpuMsRange") })
                {
                    var v = Values(field);
                    if (v.Length > 1)
                        w[range] = new JsonArray(v[0], v[^1]);
                }
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
        if (Sys("Mac settings") is { Length: > 0 } mac && mac != "defaults")
            notes.Add($"Mac settings were changed ({mac}): they change what the game draws");
        if (Sys("Game patches").Contains("skipped"))
            notes.Add("a game patch was skipped (the game's code changed): see Game patches");
        if (Sys("Tools") is { Length: > 0 } tools && tools != "none")
            notes.Add($"measured with {tools}: their own cost is in the numbers");
        var run = KeyValues(Path.Combine(dir, "run.txt")).ToDictionary(kv => kv.key, kv => kv.value);
        if (run.TryGetValue("Memory pressure", out var pressure) && pressure != "normal")
            notes.Add($"macOS reported memory pressure ({pressure}): the game may have had to wait for memory, which shows as stutters");
        if (run.TryGetValue("Swap used", out var swap) && swap.Split(' ') is [var from, "->", var to, ..] &&
            double.TryParse(from, out var f) && double.TryParse(to, out var t) && t - f > 256)
            notes.Add($"the Mac swapped memory to disk during the run ({swap})");
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
        return notes;
    }

    // What went wrong in a run: how the game ended, the script's steps and screenshots, and errors in the game's log
    // (KSA Metal's, the game's own and exceptions nobody caught)
    private static List<string> Problems(string dir, JsonObject results)
    {
        var problems = new List<string>();
        foreach (var (key, ending) in KeyValues(Path.Combine(dir, "run.txt")).Where(kv => kv.key.StartsWith("Game", StringComparison.Ordinal)))
        {
            if (ending != "quit normally")
                problems.Add($"the game {(key == "Game" ? "" : $"(repeat {key[5..]}) ")}{ending}");
        }
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
        // The game's log of each session (game-2.log... for repeats)
        foreach (var log in Directory.GetFiles(dir, "game*.log").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(log);
            var errors = new Dictionary<string, int>();
            bool gpuReset = false;
            var ansi = new Regex(@"\x1b\[[0-9;]*m");
            var gameError = new Regex(@"^\d\d:\d\d:\d\d\s+(ERROR|FATAL)\b");
            foreach (var raw in File.ReadLines(log))
            {
                var line = ansi.Replace(raw, "").Trim();
                if (line.StartsWith("[ksa-metal ERROR]", StringComparison.Ordinal) || gameError.IsMatch(line) ||
                    line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                {
                    gpuReset |= line.Contains("kIOGPUCommandBufferCallbackError", StringComparison.Ordinal);
                    var key = line.Length > 160 ? line[..160] + "..." : line;
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
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        foreach (var (key, value) in report["system"] as JsonObject ?? new JsonObject())
            md.AppendLine($"| {key} | {value} |");
        if (report["drawable"] is { } drawable)
            md.AppendLine($"| Drawn | {drawable} pixels |");
        md.AppendLine($"| Loading | {Num(report["loadSeconds"], "F1")} s |");
        int runs = report["runs"]?.GetValue<int>() ?? 1;
        if (runs > 1)
            md.AppendLine($"| Runs | {runs}: each number is the median of the runs, Frame ms shows their range |");
        md.AppendLine();
        md.AppendLine((string?)report["mode"] == "fast"
            ? "Fast mode: each part is the view standing still (the game paused) at the screenshot of that name."
            : "Each part is measured along the way, between two screenshots.");
        md.AppendLine("Frame: time between two frames (median, 95th percentile). GPU: time the GPU worked on a frame. Hitches: frames longer than twice the median. " +
                      "Memory: the game's footprint at its peak, and what KSA Metal allocated for the GPU. Shaders: compiled while measuring (each one a stutter).");
        md.AppendLine();
        md.AppendLine("| Section | Part | FPS | Frame ms | p95 ms | GPU ms | Hitches | Worst ms | Draws | Memory MB | GPU memory MB | Shaders |");
        md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var section in Sections(report).OfType<JsonObject>())
        {
            string name = (string?)section["name"] ?? "?";
            foreach (var w in (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
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
        return md.ToString();
    }

    private static string Num(JsonNode? node, string format) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    private static int Compare(string a, string b)
    {
        (a, b) = (Unpacked(a), Unpacked(b));
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
        Macs(ra, rb);
        Settings(Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, Directory.Exists(b) ? b : Path.GetDirectoryName(b)!);
        Numbers(ra, rb);
        return Screenshots(Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, Directory.Exists(b) ? b : Path.GetDirectoryName(b)!,
            modeA == "fast" ? FastShotLimit : CinematicShotLimit);
    }

    // A shared run's zip, unpacked next to it (once): the folder that holds its report.json
    private static string Unpacked(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        if (!Directory.Exists(dir))
            ZipFile.ExtractToDirectory(path, dir);
        var report = Directory.GetFiles(dir, "report.json", SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault();
        return report != null ? Path.GetDirectoryName(report)! : dir;
    }

    // What differs between the two runs' Macs and versions (from their system.txt)
    private static void Macs(JsonObject ra, JsonObject rb)
    {
        var (sa, sb) = (ra["system"] as JsonObject ?? new JsonObject(), rb["system"] as JsonObject ?? new JsonObject());
        var keys = sa.Select(kv => kv.Key).Union(sb.Select(kv => kv.Key))
            .Where(k => k is not ("Displays" or "Sections" or "Settings changed") && (string?)sa[k] != (string?)sb[k]).ToList();
        if ((string?)ra["drawable"] != (string?)rb["drawable"])
            keys.Add("Drawn");
        if (keys.Count == 0)
            return;
        Console.WriteLine("| | a | b |");
        Console.WriteLine("|---|---|---|");
        foreach (var k in keys)
            Console.WriteLine(k == "Drawn" ? $"| Drawn | {ra["drawable"]} | {rb["drawable"]} |" : $"| {k} | {sa[k]} | {sb[k]} |");
        Console.WriteLine();
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
                              $"{Change(wa["frameMsMedian"], wb["frameMsMedian"], wa["frameMsRange"], wb["frameMsRange"])} | {Num(wa["gpuMsMedian"], "F1")} | " +
                              $"{Num(wb["gpuMsMedian"], "F1")} | {Change(wa["gpuMsMedian"], wb["gpuMsMedian"], wa["gpuMsRange"], wb["gpuMsRange"])} |");
        }
        Console.WriteLine();
        bool repeated = (ra["runs"]?.GetValue<int>() ?? 1) > 1 && (rb["runs"]?.GetValue<int>() ?? 1) > 1;
        Console.WriteLine(repeated
            ? $"Both runs were repeated: changes are marked, faster (+) or slower (-), when the ranges of the repeats don't overlap and the change is over {SignificantRepeated:P0}."
            : $"Changes over {Significant:P0} are marked: faster (+) or slower (-). Smaller ones are within run-to-run noise.");
        Console.WriteLine();
    }

    // Each screenshot of a against b's of the same name; marked copies of b's that differ go to b/diff/
    private static int Screenshots(string a, string b, double shotLimit)
    {
        var shots = Directory.GetFiles(a, "*_*.png").Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (shots.Count == 0)
        {
            Console.WriteLine(Directory.GetFiles(a, "*_*.jpg").Length > 0
                ? "Screenshots not compared: a's are JPEG (a shared zip's), which only show by eye what changed."
                : "No screenshots in a to compare.");
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

    // KSA Metal's GPU profiler (gpuprof.csv): per measured part, the encoders that took the most GPU time, averaged over
    // the frames profiled. Rows are "frame,cb,encoder,type,draws,dispatches,start_us,vertex_us,fragment_us,total_us,
    // pipelines", a "cb" row per command buffer, and "<first frame>,label,<section/part>" where a part's frames begin.
    private static int Profile(string dir)
    {
        var csv = Path.Combine(dir, "gpuprof.csv");
        if (!File.Exists(csv))
        {
            Console.Error.WriteLine($"no GPU profile at {csv}");
            return 1;
        }
        var labels = new List<(ulong first, string label)>();
        var rows = new List<string[]>();
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            var f = line.Split(',');
            if (f.Length == 3 && f[1] == "label" && ulong.TryParse(f[0], out var first))
                labels.Add((first, f[2]));
            else if (f.Length == 11)
                rows.Add(f);
        }
        string? LabelOf(ulong frame) => labels.LastOrDefault(l => l.first <= frame).label;
        var md = new StringBuilder();
        md.AppendLine("## GPU profile");
        md.AppendLine();
        md.AppendLine("Per measured part, the encoders that took the most GPU time, averaged over the frames profiled. An encoder is named after the " +
                      "pipelines it ran: v/t/c and the hash of the vertex, tessellation or compute shader, +f and the fragment shader's " +
                      "(KSA_MTL_DUMP_SPV=<dir> writes the shaders by those hashes). Profiling costs GPU time itself: compare these numbers only " +
                      "with other profiles.");
        foreach (var part in rows.GroupBy(r => LabelOf(ulong.Parse(r[0]))).Where(g => g.Key != null))
        {
            var frames = part.Select(r => r[0]).Distinct().Count();
            double cbMs = part.Where(r => r[3] == "cb").Sum(r => double.Parse(r[9], CultureInfo.InvariantCulture)) / frames / 1000;
            var encoders = part.Where(r => r[3] != "cb" && double.Parse(r[9], CultureInfo.InvariantCulture) >= 0)
                .GroupBy(r => (type: r[3], pipelines: r[10]))
                .Select(g => (g.Key.type, g.Key.pipelines,
                    us: g.Sum(r => double.Parse(r[9], CultureInfo.InvariantCulture)) / frames,
                    draws: g.Sum(r => double.Parse(r[4], CultureInfo.InvariantCulture)) / frames,
                    dispatches: g.Sum(r => double.Parse(r[5], CultureInfo.InvariantCulture)) / frames))
                .OrderByDescending(e => e.us).ToList();
            double total = encoders.Sum(e => e.us);
            md.AppendLine();
            md.AppendLine($"### {part.Key}");
            md.AppendLine();
            md.AppendLine($"{frames} frames: command buffers {cbMs:F2} ms a frame, encoders {total / 1000:F2} ms (they overlap a little).");
            md.AppendLine();
            md.AppendLine("| Encoder | Type | µs a frame | Share | Draws | Dispatches |");
            md.AppendLine("|---|---|---:|---:|---:|---:|");
            foreach (var e in encoders.Take(15))
            {
                var names = e.pipelines.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string name = names.Length == 0 ? "(no pipeline)" : string.Join(" ", names.Take(2)) + (names.Length > 2 ? $" +{names.Length - 2}" : "");
                string type = e.type switch { "r" => "render", "c" => "compute", "b" => "blit", _ => e.type };
                md.AppendLine($"| {name} | {type} | {e.us:F0} | {e.us / total:P0} | {e.draws:F0} | {e.dispatches:F0} |");
            }
        }
        File.WriteAllText(Path.Combine(dir, "profile.md"), md.ToString());
        Console.WriteLine(Path.Combine(dir, "profile.md"));
        return 0;
    }

    // The rows of a table that xctrace exported. Its values are written once with an id and later as <x ref="id"/>: each
    // row comes back with those references replaced by what they stand for.
    private static IEnumerable<XElement> TraceRows(string file)
    {
        var known = new Dictionary<string, XElement>();
        XElement Resolve(XElement e)
        {
            if (e.Attribute("ref")?.Value is { } r && known.TryGetValue(r, out var target))
                return target;
            var copy = new XElement(e.Name, e.Attributes(), e.Nodes().Select(n => n is XElement c ? Resolve(c) : n));
            if (e.Attribute("id")?.Value is { } id)
                known[id] = copy;
            return copy;
        }
        using var reader = XmlReader.Create(file, new XmlReaderSettings { IgnoreWhitespace = true });
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.Name == "row")
                yield return Resolve((XElement)XNode.ReadFrom(reader));
            else
                reader.Read();
        }
    }

    // An Instruments trace of the run: per measured part (the signposts KSA Metal set for the mod), the GPU time of the
    // game's encoders by their names (KSA_MTL_LABELS) and by the GPU's channels, as Instruments measured it
    private static int Trace(string dir)
    {
        string partsFile = Path.Combine(dir, "trace-parts.xml"), gpuFile = Path.Combine(dir, "trace-gpu.xml");
        if (!File.Exists(partsFile) || !File.Exists(gpuFile))
        {
            Console.Error.WriteLine($"no exported trace tables in {dir}");
            return 1;
        }
        var parts = new List<(string name, long start, long end)>();
        foreach (var row in TraceRows(partsFile))
        {
            var cells = row.Elements().ToList();
            if (cells.Count < 12 || cells[5].Value != "io.github.ksa-macos-port")
                continue;
            long start = long.Parse(cells[0].Value), duration = long.Parse(cells[1].Value);
            parts.Add((cells[11].Attribute("fmt")?.Value ?? "?", start, start + duration));
        }
        var game = new Regex(@"^Command Buffer \d+:?");
        // part -> (label, channel) -> nanoseconds, and the GPU frames seen in each part
        var times = parts.ToDictionary(p => p, _ => new Dictionary<(string label, string channel), long>());
        var frames = parts.ToDictionary(p => p, _ => new HashSet<string>());
        var channels = new SortedSet<string>();
        int pid = -1;
        foreach (var row in TraceRows(gpuFile))
        {
            var cells = row.Elements().ToList();
            if (cells.Count < 11)
                continue;
            // The game's process: the one that labels its encoders like KSA Metal does
            var label = cells[6].Elements("string").FirstOrDefault()?.Value ?? "";
            int rowPid = int.TryParse(cells[10].Element("pid")?.Value, out var p) ? p : -2;
            if (pid < 0 && game.IsMatch(label) && label.Contains(':'))
                pid = rowPid;
            if (rowPid != pid)
                continue;
            long start = long.Parse(cells[0].Value), duration = long.Parse(cells[1].Value);
            var part = parts.FirstOrDefault(x => start >= x.start && start < x.end);
            if (part.name == null)
                continue;
            string channel = cells[2].Attribute("fmt")?.Value ?? "?";
            string name = game.Replace(label, "").Trim();
            channels.Add(channel);
            var key = (name.Length > 0 ? name : "(no name)", channel);
            times[part][key] = times[part].GetValueOrDefault(key) + duration;
            frames[part].Add(cells[3].Value);
        }
        var md = new StringBuilder();
        md.AppendLine("## Instruments trace");
        md.AppendLine();
        md.AppendLine("Per measured part, the GPU time of the game's encoders as Instruments measured it, averaged over the GPU frames of the part. " +
                      "Encoders are named after their pipelines (KSA_MTL_LABELS: the render pass's size, v/t/c and the vertex, tessellation or " +
                      "compute shader's hash, +f and the fragment shader's, +n for more pipelines). A render encoder's vertex and fragment work overlap.");
        foreach (var part in parts)
        {
            int n = Math.Max(frames[part].Count, 1);
            var byLabel = times[part].GroupBy(kv => kv.Key.label)
                .Select(g => (label: g.Key, total: g.Sum(kv => kv.Value), per: channels.ToDictionary(c => c, c => g.Where(kv => kv.Key.channel == c).Sum(kv => kv.Value))))
                .OrderByDescending(x => x.total).ToList();
            md.AppendLine();
            md.AppendLine($"### {part.name}");
            md.AppendLine();
            md.AppendLine($"{(part.end - part.start) / 1e9:F2} s, {frames[part].Count} GPU frames. " +
                          string.Join(", ", channels.Select(c => $"{c} {times[part].Where(kv => kv.Key.channel == c).Sum(kv => kv.Value) / 1e6 / n:F2} ms")) + " a frame.");
            md.AppendLine();
            md.AppendLine($"| Encoder | {string.Join(" | ", channels.Select(c => c + " ms"))} |");
            md.AppendLine($"|---|{string.Concat(channels.Select(_ => "---:|"))}");
            foreach (var x in byLabel.Take(15))
                md.AppendLine($"| {x.label} | {string.Join(" | ", channels.Select(c => (x.per[c] / 1e6 / n).ToString("F2", CultureInfo.InvariantCulture)))} |");
        }
        File.WriteAllText(Path.Combine(dir, "trace.md"), md.ToString());
        Console.WriteLine(Path.Combine(dir, "trace.md"));
        return 0;
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

    private static string Change(JsonNode? a, JsonNode? b, JsonNode? rangeA, JsonNode? rangeB)
    {
        if (a is not JsonValue va || b is not JsonValue vb || !va.TryGetValue<double>(out var x) || !vb.TryGetValue<double>(out var y) || x <= 0)
            return "";
        double change = (y - x) / x;
        string text = change.ToString("+0%;-0%;0%", CultureInfo.InvariantCulture);
        bool significant = rangeA is JsonArray ra && rangeB is JsonArray rb
            ? Math.Abs(change) >= SignificantRepeated &&
              ((double)rb[0]! > (double)ra[1]! || (double)rb[1]! < (double)ra[0]!)
            : Math.Abs(change) >= Significant;
        if (!significant)
            return text;
        // Less time is faster
        return change < 0 ? $"**{text} (+)**" : $"**{text} (-)**";
    }
}
