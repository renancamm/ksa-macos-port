// The tools' summaries: KSA Metal's GPU profiler (profile) and an Instruments trace (trace).
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

public static partial class Program
{
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
            double cbMs = part.Where(r => r[3] == "cb").Sum(r => double.Parse(r[9])) / frames / 1000;
            var encoders = part.Where(r => r[3] != "cb" && double.Parse(r[9]) >= 0)
                .GroupBy(r => (type: r[3], pipelines: r[10]))
                .Select(g => (g.Key.type, g.Key.pipelines,
                    us: g.Sum(r => double.Parse(r[9])) / frames,
                    draws: g.Sum(r => double.Parse(r[4])) / frames,
                    dispatches: g.Sum(r => double.Parse(r[5])) / frames))
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
                md.AppendLine($"| {x.label} | {string.Join(" | ", channels.Select(c => (x.per[c] / 1e6 / n).ToString("F2")))} |");
        }
        File.WriteAllText(Path.Combine(dir, "trace.md"), md.ToString());
        Console.WriteLine(Path.Combine(dir, "trace.md"));
        return 0;
    }
}
