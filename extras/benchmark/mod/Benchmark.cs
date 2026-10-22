// ksa-metal-benchmark: plays one benchmark scene and measures it.
//
// extras/benchmark/benchmark.sh starts the game once per scene with this mod installed and two variables set:
//   KSA_BENCH_SCENE  the scene script (extras/benchmark/scenes/<name>.txt)
//   KSA_BENCH_OUT    the folder for the scene's results (<name>.json) and screenshots
// Without them the mod does nothing.
//
// A scene script has one "<seconds> <command>" step per line, timed from the first in-game frame. A command is a
// console command or one of the directives in Directives.cs. Other lines (comments, the runner's own keys) are
// skipped. Times are seconds rather than frames, so every Mac measures the same moments of the scene.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using KSA;
using StarMap.API;

namespace KsaMetalBenchmark;

[StarMapMod]
public partial class Benchmark
{
    private readonly List<(double at, string command)> _steps = new();
    private readonly Stopwatch _clock = new();
    private string _scene = "";
    private string _outDir = "";
    private int _next;
    private bool _active;
    private double _lastFrameAt;
    private double _exitAt = double.MaxValue;
    private double _rotateRadPerSecond;
    // Commands a directive left for the next frames, one per frame (camera moves need a frame to take effect)
    private readonly Queue<string> _deferred = new();
    private Results? _results;
    private Window? _window;

    // StarMap only calls an ImmediateLoad method with the signature void(Mod)
    [StarMapImmediateLoad]
    public void Init(Mod mod)
    {
        var script = Environment.GetEnvironmentVariable("KSA_BENCH_SCENE");
        _outDir = Environment.GetEnvironmentVariable("KSA_BENCH_OUT") ?? "";
        if (string.IsNullOrEmpty(script) || string.IsNullOrEmpty(_outDir))
            return;
        if (!File.Exists(script))
        {
            Log($"scene {script} not found");
            return;
        }
        _scene = Path.GetFileNameWithoutExtension(script);
        foreach (var raw in File.ReadAllLines(script))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space > 0 && double.TryParse(line[..space], NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
                _steps.Add((at, line[(space + 1)..].Trim()));
        }
        // Steps with the same time run in the order they are written
        var ordered = _steps.OrderBy(s => s.at).ToList();
        _steps.Clear();
        _steps.AddRange(ordered);
        Directory.CreateDirectory(_outDir);
        _results = new Results(_scene, DriverStats.Available);
        _active = true;
        Log($"scene {_scene}: {_steps.Count} steps, GPU times {(DriverStats.Available ? "available" : "not available")}");
    }

    [StarMapAfterOnFrame]
    public void OnFrame(double currentPlayerTime, double dtPlayer)
    {
        if (!_active)
            return;
        if (!_clock.IsRunning)
        {
            _clock.Start();
            _results!.LoadSeconds = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds;
            Log(FormattableString.Invariant($"first in-game frame after {_results.LoadSeconds:F1} s"));
        }
        double now = _clock.Elapsed.TotalSeconds;
        double frameMs = (now - _lastFrameAt) * 1000;
        _lastFrameAt = now;

        if (_rotateRadPerSecond != 0 && Program.MainViewport.OrbitController.View is { } view)
            view.Azimuth += _rotateRadPerSecond * frameMs / 1000;

        if (_window != null)
        {
            _window.Add(frameMs, DriverStats.Read());
            if (now >= _window.EndsAt)
            {
                _results!.Windows.Add(_window.Summarize());
                Log($"measured {_window.Name}: {_window.Frames} frames");
                _window = null;
            }
        }

        if (_deferred.Count > 0)
            Run(_deferred.Dequeue(), now);
        while (_next < _steps.Count && _steps[_next].at <= now)
            Run(_steps[_next++].command, now);

        if (now >= _exitAt)
        {
            _active = false;
            Program.TerminalInterface.Execute("exit");
        }
    }

    private void Run(string command, double now)
    {
        bool ok;
        try
        {
            ok = command.StartsWith('@') ? Directive(command, now) : Program.TerminalInterface.Execute(command);
        }
        catch (Exception e)
        {
            Log($"'{command}' threw {e.GetType().Name}: {e.Message}");
            ok = false;
        }
        Log(FormattableString.Invariant($"{now:F1} s: '{command}' -> {(ok ? "ok" : "failed")}"));
        if (!ok)
            _results!.Errors.Add(FormattableString.Invariant($"{now:F1} s: '{command}' failed"));
    }

    // @end: write the results and quit a second later (screenshots finish on the GPU after their frame)
    private void End(double now)
    {
        if (_window != null)
        {
            _results!.Windows.Add(_window.Summarize());
            _window = null;
        }
        var path = Path.Combine(_outDir, _scene + ".json");
        _results!.Write(path);
        Log($"results written to {path}");
        _exitAt = now + 1;
    }

    private static void Log(string message)
    {
        // KSA redirects stderr into its own logger, so stdout it is
        Console.Out.WriteLine($"[benchmark] {message}");
        Console.Out.Flush();
    }
}
