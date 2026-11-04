// Named runs to compare with (baselines.json in the benchmarks folder): a name, such as "main", stands for one run of each
// test, so `./benchmark.sh check` compares a new Mixed Test with main's Mixed Test, and `compare main` with the baseline of
// the last run's test.
//
//   baseline                         the names and their runs
//   baseline set <name> [run...]     those runs (default: the last one) under that name, each for its test
//   baseline remove <name>           forget the name (the runs stay)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    private static string BaselinesFile => Path.Combine(Benchmarks, "baselines.json");

    private static readonly JsonSerializerOptions BaselinesJson = new() { WriteIndented = true };

    // Name -> test -> the run's folder name
    private static Dictionary<string, Dictionary<string, string>> ReadBaselines()
    {
        try
        {
            return File.Exists(BaselinesFile)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(BaselinesFile)) ?? []
                : [];
        }
        catch (JsonException)
        {
            Output.Warn($"{BaselinesFile} can't be read: no baselines.");
            return [];
        }
    }

    private static void WriteBaselines(Dictionary<string, Dictionary<string, string>> baselines)
    {
        Directory.CreateDirectory(Benchmarks);
        File.WriteAllText(BaselinesFile, JsonSerializer.Serialize(baselines, BaselinesJson) + "\n");
    }

    // The run a baseline's name stands for in that test, or null
    private static string? Baseline(string name, string test) =>
        ReadBaselines().GetValueOrDefault(name)?.GetValueOrDefault(test) is { } run && Directory.Exists(Path.Combine(Benchmarks, run))
            ? Path.Combine(Benchmarks, run)
            : null;

    private static int BaselineCommand(string[] args)
    {
        var baselines = ReadBaselines();
        switch (args)
        {
            case []:
            {
                if (baselines.Count == 0)
                {
                    Output.Info("No baselines yet: ./benchmark.sh baseline set main names the last run main.");
                    return 0;
                }
                var md = new StringBuilder("| Name | Test | Run |\n|---|---|---|\n");
                foreach (var (name, tests) in baselines.OrderBy(b => b.Key, StringComparer.Ordinal))
                    foreach (var (test, run) in tests.OrderBy(t => t.Key, StringComparer.Ordinal))
                        md.AppendLine($"| {name} | {Pages.TestName(test)} | {run} |");
                Console.Write(Terminal.Layout(md.ToString()));
                return 0;
            }
            case ["set", var name, .. var runs]:
            {
                if (name.StartsWith('-') || name is "last" or "previous")
                    Die($"a baseline needs a name of its own, not '{name}'.");
                var tests = baselines.GetValueOrDefault(name) ?? [];
                foreach (var run in runs.Length > 0 ? runs : ["last"])
                {
                    var dir = RunFolder(run);
                    if (!Directory.Exists(dir) || Path.GetDirectoryName(Path.GetFullPath(dir)) != Path.GetFullPath(Benchmarks))
                        Die($"a baseline is a run in {Benchmarks}, not {run}.");
                    var record = RunRecord.Read(dir);
                    if (record == null || record.Test is "places" or "full")
                        throw new PortException($"{run} is not a run of one test (mixed, tour or warp): a baseline stands for one test.");
                    if (record.Status != Status.Passed)
                        Output.Warn($"{Path.GetFileName(dir)} did not pass: it is the baseline all the same.");
                    tests[record.Test] = Path.GetFileName(dir);
                    Output.Info($"{name}'s {Pages.TestName(record.Test)}: {Path.GetFileName(dir)}");
                }
                baselines[name] = tests;
                WriteBaselines(baselines);
                return 0;
            }
            case ["remove", var name]:
                if (!baselines.Remove(name))
                    Die($"no baseline '{name}'.");
                WriteBaselines(baselines);
                Output.Info($"Forgot {name} (its runs stay).");
                return 0;
            default:
                Die("baseline [set <name> [run...] | remove <name>]");
                return 1;
        }
    }
}
