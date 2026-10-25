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
[hooks] cloud shadow tuning: off
[hooks] crew portraits: on, when shown
[hooks] worker parking: on, 0.25 ms
[hooks] render scale: skipped, the game's code changed (the sun's radius not found)
[hooks] documents folder: off
[hooks] restart: on
[hooks] Mac Port tab: on
[hooks] benchmark: ready
```

A hook that throws anything else has a bug of its own: it is undone the same way, and `console.log` says `failed`,
followed by the error and where it happened. The game then runs as it would without that hook; the Mac Port tab
shows the settings it does as unavailable, and a benchmark run fails, so a release can't pass its test with a hook
skipped or failed. Nothing here changes how the game saves or what it simulates; the patches only change what is
drawn and how much work drawing it takes.

## The patches

| Patch | File | How |
|---|---|---|
| Terrain cache | [`TerrainCache.cs`](patches/TerrainCache.cs) | a prefix on `PlanetRenderer.GenerateMeshData` compares the inputs of the terrain's height passes (body, mesh level of detail, snapped texture rotation, height buffer) with those the frame slot's buffer was last computed for; a transpiler routes the two dispatches of those passes through a check that skips them when nothing changed. They run every 2 s anyway, for the streamed heightmap. The normals pass is skipped too while the mesh's rotation has turned less than 1e-4 rad (its normals don't depend on where the camera is) |
| Outline skip | [`OutlineSkip.cs`](patches/OutlineSkip.cs) | `Part.Selected`'s setter counts the selected parts; `PartSelectedRenderer.Run` returns early while there are none (only selected parts mark the image it outlines) |
| Cloud shadow tuning | [`CloudShadows.cs`](patches/CloudShadows.cs) | with `cloud_shadow_tuning = "slower_refresh"`: the slices `CloudShadowVolume.UpdateVolumeDensity` refreshes per frame, 24 and 8, become 6 and 2 |
| Crew portraits | [`CrewPortraits.cs`](patches/CrewPortraits.cs) | the faces in the crew panel are views of their own, drawn every frame while the vessel has crew; after `CrewPortraitPanel.Update`, their views are hidden for the frame unless the panel was drawn in the previous one (the panel keeps their last image). With `crew_portraits = "shown_15"`, a drawn panel is refreshed at most 15 times a second; with `"always"`, the patch leaves the views alone. The setting changes while the game runs |
| Worker parking | [`WorkerPark.cs`](patches/WorkerPark.cs) | the game sets its job pools' park timeout to half the frame time every frame, so idle workers spin about 1.5 cores between jobs; `DynamicWorkerPool.SetParkTimeout` is capped at 0.25 ms, after which a worker sleeps until work arrives. |
| Trail caches | [`TrailCaches.cs`](patches/TrailCaches.cs) | with `trail_caches` set (a developer setting): a prefix on `VolumetricTrailVolumeCache.CreateVolumeCacheAtlasResources` sets the atlas budgets of the smoke's 16, 8 and 4 texel volume caches (the game's 8 MiB each) and the fewest segments a cell needs to get one (33). With `0,0,16`, the giga rocket's smoke draws about 20 % faster on an M5: the 4-texel caches are cheap to build every frame, the larger ones cost more to build than they save. Which cells are baked changes, so the smoke's pixels change within its noise |
| Plume shadow tuning | [`PlumeShadows.cs`](patches/PlumeShadows.cs) | with `plume_shadow_tuning = "low"` (or `trail_shadows` set, a developer setting): a prefix on `VolumetricTrailShadowCascades.CreateResources` sets the count, resolution and range of the shadow cascades the game draws for its Plume trail shadows setting, 4 of 128^2 out to 3 km instead of 8 of 512^2 out to 20 km. The raymarch's own self-shadow samples stay, so smoke beyond the cascades is still shaded |
| Render scale | [`RenderScale.cs`](patches/RenderScale.cs), [`Upscale.cs`](patches/Upscale.cs) | the app is marked `NSHighResolutionCapable`: the swapchain has the display's pixels and the game draws its UI at them (ImGui scales itself by the framebuffer's scale), while the window, the mouse and the UI's layout are in points. The main view renders at `render_scale` of the window's size in points through `Renderer.ExtentOverride` (set after every swapchain rebuild, unless a screenshot set its own), and its composite scales it up to the swapchain: with MetalFX, or with upscaling off with the composite's own stretch, its sampler made linear for it (and CMAA2's final blit's, which stretches its anti-aliased image without MetalFX). The cameras keep the window's size in points; the camera data, the light clustering, the UI coverage mask (the tiles of opaque UI the game skips the main view's pixels under) and the sun's radius use the render size, and the camera data's spare int carries the window's height for the shader edits `convert-shaders.sh` makes. The panels the game places from its main view's size (the Vehicle Editor's, the transfer planner's, the docking overlay's) get the window's size in points instead. With MetalFX: a sub-pixel jitter in the projection, reprojection matrices for the scene and the followed vessel (in double), `vkCmdUpscaleKSA` before the composite, which then reads the upscaled image, and a copy of the scene before the exhausts for MetalFX's reactive mask. With frame interpolation, `vkCmdMarkUiKSA` after the composite, before the UI. The game's anti-aliasing with MetalFX: the temporal upscaler smooths edges itself, and the game's MSAA x2 under it cost 1-3 ms on an M5 and left dark specks on Earth's views, so with `override_antialiasing` (on by default) `GameSettings.SampleCountOverride` (the door the game's high-resolution screenshots use, which also turns CMAA2 off) is one sample, set again after a screenshot resets it; with the spatial upscaler MSAA stays. CMAA2 is off with either (`Cmaa2Renderer.CreateResourcesIfNeeded` reads `IsCmaa2Enabled` as false): it would smooth a copy of MetalFX's image shrunk back to the render size. On a display without Retina at a render scale of 1 the three sizes are one, and the game draws as it does without the patch. Skipped (or with `hooks` off), the game draws everything at the display's pixels: about 2-3 times the GPU work on a Retina display, and the Vehicle Editor's panels off the screen |
| HUD layout | [`HudLayout.cs`](patches/HudLayout.cs) | the game lays out its flight HUD in fractions of the window's height: a panel's size is a fraction times the HUD scale, its offset from its anchor (a corner or an edge's middle of the window) a fraction alone, the buttons and frames inside the panels fractions too, and the text of the hosted panels (resources, staging, crew, burn) a fixed 20 points times the HUD scale. With `hud_layout = "fit"` the sizes belong to the display the window is on (`NSWindow.screen`, read every 0.25 s) and the positions to the window: every read of `GameSettings.GetGaugeScale` in the panels' classes (patched at the callers: the JIT inlines it), where only sizes go through it, is multiplied by the display's height / the window's and by min(1, the display's aspect / (16/9)); `ScreenReference.UvToPixels` and `PixelsToUv` inside the panels (`ImGauge`, the crew and resources panels) use the display's height, but for `ImGauge`'s window offset; `ConsoleStyle.BeginGaugeHostScope`'s scale is multiplied by the window's height / 1440, so the hosted text is the display's height / 1440 and keeps the proportion to its panel the game's layout has at 1440. Fullscreen, the HUD is the 16:9 layout scaled to the display's width; a window has the same panels at the same places in proportion to it (a small one overlaps them, and a HUD scale of the window's height / the display's gives the layout fitted to the window); moving the window to another display resizes it. The setting changes while the game runs |

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
| `KSA_TERRAIN_CACHE=0`, `KSA_OUTLINE_SKIP=0`, `KSA_WORKER_PARKING=0` | `terrain_cache`, `outline_skip`, `worker_parking`: leave out that patch |
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
