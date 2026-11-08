// The probes: tools plugged into a run, the benchmark's own (a frame capture, film, the camera's log, the triggers, the
// memory census) and assemblies the plan names (--probe), all on one interface. A probe watches the run (its session, its
// passes, its parts, its checkpoints, its frames) and records through its host: counters (a metric each, sampled once a
// frame, judged as the run's own numbers are), zones of its own work on any thread (their time a frame, a counter too),
// events, files in the run's folder, and who holds memory (the census). Its knobs come from the plan (--knob
// light=2), so a study can vary them as it varies a setting. It reaches the game through the hooks: their Harmony and
// their lookup by name, which reports a method a KSA update moved instead of failing.
//
// A probe says what it costs: free (counters something keeps anyway), cheap (bounded, a fraction of a frame), perturbing
// (it changes the game's timing: its patches run every frame) or heavy (it holds the game or writes GB). A run with a
// perturbing or heavy probe gets a note: its numbers carry the probe's cost. A probe that works on a checkpoint's view (a
// frame capture before the screenshot, a pass dump after it) holds the checkpoint until it is done: it returns a wait,
// which the director asks every frame.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

// A checkpoint's view as a probe sees it: the scenario, the screenshot's name and file
public sealed record View(string Scenario, string Shot, string Path);

// Done: true once the work it waits for is over (or has taken too long), asked once a frame
public delegate bool Wait();

// What a probe costs the run it records
public enum Cost { Free, Cheap, Perturbing, Heavy }

public abstract class Probe
{
    // Its name: its knobs' and its counters' (probe/<name>/<counter>), a word
    public abstract string Name { get; }

    // What it does, for the log
    public virtual string Says => Name;

    public virtual Cost Cost => Cost.Cheap;

    // What it records through and reaches the game with: there before Start
    public ProbeHost Host { get; internal set; } = null!;

    // The game is starting, its world not loaded yet: a patch that changes what loads goes here (a run that starts with the
    // game: benchmark.sh's, the tab's restarted one; a tab run in a running game starts after it)
    public virtual void Loading() { }

    // The game session starts (the world loaded, before the first pass)
    public virtual void Start() { }

    // A pass begins or ends: its kind and its number in the session's events
    public virtual void PassBegan(PassKind kind, int pass) { }

    public virtual void PassEnded(PassKind kind, int pass) { }

    // A measured part opened or closed
    public virtual void Opened(string scenario, string part) { }

    public virtual void Closed(string scenario, string part) { }

    // The view of a checkpoint stands still, before its screenshot
    public virtual Wait? BeforeShot(View view) => null;

    // The screenshot is there
    public virtual Wait? AfterShot(View view) => null;

    // A frame of the script playing: the seconds since the run's first frame, the time of the script's clock, the
    // director's phase
    public virtual void Frame(double wall, double script, string phase) { }

    // The run ended
    public virtual void End() { }
}

// A counter of a probe: set it when it changes (or add to it), and the host writes its value every frame
public sealed class Counter(string metric)
{
    private long _bits = BitConverter.DoubleToInt64Bits(double.NaN);

    public string Metric { get; } = metric;

    public void Set(double value) => Interlocked.Exchange(ref _bits, BitConverter.DoubleToInt64Bits(value));

    public void Add(double value)
    {
        long seen, next;
        do
        {
            seen = Interlocked.Read(ref _bits);
            double now = BitConverter.Int64BitsToDouble(seen);
            next = BitConverter.DoubleToInt64Bits((double.IsNaN(now) ? 0 : now) + value);
        } while (Interlocked.CompareExchange(ref _bits, next, seen) != seen);
    }

    // Its value now; NaN before it was set
    public double Value => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bits));

    // Its value, and 0 from now on (a zone's time a frame)
    internal double Take() => BitConverter.Int64BitsToDouble(Interlocked.Exchange(ref _bits, 0));
}

// A zone of a probe's own work, on any thread: its time adds to the zone's counter, ms a frame
public readonly struct ProbeZone(Counter counter) : IDisposable
{
    private readonly long _start = System.Diagnostics.Stopwatch.GetTimestamp();

    public void Dispose() => counter.Add(System.Diagnostics.Stopwatch.GetElapsedTime(_start).TotalMilliseconds);
}

// What a probe has to tell: the run's folder, a problem of the run, a line in the game's log, an event in the run's
// store
internal sealed record ProbeContext(string Dir, Action<string> Error, Action<string> Log, EventLog? Events = null);

public sealed class ProbeHost
{
    private readonly Probe _probe;
    // The game's log alone while the game loads, the run's once the director starts
    internal ProbeContext _context;
    // Its knobs, by key without the probe's name
    private readonly Dictionary<string, string> _knobs = new(StringComparer.Ordinal);
    private readonly List<Counter> _counters = [];
    // Zone counters, cleared after each frame's sample: their time is a frame's
    private readonly List<Counter> _zones = [];
    private readonly Dictionary<string, Counter> _byName = new(StringComparer.Ordinal);
    private readonly List<Metric> _metrics = [];

    internal ProbeHost(Probe probe, ProbeContext context, IReadOnlyDictionary<string, string> knobs)
    {
        _probe = probe;
        _context = context;
        foreach (var (key, value) in knobs)
            if (key.StartsWith(probe.Name + ".", StringComparison.Ordinal))
                _knobs[key[(probe.Name.Length + 1)..]] = value;
    }

    // The run's folder
    public string Folder => _context.Dir;

    public void Log(string text) => _context.Log($"{_probe.Name}: {text}");

    // A problem of the run: it fails
    public void Error(string text) => _context.Error($"{_probe.Name}: {text}");

    // Events said while the game loaded, before the run's events: written when the director starts
    private readonly List<(string Text, double? Value)> _early = [];

    // The same host, writing to the run from now on (what it said while the game loaded first)
    internal ProbeHost With(ProbeContext context)
    {
        lock (_early)
        {
            _context = context;
            foreach (var (text, value) in _early)
                context.Events?.Add("probe", text: text, value: value);
            _early.Clear();
        }
        // Metrics made while the game loaded, before the run's store
        lock (_counters)
            foreach (var m in _metrics)
                Probes.Store?.AddMetric(m);
        return this;
    }

    // A knob as the plan gives it, or null
    public string? Knob(string key) => _knobs.TryGetValue(key, out var v) ? v : null;

    public double Knob(string key, double otherwise) =>
        Knob(key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : otherwise;

    public bool Knob(string key, bool otherwise) => Knob(key) is { } v ? v is "true" or "1" or "on" : otherwise;

    // Its knobs, as the plan gives them
    public IReadOnlyDictionary<string, string> Knobs => _knobs;

    // A counter, made once: probe/<probe>/<name>, a metric that the run's record, comparisons and studies carry
    public Counter Counter(string name, string unit, Better better = Better.Lower, Across across = Across.Median, int decimals = 2,
        string help = "", double? limit = 5) =>
        Metric($"probe/{_probe.Name}/{name}", name, unit, better, across, decimals, help, limit);

    // A zone of the probe's own work: using (Host.Zone("solve")) { ... }; its time a frame is the counter
    // probe/<probe>/zone/<name>, ms
    public ProbeZone Zone(string name)
    {
        Counter? counter;
        lock (_counters)
            _byName.TryGetValue($"probe/{_probe.Name}/zone/{name}", out counter);
        if (counter == null)
        {
            counter = Counter($"zone/{name}", "ms", help: $"{name}: the probe's own work, ms a frame");
            lock (_zones)
                if (!_zones.Contains(counter))
                    _zones.Add(counter);
        }
        return new ProbeZone(counter);
    }

    // A counter under a metric's own name (the benchmark's probes: mem/...)
    internal Counter Metric(string metric, string label, string unit, Better better, Across across, int decimals, string help, double? limit)
    {
        lock (_counters)
        {
            if (_byName.TryGetValue(metric, out var known))
                return known;
            var m = new Metric(metric, label, unit, better, across, decimals, help.Length > 0 ? help : label) { Limit = limit };
            Metrics.Register(m);
            Probes.Store?.AddMetric(m);
            _metrics.Add(m);
            var counter = new Counter(metric);
            _counters.Add(counter);
            _byName[metric] = counter;
            return counter;
        }
    }

    // An event in the run's store: "<probe> <name>: <text>"
    public void Event(string name, string? text = null, double? value = null)
    {
        var line = $"{_probe.Name} {name}{(text != null ? ": " + text : "")}";
        lock (_early)
        {
            if (_context.Events is { } events)
                events.Add("probe", text: line, value: value);
            else
                _early.Add((line, value));
        }
    }

    // A file of the probe's in the run's folder: probes/<probe>/<name> (its folder made)
    public string Artefact(string name)
    {
        var dir = Path.Combine(Folder, "probes", _probe.Name);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    // Who holds memory now: an owner (kind game, driver, runtime, process, or the probe's own), its bytes, at the
    // checkpoint under way (the census)
    public void Census(string owner, string kind, long bytes)
    {
        if (Probes.At.Shot is not { } shot)
        {
            Log("a census is taken at a checkpoint (BeforeShot, AfterShot): this one is left out");
            return;
        }
        Probes.Store?.AddCensus(RunClock.Now(), Probes.At.Session, Probes.At.Pass, Probes.At.Scenario, shot, owner, kind, bytes);
    }

    // A method of the game by its name and parameters, or null (said in the log) when a KSA update moved it
    public MethodInfo? Method(Type type, string name, params Type[] parameters)
    {
        try
        {
            return Probes.Find?.Invoke(type, name, parameters) ?? type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static, parameters) ?? throw new MissingMethodException(type.Name, name);
        }
        // The hooks' lookup says why with an exception of its own
        catch (Exception e)
        {
            Log($"{type.Name}.{name} not found (this KSA version moved it): {e.Message}");
            return null;
        }
    }

    // A Harmony patch through the hooks (prefix, postfix, transpiler: static methods); false, said in the log, when there
    // is no target or the hooks can't patch
    public bool Patch(MethodBase? target, MethodInfo? prefix = null, MethodInfo? postfix = null, MethodInfo? transpiler = null)
    {
        if (target == null)
            return false;
        if (Probes.Patcher == null)
        {
            Log($"can't patch {target.Name}: the hooks aren't there");
            return false;
        }
        try
        {
            Probes.Patcher(target, prefix, postfix, transpiler);
            return true;
        }
        catch (Exception e)
        {
            Log($"{target.Name} not patched: {e.Message}");
            return false;
        }
    }

    // Every counter's value this frame into the store, in the part it counts in; the zones' then start again
    internal void Sample(RunStore store, double t, int session, int pass, string? scenario, string? part)
    {
        List<Counter> zones;
        lock (_zones)
            zones = [.. _zones];
        lock (_counters)
            foreach (var c in _counters)
            {
                double value = zones.Contains(c) ? c.Take() : c.Value;
                if (!double.IsNaN(value))
                    store.AddCounter(t, session, pass, scenario, part, c.Metric, value);
            }
    }
}

internal static class Probes
{
    // The run's store, and the hooks' lookup and patcher (Runs.Install): set before probes start
    public static RunStore? Store;
    public static Func<Type, string, Type[], MethodInfo>? Find;
    public static Action<MethodBase, MethodInfo?, MethodInfo?, MethodInfo?>? Patcher;
    // The checkpoint under way, for the census's rows (no shot: none)
    public static (int Session, int Pass, string? Scenario, string? Shot) At;

    // The benchmark's own probes, by the name --probe gives them
    public static readonly string[] Own = ["memory"];

    // The plan's probes made as the game starts, Loading called: the director's For starts them
    private static List<Probe>? _early;

    // What went wrong while they loaded: the run's problems once the director starts
    private static readonly List<string> EarlyErrors = [];

    public static void Preload(RunPlan plan, Action<string> log)
    {
        if (plan.Record.Probes.Count == 0)
            return;
        try
        {
            var context = new ProbeContext(plan.Out, e =>
            {
                log("probe: " + e);
                EarlyErrors.Add(e);
            }, log);
            var early = plan.Record.Probes.SelectMany(p => Load(p, context)).ToList();
            foreach (var probe in early)
                probe.Host = new ProbeHost(probe, context, plan.Record.Knobs);
            _early = early;
            Each(_early, p => p.Loading());
        }
        catch (Exception e)
        {
            // Nothing of a probe stops the benchmark from loading: the run says it
            log($"probe: the probes failed to load: {e}");
            EarlyErrors.Add($"the probes failed to load: {e.Message}");
            _early = null;
        }
    }

    // The probes this plan turns on, each with its host, started
    public static List<Probe> For(RunPlan plan, ProbeContext context)
    {
        var probes = new List<Probe>();
        if (plan.Record.Capture)
            probes.Add(new CaptureProbe(context));
        if (plan.Record.Film > 0)
            probes.Add(new FilmProbe(plan.Record.Film, context));
        if (plan.Record.CameraLog)
            probes.Add(new CameraLogProbe(context));
        foreach (var probe in probes)
            probe.Host = new ProbeHost(probe, context, plan.Record.Knobs);
        // The plan's, made as the game started if it did with the run (its problems now the run's), else now
        foreach (var e in EarlyErrors)
            context.Error(e);
        bool loadedEarly = EarlyErrors.Count == 0 && _early != null;
        EarlyErrors.Clear();
        Disabled.Clear();
        At = (plan.Host.Session, 0, null, null);
        var own = loadedEarly ? _early! : Safe(() => plan.Record.Probes.SelectMany(p => Load(p, context)).ToList(), context) ?? [];
        _early = null;
        foreach (var probe in own)
            probe.Host = probe.Host is { } early ? early.With(context) : Safe(() => new ProbeHost(probe, context, plan.Record.Knobs), context)!;
        own.RemoveAll(p => p.Host == null);
        probes.AddRange(own);
        foreach (var probe in probes)
        {
            try
            {
                probe.Start();
            }
            catch (Exception e)
            {
                context.Error($"the probe {Named(probe)} failed to start: {e.Message}");
                Disabled.Add(probe);
            }
            if (Safe(() => probe.Cost, context) is Cost.Perturbing or Cost.Heavy)
                context.Events?.Add("probe-cost", text: $"{Named(probe)}: {probe.Cost.ToString().ToLowerInvariant()}");
        }
        return probes;
    }

    // A probe of the plan: the benchmark's own by its name, or every probe in an assembly (loaded where the benchmark is,
    // so it shares the game's and the benchmark's types)
    private static IEnumerable<Probe> Load(string probe, ProbeContext context)
    {
        if (!probe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Probe? own = probe switch { "memory" => new MemoryProbe(), _ => null };
            if (own == null)
                context.Error($"no probe '{probe}' in the benchmark");
            return own != null ? [own] : [];
        }
        try
        {
            var loader = AssemblyLoadContext.GetLoadContext(typeof(Probe).Assembly) ?? AssemblyLoadContext.Default;
            var assembly = loader.LoadFromAssemblyPath(probe);
            var found = assembly.GetTypes().Where(t => typeof(Probe).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
                .Select(t => (Probe)Activator.CreateInstance(t)!).ToList();
            if (found.Count == 0)
                context.Error($"{Path.GetFileName(probe)} has no probe (a public class of Probe with a constructor without parameters)");
            return found;
        }
        // Anything of a probe's (a static constructor that reaches a moved type): the run's problem, not the game's
        catch (Exception e)
        {
            context.Error($"the probe {probe} can't be loaded: {(e as ReflectionTypeLoadException)?.LoaderExceptions.FirstOrDefault()?.Message ?? e.Message}");
            return [];
        }
    }

    // Every probe's counters this frame, in the part the frame counts in
    public static void Sample(IEnumerable<Probe> probes, RunStore? store, int session, int pass, string? scenario, string? part)
    {
        if (store == null)
            return;
        double t = RunClock.Now();
        foreach (var probe in probes)
            probe.Host?.Sample(store, t, session, pass, scenario, part);
    }

    // Each probe in turn; one that fails is said in the log and left out of the rest of the run, not a crash of it
    public static void Each(IEnumerable<Probe> probes, Action<Probe> call)
    {
        foreach (var probe in probes)
        {
            if (Disabled.Contains(probe))
                continue;
            try
            {
                call(probe);
            }
            catch (Exception e) when (probe is not TriggerRunner)
            {
                Disabled.Add(probe);
                probe.Host?.Log($"failed: {e}");
                probe.Host?.Error($"failed, left out of the rest of the run: {e.Message}");
            }
        }
    }

    // The probes that failed in this run
    private static readonly HashSet<Probe> Disabled = [];

    // A probe's work on a checkpoint's view, and its wait: done when either fails
    public static Wait? Try(Probe probe, Func<Wait?> call)
    {
        Wait? wait = null;
        Each([probe], _ => wait = call());
        if (wait == null)
            return null;
        return () =>
        {
            bool done = true;
            Each([probe], _ => done = wait());
            return done || Disabled.Contains(probe);
        };
    }

    // A probe's name, or what it is when its name fails
    private static string Named(Probe probe) => Safe(() => probe.Name, null) ?? probe.GetType().Name;

    private static T? Safe<T>(Func<T> call, ProbeContext? context)
    {
        try
        {
            return call();
        }
        catch (Exception e)
        {
            context?.Error($"a probe failed: {e.Message}");
            return default;
        }
    }

    // Waits for a condition, at most so many seconds of the Mac's clock
    public static Wait Until(Func<bool> done, double seconds)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        return () => done() || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds;
    }

    // Several waits, one after the other
    public static Wait Then(Wait first, Func<Wait?> next)
    {
        Wait? second = null;
        bool firstDone = false;
        return () =>
        {
            if (!firstDone)
            {
                if (!first())
                    return false;
                firstDone = true;
                second = next();
            }
            return second == null || second();
        };
    }
}

// An Xcode frame capture of the view at every checkpoint, before its screenshot: <scenario>_<shot>.gputrace (the game
// needs MTL_CAPTURE_ENABLED=1)
internal sealed class CaptureProbe(ProbeContext context) : Probe
{
    public override string Name => "capture";

    public override string Says => "capturing a frame at each checkpoint";

    public override Wait? BeforeShot(View view)
    {
        var path = Path.ChangeExtension(view.Path, ".gputrace");
        if (!DriverStats.Capture(path, 1))
        {
            context.Error($"{view.Shot}: KSA Metal could not capture a frame (does the game have MTL_CAPTURE_ENABLED=1?)");
            return null;
        }
        return Probes.Then(Probes.Until(() => !DriverStats.Capturing(), 120), () =>
        {
            if (Directory.Exists(path) || File.Exists(path))
                context.Log($"{view.Shot}: frame capture {Path.GetFileName(path)}");
            else
                context.Error($"{view.Shot}: no frame capture");
            return null;
        });
    }
}

// A screenshot every that many seconds of the script's clock, into film/, to see the camera's moves
internal sealed class FilmProbe(double every, ProbeContext context) : Probe
{
    private double _next;

    public override string Name => "film";

    public override string Says => $"a film frame every {every.ToString(CultureInfo.InvariantCulture)} s";

    public override void Frame(double wall, double script, string phase)
    {
        if (script < _next)
            return;
        Directory.CreateDirectory(Path.Combine(context.Dir, "film"));
        DriverStats.Screenshot(Path.Combine(context.Dir, "film", FormattableString.Invariant($"{script:000.00}.png")));
        _next = script + every;
    }
}

// The camera's place every frame in camera-log.csv, to check its moves are smooth: where it is, where it looks, its up, and over
// the celestial it follows, its latitude, longitude and altitude and the ground under it
internal sealed class CameraLogProbe : Probe
{
    private readonly StreamWriter _out;

    public CameraLogProbe(ProbeContext context)
    {
        _out = new StreamWriter(Path.Combine(context.Dir, "camera-log.csv"));
        _out.WriteLine("wall,script,phase,following,x,y,z,fx,fy,fz,ux,uy,uz,lat,lon,alt,ground");
    }

    public override string Name => "camera-log";

    public override string Says => "the camera's place every frame";

    public override void Frame(double wall, double script, string phase)
    {
        if (KSA.Program.GetHoveredCamera() is not { } camera)
            return;
        var p = camera.PositionEcl;
        var f = camera.GetForwardEcl();
        var u = camera.GetUpEcl();
        string over = ",,,";
        if (camera.Following is KSA.Celestial body)
        {
            var (lat, lon, alt) = CameraOperator.Place(body, p);
            over = FormattableString.Invariant($"{lat:F6},{lon:F6},{alt:F1},{CameraOperator.Ground(body, CameraOperator.Frame(body, lat, lon).up):F1}");
        }
        _out.WriteLine(FormattableString.Invariant(
            $"{wall:F4},{script:F4},{phase},{(camera.Following as KSA.Astronomical)?.Id},{p.X:F1},{p.Y:F1},{p.Z:F1},{f.X:F7},{f.Y:F7},{f.Z:F7},{u.X:F7},{u.Y:F7},{u.Z:F7},{over}"));
    }

    public override void End() => _out.Dispose();
}
