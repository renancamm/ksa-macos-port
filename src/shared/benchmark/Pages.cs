// The benchmark's pages, all from a run's record (Record.cs): report.md and report.html, what a run measured with its
// screenshots, and compare.html, two runs side by side. Each page is one file with its style and script inside, needing
// nothing from the network. Images are the run folder's screenshots by relative path: PNG in a run folder, JPEG in a
// shared zip, so an image that isn't there tries the other.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace KsaPort.Common.Benchmark;

// One screenshot of a comparison: what was found. State: same, differs, unreadable, or free (a view that may change from
// run to run as it likes, not compared)
public sealed record ShotResult(string Name, double ChangedPercent, double MeanChange, double Allowed, string State);

// One part of both runs of a comparison: its numbers in each, and whether it changed past the noise
public sealed record PartChange(string Scenario, PartRecord A, PartRecord B, bool Marked, double Change);

public static class Pages
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string F(double x, string format = "0.#") => x.ToString(format, CultureInfo.InvariantCulture);

    private static string N(double? x, string format) => x is { } v ? F(v, format) : "";

    public static string TestName(string? test) => test switch
    {
        "mixed" => "Mixed Test",
        "tour" => "Planetary Tour",
        "warp" => "Warp Test",
        "full" => "Full Run",
        _ => "Chosen Places"
    };

    // A test's name, with "places" when it jumped to places
    public static string Title(RunRecord r) => TestName(r.Test) + (r.Mode.Split(' ').Contains("jump") ? ", places" : "");

    // When a run was: "Oct 31, 2026, 05:45"
    public static string When(RunRecord r) => r.Started.ToString("MMM d, yyyy, HH:mm", CultureInfo.InvariantCulture);

    // The Mac and the version in one line
    private static string MacLine(RunRecord r)
    {
        var parts = new[] { r.Fact("Chip") ?? "", r.Fact("Memory") ?? "", r.Fact("macOS") is { Length: > 0 } m ? "macOS " + m.Split(' ')[0] : "", r.Fact("KSA") is { Length: > 0 } k ? "KSA " + k : "" };
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    // A relative path as a URL, each of its names encoded (a folder can be called anything)
    private static string Url(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Image(string src, string alt, string css = "")
    {
        var other = Url(Path.ChangeExtension(src, ".jpg"));
        return $"<img src=\"{E(Url(src))}\" alt=\"{E(alt)}\" loading=\"lazy\" {(css.Length > 0 ? $"class=\"{css}\" " : "")}" +
               $"data-other=\"{E(other)}\" onerror=\"this.onerror=null;this.src=this.dataset.other\">";
    }

    // --- report.md: the same as the page, as text (benchmark.sh prints it up to the facts)

    public const string FactsHeading = "### This Mac and the run";

    public static string Markdown(RunRecord r)
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return MarkdownOf(r);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    private static string MarkdownOf(RunRecord r)
    {
        var md = new StringBuilder();
        md.AppendLine($"## KSA Metal benchmark: {Title(r)}");
        md.AppendLine();
        var problems = r.Problems.ToList();
        if (r.Status == Benchmark.Status.Stopped)
            md.AppendLine(r.AllParts().Any()
                ? "**Stopped**: you stopped the run. Below is what it measured until then."
                : "**Stopped**: you stopped the run before it finished measuring a part.");
        else if (problems.Count == 0)
            md.AppendLine("**Passed**: the run played to its end without errors.");
        if (problems.Count > 0)
        {
            md.AppendLine($"**Failed**: {problems.Count} problem{(problems.Count > 1 ? "s" : "")} (the parts they concern may not compare with other runs):");
            md.AppendLine();
            foreach (var p in problems)
                md.AppendLine($"- {p.Text}");
        }
        var h = r.Headline();
        var headline = new List<string>();
        if (h.Fps is { } fps)
            headline.Add($"**{fps:F0} FPS** across the parts{(h.GpuMs is { } gpu ? $", GPU {gpu:F1} ms a frame" : "")}");
        if (h.Slowest is { } slowest)
            headline.Add($"slowest {slowest.Scenario.Name} {slowest.Part.Name}, {slowest.Part.AverageFps:F0} FPS");
        if (h.WarpDays > 0)
            headline.Add($"**{h.WarpDays:F1} game days** in time warp");
        if (h.MemoryMb > 0)
            headline.Add($"memory peak {h.MemoryMb / 1024:F1} GB");
        if (headline.Count > 0)
        {
            md.AppendLine();
            md.AppendLine(string.Join("; ", headline) + ".");
        }
        var notes = r.Notes.ToList();
        if (notes.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Conditions worth knowing:");
            md.AppendLine();
            foreach (var n in notes)
                md.AppendLine($"- {n.Text}");
        }
        md.AppendLine();
        var plain = r.Scenarios.Where(s => s.Parts.Count > 0).ToList();
        if (plain.Count > 0)
        {
            md.AppendLine("| Scenario | Part | FPS | Frame ms | p95 ms | GPU ms | Hitches | Worst ms | Draws | Memory MB | GPU memory MB | Shaders |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var s in plain)
            {
                string name = Label(s);
                foreach (var p in s.Parts)
                {
                    string frame = F(p.FrameMsMedian, "0.0");
                    if (p.FrameMsRange is [var lo, var hi])
                        frame += $" ({F(lo, "0.0")}-{F(hi, "0.0")})";
                    md.AppendLine($"| {name} | {p.Name} | {F(p.AverageFps, "0.0")} | {frame} | {F(p.FrameMsP95, "0.0")} | {N(p.GpuMsMedian, "0.0")} | {p.Hitches} | " +
                                  $"{F(p.FrameMsMax, "0")} | {N(p.DrawsMedian, "0")} | {p.FootprintPeakMb} | {p.GpuMemoryPeakMb} | {p.ShadersCompiled} |");
                    name = "";
                }
            }
            md.AppendLine();
            if (plain.Any(s => s.Kind == Kind.Views) || r.Mode.Contains("jump"))
                md.AppendLine("A part of the Mixed Test, or of places jumped to, is the view standing still (the game paused) at the screenshot of that name.");
            if (plain.Any(s => s.Kind == Kind.Flight) && !r.Mode.Contains("jump"))
                md.AppendLine("A part of the Planetary Tour is measured along the way, between two screenshots.");
            md.AppendLine("Frame: time between two frames (median, 95th percentile). GPU: time the GPU worked on a frame. Hitches: frames longer than twice the median. " +
                          "Memory: the game's footprint at its peak, and what KSA Metal allocated for the GPU. Shaders: compiled while measuring (each one a stutter).");
        }
        var ab = AB(r);
        if (ab.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"A/B: B played with {r.Fact("A/B") ?? "the setting changed"}, A with the run's own value of it, in sessions taken in turn.");
            md.AppendLine();
            md.AppendLine("| Scenario | Part | GPU ms A | GPU ms B | Change |");
            md.AppendLine("|---|---|---:|---:|---:|");
            foreach (var (scenario, a, b, moved, change) in ab)
            {
                string Range(PartRecord p) => (p.GpuMsMedian ?? p.FrameMsMedian).ToString("0.0", CultureInfo.InvariantCulture) +
                                              (p.GpuMsRange is [var lo, var hi] ? $" ({F(lo, "0.0")}-{F(hi, "0.0")})" : "");
                md.AppendLine($"| {scenario} | {a.Name} | {Range(a)} | {Range(b)} | " +
                              (moved ? $"**{Math.Abs(change) * 100:F0} % {(change < 0 ? "faster" : "slower")}**" : $"{change * 100:+0;-0;0} %") + " |");
            }
            md.AppendLine();
            md.AppendLine("A change is marked past the noise: with several passes or sessions, when the ranges of A's and B's measurings don't overlap and it is over 2 %; " +
                          "with one, past the part's limit (5 % without a noise profile).");
        }
        var warp = r.AllParts().Where(x => x.Part.Warp != null).ToList();
        if (warp.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"Time warp: {h.WarpDays:F1} game days in the {warp.Sum(x => x.Part.Seconds):F0} s of its {warp.Count} parts.");
            md.AppendLine();
            md.AppendLine("Speed: game seconds per second, against what was asked (1 is real time). To 90 %: seconds until it ran at 90 % of what was asked. Physics: the slowest vehicle job. A speed far under what was asked means the simulation held it back.");
            md.AppendLine();
            md.AppendLine("| Scenario | Part | Game days | Speed | Asked | To 90 % s | GPU ms | Worst ms | Physics ms | Substeps | Vehicles | Bubbles |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var (s, p) in warp)
            {
                var w = p.Warp!;
                var speed = $"x{w.Speed:F0}" + (w.SpeedRange is [var lo, var hi] ? $" ({lo:F0}-{hi:F0})" : "");
                md.AppendLine($"| {s.Name} | {p.Name} | {F(w.GameDays, "0.##")} | {speed} | x{w.SpeedAsked:F0} | {(w.SecondsToSpeed is { } t ? F(t, "0.0") : "never")} | " +
                              $"{N(p.GpuMsMedian, "0.0")} | {F(p.FrameMsMax, "0")} | {F(w.SimMsMax, "0.0")} | {w.Substeps} | {w.VehiclesMax} | {w.BubblesMax} |");
            }
        }
        md.AppendLine();
        md.AppendLine(FactsHeading);
        md.AppendLine();
        md.AppendLine("| | |");
        md.AppendLine("|---|---|");
        foreach (var f in r.Facts)
            md.AppendLine($"| {f.Key} | {f.Value} |");
        if (r.Drawable != null)
            md.AppendLine($"| Drawn | {r.Drawable} pixels |");
        if (r.LoadSeconds is > 0 and var load)
            md.AppendLine($"| Loading | {F(load, "0.0")} s |");
        if (r.AllParts().Select(x => x.Part.Runs).DefaultIfEmpty(1).Max() is > 1 and var runs)
            md.AppendLine($"| Runs | {runs}: each number is the median of the runs, Frame ms shows their range |");
        return md.ToString();
    }

    // An A/B run's parts, B's against A's: whether each moved past the noise, and by how much (GPU time, or frame time)
    public static List<(string Scenario, PartRecord A, PartRecord B, bool Moved, double Change)> AB(RunRecord r)
    {
        var list = new List<(string, PartRecord, PartRecord, bool, double)>();
        foreach (var a in r.Scenarios.Where(s => s.Variant == "A"))
        {
            if (r.Scenarios.FirstOrDefault(s => s.Name == a.Name && s.Variant == "B") is not { } b)
                continue;
            foreach (var pa in a.Parts.Where(p => p.Warp == null))
            {
                if (b.Part(pa.Name) is not { } pb)
                    continue;
                bool gpu = pa.GpuMsMedian != null && pb.GpuMsMedian != null;
                double x = gpu ? pa.GpuMsMedian!.Value : pa.FrameMsMedian, y = gpu ? pb.GpuMsMedian!.Value : pb.FrameMsMedian;
                list.Add((a.Name, pa, pb, Noise.Moved(x, y, gpu ? pa.GpuMsRange : pa.FrameMsRange, gpu ? pb.GpuMsRange : pb.FrameMsRange, pa.Limit),
                    x > 0 ? (y - x) / x : 0));
            }
        }
        return list;
    }

    // --- report.html

    public static void WriteReport(string dir, RunRecord r)
    {
        File.WriteAllText(Path.Combine(dir, "report.md"), Markdown(r));
        var ext = Directory.GetFiles(dir, "*_*.png").Length > 0 ? ".png" : ".jpg";
        var order = r.Scenarios.SelectMany(s => s.Shots).Where(s => File.Exists(Path.Combine(dir, s.File)) || File.Exists(Path.Combine(dir, Path.ChangeExtension(s.File, ".jpg")))).ToList();
        var h = r.Headline();
        var problems = r.Problems.ToList();
        var notes = r.Notes.ToList();

        var html = new StringBuilder();
        Open(html, $"{Title(r)}: {When(r)}", r.Drawable);
        html.Append("<header class=\"hero\">");
        if (Banner(dir, order, ext) is { } banner)
            html.Append(Image(banner, "", "hero-image"));
        html.Append("<div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark</div>");
        html.Append($"<h1>{E(TestName(r.Test))}{(r.Mode.Split(' ').Contains("jump") ? " <span>places</span>" : "")}</h1>");
        html.Append($"<p>{E(When(r))} · {E(MacLine(r))}</p>");
        html.Append(Status(r.Status == Benchmark.Status.Passed, r.Status.ToString()));
        html.Append("</div></header><main>");

        if (problems.Count > 0)
        {
            html.Append($"<section class=\"callout bad\"><h2>{problems.Count} problem{(problems.Count > 1 ? "s" : "")}</h2><p>The parts they concern may not compare with other runs.</p><ul>");
            foreach (var p in problems)
                html.Append($"<li>{E(p.Text)}</li>");
            html.Append("</ul></section>");
        }

        html.Append("<section class=\"tiles\">");
        if (h.Fps is { } fps)
            Tile(html, F(fps, "0"), "FPS", "across the measured parts");
        if (h.GpuMs is { } gpu)
            Tile(html, F(gpu), "ms", "GPU time a frame");
        if (h.Slowest is { } slowest)
            Tile(html, F(slowest.Part.AverageFps, "0"), "FPS", $"slowest part: {slowest.Scenario.Name} {slowest.Part.Name}");
        if (h.WarpDays > 0)
            Tile(html, F(h.WarpDays, h.WarpDays >= 100 ? "0" : "0.#"), "days", "game time in the Warp Test");
        if (h.MemoryMb > 0)
            Tile(html, F(h.MemoryMb / 1024), "GB", "memory at its peak");
        if (r.LoadSeconds is > 0 and var load)
            Tile(html, F(load, "0"), "s", "loading");
        html.Append("</section>");

        if (notes.Count > 0)
        {
            html.Append("<section class=\"callout\"><h2>Worth knowing</h2><ul>");
            foreach (var n in notes)
                html.Append($"<li>{E(n.Text)}</li>");
            html.Append("</ul></section>");
        }

        var shown = order.Select(s => Path.GetFileNameWithoutExtension(s.File)).ToHashSet();
        Parts(html, r, shown, ext);
        ABTable(html, r);
        var timelines = Timelines(dir);
        WarpCharts(html, r, timelines);
        FrameCharts(html, r, timelines);
        Gallery(html, r, order, ext);
        FactsTable(html, r);
        html.Append("<footer>Every frame's numbers are in <code>frames.csv</code>, the run's record in <code>run.json</code>, the same numbers as text in <code>report.md</code>, the game's log in <code>game.log</code>.</footer>");
        html.Append("</main>");
        Close(html);
        File.WriteAllText(Path.Combine(dir, "report.html"), html.ToString());
    }

    // The banner: the first screenshot of the run with some detail, by its file's size, not a view of black space
    private static string? Banner(string dir, List<ShotRecord> order, string ext)
    {
        var files = order.Select(s => Path.ChangeExtension(s.File, ext)).Where(f => File.Exists(Path.Combine(dir, f))).ToList();
        if (files.Count == 0)
            return null;
        var sizes = files.Select(f => new FileInfo(Path.Combine(dir, f)).Length).OrderBy(x => x).ToList();
        return files.First(f => new FileInfo(Path.Combine(dir, f)).Length >= sizes[sizes.Count / 2]);
    }

    private static void Tile(StringBuilder html, string value, string unit, string what) =>
        html.Append($"<div class=\"tile\"><div class=\"value\">{E(value)}<small>{E(unit)}</small></div><div class=\"what\">{E(what)}</div></div>");

    private static string Status(bool good, string text) =>
        $"<span class=\"status {(good ? "good" : "bad")}\"><svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"{(good ? "M3 8.5l3.2 3.2L13 4.8" : "M4 4l8 8M12 4l-8 8")}\"/></svg>{E(text)}</span>";

    // The parts as a table, a bar for each one's frame time on one scale
    private static void Parts(StringBuilder html, RunRecord r, HashSet<string> shots, string ext)
    {
        var parts = r.AllParts().Where(x => x.Part.Warp == null).ToList();
        if (parts.Count == 0)
            return;
        double scale = Math.Max(parts.Max(x => Math.Max(x.Part.FrameMsMedian, x.Part.GpuMsMedian ?? 0)), 1);
        html.Append("<section><h2>Part by part</h2><p class=\"lead\">Frame: the time between two frames. GPU: the time the GPU worked on one. A hitch is a frame longer than twice the usual one.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Frame ms<i class=\"s2\"></i>GPU ms</div>");
        html.Append("<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th><th>FPS</th><th>Frame ms</th><th>GPU ms</th><th>p95 ms</th><th>Worst ms</th><th>Hitches</th><th>Draws</th><th>Memory MB</th></tr></thead><tbody>");
        string last = "";
        foreach (var (s, p) in parts)
        {
            if (Label(s) != last && r.Scenarios.Count > 1)
                html.Append($"<tr class=\"group\"><th colspan=\"10\">{E(Label(s))}</th></tr>");
            last = Label(s);
            var shot = $"{s.Name}_{p.Name}";
            string frame = F(p.FrameMsMedian, "0.0");
            if (p.FrameMsRange is [var lo, var hi])
                frame += $" <small>{F(lo, "0.0")}-{F(hi, "0.0")}</small>";
            html.Append($"<tr><td class=\"part\">{(shots.Contains(shot) ? $"<a href=\"#{E(shot)}\">{Image(shot + ext, "")}</a>" : "<span class=\"no-thumb\"></span>")}{E(p.Name)}</td>");
            html.Append("<td class=\"bar-cell\">");
            html.Append($"<div class=\"bar s1\" style=\"width:{F(100 * p.FrameMsMedian / scale, "0.0")}%\" title=\"Frame {F(p.FrameMsMedian)} ms\"></div>");
            if (p.GpuMsMedian is { } g)
                html.Append($"<div class=\"bar s2\" style=\"width:{F(100 * g / scale, "0.0")}%\" title=\"GPU {F(g)} ms\"></div>");
            html.Append("</td>");
            html.Append($"<td>{F(p.AverageFps, "0.0")}</td><td>{frame}</td><td>{N(p.GpuMsMedian, "0.0")}</td><td>{F(p.FrameMsP95, "0.0")}</td><td>{F(p.FrameMsMax, "0")}</td><td>{p.Hitches}</td><td>{N(p.DrawsMedian, "0")}</td><td>{p.FootprintPeakMb}</td></tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    // A scenario's name, with its side in an A/B run
    private static string Label(ScenarioRecord s) => s.Variant is { } v ? $"{s.Name} ({v})" : s.Name;

    // An A/B run: each part's GPU time with the setting changed (B) and as the run had it (A), and whether it moved
    // past the noise
    private static void ABTable(StringBuilder html, RunRecord r)
    {
        var ab = AB(r);
        if (ab.Count == 0)
            return;
        html.Append($"<section><h2>A/B</h2><p class=\"lead\">B played with {E(r.Fact("A/B") ?? "the setting changed")}, A with the run's own value of it, in sessions taken in turn. A change is marked past the noise: ranges that don't overlap, or with one measuring each, the part's limit.</p>");
        html.Append("<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>A ms</th><th>B ms</th><th>Change</th></tr></thead><tbody>");
        foreach (var (scenario, a, b, moved, change) in ab)
        {
            html.Append($"<tr><td class=\"part\">{E(scenario)} {E(a.Name)}</td><td>{F(a.GpuMsMedian ?? a.FrameMsMedian, "0.0")}</td><td>{F(b.GpuMsMedian ?? b.FrameMsMedian, "0.0")}</td>");
            html.Append($"<td>{(moved ? Change(change < 0, $"{F(Math.Abs(change) * 100, "0")} % {(change < 0 ? "faster" : "slower")}") : $"<span class=\"quiet\">{F(change * 100, "+0;-0;0")} %</span>")}</td></tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    // frames.csv by scenario (the first session and measured pass that played it, not the warm-up): for each frame
    // the seconds since the scenario began, the part, frame and GPU ms, the game's clock and the speed asked for
    private sealed record Sample(double T, string Part, double FrameMs, double GpuMs, double Game, double Asked);

    private static Dictionary<string, List<Sample>> Timelines(string dir)
    {
        var all = new Dictionary<string, List<Sample>>();
        var starts = new Dictionary<string, double>();
        var first = new Dictionary<string, (int, int)>();
        foreach (var f in FrameLog.Read(dir).Where(f => f.Scenario.Length > 0 && f.Pass > 0))
        {
            if (!first.TryGetValue(f.Scenario, out var played))
                first[f.Scenario] = played = (f.Session, f.Pass);
            if (played != (f.Session, f.Pass))
                continue;
            if (!starts.TryGetValue(f.Scenario, out var start))
                starts[f.Scenario] = start = f.Time;
            if (!all.TryGetValue(f.Scenario, out var list))
                all[f.Scenario] = list = new List<Sample>();
            list.Add(new Sample(f.Time - start, f.Part, f.FrameMs, f.GpuMs ?? double.NaN, f.GameSeconds ?? double.NaN, f.Asked ?? double.NaN));
        }
        return all;
    }

    // Time warp's parts: the speed the game's clock ran at, second by second, on a log scale, one small chart a part
    private static void WarpCharts(StringBuilder html, RunRecord r, Dictionary<string, List<Sample>> timelines)
    {
        var parts = r.AllParts().Where(x => x.Part.Warp != null).ToList();
        if (parts.Count == 0)
            return;
        double days = parts.Sum(x => x.Part.Warp!.GameDays);
        html.Append($"<section><h2>Time warp</h2><p class=\"lead\">The game got through <b>{F(days, days >= 100 ? "0" : "0.#")} days</b> in the {F(parts.Sum(x => x.Part.Seconds), "0")} s of the parts. Each chart shows how fast the game's clock ran, against the speed asked for.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Speed reached<i class=\"line\"></i>Speed asked</div><div class=\"multiples\">");
        foreach (var (s, p) in parts)
        {
            var w = p.Warp!;
            var samples = timelines.GetValueOrDefault(s.Name)?.Where(x => x.Part == p.Name).ToList() ?? new List<Sample>();
            html.Append($"<figure><figcaption><b>{E(p.Name)}</b><span>{Count(w.GameDays, "day", "0.#")}</span></figcaption>");
            html.Append(SpeedChart(samples));
            string reached = w.SecondsToSpeed is { } t ? $"reached it in {F(t, "0.0")} s" : "never reached the speed";
            html.Append($"<p class=\"small\">x{Compact(w.Speed)} of x{Compact(w.SpeedAsked)}, {reached}; GPU {N(p.GpuMsMedian, "0.0")} ms a frame, worst frame {Compact(p.FrameMsMax)} ms, {Count(w.VehiclesMax, "vehicle")}, {Count(w.BubblesMax, "physics bubble")}</p></figure>");
        }
        html.Append("</div></section>");
    }

    private static string Count(double x, string what, string format = "0") => $"{F(x, format)} {what}{(F(x, format) == "1" ? "" : "s")}";

    private static string Compact(double x)
    {
        if (double.IsNaN(x))
            return "";
        return x >= 1e6 ? F(x / 1e6, "0.#") + "M" : x >= 1000 ? F(x / 1000, x >= 1e4 ? "0" : "0.#") + "K" : F(x, "0");
    }

    private static string SpeedChart(List<Sample> samples)
    {
        const int width = 480, height = 180, left = 44, right = 8, top = 10, bottom = 22;
        if (samples.Count < 2)
            return "<p class=\"small\">No frames for this part.</p>";
        double t0 = samples[0].T, t1 = samples[^1].T, asked = samples.Max(s => double.IsNaN(s.Asked) ? 0 : s.Asked);
        // The speed over each half second of the Mac's clock, the game's clock read between frames: a frame that took 40 s
        // shows as those 40 s at the crawl it was, not as one jump
        var clock = samples.Where(s => !double.IsNaN(s.Game)).ToList();
        if (clock.Count < 2)
            return "<p class=\"small\">No speed to show for this part.</p>";
        int index = 0;
        double GameAt(double t)
        {
            while (index < clock.Count - 2 && clock[index + 1].T < t)
                index++;
            var (p, q) = (clock[index], clock[index + 1]);
            return q.T > p.T ? p.Game + (q.Game - p.Game) * Math.Clamp((t - p.T) / (q.T - p.T), 0, 1) : q.Game;
        }
        var speed = new List<(double T, double V)>();
        double before = GameAt(t0);
        for (double at = t0 + 0.5; at <= t1; at += 0.5)
        {
            double now = GameAt(at);
            speed.Add((at - t0, Math.Max((now - before) / 0.5, 0)));
            before = now;
        }
        if (speed.Count < 2 || asked <= 0)
            return "<p class=\"small\">No speed to show for this part.</p>";
        double high = Math.Max(asked, speed.Max(p => p.V)) * 1.5, low = 1;
        double X(double t) => left + t / Math.Max(t1 - t0, 1e-6) * (width - left - right);
        double Y(double v) => top + (1 - Math.Log10(Math.Max(v, low) / low) / Math.Log10(high / low)) * (height - top - bottom);
        var svg = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Speed over time\" data-unit=\"x\" data-points=\"");
        svg.Append(string.Join(' ', speed.Select(p => $"{F(X(p.T), "0.0")},{F(Y(p.V), "0.0")},{F(p.T, "0.0")},{F(Math.Round(p.V), "0")}")));
        svg.Append("\">");
        for (double v = 10; v < high; v *= 10)
        {
            double y = Y(v);
            svg.Append($"<line class=\"grid\" x1=\"{left}\" x2=\"{width - right}\" y1=\"{F(y, "0.0")}\" y2=\"{F(y, "0.0")}\"/><text class=\"axis\" x=\"{left - 6}\" y=\"{F(y + 3, "0.0")}\" text-anchor=\"end\">x{Compact(v)}</text>");
        }
        svg.Append($"<line class=\"asked\" x1=\"{left}\" x2=\"{width - right}\" y1=\"{F(Y(asked), "0.0")}\" y2=\"{F(Y(asked), "0.0")}\"/>");
        svg.Append($"<text class=\"axis\" x=\"{left}\" y=\"{height - 6}\">0 s</text><text class=\"axis\" x=\"{width - right}\" y=\"{height - 6}\" text-anchor=\"end\">{F(t1 - t0, "0")} s</text>");
        svg.Append($"<path class=\"area\" d=\"M{F(X(speed[0].T), "0.0")},{height - bottom} {string.Join(' ', speed.Select(p => $"L{F(X(p.T), "0.0")},{F(Y(p.V), "0.0")}"))} L{F(X(speed[^1].T), "0.0")},{height - bottom} Z\"/>");
        svg.Append($"<path class=\"line s1\" d=\"{string.Join(' ', speed.Select((p, i) => $"{(i == 0 ? "M" : "L")}{F(X(p.T), "0.0")},{F(Y(p.V), "0.0")}"))}\"/>");
        svg.Append("<line class=\"cross\" y1=\"10\" y2=\"158\"/><circle class=\"dot s1\" r=\"4\"/></svg>");
        return svg.ToString();
    }

    // A flight: frame and GPU time along each scenario, in steps a chart can show, its parts marked along the top
    private static void FrameCharts(StringBuilder html, RunRecord r, Dictionary<string, List<Sample>> timelines)
    {
        var flights = r.Scenarios.Where(s => s.Kind == Kind.Flight && !r.Mode.Contains("jump") && timelines.ContainsKey(s.Name)).ToList();
        if (flights.Count == 0)
            return;
        html.Append("<section><h2>Along the flight</h2><p class=\"lead\">Frame and GPU time as the camera goes, with the slowest frame of each moment. Spikes are hitches.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Frame ms<i class=\"s2\"></i>GPU ms</div>");
        foreach (var s in flights)
            html.Append($"<figure class=\"wide\"><figcaption><b>{E(s.Name)}</b></figcaption>{FrameChart(timelines[s.Name])}</figure>");
        html.Append("</section>");
    }

    private static string FrameChart(List<Sample> samples)
    {
        const int width = 1000, height = 200, left = 44, right = 8, top = 22, bottom = 22;
        if (samples.Count < 2)
            return "";
        double t1 = samples[^1].T;
        int buckets = Math.Min(400, samples.Count);
        var frame = new double[buckets];
        var gpu = new List<double>[buckets];
        for (int i = 0; i < buckets; i++)
            gpu[i] = new List<double>();
        foreach (var s in samples)
        {
            int b = Math.Clamp((int)(s.T / Math.Max(t1, 1e-6) * buckets), 0, buckets - 1);
            frame[b] = Math.Max(frame[b], double.IsNaN(s.FrameMs) ? 0 : s.FrameMs);
            if (!double.IsNaN(s.GpuMs))
                gpu[b].Add(s.GpuMs);
        }
        var sorted = samples.Where(s => !double.IsNaN(s.FrameMs)).Select(s => s.FrameMs).OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
            return "";
        // The scale keeps the usual frames readable: the few past it run off the top
        double high = Math.Max(sorted[(int)(sorted.Length * 0.99)] * 1.25, 20);
        double X(int b) => left + (b + 0.5) / buckets * (width - left - right);
        double Y(double v) => top + (1 - Math.Min(v, high) / high) * (height - top - bottom);
        double T(int b) => (b + 0.5) / buckets * t1;
        var points = new List<string>();
        var framePath = new StringBuilder();
        var gpuPath = new StringBuilder();
        double lastFrame = 0;
        for (int b = 0; b < buckets; b++)
        {
            if (frame[b] > 0)
                lastFrame = frame[b];
            framePath.Append($"{(b == 0 ? "M" : "L")}{F(X(b), "0.0")},{F(Y(lastFrame), "0.0")} ");
            if (gpu[b].Count > 0)
            {
                var ordered = gpu[b].OrderBy(x => x).ToList();
                gpuPath.Append($"{(gpuPath.Length == 0 ? "M" : "L")}{F(X(b), "0.0")},{F(Y(ordered[ordered.Count / 2]), "0.0")} ");
            }
            points.Add($"{F(X(b), "0.0")},{F(Y(lastFrame), "0.0")},{F(T(b), "0.0")},{F(lastFrame, "0.0")}");
        }
        var svg = new StringBuilder($"<svg class=\"chart\" viewBox=\"0 0 {width} {height}\" role=\"img\" aria-label=\"Frame time over the section\" data-unit=\"ms\" data-points=\"{string.Join(' ', points)}\">");
        // The parts, along the top
        foreach (var part in samples.Where(s => s.Part.Length > 0).GroupBy(s => s.Part))
        {
            double a = left + part.First().T / t1 * (width - left - right), z = left + part.Last().T / t1 * (width - left - right);
            svg.Append($"<rect class=\"band\" x=\"{F(a, "0.0")}\" y=\"{top}\" width=\"{F(Math.Max(z - a - 2, 1), "0.0")}\" height=\"{height - top - bottom}\"/>");
            if (z - a > 36)
                svg.Append($"<text class=\"axis\" x=\"{F(a + 2, "0.0")}\" y=\"{top - 6}\">{E(part.Key)}</text>");
        }
        double step = high > 200 ? 100 : high > 80 ? 40 : high > 40 ? 20 : 10;
        for (double v = step; v < high; v += step)
        {
            double y = Y(v);
            svg.Append($"<line class=\"grid\" x1=\"{left}\" x2=\"{width - right}\" y1=\"{F(y, "0.0")}\" y2=\"{F(y, "0.0")}\"/><text class=\"axis\" x=\"{left - 6}\" y=\"{F(y + 3, "0.0")}\" text-anchor=\"end\">{F(v, "0")}</text>");
        }
        svg.Append($"<text class=\"axis\" x=\"{left}\" y=\"{height - 6}\">0 s</text><text class=\"axis\" x=\"{width - right}\" y=\"{height - 6}\" text-anchor=\"end\">{F(t1, "0")} s</text>");
        svg.Append($"<path class=\"line s1\" d=\"{framePath}\"/><path class=\"line s2\" d=\"{gpuPath}\"/>");
        svg.Append($"<line class=\"cross\" y1=\"{top}\" y2=\"{height - bottom}\"/><circle class=\"dot s1\" r=\"4\"/></svg>");
        return svg.ToString();
    }

    // The screenshots: click one to see it whole, the arrow keys go through them
    private static void Gallery(StringBuilder html, RunRecord r, List<ShotRecord> order, string ext)
    {
        if (order.Count == 0)
            return;
        html.Append("<section><h2>Screenshots</h2><div class=\"gallery\">");
        foreach (var shot in order)
        {
            var id = Path.GetFileNameWithoutExtension(shot.File);
            var part = r.Scenarios.FirstOrDefault(s => s.Shots.Contains(shot))?.Part(shot.Name);
            string numbers = part is { Warp: null } ? $"<span>{F(part.AverageFps, "0")} FPS</span>" : "";
            html.Append($"<figure id=\"{E(id)}\"><a href=\"{E(Url(id + ext))}\" class=\"shot\" data-caption=\"{E(shot.Name)}\">{Image(id + ext, shot.Name)}</a><figcaption><b>{E(shot.Name)}</b>{numbers}</figcaption></figure>");
        }
        html.Append("</div></section>");
    }

    // The run's facts, folded
    private static void FactsTable(StringBuilder html, RunRecord r)
    {
        html.Append("<section><details><summary>This Mac and the run</summary><table class=\"facts\"><tbody>");
        foreach (var f in r.Facts)
            html.Append($"<tr><th>{E(f.Key)}</th><td>{E(f.Value)}</td></tr>");
        if (r.Drawable != null)
            html.Append($"<tr><th>Drawn</th><td>{E(r.Drawable)} pixels</td></tr>");
        if (r.AllParts().Select(x => x.Part.Runs).DefaultIfEmpty(1).Max() is > 1 and var runs)
            html.Append($"<tr><th>Runs</th><td>{runs}: each number is the median of the runs</td></tr>");
        html.Append("</tbody></table></details></section>");
    }

    // --- compare.html

    public static void WriteCompare(string a, string b, RunRecord ra, RunRecord rb, IReadOnlyList<PartChange> parts, IReadOnlyList<PartChange> warp,
        IReadOnlyList<ShotResult> shots, string verdict, bool? good, IReadOnlyList<(string Key, string A, string B)> differences, string shotsNote)
    {
        var html = new StringBuilder();
        var test = TestName(rb.Test);
        Open(html, $"{test}: {When(ra)} and {When(rb)}", rb.Drawable);
        html.Append("<header class=\"hero slim\"><div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark · comparison</div>");
        html.Append($"<h1>{E(test)}</h1><p><b>A</b> {E(When(ra))} &nbsp; <b>B</b> {E(When(rb))}</p>");
        html.Append(good is { } better ? Status(better, verdict) : $"<span class=\"status\">{E(verdict)}</span>");
        html.Append("</div></header><main>");
        if (differences.Count > 0)
        {
            html.Append("<section><h2>What differs between the runs</h2><div class=\"table-scroll\"><table class=\"facts three\"><thead><tr><th></th><th>A</th><th>B</th></tr></thead><tbody>");
            foreach (var (key, va, vb) in differences)
                html.Append($"<tr><th>{E(key)}</th><td>{E(va)}</td><td>{E(vb)}</td></tr>");
            html.Append("</tbody></table></div></section>");
        }
        else
            html.Append("<section><p class=\"lead\">Same Mac, same version, same settings.</p></section>");

        // GPU time decides: frame times sit on the display's beat
        if (parts.Count > 0)
        {
            static double Measure(PartRecord p) => p.GpuMsMedian ?? p.FrameMsMedian;
            double scale = Math.Max(parts.Max(p => Math.Max(Measure(p.A), Measure(p.B))), 1);
            html.Append("<section><h2>Part by part</h2><p class=\"lead\">GPU time a frame. Small changes are within run-to-run noise and go unmarked.</p>");
            html.Append("<div class=\"key\"><i class=\"s0\"></i>A<i class=\"s1\"></i>B</div><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th><th>A ms</th><th>B ms</th><th>Change</th><th>Frame ms A</th><th>Frame ms B</th></tr></thead><tbody>");
            string last = "";
            foreach (var p in parts)
            {
                if (p.Scenario != last && ra.Scenarios.Count > 1)
                    html.Append($"<tr class=\"group\"><th colspan=\"7\">{E(p.Scenario)}</th></tr>");
                last = p.Scenario;
                double x = Measure(p.A), y = Measure(p.B);
                html.Append($"<tr><td class=\"part\">{E(p.A.Name)}</td><td class=\"bar-cell\"><div class=\"bar s0\" style=\"width:{F(100 * x / scale, "0.0")}%\"></div><div class=\"bar s1\" style=\"width:{F(100 * y / scale, "0.0")}%\"></div></td>");
                html.Append($"<td>{F(x, "0.0")}</td><td>{F(y, "0.0")}</td><td>{(p.Marked ? Change(p.Change < 0, $"{F(Math.Abs(p.Change) * 100, "0")} % {(p.Change < 0 ? "faster" : "slower")}") : $"<span class=\"quiet\">{F(p.Change * 100, "+0;-0;0")} %</span>")}</td>");
                html.Append($"<td>{F(p.A.FrameMsMedian, "0.0")}</td><td>{F(p.B.FrameMsMedian, "0.0")}</td></tr>");
            }
            html.Append("</tbody></table></div></section>");
        }

        if (warp.Count > 0)
        {
            html.Append("<section><h2>Time warp</h2><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>Days A</th><th>Days B</th><th>Speed A</th><th>Speed B</th><th>Change</th><th>GPU ms A</th><th>GPU ms B</th></tr></thead><tbody>");
            foreach (var p in warp)
            {
                var (wa, wb) = (p.A.Warp!, p.B.Warp!);
                html.Append($"<tr><td class=\"part\">{E(p.A.Name)}</td><td>{F(wa.GameDays, "0.#")}</td><td>{F(wb.GameDays, "0.#")}</td><td>x{Compact(wa.Speed)}</td><td>x{Compact(wb.Speed)}</td><td>{(p.Marked ? Change(p.Change > 0, $"{F(Math.Abs(p.Change) * 100, "0")} % {(p.Change > 0 ? "faster" : "slower")}") : $"<span class=\"quiet\">{F(p.Change * 100, "+0;-0;0")} %</span>")}</td><td>{N(p.A.GpuMsMedian, "0.0")}</td><td>{N(p.B.GpuMsMedian, "0.0")}</td></tr>");
            }
            html.Append("</tbody></table></div></section>");
        }

        if (shotsNote.Length > 0)
            html.Append($"<section><h2>Screenshots</h2><p class=\"lead\">{E(shotsNote)}</p></section>");
        if (shots.Count > 0)
        {
            var relative = Path.GetRelativePath(b, a);
            int differ = shots.Count(s => s.State is "differs" or "unreadable");
            html.Append($"<section><h2>Screenshots</h2><p class=\"lead\">{(differ == 0 ? "None differs." : $"{differ} of {shots.Count} differ.")} Drag across a picture to go from A to B. Changed: the share of its blocks whose colour moved.</p><div class=\"gallery compare\">");
            foreach (var s in shots.OrderByDescending(s => s.State is "differs" or "unreadable"))
            {
                var label = s.State switch { "differs" => Change(false, "differs"), "unreadable" => Change(false, "unreadable"), "free" => "<span class=\"quiet\">not compared</span>", _ => "<span class=\"quiet\">same</span>" };
                html.Append($"<figure class=\"slider\" style=\"--at:50%\"><div class=\"pair\">{Image(Path.Combine(relative, s.Name), "A: " + s.Name)}<div class=\"over\">{Image(s.Name, "B: " + s.Name)}</div><i class=\"handle\"></i><span class=\"tag a\">A</span><span class=\"tag b\">B</span><input type=\"range\" min=\"0\" max=\"100\" value=\"50\" aria-label=\"From A to B\"></div>");
                html.Append($"<figcaption><b>{E(Path.GetFileNameWithoutExtension(s.Name))}</b><span>{(s.State == "free" ? "" : F(s.ChangedPercent, "0.00") + " % · ")}{label}</span></figcaption>");
                if (s.State == "differs")
                    html.Append($"<a class=\"small\" href=\"diff/{E(Url(s.Name))}\">The changed blocks, in red</a>");
                html.Append("</figure>");
            }
            html.Append("</div></section>");
        }
        html.Append("</main>");
        Close(html);
        File.WriteAllText(Path.Combine(b, "compare.html"), html.ToString());
    }

    private static string Change(bool better, string text) => $"<span class=\"chip {(better ? "good" : "bad")}\">{(better ? "▲" : "▼")} {E(text)}</span>";

    // --- the page around it

    private static void Open(StringBuilder html, string title, string? drawable)
    {
        html.Append($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{E(title)}</title><style>");
        html.Append(Style);
        // The screenshots' shape: 3024x1964
        html.Append(drawable?.Split('x') is [var w, var h] && int.TryParse(w, out _) && int.TryParse(h, out _) ? $":root {{ --shape: {w}/{h}; }}" : "");
        html.Append("</style></head><body>");
    }

    private static void Close(StringBuilder html)
    {
        html.Append("<dialog id=\"viewer\"><img alt=\"\"><p></p></dialog><script>");
        html.Append(Script);
        html.Append("</script></body></html>");
    }

    private const string Style = """
        :root { color-scheme: light dark; --surface: #fcfcfb; --raised: #ffffff; --ink: #0b0b0b; --ink-2: #52514e; --ink-3: #7c7b75; --hair: #e4e3de;
          --s0: #a9a8a1; --s1: #2a78d6; --s2: #eb6834; --good: #0a7d0a; --good-wash: #e4f3e4; --bad: #c62a2a; --bad-wash: #fbe9e8; }
        @media (prefers-color-scheme: dark) { :root { --surface: #161615; --raised: #1f1f1e; --ink: #f0efec; --ink-2: #c3c2b7; --ink-3: #94938a; --hair: #33332f;
          --s0: #6d6c66; --s1: #3987e5; --s2: #d95926; --good: #4fc24f; --good-wash: #16301a; --bad: #ff7b78; --bad-wash: #3a1c1b; } }
        * { box-sizing: border-box; } html { scroll-behavior: smooth; }
        body { margin: 0; background: var(--surface); color: var(--ink); font: 15px/1.5 -apple-system, BlinkMacSystemFont, "SF Pro Text", "Helvetica Neue", sans-serif; -webkit-font-smoothing: antialiased; }
        .hero { position: relative; min-height: 320px; display: flex; align-items: flex-end; background: #0c0f14; color: #fff; overflow: hidden; }
        .hero.slim { min-height: 180px; } .hero-image { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
        .hero::after { content: ""; position: absolute; inset: 0; background: linear-gradient(180deg, rgba(8,10,14,.15) 0%, rgba(8,10,14,.82) 100%); }
        .hero-text { position: relative; z-index: 1; width: 100%; max-width: 1120px; margin: 0 auto; padding: 32px 24px 28px; }
        .eyebrow { font-size: 12px; letter-spacing: .09em; text-transform: uppercase; opacity: .8; }
        h1 { margin: 4px 0 2px; font-size: 38px; line-height: 1.15; font-weight: 650; letter-spacing: -.015em; } h1 span { font-weight: 400; opacity: .75; }
        .hero p { margin: 0 0 14px; opacity: .88; }
        .status { display: inline-flex; align-items: center; gap: 6px; padding: 4px 12px 4px 8px; border-radius: 999px; font-weight: 600; font-size: 14px; background: #fff; }
        .status svg { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 2.4; stroke-linecap: round; stroke-linejoin: round; }
        .status { color: #33332f; } .status.good { color: #0a7d0a; } .status.bad { color: #c62a2a; }
        main { max-width: 1120px; margin: 0 auto; padding: 8px 24px 48px; }
        section { margin-top: 36px; } h2 { margin: 0 0 6px; font-size: 20px; font-weight: 620; letter-spacing: -.005em; }
        .lead { margin: 0 0 12px; color: var(--ink-2); } .small { margin: 6px 0 0; color: var(--ink-2); font-size: 13px; }
        .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 12px; margin-top: 24px; }
        .tile { background: var(--raised); border: 1px solid var(--hair); border-radius: 10px; padding: 14px 16px; }
        .value { font-size: 32px; font-weight: 620; line-height: 1.1; letter-spacing: -.015em; font-variant-numeric: tabular-nums; } .value small { font-size: 14px; font-weight: 500; margin-left: 4px; color: var(--ink-2); letter-spacing: 0; }
        .what { margin-top: 4px; color: var(--ink-2); font-size: 13px; }
        .callout { background: var(--raised); border: 1px solid var(--hair); border-radius: 10px; padding: 14px 18px; margin-top: 24px; } .callout h2 { font-size: 15px; margin-bottom: 4px; } .callout ul { margin: 6px 0 0; padding-left: 20px; } .callout li { margin: 3px 0; color: var(--ink-2); overflow-wrap: anywhere; } .callout p { margin: 0; color: var(--ink-2); }
        .callout.bad { background: var(--bad-wash); border-color: transparent; } .callout.bad h2 { color: var(--bad); } .callout.bad li, .callout.bad p { color: var(--ink); }
        .key { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--ink-2); margin-bottom: 8px; } .key i { display: inline-block; width: 12px; height: 8px; border-radius: 2px; margin-left: 10px; } .key i:first-child { margin-left: 0; } .key i.line { height: 0; border-top: 2px dashed var(--ink-3); border-radius: 0; background: none; }
        i.s0, .bar.s0 { background: var(--s0); } i.s1, .bar.s1 { background: var(--s1); } i.s2, .bar.s2 { background: var(--s2); }
        .table-scroll { overflow-x: auto; }
        table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
        .parts th, .parts td { padding: 7px 10px; text-align: right; border-bottom: 1px solid var(--hair); white-space: nowrap; } .parts thead th { font-weight: 500; font-size: 12.5px; color: var(--ink-3); }
        .parts th:first-child, .parts td:first-child { text-align: left; padding-left: 0; } .parts small { color: var(--ink-3); }
        .parts .group th { padding-top: 16px; text-align: left; font-weight: 600; font-size: 13px; color: var(--ink-2); text-transform: capitalize; }
        .part { font-weight: 550; } .part img, .no-thumb { display: inline-block; width: 52px; height: 32px; object-fit: cover; border-radius: 4px; vertical-align: middle; margin-right: 10px; background: var(--hair); } .no-thumb { background: none; }
        .bar-cell { width: 28%; min-width: 140px; } .bar { height: 6px; border-radius: 0 3px 3px 0; margin: 2px 0; }
        .multiples { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 16px; }
        figure { margin: 0; } figure.wide { margin-top: 12px; } figcaption { display: flex; justify-content: space-between; gap: 8px; align-items: baseline; font-size: 14px; margin-bottom: 4px; } figcaption span { color: var(--ink-2); font-size: 13px; font-variant-numeric: tabular-nums; }
        .chart { display: block; width: 100%; height: auto; background: var(--raised); border: 1px solid var(--hair); border-radius: 8px; }
        .chart .grid { stroke: var(--hair); stroke-width: 1; } .chart .axis { fill: var(--ink-3); font-size: 11px; font-variant-numeric: tabular-nums; } .chart .asked { stroke: var(--ink-3); stroke-width: 1.5; stroke-dasharray: 5 4; } .chart .band { fill: var(--ink); opacity: .035; }
        .chart .line { fill: none; stroke-width: 2; stroke-linejoin: round; stroke-linecap: round; } .chart .line.s1 { stroke: var(--s1); } .chart .line.s2 { stroke: var(--s2); stroke-width: 1.5; } .chart .area { fill: var(--s1); opacity: .1; }
        .chart .cross { stroke: var(--ink-3); stroke-width: 1; opacity: 0; } .chart .dot { fill: var(--s1); stroke: var(--raised); stroke-width: 2; opacity: 0; } .chart text.tip { fill: var(--ink); font-size: 12px; font-weight: 600; }
        .gallery { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 18px; } .gallery figure { scroll-margin-top: 16px; } .gallery figcaption { margin: 6px 0 0; }
        .shot img { display: block; width: 100%; aspect-ratio: var(--shape, 3/2); object-fit: cover; border-radius: 8px; background: var(--hair); transition: transform .15s ease; } .shot:hover img { transform: scale(1.012); }
        .compare { grid-template-columns: repeat(auto-fill, minmax(460px, 1fr)); }
        .pair { position: relative; border-radius: 8px; overflow: hidden; background: var(--hair); aspect-ratio: var(--shape, 3/2); } .pair img { position: absolute; inset: 0; display: block; width: 100%; height: 100%; object-fit: cover; }
        .over { position: absolute; inset: 0; clip-path: inset(0 0 0 var(--at)); } .handle { position: absolute; top: 0; bottom: 0; left: var(--at); width: 2px; background: #fff; box-shadow: 0 0 6px rgba(0,0,0,.6); pointer-events: none; }
        .tag { position: absolute; top: 8px; font-size: 12px; font-weight: 600; color: #fff; background: rgba(0,0,0,.55); padding: 1px 8px; border-radius: 999px; pointer-events: none; } .tag.a { left: 8px; } .tag.b { right: 8px; }
        .pair input { position: absolute; inset: 0; width: 100%; height: 100%; margin: 0; opacity: 0; cursor: ew-resize; }
        .chip { font-weight: 600; font-size: 13px; padding: 1px 8px; border-radius: 999px; } .chip.good { color: var(--good); background: var(--good-wash); } .chip.bad { color: var(--bad); background: var(--bad-wash); } .quiet { color: var(--ink-3); }
        details { border-top: 1px solid var(--hair); padding-top: 10px; } summary { cursor: pointer; font-weight: 600; } .facts { margin-top: 8px; font-size: 13.5px; } .facts th, .facts td { text-align: left; padding: 5px 12px 5px 0; vertical-align: top; border-bottom: 1px solid var(--hair); overflow-wrap: anywhere; } .facts th { font-weight: 500; color: var(--ink-2); white-space: nowrap; }
        footer { margin-top: 36px; color: var(--ink-3); font-size: 13px; } code { font: 12.5px ui-monospace, Menlo, monospace; } a { color: inherit; }
        dialog { border: 0; padding: 0; background: transparent; max-width: 96vw; max-height: 96vh; color: #fff; } dialog::backdrop { background: rgba(0,0,0,.88); } dialog img { display: block; max-width: 96vw; max-height: 90vh; border-radius: 6px; } dialog p { margin: 8px 0 0; text-align: center; }
        @media (max-width: 640px) { h1 { font-size: 28px; } main { padding: 8px 16px 40px; } .hero-text { padding: 24px 16px; } .compare, .multiples { grid-template-columns: 1fr; } }
        """;

    private const string Script = """
        document.querySelectorAll('.chart').forEach(svg => {
          const points = (svg.dataset.points || '').split(' ').filter(Boolean).map(p => p.split(',').map(Number));
          const cross = svg.querySelector('.cross'), dot = svg.querySelector('.dot');
          if (!points.length || !cross) return;
          const tip = document.createElementNS('http://www.w3.org/2000/svg', 'text');
          tip.setAttribute('class', 'tip'); svg.appendChild(tip);
          const width = svg.viewBox.baseVal.width, unit = svg.dataset.unit;
          svg.addEventListener('mousemove', e => {
            const box = svg.getBoundingClientRect(), x = (e.clientX - box.left) / box.width * width;
            let near = points[0];
            for (const p of points) if (Math.abs(p[0] - x) < Math.abs(near[0] - x)) near = p;
            cross.setAttribute('x1', near[0]); cross.setAttribute('x2', near[0]); cross.style.opacity = 1;
            dot.setAttribute('cx', near[0]); dot.setAttribute('cy', near[1]); dot.style.opacity = 1;
            const value = unit === 'x' ? 'x' + Math.round(near[3]).toLocaleString('en') : near[3] + ' ms';
            tip.textContent = near[2] + ' s: ' + value;
            const right = near[0] > width * .7;
            tip.setAttribute('x', near[0] + (right ? -8 : 8)); tip.setAttribute('text-anchor', right ? 'end' : 'start'); tip.setAttribute('y', 16);
            tip.style.opacity = 1;
          });
          svg.addEventListener('mouseleave', () => { cross.style.opacity = 0; dot.style.opacity = 0; tip.style.opacity = 0; });
        });
        document.querySelectorAll('.pair input').forEach(input => input.addEventListener('input', () => input.closest('.slider').style.setProperty('--at', input.value + '%')));
        const viewer = document.getElementById('viewer'), shots = [...document.querySelectorAll('a.shot')];
        let at = -1;
        function show(i) {
          at = (i + shots.length) % shots.length;
          const img = viewer.querySelector('img'), shown = shots[at].querySelector('img');
          img.src = shown.currentSrc || shown.src;
          viewer.querySelector('p').textContent = shots[at].dataset.caption;
          if (!viewer.open) viewer.showModal();
        }
        shots.forEach((a, i) => a.addEventListener('click', e => { e.preventDefault(); show(i); }));
        viewer.addEventListener('click', () => viewer.close());
        document.addEventListener('keydown', e => { if (viewer.open && e.key === 'ArrowRight') show(at + 1); if (viewer.open && e.key === 'ArrowLeft') show(at - 1); });
        """;
}
