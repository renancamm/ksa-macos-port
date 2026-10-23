# Benchmark

An optional extra that measures how KSA runs on your Mac. It plays a fixed tour of the game in one session, a camera
flight from Earth to Jupiter without a cut, then a day going by in a capsule's cockpit, a kitten in the grass, a
rocket launch and a 2,000-part rocket blown up on its pad, measures each part of it, and writes a report you can
compare with another run or share.

It is meant for spotting changes: between two settings, two versions of the port, or two Macs. Two runs on the same
Mac differ by a few percent in most parts, so `compare` marks changes over 5% (the flights through space vary more:
little is on screen there, and the frame rate is high). Since it plays the same way every
time, it also serves as the project's test: every run passes or fails, and `compare` checks its screenshots against
another run's.

## Run it

From the project's folder, after `./port.sh`:

```bash
./extras/benchmark/benchmark.sh
```

It takes about 8 minutes. The game takes over the screen: leave the mouse and keyboard alone until the script says
it is done. The first run also downloads Microsoft's .NET SDK (about 200 MB) to build the benchmark mod, and adds the
[StarMap](../starmap/README.md) mod loader to the app when it isn't there yet.

The results go to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`: `report.md` with the numbers,
`report.json` for comparing, the screenshots, and the game's log. The report starts with whether the run passed: it
fails when the game crashed or hung, a step of the tour failed, a vehicle broke up before its time, a screenshot is
missing, the game's log has errors (KSA Metal's, the game's own, or exceptions), or another game ran at the same time.
The script then exits with 1.

To compare two runs:

```bash
./extras/benchmark/benchmark.sh compare <first run folder> <second run folder>
```

It lists the settings that differ between them, the numbers of each part side by side, and whether each screenshot
still looks the same. Copies of those that differ, with the changed places in red, go to `diff/` in the second run's
folder.

### Two ways to play it

The default is the cinematic tour above, for watching: the camera flies all the way, and the numbers are measured
along the way. With `--fast` the tour skips the way: it jumps from one screenshot's place to the next, lets the game
catch up there, measures the view standing still for two seconds and takes the screenshot. It takes about 4 minutes,
and its screenshots come out the same from one run to the next, so it is the quick way to check a change:

```bash
./extras/benchmark/benchmark.sh --fast
# ...make the change, then:
./extras/benchmark/benchmark.sh --fast --compare <first run folder>
```

The two ways measure different things (the flight, or the views standing still), so a fast run compares only with
other fast runs. `--no-shots` plays either way without taking screenshots, for the numbers alone.

To look at one place closely, `--hold <screenshot>` (for example `--hold earth4` or `--hold launch_pad`) goes there
the fast way and stays, the game paused, until you quit it: for Apple's performance overlay, or a GPU capture in
Xcode. `--set <key>=<value>` changes one setting for a run (again for more), to see what it does.

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
| `capsule` | a capsule's cockpit while a day goes by in twenty seconds, then a kitten climbing out of another capsule in tall grass, its long shadow, its face | ray tracing in the cockpit (when your settings have it on), fur, grass and shadows |
| `launch` | a rocket with solid boosters lifting off from LC-39A, seen from the ground, then from alongside as it climbs through the clouds, faster in between, and close to its engines up high | plumes, smoke trails, clouds, part physics |
| `giga` | a rocket of about 2,000 parts on its pad, lifting off, blown up whole, its fire and smoke, the camera turning around it all along | part physics on the CPU, many draw calls, lights, explosions and smoke |

The camera flies from one world to the next without a cut, and eases to a stop for each screenshot. With
`--system earth` the flight ends at the Moon. The vehicles of the last three sections are all put in their places at
once when the tour reaches Earth again, so the only cut is there; each of those sections starts at a set time of the
game, so its light and clouds are the same whether the sections before it played or not.

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
through (`mod/Path.cs`); around the vehicles, the camera goes along with them or stands on the ground (`mod/Rig.cs`).
The mod, in `mod/`, plays the steps, moves the camera, measures the
parts the sections mark and asks KSA Metal for each frame's GPU time and for the screenshots. It is built on your Mac
against your copy of the game, so it never ships any of the game's files. `report/` holds the small program that
writes the report and compares runs.

Each screenshot is a checkpoint. The game runs its simulation as fast as the Mac allows, so a slower Mac can fall a
little behind the tour; at a checkpoint the tour waits for the game to reach the moment it should be at, pauses it
there and takes the screenshot. So the screenshots show the same moments on every Mac, and so does the light.

In fast mode the tour's clock jumps from one step to the next, and before each step the game catches up with the
moment it should be at and pauses, so the vehicles get their controls at the same moments of the game as in the
cinematic tour. At a checkpoint the mod takes a screenshot every half second until two in a row are the same (the
ground and textures streaming in after the jump have settled), then measures and takes the real one. Stretches where a
stop would break a vehicle up (the 2,000-part rocket's flight) play as they are. The clouds are the one thing the two
ways show differently: the game moves their patterns with the camera's way there, so the same place at the same moment
has other clouds after a jump than after a flight.

Screenshots are compared in blocks of 16 by 16 pixels: a block changed when its mean colour moved by more than 12 (of
255), and a screenshot differs when more than 1% of its blocks changed (3% in the cinematic tour, whose screenshots
vary a little with the frames the Mac happened to draw on the way). Small differences from run to run stay within
that, while a missing texture or a broken shader does not. A few views change every run, because of physics,
particles or the sea's waves (the kitten after its walk, the plumes, the explosion, the sea close up): their `@shot`
lines in `benchmark.txt` allow more.

While the game plays, the mod touches a file every second; a game that stops touching it for a minute has hung and is
stopped.

The rockets and the capsule in `vehicles/` were built by the project for the tour.

### Changing the tour

`--explore` starts the benchmark's game the same way as a run, with its clock paused so the light is the tour's. Fly
the free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is added as a `@key` line to
`keys.txt` in a new run folder, and copied to the clipboard, ready for `benchmark.txt`.

## Options and files

| Option | |
|---|---|
| `--fast` | jump from one screenshot's place to the next, and measure the views standing still (default: the cinematic tour) |
| `--no-shots` | take no screenshots |
| `--hold <screenshot>` | go to that screenshot's place the fast way and stay there, paused, until you quit the game (`earth4`, or `launch_pad` where a name is in more than one section) |
| `--set <key>=<value>` | change a setting of `settings.toml` for the run, on top of the preset (again for more; words need no quotes) |
| `--compare <run folder>` | compare with that run at the end; the script fails when screenshots differ |
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
