// Two runs compared (compare, comparable): a verdict, the Macs and settings that differ, the numbers part by part, the
// screenshots side by side.
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
    // Changes smaller than this are within run-to-run noise (two runs on one Mac differ by 1% in most parts, 4% at most).
    // With repeats on both sides, a change counts when the ranges of the repeats don't overlap and it is over the
    // smaller threshold.
    private const double Significant = 0.05, SignificantRepeated = 0.02;
    // A screenshot differs from another when more of its blocks than this changed (percent; see Images.cs): fast mode
    // takes the same screenshots every time, the cinematic tour's vary a little with the frames the Mac happened to draw
    private const double FastShotLimit = 1.0, CinematicShotLimit = 3.0;

    // Whether a run about to play will compare with this one: said before the minutes it takes, not after
    private static int Comparable(string run, string mode, string sections)
    {
        var report = Load(Unpacked(run));
        if (report == null)
            return 2;
        // The sections played, whole or up to a place
        static HashSet<string> Names(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => n.Split('_')[0]).ToHashSet();
        string other = (string?)report["mode"] ?? "cinematic";
        if (other != mode)
        {
            Console.WriteLine($"that run played in {other} mode and this one plays in {mode} mode: neither their numbers nor their screenshots would compare.");
            return 2;
        }
        var played = Sections(report).OfType<JsonObject>().Select(s => (string?)s["name"] ?? "").ToHashSet();
        if (!played.Overlaps(Names(sections)))
        {
            Console.WriteLine($"that run played {string.Join(" ", played)} and this one plays {sections}: they have nothing in common.");
            return 2;
        }
        // Fast mode measures the places chosen: places of a section still have to be among those that run measured
        var parts = Windows(report).Keys.ToHashSet();
        var wanted = sections.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (mode == "fast" && wanted.All(n => n.Contains('_')) &&
            !wanted.Any(n => parts.Contains((n[..n.IndexOf('_')], n[(n.IndexOf('_') + 1)..]))))
        {
            Console.WriteLine($"that run didn't measure {sections}: they have no part in common.");
            return 2;
        }
        return 0;
    }

    // The parts of a live section, the time warp's: how far the game's clock got in the time they took
    private static void Warp(StringBuilder md, JsonObject report)
    {
        var parts = Sections(report).OfType<JsonObject>()
            .SelectMany(s => (s["windows"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Where(w => w["gameDays"] is not null).ToList();
        if (parts.Count == 0)
            return;
        double days = parts.Sum(w => w["gameDays"]?.GetValue<double>() ?? 0);
        md.AppendLine();
        md.AppendLine($"Time warp: {days:F1} game days in the {parts.Sum(w => w["seconds"]?.GetValue<double>() ?? 0):F0} s of its {parts.Count} part{(parts.Count > 1 ? "s" : "")}.");
        md.AppendLine();
        md.AppendLine("Speed: game seconds per second, against what was asked (1 is real time). To 90 %: seconds until it ran at 90 % of what was asked. Sim: the slowest vehicle job. A speed far under what was asked means the simulation held it back.");
        md.AppendLine();
        md.AppendLine("| Part | Game days | Speed | Asked | To 90 % s | Worst frame ms | Sim ms | Substeps | Bubbles | Vehicles |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var w in parts)
        {
            string speed = Num(w["speed"], "F0");
            if (w["speedRange"] is JsonArray range)
                speed += $" ({Num(range[0], "F0")}-{Num(range[1], "F0")})";
            md.AppendLine($"| {w["name"]} | {Num(w["gameDays"], "F1")} | {speed} | {Num(w["speedAsked"], "F0")} | {(w["secondsToSpeed"] is null ? "never" : Num(w["secondsToSpeed"], "F1"))} | " +
                          $"{Num(w["frameMsMax"], "F0")} | {Num(w["simMsMax"], "F0")} | {w["substeps"]} | {w["bubblesMax"]} | {w["vehiclesMax"]} |");
        }
    }

    private static string Num(JsonNode? node, string format) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString(format) : "";

    private static int Compare(string a, string b)
    {
        (a, b) = (Unpacked(a), Unpacked(b));
        var (ra, rb) = (Load(a), Load(b));
        if (ra == null || rb == null)
            return 2;
        string modeA = (string?)ra["mode"] ?? "cinematic", modeB = (string?)rb["mode"] ?? "cinematic";
        if (modeA != modeB)
        {
            // Live sections play the same in both modes
            var warp = new StringBuilder();
            WarpNumbers(warp, Windows(ra), Windows(rb));
            Console.Write(Terminal.Layout(warp.ToString()));
            Console.WriteLine($"The runs played in different modes ({modeA}, {modeB}): {(warp.Length > 0 ? "only the time warp's parts compare" : "neither their numbers nor their screenshots compare")}.");
            return 2;
        }
        string dirA = Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, dirB = Directory.Exists(b) ? b : Path.GetDirectoryName(b)!;
        var (partsA, partsB) = (Windows(ra), Windows(rb));
        if (!partsA.Keys.Any(partsB.ContainsKey))
        {
            Console.WriteLine($"The runs have no part in common ({SectionNames(ra)}; {SectionNames(rb)}): nothing to compare.");
            return 2;
        }
        // The screenshots first: the verdict leads with them
        var body = new StringBuilder();
        var warnings = new List<string>();
        foreach (var (run, report) in new[] { ("a", ra), ("b", rb) })
        {
            if (report["stopped"]?.GetValue<bool>() == true)
                warnings.Add($"Run {run} was stopped before its end.");
            else if (report["passed"]?.GetValue<bool>() == false)
                warnings.Add($"Run {run} failed (see its report.md): the parts its problems concern may not compare.");
        }
        if (SectionNames(ra) != SectionNames(rb))
            warnings.Add($"The runs played different sections ({SectionNames(ra)}; {SectionNames(rb)}): clouds and the sea follow what played before, so screenshots may differ for that alone.");
        var differences = Macs(ra, rb);
        differences.AddRange(Settings(dirA, dirB));
        if (differences.Count > 0)
        {
            body.AppendLine("| | a | b |");
            body.AppendLine("|---|---|---|");
            foreach (var (key, va, vb) in differences)
                body.AppendLine($"| {key} | {va} | {vb} |");
            body.AppendLine();
        }
        else
        {
            body.AppendLine("Same Mac, same version, same settings.");
            body.AppendLine();
        }
        Numbers(body, ra, rb);
        // A run in a running game starts with the clouds and the sea where that game had them
        var shots = new List<ShotResult>();
        bool inGame = new[] { ra, rb }.Any(r => ((string?)r["system"]?["Start"] ?? "").StartsWith("in the running game", StringComparison.Ordinal));
        string shotsTable = "", shotsNote = "";
        int differ = 0;
        if (inGame)
            body.AppendLine("Screenshots: not compared, a run played in a running game (its clouds and sea are that game's).");
        else
        {
            (differ, shotsTable) = Screenshots(dirA, dirB, modeA == "fast" ? FastShotLimit : CinematicShotLimit, shots);
            // Nothing compared: the sentence that says why
            if (shots.Count == 0)
                shotsNote = shotsTable;
        }
        bool repeated = (ra["runs"]?.GetValue<int>() ?? 1) > 1 && (rb["runs"]?.GetValue<int>() ?? 1) > 1;
        var (verdict, good) = Verdict(partsA, partsB, shots, inGame, repeated);
        var md = new StringBuilder();
        md.AppendLine("## Comparison");
        md.AppendLine();
        md.AppendLine($"**{verdict}**");
        md.AppendLine();
        foreach (var w in warnings)
        {
            md.AppendLine(w);
            md.AppendLine();
        }
        md.AppendLine($"a: {a}");
        md.AppendLine($"b: {b}");
        md.AppendLine();
        var printed = new StringBuilder(md.ToString()).Append(body).Append(shotsNote).Append(ShotsForTerminal(shots, differ));
        md.Append(body).Append(shotsTable);
        File.WriteAllText(Path.Combine(dirB, "compare.md"), md.ToString());
        Html.WriteCompare(dirA, dirB, ra, rb, shots, verdict, good, differences, shotsNote.Trim());
        Console.Write(Terminal.Layout(printed.ToString()));
        Console.WriteLine();
        Console.WriteLine($"Side by side: {Path.Combine(dirB, "compare.html")}");
        return differ > 0 ? 1 : 0;
    }

    private static string SectionNames(JsonObject report) => string.Join(" ", Sections(report).OfType<JsonObject>().Select(s => (string?)s["name"] ?? ""));

    // One sentence for two runs: the image, then the GPU time over the parts (each part counts the same), then time warp
    private static (string Text, bool? Good) Verdict(Dictionary<(string section, string part), JsonObject> a, Dictionary<(string section, string part), JsonObject> b,
        List<ShotResult> shots, bool inGame, bool repeated)
    {
        var words = new List<string>();
        int differ = shots.Count(s => s.State is "differs" or "unreadable"), compared = shots.Count(s => s.State != "free");
        if (!inGame && compared > 0)
            words.Add(differ == 0 ? $"same image ({compared} of {compared} screenshots)" : $"{differ} of {compared} screenshots differ");
        double Ratio(string field, bool warp, out int parts)
        {
            double sum = 0;
            int n = 0;
            foreach (var (key, wa) in a)
            {
                if (!b.TryGetValue(key, out var wb) || wa["gameDays"] is not null != warp)
                    continue;
                if (wa[field] is JsonValue x && wb[field] is JsonValue y && x.TryGetValue<double>(out var dx) && y.TryGetValue<double>(out var dy) && dx > 0 && dy > 0)
                {
                    sum += Math.Log(dy / dx);
                    n++;
                }
            }
            parts = n;
            return n > 0 ? Math.Exp(sum / n) : 1;
        }
        // Over the limit, and a part that counts as changed in that direction: with repeats, ranges that don't overlap
        bool Moved(string field, string range, bool warp, double ratio)
        {
            if (Math.Abs(ratio - 1) < (repeated ? SignificantRepeated : Significant))
                return false;
            return a.Any(kv => b.TryGetValue(kv.Key, out var wb) && kv.Value["gameDays"] is not null == warp && kv.Value[field] is JsonValue x && wb[field] is JsonValue y &&
                               x.TryGetValue<double>(out var dx) && y.TryGetValue<double>(out var dy) && dy > dx == ratio > 1 && Marked(kv.Value[field], wb[field], kv.Value[range], wb[range]));
        }
        double gpu = Ratio("gpuMsMedian", false, out var gpuParts);
        bool gpuMoved = gpuParts > 0 && Moved("gpuMsMedian", "gpuMsRange", false, gpu);
        if (gpuParts > 0)
            words.Add(gpuMoved ? $"GPU time {Math.Abs(gpu - 1) * 100:F0} % {(gpu < 1 ? "lower" : "higher")}" : "GPU time within noise");
        double speed = Ratio("speed", true, out var warpParts);
        bool warpMoved = warpParts > 0 && Moved("speed", "speedRange", true, speed);
        if (warpParts > 0)
            words.Add(warpMoved ? $"time warp {Math.Abs(speed - 1) * 100:F0} % {(speed > 1 ? "faster" : "slower")}" : "time warp within noise");
        if (words.Count == 0)
            return ("Nothing to compare", null);
        var text = string.Join(", ", words);
        // Good: nothing got worse
        return (char.ToUpperInvariant(text[0]) + text[1..], differ == 0 && !(gpuMoved && gpu > 1) && !(warpMoved && speed < 1));
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
    private static List<(string Key, string A, string B)> Macs(JsonObject ra, JsonObject rb)
    {
        var (sa, sb) = (ra["system"] as JsonObject ?? new JsonObject(), rb["system"] as JsonObject ?? new JsonObject());
        var rows = sa.Select(kv => kv.Key).Union(sb.Select(kv => kv.Key))
            .Where(k => k is not ("Displays" or "Sections" or "Settings changed") && (string?)sa[k] != (string?)sb[k])
            .Select(k => (k, (string?)sa[k] ?? "", (string?)sb[k] ?? "")).ToList();
        if ((string?)ra["drawable"] != (string?)rb["drawable"])
            rows.Add(("Drawn", (string?)ra["drawable"] ?? "", (string?)rb["drawable"] ?? ""));
        return rows;
    }

    // The settings that differ between the two runs (settings.toml in their folders)
    private static List<(string Key, string A, string B)> Settings(string a, string b)
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
        return sa.Keys.Union(sb.Keys).Where(k => sa.GetValueOrDefault(k) != sb.GetValueOrDefault(k)).OrderBy(k => k)
            .Select(k => (k, sa.GetValueOrDefault(k, ""), sb.GetValueOrDefault(k, ""))).ToList();
    }

    private static void Numbers(StringBuilder md, JsonObject ra, JsonObject rb)
    {
        var parts = Windows(ra);
        var after = Windows(rb);
        var plain = parts.Where(kv => kv.Value["gameDays"] is null && after.ContainsKey(kv.Key)).ToList();
        if (plain.Count > 0)
        {
            md.AppendLine("| Section | Part | GPU ms a | GPU ms b | Change | Frame ms a | Frame ms b | Change |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
            string last = "";
            foreach (var (key, wa) in plain)
            {
                var wb = after[key];
                md.AppendLine($"| {(key.section == last ? "" : key.section)} | {key.part} | {Num(wa["gpuMsMedian"], "F1")} | {Num(wb["gpuMsMedian"], "F1")} | " +
                              $"{Change(wa["gpuMsMedian"], wb["gpuMsMedian"], wa["gpuMsRange"], wb["gpuMsRange"])} | {Num(wa["frameMsMedian"], "F1")} | " +
                              $"{Num(wb["frameMsMedian"], "F1")} | {Change(wa["frameMsMedian"], wb["frameMsMedian"], wa["frameMsRange"], wb["frameMsRange"])} |");
                last = key.section;
            }
            md.AppendLine();
        }
        WarpNumbers(md, parts, after);
        var gone = parts.Keys.Where(k => !after.ContainsKey(k)).Select(k => $"{k.section} {k.part}").ToList();
        var come = after.Keys.Where(k => !parts.ContainsKey(k)).Select(k => $"{k.section} {k.part}").ToList();
        if (gone.Count > 0)
            md.AppendLine($"Only in a: {string.Join(", ", gone)}.");
        if (come.Count > 0)
            md.AppendLine($"Only in b: {string.Join(", ", come)}.");
        bool repeated = (ra["runs"]?.GetValue<int>() ?? 1) > 1 && (rb["runs"]?.GetValue<int>() ?? 1) > 1;
        md.AppendLine(repeated
            ? $"Both runs were repeated: a change is marked when the ranges of the repeats don't overlap and it is over {SignificantRepeated:P0}."
            : $"Changes over {Significant:P0} are marked; smaller ones are within run-to-run noise. Frame times sit on the display's beat: read GPU ms first. " +
              "A Mac's heat moves GPU time too, about 10 % between a first run after a rest and the next ones: compare runs at the same point of a series, or with --repeat 3 on both sides.");
        md.AppendLine();
    }

    // The time warp's parts by the speed they reached: more is better
    private static void WarpNumbers(StringBuilder md, Dictionary<(string section, string part), JsonObject> a, Dictionary<(string section, string part), JsonObject> b)
    {
        var parts = a.Where(kv => kv.Value["gameDays"] is not null && b.TryGetValue(kv.Key, out var w) && w["gameDays"] is not null).ToList();
        if (parts.Count == 0)
            return;
        md.AppendLine("| Warp part | Game days a | Game days b | Speed a | Speed b | Change | GPU ms a | GPU ms b |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (key, wa) in parts)
        {
            var wb = b[key];
            md.AppendLine($"| {key.part} | {Num(wa["gameDays"], "F1")} | {Num(wb["gameDays"], "F1")} | {Num(wa["speed"], "F0")} | {Num(wb["speed"], "F0")} | " +
                          $"{Change(wa["speed"], wb["speed"], wa["speedRange"], wb["speedRange"], moreIsBetter: true)} | {Num(wa["gpuMsMedian"], "F1")} | {Num(wb["gpuMsMedian"], "F1")} |");
        }
        md.AppendLine();
    }

    // Each screenshot of a against b's of the same name; marked copies of b's that differ go to b/diff/. A view allowed to
    // change all it likes (the Warp Test's) isn't compared. Returns how many differ, and their table.
    private static (int Differ, string Table) Screenshots(string a, string b, double shotLimit, List<ShotResult> results)
    {
        var md = new StringBuilder();
        var shots = Directory.GetFiles(a, "*_*.png").Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (shots.Count == 0)
        {
            md.AppendLine(Directory.GetFiles(a, "*_*.jpg").Length > 0
                ? "Screenshots not compared: a's are JPEG (a shared zip's), which only show by eye what changed."
                : "No screenshots in a to compare.");
            return (0, md.ToString());
        }
        // The limits of screenshots that change from run to run
        // The newer run's say, so that a limit tightened since an older run counts: a's only for sections b didn't play
        var limits = new Dictionary<string, double>();
        var played = Sections(Load(b) ?? new JsonObject()).OfType<JsonObject>().Select(s => (string?)s["name"]).ToHashSet();
        foreach (var (dir, onlyOthers) in new[] { (a, true), (b, false) })
        {
            foreach (var section in Sections(Load(dir) ?? new JsonObject()).OfType<JsonObject>().Where(s => !onlyOthers || !played.Contains((string?)s["name"])))
            {
                foreach (var (shot, limit) in section["shotLimits"] as JsonObject ?? new JsonObject())
                    limits[$"{section["name"]}_{shot}.png"] = limit?.GetValue<double>() ?? shotLimit;
            }
        }
        var diffDir = Path.Combine(b, "diff");
        if (Directory.Exists(diffDir))
            Directory.Delete(diffDir, true);
        int differ = 0;
        md.AppendLine("| Screenshot | Changed | Mean change | |");
        md.AppendLine("|---|---:|---:|---|");
        foreach (var name in shots)
        {
            var pathB = Path.Combine(b, name);
            if (!File.Exists(pathB))
            {
                // A run without screenshots compares by its numbers only
                continue;
            }
            double allowed = limits.GetValueOrDefault(name, shotLimit);
            if (allowed >= 100)
            {
                results.Add(new ShotResult(name, 0, 0, allowed, "free"));
                continue;
            }
            var (ia, ib) = (Images.Read(Path.Combine(a, name)), Images.Read(pathB));
            if (ia == null || ib == null)
            {
                md.AppendLine($"| {name} | | | **unreadable** |");
                results.Add(new ShotResult(name, 100, 0, allowed, "unreadable"));
                differ++;
                continue;
            }
            var d = Images.Compare(ia, ib);
            bool same = d.ChangedPercent <= allowed;
            md.AppendLine($"| {name} | {d.ChangedPercent:F2}% | {d.MeanChange:F1} | {(same ? "same" : "**differs**")}{(allowed > shotLimit ? $" (up to {allowed:F0}%)" : "")} |");
            results.Add(new ShotResult(name, d.ChangedPercent, d.MeanChange, allowed, same ? "same" : "differs"));
            if (same)
                continue;
            differ++;
            Directory.CreateDirectory(diffDir);
            Images.Write(Path.Combine(diffDir, name), Images.Mark(ib, d));
        }
        if (results.Count == 0)
            return (0, "Screenshots not compared: b has no PNG of a's screenshots (a run without screenshots, or a shared zip's).\n");
        md.AppendLine();
        md.AppendLine($"Changed: the share of the screenshot's {Images.Block}-pixel blocks whose colour moved by more than {Images.BlockLimit:F0} " +
                      $"(of 255); over {shotLimit:F0}% it differs (more for views that physics, particles or the sea change every time).");
        if (differ > 0)
            md.AppendLine($"{differ} screenshot{(differ > 1 ? "s differ" : " differs")}: {diffDir} shows where (in red).");
        return (differ, md.ToString());
    }

    // The screenshots for a terminal: those that differ, each with its numbers, and how many are the same
    private static string ShotsForTerminal(List<ShotResult> shots, int differ)
    {
        var md = new StringBuilder();
        if (differ > 0)
        {
            md.AppendLine("| Screenshot | Changed | Mean change | |");
            md.AppendLine("|---|---:|---:|---|");
            foreach (var s in shots.Where(s => s.State is "differs" or "unreadable"))
                md.AppendLine($"| {s.Name} | {s.ChangedPercent:F2}% | {s.MeanChange:F1} | **{s.State}** (up to {s.Allowed:F0}%) |");
            md.AppendLine();
        }
        int same = shots.Count(s => s.State == "same"), free = shots.Count(s => s.State == "free");
        var told = new List<string>();
        if (same > 0)
            told.Add($"{same} screenshot{(same > 1 ? "s" : "")} the same");
        if (free > 0)
            told.Add($"{free} that may change from run to run not compared");
        if (told.Count > 0)
            md.AppendLine(string.Join(", ", told) + ".");
        return md.ToString();
    }

    // A run's report, and the measured parts of its sections (with Html.cs and Report.cs's page)
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

    // Whether a number moved by more than noise: with repeats on both sides, the ranges don't overlap
    public static bool Marked(JsonNode? a, JsonNode? b, JsonNode? rangeA, JsonNode? rangeB)
    {
        if (a is not JsonValue va || b is not JsonValue vb || !va.TryGetValue<double>(out var x) || !vb.TryGetValue<double>(out var y) || x <= 0)
            return false;
        double change = (y - x) / x;
        return rangeA is JsonArray ra && rangeB is JsonArray rb
            ? Math.Abs(change) >= SignificantRepeated && ((double)rb[0]! > (double)ra[1]! || (double)rb[1]! < (double)ra[0]!)
            : Math.Abs(change) >= Significant;
    }

    // How far b is from a, marked when it counts: "12 % slower", or "+2%" within noise. Times: less is faster.
    private static string Change(JsonNode? a, JsonNode? b, JsonNode? rangeA, JsonNode? rangeB, bool moreIsBetter = false)
    {
        if (a is not JsonValue va || b is not JsonValue vb || !va.TryGetValue<double>(out var x) || !vb.TryGetValue<double>(out var y) || x <= 0)
            return "";
        double change = (y - x) / x;
        if (!Marked(a, b, rangeA, rangeB))
            return (change * 100).ToString("+0;-0;0") + " %";
        return $"**{Math.Abs(change) * 100:F0} % {(change < 0 != moreIsBetter ? "faster" : "slower")}**";
    }
}
