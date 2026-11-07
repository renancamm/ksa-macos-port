// Two sides compared part by part (two runs, or an A/B run's sides): each part's verdicts (Statistics.cs) and the zones
// that moved, and one sentence for them all. A part is decided by its GPU busy where both sides have it (frame times sit
// on the display's beat), else its frame time; a time warp part by the speed it reached. The zones name where a change
// went: the GPU's zones when both sides have them (benchmark.sh profile), else the main thread's for a frame time.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsaPort.Common.Benchmark;

// A zone on both sides, its own ms a frame
public sealed record ZoneMove(string Name, double A, double B)
{
    public double Delta => B - A;
    public string Label => Metrics.ZoneLabel(Name);
}

// A part on both sides: the verdict that decides it, every judged metric's, and the zones that moved with it
public sealed record PartComparison(string Scenario, PartRecord A, PartRecord B, Verdict? Decides, IReadOnlyList<Verdict> Verdicts,
    IReadOnlyList<ZoneMove> Zones, bool GpuZones)
{
    public string Name => A.Name;

    // "earth4 GPU busy +1.4 ms: Clouds Raymarching +1.2, Ocean +0.3"
    public string Line()
    {
        if (Decides is not { } d)
            return $"{Scenario} {Name}";
        var text = string.Create(CultureInfo.InvariantCulture, $"{Scenario} {Name} {d.Metric.Label} {d.B - d.A:+0.0;-0.0;0.0} {d.Metric.Unit}");
        return Zones.Count == 0 ? text : text + ": " + string.Join(", ", Zones.Select(z => string.Create(CultureInfo.InvariantCulture, $"{z.Label} {z.Delta:+0.00;-0.00}")));
    }
}

public sealed class Comparison
{
    // A zone counts as moved from this much own time a frame, and from this share of its part's change
    private const double ZoneFloorMs = 0.05, ZoneShare = 0.1;
    private const int ZonesNamed = 3;

    public List<PartComparison> Parts { get; } = [];
    public List<PartComparison> Warps { get; } = [];
    // The parts only one side has
    public List<string> OnlyA { get; } = [];
    public List<string> OnlyB { get; } = [];

    // The weakest basis of the parts' verdicts: what the comparison as a whole stands on
    public Basis Basis => Parts.Concat(Warps).Select(p => p.Decides?.Basis).OfType<Basis>().DefaultIfEmpty(Basis.Screen).Min();

    public static Comparison Of(IEnumerable<(string Scenario, PartRecord Part)> a, IEnumerable<(string Scenario, PartRecord Part)> b)
    {
        var c = new Comparison();
        var bs = new Dictionary<(string, string), PartRecord>();
        foreach (var (s, p) in b)
            bs.TryAdd((s, p.Name), p);
        var seen = new HashSet<(string, string)>();
        foreach (var (s, pa) in a)
        {
            if (!seen.Add((s, pa.Name)))
                continue;
            if (!bs.TryGetValue((s, pa.Name), out var pb))
            {
                c.OnlyA.Add($"{s} {pa.Name}");
                continue;
            }
            (pa.Warped ? c.Warps : c.Parts).Add(Part(s, pa, pb));
        }
        c.OnlyB.AddRange(bs.Keys.Where(k => !seen.Contains(k)).Select(k => $"{k.Item1} {k.Item2}"));
        return c;
    }

    private static PartComparison Part(string scenario, PartRecord a, PartRecord b)
    {
        var verdicts = Metrics.All.Select(m => Statistics.Judge(a, b, m)).OfType<Verdict>().ToList();
        var decides = a.Warped ? verdicts.FirstOrDefault(v => v.Metric == Metrics.Speed)
            : verdicts.FirstOrDefault(v => v.Metric == Metrics.GpuBusy) ?? verdicts.FirstOrDefault(v => v.Metric == Metrics.FrameTime);
        bool gpu = decides?.Metric == Metrics.GpuBusy;
        // The profiled passes' zones when both have them, the measured passes' otherwise
        bool profiled = a.Profiled != null && b.Profiled != null;
        var prefix = gpu ? Metrics.GpuZones : Metrics.MainZones;
        var zones = decides == null || a.Warped ? [] : Moves(a.Zones(prefix, profiled), b.Zones(prefix, profiled), decides.B - decides.A);
        bool gpuZones = a.Zones(Metrics.GpuZones, profiled).Any() && b.Zones(Metrics.GpuZones, profiled).Any();
        return new PartComparison(scenario, a, b, decides, verdicts, zones, gpuZones);
    }

    // The zones that went the way their part did, the most first: at most three, each past the floors
    private static List<ZoneMove> Moves(IEnumerable<(string Name, double Ms)> a, IEnumerable<(string Name, double Ms)> b, double change)
    {
        var (za, zb) = (a.ToDictionary(z => z.Name, z => z.Ms), b.ToDictionary(z => z.Name, z => z.Ms));
        if (za.Count == 0 || zb.Count == 0)
            return [];
        double floor = Math.Max(ZoneFloorMs, ZoneShare * Math.Abs(change));
        return za.Keys.Union(zb.Keys).Select(n => new ZoneMove(n, za.GetValueOrDefault(n), zb.GetValueOrDefault(n)))
            .Where(z => Math.Abs(z.Delta) >= floor && (change == 0 || Math.Sign(z.Delta) == Math.Sign(change)))
            .OrderByDescending(z => Math.Abs(z.Delta)).Take(ZonesNamed).ToList();
    }

    // A move this large over the parts, with a part that changed that way, is the comparison's: per basis
    private static double Threshold(Basis basis) => basis switch { Basis.Screen => 0.05, Basis.QuickRead => 0.02, _ => 0.01 };

    // The parts' ratio of B to A as one: their geometric mean (each part counts the same; a part at zero on a side has
    // none and is left out)
    private static double Ratio(IEnumerable<Verdict> verdicts) =>
        verdicts.Where(v => v.A > 0 && v.B > 0).ToList() is { Count: > 0 } usable ? Math.Exp(usable.Average(v => Math.Log(v.Ratio))) : 1;

    // One sentence for the numbers: GPU time over the parts, then time warp's speed, each with what it stands on; and
    // whether nothing got worse (null: nothing to say)
    public (string Text, bool? Good) Sentence(string? image = null, bool imageSame = true)
    {
        var words = new List<string>();
        if (image != null)
            words.Add(image);
        bool good = imageSame;
        void Say(string what, List<Verdict> verdicts, string more, string less)
        {
            if (verdicts.Count == 0)
                return;
            var basis = verdicts.Min(v => v.Basis);
            double ratio = Ratio(verdicts);
            bool up = ratio > 1;
            bool moved = Math.Abs(ratio - 1) >= Threshold(basis) && verdicts.Any(v => v.Changed && v.B > v.A == up);
            string on = basis switch
            {
                Basis.Sessions => $"{verdicts.Min(v => Math.Min(v.SessionsA, v.SessionsB))} sessions a side",
                Basis.QuickRead => "quick read",
                _ => "screen",
            };
            if (moved)
            {
                words.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {Math.Abs(ratio - 1) * 100:F0} % {(up ? more : less)} ({on})"));
                bool worse = verdicts[0].Metric.Better == Better.Lower ? up : !up;
                good &= !worse;
            }
            else if (verdicts.Any(v => v.Change == Change.Inconclusive))
                words.Add($"{what} inconclusive ({on}, {Statistics.SessionsNeeded} needed)");
            else
                words.Add($"{what} within noise ({on})");
        }
        Say("GPU time", Parts.Select(p => p.Decides).OfType<Verdict>().Where(v => v.Metric == Metrics.GpuBusy).ToList(), "higher", "lower");
        if (!Parts.Any(p => p.Decides?.Metric == Metrics.GpuBusy))
            Say("Frame time", Parts.Select(p => p.Decides).OfType<Verdict>().ToList(), "higher", "lower");
        Say("time warp", Warps.Select(p => p.Decides).OfType<Verdict>().ToList(), "faster", "slower");
        if (words.Count == 0)
            return ("Nothing to compare", null);
        var text = string.Join(", ", words);
        return (char.ToUpperInvariant(text[0]) + text[1..], good);
    }

    // How a verdict reads in a table: "12 % slower" marked, "+2 %" within noise, "+8 % ?" inconclusive
    public static string Text(Verdict v)
    {
        bool lowerIsBetter = v.Metric.Better != Better.Higher;
        var pct = string.Create(CultureInfo.InvariantCulture, $"{v.Moved * 100:+0;-0;0} %");
        // Times and speeds go faster or slower, other numbers higher or lower
        bool pace = v.Metric.Unit is "ms" or "x" or "fps";
        return v.Change switch
        {
            Change.Better or Change.Worse when pace => string.Create(CultureInfo.InvariantCulture,
                $"{Math.Abs(v.Moved) * 100:F0} % {(v.B < v.A == lowerIsBetter ? "faster" : "slower")}"),
            Change.Better or Change.Worse => string.Create(CultureInfo.InvariantCulture,
                $"{Math.Abs(v.Moved) * 100:F0} % {(v.B > v.A ? "higher" : "lower")}"),
            Change.Inconclusive => pct + " ?",
            _ => pct,
        };
    }
}
