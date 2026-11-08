// A test's noise profile (lineup/noise/<test>.json): how much each screenshot and each part's numbers moved between
// repeated runs of the same build on the same Mac, and the limits a comparison takes from that, so that a change shows
// and noise doesn't (Statistics.cs applies them). benchmark.sh calibrate measures it; the build copies the profiles into
// the app next to the lineup.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsaPort.Common.Benchmark;

public sealed class NoiseProfile
{
    // How many times the measured spread a limit is (the spread of a few runs underestimates the next run's)
    public const double Margin = 3;

    // A screenshot's limit when its profile has none: the share of its blocks that may change (percent, Images.cs). A
    // views scenario stands still and comes out the same; a flight's vary a little with the frames the Mac happened to
    // draw on the way; a timed scenario's game clock is wherever the game got to, so its screenshots are not compared
    public static double DefaultShotLimit(Kind kind) => kind switch { Kind.Views => 1, Kind.Flight => 3, _ => 100 };

    // A part's number counts as changed past this (percent) when its profile has none
    public const double DefaultPartLimit = 5;

    public string Test { get; set; } = "";
    // When and how it was made: the runs it was made from, the app's version and the Mac
    public string Made { get; set; } = "";
    public List<string> Runs { get; set; } = [];
    public Dictionary<string, ShotNoise> Shots { get; set; } = [];
    public Dictionary<string, PartNoise> Parts { get; set; } = [];

    public double ShotLimit(string shot, Kind kind) =>
        kind == Kind.Timed ? 100 : Shots.TryGetValue(shot, out var n) ? Math.Max(n.Limit, DefaultShotLimit(kind)) : DefaultShotLimit(kind);

    public double PartLimit(string part) => Parts.TryGetValue(part, out var n) ? n.Limit : DefaultPartLimit;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string PathFor(string lineupFolder, string test) => System.IO.Path.Combine(lineupFolder, "noise", $"{test}.json");

    // The profile of that test in the lineup's folder, or an empty one
    public static NoiseProfile Read(string lineupFolder, string test)
    {
        var path = PathFor(lineupFolder, test);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<NoiseProfile>(File.ReadAllText(path), Json) ?? new() { Test = test } : new() { Test = test };
        }
        catch (JsonException)
        {
            return new() { Test = test };
        }
    }

    public void Write(string lineupFolder)
    {
        var path = PathFor(lineupFolder, Test);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + "\n");
    }
}

// A screenshot: the most of its blocks that changed between two of the runs (percent), and its limit
public sealed class ShotNoise
{
    public double Spread { get; set; }
    public double Limit { get; set; }
}

// A part: how far its median GPU and frame times spread across the runs (percent of their median), the limit of the one a
// comparison judges it by (GPU time where the runs have it) and the frame time's own (null in a profile of before)
public sealed class PartNoise
{
    public double GpuSpread { get; set; }
    public double FrameSpread { get; set; }
    public double Limit { get; set; }
    public double? FrameLimit { get; set; }
}
