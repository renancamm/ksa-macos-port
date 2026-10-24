# Game patches

The game patches change a few parts of KSA's C# code in memory when the game starts, with
[Harmony](https://github.com/pardeike/Harmony). `KSA.dll` stays as it is on disk, and the repository holds only the
patches' own code.

The main README says what each patch does, in short ([Game patches](../../README.md#game-patches)). This file has the
detail: how they load, the rule every patch follows, the settings they read, and how to build and test a change.

## How they load

The launcher names `KsaPatchesLoader.dll` in `DOTNET_STARTUP_HOOKS`, so .NET runs it before the program it starts:
KSA itself, or StarMap, which loads KSA into a load context of its own. The loader ([`loader/Loader.cs`](loader/Loader.cs))
references nothing of the game. It waits until `KSA.dll` is loaded, then loads `KsaPatches.dll` into the same load
context and applies it. It loads no Harmony itself: under StarMap, the patches use the copy StarMap has already
loaded; without StarMap, they find the app's copy next to them, in `Contents/Resources/GamePatches/`.

## The rule every patch follows

A patch finds its targets by name and the code it changes by pattern: an instruction it expects exactly once, or the
call that follows a field it knows. When something isn't where it expects it, typically after a KSA update, the
patch throws `Skip`. Everything that patch had changed is undone, and `console.log` says so:

```text
[patches] terrain cache: on
[patches] outline skip: on
[patches] cloud shadows: normal
[patches] crew portraits: on
[patches] render scale: skipped, the game's code changed (the sun's radius not found)
```

The game then runs as it would without that patch. Nothing here changes how the game saves or what it simulates;
the patches only change what is drawn and how much work drawing it takes.

## The patches

| Patch | File | How |
|---|---|---|
| Terrain cache | [`TerrainCache.cs`](patches/TerrainCache.cs) | a prefix on `PlanetRenderer.GenerateMeshData` compares the inputs of the terrain's height passes (body, mesh level of detail, snapped texture rotation, height buffer) with those the frame slot's buffer was last computed for; a transpiler routes the two dispatches of those passes through a check that skips them when nothing changed. They run every 2 s anyway, for the streamed heightmap |
| Outline skip | [`OutlineSkip.cs`](patches/OutlineSkip.cs) | `Part.Selected`'s setter counts the selected parts; `PartSelectedRenderer.Run` returns early while there are none (only selected parts mark the image it outlines) |
| Cloud shadows | [`CloudShadows.cs`](patches/CloudShadows.cs) | with `cloud_shadows = "slow"`: the slices `CloudShadowVolume.UpdateVolumeDensity` refreshes per frame, 24 and 8, become 6 and 2 |
| Crew portraits | [`CrewPortraits.cs`](patches/CrewPortraits.cs) | the faces in the crew panel are views of their own, drawn every frame while the vessel has crew; after `CrewPortraitPanel.Update`, their views are hidden for the frame unless the panel was drawn in the previous one (the panel keeps their last image). With `KSA_PORTRAIT_FPS`, a drawn panel is refreshed at most that often |
| Render scale | [`RenderScale.cs`](patches/RenderScale.cs), [`Upscale.cs`](patches/Upscale.cs) | the main view renders at `render_scale` of the window through `Renderer.ExtentOverride` (set after every swapchain rebuild, unless a screenshot set its own), and its composite stretches it back. The cameras keep the window's size; the camera data, the light clustering and the sun's radius use the render size, and the camera data's spare int carries the window's height for the shader edits `convert-shaders.sh` makes. With MetalFX: a sub-pixel jitter in the projection, reprojection matrices for the scene and the followed vessel (in double), `vkCmdUpscaleKSA` before the composite, which then reads the upscaled image, and a copy of the scene before the exhausts for MetalFX's reactive mask. With frame interpolation, `vkCmdMarkUiKSA` after the composite, before the UI |

The MetalFX entry points are KSA Metal's own ([`../ksa-metal/upscale.mm`](../ksa-metal/upscale.mm)).

## Settings

The patches read environment variables, which the launcher sets from `mac-settings.toml`
([Mac settings](../../README.md#mac-settings)); one set before the game starts wins over the file:

| Variable | Setting |
|---|---|
| `KSA_RENDER_SCALE=<0.25..1>` | `render_scale` (the file allows 0.5 to 1.0) |
| `KSA_UPSCALER=metalfx\|spatial\|none` | `upscaler` |
| `KSA_CLOUD_SHADOWS=normal\|slow` | `cloud_shadows` |
| `KSA_MTL_FX_INTERPOLATE=1` | `frame_interpolation` (KSA Metal reads it too) |
| `KSA_PORTRAIT_FPS=<n>` | none yet: crew portraits refreshed at most n times a second |

For finding out which patch causes a problem:

| Variable | Effect |
|---|---|
| `KSA_PATCHES=0` | the launcher doesn't load the patches at all |
| `KSA_PATCHES_OFF=<key>,...` | leaves out these patches: `terrain_cache`, `outline_skip`, `cloud_shadows`, `crew_portraits`, `render_scale` |

## Build and test

`./build.sh` builds both assemblies against the KSA it is given, like the benchmark mod
([`scripts/build-game-patches.sh`](../../scripts/build-game-patches.sh)): `KSA.dll` is a reference only. Harmony
comes from the `Lib.Harmony` package on nuget.org, pinned in [`cmake/dependencies.cmake`](../../cmake/dependencies.cmake).

The benchmark is the test. `--mac` changes a Mac setting for one run, and the report lists the Mac settings and
which patches were on:

```bash
KSA_PATCHES=0 ./benchmark.sh --fast                       # without the patches
./benchmark.sh --fast --compare <that run>                 # with them: faster, the same screenshots
./benchmark.sh --fast --mac render_scale=0.5 --set antialiasing=None
```

To see the game's code a patch works on, Harmony's `PatchProcessor.GetOriginalInstructions` lists a method's IL.
