// The benchmark's pages, all from runs' records (Record.cs) and their metrics (Metrics.cs): report.md and report.html,
// what a run measured with its screenshots, and compare.html, two runs side by side (Comparison.cs), the verdict and the
// zones that moved first, the statistics folded. Each page is one file with its style and script inside, needing
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
        "here" => "Your Game",
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

    // The parts' table's columns, in the report and on its page
    private static readonly Metric[] Columns =
    [
        Metrics.Fps, Metrics.FrameTime, Metrics.CpuBusy, Metrics.GpuBusy, Metrics.FrameP99, Metrics.Low1, Metrics.Hitches, Metrics.Over100,
        Metrics.Latency, Metrics.Footprint,
    ];

    private static string Cell(PartRecord p, Metric m) => p[m] is { } v ? m.Format(v) : "";

    private static string MarkdownOf(RunRecord r)
    {
        var md = new StringBuilder();
        md.AppendLine($"## KSA Metal benchmark: {Title(r)}");
        md.AppendLine();
        var problems = r.Problems.ToList();
        if (r.Status == Benchmark.Status.Stopped && r.Notes.Any(f => f.Code == "trigger-stop"))
            md.AppendLine(r.AllParts().Any()
                ? "**Stopped** by a trigger. Below is what it measured until then."
                : "**Stopped** by a trigger, before it finished measuring a part.");
        else if (r.Status == Benchmark.Status.Stopped)
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
            headline.Add($"**{fps:F0} FPS** across the parts{(h.GpuMs is { } gpu ? $", GPU busy {gpu:F1} ms a frame" : "")}");
        // The slowest of several parts
        if (h.Slowest is { } slowest && r.AllParts().Skip(1).Any())
            headline.Add($"slowest {slowest.Scenario.Name} {slowest.Part.Name}, {slowest.Part[Metrics.Fps]:F0} FPS");
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
        var plain = r.Scenarios.Where(s => s.Parts.Any(p => !p.Warped)).ToList();
        if (plain.Count > 0)
        {
            md.AppendLine($"| Scenario | Part | {string.Join(" | ", Columns.Select(Heading))} |");
            md.AppendLine($"|---|---|{string.Concat(Columns.Select(_ => "---:|"))}");
            foreach (var s in plain)
            {
                string name = s.Name;
                foreach (var p in s.Parts.Where(p => !p.Warped))
                {
                    md.AppendLine($"| {name} | {p.Name} | {string.Join(" | ", Columns.Select(m => Cell(p, m) + (m == Metrics.FrameTime && p.Range(m) is [var lo, var hi] ? $" ({F(lo, "0.0")}-{F(hi, "0.0")})" : "")))} |");
                    name = "";
                }
            }
            md.AppendLine();
            if (plain.Any(s => s.Kind == Kind.Views) || r.Mode.Contains("jump"))
                md.AppendLine("A part of the Mixed Test, or of places jumped to, is the view standing still (the game paused) at the screenshot of that name.");
            if (r.Test == "here")
                md.AppendLine("Your Game is your own play as it happened: no two recordings play the same.");
            else if (plain.Any(s => s.Kind == Kind.Flight) && !r.Mode.Contains("jump"))
                md.AppendLine("A part of the Planetary Tour is measured along the way, between two screenshots.");
            md.AppendLine(Glossary);
        }
        var slow = r.Moments.Where(m => m.Part.Length > 0).Take(10).ToList();
        if (slow.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"Slow frames ({r.Moments.Count(m => m.Part.Length > 0)}), and what took their time:");
            md.AppendLine();
            md.AppendLine("| Scenario | Part | ms | What took the time |");
            md.AppendLine("|---|---|---:|---|");
            foreach (var m in slow)
                md.AppendLine($"| {m.Scenario} | {m.Part} | {F(m.FrameMs, "0")} | {m.Cause} |");
        }
        var profiled = r.AllParts().Where(x => x.Part.Profiled != null && !x.Part.Warped).ToList();
        if (profiled.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("The GPU's zones in the profiled pass, own ms a frame (zones run one after another there: compare them only with other profiles):");
            md.AppendLine();
            foreach (var (s, p) in profiled)
                md.AppendLine($"- {s.Name} {p.Name} (GPU busy {N(p.Profiled!.GetValueOrDefault(Metrics.GpuBusy.Name), "0.0")}): " +
                              string.Join(", ", p.Zones(Metrics.GpuZones, profiled: true).Take(6).Select(z => $"{Metrics.ZoneLabel(z.Name)} {F(z.Ms, "0.00")}")));
        }
        // The probes' numbers, part by part
        var probed = r.ProbeMetrics.Where(m => r.AllParts().Any(x => x.Part[m] != null)).ToList();
        if (probed.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"| Scenario | Part | {string.Join(" | ", probed.Select(m => m.Unit.Length > 0 ? $"{m.Label} {m.Unit}" : m.Label))} |");
            md.AppendLine($"|---|---|{string.Concat(probed.Select(_ => "---:|"))}");
            foreach (var (s, p) in r.AllParts())
                md.AppendLine($"| {s.Name} | {p.Name} | {string.Join(" | ", probed.Select(m => Cell(p, m)))} |");
        }
        // Who held the memory at each checkpoint
        if (r.Census.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Who held the memory at each checkpoint (MB): the game's, all tags, and its largest; KSA Metal's; the .NET heap:");
            md.AppendLine();
            foreach (var at in r.Census.GroupBy(c => (c.Scenario, c.Shot)))
            {
                var game = at.Where(c => c.Kind == "game").OrderByDescending(c => c.Mb).ToList();
                md.AppendLine($"- {at.Key.Scenario} {at.Key.Shot}: game {F(GameTotal(at), "0")} ({string.Join(", ", game.Take(3).Select(c => $"{c.Owner} {F(c.Mb, "0")}"))}), " +
                              $"KSA Metal {F(at.Where(c => c.Kind == "driver").Sum(c => c.Mb), "0")}, .NET heap {F(at.Where(c => c.Kind == "runtime").Sum(c => c.Mb), "0")}");
            }
        }
        var warp = r.AllParts().Where(x => x.Part.Warped).ToList();
        if (warp.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"Time warp: {h.WarpDays:F1} game days in the {warp.Sum(x => x.Part.Seconds):F0} s of its {warp.Count} parts.");
            md.AppendLine();
            md.AppendLine("Speed: game seconds per second, against what was asked (1 is real time). To 90 %: seconds until it ran at 90 % of what was asked. Physics: the slowest vehicle job. A speed far under what was asked means the simulation held it back.");
            md.AppendLine();
            md.AppendLine("| Scenario | Part | Game days | Speed | Asked | To 90 % s | GPU busy | Worst ms | Physics ms | Substeps | Vehicles | Bubbles |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var (s, p) in warp)
            {
                var speed = $"x{Cell(p, Metrics.Speed)}" + (p.Range(Metrics.Speed) is [var lo, var hi] ? $" ({lo:F0}-{hi:F0})" : "");
                md.AppendLine($"| {s.Name} | {p.Name} | {F(p[Metrics.GameDays] ?? 0, "0.##")} | {speed} | x{Cell(p, Metrics.SpeedAsked)} | {(p[Metrics.ToSpeed] is { } t ? F(t, "0.0") : "never")} | " +
                              $"{Cell(p, Metrics.GpuBusy)} | {Cell(p, Metrics.Worst)} | {Cell(p, Metrics.SimTick)} | {Cell(p, Metrics.Substeps)} | {Cell(p, Metrics.Vehicles)} | {Cell(p, Metrics.Bubbles)} |");
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
            md.AppendLine($"| Runs | {runs}: each number is the median of the runs (counts their mean, a worst state the worst), Frame time shows their range |");
        return md.ToString();
    }

    // What the parts' table's numbers are, PresentMon's names
    public const string Glossary =
        "Frame time: between two frames (median). CPU busy: a frame's work from its start to its present, its waits for a drawable or the GPU left out. GPU busy: the GPU's time on a frame. " +
        "P99: 99 % of frames took less. 1% low: the mean of the worst 1 %. Hitches: frames over 50 ms and twice the median before them, the stutters you see. " +
        "Latency: from a frame's start to its showing. Memory: the game's footprint at its peak, MB.";

    private static string Heading(Metric m) => m == Metrics.Footprint ? "Memory MB" : m.Unit == "ms" ? $"{m.Label} ms" : m.Label;

    // A number with its measurings' range: "12.3 (12.1-12.6)"
    private static string Range(PartRecord p, Metric m) =>
        (p[m] is { } v ? m.Format(v) : "") + (p.Range(m) is [var lo, var hi] ? $" ({m.Format(lo)}-{m.Format(hi)})" : "");

    // A verdict for a Markdown table: a change marked bold
    public static string Marked(Verdict v) => v.Changed ? $"**{Comparison.Text(v)}**" : Comparison.Text(v);

    // The zones that moved with a part: "Clouds Raymarching +1.20"
    public static string Zones(PartComparison c) =>
        string.Join(", ", c.Zones.Select(z => $"{z.Label} {F(z.Delta, "+0.00;-0.00")}"));

    // What a comparison's verdicts stand on, in a sentence
    public static string BasisNote(Basis basis) => basis switch
    {
        Basis.Sessions => $"Each side's sample is a game session's median. A change is marked when Mann-Whitney's p is under {F(Statistics.Alpha, "0.00")}, " +
                          $"Cliff's delta is past {F(Statistics.NegligibleDelta, "0.000")} and it moved {F(Statistics.SessionsMove * 100, "0")} % or more (GPU and frame time; the others past their limit, CPU busy also by 0.25 ms); " +
                          $"with fewer than {Statistics.SessionsNeeded} sessions a side it is inconclusive (?). The interval is the ratio's that goes with the test: " +
                          "5 sessions a side tell apart about 5 % on a quiet Mac, more sessions less.",
        Basis.QuickRead => $"A quick read of one game session a side: a change is marked when the ranges of its passes don't overlap and it is past the part's limit, {F(Statistics.QuickReadMove * 100, "0")} % at least. " +
                           "The two sessions played at other times, so the Mac's heat between them reads as a change too: sessions in turns decide (./benchmark.sh measure --ab ... --sessions 5, or a study).",
        _ => $"A screen of one run a side: a time or an energy is marked past {F(Statistics.ScreenDrift, "0")} % (or the part's limit, if larger), other numbers past their limit: " +
             "runs of the same build played at other times move that much with the Mac's heat and the apps sharing its GPU. " +
             "Sessions in turns tell smaller changes (./benchmark.sh measure --ab ... --sessions 5, or a study).",
    };

    // What no comparison judges: shown in each run's report
    public const string NotJudged = "Not judged, shown in each run's report: FPS, P95, the worst frame, CPU wait, displayed time, animation error, GC pause, JIT, " +
                                    "shaders, pipelines, draws, the thermal state, the physics' numbers and the GPU's power; P99 and 1% low only in parts of 200 frames or more.";

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
            Tile(html, F(gpu), "ms", "GPU busy a frame");
        if (h.Slowest is { } slowest && r.AllParts().Skip(1).Any())
            Tile(html, F(slowest.Part[Metrics.Fps] ?? 0, "0"), "FPS", $"slowest part: {slowest.Scenario.Name} {slowest.Part.Name}");
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
        SlowFrames(html, r);
        WhereTheTimeGoes(html, r);
        Census(html, r);
        AllNumbers(html, r);
        var timelines = Timelines(dir);
        WarpCharts(html, r, timelines);
        FrameCharts(html, r, timelines);
        Gallery(html, r, order, ext);
        FactsTable(html, r);
        html.Append("<footer>Every frame's numbers are in <code>run.db</code> (SQLite: <code>./benchmark.sh query</code>), the run's record in <code>run.json</code>, the same numbers as text in <code>report.md</code>, the game's log in <code>game.log</code>.</footer>");
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
        var parts = r.AllParts().Where(x => !x.Part.Warped).ToList();
        if (parts.Count == 0)
            return;
        double scale = Math.Max(parts.Max(x => Math.Max(x.Part[Metrics.FrameTime] ?? 0, x.Part[Metrics.GpuBusy] ?? 0)), 1);
        html.Append($"<section><h2>Part by part</h2><p class=\"lead\">{E(Glossary)}</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Frame time<i class=\"s2\"></i>GPU busy</div>");
        html.Append("<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th>");
        foreach (var m in Columns)
            html.Append($"<th title=\"{E(m.Help)}\">{E(Heading(m))}</th>");
        html.Append("</tr></thead><tbody>");
        string last = "";
        foreach (var (s, p) in parts)
        {
            if (s.Name != last && r.Scenarios.Count > 1)
                html.Append($"<tr class=\"group\"><th colspan=\"{Columns.Length + 2}\">{E(s.Name)}</th></tr>");
            last = s.Name;
            var shot = $"{s.Name}_{p.Name}";
            html.Append($"<tr><td class=\"part\">{(shots.Contains(shot) ? $"<a href=\"#{E(shot)}\">{Image(shot + ext, "")}</a>" : "<span class=\"no-thumb\"></span>")}{E(p.Name)}</td>");
            html.Append("<td class=\"bar-cell\">");
            if (p[Metrics.FrameTime] is { } f)
                html.Append($"<div class=\"bar s1\" style=\"width:{F(100 * f / scale, "0.0")}%\" title=\"Frame time {F(f)} ms\"></div>");
            if (p[Metrics.GpuBusy] is { } g)
                html.Append($"<div class=\"bar s2\" style=\"width:{F(100 * g / scale, "0.0")}%\" title=\"GPU busy {F(g)} ms\"></div>");
            html.Append("</td>");
            foreach (var m in Columns)
                html.Append(m == Metrics.FrameTime && p.Range(m) is [var lo, var hi]
                    ? $"<td>{Cell(p, m)} <small>{F(lo, "0.0")}-{F(hi, "0.0")}</small></td>"
                    : $"<td>{Cell(p, m)}</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    // The slow frames the triggers kept, and what took their time
    private static void SlowFrames(StringBuilder html, RunRecord r)
    {
        var slow = r.Moments.Where(m => m.Part.Length > 0).ToList();
        if (slow.Count == 0)
            return;
        html.Append($"<section><h2>Slow frames</h2><p class=\"lead\">{slow.Count} frame{(slow.Count > 1 ? "s" : "")} of 100 ms or more while measuring, with what took the time: the main thread's zones with the most time of their own, KSA Metal's waits, the GPU's time around it.</p>");
        html.Append("<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>ms</th><th class=\"cause\">What took the time</th></tr></thead><tbody>");
        foreach (var m in slow.Take(20))
            html.Append($"<tr><td class=\"part\">{E(m.Scenario)} {E(m.Part)}</td><td>{F(m.FrameMs, "0")}</td><td class=\"cause\">{E(m.Cause)}</td></tr>");
        html.Append("</tbody></table></div></section>");
    }

    // Each part's zones with the most own time a frame: the GPU's from a profiled pass, the main thread's and the job
    // runners' from the measured ones
    private static void WhereTheTimeGoes(StringBuilder html, RunRecord r)
    {
        var parts = r.AllParts().Where(x => !x.Part.Warped && (x.Part.Zones(Metrics.MainZones).Any() || x.Part.Profiled != null)).ToList();
        if (parts.Count == 0)
            return;
        html.Append("<section><h2>Where the time goes</h2><p class=\"lead\">The game's own profilers, a zone's own time a frame (its time less its zones'). The GPU's zones come from a profiled pass (./benchmark.sh profile), where zones run one after another: compare them only with other profiles.</p>");
        foreach (var (s, p) in parts)
        {
            html.Append($"<details><summary>{E(s.Name)} {E(p.Name)}</summary><div class=\"zones\">");
            void Group(string title, IEnumerable<(string Name, double Ms)> zones)
            {
                var list = zones.Take(8).ToList();
                if (list.Count == 0)
                    return;
                html.Append($"<table class=\"facts\"><thead><tr><th>{E(title)}</th><th>ms</th></tr></thead><tbody>");
                foreach (var (name, ms) in list)
                    html.Append($"<tr><td>{E(name[(name.IndexOf('/') + 1)..].Replace("zone/", ""))}</td><td>{F(ms, "0.00")}</td></tr>");
                html.Append("</tbody></table>");
            }
            if (p.Profiled != null)
                Group($"GPU (profiled, {N(p.Profiled.GetValueOrDefault(Metrics.GpuBusy.Name), "0.0")} ms)", p.Zones(Metrics.GpuZones, profiled: true));
            else
                Group("GPU", p.Zones(Metrics.GpuZones));
            Group("Main thread", p.Zones(Metrics.MainZones));
            Group("Job runners", p.Zones(Metrics.JobZones));
            html.Append("</div></details>");
        }
        html.Append("</section>");
    }

    // Every metric of every part, folded
    // Who held the memory at each checkpoint (the memory probe): the owners with the most, by kind
    private static void Census(StringBuilder html, RunRecord r)
    {
        if (r.Census.Count == 0)
            return;
        html.Append("<section><h2>Who holds the memory</h2><p class=\"lead\">At each checkpoint of the first pass: the game's memory by its tags, KSA Metal's by type, the .NET heap, the process's footprint.</p><div class=\"zones\">");
        foreach (var at in r.Census.GroupBy(c => (c.Scenario, c.Shot)))
        {
            var game = at.Where(c => c.Kind == "game").ToList();
            html.Append($"<table class=\"facts\"><thead><tr><th>{E(at.Key.Scenario)} {E(at.Key.Shot)}</th><th>MB</th></tr></thead><tbody>");
            html.Append($"<tr><th>Game, all tags</th><td>{F(GameTotal(at), "0")}</td></tr>");
            foreach (var c in game.OrderByDescending(c => c.Mb).Take(8))
                html.Append($"<tr><td>{E(c.Owner)}</td><td>{F(c.Mb, "0")}</td></tr>");
            foreach (var c in at.Where(c => c.Kind is "host" or "driver" or "runtime" or "process").OrderByDescending(c => c.Mb))
                html.Append($"<tr><th>{E(c.Kind switch { "host" => "Game, host-visible", "driver" => $"KSA Metal, {c.Owner}", "runtime" => ".NET heap", _ => "Footprint" })}</th><td>{F(c.Mb, "0")}</td></tr>");
            html.Append("</tbody></table>");
        }
        html.Append("</div>");
        // What other probes counted (the alloc probe's sites), each kind folded
        foreach (var kind in r.Census.Where(c => c.Kind is not ("game" or "game-total" or "host" or "driver" or "runtime" or "process")).GroupBy(c => c.Kind))
        {
            html.Append($"<details><summary>By {E(kind.Key)}</summary><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Checkpoint</th><th class=\"cause\">Owner</th><th>MB</th></tr></thead><tbody>");
            foreach (var c in kind)
                html.Append($"<tr><td class=\"part\">{E(c.Scenario)} {E(c.Shot)}</td><td class=\"cause\">{E(c.Owner)}</td><td>{F(c.Mb, "0")}</td></tr>");
            html.Append("</tbody></table></div></details>");
        }
        html.Append("</section>");
    }

    // The game's memory at a checkpoint, all its tags: the census's total, or its tags' sum
    private static double GameTotal(IEnumerable<CensusRecord> at) =>
        at.FirstOrDefault(c => c.Kind == "game-total")?.Mb ?? at.Where(c => c.Kind == "game").Sum(c => c.Mb);

    private static void AllNumbers(StringBuilder html, RunRecord r)
    {
        var parts = r.AllParts().ToList();
        // The built-in metrics, then the probes'
        var metrics = Metrics.All.Concat(r.ProbeMetrics).Where(m => parts.Any(x => x.Part[m] != null)).ToList();
        if (parts.Count == 0)
            return;
        html.Append("<section><details><summary>All numbers</summary><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th>");
        foreach (var m in metrics)
            html.Append($"<th title=\"{E(m.Name + ": " + m.Help)}\">{E(m.Label)}{(m.Unit.Length > 0 ? $" <small>{E(m.Unit)}</small>" : "")}</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var (s, p) in parts)
        {
            html.Append($"<tr><td class=\"part\">{E(s.Name)} {E(p.Name)}</td>");
            foreach (var m in metrics)
                html.Append($"<td>{Cell(p, m)}</td>");
            html.Append("</tr>");
        }
        html.Append("</tbody></table></div></details></section>");
    }

    // A comparison's parts: a bar for each side, the deciding numbers, the verdict and the zones that moved
    private static void ComparisonTable(StringBuilder html, Comparison c, string a, string b)
    {
        var parts = c.Parts.Where(p => p.Decides != null).ToList();
        if (parts.Count == 0)
            return;
        double scale = Math.Max(parts.Max(p => Math.Max(p.Decides!.A, p.Decides.B)), 1);
        html.Append($"<div class=\"key\"><i class=\"s0\"></i>{E(a)}<i class=\"s1\"></i>{E(b)}</div><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th><th></th><th>{E(a)}</th><th>{E(b)}</th><th>Change</th><th class=\"cause\">Zones that moved</th><th>Frame ms {E(a)}</th><th>Frame ms {E(b)}</th></tr></thead><tbody>");
        string last = "";
        foreach (var p in parts)
        {
            var v = p.Decides!;
            if (p.Scenario != last && c.Parts.Select(x => x.Scenario).Distinct().Count() > 1)
                html.Append($"<tr class=\"group\"><th colspan=\"9\">{E(p.Scenario)}</th></tr>");
            last = p.Scenario;
            html.Append($"<tr><td class=\"part\">{E(p.Name)}</td><td class=\"bar-cell\"><div class=\"bar s0\" style=\"width:{F(100 * v.A / scale, "0.0")}%\"></div><div class=\"bar s1\" style=\"width:{F(100 * v.B / scale, "0.0")}%\"></div></td>");
            html.Append($"<td class=\"quiet\">{E(v.Metric.Label)}</td><td>{v.Metric.Format(v.A)}</td><td>{v.Metric.Format(v.B)}</td><td>{Chip(v)}</td>");
            var zones = p.Zones.Count > 0 ? E(Zones(p)) : v.Metric == Metrics.GpuBusy && v.Changed && !p.GpuZones ? "<span class=\"quiet\">profile both to name them</span>" : "";
            html.Append($"<td class=\"cause\">{zones}</td><td>{Cell(p.A, Metrics.FrameTime)}</td><td>{Cell(p.B, Metrics.FrameTime)}</td></tr>");
        }
        html.Append("</tbody></table></div>");
    }

    // A verdict as a chip: better, worse, or quiet within the noise
    private static string Chip(Verdict v) => v.Change switch
    {
        Change.Better => Mark(true, Comparison.Text(v)),
        Change.Worse => Mark(false, Comparison.Text(v)),
        _ => $"<span class=\"quiet\">{E(Comparison.Text(v))}</span>",
    };

    // Every judged metric of every part with its test's numbers, folded
    private static void StatisticsTable(StringBuilder html, Comparison c)
    {
        var rows = c.Parts.Concat(c.Warps).SelectMany(p => p.Verdicts.Select(v => (p, v))).ToList();
        if (rows.Count == 0)
            return;
        html.Append("<details><summary>Statistics</summary><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>Metric</th><th>A</th><th>B</th><th>Change</th><th>On</th><th>p</th><th>Cliff's delta</th><th>B/A, 95 %</th><th>Limit</th></tr></thead><tbody>");
        foreach (var (p, v) in rows)
            html.Append($"<tr><td class=\"part\">{E(p.Scenario)} {E(p.Name)}</td><td>{E(v.Metric.Label)}</td><td>{v.Metric.Format(v.A)}</td><td>{v.Metric.Format(v.B)}</td><td>{Chip(v)}</td>" +
                        $"<td>{E(Statistics.Name(v.Basis))}{(v.Basis == Basis.Sessions ? $" {v.SessionsA}/{v.SessionsB}" : "")}</td><td>{N(v.P, "0.000")}</td><td>{N(v.Delta, "+0.00;-0.00;0")}</td>" +
                        $"<td>{(v.Interval is [var lo, var hi] ? $"{F(lo, "0.000")}-{F(hi, "0.000")}" : "")}</td><td>{F(v.Limit, "0.#")} %</td></tr>");
        html.Append("</tbody></table></div></details>");
    }

    // The run's frames by scenario (the first session and measured pass that played it, not the warm-up): for each frame
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
        var parts = r.AllParts().Where(x => x.Part.Warped).ToList();
        if (parts.Count == 0)
            return;
        double days = parts.Sum(x => x.Part[Metrics.GameDays] ?? 0);
        html.Append($"<section><h2>Time warp</h2><p class=\"lead\">The game got through <b>{F(days, days >= 100 ? "0" : "0.#")} days</b> in the {F(parts.Sum(x => x.Part.Seconds), "0")} s of the parts. Each chart shows how fast the game's clock ran, against the speed asked for.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Speed reached<i class=\"line\"></i>Speed asked</div><div class=\"multiples\">");
        foreach (var (s, p) in parts)
        {
            var samples = timelines.GetValueOrDefault(s.Name)?.Where(x => x.Part == p.Name).ToList() ?? new List<Sample>();
            html.Append($"<figure><figcaption><b>{E(p.Name)}</b><span>{Count(p[Metrics.GameDays] ?? 0, "day", "0.#")}</span></figcaption>");
            html.Append(SpeedChart(samples));
            string reached = p[Metrics.ToSpeed] is { } t ? $"reached it in {F(t, "0.0")} s" : "never reached the speed";
            html.Append($"<p class=\"small\">x{Compact(p[Metrics.Speed] ?? 0)} of x{Compact(p[Metrics.SpeedAsked] ?? 0)}, {reached}; GPU busy {N(p[Metrics.GpuBusy], "0.0")} ms a frame, worst frame {Compact(p[Metrics.Worst] ?? 0)} ms, {Count(p[Metrics.Vehicles] ?? 0, "vehicle")}, {Count(p[Metrics.Bubbles] ?? 0, "physics bubble")}</p></figure>");
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
        html.Append("<section><h2>Along the flight</h2><p class=\"lead\">Frame and GPU time as the camera goes, with the slowest frame of each moment. Spikes are stutters.</p>");
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
            string numbers = part is { Warped: false } && part[Metrics.Fps] is { } fps ? $"<span>{F(fps, "0")} FPS</span>" : "";
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

    public static void WriteCompare(string a, string b, RunRecord ra, RunRecord rb, Comparison c, IReadOnlyList<ShotResult> shots, string verdict,
        bool? good, IReadOnlyList<(string Key, string A, string B)> differences, IReadOnlyList<string> warnings, string shotsNote)
    {
        var html = new StringBuilder();
        var test = TestName(rb.Test);
        Open(html, $"{test}: {When(ra)} and {When(rb)}", rb.Drawable);
        html.Append("<header class=\"hero slim\"><div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark · comparison</div>");
        html.Append($"<h1>{E(test)}</h1><p><b>A</b> {E(When(ra))} &nbsp; <b>B</b> {E(When(rb))}</p>");
        html.Append(good is { } better ? Status(better, verdict) : $"<span class=\"status\">{E(verdict)}</span>");
        html.Append("</div></header><main>");
        if (warnings.Count > 0)
            html.Append("<section>").Append(string.Concat(warnings.Select(w => $"<p class=\"lead\">{E(w)}</p>"))).Append("</section>");

        // What moved first: each part that changed, with its zones, and its other numbers that moved
        var moved = c.Parts.Concat(c.Warps).Where(p => p.Decides is { Changed: true } || p.Others.Any()).ToList();
        html.Append("<section><h2>What moved</h2>");
        if (moved.Count == 0)
            html.Append($"<p class=\"lead\">No part moved past the noise. {E(BasisNote(c.Basis))}</p>");
        else
        {
            html.Append("<ul class=\"moved\">");
            foreach (var p in moved)
            {
                if (p.Decides is { Changed: true } d)
                    html.Append($"<li>{Chip(d)} {E(p.Line())}</li>");
                foreach (var v in p.Others)
                    html.Append($"<li>{Chip(v)} {E($"{p.Scenario} {p.Name} {v.Metric.Label} {v.Metric.Format(v.A)} to {v.Metric.Format(v.B)} {v.Metric.Unit}".TrimEnd())}</li>");
            }
            html.Append("</ul>");
            if (moved.Any(p => p.ZonesWanted))
                html.Append("<p class=\"small\">Profile both runs to name the GPU's zones that moved: <code>./benchmark.sh profile</code>.</p>");
            html.Append($"<p class=\"small\">{E(BasisNote(c.Basis))}</p>");
        }
        html.Append("</section>");

        if (differences.Count > 0)
        {
            html.Append("<section><h2>What differs between the runs</h2><div class=\"table-scroll\"><table class=\"facts three\"><thead><tr><th></th><th>A</th><th>B</th></tr></thead><tbody>");
            foreach (var (key, va, vb) in differences)
                html.Append($"<tr><th>{E(key)}</th><td>{E(va)}</td><td>{E(vb)}</td></tr>");
            html.Append("</tbody></table></div></section>");
        }
        else
            html.Append("<section><p class=\"lead\">Same Mac, same version, same settings.</p></section>");

        if (c.Parts.Count > 0)
        {
            html.Append("<section><h2>Part by part</h2><p class=\"lead\">GPU busy a frame where both runs have it (frame times sit on the display's beat). Small changes are within run-to-run noise and go unmarked.</p>");
            ComparisonTable(html, c, "A", "B");
            html.Append($"<p class=\"small\">{E(NotJudged)}</p></section>");
        }

        if (c.Warps.Count > 0)
        {
            html.Append("<section><h2>Time warp</h2><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>Days A</th><th>Days B</th><th>Speed A</th><th>Speed B</th><th>Change</th><th>GPU busy A</th><th>GPU busy B</th></tr></thead><tbody>");
            foreach (var p in c.Warps.Where(p => p.Decides != null))
                html.Append($"<tr><td class=\"part\">{E(p.Name)}</td><td>{F(p.A[Metrics.GameDays] ?? 0, "0.#")}</td><td>{F(p.B[Metrics.GameDays] ?? 0, "0.#")}</td><td>x{Compact(p.Decides!.A)}</td><td>x{Compact(p.Decides.B)}</td><td>{Chip(p.Decides)}</td><td>{Cell(p.A, Metrics.GpuBusy)}</td><td>{Cell(p.B, Metrics.GpuBusy)}</td></tr>");
            html.Append("</tbody></table></div></section>");
        }
        if (c.Parts.Count + c.Warps.Count > 0)
        {
            html.Append("<section>");
            StatisticsTable(html, c);
            html.Append("</section>");
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
                var label = s.State switch { "differs" => Mark(false, "differs"), "unreadable" => Mark(false, "unreadable"), "free" => "<span class=\"quiet\">not compared</span>", _ => "<span class=\"quiet\">same</span>" };
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

    // A study's page (report.html in its folder): each variant's verdict against the baseline, the parts as a matrix of
    // variants, what moved, the screenshots that differ, each variant's statistics, and the sessions with their runs
    public static void WriteStudy(string dir, StudyPage s)
    {
        var html = new StringBuilder();
        Open(html, $"Study {s.Name}", s.First?.Drawable);
        html.Append("<header class=\"hero slim\"><div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark · study</div>");
        html.Append($"<h1>{E(s.Name)}</h1><p>{E(s.First is { } r ? $"{When(r)} · {MacLine(r)} · " : "")}{s.Played} of {s.Sessions.Count} game sessions</p>");
        html.Append("</div></header><main>");

        html.Append($"<section><h2>Against {E(s.Baseline)}</h2><p class=\"lead\">Every session: <code>{E(s.Command)}</code>, {s.SessionsEach} for each variant, in turn. " +
                    $"The baseline: {E(s.Baseline)}{(s.BaselineOptions.Count > 0 ? $" (<code>{E(string.Join(' ', s.BaselineOptions))}</code>)" : "")}.</p><ul class=\"moved\">");
        foreach (var v in s.Variants)
            html.Append($"<li><b>{E(v.Name)}</b> {(v.Good is { } g ? Status(g, v.Sentence) : $"<span class=\"quiet\">{E(v.Sentence)}</span>")}" +
                        $"{(v.Options.Count > 0 ? $" <span class=\"small\"><code>{E(string.Join(' ', v.Options))}</code></span>" : "")}</li>");
        html.Append("</ul>").Append(string.Concat(s.Warnings.Select(w => $"<p class=\"lead\">{E(w)}</p>"))).Append("</section>");

        var rows = s.Rows();
        var shown = s.Variants.Where(v => v.Comparison != null).ToList();
        if (rows.Count > 0)
        {
            html.Append("<section><h2>Part by part</h2><p class=\"lead\">GPU busy a frame where both sides have it, else frame time; time warp's parts by their speed. Each cell: the variant against the baseline, the 95 % interval of the ratio below it.</p>");
            html.Append($"<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th></th><th>{E(s.Baseline)}</th>{string.Concat(shown.Select(v => $"<th>{E(v.Name)}</th>"))}</tr></thead><tbody>");
            string last = "";
            foreach (var (scenario, part) in rows)
            {
                if (scenario != last && rows.Select(x => x.Scenario).Distinct().Count() > 1)
                    html.Append($"<tr class=\"group\"><th colspan=\"{3 + shown.Count}\">{E(scenario)}</th></tr>");
                last = scenario;
                var first = shown.Select(v => s.Cell(v, scenario, part)).First(c => c != null)!.Decides!;
                html.Append($"<tr><td class=\"part\">{E(part)}</td><td class=\"quiet\">{E(first.Metric.Label)}</td><td>{first.Metric.Format(first.A)} {E(first.Metric.Unit)}</td>");
                foreach (var v in shown)
                    html.Append(s.Cell(v, scenario, part)?.Decides is { } d
                        ? $"<td>{(d.Metric != first.Metric ? $"<span class=\"quiet\">{E(d.Metric.Label)}</span> " : "")}{Chip(d)}{(d.Interval is [var lo, var hi] ? $"<div class=\"small\">{F(lo, "0.000")}-{F(hi, "0.000")}</div>" : "")}</td>"
                        : "<td></td>");
                html.Append("</tr>");
            }
            html.Append($"</tbody></table></div><p class=\"small\">{E(BasisNote(shown.Select(v => v.Comparison!.Basis).Min()))}</p></section>");
        }

        var moved = shown.SelectMany(v => v.Comparison!.Parts.Concat(v.Comparison.Warps).Where(p => p.Decides is { Changed: true } || p.Others.Any()).Select(p => (v, p))).ToList();
        if (moved.Count > 0)
        {
            html.Append("<section><h2>What moved</h2><ul class=\"moved\">");
            foreach (var (v, p) in moved)
            {
                if (p.Decides is { Changed: true } d)
                    html.Append($"<li>{Chip(d)} <b>{E(v.Name)}</b> {E(p.Line())}</li>");
                foreach (var o in p.Others)
                    html.Append($"<li>{Chip(o)} <b>{E(v.Name)}</b> {E($"{p.Scenario} {p.Name} {o.Metric.Label} {o.Metric.Format(o.A)} to {o.Metric.Format(o.B)} {o.Metric.Unit}".TrimEnd())}</li>");
            }
            html.Append("</ul></section>");
        }

        // The screenshots that differ from the baseline's, each variant's first session's
        var differing = shown.Where(v => v.ShotsDiffer > 0 && v.ShotsOf != null).ToList();
        if (differing.Count > 0)
        {
            var baseShots = s.Sessions.FirstOrDefault(x => x.Variant == s.Baseline && x.Run != null && Directory.GetFiles(Path.Combine(dir, x.Run), "*_*.png").Length > 0)?.Run;
            html.Append("<section><h2>Screenshots that differ</h2><p class=\"lead\">Each variant's first session against the baseline's. Drag across a picture to go from the baseline to the variant.</p><div class=\"gallery compare\">");
            foreach (var v in differing)
                foreach (var shot in v.Shots.Where(x => x.State is "differs" or "unreadable"))
                {
                    html.Append($"<figure class=\"slider\" style=\"--at:50%\"><div class=\"pair\">{(baseShots != null ? Image(Path.Combine(baseShots, shot.Name), "baseline: " + shot.Name) : "")}<div class=\"over\">{Image(Path.Combine(v.ShotsOf!, shot.Name), v.Name + ": " + shot.Name)}</div><i class=\"handle\"></i><span class=\"tag a\">{E(s.Baseline)}</span><span class=\"tag b\">{E(v.Name)}</span><input type=\"range\" min=\"0\" max=\"100\" value=\"50\" aria-label=\"From the baseline to the variant\"></div>");
                    html.Append($"<figcaption><b>{E(v.Name)} · {E(Path.GetFileNameWithoutExtension(shot.Name))}</b><span>{F(shot.ChangedPercent, "0.00")} % · {Mark(false, shot.State)}</span></figcaption></figure>");
                }
            html.Append("</div></section>");
        }

        if (shown.Count > 0)
        {
            html.Append("<section><h2>Statistics</h2><p class=\"lead\">A: the baseline, B: the variant.</p>");
            foreach (var v in shown)
            {
                html.Append($"<h3>{E(v.Name)}</h3>");
                StatisticsTable(html, v.Comparison!);
            }
            html.Append("</section>");
        }

        html.Append("<section><h2>Sessions</h2><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Session</th><th>Round</th><th>Variant</th><th>Run</th></tr></thead><tbody>");
        foreach (var x in s.Sessions)
            html.Append($"<tr><td>{x.Number}</td><td>{x.Round}</td><td class=\"part\">{E(x.Variant)}</td><td class=\"part\">{(x.Run != null ? $"<a href=\"{E(Url(x.Run))}/report.html\">{E(x.Run)}</a>" : $"<span class=\"quiet\">{E(x.State)}</span>")}</td></tr>");
        html.Append("</tbody></table></div></section></main>");
        Close(html);
        File.WriteAllText(Path.Combine(dir, "report.html"), html.ToString());
    }

    private static string Mark(bool better, string text) => $"<span class=\"chip {(better ? "good" : "bad")}\">{(better ? "▲" : "▼")} {E(text)}</span>";

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
        .bar-cell { width: 16%; min-width: 96px; } .bar { height: 6px; border-radius: 0 3px 3px 0; margin: 2px 0; }
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
        .parts td.cause, .parts th.cause { text-align: left; white-space: normal; min-width: 240px; } .zones { display: flex; flex-wrap: wrap; gap: 8px 32px; margin: 4px 0 12px; } .zones .facts { width: auto; min-width: 260px; } .zones .facts td:last-child { text-align: right; font-variant-numeric: tabular-nums; }
        .moved { margin: 6px 0 0; padding-left: 0; list-style: none; } .moved li { margin: 6px 0; } section details + details { border-top: 0; padding-top: 4px; }
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
