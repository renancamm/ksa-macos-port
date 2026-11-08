// Whether a number moved between two sides (two runs, an A/B run's sides, a study's variants), on what it can stand on:
//
//   sessions    both sides have game sessions of their own (a sample is a session's median: frames and passes within a
//               session aren't independent, and the Mac's heat ties them): Mann-Whitney U, Cliff's delta and the
//               Hodges-Lehmann interval of the ratio that goes with the test, as Mozilla's PerfCompare and BenchmarkDotNet
//               judge. Changed: p under 0.05, an effect that isn't negligible (|delta| from 0.147) and a move of 1 % or
//               more for the GPU and frame times, whose limits are a run's noise (the sessions' own spread is the noise
//               here), the metric's own limit for the others (the least change that matters: a comparison tests a dozen
//               numbers a part). Under 5 sessions a side the verdict is inconclusive, never the same
//   quick read  one session a side with passes (benchmark.sh measure): the passes' ranges don't overlap and the move is
//               past the part's limit, 2 % at least
//   screen      one measuring on a side (check against the baseline): a move past the limit (a test's noise profile, or
//               the metric's own), and for a number that drifts with the Mac's heat past ScreenDrift too: runs played at
//               other times move that much on their own; a study confirms
using System;
using System.Collections.Generic;
using System.Linq;

namespace KsaPort.Common.Benchmark;

public enum Basis { Screen, QuickRead, Sessions }

public enum Change { Same, Better, Worse, Inconclusive }

// One metric of one part on both sides: their numbers, the ratio of B to A, what the verdict stands on and its outcome,
// and with sessions the test's numbers: p, Cliff's delta, the ratio's 95 % interval and the sessions on each side
public sealed record Verdict(Metric Metric, double A, double B, Basis Basis, Change Change, double Limit, double? P = null, double? Delta = null,
    double[]? Interval = null, int SessionsA = 0, int SessionsB = 0)
{
    public double Ratio => A > 0 ? B / A : 1;
    public double Moved => Ratio - 1;
    public bool Changed => Change is Change.Better or Change.Worse;
}

public static class Statistics
{
    public const int SessionsNeeded = 5;
    public const double Alpha = 0.05, NegligibleDelta = 0.147, QuickReadMove = 0.02, SessionsMove = 0.01;
    // How far a time or an energy moves between two runs of the same build played at other times (percent): the Mac's
    // heat and the apps sharing its GPU (two runs of one build on 2026-11-08, two hours apart: +4 to +10 % on every
    // Mixed part)
    public const double ScreenDrift = 10;

    public static string Name(Basis basis) => basis switch
    {
        Basis.Sessions => "sessions",
        Basis.QuickRead => "quick read",
        _ => "screen",
    };

    // How it compares: by sessions when both sides have more than one, by passes when both measured more than once in one,
    // else as a screen
    public static Basis BasisOf(PartRecord a, PartRecord b, Metric m)
    {
        if (a.Sessions(m).Length > 1 && b.Sessions(m).Length > 1)
            return Basis.Sessions;
        return a.Samples(m).Length > 1 && b.Samples(m).Length > 1 ? Basis.QuickRead : Basis.Screen;
    }

    // B's number against A's; null when one side lacks it, or the metric isn't judged
    public static Verdict? Judge(PartRecord a, PartRecord b, Metric m)
    {
        // A number neither better nor worse is shown, never judged (a run may carry a limit for it from before)
        if (a[m] is not { } x || b[m] is not { } y || m.Limit is not { } own || m.Better == Better.Neither)
            return null;
        // A tail of a few frames is its worst frame or two
        if (m.MinFrames > 0 && (a.Frames < m.MinFrames || b.Frames < m.MinFrames))
            return null;
        // A part's limits from its test's noise profile: GPU time's, else frame time's own (in a record of before, a part
        // without GPU time has its frame time's as its limit)
        double? FrameLimit(PartRecord p) => p.FrameLimit ?? (p[Metrics.GpuBusy] == null ? p.Limit : null);
        var (la, lb) = m == Metrics.FrameTime ? (FrameLimit(a), FrameLimit(b)) : (a.Limit, b.Limit);
        var limit = m.Calibrated && (la ?? lb) != null ? Math.Max(la ?? 0, lb ?? 0) : own;
        var basis = BasisOf(a, b, m);
        // Moved past a share of where it was (a count from 0 moves when it moves by its floor)
        bool Past(double from, double to, double share) => from > 0 ? Math.Abs(to - from) / from > share : to > from && m.Floor > 0;
        Change Direction(bool moved, double from, double to) =>
            !moved || Math.Abs(to - from) < m.Floor ? Change.Same : (to < from) == (m.Better == Better.Lower) ? Change.Better : Change.Worse;
        switch (basis)
        {
            case Basis.Sessions:
            {
                var (sa, sb) = (a.Sessions(m), b.Sessions(m));
                double p = MannWhitney(sa, sb), delta = CliffsDelta(sa, sb);
                var interval = RatioInterval(sa, sb);
                // The medians of the sessions, as the test sees them
                double ma = Measure.Median(sa), mb = Measure.Median(sb);
                double least = m.Calibrated ? SessionsMove * 100 : own;
                var change = sa.Length < SessionsNeeded || sb.Length < SessionsNeeded
                    ? Change.Inconclusive
                    : Direction(p < Alpha && Math.Abs(delta) >= NegligibleDelta && Past(ma, mb, least / 100), ma, mb);
                return new Verdict(m, ma, mb, basis, change, least, p, delta, interval, sa.Length, sb.Length);
            }
            case Basis.QuickRead:
            {
                var (ra, rb) = (a.Range(m)!, b.Range(m)!);
                // A calibrated number without its profile: the passes' own spread is the noise, 2 %
                limit = m.Calibrated && la == null && lb == null ? QuickReadMove * 100 : Math.Max(limit, QuickReadMove * 100);
                return new Verdict(m, x, y, basis, Direction(Past(x, y, limit / 100) && (rb[1] < ra[0] || rb[0] > ra[1]), x, y), limit);
            }
            default:
                if (m.Drifts)
                    limit = Math.Max(limit, ScreenDrift);
                return new Verdict(m, x, y, basis, Direction(Past(x, y, limit / 100), x, y), limit);
        }
    }

    // Mann-Whitney U's two-sided p for B against A: exact over every split of the samples (ties as mid-ranks) when there
    // are few enough, else the normal approximation with the ties' correction
    public static double MannWhitney(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n1 = a.Count, n2 = b.Count, n = n1 + n2;
        if (n1 == 0 || n2 == 0)
            return 1;
        var all = a.Concat(b).ToArray();
        var ranks = Ranks(all);
        double expected = n1 * (n + 1) / 2.0;
        double observed = Math.Abs(Enumerable.Range(0, n1).Sum(i => ranks[i]) - expected);
        if (Choose(n, n1) <= 200_000)
        {
            long extreme = 0, total = 0;
            foreach (var sum in RankSums(ranks, n1))
            {
                total++;
                if (Math.Abs(sum - expected) >= observed - 1e-9)
                    extreme++;
            }
            return (double)extreme / total;
        }
        double u = Enumerable.Range(0, n1).Sum(i => ranks[i]) - n1 * (n1 + 1) / 2.0;
        double ties = all.GroupBy(x => x).Sum(g => Math.Pow(g.Count(), 3) - g.Count());
        double sigma = Math.Sqrt(n1 * n2 / 12.0 * (n + 1 - ties / (n * (double)(n - 1))));
        if (sigma <= 0)
            return 1;
        double z = (Math.Abs(u - n1 * n2 / 2.0) - 0.5) / sigma;
        return Math.Min(1, 2 * (1 - NormalCdf(Math.Max(z, 0))));
    }

    // Cliff's delta of B against A: the share of pairs where B is higher, less the share where it is lower (-1 to 1)
    public static double CliffsDelta(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        int more = 0, less = 0;
        foreach (var x in a)
            foreach (var y in b)
            {
                if (y > x)
                    more++;
                else if (y < x)
                    less++;
            }
        return (more - less) / (double)(a.Count * b.Count);
    }

    // The 95 % interval of B's ratio to A that goes with Mann-Whitney's test (Hodges-Lehmann): of the ratios of every B
    // sample to every A sample, the ones between the test's critical counts of the lowest and the highest, so that an
    // interval that leaves out 1 is a change the test finds, and the other way round. Null with too few samples for
    // any interval at 95 % (3 a side), or a sample at 0
    public static double[]? RatioInterval(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        int n1 = a.Count, n2 = b.Count;
        if (n1 < 2 || n2 < 2 || a.Any(x => x <= 0) || b.Any(y => y <= 0))
            return null;
        var logs = a.SelectMany(x => b.Select(y => Math.Log(y / x))).Order().ToArray();
        int k = CriticalCount(n1, n2, (1 - 0.95) / 2);
        if (k < 1)
            return null;
        return [Math.Round(Math.Exp(logs[k - 1]), 4), Math.Round(Math.Exp(logs[^k]), 4)];
    }

    // The most pairs that may lie below an interval: the largest k with P(U < k) at most the tail's share, U being Mann-
    // Whitney's count for samples of these sizes with nothing between them (exact: the counts of every rank order; for
    // large samples the normal approximation)
    private static int CriticalCount(int n1, int n2, double tail)
    {
        int most = n1 * n2;
        if (Choose(n1 + n2, n1) > 1e12)
        {
            double mean = most / 2.0, sigma = Math.Sqrt(n1 * n2 * (n1 + n2 + 1) / 12.0);
            // 1.959964: the normal distribution's 97.5 % point
            return Math.Max(0, (int)Math.Floor(mean - 1.959964 * sigma + 0.5));
        }
        // ways[i, j][u]: the orders of i A samples and j B samples with u pairs where B is the lower
        var ways = new double[n1 + 1, n2 + 1][];
        for (int i = 0; i <= n1; i++)
            for (int j = 0; j <= n2; j++)
            {
                var w = ways[i, j] = new double[i * j + 1];
                if (i == 0 || j == 0)
                {
                    w[0] = 1;
                    continue;
                }
                // The largest sample is an A one (it is above all j B samples) or a B one
                for (int u = 0; u < w.Length; u++)
                    w[u] = (u >= j && u - j < ways[i - 1, j].Length ? ways[i - 1, j][u - j] : 0) + (u < ways[i, j - 1].Length ? ways[i, j - 1][u] : 0);
            }
        var all = ways[n1, n2];
        double total = all.Sum(), below = 0;
        int k = 0;
        while (k < all.Length && (below + all[k]) / total <= tail)
            below += all[k++];
        return k;
    }

    // Each value's rank from 1, ties sharing their mean rank
    private static double[] Ranks(double[] values)
    {
        var order = Enumerable.Range(0, values.Length).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Length];
        for (int i = 0; i < order.Length;)
        {
            int j = i;
            while (j + 1 < order.Length && values[order[j + 1]] == values[order[i]])
                j++;
            for (int k = i; k <= j; k++)
                ranks[order[k]] = (i + j) / 2.0 + 1;
            i = j + 1;
        }
        return ranks;
    }

    // The sum of the ranks of every choice of k of them
    private static IEnumerable<double> RankSums(double[] ranks, int k)
    {
        var chosen = new int[k];
        for (int i = 0; i < k; i++)
            chosen[i] = i;
        while (true)
        {
            double sum = 0;
            foreach (var i in chosen)
                sum += ranks[i];
            yield return sum;
            int at = k - 1;
            while (at >= 0 && chosen[at] == ranks.Length - k + at)
                at--;
            if (at < 0)
                yield break;
            chosen[at]++;
            for (int i = at + 1; i < k; i++)
                chosen[i] = chosen[i - 1] + 1;
        }
    }

    private static double Choose(int n, int k)
    {
        double c = 1;
        for (int i = 1; i <= k; i++)
            c = c * (n - k + i) / i;
        return c;
    }

    // The standard normal distribution's cumulative probability (Abramowitz and Stegun 7.1.26, error under 1.5e-7)
    private static double NormalCdf(double z)
    {
        double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
        double erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t *
            Math.Exp(-z * z / 2);
        return z >= 0 ? (1 + erf) / 2 : (1 - erf) / 2;
    }
}
