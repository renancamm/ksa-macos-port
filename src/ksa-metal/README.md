# KSA Metal

KSA Metal implements the part of Vulkan that Kitten Space Agency uses, on top of Metal. The app ships it as
`libvulkan.1.dylib`, in the place of the Vulkan loader and a Vulkan driver, so nothing in the game changes.

The main README says what it is, in short ([KSA Metal](../../README.md#ksa-metal)). This file has the detail: its
switches, how to build and test a change, how each Vulkan concept maps onto Metal, and what each source file holds.

## Configuration

KSA Metal takes its settings from environment variables. To use one, start the app's executable from Terminal, where
its output stays visible:

```bash
KSA_MTL_TESSELLATION=0 ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

These switches turn a feature off or change a default, to work around a problem or to find out what causes it:

| Variable | Effect |
|---|---|
| `KSA_MTL_TESSELLATION=0` | hide tessellation from the game; it draws terrain and ocean without it |
| `KSA_MTL_TESS_FUSE=0` | tessellate without the fused path ([Tessellation](#from-vulkan-to-metal)): the vertex stage's outputs go through memory (slower; try it when terrain or ocean look wrong) |
| `KSA_MTL_RAYQUERY=0` | hide ray queries and ray tracing; the game places clutter without them, and ray tracing inside the cockpit (IVA) is unavailable |
| `KSA_MTL_RTPIPELINE=0` | hide only ray tracing pipelines (the cockpit's ray tracing) |
| `KSA_MTL_ASYNC_PIPELINES=0` | compile pipelines on the game's thread, when the game creates them |
| `KSA_MTL_CACHE=0` | don't use the shader cache on disk |
| `KSA_MTL_SYNC=strict` | run all GPU work strictly in order; slower, but rules out synchronization bugs |
| `KSA_MTL_SAFE_MATH=1` | compile shaders without fast math; rules out precision problems |
| `KSA_MTL_AB_CONSTANT=0` | shaders read their resources from device memory instead of constant memory; a bit slower, try it when a shader fails to compile |
| `KSA_MTL_BUDGET_MB=<n>` | the GPU memory budget reported to the game, which sizes its texture streaming from it (default: what Metal recommends for this Mac) |

## Build

Building needs the tools in the main README's [Building the port](../../README.md#building-the-port). From the
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

To see what a change does across the game, run the [benchmark](../benchmark/README.md) before and after it
(`dist/ksa-macos-port/benchmark.sh`): `--fast` is the quick way, and `--compare` checks the screenshots as well as the
numbers. To look at one place, play only that place (`--fast jupiter2`) or stay there with `--hold jupiter2`. The
game gets the variables below from the script, so `MTL_SHADER_VALIDATION=1 ./benchmark.sh --hold jupiter2` works too.

For a problem the tour doesn't reach, reproduce it in a save and start the app's executable from Terminal with the
debugging variables below, as with the switches above. For example, to save two screenshots and quit:

```bash
KSA_MTL_SHOTS=300,3000 KSA_MTL_SHOT_DIR=/tmp/ksa-shots KSA_MTL_EXIT_AFTER_SHOTS=1 \
  ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

Either way, `MTL_SHADER_VALIDATION=1` and `KSA_MTL_DUMP_MSL` show which shader misbehaves and what Metal source it
became. Both shader fixes in `src/port/lib/convert-shaders.sh` were found this way.

Vulkan validation layers can't be used, because KSA Metal takes the loader's place. `KSA_MTL_LOG=1` logs its
warnings and the first call of each Vulkan function, which shows what the game asks for, and `KSA_MTL_STRICT=1`
returns pipeline errors to the game instead of skipping the pipeline.

The debugging variables, used the same way:

| Variable | Effect |
|---|---|
| `KSA_MTL_LOG=<0-2>` | log detail: 0 errors only (default), 1 also warnings and the first call of each function, 2 also object creation |
| `KSA_MTL_LOG_FILE=<file>` | write the log to a file instead of the terminal |
| `KSA_MTL_STRICT=1` | return pipeline errors to the game, instead of skipping the pipeline and continuing |
| `KSA_MTL_DUMP_MSL=<dir>` | write every translated shader, and the errors of those that fail to compile |
| `KSA_MTL_DUMP_SPV=<dir>` | write every shader module the game creates as `<hash>.spv`, and the modules KSA Metal rewrites for tessellation and ray tracing |
| `KSA_MTL_OVERRIDES=<dir>` | use hand-written Metal source in place of a translated shader: `<dir>/<name>.metal`, named as in `KSA_MTL_DUMP_MSL`; each use keeps its own specialization values |
| `KSA_MTL_SPEC=<hash>:<id>=<value>,...` | replace a specialization constant of a shader module, whatever the game passes (e.g. fewer iterations of an expensive loop) |
| `KSA_MTL_CACHE_DIR=<dir>` | where the shader cache goes (default: `~/Library/Caches/ksa-metal`) |
| `KSA_MTL_SHOTS=<n,...>` | save the presented image as `shot_<n>.png` at these points |
| `KSA_MTL_SHOT_UNIT=<unit>` | count shots in `presents` (default), or in `frames` or `seconds` from the first in-game frame |
| `KSA_MTL_SHOT_DIR=<dir>` | where shots go (default: the current folder) |
| `KSA_MTL_EXIT_AFTER=<n>` | quit after n presents (`KSA_MTL_EXIT_AFTER_S=<s>`: s seconds after start) |
| `KSA_MTL_EXIT_AFTER_SHOTS=1` | quit once the last shot is saved |
| `KSA_MTL_FRAMES=<file.csv>` | per frame: CPU frame time, GPU busy time, draws, dispatches and allocated memory |
| `KSA_MTL_GPUPROF=<file.csv>` | GPU time of every encoder, for 300 frames from present 2000 (`KSA_MTL_GPUPROF_FRAMES=<first>,<count>` changes that) |
| `KSA_MTL_GPUPROF_SPLIT=1` | give every draw and dispatch its own encoder, so the profiler times each one (times grow: each draw stores and loads its attachments) |
| `KSA_MTL_LABELS=1` | name every encoder after its pipelines (render pass size, shader hashes), for Instruments, the Metal HUD and Xcode's frame capture |

Metal's own tools work too: `MTL_HUD_ENABLED=1` shows Apple's performance overlay, and `MTL_DEBUG_LAYER=1` and
`MTL_SHADER_VALIDATION=1` check API use and catch out-of-bounds memory access in shaders (slow).

The benchmark's mod talks to KSA Metal through the only functions it exports besides Vulkan's, in `swapchain.mm` and
`profile.mm`:

| Function | |
|---|---|
| `ksaMtlFrameStats` | the latest presented frame: the `KSA_MTL_FRAMES` numbers, thermal state, shaders compiled, memory footprint, drawable size, display interval |
| `ksaMtlScreenshot` | a PNG of the next frame |
| `ksaMtlMarker` | begins or ends a signpost interval, one per measured part, under Points of Interest in Instruments |
| `ksaMtlCapture`, `ksaMtlCapturing` | an Xcode GPU frame capture of the next frames (with `MTL_CAPTURE_ENABLED=1`), and whether it is still being written |
| `ksaMtlProfileFrames` | the GPU profiler (`KSA_MTL_GPUPROF`) on the next frames, under a label |

The benchmark's `--profile`, `--trace`, `--capture` and `--hud` use them.

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
time of the scenes with terrain; `KSA_MTL_TESS_FUSE=0` turns it off.

**Ray tracing.** KSA uses ray queries for ground clutter placement, and ray tracing pipelines for lighting inside
the cockpit (IVA). Ray queries and acceleration structures map onto Metal's. Metal has no ray tracing pipelines, so
KSA Metal rewrites each one into a single compute kernel: every "trace ray" in the ray generation shader becomes a
ray query, and the hit and miss shaders become functions called from it. The rewrite works on SPIR-V, using
[SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools) to link the shaders together
([`rtspirv.cpp`](rtspirv.cpp)).

**Presentation.** The swapchain's images are KSA Metal's own textures. When the game presents one, it is copied into
the window's `CAMetalLayer` drawable.

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

Buffer indices per shader stage: 0-9 descriptor sets, 10 push constants, 11 draw index, 12 buffer sizes, 13 view
mask, 14 dynamic offsets, 15 texture swizzles, 16-21 tessellation stage buffers, 22 ray tracing launch size, 30 - n
vertex buffer n.

## Source files

| File | What it holds |
|---|---|
| `kmtl.h` | shared declarations |
| `config.mm` | the `KSA_MTL_*` variables above, read once |
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
| `rtpipeline.mm`, `rtspirv.cpp`, `rtspirv.h` | ray tracing pipelines rewritten as compute kernels |
| `tessspirv.cpp`, `tessspirv.h` | the SPIR-V rewrites behind fused tessellation |
| `spvmodule.h` | a SPIR-V module as a list of instructions, for those rewrites |
| `swapchain.mm` | swapchain, presentation, screenshots and frame log |
| `profile.mm` | the GPU profiler |
| `CMakeLists.txt`, `exports.txt` | the build: sources, flags, and the symbols the library exports |

## Not implemented

KSA doesn't use these, so KSA Metal doesn't implement them: indexed triangle fans, non-indexed indirect tessellated
draws, secondary command buffers, and writable multisample storage images (not reported as supported, so the game
uses its fallback).
