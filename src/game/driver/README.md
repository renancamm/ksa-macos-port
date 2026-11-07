# KSA Metal: the driver

The driver is the part of KSA Metal that draws the game: it implements the part of Vulkan that Kitten Space Agency
uses, on top of Metal. The app ships it as `libvulkan.1.dylib`, in the place of the Vulkan loader and a Vulkan
driver, so nothing in the game changes. In this file, KSA Metal means the driver.

The main README says what it is, in short ([The driver](../../../README.md#the-driver)). This file has the detail: its
switches, how to build and test a change, how each Vulkan concept maps onto Metal, and what each source file holds.

## Configuration

KSA Metal takes its settings from environment variables, which the hooks' loader sets from the port settings ([The Mac
Port tab](../../../README.md#the-mac-port-tab)): each setting below is a line of `port-settings.toml`
([`../../shared/settings/port-settings.txt`](../../shared/settings/port-settings.txt)), the switches in the tab's
Optimizations, Memory and GPU Features sections, the debugging settings in the file's Troubleshooting, Diagnostics and
Experiments sections. A setting's variable is `KSA_MTL_` and its key in capitals, with 0 for off and 1 for on. A
variable can also be set where the app's executable starts, from Terminal, where its output stays visible; it then
wins over the file:

```bash
KSA_MTL_TESSELLATION=0 ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

These switches turn a feature off or change a default, to work around a problem or to find out what causes it:

| Setting | Effect |
|---|---|
| `tessellation` | off: hide tessellation from the game; it draws terrain and ocean without it |
| `ray_queries` | off: hide ray queries and ray tracing; the game places clutter without them, and ray tracing inside the cockpit (IVA) is unavailable |
| `ray_tracing` | off: hide only ray tracing pipelines (the cockpit's ray tracing) |
| `background_shader_compiling` | off: compile pipelines on the game's thread, when the game creates them |
| `shader_cache` | off: don't use the shader cache on disk |
| `strict_sync` | on: run all GPU work strictly in order; slower, but rules out synchronization bugs |
| `precise_math` | on: compile shaders without fast math; rules out precision problems |
| `shader_constant_memory` | off: shaders read their resources from device memory instead of constant memory; a bit slower, try it when a shader fails to compile |
| `in_place_shader_parameters` | off: helper functions get their own copy of each uniform struct they take, as SPIRV-Cross writes it ([Shaders](#from-vulkan-to-metal)); slower in the smoke raymarch, try it when a shader looks wrong |
| `skip_gpu_texture_compression` | off: textures read at scattered positions (those the game only uploads and samples, 3D images, square power-of-two storage images of 64 to 256 such as the ocean's waves) are lossless-compressed, as Metal does by default; about 2-4 % slower over terrain, up to 10 % over clouds and the sea, try it when the terrain, clouds, smoke or the sea look wrong |
| `iva_ray_search` | off: the cockpit's ray tracing finds each hit with a ray query, as SPIRV-Cross writes it, instead of Metal's intersector ([Ray tracing](#from-vulkan-to-metal)); slower in the cockpit, try it when the cockpit looks wrong |
| `compact_ray_tracing_memory` | on: each acceleration structure sits in the buffer the game made for it, instead of memory of its own while that buffer stays unused (the terrain's structures for clutter placement are about 170 MB each); off: try it when grass or the cockpit look wrong |
| `dedicated_large_buffers` | off: buffers of 4 MB or more share Vulkan Memory Allocator's blocks (up to 256 MB) like smaller ones, so memory the game frees stays in its block (the Planetary Tour: about 0.1 GB more at its peak, 0.4 GB more from leaving Earth until another world's terrain is built); try it when something looks wrong |
| `skip_shadows_on_unlit_sides` | off: the port's shader edits that skip shadows where there is no light ([Shaders](#from-vulkan-to-metal)) look them up everywhere, as the game does; slower, try it when a shadow looks wrong |
| `shared_shadow_samples` | off: every pixel filters the terrain's sun shadow with all its samples, as the game does, instead of sharing them with its 2x2 quad ([Shaders](#from-vulkan-to-metal)); slower, try it when a shadow on the ground looks wrong |
| `early_fragment_tests` | off: no second state that tests depth first for pipelines whose fragment shader stores to an image ([Shaders](#from-vulkan-to-metal)): the hooks' Skip Hidden Vessel Parts has nothing to switch to |
| `texture_budget_mb` | the GPU memory budget in MB reported to the game, which sizes its texture streaming from it (0, the default: what Metal recommends for this Mac) |
| `frame_interpolation` | on: frame interpolation ([MetalFX](#from-vulkan-to-metal)) |
| `hdr_output` | off: offer the game no HDR output ([Presentation](#from-vulkan-to-metal)): its HDR settings (Display) stay hidden |
| `vsync_pacing` | off: with Vsync, the game's wait for an earlier frame ends only when the display shows it ([Presentation](#from-vulkan-to-metal)); in a window, frames that take less than about 23 ms on the GPU then run slower |
| `steady_frame_rate` | on, with Vsync: each game frame shown at least two refreshes of the display the window is on after the one before, set again when the window moves to another display; at 40 fps or less with two drawables instead of three, one frame less latency (three with frame interpolation) ([Presentation](#from-vulkan-to-metal)) |

## Build

Building needs the tools in the main README's [Building the port](../../../README.md#building-the-port). From the
repository's folder:

```bash
./build.sh --install
```

That builds the port folder (`dist/ksa-macos-port/`) and puts its files into the installed app. KSA Metal is a target
of the CMake project: [`CMakeLists.txt`](CMakeLists.txt) in this folder lists its sources and flags, and
`cmake/dependencies.cmake` pins Vulkan-Headers, SPIRV-Cross and SPIRV-Tools; the last two are built once as static
libraries and linked in. The library goes to `deps/build/natives/libvulkan.1.dylib`, and a change recompiles only the
files it affects, including after a dependency's update. `KSA_MTL_OPT=-O0 ./build.sh` changes the optimization level
(default `-O2`). To build only the native libraries: `cmake --preset default && cmake --build --preset default`.

Editors find the compiler flags in `compile_commands.json` at the repository's root, which `build.sh` links to
CMake's.

## Test

Two tests run without the game ([`tests/`](tests/)): `build.sh` runs the smoke test, a fill and a copy on the GPU
through the driver alone, after every build. The shader corpus test translates and compiles again every shader the game
had the driver translate in a recorded run, and fails when a translation changed or doesn't compile: run it before and
after a change to the translation or the pipelines.

To see what a change does across the game, run the [benchmark](../benchmark/README.md) before and after it
(`dist/ksa-macos-port/benchmark.sh`): `check` is the quick way, the Mixed Test compared with a baseline, screenshots as
well as numbers; `measure` gives the numbers to judge it by. To look at one place, play only that place (`--fast
jupiter2`) or stay there with `investigate hold jupiter2`. The game gets the variables below from the script, so
`MTL_SHADER_VALIDATION=1 ./benchmark.sh investigate hold jupiter2` works too.

For a problem the tests don't reach, reproduce it in a save and start the app's executable from Terminal with the
debugging settings' variables below, as with the switches above. For example, to save two screenshots and quit:

```bash
KSA_MTL_SHOTS=300,3000 KSA_MTL_SHOT_DIR=/tmp/ksa-shots KSA_MTL_EXIT_AFTER_SHOTS=1 \
  ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

Either way, `MTL_SHADER_VALIDATION=1` and `dump_msl` show which shader misbehaves and what Metal source it
became. The ground clutter's shader fixes ([`../changes/fixes/`](../changes/fixes/)) were found this
way.

Vulkan validation layers can't be used, because KSA Metal takes the loader's place. `log` at 1 logs its warnings and
the first call of each Vulkan function, which shows what the game asks for, and `strict_pipelines` returns pipeline
errors to the game instead of skipping the pipeline.

The debugging settings, used the same way:

| Setting | Effect |
|---|---|
| `log` | 0-2, the log's detail: 0 errors only (default), 1 also warnings and the first call of each function, 2 also object creation |
| `log_file` | a file: write the log there instead of the terminal |
| `strict_pipelines` | on: return pipeline errors to the game, instead of skipping the pipeline and continuing (pipelines then compile on the game's thread, so every error can go back) |
| `dump_msl` | a folder: write every translated shader there, and the errors of those that fail to compile |
| `dump_spv` | a folder: write every shader module the game creates there as `<hash>.spv`, and the modules KSA Metal rewrites for tessellation and ray tracing |
| `shader_corpus` | a folder: record every shader translation's inputs and result there, once each, for the shader corpus test ([`tests/`](tests/)) |
| `shader_overrides` | a folder: use hand-written Metal source in place of a translated shader, `<folder>/<name>.metal`, named as `dump_msl` names them; each use keeps its own specialization values |
| `spec` | `<hash>:<id>=<value>,...`: replace a specialization constant of a shader module, whatever the game passes (e.g. fewer iterations of an expensive loop) |
| `cache_dir` | a folder: where the shader cache goes, and the KTX shim's textures (default: `~/Library/Caches/ksa-metal`) |
| `shots` | `<n>,...`: save the presented image as `shot_<n>.png` at these points |
| `shot_unit` | what `shots` counts: `presents` (default), or `frames` or `seconds` from the first in-game frame |
| `shot_dir` | a folder: where shots go (default: the current folder) |
| `exit_after` | quit after this many presents |
| `exit_after_s` | quit this many seconds after start |
| `exit_after_shots` | on: quit once the last shot is saved |
| `frames` | a CSV file: per frame, CPU frame time, GPU busy time, draws, dispatches and allocated memory |
| `fx_debug` | on: the upscaled image shows MetalFX's motion vectors instead (red and green: horizontal and vertical, 32 pixels = full; blue: the followed vessel) |
| `fx_shots` | a folder: now and then, a PNG of what MetalFX made, an interpolated frame with the frames around it |
| `latency` | a CSV file: per frame, when it began (when the game's wait for an earlier frame ended), was presented, was done on the GPU and was shown, how long it waited, and whether pacing ended the wait |
| `stalls` | milliseconds: log what blocks the game longer than that: waits for fences, drawables and submits, residency set commits (with what they added), command buffers that started late on the GPU, and allocations of 16 MB or more, each with the present it falls in |
| `gpu_profile` | a CSV file: the GPU time of every encoder, for 300 frames from present 2000 |
| `gpu_profile_frames` | `<first>,<count>`: other frames for `gpu_profile` |
| `gpu_profile_split` | on: give every draw and dispatch its own encoder, so the profiler times each one (times grow: each draw stores and loads its attachments) |
| `labels` | on: name every encoder after its pipelines (render pass size, shader hashes), for Instruments, the Metal HUD and Xcode's frame capture; on with `frame_capture` too |
| `frame_capture` | on: Xcode frame captures are allowed (it sets Apple's `MTL_CAPTURE_ENABLED`) |
| `gpu_errors` | on: a failed command buffer logs each encoder's state (completed, faulted, affected, pending); with `labels` the log names them |
| `watch` | a folder: every 31 presents, a look at a sample of the presented image: at 30 % pure white, 99.9 % pure black for a second, or 0.1 % of pure primary colours or green with no red or blue, it saves the image and a pass dump of the next frame there (at most 3, 10 s apart), `watch.txt` says when each fault began and ended, and `watch.csv` has every look's numbers. Only an image after a clean one counts |
| `watch_white` | percent: the white limit of `watch` (30); 0 fires on any image, to test it |
| `throttle` | `<hash>:<n>,...`: an experiment, a pipeline whose name holds that shader hash runs only every n-th present (0: never), to see what a shader costs and what it draws |
| `frame_cadence` | `<ms>`: an experiment, each frame shown at least that long after the one before (25: 40 fps at 120 Hz), so the frame rate holds one step; wins over `steady_frame_rate` |
| `drawable_count` | `2` or `3`: an experiment, how many images the window keeps to show; 2 lets fewer frames wait to be shown (latency; 3 with frame interpolation). `0`, the default: 2 at a cadence of 25 ms or more, else 3 |
| `late_frame_start` | `<ms>`: an experiment, with a cadence (in whole refresh intervals; not with frame interpolation) each frame starts as late as still keeps its slot on the display: the game's wait for an earlier frame grows by that much after each frame shown on time and is cut in half after one shown late, so about one frame in (wait / 2) / step misses its slot (lower latency at the same cadence) |

Metal's own tools work too: `MTL_HUD_ENABLED=1` shows Apple's performance overlay, and `MTL_DEBUG_LAYER=1` and
`MTL_SHADER_VALIDATION=1` check API use and catch out-of-bounds memory access in shaders (slow).

The benchmark's mod and the hooks talk to KSA Metal through the only functions it exports besides Vulkan's, in
`diagnostics/instruments.mm`, `diagnostics/drain.mm`, `diagnostics/profile.mm`, `diagnostics/passdump.mm`,
`pipelines/stages.mm` and `presentation/swapchain.mm`:

| Function | |
|---|---|
| `ksaMtlFrameStats` | the latest presented frame: the `frames` numbers, thermal state, shaders compiled, memory footprint, drawable size, display interval |
| `ksaMtlDrainOpen`, `ksaMtlDrain`, `ksaMtlDrainDropped`, `ksaMtlDrainClose` | the drain: once a reader opens it with the layout it knows, every present (the `ksaMtlFrameStats` numbers), when each frame began, was presented, finished on the GPU and was shown, each wait of at least the reader's threshold (the `stalls` scopes, residency commits) and each allocation or free of 16 MB or more, as records in a 1 MB ring the reader empties; a record that finds it full is counted and dropped, nothing waits |
| `ksaMtlScreenshot` | a PNG of the next frame |
| `ksaMtlMarker` | begins or ends a signpost interval, one per measured part, under Points of Interest in Instruments |
| `ksaMtlPassDump`, `ksaMtlPassDumping` | the next frame's colour attachments after each draw of a render pass (the first 64, then every 16th) and at its end, the images at the frame's end that it can read, as PNGs (NaN magenta, infinity cyan), and the first 64 KB of the buffers bound for each draw, with their numbers and heap places in `passes.txt`, which says why others are left out; that frame's passes don't overlap, so a clean dump doesn't rule out a missing barrier. The second says whether they are still being written |
| `ksaMtlGpuErrors` | how many command buffers failed; after a hang the GPU refuses the process's work, and pass dumps refuse to start |
| `ksaMtlCapture`, `ksaMtlCapturing` | an Xcode GPU frame capture of the next frames (with `MTL_CAPTURE_ENABLED=1`), and whether it is still being written |
| `ksaMtlProfileFrames` | the GPU profiler (`gpu_profile`) on the next frames, under a label |
| `ksaMtlEarlyFragmentTests` | whether draws use the pipelines' states that test depth first (below); the hooks' Skip Hidden Vessel Parts sets it at the start of a frame |
| `ksaMtlHdrNits` | the game's HDR Paper White and HDR Peak Brightness in nits, which HDR output maps to the display (below); the hooks pass them when the game applies its Display settings |

The benchmark's tools `profile`, `trace`, `capture` and `hud` use the first five.

## From Vulkan to Metal

Vulkan and Metal are both explicit, low-level GPU APIs with similar ideas, which is what makes a thin translation
possible. These are the main pieces:

**Memory.** In Vulkan, a program allocates large blocks of GPU memory (`VkDeviceMemory`) and places buffers and
images inside them. KSA does this through Vulkan Memory Allocator. KSA Metal backs each block with a Metal heap
(`MTLHeap`) and one Metal buffer that spans the whole heap. A Vulkan buffer is then just an offset into that heap
buffer, with no Metal object of its own, and a Vulkan image is a Metal texture placed in the heap at its offset.
Images of 64 MB or more and buffers of 4 MB or more ask for a block of their own, so memory the game frees goes back
to macOS. All heaps are in one residency set, so Metal keeps them resident for every command buffer.

**Shaders.** KSA compiles its GLSL shaders to SPIR-V, Vulkan's shader format. Metal only accepts its own shading
language (MSL). KSA Metal translates each shader with [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)
when the game creates a pipeline, and Metal compiles the result. Translation and compilation take time, so:

- Pipelines compile on worker threads, and the game only waits for one when it first uses it.
- Translations are cached on disk (`~/Library/Caches/ksa-metal`).
- At startup, the shaders of the previous run are compiled in the background. Metal keeps compiled shaders in a
  system cache, which macOS clears when it updates; the background compile refills it.

GLSL passes structs to functions by value, and SPIRV-Cross keeps that: a helper that takes one of the game's
parameter blocks gets a copy of it, field by field, at every call. The smoke and explosion raymarch does this for its
1.2 KB of parameters several times per step, and a copy whose arrays a helper indexes has to live in memory. Before
compiling, KSA Metal turns those copies into references to the block itself and lets the helpers take the block's
own type. A shader where that doesn't compile (a copy the shader writes to, a helper also called with a local
struct) is compiled as translated.

A fragment shader that stores to an image runs before the depth test, as Vulkan and Metal require for its stores, so
every hidden fragment of its draws is shaded and the GPU's hidden surface removal can't skip it. The vessels' shaders
store only for selected parts, into the mask the selection outline is drawn from. KSA Metal gives each pipeline whose
fragment shader stores only to that mask (and doesn't discard or write depth) a second state that tests depth first, and draws with it
while `ksaMtlEarlyFragmentTests` says so: the hooks do while no part is selected, when the stores never run, so the
image is the same. On an M5 at the game's own settings it takes the cockpit's GPU time from 23.9 ms to 21.2.

At port time, the port's program also edits the game's GLSL for speed
([`../changes/optimizations/`](../changes/optimizations/)), three times with the same
idea. The terrain's, the vessels' and the kitten's shaders multiply the sun's light by its soft shadows (32 shadow-map
taps a pixel) also where that light is already zero, on every surface turned away from the sun; the cockpit's ray
tracing likewise traces a shadow ray towards a light from surfaces it doesn't light. The grass and the buildings
multiply all their shadows with the planet's own, so at night they count for nothing, and the grass's soft shadows
also in a mountain's shadow. The terrain also looks up its own sun shadow, the clouds' and the trails' shadows where
its light is zero, and the ocean its cloud shadows at night; and where the terrain lies in the planet's own shadow
with no lamps around, its whole colour is zero, so it is drawn black without its textures; those take derivatives, so
the edit skips them only where a whole 2x2 quad of pixels is unlit. The godrays' march through the air, which the
clouds run too, looks up the terrain's, the trails' and the clouds' shadows at every step, also where no sunlight
reaches it. Each edit looks the shadow up only where there is light (the other shadows per 2x2 quad), with the same
image. On an M5 at the game's own settings the soft shadows' edit takes the cockpit's GPU time from 44 ms to 24, and
the grass's edit takes a view of grass at night from 33.5 ms to 28; with IVA Ray Tracing on, the shadow rays' edit
takes the cockpit from 36 ms to 34.5; the terrain's, in a view across the terminator, from 22.9 ms to 22.0, the
ocean's, at sea at night, from 20.5 ms to 19.9, and a view of the night side from orbit from 11.9 ms to 10.7; the
godrays', at sea at night, from 19.9 ms to 18.8; the grass's and the buildings' other shadows at night, a view of
grass from 26.5 ms to 24.9 and one of the launch pad from 23.3 ms to 22.4. All of them test a specialization constant
the script declares, `PORT_SKIP_UNLIT_SHADOWS` (id 900, true by default), which the driver sets to false in every
shader with `skip_shadows_on_unlit_sides` off: the game's code again, without porting the game anew.

The terrain's sun shadow map is filtered with 16 to 32 samples (PCSS) or a PCF kernel for every pixel of the terrain
and the grass, though its texels are metres wide. With the edits' shared samples, the pixels of a 2x2 quad share them:
each takes every fourth and the quad sums (copies of the game's filters, held to the game's by a check), a 2-pixel blur
of a shadow metres wide. On an M5 near the ground (the Tour's earth6, two games a side) the game's own settings take
1.1 ms less of 32.8, the Quality preset 1.1 of 29.8, Balanced 0.5 of 20.6; the Mixed Test's shots are the same, and
still shots of the Tour's Earth move as between two runs of the game's own. The
constant is `PORT_SHARED_SHADOW_TAPS` (id 901), false with `shared_shadow_samples` off.

Two more edits skip work that changes nothing, with the same image. The clouds' noise blends three lookups of a 3D
texture at every sample, weighted by where the sample lies in a triangle grid, and near a triangle's edges and corners
one or two of the weights are zero: the first edit skips those lookups. On an M5 at the game's own settings it takes
about 5 % off views over the sea and of the rocket's liftoff. The second skips that noise deep inside a cloud, where
the step that carves the cloud's edges with it gives its largest value whatever the noise is; the samples the clouds'
lighting takes towards the sun often lie there. It takes another 4-7 % off the same views.

**Descriptors.** Vulkan shaders find their resources (buffers, textures, samplers) through *descriptor sets*. Metal's
equivalent is an *argument buffer*: a block of memory holding GPU addresses and resource IDs. KSA Metal writes each
descriptor set directly as an argument buffer, where every binding owns consecutive 8-byte slots, and tells
SPIRV-Cross the same layout, so the translated shader reads the right slot. Shaders read argument buffers as
`constant` memory, which lets the compiler load the resource handles like uniforms; only the ray tracing kernels
read theirs as `device` memory, because their resources are passed on to functions.

A descriptor can outlive its buffer. Destroying a buffer moves the descriptors left on it to 1 MiB of zeros with size
0, and binding such a set writes one line to the log (`descriptor set 1 bound with a destroyed buffer`): a shader that
reads it by mistake reads zeros, not what the allocator has put in that memory since.

**Commands.** Vulkan programs record commands into command buffers and submit them; Metal works the same way with
`MTLCommandBuffer`. KSA Metal encodes Metal commands immediately while the game records, so submitting is cheap.
Render passes become Metal render pass descriptors.

**Synchronization.** Vulkan programs say explicitly which work has to finish before other work starts (*barriers*).
Metal can express this with fences between *encoders*, the groups of commands inside a command buffer. KSA Metal
sorts work into a few stage classes (compute, transfer, vertex, fragment, acceleration structure builds) and turns
each barrier into fence waits between them. Work that doesn't depend on each other can overlap on the GPU, for
example the vertex work of one render pass with the fragment work of the previous one.

**Tessellation.** KSA tessellates its terrain in a compute pass of its own; its ocean uses Vulkan's tessellation
pipeline. That pipeline has a vertex shader, a control shader, a fixed-function tessellator and an evaluation shader.
Metal's tessellator has no vertex or control stages, so KSA Metal runs those two as compute kernels that write their
output into scratch buffers, then draws the patches with the evaluation shader as Metal's post-tessellation vertex
function.

When the control shader passes its control points through unchanged, as KSA's ocean shaders do, KSA Metal strips
the copies from it at the SPIR-V level, and the evaluation stage reads the vertex kernel's output in their place.

**Ray tracing.** KSA uses ray queries for ground clutter placement, and ray tracing pipelines for lighting inside
the cockpit (IVA). Ray queries and acceleration structures map onto Metal's; each structure is placed in the heap of
the buffer the game made for it, as Vulkan places it, so it takes no memory of its own. Metal has no ray tracing
pipelines, so KSA Metal rewrites each one into a single compute kernel: every "trace ray" in the ray generation shader
becomes a ray query, and the hit and miss shaders become functions called from it. The rewrite works on SPIR-V, using
[SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools) to link the shaders together
([`rtspirv.cpp`](shaders/rtspirv.cpp)).

KSA's instances are opaque, so each of those ray queries stops only at the closest hit (or, for a shadow ray, at
any hit). Before compiling, KSA Metal turns each of them into one call of Metal's *intersector*, which searches the
acceleration structure faster, with the same image. A kernel where that doesn't compile is compiled as translated.

**Presentation.** The swapchain's images are KSA Metal's own textures. When the game presents one, it is copied into
the window's `CAMetalLayer` drawable. With Vsync, KSA waits for the frame before last to be presented before it
reads input. In a window, macOS shows a frame about 21 ms after the GPU finishes it, so waiting for the display
would leave the GPU idle whenever a frame takes less than that. KSA Metal also ends the wait once that frame is done
on the GPU, the one before it is on screen, and the GPU is about to need the next frame (predicted from the last
frames' GPU and CPU times). Frames then start when the GPU can take them, without queueing up. With
`steady_frame_rate`, each drawable is presented with a minimum duration (two refreshes, one with frame
interpolation's frame in between), so the frame rate holds one step of the display's refresh instead of moving between
them.

**HDR.** On a display with EDR (brighter than SDR white), the surface also offers HDR10: `A2B10G10R10` in PQ with Rec.
2020 primaries, which holds the game's absolute nits, and the game then shows its HDR settings (Display). Not scRGB:
the game's 1.0 there is 80 nits, macOS's is SDR white. With an HDR10 swapchain the layer takes the PQ colour space,
EDR, and HDR10 metadata whose optical output scale is the game's HDR Paper White: macOS shows that brightness at SDR
white, as bright as other windows, and fits the highlights up to HDR Peak Brightness into what the display has above
it. Paper White then no longer changes how bright the UI is, only how far highlights reach above it. The hooks pass
both in at every Apply ([`Hdr.cs`](../changes/fixes/Hdr.cs)); `vkSetHdrMetadataEXT` also sets the peak. Screenshots of
HDR frames are SDR PNGs, paper white at white and brighter highlights clipped. In the benchmark's places, HDR output
takes about 1-5 % more GPU time (M5, one run each).

**MetalFX.** For the port settings, the patched game ([KSA Metal Hooks](../hooks/)) calls entry points that are KSA
Metal's own, exported next to Vulkan's: `vkCmdUpscaleKSA` scales the main view's image into one of the swapchain's
size (the display's pixels) before the final composite (past the temporal upscaler's largest scale, 3x on an M5, it
upscales as far as it can and the spatial upscaler does the rest), `vkCmdUpscaleSnapshotKSA` copies the scene before its translucent effects, and `vkCmdMarkUiKSA`
copies it again after the composite, before the UI. For MetalFX's temporal upscaler, KSA Metal makes the motion
vectors in a kernel of its own, from the depth buffer and the reprojections the game passes, and marks what the
translucent effects changed as reactive. Between the encoders MetalFX adds and the game's work it puts full event
barriers. Frame interpolation presents a frame MetalFX makes from the last two scenes, their motion and depth, with this
frame's UI on top, before each real one.

The complete mapping:

| Vulkan | Metal |
|---|---|
| Instance, physical device, device | one `MTLDevice` |
| Queues | family 0 does everything, family 1 only transfers (KSA streams textures on it); one `MTLCommandQueue` each |
| `VkDeviceMemory` | placement `MTLHeap` plus a whole-heap `MTLBuffer` (type 0 private, types 1-2 shared and mapped); all heaps in one `MTLResidencySet` |
| `VkBuffer` | an offset into the whole-heap buffer |
| `VkImage` | `MTLTexture` placed in the heap; those read at scattered positions (upload-only, 3D, square power-of-two storage up to 256) without lossless compression (`skip_gpu_texture_compression`) |
| Descriptor set | Metal 3 argument buffer: each binding owns consecutive 8-byte slots (GPU address or resource ID); a trailing slot points at a table of buffer sizes |
| Dynamic offsets | the set is copied into a per-command-buffer ring with the offsets added |
| Push constants | `set*Bytes` at buffer index 10 |
| Shader module, pipeline | SPIR-V -> MSL with SPIRV-Cross per pipeline, `newLibraryWithSource`, compiled on worker threads |
| Command buffer | `MTLCommandBuffer`, encoded while recording |
| Barriers | fences between encoders by stage class; command buffers start after and end before a per-queue chain fence |
| MSAA resolves | a pass that resolves signals an `MTLEvent`; the next compute or blit encoder that reads fragment output waits on it |
| Render pass, dynamic rendering | `MTLRenderPassDescriptor` |
| Multiview | one draw per view |
| Tessellation | vertex and control stages as compute kernels into scratch buffers, `drawPatches` with the evaluation shader as post-tessellation vertex function; a passthrough control stage loses its copies, and the evaluation stage reads the vertex kernel's output |
| Acceleration structures, ray query | Metal acceleration structures placed in their buffer's heap (`compact_ray_tracing_memory`; private heaps only, else standalone); instances converted on the CPU to indirect instance descriptors |
| Ray tracing pipeline | one compute kernel with ray queries (`rtspirv.cpp`) |
| Triangle fans | a shared (0, i, i+1) index buffer |
| Semaphores | in-order within a queue; `MTLSharedEvent` across queues |
| Fences | signalled from command buffer completion |
| Timestamps | GPU counter samples at encoder boundaries |
| Present wait | the drawables' presented handlers advance the present ID; a wait also ends when the GPU is about to need the next frame |
| Transient attachments | memoryless textures (tile memory only) |
| Swapchain | own textures at the display's pixels, copied into the `CAMetalLayer` drawable at present |
| HDR10 colour space, `vkSetHdrMetadataEXT` | the layer in PQ with EDR and `CAEDRMetadata`, the game's paper white at SDR white |
| `vkCmdUpscaleKSA`, `vkCmdUpscaleSnapshotKSA`, `vkCmdMarkUiKSA`, `vkGetUpscaleMaxScaleKSA` | KSA Metal's own: MetalFX for the hooks |

Buffer indices per shader stage: 0-9 descriptor sets, 10 push constants, 11 draw index, 12 buffer sizes, 13 view
mask, 14 dynamic offsets, 15 texture swizzles, 16-21 tessellation stage buffers, 22 ray tracing launch size, 30 - n
vertex buffer n.

## Source files

The driver is in folders by layer, each with a header that `kmtl.h` includes: a file includes `kmtl.h` and sees every
layer's types and the functions the layers call each other by (named `kmtl*`). A function only the files of one folder
share is in that folder's header too, under a heading that says so (`stages.h` for the pipelines).

| Folder, file | What it holds |
|---|---|
| `kmtl.h` | the core: logging, the configuration, buffer indices, the dispatchable objects (instance, physical device, queue, device), shared helpers; then the layers' headers |
| `setup/config.mm` | the settings above, read once from their variables (their names and defaults from `port-settings.h`, which the build makes from the settings table), and the log |
| `setup/entry.mm` | the table behind `vkGetInstanceProcAddr` and `vkGetDeviceProcAddr`, `entries.inc`, which the build makes from Vulkan's registry and the driver's sources ([`../../build-tools/entries-generator/`](../../build-tools/entries-generator/)) |
| `setup/aliases.txt` | the aliases the entry table answers for besides those the registry brings ([`../../build-tools/entries-generator/`](../../build-tools/entries-generator/)) |
| `setup/instance.mm` | instance, physical device queries, features and limits, the extensions advertised |
| `setup/device.mm` | device and queues, residency, fences and semaphores, submission |
| `resources/memory.mm` | device memory, buffers, buffer views |
| `resources/images.mm` | images, image views, samplers |
| `resources/queries.mm` | query pools, pipeline caches, debug names |
| `resources/format.mm` | Vulkan formats to Metal pixel and vertex formats |
| `resources/descriptor.mm` | descriptor sets as argument buffers, pipeline layouts |
| `resources/accel.mm` | acceleration structures for ray queries |
| `shaders/translate.mm`, `translate.h` | a shader stage's translation from SPIR-V to MSL |
| `shaders/shadercache.mm` | the translation cache on disk and the background compile |
| `shaders/corpus.mm` | the shader corpus (`shader_corpus`): translations recorded for the tests |
| `shaders/tessspirv.cpp`, `rtspirv.cpp`, `spvmodule.h` | the SPIR-V rewrites behind passthrough tessellation and ray tracing as compute kernels; a SPIR-V module as a list of instructions, for them |
| `pipelines/stages.mm`, `stages.h` | shader modules, MSL compilation, a stage made ready: translated or from the cache, overridden, compiled |
| `pipelines/pipelines.mm` | render passes, framebuffers, graphics and compute pipelines |
| `pipelines/rtpipeline.mm` | ray tracing pipelines rewritten as compute kernels |
| `commands/encoders.mm` | encoder ordering, the upload ring, the encoders, command pools and buffers |
| `commands/state.mm` | the state set between draws, and its flush before a draw or dispatch |
| `commands/draws.mm` | draws and dispatches, tessellated draws |
| `commands/passes.mm` | barriers, render passes and dynamic rendering |
| `commands/transfers.mm` | copies, blits, fills, clears, updates, queries, debug labels |
| `commands/internal.mm` | KSA Metal's own shaders: buffer fills, scaled blits, tessellation helpers |
| `presentation/swapchain.mm` | swapchain and presentation: pacing, HDR, frame interpolation at present, latency |
| `presentation/upscale.mm` | MetalFX: upscaling, motion vectors, frame interpolation |
| `diagnostics/instruments.mm` | what is measured at each present: frame log, screenshots, exits, the benchmark's interface, frame captures, signposts |
| `diagnostics/drain.mm` | the benchmark's drain: what is measured, as records for its run's store |
| `diagnostics/passdump.mm` | one frame's images, pass by pass (`ksaMtlPassDump`) |
| `diagnostics/profile.mm` | the GPU profiler |
| `tests/` | the tests that run without the game |
| `CMakeLists.txt`, `exports.txt` | the build: sources, flags, and the symbols the library exports |

## Not implemented

KSA doesn't use these, so KSA Metal doesn't implement them: indexed triangle fans, non-indexed indirect tessellated
draws, secondary command buffers, and writable multisample storage images (not reported as supported, so the game
uses its fallback).
