using System.IO;
using System.Text.Json;

namespace KsaPort.Common;

// A run's options: what the director plays and how. benchmark.sh's report program writes them, or the hooks for a run
// from the Mac Port tab, as run.json in the run's folder, which the game gets as KSA_BENCH_OUT; the director and the
// hooks read them there when the game starts.
public sealed class RunOptions
{
    // The script (benchmark.txt)
    public string Script { get; set; } = "";
    // What to play (space separated): sections, or places as <section>_<shot> (the section up to that checkpoint, its
    // other checkpoints only keeping the clocks in step); empty: all of it
    public string Only { get; set; } = "";
    // Fast mode (Benchmark.cs)
    public bool Fast { get; set; }
    // No screenshots (the checkpoints still keep the game's clock in step)
    public bool NoShots { get; set; }
    // <section>_<shot>: stop at that checkpoint for good, the game paused, until it is quit
    public string Hold { get; set; } = "";
    // A suffix for the results' file names (results-2.json, frames-2.csv), for repeats
    public string Suffix { get; set; } = "";
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
    // A file to wait for in the game before playing (up to a minute), so that a tool started once the game is up (an
    // Instruments trace) sees the whole run
    public string Wait { get; set; } = "";
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

    public const string FileName = "run.json";

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
