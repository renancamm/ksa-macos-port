# KSA Metal

KSA Metal implements the part of Vulkan that Kitten Space Agency uses, on top of Metal. The app ships it as
`libvulkan.1.dylib`, in the place of the Vulkan loader and a Vulkan driver, so nothing in the game changes.

The main README says what it is and lists its compatibility switches ([KSA Metal](../../README.md#ksa-metal)).
This file has the rest: how to build and test a change, how each Vulkan concept maps onto Metal, and what each
source file holds.

## Build

Building needs the Command Line Tools (`xcode-select --install`) and cmake (`brew install cmake`). From the
repository's folder:

```bash
./build.sh --install
```

That builds the port folder (`dist/ksa-macos-port/`) and puts its files into the installed app. For KSA Metal it runs
`scripts/build-spirv-cross.sh` and `scripts/build-spirv-tools.sh` (pinned in `scripts/pins.sh`, static, built once),
then `scripts/build-ksa-metal.sh` (`src/ksa-metal/` -> `deps/build/natives/libvulkan.1.dylib`). Objects in
`deps/build/ksa-metal/` are reused while their source, the headers and the compiler flags stay the same.
`KSA_MTL_OPT=-O0` changes the optimization level (default `-O2`).

## Test

Start the app's executable from Terminal with the debugging variables below, the same way as the switches in the
main README. For example, to save two screenshots and quit:

```bash
KSA_MTL_SHOTS=300,3000 KSA_MTL_SHOT_DIR=/tmp/ksa-shots KSA_MTL_EXIT_AFTER=3000 \
  ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

A useful loop for rendering bugs: reproduce the problem in a save, take shots before and after a change, and turn
on `MTL_SHADER_VALIDATION=1` and `KSA_MTL_DUMP_MSL` to see which shader misbehaves and what Metal source it became.
Both shader fixes in `src/port/lib/convert-shaders.sh` were found this way.

Vulkan validation layers can't be used, because KSA Metal takes the loader's place. `KSA_MTL_LOG=1` logs its
warnings and the first call of each Vulkan function, which shows what the game asks for, and `KSA_MTL_STRICT=1`
returns pipeline errors to the game instead of skipping the pipeline.

The debugging variables, used the same way:

| Variable | Effect |
|---|---|
| `KSA_MTL_LOG=<0-3>` | log detail: 0 errors only (default), 1 also warnings and the first call of each function, 2 also object creation, 3 also every command |
| `KSA_MTL_LOG_FILE=<file>` | write the log to a file instead of the terminal |
| `KSA_MTL_STRICT=1` | return pipeline errors to the game, instead of skipping the pipeline and continuing |
| `KSA_MTL_DUMP_MSL=<dir>` | write every translated shader, and the errors of those that fail to compile |
| `KSA_MTL_SHOTS=<n,...>` | save the presented image as `shot_<n>.png` at these points |
| `KSA_MTL_SHOT_UNIT=<unit>` | count shots in `presents` (default), or in `frames` or `seconds` from the first in-game frame |
| `KSA_MTL_SHOT_DIR=<dir>` | where shots go (default: the current folder) |
| `KSA_MTL_EXIT_AFTER=<n>` | quit after n presents |
| `KSA_MTL_EXIT_AFTER_SHOTS=1` | quit once the last shot is saved |
| `KSA_MTL_FRAMES=<file.csv>` | per frame: CPU frame time, GPU busy time, draws, dispatches and allocated memory |
| `KSA_MTL_GPUPROF=<file.csv>` | GPU time of every encoder, for 300 frames from present 2000 (`KSA_MTL_GPUPROF_FRAMES=<first>,<count>` changes that) |
| `KSA_MTL_LABELS=1` | name every encoder after its pipelines (render pass size, shader hashes), for Instruments, the Metal HUD and Xcode's frame capture |

Metal's own tools work too: `MTL_HUD_ENABLED=1` shows Apple's performance overlay, and `MTL_DEBUG_LAYER=1` and
`MTL_SHADER_VALIDATION=1` check API use and catch out-of-bounds memory access in shaders (slow).

To see what a change does across the game, run the [benchmark](../benchmark/README.md) before and after it
(`dist/ksa-macos-port/benchmark.sh`) and compare the two runs: `--fast` is the quick way, and `compare` checks the
screenshots as well as the numbers. Its mod talks to KSA Metal through the only functions it exports besides Vulkan's:
`ksaMtlFrameStats` (the latest presented frame: the `KSA_MTL_FRAMES` numbers, thermal state, shaders compiled, memory
footprint, drawable size, display interval), `ksaMtlScreenshot` (a PNG of the next one), `ksaMtlMarker` (a signpost
interval for each measured part, under Points of Interest in Instruments) and `ksaMtlCapture` (an Xcode GPU frame
capture of the next frames, with `MTL_CAPTURE_ENABLED=1`), in `swapchain.mm`, and `ksaMtlProfileFrames` (the GPU
profiler's frames) in `profile.mm`. The benchmark's `--profile`, `--trace`, `--capture` and `--hud` use them.

## From Vulkan to Metal

Vulkan and Metal are both explicit, low-level GPU APIs with similar ideas, which is what makes a thin translation
possible. These are the main pieces:

**Memory.** In Vulkan, a program allocates large blocks of GPU memory (`VkDeviceMemory`) and places buffers and
images inside them. KSA does this through Vulkan Memory Allocator. KSA Metal backs each block with a Metal heap
(`MTLHeap`) and one Metal buffer that spans the whole heap. A Vulkan buffer is then just an offset into that heap
buffer, with no Metal object of its own, and a Vulkan image is a Metal texture placed in the heap at its offset.
All heaps are in one residency set, so Metal keeps them resident for every command buffer.

**Shaders.** KSA compiles its GLSL shaders to SPIR-V, Vulkan's shader format. Metal only accepts its own shading
language (MSL). KSA Metal translates each shader with [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross)
when the game creates a pipeline, and Metal compiles the result. Translation and compilation take time, so:

- Pipelines compile on worker threads, and the game only waits for one when it first uses it.
- Translations are cached on disk (`~/Library/Caches/ksa-metal`).
- At startup, the shaders of the previous run are compiled in the background. Metal keeps compiled shaders in a
  system cache, which macOS clears when it updates; the background compile refills it.

**Descriptors.** Vulkan shaders find their resources (buffers, textures, samplers) through *descriptor sets*. Metal's
equivalent is an *argument buffer*: a block of memory holding GPU addresses and resource IDs. KSA Metal writes each
descriptor set directly as an argument buffer, where every binding owns consecutive 8-byte slots, and tells
SPIRV-Cross the same layout, so the translated shader reads the right slot. Shaders read argument buffers as
`constant` memory, which lets the compiler load the resource handles like uniforms; only the ray tracing kernels
read theirs as `device` memory, because their resources are passed on to functions.

**Commands.** Vulkan programs record commands into command buffers and submit them; Metal works the same way with
`MTLCommandBuffer`. KSA Metal encodes Metal commands immediately while the game records, so submitting is cheap.
Render passes become Metal render pass descriptors.

**Synchronization.** Vulkan programs say explicitly which work has to finish before other work starts (*barriers*).
Metal can express this with fences between *encoders*, the groups of commands inside a command buffer. KSA Metal
sorts work into a few stage classes (compute, transfer, vertex, fragment, acceleration structure builds) and turns
each barrier into fence waits between them. Work that doesn't depend on each other can overlap on the GPU, for
example the vertex work of one render pass with the fragment work of the previous one.

**Tessellation.** KSA tessellates terrain and ocean. Vulkan's tessellation pipeline has a vertex shader, a control
shader, a fixed-function tessellator and an evaluation shader. Metal's tessellator has no vertex or control stages,
so KSA Metal runs those two as compute kernels that write their output into scratch buffers, then draws the patches
with the evaluation shader as Metal's post-tessellation vertex function.

When the control shader passes its control points through unchanged, as KSA's terrain and ocean shaders do, the
vertex outputs don't go through memory at all. KSA Metal links the vertex shader into the evaluation shader at the
SPIR-V level, so the evaluation stage runs it for each of its control points on attributes Metal fetches through the
draw's index buffer. The control shader loses its copies, and the vertex and control kernels shrink to what the
tessellation factors need (for the terrain, 32 bytes per vertex instead of 128). Draws with 16-bit indices or
instances keep the evaluation stage reading the vertex kernel's output. In the benchmark this takes 5-15% off the GPU
time of every scene with terrain; `KSA_MTL_TESS_FUSE=0` turns it off.

**Ray tracing.** KSA uses ray queries for ground clutter placement, and ray tracing pipelines for lighting inside
the cockpit (IVA). Ray queries and acceleration structures map onto Metal's. Metal has no ray tracing pipelines, so
KSA Metal rewrites each one into a single compute kernel: every "trace ray" in the ray generation shader becomes a
ray query, and the hit and miss shaders become functions called from it. The rewrite works on SPIR-V, using
[SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools) to link the shaders together
([`rtspirv.cpp`](rtspirv.cpp)).

**Presentation.** The swapchain's images are KSA Metal's own textures; at present, the image is copied into the
window's `CAMetalLayer` drawable.

The complete mapping:

| Vulkan | Metal |
|---|---|
| Instance, physical device, device | one `MTLDevice` |
| Queues | family 0 does everything, family 1 only transfers (KSA streams textures on it); one `MTLCommandQueue` each |
| `VkDeviceMemory` | placement `MTLHeap` plus a whole-heap `MTLBuffer` (type 0 private, types 1-2 shared and mapped); all heaps in one `MTLResidencySet` |
| `VkBuffer` | an offset into the whole-heap buffer |
| `VkImage` | `MTLTexture` placed in the heap |
| Descriptor set | Metal 3 argument buffer: each binding owns consecutive 8-byte slots (GPU address or resource ID); a trailing slot points at a table of buffer sizes |
| Dynamic offsets | the set is copied into a per-command-buffer ring with the offsets added |
| Push constants | `set*Bytes` at buffer index 10 |
| Shader module, pipeline | SPIR-V -> MSL with SPIRV-Cross per pipeline, `newLibraryWithSource`, compiled on worker threads |
| Command buffer | `MTLCommandBuffer`, encoded while recording |
| Barriers | fences between encoders by stage class; command buffers start after and end before a per-queue chain fence |
| MSAA resolves | a pass that resolves signals an `MTLEvent`; the next compute or blit encoder that reads fragment output waits on it |
| Render pass, dynamic rendering | `MTLRenderPassDescriptor` |
| Multiview | one draw per view |
| Tessellation | vertex and control stages as compute kernels into scratch buffers, `drawPatches` with the evaluation shader as post-tessellation vertex function; with a passthrough control stage, the vertex shader linked into the evaluation shader and `drawIndexedPatches` with the index buffer as control point index buffer |
| Acceleration structures, ray query | Metal acceleration structures; instances converted on the CPU to indirect instance descriptors |
| Ray tracing pipeline | one compute kernel with ray queries (`rtspirv.cpp`) |
| Triangle fans | a shared (0, i, i+1) index buffer |
| Semaphores | in-order within a queue; `MTLSharedEvent` across queues |
| Fences | signalled from command buffer completion |
| Timestamps | GPU counter samples at encoder boundaries |
| Present wait | the drawables' presented handlers advance the present ID |
| Transient attachments | memoryless textures (tile memory only) |
| Swapchain | own textures, copied into the `CAMetalLayer` drawable at present |

Buffer indices per shader stage: 0-9 descriptor sets, 10 push constants, 11 draw index, 13 view mask, 16-21
tessellation stage buffers, 22 ray tracing launch size, 30 - n vertex buffer n.

## Source files

| File | What it holds |
|---|---|
| `kmtl.h` | shared declarations |
| `entry.mm` | the table behind `vkGetInstanceProcAddr` and `vkGetDeviceProcAddr` |
| `instance.mm` | instance, physical device queries, features and limits |
| `device.mm` | device, queues, memory, buffers, images, samplers, synchronization, submission |
| `format.mm` | Vulkan formats to Metal pixel and vertex formats |
| `descriptor.mm` | descriptor sets as argument buffers, pipeline layouts |
| `pipeline.mm` | shader translation, render passes, graphics and compute pipelines |
| `shadercache.mm` | the translation cache on disk and the background compile |
| `command.mm` | command buffers, every `vkCmd*`, encoder ordering |
| `internal.mm` | KSA Metal's own shaders: buffer fills, scaled blits, tessellation helpers |
| `accel.mm` | acceleration structures for ray queries |
| `rtpipeline.mm`, `rtspirv.cpp` | ray tracing pipelines rewritten as compute kernels |
| `tessspirv.cpp` | the SPIR-V rewrites behind fused tessellation |
| `spvmodule.h` | a SPIR-V module as a list of instructions, for those rewrites |
| `swapchain.mm` | swapchain, presentation, screenshots and frame log |
| `profile.mm` | the GPU profiler |

## Not implemented

KSA doesn't use these, so KSA Metal doesn't implement them: indexed triangle fans, non-indexed indirect tessellated
draws, secondary command buffers, and writable multisample storage images (not reported as supported, so the game
uses its fallback).
