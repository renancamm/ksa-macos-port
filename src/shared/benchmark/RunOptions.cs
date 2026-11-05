using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KsaPort.Common.Benchmark;

// A run's options: what the director plays and how, and what the run's host says about it for the record. benchmark.sh's
// program writes them for each game session, or the hooks for a run from the Mac Port tab, as options.json in the run's
// folder, which the game gets as KSA_BENCH_OUT; the director and the hooks read them there when the game starts, and
// Finish.cs when the run ends.
public sealed class RunOptions
{
    // The script (benchmark.txt)
    public string Script { get; set; } = "";
    // What to play (space separated): sections, or places as <section>_<shot> (the section up to that checkpoint, its
    // other checkpoints only keeping the clocks in step); empty: all of it
    public string Only { get; set; } = "";
    // The places chosen jumped to, the game paused on the way (a flight's), instead of played: for a close look at a place
    public bool Fast { get; set; }
    // How it plays, for its report: the kind of its scenarios (views, flight, timed, several for full), "jump" with Fast
    public string Mode { get; set; } = "";
    // No screenshots (the checkpoints still keep the game's clock in step)
    public bool NoShots { get; set; }
    // <section>_<shot>: stop at that checkpoint for good, the game paused, until it is quit
    public string Hold { get; set; } = "";
    // Which game session of the run this is, from 1, and the side of an A/B run it plays (A, B; empty: none)
    public int Session { get; set; } = 1;
    public string Variant { get; set; } = "";
    // How many times the scenarios play in the session, measured each time (benchmark.sh measure --repeat), and whether a
    // pass before them warms the Mac up, its numbers kept apart (pass 0)
    public int Repeat { get; set; } = 1;
    public bool WarmUp { get; set; }
    // A folder whose noise/ holds profiles that a test's own (in the app, next to the lineup) gives way to: the benchmarks
    // folder, where benchmark.sh calibrate writes them
    public string NoiseFolder { get; set; } = "";
    // The app played, and what the host says about the run for its record (the test, the settings, the tools...)
    public string App { get; set; } = "";
    public List<Fact> Facts { get; set; } = [];
    // Profile that many frames of every measured part with KSA Metal's GPU profiler (the game needs
    // KSA_MTL_GPU_PROFILE=<csv>)
    public uint Profile { get; set; }
    // An Xcode GPU frame capture of the view at every checkpoint, <section>_<shot>.gputrace (the game needs
    // MTL_CAPTURE_ENABLED=1)
    public bool Capture { get; set; }
    // A clean run's folder: after a screenshot under half the size of that run's same screenshot, KSA Metal's pass dump
    // of the next frame in caught/<section>_<shot>/ and an image of a frame after it in caught/, at most MaxCatches a
    // run. A screenshot also counts when it has far more pure black than that run's, or is 30 % pure white
    public string Catch { get; set; } = "";
    // With Catch: an Xcode GPU frame capture after the pass dump, in caught/<section>_<shot>.gputrace (the game needs
    // MTL_CAPTURE_ENABLED=1)
    public bool CatchCapture { get; set; }
    // The same pass dump and image at every checkpoint, broken or not: a clean run's, to compare a caught one with
    public bool Passes { get; set; }
    // Wait in the game before playing (up to a minute) for the "recording" event, so that a tool started once the game is
    // up (an Instruments trace) sees the whole run
    public bool WaitForRecording { get; set; }
    // Write the camera's place every frame to trace.csv, to check its moves are smooth
    public bool Trace { get; set; }
    // A screenshot every that many seconds of the script's clock, into film/, to see moves
    public double Film { get; set; }
    // No script: the explore mode's console command instead (Explore.cs)
    public bool Explore { get; set; }
    // The game goes on when the script ends (Done), instead of quitting
    public bool Stay { get; set; }
    // The run plays in a game that was running already (no loading time)
    public bool InGame { get; set; }
    // The test it plays (mixed, tour, warp, full), for its report
    public string Test { get; set; } = "";
    // The Mac Port tab's "Restart for a Clean Run": the game started again for it, and quits at the end
    public bool Restarted { get; set; }

    // The run's folder: where run.json is, and the results and screenshots go
    [System.Text.Json.Serialization.JsonIgnore]
    public string Out { get; set; } = "";

    public const string FileName = "options.json";

    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json));
        Out = folder;
    }

    // The options in a run's folder, or null when it holds none or they can't be read
    public static RunOptions? Read(string folder)
    {
        try
        {
            var options = JsonSerializer.Deserialize<RunOptions>(File.ReadAllText(Path.Combine(folder, FileName)), Json);
            if (options != null)
                options.Out = folder;
            return options;
        }
        catch (System.Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }
}
