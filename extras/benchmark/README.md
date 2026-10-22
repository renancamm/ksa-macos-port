# Benchmark

An optional extra that measures how KSA runs on your Mac. It plays a fixed set of scenes, from a rocket launch to a
dip into Jupiter's clouds, measures each one, and writes a report you can compare with another run or share.

It is meant for spotting changes: between two settings, two versions of the port, or two Macs. Two runs on the same
Mac differ by about 1% in most parts of the scenes, and by up to 4% in a few, so `compare` marks changes over 5%.

## Run it

From the project's folder, after `./port.sh`:

```bash
./extras/benchmark/benchmark.sh
```

It takes about 15 minutes. The game takes over the screen several times: leave the mouse and keyboard alone until
the script says it is done. The first run also downloads Microsoft's .NET SDK (about 200 MB) to build the benchmark
mod, and adds the [StarMap](../starmap/README.md) mod loader to the app when it isn't there yet.

The results go to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`: `report.md` with the numbers,
`report.json` for comparing, one screenshot per part of each scene, and the game's log of each scene.

To compare two runs:

```bash
./extras/benchmark/benchmark.sh compare <first run folder> <second run folder>
```

### Sharing a report

Reports from other Macs are very welcome: open an issue with the **Benchmark report** template, paste `report.md`,
and attach `report.json` and the screenshots. For reports that compare across Macs, run with a preset:
`--preset high` (or `--preset low` on smaller Macs).

## The scenes

| Scene | What it shows | What it measures |
|---|---|---|
| `orbit` | the test vessel in orbit, the whole Earth, the line between day and night | atmosphere, clouds and oceans from space |
| `flyby` | a canyon, snowy mountains, a forest, a volcano, clouds from 8 km, the Moon, Mars and Saturn | terrain, ground clutter, atmosphere; loading new places |
| `jupiter` | Jupiter from afar, then down through its clouds | volumetric clouds |
| `launch` | a rocket with solid boosters on the pad at dusk, ignition and the first 20 s of flight | plumes, smoke trails |
| `giga` | a rocket of about 2,000 parts, seen from above, on the pad and lifting off | part physics on the CPU, many draw calls and lights |
| `explosion` | a rocket blown up on the pad | fireball and smoke volumes |
| `sea` | a coast in the afternoon, then just above the waves | ocean simulation and reflections |
| `iva` | the cabin of a vessel in orbit, still and turning | ray tracing (on in this scene whatever your settings say) |
| `eva` | a kitten out of its capsule on the pad, then close to its face | fur |

Each scene is one start of the game. Every part of a scene is measured for a few seconds:

- **Frame** is the time between two frames, as you feel it: the median and the 95th percentile (the slow end).
- **GPU** is the time the GPU worked on a frame. When it is close to the frame time, the GPU sets the pace.
- **Hitches** are frames that took more than twice the median.

## How it works

The game runs from its own documents folder, `deps/benchmark/instance`, which StarMap points it at: a copy of your
settings (with the preset on top, if any), none of your mods, and the scenes' vehicles. Your saves, settings and
mods are never touched.

The scenes are text files in `scenes/`: one step per line, timed in seconds from the first frame in the game, each
a console command or a directive of the benchmark mod (`mod/Directives.cs` lists them). The mod, in `mod/`, plays
the steps, measures the parts the scene marks, and asks KSA Metal for each frame's GPU time and for the
screenshots. It is built on your Mac against your copy of the game, so it never ships any of the game's files.
`report/` holds the small program that writes the report and compares runs.

The rockets in `vehicles/` were built by the project for these scenes. The `eva` scene uses the game's own Gemini 7
capsule, copied from your copy of the game.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset high` | fixed settings in a 1280x800 window without V-Sync, the same on every Mac (default: your settings) |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |
| `<scene> ...` | play only these scenes |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results |
| `deps/benchmark/` | the built mod and report program, and the game's documents folder for the run |
| `deps/dotnet-sdk/` | Microsoft's .NET SDK, to build them |
