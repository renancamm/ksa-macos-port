# KSA Metal Hooks

The hooks are the part of KSA Metal that changes the game's C# code, in memory, when the game starts, with
[Harmony](https://github.com/pardeike/Harmony). `KSA.dll` stays as it is on disk, and the repository holds only the
hooks' own code. Most of them are patches that make drawing cheaper; the others add the port's part of the game: the
Mac Port tab in the game's settings, a Restart button that works in the app, and the benchmark's runs.

The main README says what the hooks are, in short ([The hooks](../../README.md#the-hooks)). This file has the
detail: how they load, the rule every hook follows, what each one changes, the settings they read, and how to build
and test a change.

## How they load

The launcher names `KsaMetalHooksLoader.dll` in `DOTNET_STARTUP_HOOKS`, so .NET runs it before the program it starts:
KSA itself, or StarMap, which loads KSA into a load context of its own. The loader
([`loader/Loader.cs`](loader/Loader.cs)) references nothing of the game. First it applies the port settings
([Settings](#settings)). Then it waits until `KSA.dll` is loaded, loads `KsaMetalHooks.dll` into the same load context
and applies it; with the `hooks` setting off it stops after the settings, except for a benchmark run. It loads no
Harmony itself: under StarMap, the hooks use the copy StarMap has already loaded; without StarMap, they find the app's
copy next to them, in `Contents/Resources/Hooks/`. The benchmark's director, which the hooks start, is in
`Contents/Resources/Benchmark/`.

## The rule every hook follows

A hook finds its targets by name and the code it changes by pattern: an instruction it expects exactly once, or the
call that follows a field it knows. When something isn't where it expects it, typically after a KSA update, the hook
throws `Skip`. Everything that hook had changed is undone, and `console.log` says so:

```text
[hooks] terrain cache: on
[hooks] outline skip: on
[hooks] cloud shadows: normal
[hooks] crew portraits: on, when shown
[hooks] worker parking: on, 0.25 ms
[hooks] render scale: skipped, the game's code changed (the sun's radius not found)
[hooks] documents folder: off
[hooks] restart: on
[hooks] Mac Port tab: on
[hooks] benchmark: ready
```

The game then runs as it would without that hook; the Mac Port tab shows the settings a skipped patch does as
unavailable. Nothing here changes how the game saves or what it simulates; the patches only change what is drawn and
how much work drawing it takes.

## The patches

| Patch | File | How |
|---|---|---|
| Terrain cache | [`TerrainCache.cs`](patches/TerrainCache.cs) | a prefix on `PlanetRenderer.GenerateMeshData` compares the inputs of the terrain's height passes (body, mesh level of detail, snapped texture rotation, height buffer) with those the frame slot's buffer was last computed for; a transpiler routes the two dispatches of those passes through a check that skips them when nothing changed. They run every 2 s anyway, for the streamed heightmap. The normals pass is skipped too while the mesh's rotation has turned less than 1e-4 rad (its normals don't depend on where the camera is) |
| Outline skip | [`OutlineSkip.cs`](patches/OutlineSkip.cs) | `Part.Selected`'s setter counts the selected parts; `PartSelectedRenderer.Run` returns early while there are none (only selected parts mark the image it outlines) |
| Cloud shadows | [`CloudShadows.cs`](patches/CloudShadows.cs) | with `cloud_shadows = "slow"`: the slices `CloudShadowVolume.UpdateVolumeDensity` refreshes per frame, 24 and 8, become 6 and 2 |
| Crew portraits | [`CrewPortraits.cs`](patches/CrewPortraits.cs) | the faces in the crew panel are views of their own, drawn every frame while the vessel has crew; after `CrewPortraitPanel.Update`, their views are hidden for the frame unless the panel was drawn in the previous one (the panel keeps their last image). With `crew_portraits = "shown_15"`, a drawn panel is refreshed at most 15 times a second; with `"always"`, the patch leaves the views alone. The setting changes while the game runs |
| Worker parking | [`WorkerPark.cs`](patches/WorkerPark.cs) | the game sets its job pools' park timeout to half the frame time every frame, so idle workers spin about 1.5 cores between jobs; `DynamicWorkerPool.SetParkTimeout` is capped at 0.25 ms, after which a worker sleeps until work arrives. Same frames, about 1.6 cores less CPU time |
| Trail caches | [`TrailCaches.cs`](patches/TrailCaches.cs) | with `trail_caches` set (a developer setting): a prefix on `VolumetricTrailVolumeCache.CreateVolumeCacheAtlasResources` sets the atlas budgets of the smoke's 16, 8 and 4 texel volume caches (the game's 8 MiB each) and the fewest segments a cell needs to get one (33). With `0,0,16`, the giga rocket's smoke draws about 20 % faster on an M5: the 4-texel caches are cheap to build every frame, the larger ones cost more to build than they save. Which cells are baked changes, so the smoke's pixels change within its noise |
| Plume shadows | [`PlumeShadows.cs`](patches/PlumeShadows.cs) | with `plume_shadows = "light"` (or `trail_shadows` set, a developer setting): a prefix on `VolumetricTrailShadowCascades.CreateResources` sets the count, resolution and range of the shadow cascades the game draws for its Plume trail shadows setting, 4 of 128^2 out to 3 km instead of 8 of 512^2 out to 20 km. The raymarch's own self-shadow samples stay, so smoke beyond the cascades is still shaded |
| Render scale | [`RenderScale.cs`](patches/RenderScale.cs), [`Upscale.cs`](patches/Upscale.cs) | with `upscaling` on, the main view renders at `render_scale` of the window through `Renderer.ExtentOverride` (set after every swapchain rebuild, unless a screenshot set its own), and its composite stretches it back. The cameras keep the window's size; the camera data, the light clustering and the sun's radius use the render size, and the camera data's spare int carries the window's height for the shader edits `convert-shaders.sh` makes. With MetalFX: a sub-pixel jitter in the projection, reprojection matrices for the scene and the followed vessel (in double), `vkCmdUpscaleKSA` before the composite, which then reads the upscaled image, and a copy of the scene before the exhausts for MetalFX's reactive mask. With frame interpolation, `vkCmdMarkUiKSA` after the composite, before the UI |

The MetalFX entry points are KSA Metal's own ([`../ksa-metal/upscale.mm`](../ksa-metal/upscale.mm)).

## The port's part of the game

| Hook | File | How |
|---|---|---|
| Mac Port tab | [`MacPortTab.cs`](MacPortTab.cs), [`PortSettings.cs`](PortSettings.cs) | the settings window's rail gets a ninth tab (its arrays are read-only fields, so the methods that read them read longer copies), whose rows are drawn with the game's own widgets once the content's body has begun. The rows come from the table of the port settings (`Contents/Resources/port-settings.txt`), their values from `port-settings.toml`, and what the game runs with from the environment the loader set. The tab's pending values go with the game's pending copy of its settings, so the game's pending marks, Apply, Discard, Reset and Close, and its "restart required on apply" popup, all count them. Apply writes the changed lines into the file; live settings take effect at once |
| Restart | [`Restart.cs`](Restart.cs) | the popup's Restart starts the game's own executable again, which in the app is .NET's `dotnet` with no program: the game only quits. A transpiler replaces that call: once the game has quit, the app's launcher starts it again (`KSA_PORT_LAUNCHER`), so the new game reads the port settings as any start does |
| Documents folder | [`Documents.cs`](Documents.cs) | `STARMAP_INSTANCE_PATH` moves the game's documents folder, as StarMap does, also without StarMap (the benchmark's runs) |
| Benchmark | [`BenchmarkRun.cs`](BenchmarkRun.cs) | a postfix on `Program.OnFrame` runs the benchmark's director ([`../benchmark/`](../benchmark/)): from the game's first frame when the game starts with `KSA_BENCH_*` set, or from the Mac Port tab. A run in the game that is open saves it (to disk and in memory), sets the world to a new game's through the game's own loading (`Universe.DeserializeSave`), and loads the saved game again at the end; the hooks then write the report with the report program |

## Settings

The hooks, KSA Metal and its shims read environment variables, which the loader sets from `port-settings.toml` when
the game starts ([`loader/PortEnvironment.cs`](loader/PortEnvironment.cs), with the table and file reading of
[`PortSettingsTable.cs`](PortSettingsTable.cs), which the hooks share); one set before the game starts wins over the
file, and the Mac Port tab shows its row as set outside. The loader does this in the game's process because, started
from the Finder, macOS refuses the launcher (a shell script) the documents folder and lets the game's process in. It
sets each variable twice, for .NET, which keeps its own copy of the environment, and with `setenv` for KSA Metal and
the shims, which read theirs when the game first uses them. It also sends the game's output to `console.log` and
copies the file's template for a new player. macOS reads Retina's `NSHighResolutionCapable` in `Info.plist` as the
game starts, so the tab writes it when it saves the setting; for a hand-edited file the loader writes it, and it takes
effect from the next start. The table [`../launcher/port-settings.txt`](../launcher/port-settings.txt)
lists every setting with its variable; those of the hooks:

| Variable | Setting |
|---|---|
| `KSA_UPSCALER=off\|metalfx\|spatial` | `upscaling` |
| `KSA_RENDER_SCALE=<0.25..1>` | `render_scale` (the file allows 0.5 to 1.0; it needs `upscaling`) |
| `KSA_MTL_FX_INTERPOLATE=1` | `frame_interpolation` (KSA Metal reads it too) |
| `KSA_CLOUD_SHADOWS=normal\|slow` | `cloud_shadows` |
| `KSA_PLUME_SHADOWS=normal\|light` | `plume_shadows` |
| `KSA_CREW_PORTRAITS=always\|shown\|shown_15` | `crew_portraits` |
| `KSA_TERRAIN_CACHE=0`, `KSA_OUTLINE_SKIP=0`, `KSA_WORKER_PARKING=0` | `terrain_cache`, `outline_skip`, `worker_parking`: leave out that patch |
| `KSA_PARK_MS=<ms>` | `worker_park_ms`: how long an idle job worker spins before it sleeps (0.25) |
| `KSA_TRAIL_CACHE=<large>,<small>,<tiny>[,<segments>]` | `trail_caches`: the smoke's volume cache budgets in MiB (developer setting; unset: the game's) |
| `KSA_TRAIL_SHADOWS=<cascades>,<resolution>,<metres>` | `trail_shadows`: the smoke's sun shadow cascades (developer setting; wins over `plume_shadows`; unset: the game's) |
| `KSA_HOOKS=0` | `hooks`: the game starts without the hooks, but for a benchmark run, which they start |

To find out which patch causes a problem, turn it off in the Mac Port tab (Optimizations), or set it in the file.

## Build and test

`./build.sh` builds both assemblies against the KSA it is given, like the benchmark
([`scripts/build-hooks.sh`](../../scripts/build-hooks.sh)): `KSA.dll` and the benchmark's director are references
only. Harmony comes from the `Lib.Harmony` package on nuget.org, pinned in
[`cmake/dependencies.cmake`](../../cmake/dependencies.cmake).

The benchmark is the test. `--port` changes a port setting for one run, and the report lists the port settings and
what the hooks did:

```bash
./benchmark.sh --fast --port hooks=false                  # without the patches
./benchmark.sh --fast --compare <that run>                 # with them: faster, the same screenshots
./benchmark.sh --fast --port terrain_cache=false           # without one of them
./benchmark.sh --fast --port upscaling=metalfx --port render_scale=0.5 --set antialiasing=None
```

To see the game's code a patch works on, Harmony's `PatchProcessor.GetOriginalInstructions` lists a method's IL.
