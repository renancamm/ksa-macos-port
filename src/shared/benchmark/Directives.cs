// The lineup's directives: what the game's console can't do. This table is what the parser checks a step against; the
// director acts on them (src/game/benchmark/Directives.cs), and a step it has no handler for fails.
//
// Moves of the camera take [<seconds>] on the script's clock and ease in and out; a value "-" keeps the current one and
// "~<x>" adds x to it. The vehicle steps act on the vehicle chosen last (@vehicle, @control, @launch, @kitten), or the
// controlled one; none of them takes control of a vehicle or moves the camera, unless said.
using System;
using System.Collections.Generic;
using System.Linq;

namespace KsaPort.Common.Benchmark;

public sealed record Directive(string Name, int[] Counts, string Usage, string Help);

public static class Directives
{
    private const int Many = int.MaxValue;

    public static readonly IReadOnlyList<Directive> All =
    [
        // Timing and measuring
        new("@measure", [2], "<name> <seconds>", "measure the next seconds as one part of the scenario"),
        new("@shot", [1], "<name>", "a checkpoint: the game's clock in step with the script's, paused, the screenshot <name>.png " +
            "taken once the view is still (a views scenario also measures it standing, as the part <name>)"),
        new("@warp", [1, 2], "<speed> [seconds]", "the simulation at that speed (1 is real time, 0 pauses it), moving to it over the seconds"),
        new("@sync", [0], "", "a checkpoint without a screenshot: the steps after it act at the same game time on every Mac"),
        new("@reset", [0], "", "the world becomes a new game's (its clock at the start, no vehicles), the game stopped"),
        // Vehicles and kittens
        new("@launch", [3, Many], "<save> at <pad> [as <id>] | <lat> <lon> <save> [as <id>]", "put a saved vehicle on a launch pad of the " +
            "home celestial (e.g. CCSFS LC-39A), as the launch menu does, or anywhere on it; it becomes the controlled vehicle and the " +
            "camera follows it, unless @place or @front holds the camera. A vehicle that is there already stays as it is"),
        new("@vehicle", [1], "<id>", "choose that vehicle for the vehicle steps and the vehicle camera (one @explode blew up too: " +
            "the camera then aims at where it blew up)"),
        new("@control", [1], "<id>", "make that vehicle the controlled one, the camera following it, at once"),
        new("@crew", [0, 1], "[name]", "seat a kitten of the benchmark's in the vehicle (Hunter's looks, so its fur is the same)"),
        new("@stage", [0, 1], "[n]", "activate the vehicle's next stage n times (the staging key)"),
        new("@input", [2], "<action> press|release", "a key action for the vehicle, e.g. MainEngineStartup (ignite)"),
        new("@explode", [0], "", "destroy the vehicle the way a crash does"),
        new("@face", [3, 5], "<yaw> <pitch> <elev>|sun [<yaw> <pitch>]", "turn the vehicle so that the view from its first crewed seat " +
            "at that yaw and pitch looks elev degrees above the level, or at the sun; with a second yaw and pitch, the celestial's " +
            "center there too"),
        new("@kitten", [3, 5], "<lat> <lon> <name> [<alt> orbit]", "a kitten of the benchmark's out on EVA, standing on the ground of " +
            "the home celestial there, or on a circular orbit that high over it; it becomes the vehicle. Only with the game stopped"),
        new("@fly", [2], "<alt> <speed>|orbit", "put the vehicle that high above the sea (\"km\" for kilometres), climbing at that " +
            "speed, or on a circular orbit there going east"),
        // The camera (Camera.cs, Path.cs, Rig.cs)
        new("@key", [6], "<celestial> <lat> <lon> <alt> <heading> <pitch>", "a place of the free camera's flight over and between " +
            "celestials"),
        new("@place", [6], "<celestial> <lat> <lon> <alt> <heading> <pitch>", "the free camera at that place at once, held there " +
            "while vehicles are placed, started and blown up, until a camera step moves it"),
        new("@camera", [1], "orbit|free|iva", "the main view's camera mode"),
        new("@chase", [3, 4], "<az> <el> <dist> [s]", "the vehicle camera going along with the vehicle, from the direction az " +
            "(degrees from north), el degrees above its horizon, dist metres away"),
        new("@watch", [3, 4, 5], "<az> <dist> <height> [<frame>] [s]", "the vehicle camera standing on the ground dist metres from " +
            "the vehicle towards az, height metres up; with frame, zoomed so that the view is that many metres tall at the vehicle"),
        new("@front", [3, 4, 6], "<axis> <dist> <up> [<aim> [<from> <seconds>]]", "the camera dist metres out along the vehicle's own " +
            "axis (x, -y, ...), looking back at it, another of its axes up in the view; coming in from that far over the game's seconds"),
        new("@aim", [1, 2], "<metres> [s]", "aim the vehicle camera that far above the vehicle's center"),
        new("@look", [2, 3], "<yaw> <pitch> [s]", "turn the cockpit camera from its seat's forward view, in degrees"),
        new("@fov", [1], "<degrees>", "the camera's field of view, until the camera moves on"),
        new("@sun", [3], "<celestial> <lat> <lon>", "log where the sun stands over that point (to plan the light of a key)"),
    ];

    public static Directive? Find(string name) => All.FirstOrDefault(d => d.Name == name);

    // What is wrong with a directive's words, or null
    public static string? Check(string[] words)
    {
        if (Find(words[0]) is not { } d)
            return $"no directive {words[0]}";
        int count = words.Length - 1;
        bool ok = d.Counts.Length == 2 && d.Counts[1] == Many ? count >= d.Counts[0] : d.Counts.Contains(count);
        // The second yaw and pitch go with the sun alone
        if (d.Name == "@face" && count == 5 && words[3] != "sun")
            ok = false;
        return ok ? null : $"{d.Name} {d.Usage}";
    }

    // The id a @launch gives its vehicle: "as <id>", or the save's name
    public static string LaunchedId(string[] words)
    {
        int asAt = Array.IndexOf(words, "as");
        if (asAt > 1 && asAt == words.Length - 2)
            return words[^1];
        int at = Array.IndexOf(words, "at");
        return at > 1 ? string.Join(' ', words[1..at]) : string.Join(' ', words[3..]);
    }
}
