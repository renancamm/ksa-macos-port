// A run's store as a timeline for Perfetto's viewer (ui.perfetto.dev): a TrackEvent trace (timeline.pftrace) and the
// same as a script (timeline.js) that the report's Open Timeline button hands to the viewer (Pages.cs; a page opened from
// disk can load a script beside it, not read a file). The viewer runs in the browser: the trace goes from the page to
// it, on this Mac, and nowhere else. The writer is ours, the few messages of Perfetto's protos it needs (pinned to v50.1:
// protos/perfetto/trace/), since no .NET writer exists.
//
// Its tracks, under one for the run: the parts as they were measured, the run's events, frame time and GPU busy, each
// frame from its start to its present (CPU) and to the GPU's end (GPU) and its time on screen, KSA Metal's waits by
// kind, the game's zones around the slow frames the triggers kept (a track per thread), memory, and the counters (the
// GPU's power and clock from benchmark.sh, the probes'), a track each. Every time is the
// store's clock (the Mac's monotonic clock) in nanoseconds, so an Instruments trace of the same run lines up.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KsaPort.Common.Benchmark;

public static class Timeline
{
    public const string TraceFile = "timeline.pftrace", ScriptFile = "timeline.js";

    // The run's timeline next to its store: the trace and its script; false when the run has no store
    public static bool Write(string dir)
    {
        var db = Path.Combine(dir, RunStore.GameFile);
        if (!File.Exists(db))
            return false;
        var trace = Make(dir);
        File.WriteAllBytes(Path.Combine(dir, TraceFile), trace);
        var run = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        File.WriteAllText(Path.Combine(dir, ScriptFile), $"window.ksaTimeline = {{ name: {System.Text.Json.JsonSerializer.Serialize(run)}, trace: '{Convert.ToBase64String(trace)}' }};\n");
        return true;
    }

    // A slice on a track, from start to end in seconds, at a depth of a tree of them (0: a flat track's)
    private readonly record struct Slice(ulong Track, double Start, double End, string Name, int Depth = 0, string? Text = null);

    private sealed class Tracks
    {
        private ulong _next = 10;
        public readonly List<Proto> Descriptors = new();
        private readonly Dictionary<ulong, (string Name, ulong Parent, int Rank)> _made = new();
        private readonly Dictionary<(ulong, int), ulong> _lanes = new();
        private int _rank;

        public ulong Add(string name, ulong parent, string? counterUnit = null, int? rank = null)
        {
            var uuid = _next++;
            int r = rank ?? (_rank += 10);
            var d = new Proto().Varint(1, uuid).Varint(5, parent).String(2, name).Varint(12, (ulong)r);
            if (counterUnit != null)
                d.Message(8, new Proto().String(6, counterUnit));
            Descriptors.Add(d);
            _made[uuid] = (name, parent, r);
            return uuid;
        }

        // A flat track's lane beside it, for slices that overlap ("Frame: present to GPU end 2"); lane 0 is the track
        public ulong Lane(ulong track, int lane)
        {
            if (lane == 0)
                return track;
            if (!_lanes.TryGetValue((track, lane), out var uuid))
            {
                var (name, parent, rank) = _made[track];
                _lanes[(track, lane)] = uuid = Add($"{name} {lane + 1}", parent, rank: rank + lane);
            }
            return uuid;
        }
    }

    private const ulong Root = 1;

    public static byte[] Make(string dir)
    {
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        var game = Path.Combine(dir, RunStore.GameFile);
        var tracks = new Tracks();
        var slices = new List<Slice>();
        var instants = new List<(ulong Track, double T, string Name)>();
        var counters = new List<(ulong Track, double T, double Value)>();

        // The parts, from their open and close events, both processes' events on one track
        ulong parts = tracks.Add("Parts", Root), events = tracks.Add("Events", Root);
        var open = new Dictionary<(long, long, string, string), double>();
        foreach (var file in new[] { Path.Combine(dir, RunStore.HostFile), game })
            RunStore.Query(file, "SELECT t, session, pass, type, scenario, part, text FROM events ORDER BY rowid", q =>
            {
                double t = q.Double(0);
                string type = q.Text(3), scenario = q.Text(4), part = q.Text(5), text = q.Text(6);
                var key = (q.Long(1), q.Long(2), scenario, part);
                if (type == "open")
                    open[key] = t;
                else if (type == "close" && open.Remove(key, out var from))
                    slices.Add(new Slice(parts, from, t, $"{scenario} {part}", Text: $"session {q.Long(1)}, pass {q.Long(2)}"));
                else if (type is not ("heartbeat" or "open" or "close"))
                    instants.Add((events, t, string.Join(" ", new[] { type, scenario, part, text }.Where(x => x.Length > 0))));
            });

        // Each frame: its time, the GPU's for its present, the memory
        ulong frameMs = tracks.Add("Frame time", Root, "ms"), gpuMs = tracks.Add("GPU busy", Root, "ms");
        ulong cpu = tracks.Add("Frame: start to present", Root), gpu = tracks.Add("Frame: present to GPU end", Root), screen = tracks.Add("On screen", Root);
        ulong footprint = tracks.Add("Memory footprint", Root, "MB"), metal = tracks.Add("GPU memory", Root, "MB"), gc = tracks.Add("GC pause", Root, "ms");
        long lastPresent = -1;
        // GPU busy: each frame's own, when its present completed (frame_gpu); a run of before has the frames' column
        bool own = false;
        RunStore.Query(game, "SELECT t, gpu_ms FROM frame_gpu ORDER BY t", q =>
        {
            own = true;
            counters.Add((gpuMs, q.Double(0), q.Double(1)));
        });
        RunStore.Query(game, "SELECT t, frame_ms, present, gpu_ms, footprint_mb, metal_mb, gc_pause_ms FROM frames ORDER BY rowid", q =>
        {
            double t = q.Double(0);
            counters.Add((frameMs, t, q.Double(1)));
            if (q.NullableDouble(3) is { } g && q.Long(2) != lastPresent)
            {
                lastPresent = q.Long(2);
                if (!own)
                    counters.Add((gpuMs, t, g));
                counters.Add((footprint, t, q.Long(4)));
                counters.Add((metal, t, q.Long(5)));
            }
            if (q.NullableDouble(6) is > 0 and var pause)
                counters.Add((gc, t, pause));
        });
        // The counters of both files (the host's power channel, the probes'), a track each with its metric's unit; a value
        // the same as the one before left out
        var units = RunStore.ReadMetrics(dir).ToDictionary(m => m.Name, m => (m.Label, m.Unit));
        var byName = new Dictionary<string, (ulong Track, double Last)>();
        ulong counterGroup = 0;
        foreach (var file in new[] { Path.Combine(dir, RunStore.HostFile), game })
            RunStore.Query(file, "SELECT t, name, value FROM counters ORDER BY t", q =>
            {
                string name = q.Text(1);
                double value = q.Double(2);
                if (!byName.TryGetValue(name, out var c))
                {
                    if (counterGroup == 0)
                        counterGroup = tracks.Add("Counters", Root);
                    var (label, unit) = units.GetValueOrDefault(name, (name, ""));
                    c = (tracks.Add(label == name ? name : $"{label} ({name})", counterGroup, unit.Length > 0 ? unit : "value"), double.NaN);
                }
                else if (c.Last == value)
                    return;
                counters.Add((c.Track, q.Double(0), value));
                byName[name] = (c.Track, value);
            });
        double lastShown = 0;
        // In the order they began: each game session counts its presents from 1
        RunStore.Query(game, "SELECT present, began, presented, gpu_end, shown FROM shown ORDER BY began", q =>
        {
            long present = q.Long(0);
            double began = q.Double(1), presented = q.Double(2), gpuEnd = q.Double(3), shown = q.Double(4);
            if (began > 0 && presented > began)
                slices.Add(new Slice(cpu, began, presented, $"frame {present}"));
            if (presented > 0 && gpuEnd > presented)
                slices.Add(new Slice(gpu, presented, gpuEnd, $"present {present}"));
            if (shown > 0)
            {
                if (lastShown > 0 && shown > lastShown)
                    slices.Add(new Slice(screen, lastShown, shown, "shown"));
                lastShown = shown;
            }
        });

        // KSA Metal's waits, a track for each kind
        var waits = new Dictionary<string, ulong>();
        ulong waitGroup = tracks.Add("Waits", Root);
        RunStore.Query(game, "SELECT t, ms, what FROM waits ORDER BY t", q =>
        {
            var what = q.Text(2);
            if (!waits.TryGetValue(what, out var track))
                waits[what] = track = tracks.Add(what, waitGroup);
            slices.Add(new Slice(track, q.Double(0), q.Double(0) + q.Double(1) / 1000, what, Text: $"{q.Double(1):0.0} ms"));
        });
        RunStore.Query(game, "SELECT t, bytes FROM memory ORDER BY t", q =>
            instants.Add((events, q.Double(0), $"{(q.Long(1) > 0 ? "allocated" : "freed")} {Math.Abs(q.Long(1)) >> 20} MB")));

        // The slow frames, and the game's zones around them: a track for each thread
        RunStore.Query(game, "SELECT t, frame_ms, scenario, part, cause FROM moments ORDER BY t", q =>
            instants.Add((events, q.Double(0), $"slow frame {q.Double(1):0} ms {q.Text(2)} {q.Text(3)}: {q.Text(4)}")));
        var threads = new Dictionary<string, ulong>();
        ulong zoneGroup = 0;
        var seen = new HashSet<(string, string, string, long, double)>();
        RunStore.Query(game, "SELECT grp, thread, zone, depth, start, ms, self_ms FROM slices ORDER BY start", q =>
        {
            string group = q.Text(0), thread = q.Text(1), zone = q.Text(2);
            double start = q.Double(4);
            // Two moments close together share their zones: each once
            if (!seen.Add((group, thread, zone, q.Long(3), start)))
                return;
            var key = $"{group} · {thread}";
            if (!threads.TryGetValue(key, out var track))
            {
                if (zoneGroup == 0)
                    zoneGroup = tracks.Add("Zones around slow frames", Root);
                threads[key] = track = tracks.Add(key, zoneGroup);
            }
            slices.Add(new Slice(track, start, start + q.Double(5) / 1000, zone, (int)q.Long(3) + 1, $"own {q.Double(6):0.00} ms"));
        });

        // The events first: they add the lanes the tracks need
        var packets = Events(slices, instants, counters, tracks).ToList();
        var trace = new Proto();
        trace.Message(1, Packet().Message(60, new Proto().Varint(1, Root).String(2, $"KSA Metal benchmark {name}").Varint(11, 3)));
        foreach (var d in tracks.Descriptors)
            trace.Message(1, Packet().Message(60, d));
        foreach (var e in packets)
            trace.Message(1, e);
        return trace.ToArray();
    }

    private static Proto Packet() => new Proto().Varint(10, 1);

    // Every event as a packet, in time order: on one track, a slice's end before a later one's start at the same time,
    // the deeper one first. A flat track's slice that overlaps the one before it goes to a lane beside it; a tree's slice
    // is clipped into its parent and after its sibling, so that every begin has its end in order
    private static IEnumerable<Proto> Events(List<Slice> slices, List<(ulong Track, double T, string Name)> instants,
        List<(ulong Track, double T, double Value)> counters, Tracks tracks)
    {
        var all = new List<(ulong Ns, int Order, Proto Packet)>();
        static ulong Ns(double seconds) => (ulong)Math.Max(0, Math.Round(seconds * 1e9));
        void Emit(ulong track, double start, double end, Slice s)
        {
            // A slice shorter than a nanosecond would end before it begins in the order below
            if (Ns(end) <= Ns(start))
                return;
            var begin = new Proto().Varint(9, 1).Varint(11, track).String(23, s.Name);
            if (s.Text != null)
                begin.Message(4, new Proto().String(10, "about").String(6, s.Text));
            all.Add((Ns(start), 1000 + s.Depth, Packet().Varint(8, Ns(start)).Message(11, begin)));
            all.Add((Ns(end), -s.Depth, Packet().Varint(8, Ns(end)).Message(11, new Proto().Varint(9, 2).Varint(11, track))));
        }
        foreach (var group in slices.GroupBy(s => s.Track))
        {
            var ends = new List<double>();
            var open = new List<Slice>();
            var lastEnd = new Dictionary<int, double>();
            foreach (var s in group.OrderBy(s => s.Start).ThenBy(s => s.Depth))
            {
                if (s.Depth == 0)
                {
                    int lane = ends.FindIndex(e => e <= s.Start);
                    if (lane < 0)
                    {
                        lane = ends.Count;
                        ends.Add(0);
                    }
                    ends[lane] = s.End;
                    Emit(tracks.Lane(s.Track, lane), s.Start, s.End, s);
                    continue;
                }
                while (open.Count > 0 && (open[^1].Depth >= s.Depth || open[^1].End <= s.Start))
                    open.RemoveAt(open.Count - 1);
                var (start, end) = (Math.Max(s.Start, lastEnd.GetValueOrDefault(s.Depth)), s.End);
                if (open.Count > 0)
                    (start, end) = (Math.Max(start, open[^1].Start), Math.Min(end, open[^1].End));
                if (end <= start)
                    continue;
                lastEnd[s.Depth] = end;
                // A deeper level starts over inside this slice
                foreach (var deeper in lastEnd.Keys.Where(d => d > s.Depth).ToList())
                    lastEnd.Remove(deeper);
                var clipped = s with { Start = start, End = end };
                open.Add(clipped);
                Emit(s.Track, start, end, clipped);
            }
        }
        foreach (var (track, t, name) in instants)
            all.Add((Ns(t), 500, Packet().Varint(8, Ns(t)).Message(11, new Proto().Varint(9, 3).Varint(11, track).String(23, name))));
        foreach (var (track, t, value) in counters)
            all.Add((Ns(t), 500, Packet().Varint(8, Ns(t)).Message(11, new Proto().Varint(9, 4).Varint(11, track).Double(44, value))));
        // Stable: events of one time keep the order they were made in
        return all.Select((e, i) => (e, i)).OrderBy(x => x.e.Ns).ThenBy(x => x.e.Order).ThenBy(x => x.i).Select(x => x.e.Packet);
    }

    // A protobuf message, written field by field
    private sealed class Proto
    {
        private readonly MemoryStream _bytes = new();

        public Proto Varint(int field, ulong value)
        {
            Key(field, 0);
            Raw(value);
            return this;
        }

        public Proto Double(int field, double value)
        {
            Key(field, 1);
            _bytes.Write(BitConverter.GetBytes(value));
            return this;
        }

        public Proto String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _bytes.ToArray();

        private Proto Bytes(int field, byte[] bytes)
        {
            Key(field, 2);
            Raw((ulong)bytes.Length);
            _bytes.Write(bytes);
            return this;
        }

        private void Key(int field, int wire) => Raw((ulong)(field << 3 | wire));

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _bytes.WriteByte((byte)value);
        }
    }
}
