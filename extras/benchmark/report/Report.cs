// The benchmark's report and comparison, run by extras/benchmark/benchmark.sh.
//
//   report <dir>       <dir>/results.json (written by the mod) + <dir>/system.txt (written by the script, "key: value"
//                      lines) -> <dir>/report.json and <dir>/report.md (to paste into a GitHub issue)
//   compare <a> <b>    two report.json files (or their folders): per part of each section, b against a
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KsaMetalBenchmarkReport;

public static class Program
{
    // Changes smaller than this are within run-to-run noise (two runs on one Mac differ by 1% in most parts, 4% at most)
    private const double Significant = 0.05;

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
        var systemFile = Path.Combine(dir, "system.txt");
        if (File.Exists(systemFile))
        {
            foreach (var line in File.ReadAllLines(systemFile))
            {
                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                    system[line[..colon]] = line[(colon + 2)..];
            }
        }
        var resultsFile = Path.Combine(dir, "results.json");
        if (!File.Exists(resultsFile))
        {
            Console.Error.WriteLine($"no results at {resultsFile}");
            return 1;
        }
        var results = JsonNode.Parse(File.ReadAllText(resultsFile)) as JsonObject ?? new JsonObject();
        var report = new JsonObject
        {
            ["system"] = system, ["loadSeconds"] = results["loadSeconds"]?.DeepClone(), ["sections"] = results["sections"]?.DeepClone()
        };
        File.WriteAllText(Path.Combine(dir, "report.json"), report.ToJsonString(Indented));
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(report));
        Console.WriteLine(Path.Combine(dir, "report.md"));
        return 0;
    }

    private static JsonArray Sections(JsonObject report) => report["sections"] as JsonArray ?? new JsonArray();

    private static string Markdown(JsonObject report)
    {
        var md = new StringBuilder();
        md.AppendLine("## KSA Metal benchmark");
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        foreach (var (key, value) in report["system"] as JsonObject ?? new JsonObject())
            md.AppendLine($"| {key} | {value} |");
        md.AppendLine($"| Loading | {Num(report["loadSeconds"], "F1")} s |");
        md.AppendLine();
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
        var problems = Sections(report).OfType<JsonObject>()
            .SelectMany(s => (s["errors"] as JsonArray ?? new JsonArray()).Select(e => $"{s["name"]}: {e}")).ToList();
        if (problems.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Problems (parts listed here may not compare with other runs):");
            md.AppendLine();
            foreach (var p in problems)
                md.AppendLine($"- {p}");
        }
        return md.ToString();
    }

    private static string Num(JsonNode? node, string format) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    private static int Compare(string a, string b)
    {
        var (ra, rb) = (Load(a), Load(b));
        if (ra == null || rb == null)
            return 1;
        var parts = Windows(ra);
        var after = Windows(rb);
        Console.WriteLine($"a: {a}");
        Console.WriteLine($"b: {b}");
        Console.WriteLine();
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
