// The game's profiler zones in a run (the director's GameProfilers reads them from the game): every zone's time summed per
// part into the run's store, and the last seconds of zones, KSA Metal's waits and its presents' GPU times kept, so that a
// slow frame can be told by what took its time.
//
// A source is one thread of the game's profilers (the main thread, a job runner); a group is what its numbers add up under
// (main, VehicleSolver...). Each frame of a source is a tree of zones with their start, length and the bytes allocated in
// them; the length of a zone minus its children's is its own (self) time.
//
//   zone_stats  per part and group, each zone: the game frames it was in, the calls, total and self ms over the part, the
//               median, 95th percentile and worst of its time in a game frame (frames without it count as 0), MB allocated
//   moments     a frame a trigger kept (Triggers.cs; by default a measured part's of 100 ms or more, 20 a pass): when,
//               how long, the part (none: ""), and its cause: the three zones of the main thread's slowest frame there with
//               the most self time, KSA Metal's waits of 2 ms or more in it, and the most GPU time a present around it took
//   slices      every zone around a kept moment (the first MaxSliced of the measured passes): from half a second before the
//               slow frame to a quarter of a second after it, each with its start on the store's clock, length, own time
//               and depth
//
// The game's main thread only hands frames over: a part's sums and a moment are written by a task of their own, so
// writing them makes no slow frame, and a moment while no part is open, so that its work (its allocations, a collection
// they bring, a first compile) isn't in a part's numbers. A source's frame comes in an array of the profilers' pool
// (GameProfilers), given back when the ring lets it go, unless a moment still holds it. The game's profilers count time
// in 32-bit ticks of a nanosecond: a zone of more than 4.29 s reads short.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KsaPort.Common.Benchmark;

// A zone of a source's frame: its name's id, its parent's place in the frame (-1: the frame itself), its start on the
// store's clock in seconds, its length in milliseconds and the bytes allocated in it
public readonly record struct ZoneSample(int Zone, int Parent, double Start, double Ms, long Bytes);

// A source's frame: its group, the thread it ran on, and its zones, the frame's own root first: the first Count of
// Samples, an array of ArrayPool's when Pooled
public sealed record ZoneFrame(string Group, string Thread, ZoneSample[] Samples, int Count, bool Pooled = false)
{
    public ZoneFrame(string group, string thread, ZoneSample[] samples) : this(group, thread, samples, samples.Length)
    {
    }

    public ArraySegment<ZoneSample> Zones => new(Samples, 0, Count);
    public double Start => Count > 0 ? Samples[0].Start : 0;
    public double End => Count > 0 ? Samples[0].Start + Samples[0].Ms / 1000 : 0;
    // A moment's writing holds it: its array isn't given back
    public bool Kept { get; set; }
}

// The part a game frame's numbers count in, as the frames table names it
public readonly record struct PartKey(int Session, int Pass, string Scenario, string Part);

public sealed class ZoneLog
{
    // Moments of the measured passes with their zones written as slices; later ones, and the warm-up's, keep their cause
    public const int MaxSliced = 10;
    private const double Before = 0.5, After = 0.25, Keep = 2, CauseWaitMs = 2;

    private readonly RunStore? _store;
    // Names zones by their ids; called from the writing tasks, so it must be safe on any thread
    private readonly Func<int, string> _name;
    // This game frame's time per (group, zone), and the zones' calls, self time and bytes
    private readonly Dictionary<(string Group, int Zone), (double Ms, double Self, int Calls, long Bytes)> _frame = new();
    // The part being summed: per (group, zone) each game frame's time, and the totals
    private PartKey? _part;
    private int _partFrames;
    private Dictionary<(string Group, int Zone), PartZone> _sums = new();
    private readonly Queue<ZoneFrame> _ring = new();
    private readonly Queue<(double Start, double Ms, string What)> _waits = new();
    private readonly Queue<(double T, double GpuMs)> _presents = new();
    private readonly List<(double At, double FrameMs, PartKey Part)> _pending = new();
    // Moments of the open part, written when it closes
    private readonly List<Action> _later = new();
    private readonly List<Task> _writing = new();
    // Each zone's children's time in a frame Add counts (the main thread's own)
    private double[] _children = new double[256];
    private int _moments, _sliced;

    private sealed class PartZone
    {
        public readonly List<double> PerFrame = new();
        public int Calls;
        public double Total, Self;
        public long Bytes;
    }

    public ZoneLog(RunStore? store, Func<int, string> name) => (_store, _name) = (store, name);

    // A source's frame that ended
    public void Add(ZoneFrame frame)
    {
        _ring.Enqueue(frame);
        if (_children.Length < frame.Count)
            _children = new double[frame.Count * 2];
        var children = Children(frame, _children);
        for (int i = 0; i < frame.Count; i++)
        {
            var z = frame.Samples[i];
            var key = (frame.Group, z.Zone);
            var (ms, self, calls, bytes) = _frame.GetValueOrDefault(key);
            _frame[key] = (ms + z.Ms, self + Math.Max(0, z.Ms - children[i]), calls + 1, bytes + z.Bytes);
        }
    }

    // A wait KSA Metal timed, and a present's GPU time: what a moment's cause names besides the zones
    public void AddWait(double start, double ms, string what) => _waits.Enqueue((start, ms, what));

    public void AddPresent(double t, double gpuMs) => _presents.Enqueue((t, gpuMs));

    // A game frame ended, at t on the store's clock: what its sources added counts in this part (none: in no part); the
    // rings keep the last seconds, and moments whose quarter second after has passed are written
    public void EndFrame(double t, PartKey? part)
    {
        if (part != _part)
        {
            WritePart();
            // The closed part's moments, now that their work no longer falls in it
            foreach (var later in _later)
                Write(later);
            _later.Clear();
            _part = part;
        }
        if (part != null)
        {
            foreach (var (key, f) in _frame)
            {
                if (!_sums.TryGetValue(key, out var sum))
                    _sums[key] = sum = new PartZone();
                // Frames before this zone's first count as 0
                while (sum.PerFrame.Count < _partFrames)
                    sum.PerFrame.Add(0);
                sum.PerFrame.Add(f.Ms);
                sum.Calls += f.Calls;
                sum.Total += f.Ms;
                sum.Self += f.Self;
                sum.Bytes += f.Bytes;
            }
            _partFrames++;
        }
        _frame.Clear();
        while (_ring.Count > 0 && _ring.Peek().End < t - Keep)
        {
            var old = _ring.Dequeue();
            if (old.Pooled && !old.Kept)
                System.Buffers.ArrayPool<ZoneSample>.Shared.Return(old.Samples);
        }
        while (_waits.Count > 0 && _waits.Peek().Start < t - Keep)
            _waits.Dequeue();
        while (_presents.Count > 0 && _presents.Peek().T < t - Keep)
            _presents.Dequeue();
        for (int i = 0; i < _pending.Count; i++)
        {
            if (t < _pending[i].At + After)
                continue;
            WriteMoment(_pending[i]);
            _pending.RemoveAt(i--);
        }
        _writing.RemoveAll(w => w.IsCompleted);
    }

    // A frame kept as a moment, which ended at t: written once the zones after it are in
    public void Hitch(double t, double frameMs, PartKey part) => _pending.Add((t, frameMs, part));

    // The run's end: the part and the moments left, and everything written
    public void Close()
    {
        WritePart();
        _part = null;
        foreach (var p in _pending)
            WriteMoment(p);
        _pending.Clear();
        foreach (var later in _later)
            Write(later);
        _later.Clear();
        Task.WaitAll(_writing.ToArray(), TimeSpan.FromSeconds(30));
        _writing.Clear();
    }

    // In a task of its own, which a failure ends quietly: the run goes on without that part's zones
    private void Write(Action write) => _writing.Add(Task.Run(() =>
    {
        try
        {
            write();
        }
        catch (Exception)
        {
        }
    }));

    private void WritePart()
    {
        var (part, frames, sums) = (_part, _partFrames, _sums);
        _sums = new();
        _partFrames = 0;
        if (part is not { } p || frames == 0)
            return;
        Write(() =>
        {
            foreach (var ((group, zone), sum) in sums)
            {
                while (sum.PerFrame.Count < frames)
                    sum.PerFrame.Add(0);
                var sorted = sum.PerFrame.ToArray();
                Array.Sort(sorted);
                int present = sum.PerFrame.Count(x => x > 0);
                _store?.AddZoneStats(p, group, _name(zone), present, sum.Calls, sum.Total, sum.Self, Percentile(sorted, 50),
                    Percentile(sorted, 95), sorted[^1], sum.Bytes / 1048576.0);
            }
        });
    }

    private void WriteMoment((double At, double FrameMs, PartKey Part) m)
    {
        double from = m.At - m.FrameMs / 1000 - Before, to = m.At + After;
        var frames = _ring.Where(f => f.End >= from && f.Start <= to).ToArray();
        foreach (var f in frames)
            f.Kept = true;
        var waits = _waits.Where(w => w.Start <= m.At && w.Start + w.Ms / 1000 >= m.At - m.FrameMs / 1000 && w.Ms >= CauseWaitMs).ToArray();
        double gpu = _presents.Where(p => p.T >= m.At - m.FrameMs / 1000 && p.T <= m.At + 0.1).Select(p => p.GpuMs).DefaultIfEmpty(0).Max();
        int id = ++_moments;
        bool sliced = m.Part.Pass > 0 && _sliced < MaxSliced;
        if (sliced)
            _sliced++;
        // Written when no part is open (its own, or one that opened since, would count its work)
        Action write = () =>
        {
            // The main thread's frame of the slow frame, the last that ended by its end (the one before ends a frame earlier,
            // within the clocks' slack), and its zones with the most self time
            var main = frames.Where(f => f.Group == "main" && f.End <= m.At + 0.002 && f.End >= m.At - m.FrameMs / 1000 - 0.002)
                .OrderByDescending(f => f.End).FirstOrDefault();
            var cause = new List<string>();
            if (main != null)
            {
                var children = Children(main, new double[main.Count]);
                cause.AddRange(main.Zones.Select((z, i) => (Name: i == 0 ? "(the frame's own)" : _name(z.Zone), Self: z.Ms - children[i]))
                    .GroupBy(x => x.Name).Select(g => (g.Key, Self: g.Sum(x => x.Self))).OrderByDescending(x => x.Self).Take(3)
                    .Select(x => FormattableString.Invariant($"{x.Key} {x.Self:F1} ms")));
            }
            cause.AddRange(waits.GroupBy(w => w.What).Select(g => (g.Key, Ms: g.Sum(w => w.Ms))).OrderByDescending(w => w.Ms)
                .Select(w => FormattableString.Invariant($"waited in {w.Key} {w.Ms:F1} ms")));
            if (gpu > 0)
                cause.Add(FormattableString.Invariant($"GPU up to {gpu:F1} ms a frame"));
            _store?.AddMoment(id, m.At, m.FrameMs, m.Part, string.Join("; ", cause));
            if (!sliced)
                return;
            foreach (var f in frames)
            {
                var depth = new int[f.Count];
                var children = Children(f, new double[f.Count]);
                for (int i = 0; i < f.Count; i++)
                {
                    var z = f.Zones[i];
                    depth[i] = z.Parent >= 0 && z.Parent < i ? depth[z.Parent] + 1 : 0;
                    _store?.AddSlice(id, f.Group, f.Thread, _name(z.Zone), depth[i], z.Start, z.Ms, Math.Max(0, z.Ms - children[i]), z.Bytes);
                }
            }
        };
        if (_part != null)
            _later.Add(write);
        else
            Write(write);
    }

    // Each zone's children's time, summed, into a buffer of at least the frame's zones
    private static double[] Children(ZoneFrame frame, double[] children)
    {
        Array.Clear(children, 0, frame.Count);
        for (int i = frame.Count - 1; i >= 0; i--)
            if (frame.Samples[i].Parent >= 0 && frame.Samples[i].Parent < i)
                children[frame.Samples[i].Parent] += frame.Samples[i].Ms;
        return children;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        int i = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }
}
