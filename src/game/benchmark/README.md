# Benchmark

The port's benchmark measures how KSA runs on your Mac. It has three tests, each starting in a new game's world. It
measures each part and writes a report you can compare with another run or share.

| Test | What it plays | What it is for |
|---|---|---|
| **Mixed Test** (`mixed`, the default) | rockets, smoke, a 2,000-part rocket blown up, a kitten and a cockpit, each view standing still | what the tour doesn't check: plumes, explosions, part physics, fur, ray tracing |
| **Planetary Tour** (`tour`) | a flight from Earth to Jupiter | terrain, ground clutter, the ocean, clouds, rings |
| **Warp Test** (`warp`) | a ship in orbit at the top speed of time warp, alone and with debris around Earth | how far time warp gets in a fixed time |

`full` plays all three, one after the other.

It is for spotting differences: between two settings, two versions of the port, or two Macs. Since it plays the same
way every time, it is also the project's test: every run passes or fails, and `compare` checks its screenshots
against another run's.

## Run it

In the game, open the settings (Esc), then **Mac Port**, and press a test's **RUN** button at the bottom, with your own
settings. It plays in the game you have open. Your game is saved first, the test plays in a new game's world, and your
game comes back when it ends; Esc stops it. With **Restart for a Clean Run** on, your game is saved as "Before the
benchmark" and the game starts again for it instead, the way `./benchmark.sh` plays it, and quits at the end with the
report open. **Show report** under the buttons opens the last report.

From the port folder, after `./port.sh`:

```bash
./benchmark.sh              # the Mixed Test, about a minute
./benchmark.sh tour         # the Planetary Tour, about 5 minutes
./benchmark.sh warp         # the Warp Test, about 3 minutes
./benchmark.sh full         # all three, about 9 minutes
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
verdict, lists what differs between the two Macs and their settings, shows each part's numbers side by side, and names
the screenshots that changed. `compare.html` in the second run's folder shows the screenshots of both runs with a slider
to go from one to the other, and a copy of each changed screenshot, with the changed blocks marked in red, goes to
`diff/` there.

Runs add up, up to a few hundred MB each with their screenshots. `./benchmark.sh clean` deletes all but the last 30,
after asking (`--keep <n>` keeps another number).

### Sharing a run

Each run writes its results to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`, and a zip of them in that
folder (`<date>.zip`). The zip holds the report, the run's record, every frame's numbers, the game's log and any crash
report of the run, the settings and the screenshots as JPEG; your home folder's name is replaced by `~` throughout. To
share one, open an issue with the **Benchmark report** template and attach the zip. For numbers that compare across
Macs, run with a preset (`--preset low`, `medium` or `high`): fixed settings that are the same on every Mac. `compare`
takes a zip in place of a run folder.

### The three tests

The **Mixed Test** sets its vehicles up with the camera waiting on a kitten floating in orbit, then pauses the game and
goes from view to view: the 2,000-part rocket blown up on its pad, a rocket lifting off and one high over the coast, a
cockpit in orbit. Each view is measured standing still once it stops changing, and its screenshot comes out the same
from one run to the next.

The **Planetary Tour** flies the camera from Earth to Jupiter without a cut and eases to a stop for each screenshot.
Its parts are measured along the way. Its screenshots vary a little with the frames the Mac happened to draw on the
way (the clouds' fine detail most), so a comparison allows each one what repeated runs showed.

In the **Warp Test** each part starts from a new game's world and takes the same time on any Mac, from 8 to 30 seconds,
whatever the game gets through in it: a slow Mac or settings that hold warp back get less far, and the run never waits
for them. The ship is asked for its speed at once, as a player holding the warp key does. The report's headline is how
many game days the parts got through, and its table shows, for each part, the speed reached against the speed asked,
how long it took to get there, the worst frame and the physics' work. Runs differ more here than in the other tests:
measure it (below) to judge a change. With the Extra Performance warp rows off, time warp can stall for most of a
minute and the game may stop with an error in the part where pieces fall back. The run then fails, keeping the parts
it finished.

You can also play part of a test: name places by their screenshots' names (`./benchmark.sh earth4 jupiter2`). A place
plays its scenario up to that point; with `--fast` the camera jumps straight to the places of the flight, the game
paused on the way.

## For work on the port

The same tests are the project's tool for looking into the driver, the shaders and the game. Five commands of
`benchmark.sh` are for that work.

| Command | What it does |
|---|---|
| `check [test]` | the standard test of a change: the test (default `mixed`) at a fresh install's settings, compared with the baseline `main` of that test, or else the last run of it that passed played the same way; exits with 1 when screenshots differ |
| `measure [test] [--repeat n] [--sessions n] [--ab key=value]` | numbers to judge a change by: a warm-up pass first, left out of the numbers, then `n` passes (3) in one game session; the report shows each part's median and range; `--sessions` plays more games of it; `--ab` plays games with the port setting `key=value` (B) and as the run has it (A, `--port` can give its value) in turn, and sets B against A part by part, with macOS's memory pressure and swap while each side played |
| `investigate <tool> <places...>` | a tool on places jumped to, or a whole test (below) |
| `calibrate [test] [n]` | how much a test moves between runs of the same build on this Mac: `n` runs (3) at a fresh install's settings, written as the test's noise profile |
| `baseline [set <name> [run...] \| remove <name>]` | names runs, one of each test, to compare with; a baseline's name stands for its run wherever a run is named, and `clean` keeps it |

```bash
# A change to the driver: before, name the runs; after, check
./benchmark.sh check && ./benchmark.sh baseline set main
# ...make the change, ./build.sh --install, then:
./benchmark.sh check                    # exits with 1 when a screenshot differs
./benchmark.sh measure tour             # the numbers, with a warm-up and three passes
./benchmark.sh measure --ab shader_constant_memory=false   # a port setting's cost, A and B in turn
```

A Mac's heat moves GPU time: after a rest a run reads up to about a tenth low until the chip warms up. `measure`'s
warm-up pass takes care of that. A `benchmark.sh` run without one notes when the Mac had rested more than ten minutes
before it.

### Noise profiles

The Mixed Test and the Planetary Tour have a noise profile, `lineup/noise/<test>.json`: how much each screenshot and
each part's GPU time moved between runs of the same build, and the limit a comparison holds it to (three times that,
2 % at least). `calibrate` measures it on your Mac and writes it to the benchmarks folder's `noise/`, which runs and
comparisons on that Mac use from then on, the Mac Port tab's runs too; copied into `lineup/noise/` it ships with the
app. A view standing still gets at least 1 % of its blocks, a view of the flight 3 %. The Warp Test has no profile:
its screenshots are not compared, since the game's clock is wherever it got to, and its parts are the speed time warp
reached.

### The tools

`investigate <tool> <places...>` jumps to the places (a flight's places with the game paused on the way) and turns one
tool on. A place is a screenshot's name; a whole test works for the tools that don't take GB at every place.

| Tool | |
|---|---|
| `profile` | KSA Metal's GPU profiler on the first 60 frames of every measured part: `profile.md` lists, per part, the encoders that cost the most GPU time, named by their shader hashes. The timestamps span each encoder from start to end, waits included, so they show where to look rather than exact costs |
| `trace` | an Instruments trace of the places (the Metal System Trace template, or another with `--trace-template <name>`), each measured part marked under Points of Interest; `trace.md` sums up the GPU time per part by channel (compute, vertex, fragment). Needs Xcode, 10 GB free, and places |
| `capture` | an Xcode GPU frame capture at each place: every draw and dispatch, the resources, the shader profiler and debugger. Needs places, 20 GB free |
| `hud` | Apple's Metal Performance HUD over the game, its per-frame log in `hud.log`; the HUD's menu, in the menu bar while the game runs, writes its reports to `hud/`. Not with `profile`: both use the GPU's counters |
| `catch <clean run>` | for a picture that goes wrong now and then: KSA Metal looks at the image a few times a second, and each screenshot is compared with that clean run's (30 % pure white, under half the size, or far more pure black). A broken one gets a pass dump in `caught/`, with `passes.txt` and an image of a frame after it; `watch.txt` says when each fault began and ended (at most 3 of each; about 40 MB each; 1 GB free). `--catch-capture` adds an Xcode frame capture after each dump (40 GB free). After a GPU failure nothing more is dumped |
| `passes` | a pass dump at every place: the clean run a caught dump is compared with |
| `film <seconds>` | a screenshot every that many seconds of the script's clock, in `film/`, to see the camera's moves |
| `camera-log` | the camera's place every frame in `trace.csv`, to check its moves are smooth |
| `hold` | jump to one place and stay there, paused, until you quit the game: for a close look with Apple's overlay or Xcode |

With `trace`, `capture` and `hud`, KSA Metal names every encoder after its pipelines (the `labels` setting), so the
tools show `1280x800 v1658c4ba926081e2+f7e01e1b174a96688 +1` (the render pass's size, the vertex and fragment shaders'
hashes, how many other pipelines it ran) instead of an anonymous encoder.

```bash
# Where does jupiter2's GPU time go?
./benchmark.sh investigate trace jupiter2
# Stay at earth4 for Xcode
./benchmark.sh investigate hold earth4
```

## The tests' scenarios

A test is a list of scenarios, and a scenario has a kind: how it keeps time.

| Scenario | Kind | Test | What it shows | What it measures |
|---|---|---|---|---|
| `mixed` | views | mixed | a kitten in its helmet floating in orbit, looking into the camera, then the vehicles at one moment, the game paused: the giga rocket blown up whole in the smoke of its boosters on its pad at Starbase, a rocket lifting off from LC-39A, one high over the coast from Vandenberg, and a capsule's cockpit in orbit with sunlight on its wall (with IVA Ray Tracing on) and Earth in its window | plumes, smoke trails, an explosion and smoke, part physics on the CPU, many draw calls, ray tracing in the cockpit, the kitten |
| `earth` | flight | tour | the whole Earth, down through the clouds to the sea off Brazil, then the jungle, the Andes and the coast of Peru, and up over northern Quebec | atmosphere, clouds, the ocean, terrain and ground clutter; new ground streaming in all along |
| `moon` | flight | tour | the flight out to the Moon, its disk, the crater Copernicus, then low over the ground in the east | terrain, the long shadows of craters and rocks |
| `mars` | flight | tour | the flight on to Mars, its disk, sunrise over a dust storm, then low over rocky ground | terrain, the dusty air, rocks |
| `saturn` | flight | tour | the rings from above, the haze under them, then the ring plane with its rocks going by | rings, their rocks |
| `jupiter` | flight | tour | Jupiter from afar, its cloud deck, then straight down into the layers under it | volumetric clouds |
| `warpclean` | timed | warp | a capsule in a 400 km orbit at the top speed of time warp | what the picture costs at that speed |
| `warpdebris` | timed | warp | the same among 18 rockets blown up around Earth in orbits from 150 to 800 km | hundreds of pieces in orbit, the bubbles of physics they make |
| `warpfalling` | timed | warp | the same with 18 rockets blown up high, their pieces falling back and landing | pieces in the air and on the ground holding warp back |

## How it works

The benchmark is in the app, in `Contents/Resources/Benchmark/`, and [KSA Metal Hooks](../hooks/) give it every frame
of the game. For a run of `./benchmark.sh`, the game starts from a documents folder of its own,
`~/Library/Caches/ksa-macos-port/benchmark/instance/`, with a copy of your settings (with the preset on top, if any)
and of your port settings (the defaults with a preset), and no mods, so your saves, settings and mods are never
touched. The command changes only the settings that start the game straight into the test (no system menu, the chosen
system, the game's test vessel in orbit); every quality setting stays yours or the preset's. A run in the game you
have open keeps its system: the benchmark saves your game, sets the world to a new game's, and loads your game again
at the end.

### The lineup

The tests are a text file, [`lineup/benchmark.txt`](lineup/benchmark.txt), next to their vehicles (`lineup/vehicles/`)
and their noise profiles (`lineup/noise/`): scenarios of steps timed in seconds, each a console command or a directive
of the benchmark's, and a `test` line for each test with the scenarios it plays. One parser reads it,
[`../../shared/benchmark/Lineup.cs`](../../shared/benchmark/Lineup.cs), for the director, `benchmark.sh`, the Mac Port
tab and the tests; it refuses a line it can't read, with its number. The directives are a table,
[`Directives.cs`](../../shared/benchmark/Directives.cs), with what each takes and does. A scenario's kind is how it
keeps time:

- **flight**: the script's clock and the game's run together; every `@shot` is a checkpoint where the game's clock
  lands on the moment the script expects, so the screenshots show the same moments on every Mac.
- **views**: the vehicles set up in steps of the same size on every Mac, then the game paused, the camera going from
  view to view. At each one the director takes a screenshot every quarter of a second until two in a row match (the
  ground and textures streaming in after a jump have settled), measures the view for a second, takes the screenshot,
  and takes it again if a look right after it differs (a kitten's blink).
- **timed**: every part takes the same time on any Mac, whatever the game gets through in it; nothing waits for the
  game.

### The director

The director, in this folder, plays the lineup in the game: `Director.cs` the scenarios and steps,
`Checkpoints.cs` the checkpoints, `Directives.cs` the table of handlers, which a run checks against the directives'
table when it starts. Around it:

| Part | |
|---|---|
| `Clock.cs` | the script's clock and the game's. Just before the game works out each step of its simulation, the hooks call the director (a prefix on the game's `Universe.GetJobSimStep`), which gives that step the game's own limit (`Universe.LimitNextStep`): a flight's steps never pass the next checkpoint, a catch-up lands on its moment, and a views scenario's setup runs the same steps of the simulation on every Mac |
| `Camera.cs`, `Path.cs`, `Rig.cs` | the camera: its flights through the `@key` places (`Path.cs`), the vehicle camera that follows or watches a vehicle (`Rig.cs`), the cockpit and front views |
| `Vehicles.cs` | the vehicle directives: launch pads, flights, kittens, staging, explosions |
| `Probes.cs` | the tools of `investigate`: each watches the parts, the checkpoints or the frames; one that works on a view holds its checkpoint until it is done |
| `World.cs` | a new game's world, and the sea's waves settled at once for a view standing still |
| `Explore.cs` | `explore`'s console command |
| `Runs.cs` | the runs in the game: those `benchmark.sh` starts, those of the Mac Port tab, and its watchdog |

A test starts in a game of its own and goes through its views and places in the same order, so the clouds and the sea,
which change with where the camera has been, look the same every run without being put back.

One thing still moves between runs: how the giga rocket breaks up comes from the game's physics, and with it whether
about 25 of its boosters' exhaust lights still burn in the explosion view. Their shadows add about 3,800 draws, up to
2 ms of GPU time, and nothing the screenshot shows; a noise profile calibrated on your Mac allows for it.

### A run's record

The director measures nothing itself. It writes what happens to `events.jsonl` (a scenario starting, a part opened and
closed, a screenshot, a checkpoint's moment, an error, a heartbeat every second) and every frame to `frames.csv`, marked
with the part its numbers count in; `benchmark.sh` adds its own events around the game (a session, how it ended, and
macOS's memory pressure and swap, over the run and in each game session: an A/B run's report shows them for each
side). When the run ends, one step makes its record, `run.json`, from them
([`../../shared/benchmark/Finish.cs`](../../shared/benchmark/Finish.cs)): each part's numbers from its frames, the facts
about the Mac and the settings, and one list of checks that decides what fails the run and what is worth knowing. The
report, its page, the comparison and the list of runs all read records. `benchmark.sh` and the Mac Port tab's runs make
it the same way; for a run with a `report.json` and no `run.json`, the report is read into a record.

A game counts as hung when its last heartbeat is a minute old, three in the Warp Test or with a tool that holds a
checkpoint, and as stuck when its heartbeats still come but have said the same moment of the script for as long, with
no screenshot, checkpoint or part begun or ended there (the game's clock stopped, so a checkpoint waits for ever;
[`Watchdog.cs`](../../shared/benchmark/Watchdog.cs)). `benchmark.sh` stops either game, keeping what it measured; for a
stuck one it first records what the game's threads were doing in `stuck.txt` (`stuck-<n>.txt` for the nth game of a
run; macOS's `sample`, 5 s), which the zip to share holds too. It doesn't look for a stuck game with `hold` or a tool
that waits at checkpoints (`capture`, `catch`, `passes`). The tab stops a hung game the same way, and a stuck run as Esc
does, failed, giving the player's game back.

A run fails when the game crashed, hung or was stuck, a step of the test failed, a vehicle broke apart before it should, a
screenshot is missing, the game's log has errors (during the Warp Test the game's own errors are only noted, since
settings that hold warp back cause them), one of the hooks was skipped (the game's code it changes has moved) or
failed, the game could not read the run's settings, a screenshot is one colour from edge to edge or 30 % of it or more
pure white (a real view reaches about a fifth), or another game ran at the same time; the script then exits with 1. A
run you stop (Esc, Ctrl-C) ends with what it measured and exits with 1 too. The game failing to reach its servers is
noted, not a failure. Before the game starts, the script refuses what would waste the run: a value a port setting can't
take, a `--compare` run that shares nothing with this one or played another way, an app or a test that isn't there. It
warns when the GPU is already busy.

`compare` reads GPU time first, as frame times sit on the display's beat. It marks a number as changed when it moved by
more than its noise profile's limit, or 5 %. Between runs with several passes it marks a change when the ranges don't
overlap and the difference is over 2 %. Screenshots are compared in blocks of 16 by 16 pixels: a block counts as changed
when its mean colour moved by more than 12 (of 255), and a screenshot differs when more of its blocks changed than its
limit allows.

Every frame of a run goes into `frames.csv`: the script's time, the scenario and part, frame and GPU time, draws and
passes, memory, thermal state, shaders compiled so far, the time between the last two images the display showed, and in
time warp the game's clock and the physics' work. The record's numbers are summaries of it, and it goes into the zip,
so a shared run can answer questions nobody asked when it was made. While a part is measured, KSA Metal also marks it
as a signpost interval, which is how an Instruments trace knows where each part is.

The run's store holds the same events and frames, and more, in SQLite files in the run's folder
([`../../shared/benchmark/Store.cs`](../../shared/benchmark/Store.cs)): the game writes `run.db`, `benchmark.sh` writes
`host.db`, so two processes never write one file, and every row carries the time on one clock, the Mac's monotonic clock
in seconds, which KSA Metal's own times use. Besides the events and frames, `run.db` has what KSA Metal hands the
director through its drain while a run plays: every present with its numbers (`presents`), when each frame began, was
presented, finished on the GPU and was shown (`shown`), every wait of a millisecond or more that blocked the game, such
as a fence or the next drawable (`waits`), and every allocation or free of 16 MB or more (`memory`). The record is made
from `events.jsonl` and `frames.csv`; a run whose store says otherwise gets a note. macOS's `sqlite3` reads the files:

```bash
sqlite3 run.db "SELECT what, COUNT(*), ROUND(MAX(ms), 1) FROM waits GROUP BY what ORDER BY 3 DESC"
```

The build runs the benchmark's tests, which need no game, and stops when one fails. Those in
[`../../player/benchmark-cli/tests/`](../../player/benchmark-cli/tests/) check the record (what passes and fails a run,
the conditions it notes, passes and sessions merged into medians, numbers the same on a Mac in any region, an older
run's report), the run's store against its files, KSA Metal's drain records, its report and zip, the comparison of two
runs, the PNG reader, and the lineup itself.
[`command.sh`](../../player/benchmark-cli/tests/command.sh) plays [`benchmark.sh`](../../player/port-folder/benchmark.sh)
against a stand-in for the game that writes events and frames as the director does: its commands, what it refuses, and
a run that passes, crashes, hangs or is stopped. [`../../player/benchmark-cli/`](../../player/benchmark-cli/) holds
`benchmark.sh`'s program, compiled against nothing of the game.

The rockets and the capsule in `lineup/vehicles/` were built for the tests.

### Changing the tests

`./benchmark.sh explore` starts the benchmark's game as a run would, with its clock paused so the light matches the
flight's. Fly the free camera to a place, open the console (`\`) and type `benchkey <name>`: the place is written as a
`@key` line to `keys.txt` in a new run folder and copied to the clipboard, ready to paste into `benchmark.txt`.
`./build.sh --install` puts the tests into the app. After a change to a scenario of the Mixed Test or the Tour,
`calibrate` its test again.

## Options and files

| Option | |
|---|---|
| `--preset low`, `--preset medium`, `--preset high` | fixed settings in a 1280x800 window without Vsync, the same on every Mac (default: your settings) |
| `--defaults` | the settings of a fresh install instead of yours: the game's own defaults and the port settings' (a preset, `--set` and `--port` go on top) |
| `--system earth` | Earth and the Moon only (the game's smaller system): skips Mars, Saturn and Jupiter, and needs less memory (default: `earth` when what plays is all on Earth or the Moon, otherwise `sol`) |
| `mixed`, `tour`, `warp`, `full` | the test to play (default: `mixed`) |
| `<place> ...` | play only these places (a screenshot's name): each one's scenario up to there |
| `--fast` | with places: jump to them, the game paused on the way, instead of flying there |
| `--no-shots` | take no screenshots |
| `--set <key>=<value>` | change a setting of `settings.toml` for the run, on top of the preset (repeat for more; strings need no quotes); as `<section>.<key>=<value>` it adds a key your file lacks to that section, such as one a newer KSA added (`display.hdr=true`) |
| `--settings <file>` | apply the settings in a file of `key = value` lines, on top of the preset and before `--set` |
| `--port <key>=<value>` | change a [port setting](../../../README.md#the-mac-port-tab) for the run, on top of yours (with a preset or `--defaults`, on top of the defaults); the report shows the port settings and what the hooks did |
| `--compare <run>` | compare with that run at the end (its folder, zip or name, `last`, `previous`, a baseline's name), checked before the game starts; the script fails when screenshots differ |
| `--app <path>` | the app to measure (default: `~/Applications/Kitten Space Agency.app`) |

| Command | |
|---|---|
| `list` | the tests, their scenarios and their places |
| `runs [n]` | your last runs (15, or n), the newest first |
| `report [run] [test]` | a run's report in the terminal, and its page in your browser (default: the last run; a baseline with runs of several tests needs the test) |
| `compare [run] [run]` | how the second run differs from the first (with one run: against the last; with none: the last against the one before it); exits with 1 when screenshots differ, 2 when the runs don't compare |
| `clean [--keep n] [--yes]` | delete all but your last runs (30, or n) and the baselines' runs, after asking (`--yes`: without asking); only folders a run leaves |
| `check`, `measure`, `investigate`, `calibrate`, `baseline`, `explore` | for work on the port (above) |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results: `report.html`, `report.md`, `run.json` (the record), `events.jsonl`, `frames.csv`, `run.db` and `host.db` (the run's store), `options.json`, the screenshots, `game.log`, `stuck.txt` from a stuck game, `compare.html` and `compare.md` after a comparison, and what the tools wrote |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/<date>.zip` | the run to share |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/baselines.json`, `noise/` | the baselines' names, and the noise profiles `calibrate` measured on this Mac |
| `~/Library/Caches/ksa-macos-port/benchmark/instance/` | the game's documents folder for a run that starts the game |
| `Contents/Resources/Benchmark/` in the app | the director, `benchmark.sh`'s program, the tests, their vehicles and noise profiles |
