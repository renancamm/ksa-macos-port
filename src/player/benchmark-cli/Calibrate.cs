// calibrate [test] [n]: how much a test moves between runs of the same build on this Mac, as its noise profile. It
// plays the test n times (3 unless said) at a fresh install's settings without Vsync (--capped: with the game's own),
// after a run that warms the Mac up and is left out (a first run's GPU reads about 7 % fast while the chip warms:
// three runs in a row on 2026-11-08, the first at 1,508 MHz and 13.3 W, the next two at 1,400 MHz and 11 W), each run a
// game of its own as a comparison sets two runs against each other, then takes for each screenshot the most of its
// blocks that changed between any two of the runs and for each part how far its GPU and frame times spread, and writes
// the limits (NoiseProfile.Margin times the spread) to noise/<test>.json in the benchmarks folder. Runs and comparisons
// on this Mac use it from then on; copied into src/game/benchmark/lineup/noise/ it ships with the app.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    private static int Calibrate(string[] args)
    {
        string test = "mixed";
        int n = 3;
        var passOn = new List<string>();
        foreach (var a in args)
        {
            if (a is "mixed" or "tour")
                test = a;
            else if (a == "warp")
                Die("the Warp Test has no noise profile: its screenshots are not compared, and its parts are the speed reached in time warp.");
            else if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count is >= 2 and <= 20)
                n = count;
            else if (a is "--app" or "--system" or "--capped" || passOn.LastOrDefault() is "--app" or "--system")
                passOn.Add(a);
            else
                Die($"calibrate [mixed | tour] [runs, 2 to 20] [--app <path>] [--system <name>] [--capped], not '{a}'.");
        }
        Output.Step($"Calibrating the {Pages.TestName(test)}: {n} runs at a fresh install's settings{(passOn.Contains("--capped") ? "" : " without Vsync")}, " +
                    "each a game of its own, after one that warms the Mac up");
        var runs = new List<(string Dir, RunRecord Record)>();
        for (int i = 0; i <= n; i++)
        {
            Output.Info(i == 0 ? "Warming up (left out)" : $"Run {i} of {n}");
            var run = new BenchmarkRun();
            run.Run([test, "--defaults", .. passOn], Purpose.Calibrate);
            if (i > 0 && run.Folder is { Length: > 0 } dir && RunRecord.Read(dir) is { Status: Status.Passed } record)
                runs.Add((dir, record));
            else if (i > 0)
                Output.Warn($"run {i} did not pass: it is left out.");
            else if (!(run.Folder is { Length: > 0 } w && RunRecord.Read(w) is { Status: Status.Passed }))
                Output.Warn("the warm-up did not pass: run 1 may read a cold Mac.");
            if (run.Stopped)
                Die("stopped: no profile written.");
        }
        if (runs.Count < 2)
            Die($"{runs.Count} of the {n} runs passed: a profile needs two at least.");
        var profile = Profile(test, runs);
        // The benchmarks folder holds it as the app's lineup folder does: noise/<test>.json
        var folder = Benchmarks;
        profile.Write(folder);
        Console.WriteLine();
        Console.Write(Terminal.Layout(ProfileTable(profile)));
        Console.WriteLine();
        Output.Step($"The {Pages.TestName(test)}'s noise profile: {NoiseProfile.PathFor(folder, test)}");
        Output.Info("Runs and comparisons on this Mac use it from now on. To ship it with the app, copy it into src/game/benchmark/lineup/noise/.");
        Output.Info(FormattableString.Invariant($"Runs played minutes apart move less than runs played hours apart: one run against another marks a time past its limit or {Statistics.ScreenDrift:0} %, whichever is larger."));
        return 0;
    }

    private static NoiseProfile Profile(string test, List<(string Dir, RunRecord Record)> runs)
    {
        var first = runs[0].Record;
        var profile = new NoiseProfile
        {
            Test = test,
            Made = string.Create(CultureInfo.InvariantCulture,
                $"benchmark.sh calibrate, {DateTime.Now:yyyy-MM-dd}, {runs.Count} runs on {first.Fact("Chip") ?? "?"} {first.Fact("Memory") ?? ""}, port {first.Fact("Port") ?? "?"}, KSA {first.Fact("KSA") ?? "?"}, Vsync {first.Fact("Vsync") ?? "?"}"),
            Runs = runs.Select(r => Path.GetFileName(r.Dir)).ToList(),
        };
        // Each screenshot: the most of its blocks that changed between any two runs
        foreach (var shot in first.Scenarios.Where(s => s.Kind != Kind.Timed).SelectMany(s => s.Shots))
        {
            double most = 0;
            for (int i = 0; i < runs.Count; i++)
                for (int j = i + 1; j < runs.Count; j++)
                    if (Images.Read(Path.Combine(runs[i].Dir, shot.File)) is { } a && Images.Read(Path.Combine(runs[j].Dir, shot.File)) is { } b)
                        most = Math.Max(most, Images.Compare(a, b).ChangedPercent);
            profile.Shots[shot.Name] = new ShotNoise { Spread = Math.Round(most, 2), Limit = Math.Round(NoiseProfile.Margin * most, 2) };
        }
        // Each part: how far its GPU and frame times spread across the runs, against their median
        foreach (var (scenario, part) in first.AllParts().Where(x => !x.Part.Warped))
        {
            var mine = runs.Select(r => r.Record.Scenario(scenario.Name)?.Part(part.Name)).OfType<PartRecord>().ToList();
            static double Spread(IEnumerable<double> values)
            {
                var v = values.OrderBy(x => x).ToArray();
                return v.Length < 2 || v[v.Length / 2] <= 0 ? 0 : (v[^1] - v[0]) / v[v.Length / 2] * 100;
            }
            double gpu = Spread(mine.Select(p => p[Metrics.GpuBusy]).OfType<double>()), frame = Spread(mine.Select(p => p[Metrics.FrameTime]).OfType<double>());
            // A comparison judges GPU time where the runs have it, frame time where they don't; never under what two
            // runs of anything differ by: 2 %
            var judged = mine.All(p => p[Metrics.GpuBusy] != null) ? gpu : frame;
            profile.Parts[part.Name] = new PartNoise
            {
                GpuSpread = Math.Round(gpu, 2), FrameSpread = Math.Round(frame, 2), Limit = Math.Round(Math.Max(NoiseProfile.Margin * judged, 2), 1),
                FrameLimit = Math.Round(Math.Max(NoiseProfile.Margin * frame, 2), 1),
            };
        }
        return profile;
    }

    private static string ProfileTable(NoiseProfile profile)
    {
        var md = new StringBuilder("| Screenshot | Spread % | Limit % |\n|---|---:|---:|\n");
        foreach (var (name, shot) in profile.Shots)
            md.AppendLine(FormattableString.Invariant($"| {name} | {shot.Spread:0.00} | {shot.Limit:0.##} |"));
        md.Append("\n| Part | GPU spread % | Frame spread % | Limit % |\n|---|---:|---:|---:|\n");
        foreach (var (name, part) in profile.Parts)
            md.AppendLine(FormattableString.Invariant($"| {name} | {part.GpuSpread:0.0} | {part.FrameSpread:0.0} | {part.Limit:0.#} |"));
        return md.ToString();
    }
}
