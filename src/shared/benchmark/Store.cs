// A run's store: everything a run recorded, in SQLite files in its folder, one per process that records, so that two
// processes never write one file. The game writes run.db (the director's frames and events, the tab's runs' events, and what KSA
// Metal measured: every present, when each was shown, the waits that blocked the game, large allocations); benchmark.sh's
// program writes host.db (its events around the game). Every row carries the time on one clock (RunClock: the Mac's
// monotonic clock in seconds, which KSA Metal's own times use), so the tables of both files line up.
//
//   meta      key, value: the schema's version
//   events    a run event (Events.cs) and its time
//   frames    a frame (Frames.cs) and its time
//   presents  every present: KSA Metal's numbers for it (DriverStats.FrameStats)
//   shown     a present: when its frame began (the game's wait for an earlier frame ended), was presented, finished on the
//             GPU and was shown, how long the frame waited, whether pacing ended the wait
//   waits     a call that blocked the game longer than the threshold the director opened the drain with: what, how long
//   memory    an allocation (positive) or a free (negative) of 16 MB or more
//   frame_gpu a present's frame's own GPU time, when its command buffer completed (a part's GPU busy; presents' gpu_ms is
//             what completed between two presents)
//
// shown and frame_gpu carry the game session (every session's game writes the run's file, and each counts its presents
// from 1); a store of before has none, its presents read as the first session's.
//   zone_stats, moments, slices  the game's profiler zones summed per part, the slow frames and what took their time
//             (Zones.cs)
//   counters  a probe's counter, sampled once a frame: its metric's name, its value, and the part it counts in
//   metrics   a metric a probe registered (Metrics.cs): its name, label, unit, which way is better, how a part's
//             measurings combine, its limit
//   census    who held the game's memory at a checkpoint: an owner (one of the game's memory tags, a kind of KSA Metal's
//             memory, the .NET heap), its kind, its bytes
//
// frames has, after the director's columns, the .NET runtime's work in each frame (RuntimeCounters): GC pause, collections
// by generation, JIT time and methods compiled, lock contentions, MB allocated.
//
// The run's record is made from the store (Finish.cs). An event is written within milliseconds (the other process watches
// for them: benchmark.sh the game's heartbeats, the game benchmark.sh's recording), the rest four times a second.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KsaPort.Common.Benchmark;

// The clock of a run's store: the Mac's monotonic clock in seconds, the clock of CACurrentMediaTime and of Metal's times
public static class RunClock
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Timebase
    {
        public uint Numer, Denom;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern ulong mach_absolute_time();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int mach_timebase_info(out Timebase info);

    private static readonly double SecondsPerTick = Init();

    private static double Init()
    {
        mach_timebase_info(out var t);
        return t.Denom == 0 ? 1e-9 : (double)t.Numer / t.Denom * 1e-9;
    }

    public static double Now() => mach_absolute_time() * SecondsPerTick;
}

public sealed class RunStore : IDisposable
{
    public const string GameFile = "run.db", HostFile = "host.db";
    private const int Schema = 4;

    // The file this process writes: the game's, unless the process says it is the host (benchmark.sh's program)
    public static string ProcessFile { get; set; } = GameFile;

    // Where a store that can't be written says so (the hooks' log in the game); it then stops writing, and the run goes on
    public static Action<string>? Log { get; set; }

    private static readonly Dictionary<string, RunStore> Open = new();

    // This process's store in a run's folder, opened once and shared by everything in the process that writes there;
    // null when it can't be opened
    public static RunStore? For(string dir)
    {
        var path = Path.GetFullPath(Path.Combine(RunFiles.Data(dir), ProcessFile));
        lock (Open)
        {
            if (Open.TryGetValue(path, out var store))
                return store.Failed ? null : store;
            try
            {
                store = new RunStore(path);
            }
            catch (Exception e) when (e is SqliteException or DllNotFoundException or EntryPointNotFoundException or IOException
                                          or UnauthorizedAccessException)
            {
                Say($"RunStore: {path} can't be written: {e.Message}");
                return null;
            }
            Open[path] = store;
            return store;
        }
    }

    // Everything this process's stores were given, written to their files: before a run is finished, and when the process
    // ends
    public static void FlushAll()
    {
        List<RunStore> stores;
        lock (Open)
            stores = Open.Values.ToList();
        foreach (var store in stores)
            store.Flush();
    }

    // What this process gave its store in a run's folder, written to its file (none open: nothing to do)
    public static void Flush(string dir)
    {
        var path = Path.GetFullPath(Path.Combine(RunFiles.Data(dir), ProcessFile));
        RunStore? store;
        lock (Open)
            store = Open.GetValueOrDefault(path);
        store?.Flush();
    }

    // This process's stores of a run's folder written and closed: once the run is finished
    public static void Close(string dir)
    {
        var folder = Path.GetFullPath(dir).TrimEnd('/') + "/";
        List<RunStore> stores;
        lock (Open)
            stores = Open.Values.Where(s => s._path.StartsWith(folder, StringComparison.Ordinal)).ToList();
        foreach (var store in stores)
            store.Dispose();
    }

    // The log, if any; a log that fails says nothing (this may run on the store's thread, or as the process ends)
    private static void Say(string message)
    {
        try
        {
            Log?.Invoke(message);
        }
        catch (Exception)
        {
        }
    }

    // This process's stores written and closed: before their folder goes
    public static void CloseAll()
    {
        List<RunStore> stores;
        lock (Open)
            stores = Open.Values.ToList();
        foreach (var store in stores)
            store.Dispose();
    }

    private enum Table { Event, Frame, Present, Shown, Wait, Memory, FrameGpu, ZoneStats, Moment, Slice, Counter, Metric, Census }

    private static readonly Dictionary<Table, string> Inserts = new()
    {
        [Table.Event] = "INSERT INTO events (t, at, session, pass, type, scenario, part, text, game, value) VALUES (?,?,?,?,?,?,?,?,?,?)",
        [Table.Frame] = "INSERT INTO frames (t, time, session, pass, script, scenario, part, phase, frame_ms, present, gpu_ms, cpu_ms, draws, " +
                        "passes, metal_mb, footprint_mb, thermal, shaders, pipelines, display_ms, game_s, asked, sim_ms, substeps, bubbles, " +
                        "vehicles, gc_pause_ms, gc0, gc1, gc2, jit_ms, jit_methods, contentions, alloc_mb) " +
                        "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        [Table.Present] = "INSERT INTO presents (t, present, cpu_ms, gpu_ms, draws, dispatches, passes, submits, metal_mb, footprint_mb, " +
                          "thermal, shaders, pipelines, width, height, display_ms) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        [Table.Shown] = "INSERT INTO shown (present, began, presented, gpu_end, shown, waited_ms, paced, session) VALUES (?,?,?,?,?,?,?,?)",
        [Table.Wait] = "INSERT INTO waits (t, ms, what, present) VALUES (?,?,?,?)",
        [Table.Memory] = "INSERT INTO memory (t, bytes, type, present) VALUES (?,?,?,?)",
        [Table.FrameGpu] = "INSERT INTO frame_gpu (session, present, t, gpu_ms) VALUES (?,?,?,?)",
        [Table.ZoneStats] = "INSERT INTO zone_stats (session, pass, scenario, part, grp, zone, frames, calls, total_ms, self_ms, median_ms, " +
                            "p95_ms, max_ms, alloc_mb) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        [Table.Moment] = "INSERT INTO moments (id, t, frame_ms, session, pass, scenario, part, cause) VALUES (?,?,?,?,?,?,?,?)",
        [Table.Slice] = "INSERT INTO slices (moment, grp, thread, zone, depth, start, ms, self_ms, bytes) VALUES (?,?,?,?,?,?,?,?,?)",
        [Table.Counter] = "INSERT INTO counters (t, session, pass, scenario, part, name, value) VALUES (?,?,?,?,?,?,?)",
        [Table.Metric] = "INSERT OR REPLACE INTO metrics (name, label, unit, better, across, decimals, limit_pct, help) VALUES (?,?,?,?,?,?,?,?)",
        [Table.Census] = "INSERT INTO census (t, session, pass, scenario, shot, owner, kind, bytes) VALUES (?,?,?,?,?,?,?,?)",
    };

    private const string Create = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS events (t REAL, at INTEGER, session INTEGER, pass INTEGER, type TEXT, scenario TEXT, part TEXT,
            text TEXT, game REAL, value REAL);
        CREATE TABLE IF NOT EXISTS frames (t REAL, time REAL, session INTEGER, pass INTEGER, script REAL, scenario TEXT, part TEXT,
            phase TEXT, frame_ms REAL, present INTEGER, gpu_ms REAL, cpu_ms REAL, draws INTEGER, passes INTEGER, metal_mb INTEGER,
            footprint_mb INTEGER, thermal INTEGER, shaders INTEGER, pipelines INTEGER, display_ms REAL, game_s REAL, asked REAL,
            sim_ms REAL, substeps INTEGER, bubbles INTEGER, vehicles INTEGER, gc_pause_ms REAL, gc0 INTEGER, gc1 INTEGER, gc2 INTEGER,
            jit_ms REAL, jit_methods INTEGER, contentions INTEGER, alloc_mb REAL);
        CREATE TABLE IF NOT EXISTS presents (t REAL, present INTEGER, cpu_ms REAL, gpu_ms REAL, draws INTEGER, dispatches INTEGER,
            passes INTEGER, submits INTEGER, metal_mb INTEGER, footprint_mb INTEGER, thermal INTEGER, shaders INTEGER,
            pipelines INTEGER, width INTEGER, height INTEGER, display_ms REAL);
        CREATE TABLE IF NOT EXISTS shown (present INTEGER, began REAL, presented REAL, gpu_end REAL, shown REAL, waited_ms REAL,
            paced INTEGER, session INTEGER);
        CREATE TABLE IF NOT EXISTS waits (t REAL, ms REAL, what TEXT, present INTEGER);
        CREATE TABLE IF NOT EXISTS memory (t REAL, bytes INTEGER, type INTEGER, present INTEGER);
        CREATE TABLE IF NOT EXISTS frame_gpu (session INTEGER, present INTEGER, t REAL, gpu_ms REAL);
        CREATE TABLE IF NOT EXISTS zone_stats (session INTEGER, pass INTEGER, scenario TEXT, part TEXT, grp TEXT, zone TEXT, frames INTEGER,
            calls INTEGER, total_ms REAL, self_ms REAL, median_ms REAL, p95_ms REAL, max_ms REAL, alloc_mb REAL);
        CREATE TABLE IF NOT EXISTS moments (id INTEGER, t REAL, frame_ms REAL, session INTEGER, pass INTEGER, scenario TEXT, part TEXT,
            cause TEXT);
        CREATE TABLE IF NOT EXISTS slices (moment INTEGER, grp TEXT, thread TEXT, zone TEXT, depth INTEGER, start REAL, ms REAL,
            self_ms REAL, bytes INTEGER);
        CREATE TABLE IF NOT EXISTS counters (t REAL, session INTEGER, pass INTEGER, scenario TEXT, part TEXT, name TEXT, value REAL);
        CREATE TABLE IF NOT EXISTS metrics (name TEXT PRIMARY KEY, label TEXT, unit TEXT, better TEXT, across TEXT, decimals INTEGER,
            limit_pct REAL, help TEXT);
        CREATE TABLE IF NOT EXISTS census (t REAL, session INTEGER, pass INTEGER, scenario TEXT, shot TEXT, owner TEXT, kind TEXT,
            bytes INTEGER);
        """;

    private readonly string _path;
    private readonly Sqlite _db;
    private readonly Dictionary<Table, Sqlite.Statement> _inserts = new();
    private readonly ConcurrentQueue<(Table Table, object?[] Values)> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private readonly object _writing = new();
    private volatile bool _closing;
    private long _queued, _written;

    public bool Failed { get; private set; }

    private RunStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = Sqlite.Open(path);
        // The host reads the game's file while it writes; a game that dies loses at most the rows not yet committed
        _db.Exec("PRAGMA journal_mode=WAL");
        _db.Exec("PRAGMA synchronous=NORMAL");
        foreach (var sql in Create.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _db.Exec(sql);
        using (var meta = _db.Prepare("INSERT OR IGNORE INTO meta (key, value) VALUES ('schema', ?)"))
            meta.Run(Schema.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (table, sql) in Inserts)
            _inserts[table] = _db.Prepare(sql);
        _writer = new Thread(Write) { IsBackground = true, Name = "benchmark store" };
        _writer.Start();
        AppDomain.CurrentDomain.ProcessExit += OnExit;
    }

    private void OnExit(object? sender, EventArgs e)
    {
        try
        {
            Dispose();
        }
        catch (Exception)
        {
        }
    }

    // An event, written at once
    public void Add(RunEvent e)
    {
        Enqueue(Table.Event, RunClock.Now(), e.At.ToUniversalTime().Ticks, e.Session, e.Pass, e.Type, e.Scenario, e.Part, e.Text, e.Game, e.Value);
        _wake.Set();
    }

    // What was given so far into the file soon, without waiting for it
    public void Wake() => _wake.Set();

    // A frame: its row as FrameLog.Columns makes it
    public void AddFrame(object?[] row) => Enqueue(Table.Frame, row);

    public void AddZoneStats(PartKey part, string group, string zone, int frames, int calls, double totalMs, double selfMs, double medianMs,
        double p95Ms, double maxMs, double allocMb) =>
        Enqueue(Table.ZoneStats, part.Session, part.Pass, part.Scenario, part.Part, group, zone, frames, calls, totalMs, selfMs, medianMs,
            p95Ms, maxMs, allocMb);

    public void AddMoment(int id, double t, double frameMs, PartKey part, string cause) =>
        Enqueue(Table.Moment, id, t, frameMs, part.Session, part.Pass, part.Scenario, part.Part, cause);

    public void AddSlice(int moment, string group, string thread, string zone, int depth, double start, double ms, double selfMs, long bytes) =>
        Enqueue(Table.Slice, moment, group, thread, zone, depth, start, ms, selfMs, bytes);

    // A counter's value in a frame, in the part it counts in (none: outside the parts)
    public void AddCounter(double t, int session, int pass, string? scenario, string? part, string name, double value) =>
        Enqueue(Table.Counter, t, session, pass, scenario, part, name, value);

    public void AddMetric(Metric m) =>
        Enqueue(Table.Metric, m.Name, m.Label, m.Unit, m.Better.ToString(), m.Across.ToString(), m.Decimals, m.Limit, m.Help);

    public void AddCensus(double t, int session, int pass, string? scenario, string shot, string owner, string kind, long bytes) =>
        Enqueue(Table.Census, t, session, pass, scenario, shot, owner, kind, bytes);

    // A record of KSA Metal's drain, in the game session that drained it
    public void Add(DriverRecord record, int session)
    {
        switch (record)
        {
            case PresentRecord p:
                var s = p.Stats;
                Enqueue(Table.Present, p.T, s.Present, s.CpuMs, s.GpuMs, s.Draws, s.Dispatches, s.Passes, s.Submits, s.AllocatedMb,
                    s.FootprintMb, s.ThermalState, s.ShadersCompiled, s.PipelinesCreated, s.DrawableWidth, s.DrawableHeight, s.DisplayMs);
                break;
            case ShownRecord w:
                Enqueue(Table.Shown, w.Present, w.Began, w.Presented, w.GpuEnd, w.Shown, w.WaitedMs, w.Paced, session);
                break;
            case WaitRecord w:
                Enqueue(Table.Wait, w.T, w.Ms, w.What, w.Present);
                break;
            case MemoryRecord m:
                Enqueue(Table.Memory, m.T, m.Bytes, m.Type, m.Present);
                break;
            case FrameGpuRecord g:
                Enqueue(Table.FrameGpu, session, g.Present, g.T, g.GpuMs);
                break;
        }
    }

    private void Enqueue(Table table, params object?[] values)
    {
        if (Failed || _closing)
            return;
        _queue.Enqueue((table, values));
        Interlocked.Increment(ref _queued);
    }

    // Until everything given so far is in the file
    public void Flush()
    {
        long target = Interlocked.Read(ref _queued);
        while (!Failed && Interlocked.Read(ref _written) < target && _writer.IsAlive)
        {
            _wake.Set();
            Thread.Sleep(5);
        }
    }

    // The writer: a transaction four times a second, or when asked
    private void Write()
    {
        while (true)
        {
            _wake.WaitOne(250);
            bool closing = _closing;
            WriteQueued();
            if (closing || Failed)
                return;
        }
    }

    private void WriteQueued()
    {
        lock (_writing)
        {
            if (Failed || _queue.IsEmpty)
                return;
            int n = 0;
            try
            {
                _db.Exec("BEGIN");
                while (_queue.TryDequeue(out var row))
                {
                    _inserts[row.Table].Run(row.Values);
                    n++;
                }
                _db.Exec("COMMIT");
                Interlocked.Add(ref _written, n);
            }
            // Anything: an exception out of this thread would end the game
            catch (Exception e)
            {
                Failed = true;
                Say($"RunStore: {_path}: {e.Message}; the run goes on without it");
            }
        }
    }

    public void Dispose()
    {
        if (_closing)
            return;
        _closing = true;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        _wake.Set();
        // A writer still in a statement after that keeps its statements and connection: left open, not freed under it
        if (!_writer.Join(5000))
        {
            Say($"RunStore: {_path}: its writer didn't finish in 5 s; the file is left open");
            lock (Open)
                Open.Remove(_path);
            return;
        }
        lock (_writing)
        {
            foreach (var s in _inserts.Values)
                s.Dispose();
            _inserts.Clear();
            bool single = false;
            if (!Failed)
            {
                try
                {
                    // One file again: the rows in the write-ahead log go into it, and the log goes. The mode stays WAL,
                    // and says so, while another connection (the host reading) has the file open
                    _db.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
                    using var mode = _db.Prepare("PRAGMA journal_mode=DELETE");
                    single = mode.Step() && mode.Text(0) == "delete";
                }
                catch (SqliteException)
                {
                }
            }
            _db.Dispose();
            // Out of WAL mode no connection uses the index macOS's SQLite leaves behind
            if (single)
            {
                try
                {
                    File.Delete(_path + "-shm");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        lock (Open)
            Open.Remove(_path);
    }

    // The events of a run's folder's stores, both processes' in time order (an event's time, then the host's first, then the
    // order each wrote them); none when the folder has no store or it can't be read
    public static List<RunEvent> ReadEvents(string dir)
    {
        var events = new List<(long At, int File, long Row, RunEvent Event)>();
        int file = 0;
        foreach (var path in new[] { Path.Combine(RunFiles.Data(dir), HostFile), Path.Combine(RunFiles.Data(dir), GameFile) })
        {
            file++;
            Query(path, "SELECT rowid, at, session, pass, type, scenario, part, text, game, value FROM events ORDER BY rowid", q =>
            {
                long at = q.Long(1);
                events.Add((at, file, q.Long(0), new RunEvent
                {
                    At = new DateTime(at, DateTimeKind.Utc), Session = (int)q.Long(2), Pass = (int)q.Long(3), Type = q.Text(4),
                    Scenario = q.NullableText(5), Part = q.NullableText(6), Text = q.NullableText(7), Game = q.NullableDouble(8),
                    Value = q.NullableDouble(9),
                }));
            });
        }
        return events.OrderBy(e => e.At).ThenBy(e => e.File).ThenBy(e => e.Row).Select(e => e.Event).ToList();
    }

    // The game's frames in a run's folder, in the order they came
    public static List<FrameRow> ReadFrames(string dir)
    {
        var frames = new List<FrameRow>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT time, session, pass, script, scenario, part, phase, frame_ms, present, gpu_ms, draws, metal_mb, " +
                                            "footprint_mb, thermal, shaders, pipelines, game_s, asked, sim_ms, substeps, bubbles, vehicles, gc_pause_ms, jit_ms " +
                                            "FROM frames ORDER BY rowid", f =>
            frames.Add(new FrameRow(f.Double(0), (int)f.Long(1), (int)f.Long(2), f.Double(3), f.Text(4), f.Text(5), f.Text(6), f.Double(7),
                (ulong)f.Long(8), f.NullableDouble(9), (uint)f.Long(10), f.Long(11), f.Long(12), (int)f.Long(13), (uint)f.Long(14),
                (uint)f.Long(15), f.NullableDouble(16), f.NullableDouble(17), f.NullableDouble(18), f.NullableLong(19),
                (int?)f.NullableLong(20), (int?)f.NullableLong(21), f.NullableDouble(22), f.NullableDouble(23))));
        return frames;
    }

    // KSA Metal's shown records in a run's folder, by present
    public static Dictionary<int, Dictionary<ulong, ShownRow>> ReadShown(string dir)
    {
        var shown = new Dictionary<int, Dictionary<ulong, ShownRow>>();
        void Row(int session, Sqlite.Statement q)
        {
            if (!shown.TryGetValue(session, out var of))
                shown[session] = of = new();
            of[(ulong)q.Long(0)] = new ShownRow((ulong)q.Long(0), q.Double(1), q.Double(2), q.Double(3), q.Double(4), q.Double(5));
        }
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT present, began, presented, gpu_end, shown, waited_ms, session FROM shown", q => Row((int)q.Long(6), q));
        // A store of before: no session
        if (shown.Count == 0)
            Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT present, began, presented, gpu_end, shown, waited_ms FROM shown", q => Row(1, q));
        return shown;
    }

    // Each present's frame's own GPU time in a run's folder, by game session (none in a run of before frame_gpu)
    public static Dictionary<int, Dictionary<ulong, double>> ReadFrameGpu(string dir)
    {
        var gpu = new Dictionary<int, Dictionary<ulong, double>>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT session, present, gpu_ms FROM frame_gpu", q =>
        {
            if (!gpu.TryGetValue((int)q.Long(0), out var of))
                gpu[(int)q.Long(0)] = of = new();
            of[(ulong)q.Long(1)] = q.Double(2);
        });
        return gpu;
    }

    // KSA Metal's waits in a run's folder, in the order they began
    public static List<WaitRow> ReadWaits(string dir)
    {
        var waits = new List<WaitRow>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT t, ms, what FROM waits ORDER BY t", q => waits.Add(new WaitRow(q.Double(0), q.Double(1), q.Text(2))));
        return waits;
    }

    // The zones' sums in a run's folder, per measuring
    public static List<ZoneRow> ReadZones(string dir)
    {
        var zones = new List<ZoneRow>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT session, pass, scenario, part, grp, zone, frames, calls, total_ms, self_ms FROM zone_stats", q =>
            zones.Add(new ZoneRow((int)q.Long(0), (int)q.Long(1), q.Text(2), q.Text(3), q.Text(4), q.Text(5), (int)q.Long(6), (int)q.Long(7),
                q.Double(8), q.Double(9))));
        return zones;
    }

    // The moments in a run's folder, in the order kept
    public static List<MomentRecord> ReadMoments(string dir)
    {
        var moments = new List<MomentRecord>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT scenario, part, session, pass, frame_ms, cause FROM moments ORDER BY t", q =>
            moments.Add(new MomentRecord(q.Text(0), q.Text(1), (int)q.Long(2), (int)q.Long(3), Math.Round(q.Double(4), 1), q.Text(5))));
        return moments;
    }

    // The counters in a run's folder, per measuring: each counter's samples in the part, by metric name. The game's carry
    // their part; the host's (the power channel) fall in the part that was open at their time
    public static Dictionary<(string Scenario, string Part, int Session, int Pass), Dictionary<string, List<double>>> ReadCounters(string dir)
    {
        var counters = new Dictionary<(string, string, int, int), Dictionary<string, List<double>>>();
        void Add((string, string, int, int) key, string name, double value)
        {
            if (!counters.TryGetValue(key, out var byName))
                counters[key] = byName = new Dictionary<string, List<double>>(StringComparer.Ordinal);
            if (!byName.TryGetValue(name, out var values))
                byName[name] = values = [];
            values.Add(value);
        }
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT scenario, part, session, pass, name, value FROM counters WHERE part IS NOT NULL AND part != ''", q =>
            Add((q.Text(0), q.Text(1), (int)q.Long(2), (int)q.Long(3)), q.Text(4), q.Double(5)));
        // The parts' times, from the start of their first frame to the end of their last: the frames the part's numbers
        // come from (its open and close events are a frame off at either end)
        var windows = new List<(double From, double To, (string, string, int, int) Key)>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT scenario, part, session, pass, MIN(t - frame_ms / 1000.0), MAX(t) FROM frames " +
                                            "WHERE part IS NOT NULL AND part != '' GROUP BY scenario, part, session, pass", q =>
            windows.Add((q.Double(4), q.Double(5), (q.Text(0), q.Text(1), (int)q.Long(2), (int)q.Long(3)))));
        if (windows.Count > 0)
            Query(Path.Combine(RunFiles.Data(dir), HostFile), "SELECT t, session, name, value FROM counters", q =>
            {
                double t = q.Double(0);
                int session = (int)q.Long(1);
                foreach (var w in windows)
                    if (w.Key.Item3 == session && t > w.From && t <= w.To)
                        Add(w.Key, q.Text(2), q.Double(3));
            });
        return counters;
    }

    // The metrics the run's probes and its host's channels registered
    public static List<Metric> ReadMetrics(string dir)
    {
        var metrics = new List<Metric>();
        foreach (var file in new[] { GameFile, HostFile })
            Query(Path.Combine(RunFiles.Data(dir), file), "SELECT name, label, unit, better, across, decimals, limit_pct, help FROM metrics ORDER BY rowid", q =>
            {
                if (Enum.TryParse<Better>(q.Text(3), out var better) && Enum.TryParse<Across>(q.Text(4), out var across) &&
                    !metrics.Any(m => m.Name == q.Text(0)))
                    metrics.Add(new Metric(q.Text(0), q.Text(1), q.Text(2), better, across, (int)q.Long(5), q.Text(7)) { Limit = q.NullableDouble(6) });
            });
        return metrics;
    }

    // The census at each checkpoint of the run's first pass that took one: of each kind the 25 owners with the most bytes,
    // and the game's tags all together (kind "game-total"); the store keeps every owner
    public static List<CensusRecord> ReadCensus(string dir)
    {
        var census = new List<CensusRecord>();
        Query(Path.Combine(RunFiles.Data(dir), GameFile), "SELECT scenario, shot, owner, kind, bytes, session, pass FROM census " +
                                            "WHERE shot != '' AND (session, pass) IN (SELECT session, pass FROM census WHERE shot != '' ORDER BY rowid LIMIT 1) ORDER BY rowid", q =>
            census.Add(new CensusRecord(q.NullableText(0) ?? "", q.Text(1), q.Text(2), q.Text(3), Math.Round(q.Long(4) / 1048576.0, 1))));
        return census.GroupBy(c => (c.Scenario, c.Shot)).SelectMany(at =>
        {
            var kept = at.GroupBy(c => c.Kind).SelectMany(k => k.OrderByDescending(c => c.Mb).Take(25)).ToList();
            if (at.Any(c => c.Kind == "game"))
                kept.Add(new CensusRecord(at.Key.Scenario, at.Key.Shot, "all tags", "game-total", Math.Round(at.Where(c => c.Kind == "game").Sum(c => c.Mb), 1)));
            return kept;
        }).ToList();
    }

    // Each row of a query on one of a run's files; nothing when the file isn't there, can't be read or lacks the table
    // (a process that died before it made them)
    public static void Query(string path, string sql, Action<Sqlite.Statement> row)
    {
        if (!File.Exists(path))
            return;
        try
        {
            // Not read-only: a file in WAL mode may need its side files made to be read
            using var db = Sqlite.Open(path);
            using var q = db.Prepare(sql);
            while (q.Step())
                row(q);
        }
        catch (Exception e) when (e is SqliteException or DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }
}
