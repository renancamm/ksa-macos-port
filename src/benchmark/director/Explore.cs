// Explore mode (benchmark.sh --explore, KSA_BENCH_EXPLORE): the benchmark's game, started the same way as a run, with
// its clock paused so the light stays the tour's. The console command "benchkey [name]" captures the free camera's
// place as a @key line for benchmark.txt: added to keys.txt in the run's folder and copied to the clipboard.
using System;
using System.IO;
using Brutal.GlfwApi;
using Brutal.ImGuiApi.Abstractions;
using Brutal.Numerics;
using KSA;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    private bool _exploring;
    private bool _exploreStarted;
    private int _exploreFrames;
    private int _keys;

    private void StartExplore()
    {
        _exploring = _option("KSA_BENCH_EXPLORE") is { Length: > 0 };
        if (_exploring)
            Log("explore mode: capture places with benchkey [name] in the console");
    }

    // Pauses the clock through the first frames of play, so the game cannot start it again as it finishes loading (the
    // tour's light: its sections start within minutes of the game's start), and adds the console command
    private void ExploreFrame()
    {
        if (++_exploreFrames <= 300)
            InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(0) });
        if (_exploreStarted)
            return;
        _exploreStarted = true;
        Program.TerminalInterface.RegisterCommand(new Action<string>(CaptureKey), this);
        Program.ConsoleWindow.Print("benchmark: capture the camera's place with benchkey [name]");
    }

    [TerminalAction("benchkey", "benchmark: capture the camera's place as a @key line in keys.txt", ArgParseMode.Unparsed)]
    private void CaptureKey(string name)
    {
        var key = CurrentKey();
        if (key == null)
        {
            Program.ConsoleWindow.Print("benchkey: the camera is near no celestial");
            return;
        }
        _keys++;
        name = name.Trim();
        // The game time too: the light of a key depends on it, and the tour's clock starts at zero
        File.AppendAllText(Path.Combine(_outDir, "keys.txt"), FormattableString.Invariant(
            $"# {(name.Length > 0 ? name : $"key {_keys}")} (game time {Universe.GetElapsedSeconds():F0} s)\n{key}\n"));
        Glfw.Clipboard = key;
        Program.ConsoleWindow.Print($"benchkey {_keys}: {key}");
    }

    // The camera's place as a key: the celestial it follows (or the nearest), altitude above sea level
    private static string? CurrentKey()
    {
        var camera = Program.GetHoveredCamera();
        var body = camera?.Following as Celestial ?? Program.GetNearbyCelestial();
        if (camera == null || body == null)
            return null;
        var (lat, lon, alt) = Place(body, camera.PositionEcl);
        var (up, north, east) = Frame(body, lat, lon);
        var forward = camera.GetForwardEcl();
        double heading = Math.Atan2(double3.Dot(forward, east), double3.Dot(forward, north)) * 180 / Math.PI;
        double pitch = Math.Asin(Math.Clamp(double3.Dot(forward, up), -1, 1)) * 180 / Math.PI;
        string altText = Math.Abs(alt) >= 100_000 ? FormattableString.Invariant($"{alt / 1000:F0}km") : FormattableString.Invariant($"{alt:F0}");
        return FormattableString.Invariant($"@key {body.Id} {lat:F6} {lon:F6} {altText} {(heading + 360) % 360:F1} {pitch:F1}");
    }
}
