// Two runs compared (compare, and the check before a run with --compare): a verdict, the Macs and settings that
// differ, the numbers part by part, the screenshots side by side. Both runs are read as records (run.json).
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
    // Whether a run about to play will compare with this one: said before the minutes it takes, not after. Null when it
    // will, else why not
    internal static string? NotComparable(string run, string mode, IReadOnlyList<string> played)
    {
        if (Load(Unpacked(run)) is not { } record)
            return $"no run's record in {run}";
        if (record.Fact("A/B") != null)
            return "that run is an A/B run of before studies: its two sides are in one record.";
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
        // An A/B run of before studies holds both sides in one record
        if (new[] { (a, ra), (b, rb) }.FirstOrDefault(x => x.Item2.Fact("A/B") != null) is ({ } ab, not null))
        {
            Console.Error.WriteLine($"{Path.GetFileName(ab.TrimEnd('/'))} is an A/B run of before studies: its two sides are in one record, which doesn't compare.");
            return 2;
        }
        var c = Comparison.Of(ra.AllParts().Select(x => (x.Scenario.Name, x.Part)), rb.AllParts().Select(x => (x.Scenario.Name, x.Part)),
            Ground.Steps(ra) || Ground.Steps(rb));
        if (ra.Mode != rb.Mode)
        {
            // Timed scenarios play the same whatever the rest played
            var md = new StringBuilder();
            WarpNumbers(md, c);
            Console.Write(Terminal.Layout(md.ToString()));
            Console.WriteLine($"The runs played in different modes ({ra.Mode}, {rb.Mode}): {(c.Warps.Count > 0 ? "only the time warp's parts compare" : "neither their numbers nor their screenshots compare")}.");
            return 2;
        }
        // Their files: each run's data/ (RunFiles), where compare.md and diff/ go too (compare.html on top of b's folder)
        string dirA = RunFiles.Data(Directory.Exists(a) ? a : Path.GetDirectoryName(a)!), dirB = RunFiles.Data(Directory.Exists(b) ? b : Path.GetDirectoryName(b)!);
        if (c.Parts.Count + c.Warps.Count == 0)
        {
            Console.WriteLine($"The runs have no part in common ({Scenarios(ra)}; {Scenarios(rb)}): nothing to compare.");
            return 2;
        }
        // The screenshots first: the verdict leads with them
        var body = new StringBuilder();
        var warnings = new List<string>();
        // Numbers that don't stand on the same ground: said first
        if (Ground.Unlike([ra, rb]) is { Count: > 0 } unlike)
            warnings.Add($"Not like for like: {string.Join("; ", unlike.Select(u => $"{u.Key} {u.Values[0]} (a), {u.Values[1]} (b)"))}. Every number moves with these.");
        foreach (var (run, record) in new[] { ("a", ra), ("b", rb) })
        {
            if (record.Status == Status.Stopped)
                warnings.Add($"Run {run} was stopped before its end.");
            else if (record.Status == Status.Failed)
                warnings.Add($"Run {run} failed (see its report.md): the parts its problems concern may not compare.");
            foreach (var note in Ground.Notes(record))
                warnings.Add($"Run {run}: {note.Text}.");
        }
        // Sessions a side played run after run: the Mac's heat over that series moves GPU time on its own, a study's turns don't
        if (c.Basis == Basis.Sessions)
            warnings.Add("Each run played its sessions in a row, one run after the other: a Mac's heat moves GPU time on its own over a series like that (several % in half an hour). A study plays the sides in turns (./benchmark.sh study, a variant a side, --app for another build).");
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
        Numbers(body, c);
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
        int compared = shots.Count(s => s.State != "free"), different = shots.Count(s => s.State is "differs" or "unreadable");
        string? image = !inGame && compared > 0 ? different == 0 ? $"same image ({compared} of {compared} screenshots)" : $"{different} of {compared} screenshots differ" : null;
        var (verdict, good) = c.Sentence(image, different == 0);
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
        // What moved, first: each part that changed, with the zones that moved with it, and its other numbers that moved
        var moved = c.Parts.Concat(c.Warps).Where(p => p.Decides is { Changed: true } || p.Others.Any()).ToList();
        if (moved.Count > 0)
        {
            head.AppendLine("What moved:");
            head.AppendLine();
            foreach (var p in moved)
            {
                if (p.Decides is { Changed: true } d)
                    head.AppendLine($"- {p.Line()} ({Comparison.Text(d)})");
                foreach (var v in p.Others)
                    head.AppendLine($"- {p.Line(v)}");
            }
            head.AppendLine();
            if (moved.Any(p => p.ZonesWanted))
            {
                head.AppendLine("Profile both runs to name the GPU's zones that moved: ./benchmark.sh profile.");
                head.AppendLine();
            }
        }
        var printed = new StringBuilder(head.ToString()).Append(body).Append(shotsNote).Append(ShotsForTerminal(shots, differ));
        head.Append(body).Append(shotsTable);
        File.WriteAllText(Path.Combine(dirB, "compare.md"), head.ToString());
        Pages.WriteCompare(dirA, dirB, ra, rb, c, shots, verdict, good, differences, warnings, shotsNote.Trim());
        Console.Write(Terminal.Layout(printed.ToString()));
        Console.WriteLine();
        Console.WriteLine($"Side by side: {Path.Combine(RunFiles.Folder(dirB), RunFiles.ComparePage)}");
        return differ > 0 ? 1 : 0;
    }

    private static string Scenarios(RunRecord r) => string.Join(" ", r.Scenarios.Select(s => s.Name));

    // A run's record, or null (said on the error stream)
    private static RunRecord? Load(string dir)
    {
        if (RunRecord.Read(dir) is { } record)
            return record;
        Console.Error.WriteLine(File.Exists(Path.Combine(RunFiles.Data(dir), RunStore.GameFile))
            ? $"the record at {dir} is older than this benchmark: ./benchmark.sh report {RunFiles.Name(dir)} --again makes it again from its store"
            : $"no run's record at {dir}");
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
        var record = Directory.GetFiles(dir, RunRecord.FileName, SearchOption.AllDirectories).OrderBy(f => f.Length).FirstOrDefault();
        return record != null ? Path.GetDirectoryName(record)! : dir;
    }

    // What differs between the two runs' Macs and versions
    private static List<(string Key, string A, string B)> Macs(RunRecord ra, RunRecord rb)
    {
        var rows = ra.Facts.Select(f => f.Key).Union(rb.Facts.Select(f => f.Key))
            .Where(k => k is not ("Displays" or "Scenarios" or "Settings changed") && ra.Fact(k) != rb.Fact(k))
            .Select(k => k == "Hooks" ? HooksThatDiffer(ra.Fact(k) ?? "", rb.Fact(k) ?? "") : (k, ra.Fact(k) ?? "", rb.Fact(k) ?? ""))
            // The same hooks in another order, or a line said more often, differ in nothing
            .Where(r => r is not ("Hooks", "-", "-")).ToList();
        if (ra.Mode != rb.Mode)
            rows.Add(("Mode", ra.Mode, rb.Mode));
        if (ra.Drawable != rb.Drawable)
            rows.Add(("Drawn", ra.Drawable ?? "", rb.Drawable ?? ""));
        return rows;
    }

    // The hooks whose words differ, each side's: the rest are the same
    private static (string Key, string A, string B) HooksThatDiffer(string a, string b)
    {
        var (ha, hb) = (a.Split("; "), b.Split("; "));
        string Only(string[] these, string[] others) => string.Join("; ", these.Except(others)) is { Length: > 0 } only ? only : "-";
        return ("Hooks", Only(ha, hb), Only(hb, ha));
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

    private static void Numbers(StringBuilder md, Comparison c)
    {
        var parts = c.Parts.Where(p => p.Decides != null).ToList();
        if (parts.Count > 0)
        {
            md.AppendLine("| Scenario | Part | | a | b | Change | Frame ms a | Frame ms b | Change |");
            md.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|");
            string last = "";
            foreach (var p in parts)
            {
                var v = p.Decides!;
                var frame = p.Verdicts.FirstOrDefault(x => x.Metric == Metrics.FrameTime);
                md.AppendLine($"| {(p.Scenario == last ? "" : p.Scenario)} | {p.Name} | {v.Metric.Label} | {v.Metric.Format(v.A)} | {v.Metric.Format(v.B)} | {Pages.Marked(v)} | " +
                              $"{(frame != null ? $"{frame.Metric.Format(frame.A)} | {frame.Metric.Format(frame.B)} | {Pages.Marked(frame)}" : " | | ")} |");
                last = p.Scenario;
            }
            md.AppendLine();
        }
        WarpNumbers(md, c);
        if (c.OnlyA.Count > 0)
            md.AppendLine($"Only in a: {string.Join(", ", c.OnlyA)}.");
        if (c.OnlyB.Count > 0)
            md.AppendLine($"Only in b: {string.Join(", ", c.OnlyB)}.");
        md.AppendLine(Pages.BasisNote(c.Basis) + (c.Basis == Basis.Screen && c.Parts.Any(p => p.Steps) ? " With Vsync, frame times sit on the display's beat: read GPU busy first." : ""));
        md.AppendLine(Pages.NotJudged);
        md.AppendLine();
    }

    // The time warp's parts by the speed they reached: more is better
    private static void WarpNumbers(StringBuilder md, Comparison c)
    {
        var warps = c.Warps.Where(p => p.Decides != null).ToList();
        if (warps.Count == 0)
            return;
        md.AppendLine("| Warp part | Game days a | Game days b | Speed a | Speed b | Change | GPU busy a | GPU busy b |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        string N(PartRecord p, Metric m) => p[m] is { } v ? m.Format(v) : "";
        foreach (var p in warps)
            md.AppendLine($"| {p.Name} | {N(p.A, Metrics.GameDays)} | {N(p.B, Metrics.GameDays)} | {Metrics.Speed.Format(p.Decides!.A)} | {Metrics.Speed.Format(p.Decides.B)} | " +
                          $"{Pages.Marked(p.Decides)} | {N(p.A, Metrics.GpuBusy)} | {N(p.B, Metrics.GpuBusy)} |");
        md.AppendLine();
    }

    // Each screenshot of a against b's of the same name, held to the limit b's record gives it (a's for one b lacks);
    // marked copies of b's that differ go to b/diff/. A view allowed to change all it likes (a timed scenario's) isn't
    // compared. Returns how many differ, and their table.
    private static (int Differ, string Table) Screenshots(string a, string b, RunRecord ra, RunRecord rb, List<ShotResult> results)
    {
        (a, b) = (RunFiles.Data(a), RunFiles.Data(b));
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
