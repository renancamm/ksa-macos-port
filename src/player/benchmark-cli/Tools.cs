// The tools' summaries: the game's GPU zones (profile) and an Instruments trace (trace).
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
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    // The game's GPU zones of a profiled pass (benchmark.sh profile) from the run's store: per part, the zones with the
    // most own time a frame (their time less their zones'), against the GPU time the measured passes took
    private static int Profile(string dir)
    {
        var db = Path.Combine(dir, RunStore.GameFile);
        if (!File.Exists(db))
        {
            Console.Error.WriteLine($"no run's store at {db}");
            return 1;
        }
        using var store = Sqlite.Open(db, readOnly: true);
        var parts = new List<(string Scenario, string Part, long Frames, double GpuFrame, List<(string Zone, double Self, double Total, double Calls, double P95)> Zones)>();
        // The profiled passes, sessions together
        const string Profiled = "(session, pass) IN (SELECT session, pass FROM events WHERE type = 'pass' AND text = 'profiled')";
        using (var q = store.Prepare("SELECT scenario, part, zone, SUM(frames), SUM(calls), SUM(total_ms), SUM(self_ms), MAX(p95_ms), zone = 'GPU' " +
                                     $"FROM zone_stats WHERE grp = 'gpu' AND {Profiled} GROUP BY scenario, part, zone ORDER BY scenario, part, SUM(self_ms) DESC"))
        {
            while (q.Step())
            {
                var (scenario, part, zone) = (q.Text(0), q.Text(1), q.Text(2));
                if (parts.Count == 0 || parts[^1].Scenario != scenario || parts[^1].Part != part)
                    parts.Add((scenario, part, 0, 0, []));
                var p = parts[^1];
                if (q.Long(8) == 1)
                {
                    // The profiler's own frame: the GPU's whole frame
                    parts[^1] = p with { Frames = q.Long(3), GpuFrame = q.Double(5) / Math.Max(1, q.Long(3)) };
                    continue;
                }
                p.Zones.Add((zone, q.Double(6), q.Double(5), q.Long(4), q.Double(7)));
            }
        }
        if (parts.Count == 0)
        {
            Console.Error.WriteLine("no GPU zones of a profiled pass in the run's store (does the app's KSA Metal have gpu_zones?)");
            return 1;
        }
        // What the GPU measured for the same parts, the median of their frames
        var measured = new Dictionary<(string, string), double>();
        // Each frame's own GPU time where the store has it (frame_gpu), else the frames' column
        bool own = store.Prepare("SELECT name FROM sqlite_master WHERE name = 'frame_gpu'") is var t && t.Step();
        t.Dispose();
        using (var q = store.Prepare($"SELECT f.scenario, f.part, {(own ? "COALESCE(g.gpu_ms, f.gpu_ms)" : "f.gpu_ms")} FROM frames f " +
                                     (own ? "LEFT JOIN frame_gpu g ON g.session = f.session AND g.present = f.present " : "") +
                                     "WHERE f.part <> '' AND f.gpu_ms > 0 AND " +
                                     "(f.session, f.pass) IN (SELECT session, pass FROM events WHERE type = 'pass' AND text = 'measured')"))
        {
            var all = new Dictionary<(string, string), List<double>>();
            while (q.Step())
            {
                var key = (q.Text(0), q.Text(1));
                if (!all.TryGetValue(key, out var list))
                    all[key] = list = [];
                list.Add(q.Double(2));
            }
            foreach (var (key, list) in all)
            {
                list.Sort();
                measured[key] = list[list.Count / 2];
            }
        }
        var md = new StringBuilder();
        md.AppendLine("## GPU zones");
        md.AppendLine();
        md.AppendLine("Per part, the game's GPU zones in the profiled pass, as its GPU profiler names them, with KSA Metal splitting render passes " +
                      "at each zone so that a zone inside a pass has its own time. Own time is a zone's time less its zones'. Every zone boundary waits " +
                      "for all the work before it, so zones run one after the other, and the profiled frame takes longer than the measured pass's " +
                      "frame of the same session (about 13 % in the Mixed Test). A zone called many times a frame reads high by about 20 µs for each " +
                      "call (up to 70), and a zone at a render pass's edge carries the store and load of the pass's images: compare these numbers only " +
                      "with other profiles.");
        foreach (var (scenario, part, frames, gpuFrame, zones) in parts)
        {
            md.AppendLine();
            md.AppendLine($"### {scenario} {part}");
            md.AppendLine();
            md.AppendLine(FormattableString.Invariant($"{frames} frames: the profiled GPU frame {gpuFrame:F2} ms") +
                          (measured.TryGetValue((scenario, part), out var gpu) ? FormattableString.Invariant($", GPU time measured {gpu:F2} ms a frame.") : "."));
            md.AppendLine();
            md.AppendLine("| Zone | Own ms a frame | Total ms a frame | Calls a frame | p95 ms |");
            md.AppendLine("|---|---:|---:|---:|---:|");
            double n = Math.Max(1, frames);
            foreach (var z in zones.Take(20))
                md.AppendLine(FormattableString.Invariant($"| {z.Zone} | {z.Self / n:F2} | {z.Total / n:F2} | {z.Calls / n:F1} | {z.P95:F2} |"));
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
