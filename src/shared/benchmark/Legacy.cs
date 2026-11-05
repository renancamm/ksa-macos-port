// A run made before run.json was its record: its report.json (schema 2: system.txt's facts, sections of windows,
// problems and notes as text, fast or cinematic mode) read into a RunRecord, so that its page, its listing and a
// comparison with it work as with a new run.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KsaPort.Common.Benchmark;

public static class Legacy
{
    public static RunRecord? Read(string dir)
    {
        var path = Path.Combine(dir, "report.json");
        JsonObject? report;
        try
        {
            report = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
        if (report == null)
            return null;
        var sections = (report["sections"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var system = report["system"] as JsonObject ?? [];
        var record = new RunRecord
        {
            Schema = RunRecord.CurrentSchema,
            Name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/')),
            Started = DateTime.TryParseExact(Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/')) is { Length: >= 15 } n ? n[..15] : "", "yyyy-MM-dd_HHmm",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var named) ? named : Directory.GetCreationTime(dir),
            Test = (string?)system["Test"] ?? "places",
            Mode = ModeOf((string?)report["mode"] ?? "cinematic", sections.Select(s => (string?)s["name"] ?? "").ToList()),
            Status = report["passed"]?.GetValue<bool>() == true ? Status.Passed : report["stopped"]?.GetValue<bool>() == true ? Status.Stopped : Status.Failed,
            Start = (string?)system["Start"] ?? "",
            Sessions = report["runs"]?.GetValue<int>() ?? 1,
            LoadSeconds = Number(report["loadSeconds"]),
            Drawable = (string?)report["drawable"],
            Facts = system.Select(kv => new Fact(kv.Key == "Sections" ? "Scenarios" : kv.Key, kv.Value?.ToString() ?? "")).ToList(),
        };
        // The game's trouble reaching its servers failed a run then; it is a note now
        foreach (var p in (report["problems"] as JsonArray ?? []).Select(p => (string?)p ?? ""))
            record.Findings.Add(new Finding(p.Contains("error reaching master server", StringComparison.Ordinal) ? Severity.Note : Severity.Fail, "legacy", p));
        if (record.Status == Status.Failed && !record.Problems.Any())
            record.Status = Status.Passed;
        record.Findings.AddRange((report["notes"] as JsonArray ?? []).Select(p => new Finding(Severity.Note, "legacy", (string?)p ?? "")));
        var kinds = record.Mode.Split(' ');
        foreach (var section in sections)
        {
            var name = (string?)section["name"] ?? "";
            bool live = section["live"]?.GetValue<bool>() == true;
            // The mixed section stood still, places were jumped to; the rest flew
            var kind = live ? Kind.Timed : name == "mixed" || kinds.Contains("jump") ? Kind.Views : Kind.Flight;
            var scenario = new ScenarioRecord { Name = name, Kind = kind };
            foreach (var w in (section["windows"] as JsonArray ?? []).OfType<JsonObject>())
                scenario.Parts.Add(Part(w));
            var limits = section["shotLimits"] as JsonObject ?? [];
            foreach (var shot in (section["screenshots"] as JsonArray ?? []).Select(s => (string?)s ?? ""))
                scenario.Shots.Add(new ShotRecord(shot, $"{name}_{shot}.png", Number(limits[shot]) ?? NoiseProfile.DefaultShotLimit(kind)));
            record.Scenarios.Add(scenario);
        }
        return record;
    }

    // The mode as the kinds name it now: the mixed section was the Mixed Test's fast form, the warp sections were timed, a
    // fast run of the flight jumped to its places
    public static string ModeOf(string mode, IReadOnlyList<string> sections) =>
        mode is not ("fast" or "cinematic") ? mode
        : sections.Count > 0 && sections.All(n => n.StartsWith("warp", StringComparison.Ordinal)) ? "timed"
        : sections.Count > 0 && sections.All(n => n == "mixed") ? "views"
        : mode == "fast" ? "jump" : "flight";

    private static PartRecord Part(JsonObject w)
    {
        var part = new PartRecord
        {
            Name = (string?)w["name"] ?? "",
            Runs = (int)(Number(w["runs"]) ?? 1),
            Frames = (int)(Number(w["frames"]) ?? 0),
            Seconds = Number(w["seconds"]) ?? 0,
            AverageFps = Number(w["averageFps"]) ?? 0,
            FrameMsMedian = Number(w["frameMsMedian"]) ?? 0,
            FrameMsP95 = Number(w["frameMsP95"]) ?? 0,
            FrameMsP99 = Number(w["frameMsP99"]) ?? 0,
            FrameMsMax = Number(w["frameMsMax"]) ?? 0,
            Hitches = (int)(Number(w["hitches"]) ?? 0),
            GpuMsMedian = Number(w["gpuMsMedian"]),
            GpuMsP95 = Number(w["gpuMsP95"]),
            DrawsMedian = Number(w["drawsMedian"]),
            GpuMemoryPeakMb = (long?)Number(w["gpuMemoryPeakMb"]),
            FootprintPeakMb = (long?)Number(w["footprintPeakMb"]),
            ThermalMax = (int?)Number(w["thermalMax"]),
            ShadersCompiled = (int?)Number(w["shadersCompiled"]),
            PipelinesCreated = (int?)Number(w["pipelinesCreated"]),
            FrameMsRange = Range(w["frameMsRange"]),
            GpuMsRange = Range(w["gpuMsRange"]),
        };
        if (Number(w["gameDays"]) is { } days)
            part.Warp = new WarpRecord
            {
                GameDays = days,
                Speed = Number(w["speed"]) ?? 0,
                SpeedAsked = Number(w["speedAsked"]) ?? 0,
                SecondsToSpeed = Number(w["secondsToSpeed"]),
                SimMsMax = Number(w["simMsMax"]) ?? 0,
                Substeps = (long)(Number(w["substeps"]) ?? 0),
                BubblesMax = (int)(Number(w["bubblesMax"]) ?? 0),
                VehiclesMax = (int)(Number(w["vehiclesMax"]) ?? 0),
                SpeedRange = Range(w["speedRange"]),
            };
        return part;
    }

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static double[]? Range(JsonNode? node) =>
        node is JsonArray { Count: 2 } a && Number(a[0]) is { } lo && Number(a[1]) is { } hi ? [lo, hi] : null;
}
