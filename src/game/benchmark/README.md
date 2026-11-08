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
report open. **Show report** under the buttons opens the last report, and **Timeline**'s **OPEN** opens the last run
frame by frame in your browser (it stays on this Mac).

**Report Detail** says what a test's report holds besides its frame rate: **Standard**, its numbers and screenshots;
**Profile**, also the GPU's work named in each part ("Clouds Raymarching", "Ocean"), from the test played twice more in a
game started again, which quits at the end as with Restart for a Clean Run (`./benchmark.sh profile`; not with Full);
**Memory**, also what holds the game's memory at each view.

**Your Game**'s **RECORD** button records your own game instead: 20 seconds of your play from when the settings close,
with the frame rate, the stutters and what caused them, as a report to share when the game stutters somewhere. Nothing
in your game changes, and nothing of the benchmark runs before you press it; Esc stops it.

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

The terminal shows the result, each part's numbers, and the slow frames with what took their time. `report.html` in the
run's folder shows them with the run's screenshots and charts:

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
after asking (`--keep <n>` keeps another number), never while a run plays; studies keep their runs until you delete a
study's folder.

### Sharing a run

Each run writes its results to `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/`, and a zip of them in that
folder (`<date>.zip`). The zip holds the report, the run's record, every frame's numbers, the game's log and any crash
report of the run, the settings and the screenshots as JPEG; your home folder's name is replaced by `~` throughout. To
share one, open an issue with the **Benchmark report** template and attach the zip. For numbers that compare across
Macs, run with a preset (`--preset low`, `medium` or `high`): fixed settings over a fresh install's defaults, the
same on every Mac. `compare` takes a zip in place of a run folder.

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

The same tests are the project's tool for looking into the driver, the shaders and the game. These commands of
`benchmark.sh` are for that work.

| Command | What it does |
|---|---|
| `check [test]` | the standard test of a change: the test (default `mixed`) at a fresh install's settings, compared with the baseline `main` of that test, or else the last run of it that passed played the same way; exits with 1 when screenshots differ |
| `measure [test] [--repeat n] [--sessions n] [--ab key=value]` | numbers to judge a change by: a warm-up pass first, left out of the numbers, then `n` passes (3) in one game session; the report shows each part's median and range; `--sessions` plays more games of it; `--ab` is a [study](#studies) of two variants, the port setting `key=value` (B) and as the run has it (A, `--port` can give its value), `--sessions` games each |
| `study <file>`, `study resume [study]`, `study report [study]` | variants of one `measure` or `profile` command, each in game sessions of its own, played in turn and set against a baseline variant by their sessions ([below](#studies)) |
| `profile [test \| places...]` | where the time goes: a warm-up, a measured pass and a profiled pass in one game session. The profiled pass turns the game's GPU zones on ("Clouds Raymarching", "Ocean", "Stars") and `profile.md` lists, per part, the zones with the most own time a frame; the measured pass gives the run's numbers |
| `investigate <tool> <places...>` | a tool on places jumped to, or a whole test (below) |
| `calibrate [test] [n]` | how much a test moves between runs of the same build on this Mac: `n` runs (3) at a fresh install's settings, written as the test's noise profile |
| `baseline [set <name> [run...] \| remove <name>]` | names runs, one of each test, to compare with; a baseline's name stands for its run wherever a run is named, and `clean` keeps it |
| `query [run] '<sql>'` | SQL on a run's [store](#a-runs-record) (default: the last run) |
| `timeline [run]` | the run in Perfetto's viewer, in your browser: its parts, frame and GPU times, each frame's way to the screen, KSA Metal's waits, the game's zones around slow frames, and the counters (the GPU's power and clock, the probes') |
| `report <run> --again` | the run's record, report and page made again from its store by this version of the benchmark, the facts about the Mac as the run had them |

```bash
# A change to the driver: before, name the runs; after, check
./benchmark.sh check && ./benchmark.sh baseline set main
# ...make the change, ./build.sh --install, then:
./benchmark.sh check                    # exits with 1 when a screenshot differs
./benchmark.sh measure tour             # the numbers, with a warm-up and three passes
./benchmark.sh measure --ab shader_constant_memory=false --sessions 5   # a port setting's cost, A and B in turn
```

A Mac's heat moves GPU time: after a rest a run reads up to about a tenth low until the chip warms up. `measure`'s
warm-up pass takes care of that. A `benchmark.sh` run without one notes when the Mac had rested more than ten minutes
before it.

### Verdicts

A comparison judges each part by its GPU busy (its frame time where a run has no GPU time, the speed reached in time
warp), and the other numbers beside it ([`../../shared/benchmark/Comparison.cs`](../../shared/benchmark/Comparison.cs),
[`Statistics.cs`](../../shared/benchmark/Statistics.cs)). What a verdict stands on depends on the runs:

| Runs | Basis | A change is |
|---|---|---|
| one game session a side, one pass (`check`, two runs) | screen | a time or an energy past 10 % or the part's limit, whichever is larger; another number past its limit |
| one game session a side, several passes (`measure`) | quick read | the passes' ranges don't overlap and it is past the part's limit, 2 % at least |
| several game sessions a side (a [study](#studies), `measure --sessions`) | sessions | each session's median is a sample: Mann-Whitney's p under 0.05, Cliff's delta from 0.147 (not negligible), and a move of 1 % for the GPU and frame times, past its limit for another number; with fewer than 5 sessions a side, inconclusive |

A screen's 10 % is what two runs of the same build played at other times differ by: the Mac's heat and the apps
sharing its GPU (two runs two hours apart: +4 to +10 % on every Mixed part). A quick read's two sessions played at
other times too. Sessions played in turns tell smaller changes: 5 a side tell about 5 % apart on a quiet Mac, more
sessions less. Two `measure --sessions` runs compared played their sessions one run after the other, and a Mac's heat
over such a series moves GPU time on its own (several % in half an hour): `compare` says so. A study plays the sides
in turns.

Besides the number that decides a part, a comparison judges CPU busy, the P99 frame time and the 1% low (in parts of
200 frames or more), hitches, frames over 100 ms, display latency, the GPU's energy, and the memory footprint and GPU
memory. On a screen only those that don't drift with the Mac's heat count (memory, hitches); with Vsync the numbers
the display paces (frame time, its P99 and 1% low, latency, energy) jump a display's step with their GPU time, the same
story. The rest (FPS, P95, the worst frame, GC pauses, JIT, shaders, pipelines, draws, the thermal state, the physics'
numbers, the GPU's power) is shown in each run's report, not judged.

The verdict names each part that moved and its basis (`GPU time 12 % lower in earth4, 19 other parts within noise
(screen)`), then any other number that moved, and the comparison names the zones that moved with each part that
changed: the GPU's when both runs were profiled (`earth4 GPU busy +1.4 ms: Clouds Raymarching +1.20`), else the main
thread's for a frame time. Without a change, a verdict by sessions gives the parts' 95 % intervals: what the sessions
can't tell apart. Above the numbers stand what makes them unlike: a Mac, a power source, a window or a Vsync that
differs, and each run's notes that move numbers (the GPU busy before the run, the Mac hot or just rested, on battery).
`compare.html` shows the verdict and what moved first; every judged number, with its p, delta and the 95 % interval of
B against A that goes with the test (Hodges-Lehmann), is under Statistics.

### Studies

A study plays variants of one command, each in game sessions of its own, and sets each against a baseline variant by
those sessions ([`../../shared/benchmark/Study.cs`](../../shared/benchmark/Study.cs)). Its file is a few lines:

```text
# Fewer cloud steps, at two presets
measure mixed --defaults --repeat 1
sessions 5
variant main
variant steps --port fewer_cloud_steps=true
axis --preset low high
```

| Line | What it says |
|---|---|
| `measure <options>` or `profile <options>` | what every session plays: the command's options, as on the command line |
| `sessions <n>` | the game sessions each variant plays (5 unless said, the fewest a verdict stands on) |
| `variant <name> [options]` | a variant, and the options it adds to the command (`--port`, `--set`, `--preset`, `--app`, `--env`) |
| `axis <option> <value>...` | every variant once for each value: `main low`, `main high`, `steps low`, `steps high` |
| `baseline <name>` | the variant the others are set against (default: the first) |

The sessions play round after round, each variant once a round, the next round the other way (A B B A A B), so the
Mac's heat falls on every variant the same. Each session is a run of its own in the study's folder (the benchmarks
folder's `studies/`), from your settings as they were when the study began, and each variant's first round takes the
screenshots. The study waits while another game runs; Ctrl-C stops the session playing and the study, and
`study resume` plays the rest, after a crash or a reboot too; one terminal at a time plays a study. A session that
failed, or couldn't start (its app gone), is played again at once, and left out when it fails twice; one that a game
or another run got in ahead of waits again. A session recorded by an older benchmark counts as played, its record made
again from its store. The report, `report.html` in the study's folder, has each variant's verdict against
the baseline with its screenshots held against the baseline's, the parts as a matrix of variants with the 95 %
interval of each ratio, the zones that moved, and every session's run. `measure --ab` is such a study, and
`--save-plan <file>` writes a `measure` or `profile` command line as a study's file to add variants to.

### Noise profiles

The Mixed Test and the Planetary Tour have a noise profile, `lineup/noise/<test>.json`: how much each screenshot and
each part's GPU and frame time moved between runs of the same build, and the limits a comparison holds them to (three
times that, 2 % at least). `calibrate` measures it on your Mac and writes it to the benchmarks folder's `noise/`, which runs and
comparisons on that Mac use from then on, the Mac Port tab's runs too; copied into `lineup/noise/` it ships with the
app. A view standing still gets at least 1 % of its blocks, a view of the flight 3 %. The Warp Test has no profile:
its screenshots are not compared, since the game's clock is wherever it got to, and its parts are the speed time warp
reached.

### The tools

`investigate <tool> <places...>` jumps to the places (a flight's places with the game paused on the way) and turns one
tool on. A place is a screenshot's name; a whole test works for the tools that don't take GB at every place.

| Tool | |
|---|---|
| `trace` | an Instruments trace of the places (the Metal System Trace template, or another with `--trace-template <name>`), each measured part marked under Points of Interest; `trace.md` sums up the GPU time per part by channel (compute, vertex, fragment). Needs Xcode, 10 GB free, and places |
| `capture` | an Xcode GPU frame capture at each place: every draw and dispatch, the resources, the shader profiler and debugger. Needs places, 20 GB free |
| `hud` | Apple's Metal Performance HUD over the game, its per-frame log in `hud.log`; the HUD's menu, in the menu bar while the game runs, writes its reports to `hud/` |
| `catch <clean run>` | for a picture that goes wrong now and then: the trigger `broken: passes, screenshot` with each screenshot held against that clean run's (30 % pure white, under half the size, or far more pure black), and KSA Metal looking at the image a few times a second, which dumps on its own; `watch.txt` says when each fault began and ended (at most 3 of each; about 40 MB each; 1 GB free). `--catch-capture` adds `capture` to the rule (40 GB free) |
| `passes` | the trigger `checkpoint: passes, screenshot`: a pass dump at every place, the clean run a caught dump is compared with |
| `film <seconds>` | a screenshot every that many seconds of the script's clock, in `film/`, to see the camera's moves |
| `camera-log` | the camera's place every frame in `camera-log.csv`, to check its moves are smooth |
| `hold` | jump to one place and stay there, paused, until you quit the game: for a close look with Apple's overlay or Xcode |

With `trace`, `capture` and `hud`, KSA Metal names every encoder after the game's GPU zone it began in and its
pipelines (the `labels` setting), so the tools show `1280x800 Clouds Upscaling: v1658c4ba926081e2+f7e01e1b174a96688 +1`
(the render pass's size, the zone, the vertex and fragment shaders' hashes, how many other pipelines it ran) instead of
an anonymous encoder. An encoder that runs several zones carries the first one's name; with `KSA_MTL_GPU_ZONES=1` in
front of `benchmark.sh`, every zone is an encoder of its own and the run's store has their times as well, to set the
two side by side.

```bash
# Where does jupiter2's GPU time go?
./benchmark.sh investigate trace jupiter2
# Stay at earth4 for Xcode
./benchmark.sh investigate hold earth4
```

### Triggers

A trigger is a rule: when its condition holds, its actions run, a limited number of times
([`../../shared/benchmark/Triggers.cs`](../../shared/benchmark/Triggers.cs), and [`Triggers.cs`](Triggers.cs) in the
game). `--on '<rule>'` adds one to `run`, `check`, `measure`, `profile` or `investigate` (again for more):

```bash
./benchmark.sh run tour --on 'frame>50ms: moment'             # every frame of 50 ms or more kept, 20 a pass at most
./benchmark.sh run tour --on 'frame>200ms: passes, max 1'     # the passes after the first frame of 200 ms
./benchmark.sh run mixed --on 'broken: passes, screenshot'    # a picture gone pure white, dumped
```

| Condition | Holds when | Its actions |
|---|---|---|
| `frame>N ms` | a frame of a measured part took N ms or more | `moment`, `screenshot`, `passes`, `capture`, `stop` |
| `broken` | a checkpoint's screenshot is 30 % pure white (with `investigate catch`, also far smaller or far more pure black than the clean run's) | `screenshot`, `passes`, `capture`, `stop` |
| `checkpoint` | a checkpoint's screenshot is there | `screenshot`, `passes`, `capture` |
| `stuck` | the game is stuck (see [A run's record](#a-runs-record)); it stops whatever the rule says | `sample`, `stop` |
| `gpu-error` | a command buffer failed: the GPU refuses the game's work from then on | `moment`, `screenshot`, `stop` |

| Action | |
|---|---|
| `moment` | the frame's cause and zones in the run's store (`moments`, `slices`) |
| `screenshot` | an image of a frame after it, in `caught/` |
| `passes` | KSA Metal's pass dump of the next frame, in `caught/<name>/` with `passes.txt` (about 40 MB) |
| `capture` | an Xcode GPU frame capture of the next frame, `caught/<name>.gputrace` (20 GB free) |
| `sample` | macOS's `sample` of the game's threads for 5 s, in `stuck.txt` |
| `stop` | the run ends there: stopped, or failed after a GPU error |

`max N` among the actions runs the rule N times a pass at most (by default 20 for `frame`, 3 for `broken`, 1 for
`gpu-error`), so a warm-up's slow frames leave the measured passes theirs. Every run carries two rules, `frame>100ms: moment` and `stuck: sample, stop`; a rule of yours for the same
condition takes the default's place. Each time a rule runs, a `trigger` event says which and why, and `benchmark.sh`
prints those that did more than keep a moment. At a checkpoint the actions hold it until they are done (the dump first,
then the capture, then the image, the work a slow frame started before them); after a frame the run plays on while they
work, and a `stop` waits for them. Anything but a moment or a stop slows
the game's frames: a part measured while one works leaves that pass's numbers, and the run gets a note (`disturbed`).
After a GPU failure no pass dump or capture starts.

### Probes

A probe is a tool plugged into a run: it watches the run (its game session, passes, parts, checkpoints and frames) and
records through the run's store ([`Probes.cs`](Probes.cs)). `--probe <name>` adds one of the benchmark's, `--probe
<path>.dll` an assembly of your own, and `--knob <probe>.<key>=<value>` gives it a value, which a study can vary as it
varies a setting (`axis --knob example.level=1 example.level=2`).

| Probe | What it records |
|---|---|
| `memory` | once a second the game's VRAM, its host-visible memory, KSA Metal's memory and the .NET heap (metrics `mem/game_vram`, `mem/game_host_visible`, `mem/driver`, `mem/gc_heap`); at every screenshot a census: each of the game's memory tags with its bytes ("CascadedShadowMap.ShadowTarget.Depth"), KSA Metal's memory by type, the .NET heap and the footprint (the report shows the first pass's). It reads what the game and the driver count anyway |

A probe of your own is a class of `Probe` with a public constructor without parameters (in `KsaMetal.Benchmark.dll`, built
against it and the game); one that throws is left out of the rest of the run, which fails:

| Member | |
|---|---|
| `Name`, `Says`, `Cost` | its name (its knobs' and counters' prefix), what the log says it does, and what it costs: `Free`, `Cheap`, `Perturbing` (its patches change the game's timing) or `Heavy` (it holds the game); a run with a perturbing or heavy probe gets a note |
| `Loading()` | the game is starting, its world not loaded: patches that change what loads go here (runs that start with the game) |
| `Start()`, `PassBegan`, `PassEnded`, `Opened`, `Closed`, `BeforeShot`, `AfterShot`, `Frame`, `End` | the run: the session, a pass, a measured part, a checkpoint's view (a returned `Wait` holds the checkpoint until the probe's work is done), every frame, its end |
| `Host.Counter(name, unit, ...)` | a counter, `probe/<probe>/<name>`: its value written every frame, a metric the record, comparisons and studies judge (the median of a part's frames, or the most) |
| `Host.Zone(name)` | `using (Host.Zone("solve")) { ... }` on any thread: its time a frame, a counter |
| `Host.Event`, `Host.Artefact`, `Host.Census` | an event in the store, a file in the run's `probes/<probe>/`, an owner of memory at the checkpoint (in `BeforeShot` or `AfterShot`) |
| `Host.Knob(key)`, `Host.Method(type, name, ...)`, `Host.Patch(target, prefix, postfix, transpiler)` | its knobs; the hooks' lookup of the game's methods, which says in the log when a KSA update moved one, and their Harmony |

From the tab, Report Detail Memory is the `memory` probe, and the settings file's Diagnostics row `benchmark_captures`
adds an Xcode frame capture at each view of the Mixed Test at Standard, in a game started again with frame captures
allowed.

`--channels` turns off what every run records (`-zones`: the game's profilers, `-runtime`: .NET's counters, `-power`: the
GPU's power and clock) or adds the benchmark's probes (`+memory`).

`benchmark.sh` also records the GPU's power and clock while each game session plays, from macOS's IOReport (as
Activity Monitor's energy figures are, without root): `host/gpu_power` (W), `host/gpu_active` (the share of the time
the GPU wasn't off) and `host/gpu_clock` (MHz, its states' time weighted by their frequencies), every 0.2 s into
`host.db`, each sample in the part it fell in ([`Power.cs`](../../player/benchmark-cli/Power.cs)). Power is shown, not
judged: a comparison judges the GPU's energy a frame ([`gpu/energy`](#metrics)). A run from the tab has no host, and none
of them. Where macOS doesn't give them, the run says so and goes on.

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
`~/Library/Caches/ksa-macos-port/benchmark/instance/`, with a copy of your settings (a fresh install's with a preset or
`--defaults`) and of your port settings (the defaults with a preset or `--defaults`), and no mods, so your saves,
settings and mods are never touched. The command changes only the settings that start the game straight into the test
(no system menu, the chosen system, the game's test vessel in orbit); every quality setting stays yours or the
preset's. The game's environment is the command's: variables that change how it runs (Metal's, .NET's, the port's,
StarMap's) are listed in the run's Environment fact, KSA Metal's switches in their own. A run in the game you
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
| `Here.cs` | the tab's recording of the player's own game (the plan's `play.here`): no script, one part named `here`, everything a run records, a screenshot at its start |
| `Triggers.cs` | the run's [triggers](#triggers): every frame and after every checkpoint's screenshot, their conditions looked at and their actions run |
| `GameProfilers.cs` | the game's own profilers, on while a run plays and read every frame into the run's store |
| `GpuZones.cs` | the game's GPU zones for `profile`'s profiled pass (KSA Metal splitting render passes at them) and encoders named after them for `trace`, `capture` and `hud` |
| `World.cs` | a new game's world, and the sea's waves settled at once for a view standing still |
| `Explore.cs` | `explore`'s console command |
| `Runs.cs` | the runs in the game: those `benchmark.sh` starts, those of the Mac Port tab, and its watchdog |

A test starts in a game of its own and goes through its views and places in the same order, so the clouds and the sea,
which change with where the camera has been, look the same every run without being put back.

One thing still moves between runs: how the giga rocket breaks up comes from the game's physics, and with it whether
about 25 of its boosters' exhaust lights still burn in the explosion view. Their shadows add about 3,800 draws, up to
2 ms of GPU time, and nothing the screenshot shows; a noise profile calibrated on your Mac allows for it.

### A run's record

The director measures nothing itself. It plays the run's plan, `plan.json`
([`../../shared/benchmark/RunPlan.cs`](../../shared/benchmark/RunPlan.cs)), which `benchmark.sh` or the Mac Port tab
writes: what plays, its passes (a warm-up, measured, profiled), what the tools record, and the triggers; a plan never
changes a setting. It writes what happens into the run's store as events (a pass beginning, a scenario starting, a part
opened and closed, a screenshot, a checkpoint's moment, an error, a heartbeat every second) and every frame, marked with
the part its numbers count in; `benchmark.sh` adds its own events around the game (a session, how it ended, and
macOS's memory pressure and swap, over the run and in each game session). When the run ends, one step makes its record, `run.json`, from the store
([`../../shared/benchmark/Finish.cs`](../../shared/benchmark/Finish.cs)): each part's [metrics](#metrics) over its
measured passes, each measuring's own, a profiled pass's apart, the zones and the slow frames, the facts about the Mac
and the settings, and one list of checks that decides what fails the run and what is worth knowing. The report, its
page, the comparison and the list of runs all read records. `benchmark.sh` and the Mac Port tab's runs make it the same
way. A record of an older version isn't read: `report <run> --again` makes it again from the run's store.

A game counts as hung when its last heartbeat is a minute old, three in the Warp Test or with a tool that holds a
checkpoint, and as stuck when its heartbeats still come but have said the same moment of the script for as long, with
no screenshot, checkpoint or part begun or ended there (the game's clock stopped, so a checkpoint waits for ever;
[`Watchdog.cs`](../../shared/benchmark/Watchdog.cs)). `benchmark.sh` stops either game, TERM first so that it writes
what it measured, KILL after 10 s; for a stuck one the trigger `stuck: sample, stop` first records what the game's
threads were doing in `stuck.txt` (`stuck-<n>.txt` for the nth game of a run), which the zip to share holds too. It
doesn't look for a stuck game with `hold`, `capture` or a trigger that dumps or captures at checkpoints. The tab stops a
hung game the same way, and a stuck run, after the same sample, as Esc does, failed, giving the player's game back.
`benchmark.sh` also stops a game that hasn't finished loading after 10 minutes.

A run fails when the game crashed, hung or was stuck, a step of the test failed, a vehicle broke apart before it should, a
screenshot is missing, the game's log has errors (during the Warp Test the game's own errors are only noted, since
settings that hold warp back cause them), one of the hooks was skipped (the game's code it changes has moved) or
failed, the game could not read the run's settings, a screenshot is one colour from edge to edge or 30 % of it or more
pure white (a real view reaches about a fifth), or another game ran at the same time; the script then exits with 1. A
run you stop (Esc, Ctrl-C, TERM, or closing the terminal) ends with what it measured and exits with 1 too; with
Instruments recording, a second Ctrl-C stops waiting for its trace. The game failing to reach its servers is
noted, not a failure. Before the game starts, the script refuses what would waste the run: a value a port setting can't
take, a `--compare` run that shares nothing with this one or played another way, an app or a test that isn't there, a
game already running (or its launcher waiting to start one), and it refuses to start when it can't tell (`pgrep`
gave no answer). It warns when the GPU is already busy.

Screenshots are compared in blocks of 16 by 16 pixels: a block counts as changed when its mean colour moved by more than
12 (of 255), and a screenshot differs when more of its blocks changed than its limit allows. The numbers are judged as
[Verdicts](#verdicts) says.

Every frame of a run is a row of the store's `frames`: the script's time, the scenario and part, frame and GPU time,
draws and passes, memory, thermal state, shaders compiled so far, the time between the last two images the display
showed, in time warp the game's clock and the physics' work, and the .NET runtime's work in it (GC pause, collections,
JIT time and methods, lock contentions, MB allocated). The record's numbers are summaries of it, and the store goes into
the zip, so a shared run can answer questions nobody asked when it was made. While a part is measured, KSA Metal also
marks it as a signpost interval, which is how an Instruments trace knows where each part is.

The run's store is SQLite files in the run's folder ([`../../shared/benchmark/Store.cs`](../../shared/benchmark/Store.cs)):
the game writes `run.db`, `benchmark.sh` writes `host.db`, so two processes never write one file; `benchmark.sh` watches
the game's heartbeats there, and the game its recording event. Every row carries the time on one clock, the Mac's
monotonic clock in seconds, which KSA Metal's own times use. Besides the events and frames, `run.db` has what KSA Metal
hands the director through its drain while a run plays: every present with its numbers (`presents`), when each frame
began, was presented, finished on the GPU and was shown (`shown`), each frame's own GPU time, taken when its present's
command buffer completed (`frame_gpu`), every wait of a millisecond or more that blocked the game, such as a fence or the
next drawable (`waits`; the wait for an earlier frame's present too, the game's every frame under V-Sync), and every
allocation or free of 16 MB or more (`memory`). A part's GPU busy is its frames' own GPU time; the `GPU time` of
`frames` and `presents` is what completed between two presents, which lags by the frames in flight and holds two frames'
work after a stall (a run that has only that says so in its GPU time fact, and a comparison with it is not like for
like). `shown` and `frame_gpu` carry the game session: each session's game counts its presents from 1.

During a run the director switches on the game's own profilers, the main thread's and the job runners' (orbit and vehicle
solvers and the others), in `profile`'s profiled pass its GPU profiler too (group `gpu`, [`GpuZones.cs`](GpuZones.cs)), and reads them every frame ([`GameProfilers.cs`](GameProfilers.cs),
[`../../shared/benchmark/Zones.cs`](../../shared/benchmark/Zones.cs)): `zone_stats` has every zone of each part, its total
and own time, its median, 95th percentile and worst in a frame, and what it allocated. A frame a trigger keeps is a moment
(`moments`; by default a measured part's frame of 100 ms or more, 20 a pass): its cause names the main thread's zones with the most own time in it, KSA Metal's
waits of 2 ms or more and the most GPU time a frame around it took, and the first ten of the measured passes keep every
zone from half a second before to a quarter of a second after it (`slices`). Reading them costs the game about 20 µs a
frame; `--channels -zones` leaves the profilers off.

A profiled pass's zones run one after another: every zone boundary waits for all the work before it, so the profiled
pass's GPU time is about 13 % higher than the measured pass's (the Mixed Test), a zone called many times a frame reads
high by about 20 µs for each call (up to 70), and a zone at a render pass's edge carries the store and load of the
pass's images. Against an Instruments trace of the same frames, the zones called once a frame read a median 4 % high,
52 of 56 within 10 % (four places of the Tour). The record keeps the profiled pass's numbers apart from the measured
ones.

`query` runs SQL on a run's store, with `host.db` attached as `host`; macOS's `sqlite3` reads the files too:

```bash
./benchmark.sh query "SELECT what, COUNT(*), ROUND(MAX(ms), 1) FROM waits GROUP BY what ORDER BY 3 DESC"
./benchmark.sh query 2026-11-07_1939 "SELECT part, ROUND(frame_ms), cause FROM moments"
sqlite3 run.db "SELECT zone, ROUND(self_ms / frames, 2) FROM zone_stats WHERE part = 'earth4' ORDER BY self_ms DESC LIMIT 10"
```

`timeline` writes the store as a trace for Perfetto's viewer, `timeline.pftrace`, and opens `timeline.html`, whose
button hands it to ui.perfetto.dev in your browser; the trace goes nowhere else
([`../../shared/benchmark/Timeline.cs`](../../shared/benchmark/Timeline.cs)). Its tracks: the parts, the run's events,
frame time and GPU busy, each frame from its start to its present and to the GPU's end and its time on screen, KSA
Metal's waits by kind, the game's zones around the slow frames, and memory.

### Metrics

Every number a record, a report or a comparison shows has one name in
[`../../shared/benchmark/Metrics.cs`](../../shared/benchmark/Metrics.cs), with its unit, which way is better, how a
part's passes and sessions combine (their median, or the most for counts) and its limit. The frame metrics are
PresentMon's:

| Metric | |
|---|---|
| `frame/time` | the time between two frames, the median |
| `frame/cpu_busy` | a frame's work from its start (its wait for an earlier frame over) to its present, less KSA Metal's waits in it (a drawable, a fence) |
| `frame/cpu_wait` | those waits and the wait for the earlier frame: with CPU busy, the frame |
| `gpu/busy` | the GPU's time on a frame |
| `gpu/energy` | the GPU's energy a frame, mJ: its mean power while the part played (`host/gpu_power`, `benchmark.sh` runs) times the mean frame time; not for time warp. Power alone rises when frames come faster; a frame's energy says whether the same work got cheaper |
| `frame/latency` | from a frame's start to its showing |
| `frame/displayed` | how long a frame stayed on screen |
| `frame/animation_error` | how far the step between two frames' starts differs from the step between their showing, on average: a judder the frame rate doesn't show |
| `frame/p99`, `frame/low1` | 99 % of frames took less; the mean of the slowest 1 % |
| `frame/hitches`, `frame/over100` | frames over 50 ms and twice the median of the 60 before them; frames over 100 ms |

A zone of the game's profilers is a metric too, its own time a frame: `gpu/zone/Clouds Raymarching`,
`cpu/zone/PrepareFrame`, `job/VehicleSolver/<zone>`. The report's All numbers lists every metric of every part.

The build runs the benchmark's tests, which need no game, and stops when one fails. Those in
[`../../player/benchmark-cli/tests/`](../../player/benchmark-cli/tests/) check the record (what passes and fails a run,
the conditions it notes, passes and sessions merged into medians, numbers the same on a Mac in any region, an older
record refused), PresentMon's numbers from KSA Metal's times, the statistics against values worked out by hand, the
verdicts on each basis and the zones that moved, the run's store, KSA Metal's drain records, its report and zip, the
comparison of two runs, the PNG reader, and the lineup itself.
[`command.sh`](../../player/benchmark-cli/tests/command.sh) plays [`benchmark.sh`](../../player/port-folder/benchmark.sh)
against a stand-in for the game that writes the run's store as the director does: its commands, what it refuses, and
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
| `--preset low`, `--preset medium`, `--preset high` | fixed settings over a fresh install's defaults, in a 1280x800 window without Vsync, the same on every Mac (default: your settings) |
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
| `--on '<rule>'` | with `run`, `check`, `measure`, `profile` or `investigate`: a [trigger](#triggers) (repeat for more) |
| `--env <NAME>=<value>` | the game's environment for the run, such as a KSA Metal switch (`KSA_MTL_...`; repeat for more); the report lists them |
| `--save-plan <file>` | with `measure` or `profile`: write the command line as a [study's](#studies) file and play nothing |
| `--probe <name \| path>`, `--knob <probe>.<key>=<value>` | a [probe](#probes) (`memory`, or an assembly; repeat for more) and a value for it |
| `--channels <list>` | `-zones`, `-runtime`, `-power`: channels every run records turned off; `+memory`: a probe added, as `--probe memory` |

| Command | |
|---|---|
| `list` | the tests, their scenarios and their places |
| `runs [n]` | your last runs (15, or n), the newest first |
| `report [run] [test] [--again]` | a run's report in the terminal, and its page in your browser (default: the last run; a baseline with runs of several tests needs the test; `--again`: made again from its store first) |
| `compare [run] [run]` | how the second run differs from the first (with one run: against the last; with none: the last against the one before it); exits with 1 when screenshots differ, 2 when the runs don't compare |
| `clean [--keep n] [--yes]` | delete all but your last runs (30, or n) and the baselines' runs, after asking (`--yes`: without asking); only folders a run leaves |
| `check`, `measure`, `profile`, `study`, `investigate`, `calibrate`, `baseline`, `query`, `timeline`, `explore` | for work on the port (above) |

| File | |
|---|---|
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/` | the results: `report.html`, `report.md`, `run.json` (the record), `run.db` and `host.db` (the run's store), `plan.json`, the screenshots, `game.log`, `stuck.txt` from a stuck game, `compare.html` and `compare.md` after a comparison, `timeline.html` and `timeline.pftrace` after `timeline`, `profile.md` after `profile`, `caught/` with what the triggers kept, and what the tools wrote |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/<date>/<date>.zip` | the run to share |
| `~/Documents/My Games/Kitten Space Agency/benchmarks/baselines.json`, `noise/` | the baselines' names, and the noise profiles `calibrate` measured on this Mac |
| `~/Library/Caches/ksa-macos-port/benchmark/instance/` | the game's documents folder for a run that starts the game |
| `Contents/Resources/Benchmark/` in the app | the director, `benchmark.sh`'s program, the tests, their vehicles and noise profiles |
