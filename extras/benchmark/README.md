# Benchmark

An optional extra that measures how KSA runs on your Mac. It plays a fixed tour of the game in one session, a camera
flight from Earth to Jupiter without a cut, then a rocket launch, a kitten in the grass and a 2,000-part rocket
breaking up on its pad, measures each part of it, and writes a report you can compare with another run or share.

It is meant for spotting changes: between two settings, two versions of the port, or two Macs. Two runs on the same
Mac differ by a few percent in most parts, so `compare` marks changes over 5%.

## Run it

From the project's folder, after `./port.sh`:

```bash
./extras/benchmark/benchmark.sh
```

It takes about 8 minutes. The game takes over the screen: leave the mouse and keyboard alone until the script says
it is done. The first run also downloads Microsoft's .NET SDK (about 200 MB) to build the benchmark mod, and adds the
[StarMap](../starmap/README.md) mod loader to the app when it isn't there yet.

The results go to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`: `report.md` with the numbers,
`report.json` for comparing, the screenshots, and the game's log.

To compare two runs:

```bash
./extras/benchmark/benchmark.sh compare <first run folder> <second run folder>
```

### Sharing a report

Reports from other Macs are very welcome: open an issue with the **Benchmark report** template, paste `report.md`,
and attach `report.json` and the screenshots. For reports that compare across Macs, run with a preset:
`--preset high` (or `--preset low` on smaller Macs).

## The tour

| Section | What it shows | What it measures |
|---|---|---|
| `earth` | the whole Earth, down through the clouds to the grass of the Amazon, then hops to the Andes, the Big Sur coast, Mount Rainier and the sea off Oahu | atmosphere, clouds, terrain, ground clutter, the ocean; new ground streaming in all along |
| `moon` | the flight out to the Moon, the crater Tycho, low along the line between day and night | terrain, long shadows of craters and rocks |
| `mars` | the flight on to Mars, Olympus Mons, rocky ground facing the sunrise | terrain, the dusty air, rocks |
| `saturn` | the rings from above, the haze under them, the rocks of the rings | rings, their rocks |
| `jupiter` | Jupiter from afar and down into its clouds | volumetric clouds |
| `launch` | a rocket with solid boosters lifting off from LC-39A, its climb, ten times faster up high | plumes, smoke trails, part physics |
| `capsule` | a one-seat capsule in tall grass: its cockpit, then the kitten out, walking, helmet off | ray tracing in the cockpit (when your settings have it on), fur, grass and shadows |
| `giga` | a rocket of about 2,000 parts on its pad, lifting off and breaking up | part physics on the CPU, many draw calls, lights, explosions and smoke |

The camera flies from one world to the next without a cut, and eases to a stop for each screenshot. With
`--system earth` the flight ends at the Moon.

Every part of a section is measured for a few seconds:

- **Frame** is the time between two frames, as you feel it: the median and the 95th percentile (the slow end).
- **GPU** is the time the GPU worked on a frame. When it is close to the frame time, the GPU sets the pace.
- **Hitches** are frames that took more than twice the median.

## How it works

The game runs from its own documents folder, `deps/benchmark/instance`, which StarMap points it at: a copy of your
settings (with the preset on top, if any), none of your mods, and the tour's vehicles. Your saves, settings and mods
are never touched. The script only chooses the settings it needs to start straight into the tour (no system menu, the
chosen system, the game's test vessel in orbit); every quality setting stays yours or the preset's.

The tour is a text file, `benchmark.txt`: sections of steps timed in seconds, each a console command or a directive of
the benchmark mod (`mod/Directives.cs` lists them). The camera's flight is its `@key` steps, places the camera passes
through (`mod/Path.cs`). The mod, in `mod/`, plays the steps, moves the camera, measures the
parts the sections mark and asks KSA Metal for each frame's GPU time and for the screenshots. It is built on your Mac
against your copy of the game, so it never ships any of the game's files. `report/` holds the small program that
writes the report and compares runs.

Each screenshot is a checkpoint. The game runs its simulation as fast as the Mac allows, so a slower Mac can fall a
little behind the tour; at a checkpoint the tour waits for the game to reach the moment it should be at, pauses it
there and takes the screenshot. So the screenshots show the same moments on every Mac, and so does the light.

The rockets and the capsule in `vehicles/` were built by the project for the tour.

### Changing the tour

`--explore` starts the benchmark's game the same way as a run, with its clock paused so the light is the tour's. Fly
the free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is added as a `@key` line to
`keys.txt` in a new run folder, and copied to the clipboard, ready for `benchmark.txt`.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset high` | fixed settings in a 1280x800 window without V-Sync, the same on every Mac (default: your settings) |
| `--system earth` | Earth and the Moon only (the game's smaller system): skips Mars, Saturn and Jupiter (default: `sol`) |
| `--explore` | start the benchmark's game paused, with a console command (`benchkey`) that captures the camera's place for the tour |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |
| `<section> ...` | play only these sections |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results |
| `deps/benchmark/` | the built mod and report program, and the game's documents folder for the run |
| `deps/dotnet-sdk/` | Microsoft's .NET SDK, to build them |
