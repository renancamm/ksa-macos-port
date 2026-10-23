# Benchmark

The port's benchmark is a mod that measures how KSA runs on your Mac. It plays a fixed tour of the game in one
session: a camera flight from Earth to Jupiter without a cut, then a day in a capsule's cockpit, a kitten in the grass,
a rocket launch and a 2,000-part rocket blown up on its pad. It measures each part and writes a report you can compare
with another run or share.

It is for spotting differences: between two settings, two versions of the port, or two Macs. Since it plays the same
way every time, it is also the project's test: every run passes or fails, and `compare` checks its screenshots
against another run's.

## Run it

From the port folder, after `./port.sh`:

```bash
./benchmark.sh
```

A run takes about 8 minutes and uses your own settings. The game takes over the screen: leave the mouse and keyboard
alone until the script says it is done. On a Mac with 8 GB of memory, add `--system earth`: it plays Earth and the
Moon only and needs a few GB less.

To compare two runs:

```bash
./benchmark.sh compare <first run> <second run>
```

It lists what differs between the two Macs and their settings, shows each part's numbers side by side, and names the
screenshots that changed. A copy of each changed screenshot, with the changed blocks marked in red, goes to `diff/` in
the second run's folder.

### Sharing a run

Each run writes its results to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`, and a zip of them in that
folder (`<date>.zip`).
The zip holds the report, every frame's numbers, the game's log and any crash report of the run, the settings and the
screenshots as JPEG; your home folder's name is replaced by `~` throughout. To share one, open an issue with the
**Benchmark report** template and attach the zip. For numbers that compare across Macs, run with a preset
(`--preset low`, `medium` or `high`): fixed settings that are the same on every Mac. `compare` takes a zip in place
of a run folder.

### Two ways to play it

By default the tour is cinematic: the camera flies the whole way and the parts are measured as it goes. With `--fast`
it jumps from one screenshot's place to the next and measures each view standing still. That takes about 4 minutes
and gives screenshots that match from one run to the next, so it is the quick way to check a change:

```bash
./benchmark.sh --fast
# ...make the change, then:
./benchmark.sh --fast --compare <first run>
```

The two modes measure different things, so a fast run compares only with another fast run.

You can also play part of the tour: name sections (`./benchmark.sh --fast jupiter`) or places by their screenshots'
names (`./benchmark.sh --fast earth4 jupiter2`; `giga_pad` where two sections have a screenshot of that name). A
place plays its section up to that point, so one place in fast mode takes well under a minute.

## For work on the port

The same tour is the project's tool for looking into the driver, the shaders and the game: settings changed for one
run (`--set`, `--settings`), repeated runs, and Apple's tools on the places played.

- `--repeat <n>` plays the run n times, one game session each, and reports the median of each number and the range of
  the frame and GPU times across the runs. Between two repeated runs, `compare` marks a change only when the ranges
  don't overlap, so small real changes show and noise doesn't.
- `--hold <place>` jumps to a place and stays there with the game paused, for a close look with Apple's overlay or
  Xcode.
- `--profile` turns on KSA Metal's GPU profiler for 60 frames of every measured part. `profile.md` lists, per part,
  the encoders that cost the most GPU time, named by their shader hashes. The timestamps span each encoder from start
  to end, waits included, so they show where to look rather than exact costs.
- `--trace` records an Instruments trace of the places played (the Metal System Trace template, or another with
  `--trace-template`), with each measured part marked under Points of Interest. `trace.md` sums up the GPU time per
  part by channel (compute, vertex, fragment), as Instruments measured it.
- `--capture` takes an Xcode GPU frame capture at each place played: Xcode's view of every draw and dispatch, its
  resources, and the shader profiler and debugger.
- `--hud` shows Apple's Metal Performance HUD over the game and saves its per-frame log as `hud.log`. The HUD's menu,
  in the menu bar while the game runs, can write performance reports; they go to `hud/`. It can't be combined with
  `--profile`, since both use the GPU's counters.

With `--trace`, `--capture` and `--hud`, KSA Metal names every encoder after its pipelines (`KSA_MTL_LABELS=1`), so
the tools show `1280x800 v1658c4ba926081e2+f7e01e1b174a96688 +1` (the render pass's size, the vertex and fragment
shaders' hashes, how many other pipelines it ran) instead of an anonymous encoder.

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
| `earth` | the whole Earth, down through the clouds to the sea off Brazil, then the jungle, the Andes and the coast of Peru, and up over northern Quebec | atmosphere, clouds, the ocean, terrain and ground clutter; new ground streaming in all along |
| `moon` | the flight out to the Moon, its disk, the crater Copernicus, then low over the ground in the east | terrain, the long shadows of craters and rocks |
| `mars` | the flight on to Mars, its disk, sunrise over a dust storm, then low over rocky ground | terrain, the dusty air, rocks |
| `saturn` | the rings from above, the haze under them, then the ring plane with its rocks going by | rings, their rocks |
| `jupiter` | Jupiter from afar, its cloud deck, then straight down into the layers under it | volumetric clouds |
| `capsule` | a capsule's cockpit while a day goes by in twenty seconds, then a kitten climbing out of another capsule in tall grass, its long shadow, its face | ray tracing in the cockpit (when your settings have it on), fur, grass and shadows |
| `launch` | a rocket with solid boosters lifting off from LC-39A, seen from the ground, then from alongside as it climbs through the clouds, faster in between, and close to its engines up high | plumes, smoke trails, clouds, part physics |
| `giga` | a rocket of about 2,000 parts on its pad, lifting off, blown up whole, its fire and smoke, the camera turning around it all along | part physics on the CPU, many draw calls, lights, explosions and smoke |

The camera flies from one world to the next without a cut and eases to a stop for each screenshot. The vehicles of
the last three sections are all placed when the tour returns to Earth, the tour's only cut. Each of those sections
starts at a fixed game time, so its light and clouds are the same whether or not the sections before it played.

## How it works

The benchmark is a [StarMap](../../README.md#mods) mod. For a run, the game starts through the port folder's copy of
StarMap and from a documents folder of its own, both under `~/Library/Caches/ksa-macos-port/benchmark/`. That folder
holds a copy of your settings (with the preset on top, if any), the tour's vehicles and no mod but the benchmark's, so
the app, your saves, settings and mods are never touched. The script changes only the settings that start the game
straight into the tour (no system menu, the chosen system, the game's test vessel in orbit); every quality setting
stays yours or the preset's.

The tour is a text file, `benchmark.txt`: sections of steps timed in seconds, each a console command or a directive of
the benchmark mod (`mod/Directives.cs` lists them). The camera's flight is given by `@key` steps, places the camera
passes through (`mod/Path.cs`); around the vehicles, the camera follows them or stands on the ground (`mod/Rig.cs`).
The mod in `mod/` plays the steps, moves the camera, measures the parts each section marks, and asks KSA Metal for
each frame's GPU time and for the screenshots. `./build.sh` compiles it against the KSA version the port is made for.
`report/` holds the small program that writes the report and compares runs.

Each screenshot is a checkpoint. The game runs its simulation as fast as the Mac allows, so a slower Mac falls a
little behind the tour. At a checkpoint the tour waits for the game to reach the moment it should be at, pauses it
there and takes the screenshot, so the screenshots show the same moments, in the same light, on every Mac.

In fast mode the tour's clock jumps from one step to the next; before each step the game catches up with that moment
and pauses, so the vehicles receive their controls at the same game time as in the cinematic tour. At a checkpoint the
mod takes a screenshot every half second until two in a row match, which means the ground and textures that stream in
after a jump have settled; then it measures the view for two seconds and takes the real screenshot. Stretches where
stopping would break a vehicle apart (the 2,000-part rocket's flight) play through as they are. Clouds are the one
thing the two modes show differently: the game moves their patterns with the camera's path, so the same place at the
same moment has different clouds after a jump than after a flight.

A run fails when the game crashed or hung, a step of the tour failed, a vehicle broke apart before it should, a
screenshot is missing, the game's log has errors, or another game ran at the same time; the script then exits with 1.
The mod touches a file every second; a game that hasn't touched it for a minute counts as hung and is stopped.

`compare` marks a number as changed when it moved by more than 5%, because two runs on the same Mac differ by a few
percent in most parts; the flights through space vary more, with little on screen and a high frame rate. Between
repeated runs it marks a change when the ranges don't overlap and the difference is over 2%. Screenshots are compared
in blocks of 16 by 16 pixels: a block counts as changed when its mean colour moved by more than 12 (of 255), and a
screenshot differs when more than 1% of its blocks changed (3% in the cinematic tour, whose screenshots vary a little
with the frames the Mac happened to draw on the way). Normal run-to-run differences stay within that; a missing
texture or a broken shader does not. A few views change every run because of physics, particles or waves (the kitten
after its walk, the plumes, the explosion, the sea close up), and their `@shot` lines in `benchmark.txt` allow more.

Every frame of a run goes into `frames.csv`: the script's time, the section and part, frame and GPU time, draws and
passes, memory, thermal state, shaders compiled so far, and the time between the last two images the display showed.
The report's numbers are summaries of it, and it goes into the zip, so a shared run can answer questions nobody asked
when it was made. While a part is measured, KSA Metal also marks it as a signpost interval, which is how an Instruments
trace knows where each part is.

The rockets and the capsule in `vehicles/` were built for the tour.

### Changing the tour

`--explore` starts the benchmark's game as a run would, with its clock paused so the light matches the tour's. Fly the
free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is written as a `@key` line to
`keys.txt` in a new run folder and copied to the clipboard, ready to paste into `benchmark.txt`. `./build.sh` copies
the tour from `src/benchmark/benchmark.txt` into the port folder.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset medium`, `--preset high` | fixed settings in a 1280x800 window without V-Sync, the same on every Mac (default: your settings) |
| `--system earth` | Earth and the Moon only (the game's smaller system): skips Mars, Saturn and Jupiter, and needs less memory (default: `sol`) |
| `--fast` | jump from one screenshot's place to the next, and measure the views standing still (default: the cinematic tour) |
| `--no-shots` | take no screenshots |
| `--set <key>=<value>` | change a setting of `settings.toml` for the run, on top of the preset (repeat for more; strings need no quotes) |
| `--settings <file>` | apply the settings in a file of `key = value` lines, on top of the preset and before `--set` |
| `--compare <run>` | compare with that run (folder or zip) at the end; the script fails when screenshots differ |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |
| `<section or place> ...` | play only these sections, or a section up to the named place (a screenshot's name) |
| `--hold <place>` | jump to that place and stay there, paused, until you quit the game |
| `--repeat <n>` | play it n times, one game session each; the report shows medians and ranges |
| `--profile` | KSA Metal's GPU profiler on every measured part: `gpuprof.csv`, `profile.md` |
| `--trace`, `--trace-template <name>` | an Instruments trace of the run: `trace.trace`, `trace.md` (needs Xcode, 10 GB free, and sections or places) |
| `--capture` | an Xcode GPU frame capture at each place played: `<section>_<shot>.gputrace` (needs places, 20 GB free) |
| `--hud` | Apple's Metal Performance HUD, its log in `hud.log` and its reports in `hud/` |
| `--explore` | start the benchmark's game paused, with a console command (`benchkey`) that captures the camera's place for the tour |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results: `report.md`, `report.json`, `frames.csv`, the screenshots, `game.log`, and what the tools wrote |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/<date>.zip` | the run to share |
| `~/Library/Caches/ksa-macos-port/benchmark/` | the game's documents folder for the run (`instance/`) and the StarMap it starts through (`starmap/`) |
| `payload/benchmark/` in the port folder | the mod, the report program, the tour and its vehicles |
