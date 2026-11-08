// Two sides compared part by part (two runs, or a study's variant against its baseline): each part's verdicts
// (Statistics.cs) and the zones that moved, and one sentence for them all. A part is decided by its GPU busy where both
// sides have it (frame times sit on the display's beat), else its frame time; a time warp part by the speed it reached.
// The sentence names each part that moved, the others' other numbers that moved another way (a frame time up with the
// GPU's the same, a P99, memory), and is good when nothing judged got worse. The zones name where a change went: the
// GPU's zones when both sides have them (benchmark.sh profile), else the main thread's for a frame time.
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
    IReadOnlyList<ZoneMove> Zones, bool GpuZones, bool Steps = false)
{
    public string Name => A.Name;

    // The other numbers that moved: not the one that decides, nor a time that moved its way (a P99 with its GPU time); on a
    // screen not one that drifts with the Mac's heat and load (one run a side can't tell it from a change: two runs of one
    // build moved CPU busy 0.6 ms); with Vsync (Steps) not the numbers paced by the display in a part its GPU time decides
    // (frame time, its tail, latency, energy), which jump a display's step when the GPU's time crosses one
    public IEnumerable<Verdict> Others => Verdicts.Where(v => v.Changed && v != Decides && !(v.Basis == Basis.Screen && v.Metric.Drifts) &&
        !(Decides is { Changed: true } d && v.Metric.Unit == "ms" && d.Metric.Unit == "ms" && v.B > v.A == d.B > d.A) &&
        !(Steps && Decides?.Metric == Metrics.GpuBusy && Paced.Contains(v.Metric)));

    private static readonly Metric[] Paced = [Metrics.FrameTime, Metrics.FrameP99, Metrics.Low1, Metrics.Latency, Metrics.GpuEnergy];

    // One of its other numbers as a line: "mixed kitten Memory 8000 to 9000 MB (12 % higher)"
    public string Line(Verdict v) => $"{Scenario} {Name} {v.Metric.Label} {v.Metric.Format(v.A)} to {v.Metric.Format(v.B)} {v.Metric.Unit}".TrimEnd() +
                                     (v.A > 0 ? $" ({Comparison.Text(v)})" : "");

    // Whether a change of its GPU time could be named by GPU zones that neither side has
    public bool ZonesWanted => Decides is { Changed: true } d && d.Metric == Metrics.GpuBusy && !GpuZones;

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

    // steps: either side played with Vsync, so its frame times sit on the display's steps
    public static Comparison Of(IEnumerable<(string Scenario, PartRecord Part)> a, IEnumerable<(string Scenario, PartRecord Part)> b, bool steps = false)
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
            (pa.Warped ? c.Warps : c.Parts).Add(Part(s, pa, pb) with { Steps = steps });
        }
        c.OnlyB.AddRange(bs.Keys.Where(k => !seen.Contains(k)).Select(k => $"{k.Item1} {k.Item2}"));
        return c;
    }

    private static PartComparison Part(string scenario, PartRecord a, PartRecord b)
    {
        var verdicts = Metrics.Judged.Select(m => Statistics.Judge(a, b, m)).OfType<Verdict>().ToList();
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

    // The parts' ratio of B to A as one: their geometric mean (each part counts the same; a part at zero on a side has
    // none and is left out)
    private static double Ratio(IEnumerable<Verdict> verdicts) =>
        verdicts.Where(v => v.A > 0 && v.B > 0).ToList() is { Count: > 0 } usable ? Math.Exp(usable.Average(v => Math.Log(v.Ratio))) : 1;

    private static string Pct(double moved) => string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(moved) * 100:F0} %");

    private static bool Worse(Verdict v) => v.Change == Change.Worse;

    // One sentence for the numbers: GPU time over the parts (frame time for the parts without it), then time warp's speed,
    // each with what it stands on and the parts that moved; then the other numbers that moved; and whether nothing judged
    // got worse (null: nothing to say)
    public (string Text, bool? Good) Sentence(string? image = null, bool imageSame = true)
    {
        var words = new List<string>();
        if (image != null)
            words.Add(image);
        bool good = imageSame;
        void Say(string what, List<(PartComparison Part, Verdict Verdict)> decided, string more, string less)
        {
            if (decided.Count == 0)
                return;
            var verdicts = decided.Select(d => d.Verdict).ToList();
            var basis = verdicts.Min(v => v.Basis);
            string on = basis switch
            {
                Basis.Sessions => $"{verdicts.Min(v => Math.Min(v.SessionsA, v.SessionsB))} sessions a side",
                Basis.QuickRead => "quick read",
                _ => "screen",
            };
            var moved = decided.Where(d => d.Verdict.Changed).ToList();
            good &= !moved.Any(d => Worse(d.Verdict));
            string Way(Verdict v) => v.B > v.A ? more : less;
            if (moved.Count > 3)
            {
                // The counts first: parts that went both ways can leave the parts' mean where it was
                double ratio = Ratio(verdicts);
                int up = moved.Count(d => d.Verdict.B > d.Verdict.A);
                var mean = Math.Abs(ratio - 1) >= 0.005 ? $", {Pct(ratio - 1)} {(ratio > 1 ? more : less)} over the parts" : "";
                words.Add($"{what}: {moved.Count - up} of {decided.Count} parts {less}, {up} {more}{mean} ({on})");
            }
            else if (moved.Count > 0)
            {
                int others = decided.Count - moved.Count;
                words.Add($"{what} {string.Join(", ", moved.Select(d => $"{Pct(d.Verdict.Moved)} {Way(d.Verdict)} in {d.Part.Name}"))}" +
                          $"{(others > 0 ? $", {others} other part{(others > 1 ? "s" : "")} within noise" : "")} ({on})");
            }
            else if (verdicts.Any(v => v.Change == Change.Inconclusive))
                words.Add($"{what} inconclusive ({on}, {Statistics.SessionsNeeded} needed)");
            else if (basis == Basis.Sessions && verdicts.Select(v => v.Interval).OfType<double[]>().ToList() is { Count: > 0 } intervals)
                // What the sessions can't tell apart: the parts' intervals, as a change from A
                words.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{what}: no change shown ({on}; the parts' 95 % intervals {(intervals.Min(i => i[0]) - 1) * 100:+0;-0;0} to {(intervals.Max(i => i[1]) - 1) * 100:+0;-0;0} %)"));
            else
                words.Add($"{what} within noise ({on})");
        }
        var byGpu = Parts.Where(p => p.Decides?.Metric == Metrics.GpuBusy).Select(p => (p, p.Decides!)).ToList();
        var byFrame = Parts.Where(p => p.Decides is { } d && d.Metric != Metrics.GpuBusy).Select(p => (p, p.Decides!)).ToList();
        Say("GPU time", byGpu, "higher", "lower");
        Say(byGpu.Count > 0 ? "frame time (parts without GPU time)" : "frame time", byFrame, "higher", "lower");
        Say("time warp", Warps.Where(p => p.Decides != null).Select(p => (p, p.Decides!)).ToList(), "faster", "slower");
        // The other numbers that moved, by metric: in which parts
        foreach (var metric in Parts.Concat(Warps).SelectMany(p => p.Others.Select(v => (p, v))).GroupBy(x => x.v.Metric))
        {
            var list = metric.ToList();
            good &= !list.Any(x => Worse(x.v));
            var named = list.Take(3).Select(x => $"{Text(x.v)} in {x.p.Name}");
            words.Add($"{metric.Key.Label} {string.Join(", ", named)}{(list.Count > 3 ? $" and {list.Count - 3} more parts" : "")}");
        }
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
        // A count from 0 has no share to move by
        if (v.A <= 0 && v.Changed)
            return $"{v.Metric.Format(v.A)} to {v.Metric.Format(v.B)} {v.Metric.Unit}".TrimEnd();
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

// Whether two runs' numbers stand on the same ground: the facts of the Mac and the window that move every number, and
// the run's notes that do (a GPU busy before it, the Mac hot or just rested, on battery); compare and a study say them
// next to their verdicts
public static class Ground
{
    // The facts that make two runs' numbers unlike when they differ; a study's sessions of one variant also share its build
    private static readonly string[] MacFacts = ["Chip", "GPU cores", "Memory", "macOS", "Power", "Low Power Mode"];
    // What the run played in: a study's variant may set these on purpose
    private static readonly string[] PlayFacts = ["Window", "Vsync"];
    private static readonly string[] BuildFacts = ["Port", "KSA"];

    // The notes that move a run's numbers (Checks.cs)
    private static readonly HashSet<string> NumberNotes =
        ["gpu-busy", "hot", "rested", "warming", "battery", "low-power", "swap", "pressure", "tools", "probe-cost", "profiled", "disturbed", "shaders", "in-game"];

    // The facts that differ between the runs, with each run's value: the Mac's, and with play what it played in (the window,
    // Vsync, the size of the images drawn), with build the app's versions
    public static List<(string Key, string[] Values)> Unlike(IReadOnlyList<RunRecord> runs, bool play = true, bool build = false)
    {
        var unlike = new List<(string, string[])>();
        foreach (var key in MacFacts.Concat(play ? PlayFacts : []).Concat(build ? BuildFacts : []))
        {
            var values = runs.Select(r => r.Fact(key) ?? "?").ToArray();
            if (values.Distinct().Count() > 1)
                unlike.Add((key, values));
        }
        var drawn = runs.Select(r => r.Drawable ?? "?").ToArray();
        if (play && drawn.Distinct().Count() > 1)
            unlike.Add(("Drawn", drawn));
        return unlike;
    }

    // The run's notes that move its numbers
    public static IEnumerable<Finding> Notes(RunRecord run) => run.Notes.Where(n => NumberNotes.Contains(n.Code));

    // Whether a run played with Vsync: its frame times sit on the display's steps
    public static bool Steps(RunRecord run) => run.Fact("Vsync")?.StartsWith("true", StringComparison.Ordinal) == true;
}
