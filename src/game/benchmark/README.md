# Benchmark

The port's benchmark measures how KSA runs on your Mac. It has three tests, each played in one session of the game. It
measures each part and writes a report you can compare with another run or share.

| Test | What it plays | What it is for |
|---|---|---|
| **Mixed Test** (`mixed`, the default) | rockets, smoke, a 2,000-part rocket blown up, a kitten and a cockpit | what the tour doesn't check: plumes, explosions, part physics, fur, ray tracing |
| **Planetary Tour** (`tour`) | a flight from Earth to Jupiter | terrain, ground clutter, the ocean, clouds, rings |
| **Warp Test** (`warp`) | a ship in orbit at the top speed of time warp, alone and with debris around Earth | how far time warp gets in a fixed time |

`full` plays all three, one after the other, in one game.

It is for spotting differences: between two settings, two versions of the port, or two Macs. Since it plays the same
way every time, it is also the project's test: every run passes or fails, and `compare` checks its screenshots
against another run's.

## Run it

In the game, open the settings (Esc), then **Mac Port**, and press a test's **FAST** or **CINEMATIC** button at the
bottom (**RUN** for the Warp Test), with your own settings. It plays in the game you have open. Your game is saved
first, the test plays in a new game's world, and your game comes back when it ends; Esc stops it. With **Restart for a
Clean Run** on, your game is saved as "Before the benchmark" and the game starts again for it instead, the way
`./benchmark.sh` plays it, and quits at the end with the report open. **Show report** under the buttons opens the last
report.

From the port folder, after `./port.sh`:

```bash
./benchmark.sh              # the Mixed Test, about 4 minutes (--fast: under a minute)
./benchmark.sh tour         # the Planetary Tour, about 5 minutes (--fast: about 2)
./benchmark.sh warp         # the Warp Test, 2 to 3 minutes
./benchmark.sh full         # all three, about 11 minutes (--fast: about 4)
./benchmark.sh list         # what can be played
```

They use your own settings. The game takes over the screen: leave the mouse and keyboard alone until the script says it
is done, or press Ctrl-C in the terminal to stop the run and keep what it measured. On a Mac with 8 GB of memory, add
`--system earth`: it plays Earth and the Moon only and needs a few GB less.

A run in the game you have open starts with that game's clouds and sea, and after what the game played before, so its
numbers compare best with other runs played the same way, and `compare` doesn't check its screenshots.

### Reading a run

The terminal shows the result and each part's numbers. `report.html` in the run's folder shows them with the run's
screenshots and charts:

```bash
./benchmark.sh report       # the last run, here and in your browser
./benchmark.sh runs         # your last runs, with their main numbers
```

To compare two runs:

```bash
./benchmark.sh compare <first run> <second run>
./benchmark.sh compare <first run>      # against the last run
./benchmark.sh compare                  # the last run against the one before it
```

A run is its folder's name (`2026-11-02_0158`), its path, its zip, `last` or `previous`. The comparison starts with a
verdict, lists what differs between the two Macs and their settings, shows each part's numbers side by side, and names the
screenshots that changed. `compare.html` in the second run's folder shows the screenshots of both runs with a slider to go
from one to the other, and a copy of each changed screenshot, with the changed blocks marked in red, goes to `diff/` there.

Runs add up, up to a few hundred MB each with their screenshots. `./benchmark.sh clean` deletes all but the last 30, after
asking (`--keep <n>` keeps another number).

### Sharing a run

Each run writes its results to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`, and a zip of them in that
folder (`<date>.zip`).
The zip holds the report, every frame's numbers, the game's log and any crash report of the run, the settings and the
screenshots as JPEG; your home folder's name is replaced by `~` throughout. To share one, open an issue with the
**Benchmark report** template and attach the zip. For numbers that compare across Macs, run with a preset
(`--preset low`, `medium` or `high`): fixed settings that are the same on every Mac. `compare` takes a zip in place
of a run folder.

### Two ways to play it

By default a test is cinematic: the camera flies the whole way and the parts are measured as it goes. `--fast` is a short
test instead, for checking that a change kept the image: the game pauses and the camera jumps from view to view, measuring
each one standing still. The Mixed Test sets its vehicles up with the camera waiting on a kitten floating in orbit, then
shows the 2,000-part rocket blown up on its pad, a rocket lifting off and one high over the coast, and a cockpit in orbit.
The Planetary Tour shows each of its places with no flight between them. The screenshots match from one run to the next:

```bash
./benchmark.sh --fast
# ...make the change, then:
./benchmark.sh --fast --compare last
```

The two modes measure different things, so a fast run compares only with another fast run. The Warp Test plays one way
(below).

You can also play part of a test: name sections (`./benchmark.sh --fast jupiter`) or places by their screenshots'
names (`./benchmark.sh --fast earth4 jupiter2`; `giga_pad` where two sections have a screenshot of that name). A
place plays its section up to that point. In fast mode the camera jumps straight to the places of the flight between
the worlds, and the sections with vehicles play as written.

### The Warp Test

Each part starts from a new game's world and takes the same time on any Mac, from 8 to 30 seconds, whatever the game
gets through in it: a slow Mac or settings that hold warp back get less far, and the run never waits for them. The ship is asked for its speed at once, as a player holding the warp key does. The report's headline is how many game days
the parts got through, and its table shows, for each part, the speed reached against the speed asked, how long it took to
get there, the worst frame and the physics' work. Runs differ more here than in the other tests: use `--repeat 3` to judge
a change.

With the Extra Performance warp rows off, time warp can stall for most of a minute and the game may stop with an error in
the part where pieces fall back. The run then fails, keeping the parts it finished.

## For work on the port

The same tests are the project's tool for looking into the driver, the shaders and the game: settings changed for one
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

With `--trace`, `--capture` and `--hud`, KSA Metal names every encoder after its pipelines (the `labels` setting), so
the tools show `1280x800 v1658c4ba926081e2+f7e01e1b174a96688 +1` (the render pass's size, the vertex and fragment
shaders' hashes, how many other pipelines it ran) instead of an anonymous encoder.

```bash
# Where does jupiter2's GPU time go?
./benchmark.sh --fast --trace jupiter2
# Is a shader change faster where it matters?
./benchmark.sh --fast --repeat 3 earth4 jupiter2      # before
./benchmark.sh --fast --repeat 3 earth4 jupiter2 --compare <run before>
```

## The tests' sections

| Section | Test | What it shows | What it measures |
|---|---|---|---|
| `earth` | tour | the whole Earth, down through the clouds to the sea off Brazil, then the jungle, the Andes and the coast of Peru, and up over northern Quebec | atmosphere, clouds, the ocean, terrain and ground clutter; new ground streaming in all along |
| `moon` | tour | the flight out to the Moon, its disk, the crater Copernicus, then low over the ground in the east | terrain, the long shadows of craters and rocks |
| `mars` | tour | the flight on to Mars, its disk, sunrise over a dust storm, then low over rocky ground | terrain, the dusty air, rocks |
| `saturn` | tour | the rings from above, the haze under them, then the ring plane with its rocks going by | rings, their rocks |
| `jupiter` | tour | Jupiter from afar, its cloud deck, then straight down into the layers under it | volumetric clouds |
| `capsule` | mixed | a capsule's cockpit while a day goes by in twenty seconds, then a kitten climbing out of another capsule in tall grass, its long shadow, its face | ray tracing in the cockpit (when your settings have it on), fur, grass and shadows |
| `launch` | mixed | a rocket with solid boosters lifting off from LC-39A, seen from the ground, then from alongside as it climbs through the clouds, faster in between, and close to its engines up high | plumes, smoke trails, clouds, part physics |
| `giga` | mixed | a rocket of about 2,000 parts on its pad, lifting off, blown up whole, its fire and smoke, the camera turning around it all along | part physics on the CPU, many draw calls, lights, explosions and smoke |
| `mixed` | mixed, fast | a kitten in its helmet floating in orbit, looking into the camera, then the vehicles at one moment, the game paused: the giga rocket blown up whole in the smoke of its boosters on its pad at Starbase, a rocket lifting off from LC-39A, one high over the coast from Vandenberg, and a capsule's cockpit in orbit with sunlight on its wall (with IVA Ray Tracing on) and Earth in its window | plumes, smoke trails, an explosion and smoke, part physics on the CPU, many draw calls, ray tracing in the cockpit, the kitten |
| `warpclean` | warp | a capsule in a 400 km orbit at the top speed of time warp | what the picture costs at that speed |
| `warpdebris` | warp | the same among 18 rockets blown up around Earth in orbits from 150 to 800 km | hundreds of pieces in orbit, the bubbles of physics they make |
| `warpfalling` | warp | the same with 18 rockets blown up high, their pieces falling back and landing | pieces in the air and on the ground holding warp back |

The camera flies from one world to the next without a cut and eases to a stop for each screenshot. The Mixed Test's
vehicles are all placed at its start, in a new game's world. Each of its sections starts at a fixed game time, so its
light and clouds are the same whether or not the sections before it played.

## How it works

The benchmark is in the app, in `Contents/Resources/Benchmark/`, and [KSA Metal Hooks](../hooks/) give it every frame
of the game. For a run of `./benchmark.sh`, the game starts from a documents folder of its own,
`~/Library/Caches/ksa-macos-port/benchmark/instance/`. That folder holds a copy of your settings (with the preset on
top, if any) and of your port settings (the defaults with a preset), and no mods, so your saves, settings and mods are
never touched. The command changes only the settings that start the game straight into the test (no system menu, the
chosen system, the game's test vessel in orbit); every quality setting stays yours or the preset's. A run from the
game with **Restart for a Clean Run** starts the same way. A run in the game you have open keeps its system: the
benchmark saves your game, sets the world to a new game's (the clock at its start, no vehicles), and loads your game
again at the end. It adds the tests' vehicles to the game's list of vehicles from the app, and writes the report of a
run from the game.

The tests are a text file, [`lineup/benchmark.txt`](lineup/benchmark.txt), next to their vehicles
(`lineup/vehicles/`): sections of steps timed in seconds, each a console command or a directive of the benchmark's
director (`Directives.cs` lists them), and a `test` line for each test with the sections it plays in each mode. The
camera's flight is given by `@key` steps, places the camera passes through (`Path.cs`); around the vehicles, the
camera follows them or stands on the ground (`Rig.cs`). The director, in this folder, starts and ends the runs in the
game, after every frame the hooks give it (`Runs.cs`), plays the steps (`Director.cs`), stops at the checkpoints for
the screenshots (`Checkpoints.cs`) with the world put back the same way at each (`World.cs`), moves the camera,
measures the parts each section marks, and asks KSA Metal for each frame's GPU time and for the screenshots.
`./build.sh` compiles it against the KSA version the port is made for.
[`../../player/benchmark-cli/`](../../player/benchmark-cli/) holds the benchmark's program, compiled against nothing
of the game: the command that `./benchmark.sh` starts on the port folder's .NET runtime (`Command.cs`; a run's options
and checks `Run.cs`, its documents folder and settings `RunSettings.cs`, the game's sessions, Ctrl-C and the tools
`RunGame.cs`), and the report, which describes the Mac and writes the report, its page and the zip (`Report.cs`,
`Finish.cs`, `Html.cs`, `Terminal.cs`), compares runs (`Compare.cs`) and sums up the tools' output (`Tools.cs`). The
director and the report program share the PNG reader and a run's options (`Images.cs`, `RunOptions.cs`, in the port's
shared library, [`../../shared/`](../../shared/)). The program writes the options as `run.json` in the run's folder
and starts the game with `KSA_BENCH_OUT` naming the folder; the director reads them there, as it builds the options of
a run from the Mac Port tab. The build then runs its tests, which need no game, and stops when one fails. Those in
[`../../player/benchmark-cli/tests/`](../../player/benchmark-cli/tests/) check what passes and fails a run, the
conditions the report notes, repeats merged into medians, numbers the same on a Mac in any region, what `finish` takes
from the game's log and puts in the zip, the comparison of two runs, the PNG reader, and `benchmark.txt` itself (its
tests, sections, directives, screenshots and vehicles). They write their runs with the director's own classes, so they
also check that the report reads what the director writes.
[`../../player/benchmark-cli/tests/command.sh`](../../player/benchmark-cli/tests/command.sh) plays
[`benchmark.sh`](../../player/port-folder/benchmark.sh), the command, against a stand-in for the game: its commands,
what it refuses, and a run that passes, crashes, hangs or is stopped.

Each screenshot is a checkpoint. The game runs its simulation as fast as the Mac allows, so a slower Mac falls a
little behind the script. At a checkpoint the script waits for the game to reach the moment it should be at, pauses it
there and takes the screenshot, so the screenshots show the same moments, in the same light, on every Mac. `@sync`
waits the same way without a screenshot, so the step after it (an ignition, a kitten's first step) comes at the same
game time on every Mac.

In fast mode the game stays paused through the flight between the worlds, and the script's clock jumps from one
screenshot to the next, so the light and the clouds there are those of the game's start. The sections with vehicles
play as written, as in the cinematic run. At a checkpoint the director takes a screenshot every quarter of a second
until two in a row match, which means the ground and textures that stream in after a jump have settled; then it
measures the view for a second and takes the real screenshot. The sea's waves take the wind of the place at once (the
game eases them over several seconds), and at each checkpoint the clouds' detail starts afresh where the camera is, so
a place of the flight looks the same whatever was played before it. Fast mode plays in the game's smaller system
(Earth and the Moon) when the places played are all there, since the other worlds' textures would only take memory.

A live section, the Warp Test's, plays by the Mac's clock alone: no catching up, and a checkpoint stops where the game has
got to. Its screenshots are there to look at, since the game's clock differs from run to run; the `@shot` lines allow all
of them to change.

A run fails when the game crashed or hung, a step of the test failed, a vehicle broke apart before it should, a
screenshot is missing, the game's log has errors (during the Warp Test the game's own errors are only noted, since settings that hold warp back cause them), one of the hooks was skipped (the game's code it changes has moved) or
failed, the game could not read the run's settings, a screenshot is one colour from edge to edge or 30 % of it or more
pure white (a real view reaches about a fifth), or another game ran at
the same time; the script then exits with 1. A run you stop (Esc, Ctrl-C) ends with what it measured and exits with 1 too.
Before the game starts, the script refuses what would waste the run: a value a port setting can't take (the game would
ignore it and play unchanged), a `--compare` run that shares nothing with this one or played in another mode, an app or a
test that isn't there. It warns when the GPU is already busy.
The director touches a file every second; a game that hasn't touched it for a minute (three in the Warp Test, whose frames can stall) counts as hung and is stopped. In a run from the game, the hooks stop a game with no frame for three minutes.

`compare` reads GPU time first, as frame times sit on the display's beat. It marks a number as changed when it moved by
more than 5%, because two runs on the same Mac differ by a few
percent in most parts; the flights through space vary more, with little on screen and a high frame rate. Between
repeated runs it marks a change when the ranges don't overlap and the difference is over 2%. Screenshots are compared
in blocks of 16 by 16 pixels: a block counts as changed when its mean colour moved by more than 12 (of 255), and a
screenshot differs when more than 1% of its blocks changed (3% in a cinematic run, whose screenshots vary a little
with the frames the Mac happened to draw on the way). Normal run-to-run differences stay within that; a missing
texture or a broken shader does not. A few views change every run because of physics, particles or waves (the kitten
after its walk, the plumes, the explosion, the sea close up), and their `@shot` lines in `benchmark.txt` allow more, about
three times what repeated runs showed. The Planetary Tour in fast mode stands paused and comes out the same to the pixel, so
those allowances don't apply there.

Every frame of a run goes into `frames.csv`: the script's time, the section and part, frame and GPU time, draws and
passes, memory, thermal state, shaders compiled so far, and the time between the last two images the display showed.
The report's numbers are summaries of it, and it goes into the zip, so a shared run can answer questions nobody asked
when it was made. While a part is measured, KSA Metal also marks it as a signpost interval, which is how an Instruments
trace knows where each part is.

The rockets and the capsule in `lineup/vehicles/` were built for the tests.

### Changing the tests

`--explore` starts the benchmark's game as a run would, with its clock paused so the light matches the flight's. Fly the
free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is written as a `@key` line to
`keys.txt` in a new run folder and copied to the clipboard, ready to paste into `benchmark.txt`. `./build.sh --install`
puts the tests from `src/game/benchmark/lineup/benchmark.txt` into the app.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset medium`, `--preset high` | fixed settings in a 1280x800 window without Vsync, the same on every Mac (default: your settings) |
| `--defaults` | the settings of a fresh install instead of yours: the game's own defaults and the port settings' (a preset, `--set` and `--port` go on top) |
| `--system earth` | Earth and the Moon only (the game's smaller system): skips Mars, Saturn and Jupiter, and needs less memory (default: `sol`; `earth` in fast mode and the Warp Test when what plays is all on Earth or the Moon) |
| `mixed`, `tour`, `warp`, `full` | the test to play (default: `mixed`) |
| `--fast` | the test's short form, or the sections and places named with the camera jumping between places; measures the views standing still (default: cinematic; the Warp Test has one form) |
| `--no-shots` | take no screenshots |
| `--set <key>=<value>` | change a setting of `settings.toml` for the run, on top of the preset (repeat for more; strings need no quotes); as `<section>.<key>=<value>` it adds a key your file lacks to that section, such as one a newer KSA added (`display.hdr=true`) |
| `--settings <file>` | apply the settings in a file of `key = value` lines, on top of the preset and before `--set` |
| `--port <key>=<value>` | change a [port setting](../../../README.md#the-mac-port-tab) for the run, on top of yours (with a preset or `--defaults`, on top of the defaults); the report shows the port settings and what the hooks did |
| `--compare <run>` | compare with that run at the end (its folder, zip or name, `last`, `previous`), checked before the game starts; the script fails when screenshots differ |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |
| `<section or place> ...` | play only these sections, or a section up to the named place (a screenshot's name) |
| `--hold <place>` | jump to that place and stay there, paused, until you quit the game |
| `--repeat <n>` | play it n times, one game session each; the report shows medians and ranges |
| `--profile` | KSA Metal's GPU profiler on every measured part: `gpuprof.csv`, `profile.md` |
| `--trace`, `--trace-template <name>` | an Instruments trace of the run: `trace.trace`, `trace.md` (needs Xcode, 10 GB free, and sections or places) |
| `--capture` | an Xcode GPU frame capture at each place played: `<section>_<shot>.gputrace` (needs places, 20 GB free) |
| `--catch <run>` | for a picture that goes wrong now and then, with no capture mode or other switch: KSA Metal looks at the image a few times a second, and each screenshot is compared with that clean run's (30 % pure white, under half the size, or far more pure black). A broken one gets a pass dump in `caught/`: `watch_<n>/` from the image, `<section>_<shot>/` from a screenshot, with `passes.txt` and an image of a frame after it; `watch.txt` says when each fault began and ended (at most 3 of each; about 40 MB each; needs a comparable run with screenshots and 1 GB free). After a GPU failure nothing more is dumped |
| `--catch-capture` | with `--catch`: an Xcode GPU frame capture after each pass dump, `caught/<section>_<shot>.gputrace` (capture mode, about 9 GB each, 40 GB free) |
| `--passes` | a pass dump at every place played, in `caught/`: the clean run a caught dump is compared with (needs places, or `--fast`) |
| `--film <seconds>` | a screenshot every that many seconds of the script's clock, in `film/`, to see the camera's moves |
| `--camera-log` | the camera's place every frame in `trace.csv`, to check its moves are smooth |
| `--hud` | Apple's Metal Performance HUD, its log in `hud.log` and its reports in `hud/` |
| `--explore` | start the benchmark's game paused, with a console command (`benchkey`) that captures the camera's place for the tour |

| Command | |
|---|---|
| `list` | the tests, their sections and their places |
| `runs [n]` | your last runs (15, or n), the newest first |
| `report [run]` | a run's report in the terminal, and its page in your browser (default: the last run) |
| `compare [run] [run]` | how the second run differs from the first (with one run: against the last; with none: the last against the one before it); exits with 1 when screenshots differ, 2 when the runs don't compare |
| `clean [--keep n] [--yes]` | delete all but your last runs (30, or n), after asking (`--yes`: without asking); only folders a run leaves, and not `--explore`'s |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results: `report.html`, `report.md`, `report.json`, `frames.csv`, the screenshots, `game.log`, `compare.html` and `compare.md` after a comparison, and what the tools wrote |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/<date>.zip` | the run to share |
| `~/Library/Caches/ksa-macos-port/benchmark/instance/` | the game's documents folder for a run that starts the game |
| `Contents/Resources/Benchmark/` in the app | the director, the report program, the tests and their vehicles |
