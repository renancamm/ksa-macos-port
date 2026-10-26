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
call that follows a field it knows. It looks everything up before it changes any of the game's state. When
something isn't where it expects it, typically after a KSA update, the hook throws `Skip`. Everything that hook had
changed is undone, and `console.log` says so:

```text
[hooks] terrain cache: on
[hooks] outline skip: on
[hooks] shadow search: on
[hooks] cloud shadow tuning: off
[hooks] crew portraits: on, when shown
[hooks] worker parking: on, 0.25 ms
[hooks] loading pace: on
[hooks] render scale: skipped, the game's code changed (the sun's radius not found)
[hooks] documents folder: off
[hooks] restart: on
[hooks] Mac Port tab: on
[hooks] benchmark: ready
```

A hook that throws anything else has a bug of its own: it is undone the same way, and `console.log` says `failed`,
followed by the error and where it happened. While the game runs, the code the game calls of the terrain cache, the
crew portraits, the HUD layout and the Mac Port tab catches its own errors the same way: the hook says `failed while
the game ran`, once, and hands the game its own behaviour from then on. The render scale changes the whole frame and
can't hand it back halfway: an error there reaches the game. The game then runs as it would without that hook; the Mac Port tab
shows the settings it does as unavailable, and a benchmark run fails, so a release can't pass its test with a hook
skipped or failed. Nothing here changes how the game saves or what it simulates; the patches only change what is
drawn and how much work drawing it takes.

## The patches

| Patch | File | How |
|---|---|---|
| Terrain cache | [`TerrainCache.cs`](patches/TerrainCache.cs) | a prefix on `PlanetRenderer.GenerateMeshData` compares the inputs of the terrain's height passes (body, mesh level of detail, snapped texture rotation, height buffer) with those the frame slot's buffer was last computed for; a transpiler routes the two dispatches of those passes through a check that skips them when nothing changed. They run every 2 s anyway, for the streamed heightmap. The normals pass is skipped too while the mesh's rotation has turned less than 1e-4 rad (its normals don't depend on where the camera is) |
| Outline skip | [`OutlineSkip.cs`](patches/OutlineSkip.cs) | `Part.Selected`'s setter counts the selected parts; `PartSelectedRenderer.Run` returns early while there are none (only selected parts mark the image it outlines) |
| Shadow search limit | [`ShadowSearch.cs`](patches/ShadowSearch.cs) | with the game's soft shadows (PCSS), each pixel searches the shadow cascades for blockers as far as the widest penumbra the sun casts, 1 m: hundreds of texels in the cascades near the camera, 16 taps that miss the GPU's caches. A prefix on `CascadedShadowMap.UploadCascades` puts a limit of 32 texels in the cascades' spare `_pad1`, which the shader edit of `convert-shaders.sh` reads (0: the game's search). On an M5 the cockpit draws in 25 ms instead of 44. A penumbra is about 1/200 of its caster's height, so in the three nearest cascades a shadow cast from more than about 8, 30 or 85 m above can get a sharper edge; none of the views tried showed a change |
| Cloud shadow tuning | [`CloudShadows.cs`](patches/CloudShadows.cs) | with `cloud_shadow_tuning = "slower_refresh"`: the slices `CloudShadowVolume.UpdateVolumeDensity` refreshes per frame, 24 and 8, become 6 and 2 |
| Crew portraits | [`CrewPortraits.cs`](patches/CrewPortraits.cs) | the faces in the crew panel are views of their own, drawn every frame while the vessel has crew; after `CrewPortraitPanel.Update`, their views are hidden for the frame unless the panel was drawn in the previous one (the panel keeps their last image). With `crew_portraits = "shown_15"`, a drawn panel is refreshed at most 15 times a second; with `"always"`, the patch leaves the views alone. The setting changes while the game runs |
| Worker parking | [`WorkerPark.cs`](patches/WorkerPark.cs) | the game sets its job pools' park timeout to half the frame time every frame, so idle workers spin about 1.5 cores between jobs; `DynamicWorkerPool.SetParkTimeout` is capped at 0.25 ms, after which a worker sleeps until work arrives. |
| Loading screen pace | [`LoadingPace.cs`](patches/LoadingPace.cs) | while the game loads, `Loading.OnFrame` draws the loading screen after every step it reports (`Loading.Task`, `Loading.State`), and each frame waits for the display (the previous present, then a drawable): with V-Sync, steps shorter than a refresh wait for one each. A prefix lets the main thread's frames through at most every 33 ms and returns at once in between (a new `Loading.State` label always draws; other threads' calls go to the game, which ignores them); the game loads in 9 s instead of 11 on an M5 |
| Trail caches | [`TrailCaches.cs`](patches/TrailCaches.cs) | with `trail_caches` set (a developer setting): a prefix on `VolumetricTrailVolumeCache.CreateVolumeCacheAtlasResources` sets the atlas budgets of the smoke's 16, 8 and 4 texel volume caches (the game's 8 MiB each) and the fewest segments a cell needs to get one (33). With `0,0,16`, the giga rocket's smoke draws about 20 % faster on an M5: the 4-texel caches are cheap to build every frame, the larger ones cost more to build than they save. Which cells are baked changes, so the smoke's pixels change within its noise |
| Plume shadow tuning | [`PlumeShadows.cs`](patches/PlumeShadows.cs) | with `plume_shadow_tuning = "low"` (or `trail_shadows` set, a developer setting): a prefix on `VolumetricTrailShadowCascades.CreateResources` sets the count, resolution and range of the shadow cascades the game draws for its Plume trail shadows setting, 4 of 128^2 out to 3 km instead of 8 of 512^2 out to 20 km. The raymarch's own self-shadow samples stay, so smoke beyond the cascades is still shaded |
| Render scale | [`RenderScale.cs`](patches/RenderScale.cs), [`Upscale.cs`](patches/Upscale.cs) | the app is marked `NSHighResolutionCapable`, so the swapchain has the display's pixels, while the window, the mouse and the UI's layout are in points. The main view renders at `render_scale` of the window's size in points (`Renderer.ExtentOverride`, set after every swapchain rebuild unless a screenshot set its own), and the final composite scales it up: with MetalFX (`vkCmdUpscaleKSA`, with a sub-pixel jitter, reprojections in double and a copy of the scene before the exhausts for the reactive mask), or with its own stretch, filtered. The cameras keep the window's size; the camera data, the light clustering, the UI coverage mask and the sun's radius use the render size, the shader edits of `convert-shaders.sh` get the window's height in the camera data's spare int, and the panels the game places from its main view (the Vehicle Editor's, the transfer planner's, the docking overlay's, the celestial info window) get the window's size. Under MetalFX temporal, `override_antialiasing` gives the game one sample (its MSAA x2 cost 1-3 ms on an M5 and left dark specks on Earth's views); CMAA2 is off with either upscaler. With frame interpolation, `UiCoverageMaskSystem.RecordMaskGeneration` reads the game's UI pixel culling as off: culling leaves the scene black under opaque UI, which MetalFX smeared into the HUD's labels in the frames it made (and probably warped out beside it: the boxes players saw). On a display without Retina at a render scale of 1 the game draws as it does without the patch. Skipped, it draws everything at the display's pixels: about 2-3 times the GPU work on a Retina display, and the Vehicle Editor's panels off the screen. The files' headers have the whole mechanism |
| HUD layout | [`HudLayout.cs`](patches/HudLayout.cs) | the game lays out its flight HUD in fractions of the window's height, and the text of the hosted panels (resources, staging, crew, burn) at a fixed 20 points times the HUD scale: a window shrinks the HUD, its panels overlap on a display narrower than 16:9, and the text overflows them below 1440 high. With `hud_layout = "fit"` the sizes belong to the display the window is on (`NSWindow.screen`, read every 0.25 s) and the positions to the window: the panels' reads of `GameSettings.GetGaugeScale` (patched at the callers: the JIT inlines it) are multiplied by the display's height / the window's and by min(1, the display's aspect / (16/9)), the conversions inside the panels use the display's height (but for `ImGauge`'s window offset), and the hosted text scales with the window's height / 1440. The setting changes while the game runs; the file's header has the whole mechanism |

The MetalFX entry points are KSA Metal's own ([`../ksa-metal/upscale.mm`](../ksa-metal/upscale.mm)).

## The port's part of the game

| Hook | File | How |
|---|---|---|
| Mac Port tab | [`MacPortTab.cs`](MacPortTab.cs), [`PortSettings.cs`](PortSettings.cs) | the settings window's rail gets a ninth tab (its arrays are read-only fields, so the methods that read them read longer copies), whose rows are drawn with the game's own widgets once the content's body has begun. The rows come from the table of the port settings (`Contents/Resources/port-settings.txt`), their values from `port-settings.toml`, and what the game runs with from the environment the loader set. The tab's pending values go with the game's pending copy of its settings, so the game's pending marks, Apply, Discard, Reset and Close, and its "restart required on apply" popup, all count them. Apply writes the changed lines into the file; live settings take effect at once |
| Restart | [`Restart.cs`](Restart.cs) | the popup's Restart starts the game's own executable again, which in the app is .NET's `dotnet` with no program: the game only quits. A transpiler replaces that call: it starts the app again through macOS (`open -n`, which hands the app its own environment: the game's, without what the port settings set, and `KSA_PORT_WAIT`, the game's process id), and the new launcher waits until this game has quit. A process the game started itself would not survive: when the app was started from the Finder, macOS ends what a quitting app leaves running. The new game reads the port settings as any start does |
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
copies the file's template for a new player. The table [`../launcher/port-settings.txt`](../launcher/port-settings.txt)
lists every setting with its variable, its values and its default, and the hooks read their settings against it
([`Settings.cs`](Settings.cs)): a variable that is unset, or holds none of the setting's values, means the default
(`console.log` says which). Those of the hooks:

| Variable | Setting |
|---|---|
| `KSA_UPSCALER=off\|metalfx\|spatial` | `upscaling` |
| `KSA_RENDER_SCALE=<0.5..2>` | `render_scale`, a fraction of the window's size in points (it needs `upscaling`) |
| `KSA_MTL_FX_INTERPOLATE=1` | `frame_interpolation` (KSA Metal reads it too) |
| `KSA_OVERRIDE_AA=0` | `override_antialiasing`: with MetalFX, the game's anti-aliasing setting applies (but CMAA2) |
| `KSA_CLOUD_SHADOW_TUNING=off\|slower_refresh` | `cloud_shadow_tuning` |
| `KSA_PLUME_SHADOW_TUNING=off\|low` | `plume_shadow_tuning` |
| `KSA_CREW_PORTRAITS=always\|shown\|shown_15` | `crew_portraits` |
| `KSA_HUD_LAYOUT=fit\|game` | `hud_layout` |
| `KSA_TERRAIN_CACHE=0`, `KSA_OUTLINE_SKIP=0`, `KSA_SHADOW_SEARCH=0`, `KSA_WORKER_PARKING=0`, `KSA_LOADING_PACE=0` | `terrain_cache`, `outline_skip`, `shadow_search`, `worker_parking`, `loading_pace`: leave out that patch |
| `KSA_PARK_MS=<ms>` | `worker_park_ms`: how long an idle job worker spins before it sleeps (0.25) |
| `KSA_TRAIL_CACHE=<large>,<small>,<tiny>[,<segments>]` | `trail_caches`: the smoke's volume cache budgets in MiB (developer setting; unset: the game's) |
| `KSA_TRAIL_SHADOWS=<cascades>,<resolution>,<metres>` | `trail_shadows`: the smoke's sun shadow cascades (developer setting; wins over `plume_shadow_tuning`; unset: the game's) |
| `KSA_HOOKS=0` | `hooks`: the game starts without the hooks, but for a benchmark run, which they start |

To find out which patch causes a problem, turn it off in the Mac Port tab (Optimizations), or set it in the file.

## Build and test

`./build.sh` builds both assemblies against the KSA it is given, like the benchmark
([`scripts/build-hooks.sh`](../../scripts/build-hooks.sh)): `KSA.dll` and the benchmark's director are references
only. Harmony comes from the `Lib.Harmony` package on nuget.org, pinned in
[`cmake/dependencies.cmake`](../../cmake/dependencies.cmake).

The build then runs the tests of the code that needs no game ([`tests/`](tests/)), and stops when one fails: every
line of the settings table keeps the table's rules (a default among its values, needs that exist, without a loop,
the kinds the tab draws), the hooks it names and the settings the hooks read exist, and reading and writing
`port-settings.toml`, the needs, the values read back from the environment and what the loader sets work as they
should.

For the patches, the benchmark is the test. `--port` changes a port setting for one run, and the report lists the port settings and
what the hooks did:

```bash
./benchmark.sh --fast --port hooks=false                  # without the patches
./benchmark.sh --fast --compare <that run>                 # with them: faster, the same screenshots
./benchmark.sh --fast --port terrain_cache=false           # without one of them
./benchmark.sh --fast --port upscaling=metalfx --port render_scale=0.5 --set antialiasing=None
```

To see the game's code a patch works on, Harmony's `PatchProcessor.GetOriginalInstructions` lists a method's IL.
