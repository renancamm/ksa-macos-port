// benchmark.sh's program: its entry, and what it shows of runs: a run's report in the terminal, its page made again, the
// last runs listed. Everything comes from runs' records (src/shared/benchmark/Record.cs).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    public static int Main(string[] args)
    {
        // The numbers it reads and writes are the invariant culture's, whatever the Mac's region. This program is a
        // process of its own, so it sets that once, here; the director formats each number itself, since in the game
        // the culture is the player's.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        // Its events go to the run's host.db, the game's to its run.db (Store.cs)
        RunStore.ProcessFile = RunStore.HostFile;
        RunStore.Log = Console.Error.WriteLine;
        if (args.Length >= 1 && args[0] == "command")
            return Command(args[1..]);
        Console.Error.WriteLine("usage: command <benchmark.sh's arguments> (benchmark.sh runs it)");
        return 2;
    }

    // A run's report for a terminal: the verdict and the numbers, without the facts about the Mac
    internal static int Show(string dir)
    {
        var file = Path.Combine(dir, "report.md");
        if (!File.Exists(file))
        {
            if (RunRecord.Read(dir) is not { } record)
            {
                Console.Error.WriteLine($"no report at {file}");
                return 2;
            }
            Pages.WriteReport(dir, record);
        }
        var md = File.ReadAllText(file);
        int facts = md.IndexOf(Pages.FactsHeading, StringComparison.Ordinal);
        Console.Write(Terminal.Layout(facts > 0 ? md[..facts] : md));
        return 0;
    }

    // The screenshots of a comparison for a terminal: those that differ, each with its numbers, and how many are the same
    private static string ShotsForTerminal(List<ShotResult> shots, int differ)
    {
        var md = new StringBuilder();
        if (differ > 0)
        {
            md.AppendLine("| Screenshot | Changed | Mean change | |");
            md.AppendLine("|---|---:|---:|---|");
            foreach (var s in shots.Where(s => s.State is "differs" or "unreadable"))
                md.AppendLine($"| {s.Name} | {s.ChangedPercent:F2}% | {s.MeanChange:F1} | **{s.State}** (up to {s.Allowed:0.#}%) |");
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

    // Whether a folder is a run: named by its date and time, with a record (or an older run's report)
    private static bool IsRun(string dir) => Regex.IsMatch(Path.GetFileName(dir), @"^\d{4}-\d{2}-\d{2}_\d{4}") && HasRecord(dir);

    // A record, or an older run's report (in an older run, run.json was its options, with no events beside it)
    private static bool HasRecord(string dir) =>
        File.Exists(Path.Combine(dir, RunRecord.FileName)) && File.Exists(Path.Combine(dir, EventLog.FileName)) ||
        File.Exists(Path.Combine(dir, "report.json"));

    // The last runs of a benchmarks folder, newest first: when, what, how it ended, its main numbers
    internal static int Runs(string folder, int count)
    {
        var all = Directory.Exists(folder)
            ? Directory.GetDirectories(folder).Where(IsRun).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList()
            : new List<string>();
        if (all.Count == 0)
        {
            Console.WriteLine("No runs yet.");
            return 0;
        }
        var md = new StringBuilder("| Run | Test | Mode | Result | FPS | GPU ms | Warp days | Settings |\n|---|---|---|---|---:|---:|---:|---|\n");
        foreach (var dir in all.Take(count))
        {
            if (RunRecord.Read(dir) is not { } r)
                continue;
            var h = r.Headline();
            var preset = (r.Fact("Preset") ?? "") switch { "none, own settings" => "own", "none, a fresh install's settings" => "defaults", var named => named };
            var settings = string.Join(", ", new[] { preset, r.Fact("Settings changed") is { Length: > 0 } c && c != "none" ? c : "" }.Where(x => x.Length > 0));
            md.AppendLine($"| {Path.GetFileName(dir)} | {Pages.TestName(r.Test)} | {r.Mode} | {(r.Status == Status.Passed ? "passed" : r.Status == Status.Stopped ? "stopped" : "**failed**")} | " +
                          $"{(h.Fps is { } fps ? fps.ToString("F0") : "")} | {(h.GpuMs is { } gpu ? gpu.ToString("F1") : "")} | {(h.WarpDays > 0 ? h.WarpDays.ToString("F1") : "")} | {settings} |");
        }
        Console.Write(Terminal.Layout(md.ToString()));
        Console.WriteLine();
        Console.WriteLine($"{Math.Min(count, all.Count)} of {all.Count} runs.");
        return 0;
    }
}
