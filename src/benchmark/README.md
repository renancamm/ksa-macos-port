# Benchmark

An optional extra that measures how KSA runs on your Mac. It plays a fixed tour of the game in one session, a camera
flight from Earth to Jupiter without a cut, then a day going by in a capsule's cockpit, a kitten in the grass, a
rocket launch and a 2,000-part rocket blown up on its pad, measures each part of it, and writes a report you can
compare with another run or share.

It is meant for spotting changes: between two settings, two versions of the port, or two Macs. Two runs on the same
Mac differ by a few percent in most parts, so `compare` marks changes over 5% (the flights through space vary more:
little is on screen there, and the frame rate is high). Since it plays the same way every time, it also serves as
the project's test: every run passes or fails, and `compare` checks its screenshots against another run's.

## Run it

From the port folder, after `./port.sh`:

```bash
./benchmark.sh
```

It takes about 8 minutes, with your own settings. The game takes over the screen: leave the mouse and keyboard alone
until the script says it is done. The first run adds the [StarMap](../../README.md#mods) mod loader to the app when it
isn't there yet. On a Mac with 8 GB of memory, `--system earth` plays Earth and the Moon only, which needs a few GB
less.

The results go to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`: `report.md` with the numbers,
`report.json` for comparing, `frames.csv` with every frame of the run, the screenshots, and the game's log. The report
starts with whether the run passed: it fails when the game crashed or hung, a step of the tour failed, a vehicle broke
up before its time, a screenshot is missing, the game's log has errors (KSA Metal's, the game's own, or exceptions),
or another game ran at the same time. The script then exits with 1. Below that, the report lists the conditions
worth knowing that don't fail a run but can change its numbers: running on battery or in Low Power Mode, a Mac hot
enough to slow down, macOS short of memory or swapping, shaders compiled while measuring (the first run after an
update compiles them, the next runs don't).

To compare two runs:

```bash
./benchmark.sh compare <first run> <second run>
```

It lists what differs between the two Macs and their settings, the numbers of each part side by side, and whether
each screenshot still looks the same. Copies of those that differ, with the changed places in red, go to `diff/` in
the second run's folder.

### Sharing a run

Each run also leaves a zip next to its folder, `<date>.zip`: the report, the numbers of every frame, the game's log
and any crash report from the run, the settings, and the screenshots as JPEG, with your home folder's name replaced by
`~`. Whether you share it is up to you. Reports from other Macs are very welcome: open an issue with the **Benchmark
report** template and attach the zip. `compare` reads zips as they are.

For numbers that compare across Macs, run with a preset as well: `--preset low`, `--preset medium` or
`--preset high`, fixed settings in a 1280x800 window without V-Sync, the same on every Mac.

### Two ways to play it

The default is the cinematic tour above, for watching: the camera flies all the way, and the numbers are measured
along the way. With `--fast` the tour skips the way: it jumps from one screenshot's place to the next, lets the game
catch up there, measures the view standing still for two seconds and takes the screenshot. It takes about 4 minutes,
and its screenshots come out the same from one run to the next, so it is the quick way to check a change:

```bash
./benchmark.sh --fast
# ...make the change, then:
./benchmark.sh --fast --compare <first run>
```

The two ways measure different things (the flight, or the views standing still), so a fast run compares only with
other fast runs. `--no-shots` plays either way without taking screenshots, for the numbers alone.

The tour can also play in part: name sections (`./benchmark.sh --fast jupiter`) or single places, by their
screenshots' names (`./benchmark.sh --fast earth4 jupiter2`, or `capsule_fur` where a name is in more than one
section). A place plays its section up to there, so one place in fast mode is done in well under a minute.

## For work on the port

The same tour is the project's tool for looking into the driver, the shaders and the game. These options are for
that, and some need tools a player's Mac doesn't have; the script checks what each one needs before the game starts.

- `--set <key>=<value>` changes one setting for a run (again for more), and `--settings <file>` the settings in a file
  of `key = value` lines, as in `settings.toml`. Both go on top of the preset.
- `--repeat <n>` plays the run n times, one game session each, and reports the median of each number and the range of
  the runs' frame and GPU times. `compare` between two repeated runs marks a change when the ranges don't overlap
  (and it is over 2%), so a small real change shows, and noise doesn't.
- `--hold <place>` goes to a place the fast way and stays, the game paused, until you quit it: to look at it closely,
  with Apple's overlay or Xcode.
- `--profile` turns on KSA Metal's GPU profiler for 60 frames of every measured part. `profile.md` lists, per part,
  the encoders that cost the most GPU time, named after their shaders' hashes. Its timestamps count an encoder from
  its start to its end, waits included, so it shows where to look rather than exact costs.
- `--trace` records an Instruments trace (the Metal System Trace template, or another with `--trace-template
  <name>`) of the places played, with each measured part marked under Points of Interest. `trace.md` sums up, per
  part, the GPU time of the game's encoders by the GPU's channels (compute, vertex, fragment), as Instruments measured
  it. Needs Xcode; a trace takes a few hundred MB for a few places, so it asks for sections or places.
- `--capture` makes an Xcode GPU frame capture at each place played, `<section>_<shot>.gputrace`, to open in Xcode:
  every draw and dispatch, its resources, and the shader profiler and debugger. A capture holds all of the game's GPU
  memory (about 8 GB), so it asks for places and 20 GB free.
- `--hud` shows Apple's Metal Performance HUD over the game, with its per-frame log in `hud.log`. Its menu (in the
  menu bar while the game runs) makes performance reports, which go to `hud/`. It can't run with `--profile`: both use
  the GPU's counters.

With `--trace`, `--capture` and `--hud`, KSA Metal names every encoder after its pipelines (`KSA_MTL_LABELS=1`), so the
tools show `1280x800 v1658c4ba926081e2+f7e01e1b174a96688 +1` rather than an anonymous encoder. Their own cost is in the
numbers of such runs, and the report says so.

```bash
# Where does jupiter2's GPU time go?
./benchmark.sh --fast --trace jupiter2
# Is a shader change faster where it matters?
./benchmark.sh --fast --repeat 3 earth4 jupiter2      # before
./benchmark.sh --fast --repeat 3 earth4 jupiter2 --compare <run before>
```

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

The game runs from its own documents folder, `~/Library/Caches/ksa-macos-port/benchmark/instance`, which StarMap
points it at: a copy of your
settings (with the preset on top, if any), none of your mods, and the tour's vehicles. Your saves, settings and mods
are never touched. The script only chooses the settings it needs to start straight into the tour (no system menu, the
chosen system, the game's test vessel in orbit); every quality setting stays yours or the preset's.

The tour is a text file, `benchmark.txt`: sections of steps timed in seconds, each a console command or a directive of
the benchmark mod (`mod/Directives.cs` lists them). The camera's flight is its `@key` steps, places the camera passes
through (`mod/Path.cs`); around the vehicles, the camera goes along with them or stands on the ground (`mod/Rig.cs`).
The mod, in `mod/`, plays the steps, moves the camera, measures the
parts the sections mark and asks KSA Metal for each frame's GPU time and for the screenshots. `./build.sh` compiles it
against the KSA version the port is made for; the game's assemblies are only references, so the mod holds none of the
game's files. `report/` holds the small program that writes the report and compares runs.

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

Every frame of a run goes into `frames.csv`: the script's time, the section and part, the frame and GPU time, draws
and passes, memory, thermal state, shaders compiled so far and the time between the last two images the display
showed. The parts' numbers in the report are summaries of it, and it stays in the zip, so a shared run can answer
questions nobody asked when it was made. While a part is measured, KSA Metal also marks it as a signpost interval,
which is how an Instruments trace knows the parts.

### Changing the tour

`--explore` starts the benchmark's game the same way as a run, with its clock paused so the light is the tour's. Fly
the free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is added as a `@key` line to
`keys.txt` in a new run folder, and copied to the clipboard, ready for `benchmark.txt`. The tour goes into the port
folder with `./build.sh`, from `src/benchmark/benchmark.txt`.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset medium`, `--preset high` | fixed settings in a 1280x800 window without V-Sync, the same on every Mac (default: your settings) |
| `--system earth` | Earth and the Moon only (the game's smaller system): skips Mars, Saturn and Jupiter, and needs less memory (default: `sol`) |
| `--fast` | jump from one screenshot's place to the next, and measure the views standing still (default: the cinematic tour) |
| `--no-shots` | take no screenshots |
| `--set <key>=<value>` | change a setting of `settings.toml` for the run, on top of the preset (again for more; words need no quotes) |
| `--settings <file>` | change the settings in a file of `key = value` lines, on top of the preset and before `--set` |
| `--compare <run>` | compare with that run (folder or zip) at the end; the script fails when screenshots differ |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |
| `<section or place> ...` | play only these sections, or each section up to these places (screenshots' names) |
| `--hold <place>` | go to that place the fast way and stay there, paused, until you quit the game |
| `--repeat <n>` | play it n times, one game session each; the report shows medians and ranges |
| `--profile` | KSA Metal's GPU profiler on every measured part: `gpuprof.csv`, `profile.md` |
| `--trace`, `--trace-template <name>` | an Instruments trace of the run: `trace.trace`, `trace.md` (needs Xcode, and sections or places) |
| `--capture` | an Xcode GPU frame capture at each place played: `<section>_<shot>.gputrace` (needs places, 20 GB free) |
| `--hud` | Apple's Metal Performance HUD, its log in `hud.log` and its reports in `hud/` |
| `--explore` | start the benchmark's game paused, with a console command (`benchkey`) that captures the camera's place for the tour |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results: `report.md`, `report.json`, `frames.csv`, the screenshots, `game.log`, and what the tools wrote |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>.zip` | the run to share |
| `~/Library/Caches/ksa-macos-port/benchmark/instance/` | the game's documents folder for the run |
| `payload/benchmark/` in the port folder | the mod, the report program, the tour and its vehicles |
