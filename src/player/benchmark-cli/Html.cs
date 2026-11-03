// The benchmark's pages: report.html, what a run measured with its screenshots, and compare.html, two runs side by side.
// Each is one file with its style and script inside, needing nothing from the network. Images are the run folder's
// screenshots by relative path: PNG in a run folder, JPEG in a shared zip, so an image that isn't there tries the other.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace KsaMetal.Benchmark.Cli;

// One screenshot of a comparison: what the program found. State: same, differs, unreadable, or free (a view that may
// change from run to run as it likes, not compared)
public sealed record ShotResult(string Name, double ChangedPercent, double MeanChange, double Allowed, string State);

public static class Html
{
    public static string TestName(string? test) => test switch
    {
        "mixed" => "Mixed Test",
        "tour" => "Planetary Tour",
        "warp" => "Warp Test",
        "full" => "Full Run",
        _ => "Chosen Places"
    };

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string N(JsonNode? node, string format) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    private static double D(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : double.NaN;

    private static string F(double x, string format = "0.#") => x.ToString(format, CultureInfo.InvariantCulture);

    private static IEnumerable<JsonObject> SectionsOf(JsonObject report) => (report["sections"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    private static IEnumerable<JsonObject> WindowsOf(JsonObject section) => (section["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>();

    // When a run was, from its folder's name: 2026-10-31_0545 is "Oct 31, 2026, 05:45"
    private static string When(string dir)
    {
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        return DateTime.TryParseExact(name.Length >= 15 ? name[..15] : name, "yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("MMM d, yyyy, HH:mm", CultureInfo.InvariantCulture)
            : name;
    }

    private static string System(JsonObject report, string key) => (string?)report["system"]?[key] ?? "";

    // The Mac and the version in one line
    private static string MacLine(JsonObject report)
    {
        var parts = new[] { System(report, "Chip"), System(report, "Memory"), System(report, "macOS") is { Length: > 0 } m ? "macOS " + m.Split(' ')[0] : "", System(report, "KSA") is { Length: > 0 } k ? "KSA " + k : "" };
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

    // Parts' shared numbers across a run: frames over seconds, so a long part counts for its length
    public static (double Fps, double GpuMs, JsonObject? Slowest, string SlowestSection, double MemoryMb) Headline(JsonObject report)
    {
        double frames = 0, seconds = 0, gpuWeighted = 0, gpuSeconds = 0, memory = 0, slowest = double.MaxValue;
        JsonObject? worst = null;
        string worstSection = "";
        foreach (var section in SectionsOf(report))
        {
            foreach (var w in WindowsOf(section))
            {
                memory = Math.Max(memory, D(w["footprintPeakMb"]) is var f && !double.IsNaN(f) ? f : 0);
                // Time warp's parts are about the speed they reach
                if (w["gameDays"] is not null)
                    continue;
                double s = D(w["seconds"]), fps = D(w["averageFps"]), gpu = D(w["gpuMsMedian"]);
                if (double.IsNaN(s) || s <= 0 || double.IsNaN(fps))
                    continue;
                frames += fps * s;
                seconds += s;
                if (!double.IsNaN(gpu))
                {
                    gpuWeighted += gpu * s;
                    gpuSeconds += s;
                }
                if (fps < slowest)
                    (slowest, worst, worstSection) = (fps, w, (string?)section["name"] ?? "");
            }
        }
        return (seconds > 0 ? frames / seconds : double.NaN, gpuSeconds > 0 ? gpuWeighted / gpuSeconds : double.NaN, worst, worstSection, memory);
    }

    public static double WarpDays(JsonObject report) =>
        SectionsOf(report).SelectMany(WindowsOf).Where(w => w["gameDays"] is not null).Sum(w => D(w["gameDays"]));

    // --- report.html

    public static void WriteReport(string dir, JsonObject report)
    {
        bool passed = report["passed"]?.GetValue<bool>() == true;
        var problems = (report["problems"] as JsonArray ?? new JsonArray()).Select(p => (string?)p ?? "").ToList();
        var notes = (report["notes"] as JsonArray ?? new JsonArray()).Select(p => (string?)p ?? "").ToList();
        var test = TestName(System(report, "Test") is { Length: > 0 } t ? t : null);
        var kinds = ((string?)report["mode"] ?? "").Split(' ');
        var mode = kinds.All(k => k is "fast" or "views" or "jump") ? "fast" : "cinematic";
        var modeWord = kinds.Contains("jump") ? "places" : "";
        var shots = Directory.GetFiles(dir, "*_*.*").Select(Path.GetFileName).OfType<string>()
            .Where(f => f.EndsWith(".png", StringComparison.Ordinal) || f.EndsWith(".jpg", StringComparison.Ordinal)).Select(f => Path.GetFileNameWithoutExtension(f)).Distinct().ToHashSet();
        var ext = Directory.GetFiles(dir, "*_*.png").Length > 0 ? ".png" : ".jpg";
        // The screenshots in the order played
        var order = new List<(string Section, string Shot)>();
        foreach (var section in SectionsOf(report))
        {
            var name = (string?)section["name"] ?? "";
            foreach (var shot in (section["screenshots"] as JsonArray ?? new JsonArray()).Select(s => (string?)s ?? ""))
                if (shots.Contains($"{name}_{shot}"))
                    order.Add((name, shot));
        }
        var (fps, gpu, slowest, slowestSection, memory) = Headline(report);
        double days = WarpDays(report);

        var html = new StringBuilder();
        Open(html, $"{test}: {When(dir)}", (string?)report["drawable"]);
        html.Append("<header class=\"hero\">");
        if (Banner(dir, order, ext) is { } banner)
            html.Append(Image(banner, "", "hero-image"));
        html.Append("<div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark</div>");
        html.Append($"<h1>{E(test)}{(modeWord.Length > 0 ? $" <span>{E(modeWord)}</span>" : "")}</h1>");
        html.Append($"<p>{E(When(dir))} · {E(MacLine(report))}</p>");
        bool stopped = report["stopped"]?.GetValue<bool>() == true;
        html.Append(Status(passed, stopped ? "Stopped" : passed ? "Passed" : "Failed"));
        html.Append("</div></header><main>");

        if (problems.Count > 0)
        {
            html.Append($"<section class=\"callout bad\"><h2>{problems.Count} problem{(problems.Count > 1 ? "s" : "")}</h2><p>The parts they concern may not compare with other runs.</p><ul>");
            foreach (var p in problems)
                html.Append($"<li>{E(p)}</li>");
            html.Append("</ul></section>");
        }

        html.Append("<section class=\"tiles\">");
        if (!double.IsNaN(fps))
            Tile(html, F(fps, "0"), "FPS", "across the measured parts");
        if (!double.IsNaN(gpu))
            Tile(html, F(gpu), "ms", "GPU time a frame");
        if (slowest != null)
            Tile(html, N(slowest["averageFps"], "0"), "FPS", $"slowest part: {PartName(slowestSection, (string?)slowest["name"])}");
        if (days > 0)
            Tile(html, F(days, days >= 100 ? "0" : "0.#"), "days", "game time in the Warp Test");
        if (memory > 0)
            Tile(html, F(memory / 1024), "GB", "memory at its peak");
        if (D(report["loadSeconds"]) is var load && !double.IsNaN(load) && load > 0)
            Tile(html, F(load, "0"), "s", "loading");
        html.Append("</section>");

        if (notes.Count > 0)
        {
            html.Append("<section class=\"callout\"><h2>Worth knowing</h2><ul>");
            foreach (var n in notes)
                html.Append($"<li>{E(n)}</li>");
            html.Append("</ul></section>");
        }

        Parts(html, report, shots, ext);
        var timelines = Timelines(dir);
        WarpCharts(html, report, timelines);
        FrameCharts(html, report, timelines, mode);
        Gallery(html, report, order, ext);
        Facts(html, report);
        html.Append($"<footer>Every frame's numbers are in <code>frames.csv</code>, the same numbers as text in <code>report.md</code>, the game's log in <code>game.log</code>.</footer>");
        html.Append("</main>");
        Close(html);
        File.WriteAllText(Path.Combine(dir, "report.html"), html.ToString());
    }

    // The banner: the first screenshot of the run with some detail, by its file's size, not a view of black space
    private static string? Banner(string dir, List<(string Section, string Shot)> order, string ext)
    {
        var files = order.Select(s => $"{s.Section}_{s.Shot}{ext}").Where(f => File.Exists(Path.Combine(dir, f))).ToList();
        if (files.Count == 0)
            return null;
        var sizes = files.Select(f => new FileInfo(Path.Combine(dir, f)).Length).OrderBy(x => x).ToList();
        return files.First(f => new FileInfo(Path.Combine(dir, f)).Length >= sizes[sizes.Count / 2]);
    }

    private static string PartName(string section, string? part) => part == section || part?.StartsWith(section, StringComparison.Ordinal) == true ? part ?? "" : $"{section} {part}";

    private static void Tile(StringBuilder html, string value, string unit, string what) =>
        html.Append($"<div class=\"tile\"><div class=\"value\">{E(value)}<small>{E(unit)}</small></div><div class=\"what\">{E(what)}</div></div>");

    private static string Status(bool good, string text) =>
        $"<span class=\"status {(good ? "good" : "bad")}\"><svg viewBox=\"0 0 16 16\" aria-hidden=\"true\"><path d=\"{(good ? "M3 8.5l3.2 3.2L13 4.8" : "M4 4l8 8M12 4l-8 8")}\"/></svg>{E(text)}</span>";

    // The parts as a table, a bar for each one's frame time on one scale
    private static void Parts(StringBuilder html, JsonObject report, HashSet<string> shots, string ext)
    {
        var parts = SectionsOf(report).SelectMany(s => WindowsOf(s).Where(w => w["gameDays"] is null).Select(w => (Section: (string?)s["name"] ?? "", Window: w))).ToList();
        if (parts.Count == 0)
            return;
        double scale = Math.Max(parts.Max(p => Math.Max(D(p.Window["frameMsMedian"]), D(p.Window["gpuMsMedian"]) is var g && !double.IsNaN(g) ? g : 0)), 1);
        html.Append("<section><h2>Part by part</h2><p class=\"lead\">Frame: the time between two frames. GPU: the time the GPU worked on one. A hitch is a frame longer than twice the usual one.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Frame ms<i class=\"s2\"></i>GPU ms</div>");
        html.Append("<div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th><th>FPS</th><th>Frame ms</th><th>GPU ms</th><th>p95 ms</th><th>Worst ms</th><th>Hitches</th><th>Draws</th><th>Memory MB</th></tr></thead><tbody>");
        string last = "";
        foreach (var (section, w) in parts)
        {
            if (section != last && SectionsOf(report).Count() > 1)
                html.Append($"<tr class=\"group\"><th colspan=\"10\">{E(section)}</th></tr>");
            last = section;
            var name = (string?)w["name"] ?? "";
            var shot = $"{section}_{name}";
            string frame = N(w["frameMsMedian"], "0.0");
            if (w["frameMsRange"] is JsonArray range)
                frame += $" <small>{N(range[0], "0.0")}-{N(range[1], "0.0")}</small>";
            double a = D(w["frameMsMedian"]), b = D(w["gpuMsMedian"]);
            html.Append($"<tr><td class=\"part\">{(shots.Contains(shot) ? $"<a href=\"#{E(shot)}\">{Image(shot + ext, "")}</a>" : "<span class=\"no-thumb\"></span>")}{E(name)}</td>");
            html.Append("<td class=\"bar-cell\">");
            html.Append($"<div class=\"bar s1\" style=\"width:{F(100 * a / scale, "0.0")}%\" title=\"Frame {F(a)} ms\"></div>");
            if (!double.IsNaN(b))
                html.Append($"<div class=\"bar s2\" style=\"width:{F(100 * b / scale, "0.0")}%\" title=\"GPU {F(b)} ms\"></div>");
            html.Append("</td>");
            html.Append($"<td>{N(w["averageFps"], "0.0")}</td><td>{frame}</td><td>{N(w["gpuMsMedian"], "0.0")}</td><td>{N(w["frameMsP95"], "0.0")}</td><td>{N(w["frameMsMax"], "0")}</td><td>{N(w["hitches"], "0")}</td><td>{N(w["drawsMedian"], "0")}</td><td>{N(w["footprintPeakMb"], "0")}</td></tr>");
        }
        html.Append("</tbody></table></div></section>");
    }

    // frames.csv by section: for each frame the seconds since the section began, the part, frame and GPU ms, the game's clock
    // and the speed asked for
    private sealed record Sample(double T, string Part, double FrameMs, double GpuMs, double Game, double Asked);

    private static Dictionary<string, List<Sample>> Timelines(string dir)
    {
        var all = new Dictionary<string, List<Sample>>();
        var file = Path.Combine(dir, "frames.csv");
        if (!File.Exists(file))
            return all;
        int[]? c = null;
        var starts = new Dictionary<string, double>();
        foreach (var line in File.ReadLines(file))
        {
            var f = line.Split(',');
            if (c == null)
            {
                c = new[] { "time", "section", "part", "frame_ms", "gpu_ms", "game_s", "asked" }.Select(n => Array.IndexOf(f, n)).ToArray();
                if (c.Any(i => i < 0))
                    return all;
                continue;
            }
            if (f.Length <= c.Max() || f[c[1]].Length == 0)
                continue;
            static double Parse(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
            double t = Parse(f[c[0]]);
            if (!starts.TryGetValue(f[c[1]], out var start))
                starts[f[c[1]]] = start = t;
            if (!all.TryGetValue(f[c[1]], out var list))
                all[f[c[1]]] = list = new List<Sample>();
            list.Add(new Sample(t - start, f[c[2]], Parse(f[c[3]]), Parse(f[c[4]]), Parse(f[c[5]]), Parse(f[c[6]])));
        }
        return all;
    }

    // Time warp's parts: the speed the game's clock ran at, second by second, on a log scale, one small chart a part
    private static void WarpCharts(StringBuilder html, JsonObject report, Dictionary<string, List<Sample>> timelines)
    {
        var parts = SectionsOf(report).SelectMany(s => WindowsOf(s).Where(w => w["gameDays"] is not null).Select(w => (Section: (string?)s["name"] ?? "", Window: w))).ToList();
        if (parts.Count == 0)
            return;
        double days = parts.Sum(p => D(p.Window["gameDays"]));
        html.Append($"<section><h2>Time warp</h2><p class=\"lead\">The game got through <b>{F(days, days >= 100 ? "0" : "0.#")} days</b> in the {F(parts.Sum(p => D(p.Window["seconds"])), "0")} s of the parts. Each chart shows how fast the game's clock ran, against the speed asked for.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Speed reached<i class=\"line\"></i>Speed asked</div><div class=\"multiples\">");
        foreach (var (section, w) in parts)
        {
            var name = (string?)w["name"] ?? "";
            var samples = timelines.GetValueOrDefault(section)?.Where(s => s.Part == name).ToList() ?? new List<Sample>();
            html.Append($"<figure><figcaption><b>{E(name)}</b><span>{Count(D(w["gameDays"]), "day", "0.#")}</span></figcaption>");
            html.Append(SpeedChart(samples));
            string reached = w["secondsToSpeed"] is null ? "never reached the speed" : $"reached it in {N(w["secondsToSpeed"], "0.0")} s";
            html.Append($"<p class=\"small\">x{Compact(D(w["speed"]))} of x{Compact(D(w["speedAsked"]))}, {reached}; GPU {N(w["gpuMsMedian"], "0.0")} ms a frame, worst frame {Compact(D(w["frameMsMax"]))} ms, {Count(D(w["vehiclesMax"]), "vehicle")}, {Count(D(w["bubblesMax"]), "physics bubble")}</p></figure>");
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

    // A cinematic run: frame and GPU time along each section, in steps a chart can show, its parts marked along the top
    private static void FrameCharts(StringBuilder html, JsonObject report, Dictionary<string, List<Sample>> timelines, string mode)
    {
        if (mode == "fast")
            return;
        var sections = SectionsOf(report).Where(s => s["live"]?.GetValue<bool>() != true && timelines.ContainsKey((string?)s["name"] ?? "")).ToList();
        if (sections.Count == 0)
            return;
        html.Append("<section><h2>Along the flight</h2><p class=\"lead\">Frame and GPU time as the camera goes, with the slowest frame of each moment. Spikes are hitches.</p>");
        html.Append("<div class=\"key\"><i class=\"s1\"></i>Frame ms<i class=\"s2\"></i>GPU ms</div>");
        foreach (var section in sections)
        {
            var name = (string?)section["name"] ?? "";
            html.Append($"<figure class=\"wide\"><figcaption><b>{E(name)}</b></figcaption>{FrameChart(timelines[name])}</figure>");
        }
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
    private static void Gallery(StringBuilder html, JsonObject report, List<(string Section, string Shot)> order, string ext)
    {
        if (order.Count == 0)
            return;
        var windows = SectionsOf(report).SelectMany(s => WindowsOf(s).Select(w => ($"{s["name"]}_{w["name"]}", w))).GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().w);
        html.Append($"<section><h2>Screenshots</h2><div class=\"gallery\">");
        foreach (var (section, shot) in order)
        {
            var id = $"{section}_{shot}";
            windows.TryGetValue(id, out var w);
            string numbers = w is { } && w["gameDays"] is null && N(w["averageFps"], "0") is { Length: > 0 } f ? $"<span>{f} FPS</span>" : "";
            html.Append($"<figure id=\"{E(id)}\"><a href=\"{E(Url(id + ext))}\" class=\"shot\" data-caption=\"{E(shot)}\">{Image(id + ext, shot)}</a><figcaption><b>{E(shot)}</b>{numbers}</figcaption></figure>");
        }
        html.Append("</div></section>");
    }

    // The run's facts, folded
    private static void Facts(StringBuilder html, JsonObject report)
    {
        html.Append("<section><details><summary>This Mac and the run</summary><table class=\"facts\"><tbody>");
        foreach (var (key, value) in report["system"] as JsonObject ?? new JsonObject())
            html.Append($"<tr><th>{E(key)}</th><td>{E(value?.ToString())}</td></tr>");
        if (report["drawable"] is { } drawable)
            html.Append($"<tr><th>Drawn</th><td>{E(drawable.ToString())} pixels</td></tr>");
        if (D(report["runs"]) is > 1 and var runs)
            html.Append($"<tr><th>Runs</th><td>{F(runs, "0")}: each number is the median of the runs</td></tr>");
        html.Append("</tbody></table></details></section>");
    }

    // --- compare.html

    public static void WriteCompare(string a, string b, JsonObject ra, JsonObject rb, IReadOnlyList<ShotResult> shots, string verdict, bool? good, IReadOnlyList<(string Key, string A, string B)> differences, string shotsNote)
    {
        var html = new StringBuilder();
        var test = TestName(System(rb, "Test") is { Length: > 0 } t ? t : null);
        Open(html, $"{test}: {When(a)} and {When(b)}", (string?)rb["drawable"]);
        html.Append("<header class=\"hero slim\"><div class=\"hero-text\"><div class=\"eyebrow\">KSA Metal benchmark · comparison</div>");
        html.Append($"<h1>{E(test)}</h1><p><b>A</b> {E(When(a))} &nbsp; <b>B</b> {E(When(b))}</p>");
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
        var after = new Dictionary<(string, string), JsonObject>();
        foreach (var s in SectionsOf(rb))
            foreach (var w in WindowsOf(s))
                after[((string?)s["name"] ?? "", (string?)w["name"] ?? "")] = w;
        var both = SectionsOf(ra).SelectMany(s => WindowsOf(s).Select(w => (Section: (string?)s["name"] ?? "", Window: w)))
            .Where(p => p.Window["gameDays"] is null && after.ContainsKey((p.Section, (string?)p.Window["name"] ?? ""))).ToList();
        if (both.Count > 0)
        {
            string Measure(JsonObject w) => D(w["gpuMsMedian"]) is var g && !double.IsNaN(g) ? "gpuMsMedian" : "frameMsMedian";
            double scale = Math.Max(both.Max(p => Math.Max(D(p.Window[Measure(p.Window)]), D(after[(p.Section, (string?)p.Window["name"] ?? "")][Measure(p.Window)]))), 1);
            html.Append($"<section><h2>Part by part</h2><p class=\"lead\">GPU time a frame. Small changes are within run-to-run noise and go unmarked.</p>");
            html.Append("<div class=\"key\"><i class=\"s0\"></i>A<i class=\"s1\"></i>B</div><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th class=\"bar-cell\"></th><th>A ms</th><th>B ms</th><th>Change</th><th>Frame ms A</th><th>Frame ms B</th></tr></thead><tbody>");
            string last = "";
            foreach (var (section, wa) in both)
            {
                if (section != last && SectionsOf(ra).Count() > 1)
                    html.Append($"<tr class=\"group\"><th colspan=\"7\">{E(section)}</th></tr>");
                last = section;
                var name = (string?)wa["name"] ?? "";
                var wb = after[(section, name)];
                var m = Measure(wa);
                double x = D(wa[m]), y = D(wb[m]), change = x > 0 ? (y - x) / x : 0;
                bool changed = Program.Marked(wa[m], wb[m], wa[m == "gpuMsMedian" ? "gpuMsRange" : "frameMsRange"], wb[m == "gpuMsMedian" ? "gpuMsRange" : "frameMsRange"]);
                html.Append($"<tr><td class=\"part\">{E(name)}</td><td class=\"bar-cell\"><div class=\"bar s0\" style=\"width:{F(100 * x / scale, "0.0")}%\"></div><div class=\"bar s1\" style=\"width:{F(100 * y / scale, "0.0")}%\"></div></td>");
                html.Append($"<td>{F(x, "0.0")}</td><td>{F(y, "0.0")}</td><td>{(changed ? Change(change < 0, $"{F(Math.Abs(change) * 100, "0")} % {(change < 0 ? "faster" : "slower")}") : $"<span class=\"quiet\">{F(change * 100, "+0;-0;0")} %</span>")}</td>");
                html.Append($"<td>{N(wa["frameMsMedian"], "0.0")}</td><td>{N(wb["frameMsMedian"], "0.0")}</td></tr>");
            }
            html.Append("</tbody></table></div></section>");
        }

        var warp = SectionsOf(ra).SelectMany(s => WindowsOf(s).Select(w => (Section: (string?)s["name"] ?? "", Window: w)))
            .Where(p => p.Window["gameDays"] is not null && after.TryGetValue((p.Section, (string?)p.Window["name"] ?? ""), out var w) && w["gameDays"] is not null).ToList();
        if (warp.Count > 0)
        {
            html.Append("<section><h2>Time warp</h2><div class=\"table-scroll\"><table class=\"parts\"><thead><tr><th>Part</th><th>Days A</th><th>Days B</th><th>Speed A</th><th>Speed B</th><th>Change</th><th>GPU ms A</th><th>GPU ms B</th></tr></thead><tbody>");
            foreach (var (section, wa) in warp)
            {
                var name = (string?)wa["name"] ?? "";
                var wb = after[(section, name)];
                double x = D(wa["speed"]), y = D(wb["speed"]), change = x > 0 ? (y - x) / x : 0;
                html.Append($"<tr><td class=\"part\">{E(name)}</td><td>{N(wa["gameDays"], "0.#")}</td><td>{N(wb["gameDays"], "0.#")}</td><td>x{Compact(x)}</td><td>x{Compact(y)}</td><td>{(Program.Marked(wa["speed"], wb["speed"], wa["speedRange"], wb["speedRange"]) ? Change(change > 0, $"{F(Math.Abs(change) * 100, "0")} % {(change > 0 ? "faster" : "slower")}") : $"<span class=\"quiet\">{F(change * 100, "+0;-0;0")} %</span>")}</td><td>{N(wa["gpuMsMedian"], "0.0")}</td><td>{N(wb["gpuMsMedian"], "0.0")}</td></tr>");
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
