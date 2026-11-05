# KSA Metal Hooks

The hooks are the part of KSA Metal that changes the game's C# code, in memory, when the game starts, with
[Harmony](https://github.com/pardeike/Harmony). `KSA.dll` stays as it is on disk, and the repository holds only the
hooks' own code. Most of them are patches that make drawing cheaper; the others add the port's part of the game: the
Mac Port tab in the game's settings, a Restart button that works in the app, and the benchmark's runs.

The main README says what the hooks are, in short ([The hooks](../../../README.md#the-hooks)). This file has the
detail: how they load, the rule every hook follows, the hooks' own parts, the settings they read, and how to build
and test a change. What each hook changes in the game is in [`../changes/`](../changes/), with the shader edits.

| Path | What it holds |
|---|---|
| `Hooks.cs` | how the hooks apply: `[Hook]`, which marks one, the phases that order them, and the rule they follow: `Skip`, `Find`, `Fail` |
| `Settings.cs` | the settings as the hooks read them, a typed property each, with `Settings.Generated.cs`, which the build makes from the settings table ([Settings](#settings)) |
| `BenchmarkRun.cs`, `Displays.cs` | the benchmark's runs in the game; a display's desktop mode and pixels, for the Resolution list and the presets |
| [`detours/`](detours/) | mods' Harmony patches on generic methods, which arm64 needs fixed |
| [`tab/`](tab/) | the Mac Port tab and its presets |
| [`loader/`](loader/) | the startup hook, which applies the settings and loads the hooks ([How they load](#how-they-load)) |
| [`../changes/`](../changes/) | the changes to the game they apply: fixes, optimizations and features, with their own README |

Each folder of the hooks' assembly is a namespace of its own: `KsaMetal.Hooks.Detours`, `KsaMetal.Hooks.Tab`, and
those of the changes, `KsaMetal.Hooks.Fixes`, `KsaMetal.Hooks.Optimizations`, `KsaMetal.Hooks.Features`; the files
here are `KsaMetal.Hooks`, the root, where the loader finds `KsaMetal.Hooks.Hooks`. The settings table,
`port-settings.toml` and the environment are read by the port settings in the port's shared library
([`../../shared/settings/`](../../shared/settings/)), which the loader and the hooks share.

## How they load

The launcher names `KsaMetal.Hooks.Loader.dll` in `DOTNET_STARTUP_HOOKS`, so .NET runs it before the program it
starts: KSA itself, or StarMap, which loads KSA into a load context of its own. The loader
([`loader/Loader.cs`](loader/Loader.cs)) references nothing of the game. First it applies the port settings
([Settings](#settings)). Then it waits until `KSA.dll` is loaded, loads `KsaMetal.Hooks.dll` into the same load
context and applies it; with the `hooks` setting off it stops after the settings, except for a benchmark run. It loads
no Harmony itself: under StarMap, the hooks use the copy StarMap has already loaded; without StarMap, they find the
app's copy next to them, in `Contents/Resources/Hooks/`, with the port's shared library. The benchmark's director,
which the hooks give every frame, is in `Contents/Resources/Benchmark/`.

## The rule every hook follows

A hook is a class marked `[Hook]` ([`Hooks.cs`](Hooks.cs)), and it goes by the class's name, in the log and in the
settings table. The mark gives its phase and, when a setting turns it on, that setting (`Switch`). The hooks apply
phase by phase, and in a phase in the order of their names; a rule of order between hooks is a phase, which says why
(`Phase`). A hook finds its targets by name and the code it changes by pattern: an instruction it expects exactly
once, or the call that follows a field it knows. It looks everything up before it changes any of the game's state.
When something isn't where it expects it, typically after a KSA update, the hook throws `Skip`. Everything that hook
had changed is undone, and `console.log` says so:

```text
[hooks] Documents: off
[hooks] CloudShadows: off
[hooks] CrewPortraits: on, when shown
[hooks] LoadingPace: on
[hooks] OceanBlending: on
[hooks] OutlineSkip: on
[hooks] OverlaySkip: on
[hooks] RenderScale: skipped, the game's code changed (the sun's radius not found)
[hooks] ShadowSearch: off
[hooks] TerrainCache: on
[hooks] WorkerPark: on, 0.25 ms
[hooks] HiddenPartsSkip: on
[hooks] BenchmarkRun: ready
[hooks] Hdr: on
[hooks] MacPortTab: on
[hooks] Presets: on, Apple M5, 16 GB, memory 16 GB or less
[hooks] Restart: on
```

A hook that throws anything else has a bug of its own: it is undone the same way, and `console.log` says `failed`,
followed by the error and where it happened. While the game runs, the code the game calls of the terrain cache, the
ocean blending, the overlay skip, the crew portraits, the HUD layout and the Mac Port tab catches its own errors the
same way: the hook says `failed while the game ran`, once, and hands the game its own behaviour from then on. The
render scale changes the whole frame and can't hand it back halfway: an error there reaches the game. The game then
runs as it would without that hook; the Mac Port tab shows the settings it does as unavailable, and a benchmark run
fails, so a release can't pass its test with a hook skipped or failed. The patches change what is drawn and how much
work drawing it takes; only the tumbling physics and the debris settling, off by default, change what the game
simulates: a piece that settled stays settled in a save, and one sunk inside a planet is gone.

## The hooks' own parts

| Hook | File | How |
|---|---|---|
| `Presets` | [`Presets.cs`](tab/Presets.cs) | the tab's Performance, Balanced and Quality, and Game Defaults with the same settings at their defaults (a new `GameSettings`, as the game's Reset makes; the table's defaults): each gives every game setting any of them sets (fields of `GameSettings` and `GameSettings.Graphics`, found by name when the game starts, each value checked against its type) and its port settings, the settings table's presets column. Performance caps the game's terrain textures at 2048 (`TerrainTextureCap`: the planets' textures loaded whole, the streamed colour maps' finest level, ground clutter; about 0.3 GB less graphics memory in the solar system). With Memory Use on Auto (`memory_use`, [`MemoryUse.cs`](../../shared/settings/MemoryUse.cs) in the shared library: three tiers by `hw.memsize`, which `KSA_PORT_MEMORY_GB` stands in for), each also gives the Memory rows for the Mac's tier (on with 16 GB or less, off above; Smaller Particle Pool with 8 GB or less only) and, with 8 GB or less, limits the game's terrain and vessel texture caps and both shadow map sizes to 2048 and its texture streaming budget to 512 MB (on automatic it is set once from the memory free at its first frame, and load-time savings would only grow it), Game Defaults too. The game's Graphics tab shows the streaming budget but can't change it: back to automatic is `textureStreamingBudgetMb = 0` in `settings.toml`. Picking Auto or Off in the tab makes its values pending as a preset's card does, and the game's Reset gives Auto's. A fresh install's first start gets the same: the loader writes the Memory rows as it makes `port-settings.toml` (and sets `KSA_PORT_FRESH`), and a postfix on `GameSettings.LoadFromFile` limits the game's settings, just loaded, and saves them. In Fullscreen each also sets the Resolution to the size of the screen's desktop ([`Displays.cs`](Displays.cs): the GLFW shim's answer, the display's current mode or, while the game's Fullscreen has it switched, the one it had before), so the render scale alone sizes the 3D view and the display keeps its mode; Windowed and Borderless keep theirs. A preset's button sets them on the game's pending copy of its settings and as the tab's pending values, so the game's pending marks, Apply, Discard and restart popup handle them as any change |
| `MacPortTab` | [`MacPortTab.cs`](tab/MacPortTab.cs), [`MacPortWidgets.cs`](tab/MacPortWidgets.cs) | the settings window's rail gets a tab first, in a group of its own (its arrays are read-only fields, so the methods that read them read longer copies, and `OnDrawUi`'s `NavRail` call draws two groups), whose rows are drawn with the game's own widgets once the content's body has begun. The rows come from the table of the port settings (`Contents/Resources/port-settings.txt`), their values from `port-settings.toml`, and what the game runs with from the environment the loader set. The tab's pending values go with the game's pending copy of its settings, so the game's pending marks, Apply, Discard, Reset and Close, and its "restart required on apply" popup, all count them. Apply writes the changed lines into the file; live settings take effect at once. Under Render Scale a line gives the 3D view's size in pixels with the upscaling and render scale shown, worked out as [`RenderScale.cs`](../changes/features/RenderScale.cs) does |
| `GenericDetours` | [`GenericDetours.cs`](detours/GenericDetours.cs) | mods' Harmony patches on generic methods over reference types: on arm64, MonoMod stops at the method's instantiating stub, so the arguments arrive shifted by one or the patch never runs, and it crashes the game when a mod reads them. The first hook, so before any mod patches: each detour starts from the shared method, read from the stub's jump. It changes nothing in the game. It also keeps these patches running after tiered compilation, which x64 doesn't. As on x64, a patch on `M<string>` also runs for `M<object>`, and of patches on both only the last one runs. Limits: with arguments on the stack, direct calls skip the patch (the log says so); a shared method returning a struct through a return buffer, or a virtual method of a generic struct over a reference type, can't be patched (MonoMod's errors, with or without this); constructors aren't covered; a mod with its own copy of Harmony isn't covered; with the hooks off, it isn't applied |
| `BenchmarkRun` | [`BenchmarkRun.cs`](BenchmarkRun.cs) | a postfix on `Program.OnFrame` gives the benchmark's runs every frame ([`../benchmark/Runs.cs`](../benchmark/Runs.cs)), with the hooks' log, the summary of what they applied and the game's restart: a run starts from the game's first frame when the game starts with `KSA_BENCH_OUT` naming a run's folder, whose `run.json` holds the run's options (`RunOptions`, written by the benchmark's report program), or from the Mac Port tab. A run in the game that is open saves it (to disk and in memory), sets the world to a new game's through the game's own loading (`Universe.DeserializeSave`), and loads the saved game again at the end; the benchmark then writes the report with the report program |

## Settings

The hooks, KSA Metal and its shims read environment variables, which the loader sets from `port-settings.toml` when
the game starts ([`loader/PortEnvironment.cs`](loader/PortEnvironment.cs), with the port settings in the shared
library, [`../../shared/settings/`](../../shared/settings/)): a setting's variable is `KSA_MTL_` and its key in
capitals, with 0 for off and 1 for on. One set before the game starts wins over the file, and the Mac Port tab shows
its row as set outside. The loader sets each variable twice, for .NET, which keeps its own copy of the environment,
and with `setenv` for KSA Metal and the shims, which read theirs when the game first uses them. It also sends the
game's output to `console.log` and copies the file's template for a new player. The hooks read their settings through
the library, each as a typed property of [`Settings`](Settings.cs) named after its key (`terrain_cache`:
`Settings.TerrainCache`): a variable that is unset, holds none of the setting's values (`console.log` says which), or
belongs to a setting whose needs aren't met means the default. The table
[`../../shared/settings/port-settings.txt`](../../shared/settings/port-settings.txt) says which hook does each
setting; those of the hooks:

| Setting | Effect |
|---|---|
| `upscaling` | `off`, `metalfx` or `spatial` |
| `render_scale` | 0.5 to 2: a fraction of the window's size in points (it needs `upscaling`) |
| `frame_interpolation` | on: frame interpolation (KSA Metal reads it too) |
| `skip_game_anti_aliasing` | off: with MetalFX, the game's anti-aliasing setting applies (but CMAA2) |
| `fewer_cloud_shadow_updates`, `lighter_warp_cloud_shadows`, `lighter_plume_shadows`, `simpler_pcss_shadows`, `baked_milky_way`, `crew_portraits_at_15_fps`, `lighter_tumbling_physics`, `debris_settles_in_warp` | on: the Extra Performance rows |
| `skip_hidden_crew_portraits`, `fit_hud_to_display` | off: the game's own crew portraits and flight HUD; both change while the game runs |
| `terrain_cache`, `skip_unused_outlines`, `skip_unused_overlays`, `skip_hidden_vessel_parts`, `skip_unused_ocean_blending`, `idle_thread_sleep`, `loading_screen_fps_cap`, `clear_faded_smoke` | off: leave out that patch |
| `unload_far_terrain`, `load_textures_once`, `unload_far_textures`, `release_freed_memory`, `sound_file_mapping`, `smaller_particle_pool` (8 GB or less only) | on (Memory Use turns them on with 16 GB of memory or less): that patch; off, the default: the game's own |
| `load_terrain_by_altitude` | on: near a world, Unload Far Terrain keeps only the levels around each camera's altitude (it needs `unload_far_terrain`) |
| `cache_dir` | a folder: KSA Metal's cache, where Sound File Mapping keeps its copies in `sounds/` (default: `~/Library/Caches/ksa-metal`) |
| `worker_park_ms` | milliseconds: how long an idle job worker spins before it sleeps (0.25) |
| `trail_caches` | `<large>,<small>,<tiny>[,<segments>]`: the smoke's volume cache budgets in MiB (Experiments; unset: the game's) |
| `trail_shadows` | `<cascades>,<resolution>,<metres>`: the smoke's sun shadow cascades (Experiments; wins over `lighter_plume_shadows`; unset: the game's) |
| `harmony_generic_detours` | off: without generic detours, to tell whether a mod's problem comes from them (Troubleshooting) |
| `hooks` | off: the game starts without the hooks, but for a benchmark run, which they start |

To find out which patch causes a problem, turn it off in the Mac Port tab (Optimizations, Memory), or set it in the file.

## Build and test

`./build.sh` builds both assemblies against the KSA it is given, with the shared library and the benchmark
([`../../../cmake/port-folder.cmake`](../../../cmake/port-folder.cmake)): `KSA.dll` and the benchmark's director are
references only. Harmony comes from the `Lib.Harmony` package on nuget.org, pinned in
[`../../../cmake/dependencies.cmake`](../../../cmake/dependencies.cmake).

Before them, the build runs the port settings' generator's checks of the table against the hooks
([`../../shared/settings/`](../../shared/settings/)): the hooks it names exist, and the hooks read the settings it
says they do and no others; the build stops when they don't.

For the patches, the benchmark is the test. `--port` changes a port setting for one run, and the report lists the port
settings and what the hooks did:

```bash
./benchmark.sh --defaults --port hooks=false              # without the patches
./benchmark.sh --defaults --compare last                  # with them: faster, the same screenshots
./benchmark.sh measure --ab terrain_cache=false           # one of them against its absence, part by part
./benchmark.sh --port upscaling=metalfx --port render_scale=0.5 --set antialiasing=None
```

To see the game's code a patch works on, Harmony's `PatchProcessor.GetOriginalInstructions` lists a method's IL.
