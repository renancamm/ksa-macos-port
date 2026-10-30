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
[hooks] overlay skip: on
[hooks] ocean blending: on
[hooks] cloud shadow tuning: off
[hooks] shadow search: off
[hooks] crew portraits: on, when shown
[hooks] worker parking: on, 0.25 ms
[hooks] loading pace: on
[hooks] render scale: skipped, the game's code changed (the sun's radius not found)
[hooks] hidden parts skip: on
[hooks] documents folder: off
[hooks] restart: on
[hooks] HDR brightness: on
[hooks] presets: on, Apple M5, 16 GB
[hooks] Mac Port tab: on
[hooks] benchmark: ready
```

A hook that throws anything else has a bug of its own: it is undone the same way, and `console.log` says `failed`,
followed by the error and where it happened. While the game runs, the code the game calls of the terrain cache, the
ocean blending, the overlay skip, the crew portraits, the HUD layout and the Mac Port tab catches its own errors the same way: the hook says `failed while
the game ran`, once, and hands the game its own behaviour from then on. The render scale changes the whole frame and
can't hand it back halfway: an error there reaches the game. The game then runs as it would without that hook; the Mac Port tab
shows the settings it does as unavailable, and a benchmark run fails, so a release can't pass its test with a hook
skipped or failed. Nothing here changes how the game saves or what it simulates; the patches only change what is
drawn and how much work drawing it takes.

## The patches

| Patch | Tab row | File | How |
|---|---|---|---|
| Terrain cache | Terrain Cache | [`TerrainCache.cs`](patches/TerrainCache.cs) | a prefix on `PlanetRenderer.GenerateMeshData` compares the inputs of the terrain's height passes (body, mesh level of detail, snapped texture rotation, height buffer) with those the frame slot's buffer was last computed for; a transpiler routes the two dispatches of those passes through a check that skips them when nothing changed. They run every 2 s anyway, for the streamed heightmap. The normals pass is skipped too while the mesh's rotation has turned less than 1e-4 rad (its normals don't depend on where the camera is) |
| Outline skip | Skip Unused Outlines | [`OutlineSkip.cs`](patches/OutlineSkip.cs) | `Part.Selected`'s setter counts the selected parts; `PartSelectedRenderer.Run` returns early while there are none (only selected parts mark the image it outlines) |
| Overlay skip | Skip Unused Overlays | [`OverlaySkip.cs`](patches/OverlaySkip.cs) | every frame, after the bloom, `GizmoPass.Run` clears an image of its own and blends it over the whole view, twice, also without gizmos; with MSAA, `SingleToMultisamplePass` first copies the main view into its multisampled image for the orbit lines and gizmos, and `ResolveAttachments` resolves it back after them. A prefix returns from `GizmoPass.Run` while no gizmo has instances; in the main view without orbit lines either, a prefix skips the copy, and the resolve after it runs with the target's colour hidden, so it resolves depth only (the copy gave every sample the view's own value). Other views keep both: they draw their translucent meshes in between. Same image; with MSAA x2 about 0.2-0.5 ms less GPU time a frame on an M5 |
| Hidden parts skip | Skip Hidden Vessel Parts | [`HiddenPartsSkip.cs`](patches/HiddenPartsSkip.cs) | the vessels' shaders mark selected parts in the selection outline's mask, a store from the fragment shader, so they run before the depth test and shade every hidden fragment too. KSA Metal keeps a second state of such pipelines that tests depth first ([KSA Metal](../ksa-metal/README.md)); prefixes on `Program.RenderGame` and `RenderEditor` have the frame drawn with it while no part is selected (counted in `Part.Selected`'s setter), when the stores never run. Same image; on an M5 at the game's own settings the cockpit about 11 % less GPU time a frame |
| Ocean blending | Skip Unused Ocean Blending | [`OceanBlending.cs`](patches/OceanBlending.cs) | the ocean blends into the image behind it with its alpha, which its shader writes as 0 unless the shore filter fades the ocean out, above 10 km only. Postfixes on `OceanRenderer.Build` and `DestroyFrameResources` make and free twins of the two solid ocean pipelines without blending (the game's own `CreateOceanPipeline`); a prefix on `PerformRendering` checks the camera's altitude over the ocean, and a transpiler routes its two pipeline loads to the twins below 9 km. Same image; at sea level with MSAA about 10 % less GPU time on an M5 (no read of what is behind, hidden-surface removal for the overlapping waves). It checks the shader lines it rests on and skips when they changed |
| PCSS shadow search limit | Simpler PCSS Shadows | [`ShadowSearch.cs`](patches/ShadowSearch.cs) | with `shadow_search` on and the game's soft shadows (PCSS), each pixel searches the shadow cascades for blockers as far as the widest penumbra the sun casts, 1 m: hundreds of texels in the cascades near the camera, 16 taps that miss the GPU's caches. A prefix on `CascadedShadowMap.UploadCascades` puts a limit of 32 texels in the cascades' spare `_pad1`, which the shader edit of `convert-shaders.sh` reads (0: the game's search). On an M5 at the Quality preset, with the edit that skips shadows where there is no sunlight already in, it takes about 8 % off the GPU time of grass (fur 32.4 -> 29.7 ms) and 3 % off views with shadows near the camera; the cockpit doesn't change. A penumbra is about 1/200 of its caster's height, so in the three nearest cascades a shadow cast from more than about 8, 30 or 85 m above can get a sharper edge; none of the views tried showed a change |
| Milky Way bake | Baked Milky Way | [`MilkyWayBake.cs`](patches/MilkyWayBake.cs) | with `milky_way_bake` on: `MilkyWay.frag` works out the Milky Way's dust clouds for every pixel of its band from the view direction alone, about 90 lookups of a noise texture each, 1-2 ms of a frame with much sky on an M5 (5-7 at the game's Milky Way Quality Ultra). The shader edit of `convert-shaders.sh` moves the clouds into a function and writes `MilkyWayBake.comp` from the same file. A postfix on `MilkyWayRenderer.GenerateNoise` fills an 8192 x 512 strip of the clouds over the band (8 MB) and puts it in the noise's place, so the game binds it to the shader, which reads the clouds from it when its texture isn't square; a postfix on `BuildPipeline` fills it again when the quality (the clouds' octaves) changes. At the default field of view (50°) on a Retina display a texel of the strip is about 1.7 pixels, and the sky's pixels change by at most 1 of 255 in the views tried; the clouds are sampled from the strip, so zoomed in or in a larger window their finest grain can look softer. With the strip, at the Performance and Balanced presets, Milky Way Quality High costs what Low and Medium do (every view within 0.1 ms; Ultra not measured) |
| Cloud shadow tuning | Cloud Shadow Updates | [`CloudShadows.cs`](patches/CloudShadows.cs) | with `cloud_shadow_tuning = "slower_refresh"`: the slices `CloudShadowVolume.UpdateVolumeDensity` refreshes per frame, 24 and 8, become 6 and 2 |
| Crew portraits | Crew Portraits | [`CrewPortraits.cs`](patches/CrewPortraits.cs) | the faces in the crew panel are views of their own, drawn every frame while the vessel has crew; after `CrewPortraitPanel.Update`, their views are hidden for the frame unless the panel was drawn in the previous one (the panel keeps their last image). With `crew_portraits = "shown_15"`, a drawn panel is refreshed at most 15 times a second; with `"always"`, the patch leaves the views alone. The setting changes while the game runs |
| Worker parking | Idle Thread Sleep | [`WorkerPark.cs`](patches/WorkerPark.cs) | the game sets its job pools' park timeout to half the frame time every frame, so idle workers spin about 1.5 cores between jobs; `DynamicWorkerPool.SetParkTimeout` is capped at 0.25 ms, after which a worker sleeps until work arrives. |
| Loading screen pace | Loading Screen FPS Cap | [`LoadingPace.cs`](patches/LoadingPace.cs) | while the game loads, `Loading.OnFrame` draws the loading screen after every step it reports (`Loading.Task`, `Loading.State`), and each frame waits for the display (the previous present, then a drawable): with Vsync, steps shorter than a refresh wait for one each. A prefix lets the main thread's frames through at most every 33 ms and returns at once in between (a new `Loading.State` label always draws; other threads' calls go to the game, which ignores them); the game loads in 9 s instead of 11 on an M5 |
| Trail caches | none (developer) | [`TrailCaches.cs`](patches/TrailCaches.cs) | with `trail_caches` set (a developer setting): a prefix on `VolumetricTrailVolumeCache.CreateVolumeCacheAtlasResources` sets the atlas budgets of the smoke's 16, 8 and 4 texel volume caches (the game's 8 MiB each) and the fewest segments a cell needs to get one (33). With `0,0,16`, the giga rocket's smoke draws about 20 % faster on an M5: the 4-texel caches are cheap to build every frame, the larger ones cost more to build than they save. Which cells are baked changes, so the smoke's pixels change within its noise |
| Plume shadow tuning | Plume Shadow Quality | [`PlumeShadows.cs`](patches/PlumeShadows.cs) | with `plume_shadow_tuning = "low"` (or `trail_shadows` set, a developer setting): a prefix on `VolumetricTrailShadowCascades.CreateResources` sets the count, resolution and range of the shadow cascades the game draws for its Plume trail shadows setting, 4 of 128^2 out to 3 km instead of 8 of 512^2 out to 20 km. The raymarch's own self-shadow samples stay, so smoke beyond the cascades is still shaded |
| Render scale | Upscaling, Render Scale, Skip Game Anti-Aliasing | [`RenderScale.cs`](patches/RenderScale.cs), [`Upscale.cs`](patches/Upscale.cs) | the app is marked `NSHighResolutionCapable`, so the swapchain has the display's pixels, while the window, the mouse and the UI's layout are in points. The main view renders at `render_scale` of the window's size in points (`Renderer.ExtentOverride`, set after every swapchain rebuild unless a screenshot set its own), and the final composite scales it up: with MetalFX (`vkCmdUpscaleKSA`, with a sub-pixel jitter, reprojections in double and a copy of the scene before the exhausts for the reactive mask), or with its own stretch, filtered. The cameras keep the window's size; the camera data, the light clustering, the UI coverage mask and the sun's radius use the render size, the shader edits of `convert-shaders.sh` get the window's height in the camera data's spare int, and the panels the game places from its main view (the Vehicle Editor's, the transfer planner's, the docking overlay's, the celestial info window) get the window's size. Under MetalFX temporal, `override_antialiasing` gives the game one sample (its MSAA x2 cost 1-3 ms on an M5 and left dark specks on Earth's views); CMAA2 is off with either upscaler. The cockpit's ray tracing (`RaytracingRenderer.RunRaytracePass`) gets the jittered projection too: it finds each pixel's surface from the prepass depth, and with the camera's own its points landed off their surfaces, differently every frame, and its lighting flickered. With frame interpolation, `UiCoverageMaskSystem.RecordMaskGeneration` reads the game's UI pixel culling as off: culling leaves the scene black under opaque UI, which MetalFX smeared into the HUD's labels in the frames it made (and probably warped out beside it: the boxes players saw). On a display without Retina at a render scale of 1 the game draws as it does without the patch. Skipped, it draws everything at the display's pixels: about 2-3 times the GPU work on a Retina display, and the Vehicle Editor's panels off the screen. The files' headers have the whole mechanism |
| HUD layout | HUD Layout | [`HudLayout.cs`](patches/HudLayout.cs) | the game lays out its flight HUD in fractions of the window's height, and the text of the hosted panels (resources, staging, crew, burn) at a fixed 20 points times the HUD scale: a window shrinks the HUD, its panels overlap on a display narrower than 16:9, and the text overflows them below 1440 high. With `hud_layout = "fit"` the sizes belong to the display the window is on (`NSWindow.screen`, read every 0.25 s) and the positions to the window: the panels' reads of `GameSettings.GetGaugeScale` (patched at the callers: the JIT inlines it) are multiplied by the display's height / the window's and by min(1, the display's aspect / (16/9)), the conversions inside the panels use the display's height (but for `ImGauge`'s window offset), and the hosted text scales with the window's height / 1440. The setting changes while the game runs; the file's header has the whole mechanism |
| Resolution list | none (always on) | [`ResolutionList.cs`](patches/ResolutionList.cs) | the Display tab's Resolution lists sizes in points (`ScreenResolution`, from GLFW's video modes), so on a 14" MacBook Pro its largest size reads 1512 x 982. A transpiler on `GameSettings.OnDrawUi` swaps its `DrawLiveValueCombo<ScreenResolution>` call for the same combo whose labels are in pixels, from the desktop mode of the list's display ([`Displays.cs`](Displays.cs), `glfwGetCocoaMonitor`), whose kind of mode the shim lists, and marks the size that fills the display's own pixels, which the shim reads from its native modes (3024 x 1964 native); the game still matches and saves the sizes in points. Picking another Screen kept the old one's Resolution and Refresh Rate, which the new list may not have: the same transpiler swaps `UpdateScreenSelection` for a call that runs it and then sets a size or rate the new screen lacks to its native size (else its desktop's) and its highest rate at that size. The [GLFW shim](../glfw-shim/glfw_shim.c) decides which sizes are listed |
| Editor clutter | none (always on) | [`EditorClutter.cs`](patches/EditorClutter.cs) | a game bug since KSA 5707: `PrePassRenderer.Render` draws the ground clutter's depth and normals, also in the Vehicle Editor, which keeps the clutter of the place it was opened from and draws none in colour. The light pre-pass lights those pixels and the enclosure's walls read that light by screen position: a ghost of the grass and trees. A prefix on `GroundClutterRenderer.RenderGroundClutterDepthNormal` returns while `Program.EditorFlag` is set; flight doesn't change |

The MetalFX entry points are KSA Metal's own ([`../ksa-metal/upscale.mm`](../ksa-metal/upscale.mm)).

## The port's part of the game

| Hook | File | How |
|---|---|---|
| Presets | [`Presets.cs`](Presets.cs) | the tab's Performance, Balanced and Quality, and Game Defaults with the same settings at their defaults (a new `GameSettings`, as the game's Reset makes; the table's defaults): each gives every game setting any of them sets (fields of `GameSettings` and `GameSettings.Graphics`, found by name when the game starts, each value checked against its type) and its port settings. The Mac's memory (`hw.memsize`; 8 GB or less: shadow maps and textures at most 2048) and chip (`machdep.cpu.brand_string`; M1 and M2: no `IVARayTracing`) decide some of them (not Game Defaults', the same on every Mac). In Fullscreen each also sets the Resolution to the size of the screen's desktop ([`Displays.cs`](Displays.cs): the GLFW shim's answer, the display's current mode or, while the game's Fullscreen has it switched, the one it had before), so the render scale alone sizes the 3D view and the display keeps its mode; Windowed and Borderless keep theirs. A preset's button sets them on the game's pending copy of its settings and as the tab's pending values, so the game's pending marks, Apply, Discard and restart popup handle them as any change |
| Mac Port tab | [`MacPortTab.cs`](MacPortTab.cs), [`PortSettings.cs`](PortSettings.cs) | the settings window's rail gets a ninth tab (its arrays are read-only fields, so the methods that read them read longer copies), whose rows are drawn with the game's own widgets once the content's body has begun. The rows come from the table of the port settings (`Contents/Resources/port-settings.txt`), their values from `port-settings.toml`, and what the game runs with from the environment the loader set. The tab's pending values go with the game's pending copy of its settings, so the game's pending marks, Apply, Discard, Reset and Close, and its "restart required on apply" popup, all count them. Apply writes the changed lines into the file; live settings take effect at once. Under Render Scale a line gives the 3D view's size in pixels with the upscaling and render scale shown, worked out as [`RenderScale.cs`](patches/RenderScale.cs) does |
| Restart | [`Restart.cs`](Restart.cs) | the popup's Restart starts the game's own executable again, which in the app is .NET's `dotnet` with no program: the game only quits. A transpiler replaces that call: it starts the app again through macOS (`open -n`, which hands the app its own environment: the game's, without what the port settings set, and `KSA_PORT_WAIT`, the game's process id), and the new launcher waits until this game has quit. A process the game started itself would not survive: when the app was started from the Finder, macOS ends what a quitting app leaves running. The new game reads the port settings as any start does |
| HDR brightness | [`Hdr.cs`](Hdr.cs) | the driver shows the game's HDR output with its HDR Paper White (Display) at the display's SDR white and tells macOS its HDR Peak Brightness ([Presentation](../ksa-metal/README.md#from-vulkan-to-metal)); the game keeps paper white to itself and sends the peak only when it rebuilds the swapchain: a postfix on `GameSettings.ApplyTo(GlfwWindow)`, where the game hands its Display settings to its renderer when it starts and at every Apply, passes `HdrOutput.PaperWhiteNits` and `PeakNits` to the driver (`ksaMtlHdrNits`). Without it, the driver takes the game's defaults, 200 and 1000 nits, and the peak the swapchain was built with |
| Documents folder | [`Documents.cs`](Documents.cs) | `STARMAP_INSTANCE_PATH` moves the game's documents folder, as StarMap does, also without StarMap (the benchmark's runs) |
| Benchmark | [`BenchmarkRun.cs`](BenchmarkRun.cs) | a postfix on `Program.OnFrame` runs the benchmark's director ([`../benchmark/`](../benchmark/)): from the game's first frame when the game starts with `KSA_BENCH_*` set, or from the Mac Port tab. A run in the game that is open saves it (to disk and in memory), sets the world to a new game's through the game's own loading (`Universe.DeserializeSave`), and loads the saved game again at the end; the hooks then write the report with the report program |

## Settings

The hooks, KSA Metal and its shims read environment variables, which the loader sets from `port-settings.toml` when the
game starts ([`loader/PortEnvironment.cs`](loader/PortEnvironment.cs), with the table and file reading of
[`PortSettingsTable.cs`](PortSettingsTable.cs), which the hooks share); one set before the game starts wins over the
file, and the Mac Port tab shows its row as set outside. The loader sets each variable twice, for .NET, which keeps its
own copy of the environment, and with `setenv` for KSA Metal and the shims, which read theirs when the game first uses
them. It also sends the game's output to `console.log` and copies the file's template for a new player. The table
[`../launcher/port-settings.txt`](../launcher/port-settings.txt) lists every setting with its variable, its values and
its default, and the hooks read their settings against it ([`Settings.cs`](Settings.cs)): a variable that is unset, or
holds none of the setting's values, means the default (`console.log` says which). Those of the hooks:

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
| `KSA_SHADOW_SEARCH=1` | `shadow_search` |
| `KSA_MILKY_WAY_BAKE=1` | `milky_way_bake` |
| `KSA_TERRAIN_CACHE=0`, `KSA_OUTLINE_SKIP=0`, `KSA_OVERLAY_SKIP=0`, `KSA_HIDDEN_PARTS_SKIP=0`, `KSA_OCEAN_BLENDING=0`, `KSA_WORKER_PARKING=0`, `KSA_LOADING_PACE=0` | `terrain_cache`, `outline_skip`, `overlay_skip`, `hidden_parts_skip`, `ocean_blending`, `worker_parking`, `loading_pace`: leave out that patch |
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
