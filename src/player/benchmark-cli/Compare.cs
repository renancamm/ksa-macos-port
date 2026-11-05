// Two runs compared (compare, and the check before a run with --compare): a verdict, the Macs and settings that
// differ, the numbers part by part, the screenshots side by side. Both runs are read as records (run.json, or an older
// run's report.json).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    // Changes smaller than this are within run-to-run noise (two runs on one Mac differ by 1% in most parts, 4% at most),
    // unless the test's noise profile gives a part its own limit (Noise.Moved). With repeats on both sides, a change counts
    // when the ranges of the repeats don't overlap and it is over the smaller threshold.
    private const double Significant = Noise.Significant, SignificantRepeated = Noise.SignificantRepeated;

    // Whether a run about to play will compare with this one: said before the minutes it takes, not after. Null when it
    // will, else why not
    internal static string? NotComparable(string run, string mode, IReadOnlyList<string> played)
    {
        if (Load(Unpacked(run)) is not { } record)
            return $"no run's record in {run}";
        if (record.Scenarios.Any(s => s.Variant != null))
            return "that run is an A/B run: its report sets its two sides against each other.";
        if (record.Mode != mode)
            return $"that run played in {record.Mode} mode and this one plays in {mode} mode: neither their numbers nor their screenshots would compare.";
        var scenarios = record.Scenarios.Select(s => s.Name).ToHashSet();
        var names = played.Select(n => n.Split('_')[0]).ToHashSet();
        if (!scenarios.Overlaps(names))
            return $"that run played {string.Join(" ", scenarios)} and this one plays {string.Join(" ", played)}: they have nothing in common.";
        // Places measure only themselves: one of them has to be among those that run measured
        if (played.All(n => n.Contains('_')) &&
            !played.Any(n => record.Scenario(n[..n.IndexOf('_')])?.Part(n[(n.IndexOf('_') + 1)..]) != null))
            return $"that run didn't measure {string.Join(" ", played)}: they have no part in common.";
        return null;
    }

    internal static int Compare(string a, string b)
    {
        (a, b) = (Unpacked(a), Unpacked(b));
        var (ra, rb) = (Load(a), Load(b));
        if (ra == null || rb == null)
            return 2;
        // An A/B run's two sides are set against each other in its own report
        if (new[] { (a, ra), (b, rb) }.FirstOrDefault(x => x.Item2.Scenarios.Any(s => s.Variant != null)) is ({ } ab, not null))
        {
            Console.Error.WriteLine($"{Path.GetFileName(ab.TrimEnd('/'))} is an A/B run: its report sets its two sides against each other (./benchmark.sh report {Path.GetFileName(ab.TrimEnd('/'))}).");
            return 2;
        }
        var (warpA, warpB) = (Parts(ra, warp: true), Parts(rb, warp: true));
        if (ra.Mode != rb.Mode)
        {
            // Timed scenarios play the same whatever the rest played
            var warp = Changes(warpA, warpB, warp: true, false);
            var md = new StringBuilder();
            WarpNumbers(md, warp);
            Console.Write(Terminal.Layout(md.ToString()));
            Console.WriteLine($"The runs played in different modes ({ra.Mode}, {rb.Mode}): {(warp.Count > 0 ? "only the time warp's parts compare" : "neither their numbers nor their screenshots compare")}.");
            return 2;
        }
        string dirA = Directory.Exists(a) ? a : Path.GetDirectoryName(a)!, dirB = Directory.Exists(b) ? b : Path.GetDirectoryName(b)!;
        var (partsA, partsB) = (Parts(ra, warp: false), Parts(rb, warp: false));
        if (!partsA.Keys.Concat(warpA.Keys).Any(k => partsB.ContainsKey(k) || warpB.ContainsKey(k)))
        {
            Console.WriteLine($"The runs have no part in common ({Scenarios(ra)}; {Scenarios(rb)}): nothing to compare.");
            return 2;
        }
        bool repeated = Repeated(ra) && Repeated(rb);
        var plain = Changes(partsA, partsB, warp: false, repeated);
        var warps = Changes(warpA, warpB, warp: true, repeated);
        // The screenshots first: the verdict leads with them
        var body = new StringBuilder();
        var warnings = new List<string>();
        foreach (var (run, record) in new[] { ("a", ra), ("b", rb) })
        {
            if (record.Status == Status.Stopped)
                warnings.Add($"Run {run} was stopped before its end.");
            else if (record.Status == Status.Failed)
                warnings.Add($"Run {run} failed (see its report.md): the parts its problems concern may not compare.");
        }
        if (Scenarios(ra) != Scenarios(rb))
            warnings.Add($"The runs played different scenarios ({Scenarios(ra)}; {Scenarios(rb)}): clouds and the sea follow what played before, so screenshots may differ for that alone.");
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
        Numbers(body, plain, warps, partsA, partsB, repeated);
        // A run in a running game starts with the clouds and the sea where that game had them
        var shots = new List<ShotResult>();
        bool inGame = ra.InGame || rb.InGame;
        string shotsTable = "", shotsNote = "";
        int differ = 0;
        if (inGame)
            body.AppendLine("Screenshots: not compared, a run played in a running game (its clouds and sea are that game's).");
        else
        {
            (differ, shotsTable) = Screenshots(dirA, dirB, ra, rb, shots);
            // Nothing compared: the sentence that says why
            if (shots.Count == 0)
                shotsNote = shotsTable;
        }
        var (verdict, good) = Verdict(plain, warps, shots, inGame, repeated);
        var head = new StringBuilder();
        head.AppendLine("## Comparison");
        head.AppendLine();
        head.AppendLine($"**{verdict}**");
        head.AppendLine();
        foreach (var w in warnings)
        {
            head.AppendLine(w);
            head.AppendLine();
        }
        head.AppendLine($"a: {a}");
        head.AppendLine($"b: {b}");
        head.AppendLine();
        var printed = new StringBuilder(head.ToString()).Append(body).Append(shotsNote).Append(ShotsForTerminal(shots, differ));
        head.Append(body).Append(shotsTable);
        File.WriteAllText(Path.Combine(dirB, "compare.md"), head.ToString());
        Pages.WriteCompare(dirA, dirB, ra, rb, plain, warps, shots, verdict, good, differences, shotsNote.Trim());
        Console.Write(Terminal.Layout(printed.ToString()));
        Console.WriteLine();
        Console.WriteLine($"Side by side: {Path.Combine(dirB, "compare.html")}");
        return differ > 0 ? 1 : 0;
    }

    // Whether a run measured its parts more than once (--repeat; full's sessions play other tests)
    private static bool Repeated(RunRecord r) => r.AllParts().Any(x => x.Part.Runs > 1);

    private static string Scenarios(RunRecord r) => string.Join(" ", r.Scenarios.Select(s => s.Name));

    // A run's parts by scenario and name: time warp's, or the others
    private static Dictionary<(string Scenario, string Part), PartRecord> Parts(RunRecord r, bool warp)
    {
        var all = new Dictionary<(string, string), PartRecord>();
        foreach (var (s, p) in r.AllParts().Where(x => x.Part.Warp != null == warp))
            all.TryAdd((s.Name, p.Name), p);
        return all;
    }

    // The parts both runs have, a's order, with their change: GPU time where both have it (frame times sit on the display's
    // beat), the speed reached in time warp
    private static List<PartChange> Changes(Dictionary<(string Scenario, string Part), PartRecord> a, Dictionary<(string Scenario, string Part), PartRecord> b,
        bool warp, bool repeated)
    {
        var changes = new List<PartChange>();
        foreach (var (key, pa) in a)
        {
            if (!b.TryGetValue(key, out var pb))
                continue;
            double x, y;
            double[]? rx, ry;
            if (warp)
                (x, y, rx, ry) = (pa.Warp!.Speed, pb.Warp!.Speed, pa.Warp.SpeedRange, pb.Warp.SpeedRange);
            else if (pa.GpuMsMedian is { } ga && pb.GpuMsMedian is { } gb)
                (x, y, rx, ry) = (ga, gb, pa.GpuMsRange, pb.GpuMsRange);
            else
                (x, y, rx, ry) = (pa.FrameMsMedian, pb.FrameMsMedian, pa.FrameMsRange, pb.FrameMsRange);
            var limit = pa.Limit is { } la && pb.Limit is { } lb ? Math.Max(la, lb) : pa.Limit ?? pb.Limit;
            changes.Add(new PartChange(key.Scenario, pa, pb, Noise.Moved(x, y, rx, ry, warp ? null : limit), x > 0 ? (y - x) / x : 0));
        }
        return changes;
    }

    // One sentence for two runs: the image, then the GPU time over the parts (each part counts the same), then time warp
    private static (string Text, bool? Good) Verdict(List<PartChange> plain, List<PartChange> warps, List<ShotResult> shots, bool inGame, bool repeated)
    {
        var words = new List<string>();
        int differ = shots.Count(s => s.State is "differs" or "unreadable"), compared = shots.Count(s => s.State != "free");
        if (!inGame && compared > 0)
            words.Add(differ == 0 ? $"same image ({compared} of {compared} screenshots)" : $"{differ} of {compared} screenshots differ");
        // A part at zero on either side (a stalled warp) has no ratio: it is left out
        static double Ratio(List<PartChange> changes) =>
            changes.Where(c => c.Change > -1).ToList() is { Count: > 0 } usable ? Math.Exp(usable.Average(c => Math.Log(1 + c.Change))) : 1;
        // Over the limit, and a part that counts as changed in that direction
        bool Moved(List<PartChange> changes, double ratio) =>
            Math.Abs(ratio - 1) >= (repeated ? SignificantRepeated : Significant) && changes.Any(c => c.Marked && c.Change > 0 == ratio > 1);
        var gpuParts = plain.Where(c => c.A.GpuMsMedian != null && c.B.GpuMsMedian != null).ToList();
        double gpu = Ratio(gpuParts);
        bool gpuMoved = gpuParts.Count > 0 && Moved(gpuParts, gpu);
        if (gpuParts.Count > 0)
            words.Add(gpuMoved ? $"GPU time {Math.Abs(gpu - 1) * 100:F0} % {(gpu < 1 ? "lower" : "higher")}" : "GPU time within noise");
        double speed = Ratio(warps);
        bool warpMoved = warps.Count > 0 && Moved(warps, speed);
        if (warps.Count > 0)
            words.Add(warpMoved ? $"time warp {Math.Abs(speed - 1) * 100:F0} % {(speed > 1 ? "faster" : "slower")}" : "time warp within noise");
        if (words.Count == 0)
            return ("Nothing to compare", null);
        var text = string.Join(", ", words);
        // Good: nothing got worse
        return (char.ToUpperInvariant(text[0]) + text[1..], differ == 0 && !(gpuMoved && gpu > 1) && !(warpMoved && speed < 1));
    }

    // A run's record, or null (said on the error stream)
    private static RunRecord? Load(string dir)
    {
        if (RunRecord.Read(dir) is { } record)
            return record;
        Console.Error.WriteLine($"no run's record at {dir}");
        return null;
    }

    // A shared run's zip, unpacked next to it (once): the folder that holds its record
    private static string Unpacked(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return path;
        var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
        if (!Directory.Exists(dir))
            ZipFile.ExtractToDirectory(path, dir);
        var record = Directory.GetFiles(dir, RunRecord.FileName, SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(dir, "report.json", SearchOption.AllDirectories)).OrderBy(f => f.Length).FirstOrDefault();
        return record != null ? Path.GetDirectoryName(record)! : dir;
    }

    // What differs between the two runs' Macs and versions
    private static List<(string Key, string A, string B)> Macs(RunRecord ra, RunRecord rb)
    {
        var rows = ra.Facts.Select(f => f.Key).Union(rb.Facts.Select(f => f.Key))
            .Where(k => k is not ("Displays" or "Scenarios" or "Settings changed" or "While A played" or "While B played") && ra.Fact(k) != rb.Fact(k))
            .Select(k => (k, ra.Fact(k) ?? "", rb.Fact(k) ?? "")).ToList();
        if (ra.Mode != rb.Mode)
            rows.Add(("Mode", ra.Mode, rb.Mode));
        if (ra.Drawable != rb.Drawable)
            rows.Add(("Drawn", ra.Drawable ?? "", rb.Drawable ?? ""));
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

    private static string Num(double? x, string format) => x is { } v ? v.ToString(format, System.Globalization.CultureInfo.InvariantCulture) : "";

    // How far b is from a, marked when it counts: "12 % slower", or "+2%" within noise. Times: less is faster.
    private static string ChangeText(PartChange c, bool moreIsBetter = false) =>
        c.Marked ? $"**{Math.Abs(c.Change) * 100:F0} % {(c.Change < 0 != moreIsBetter ? "faster" : "slower")}**" : $"{c.Change * 100:+0;-0;0} %";

    private static void Numbers(StringBuilder md, List<PartChange> plain, List<PartChange> warps,
        Dictionary<(string Scenario, string Part), PartRecord> a, Dictionary<(string Scenario, string Part), PartRecord> b, bool repeated)
    {
        if (plain.Count > 0)
        {
            md.AppendLine("| Scenario | Part | GPU ms a | GPU ms b | Change | Frame ms a | Frame ms b | Change |");
            md.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
            string last = "";
            foreach (var c in plain)
            {
                var frame = new PartChange(c.Scenario, c.A, c.B, Noise.Moved(c.A.FrameMsMedian, c.B.FrameMsMedian, c.A.FrameMsRange, c.B.FrameMsRange),
                    c.A.FrameMsMedian > 0 ? (c.B.FrameMsMedian - c.A.FrameMsMedian) / c.A.FrameMsMedian : 0);
                var gpu = c.A.GpuMsMedian != null && c.B.GpuMsMedian != null ? ChangeText(c) : "";
                md.AppendLine($"| {(c.Scenario == last ? "" : c.Scenario)} | {c.A.Name} | {Num(c.A.GpuMsMedian, "F1")} | {Num(c.B.GpuMsMedian, "F1")} | {gpu} | " +
                              $"{Num(c.A.FrameMsMedian, "F1")} | {Num(c.B.FrameMsMedian, "F1")} | {ChangeText(frame)} |");
                last = c.Scenario;
            }
            md.AppendLine();
        }
        WarpNumbers(md, warps);
        var gone = a.Keys.Where(k => !b.ContainsKey(k)).Select(k => $"{k.Scenario} {k.Part}").ToList();
        var come = b.Keys.Where(k => !a.ContainsKey(k)).Select(k => $"{k.Scenario} {k.Part}").ToList();
        if (gone.Count > 0)
            md.AppendLine($"Only in a: {string.Join(", ", gone)}.");
        if (come.Count > 0)
            md.AppendLine($"Only in b: {string.Join(", ", come)}.");
        md.AppendLine(repeated
            ? $"Both runs were repeated: a change is marked when the ranges of the repeats don't overlap and it is over {SignificantRepeated:P0}."
            : $"Changes past a part's limit (its test's noise profile, else {Significant:P0}) are marked; smaller ones are within run-to-run noise. Frame times sit on the display's beat: read GPU ms first. " +
              "A Mac's heat moves GPU time too, about 10 % between a first run after a rest and the next ones: compare runs at the same point of a series, or measure both sides (./benchmark.sh measure).");
        md.AppendLine();
    }

    // The time warp's parts by the speed they reached: more is better
    private static void WarpNumbers(StringBuilder md, List<PartChange> warps)
    {
        if (warps.Count == 0)
            return;
        md.AppendLine("| Warp part | Game days a | Game days b | Speed a | Speed b | Change | GPU ms a | GPU ms b |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var c in warps)
            md.AppendLine($"| {c.A.Name} | {Num(c.A.Warp!.GameDays, "F1")} | {Num(c.B.Warp!.GameDays, "F1")} | {Num(c.A.Warp.Speed, "F0")} | {Num(c.B.Warp.Speed, "F0")} | " +
                          $"{ChangeText(c, moreIsBetter: true)} | {Num(c.A.GpuMsMedian, "F1")} | {Num(c.B.GpuMsMedian, "F1")} |");
        md.AppendLine();
    }

    // Each screenshot of a against b's of the same name, held to the limit b's record gives it (a's for one b lacks);
    // marked copies of b's that differ go to b/diff/. A view allowed to change all it likes (a timed scenario's) isn't
    // compared. Returns how many differ, and their table.
    private static (int Differ, string Table) Screenshots(string a, string b, RunRecord ra, RunRecord rb, List<ShotResult> results)
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
        var limits = new Dictionary<string, double>();
        foreach (var record in new[] { ra, rb })
            foreach (var shot in record.Scenarios.SelectMany(s => s.Shots))
                limits[shot.File] = shot.Limit;
        double fallback = rb.Scenarios.Count > 0 ? rb.Scenarios.Min(s => NoiseProfile.DefaultShotLimit(s.Kind)) : 1;
        var diffDir = Path.Combine(b, "diff");
        if (Directory.Exists(diffDir))
            Directory.Delete(diffDir, true);
        int differ = 0;
        md.AppendLine("| Screenshot | Changed | Mean change | |");
        md.AppendLine("|---|---:|---:|---|");
        foreach (var name in shots)
        {
            var pathB = Path.Combine(b, name);
            // A run without screenshots compares by its numbers only
            if (!File.Exists(pathB))
                continue;
            double allowed = limits.GetValueOrDefault(name, fallback);
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
            md.AppendLine($"| {name} | {d.ChangedPercent:F2}% | {d.MeanChange:F1} | {(same ? "same" : "**differs**")} (up to {allowed:0.#}%) |");
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
                      "(of 255); past its limit it differs. The limits come from the test's noise profile: how much repeated runs of the same build moved.");
        if (differ > 0)
            md.AppendLine($"{differ} screenshot{(differ > 1 ? "s differ" : " differs")}: {diffDir} shows where (in red).");
        return (differ, md.ToString());
    }
}
