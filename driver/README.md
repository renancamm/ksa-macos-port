# KSA Metal driver (proof of concept)

A Vulkan-shaped Metal driver that implements just the Vulkan surface Kitten Space Agency uses. It is built as
`libvulkan.1.dylib` and replaces both the Khronos loader and MoltenVK: Brutal (through `libVulkanEx`), VMA and
GLFW all resolve their Vulkan symbols from it. Nothing in the game changes.

Status (proof of concept): the game runs on it with every graphics setting the player uses: terrain tessellation,
ground clutter with shadows, ray-query grass placement, clouds, MSAA and Cmaa2. The reference scenes match
MoltenVK's within its own run-to-run noise except for small cloud-edge differences; frame times and memory are on
par with MoltenVK (better at ground level with the full settings). Not implemented: timestamps (KSA creates query
pools but never uses them), ray tracing pipelines (IVA, off on macOS), sampler LOD bias, indexed triangle fans,
non-indexed indirect tessellated draws, secondary command buffers.

## Build and run

```sh
driver/build.sh                      # -> deps/ksamtl/libvulkan.1.dylib (fetches and builds pinned SPIRV-Cross)
driver/launch.sh                     # play: the installed app on this driver, with your own settings
driver/run.sh smoke 3000 300,3000    # test run: exit after 3000 presents, PNGs at 300 and 3000
```

To use it by hand, put `deps/ksamtl` first in `DYLD_LIBRARY_PATH` when starting the game. Needs Vulkan headers
(`brew install vulkan-headers`), cmake and ninja.

## Design

| Vulkan | Metal |
|---|---|
| Instance, physical device, device, queue | one `MTLDevice`, one `MTLCommandQueue`, one queue family |
| `VkDeviceMemory` | placement `MTLHeap` + a whole-heap `MTLBuffer` (type 0 private, types 1-2 shared and mapped); all heaps in one `MTLResidencySet` on the queue |
| `VkBuffer` | (whole-heap buffer, offset); no Metal object per buffer |
| `VkImage` | `MTLTexture` placed in the heap at the bound offset |
| Descriptor set | Metal 3 argument buffer written directly: each binding owns consecutive 8-byte slots (GPU address or resource ID); a trailing slot points at a table of buffer sizes |
| Dynamic offsets | the set is copied into a per-command-buffer ring with the offsets added |
| Push constants | `set*Bytes` at buffer index 10 |
| Shaders | SPIR-V -> MSL with SPIRV-Cross per pipeline (padded argument buffers, a binding map from the pipeline layout), `newLibraryWithSource` |
| Command buffers | encoded straight into an `MTLCommandBuffer` while recording |
| Barriers | every encoder waits on and updates one queue-wide `MTLFence`, so encoders run in order; barriers inside compute and render encoders become memory barriers |
| Render passes, dynamic rendering | `MTLRenderPassDescriptor` |
| Multiview | one draw per view, the view index passed in SPIRV-Cross's view-mask buffer |
| Tessellation | the render pass is suspended (passes start with `MTLStoreActionUnknown`, real store actions applied at the end); the vertex shader runs as a compute kernel with stage-in vertex fetch, the control shader as a multi-patch kernel, both into scratch buffers; the pass resumes and `drawPatches` feeds the evaluation shader as Metal's post-tessellation vertex function. Indirect draws get stage-in region, dispatch sizes and patch count from a one-thread prep kernel, which also records the largest index count requested so the scratch cap grows to it; one scratch buffer is shared by all tessellated draws |
| Acceleration structures, ray query | Metal acceleration structures; instances converted on the CPU at record time to indirect instance descriptors; device addresses mapped back to buffers |
| Triangle fans | a shared (0, i, i+1) index buffer |
| Swapchain | own textures, blitted into the `CAMetalLayer` drawable at present |
| Semaphores | no-ops (one in-order queue); fences signal from command buffer completion |

Buffer indices per stage: 0-9 descriptor sets, 10 push constants, 11 draw index, 13 view mask, 16-21 tessellation
stage buffers, 30 - n vertex buffer binding n.

## Instruments

Vulkan layers cannot run on this driver (it is the loader), so it carries its own instruments:

| Variable | Effect |
|---|---|
| `KSA_MTL_LOG=0..3` (`KSA_MTL_LOG_FILE`) | errors; + first call of each entry point; + object creation; + more |
| `KSA_MTL_FRAMES=<csv>` | per-present CPU interval and work counts |
| `KSA_MTL_SHOTS=<n,...>`, `KSA_MTL_SHOT_DIR` | PNG of the presented image at these presents |
| `KSA_MTL_EXIT_AFTER=<n>` | exit after n presents |
| `KSA_MTL_DUMP_MSL=<dir>` | every generated MSL file and compile error |
| `KSA_MTL_BUDGET_MB` | memory budget reported through `VK_EXT_memory_budget` |
| `KSA_MTL_STRICT=1` | return pipeline errors to the game instead of a null pipeline |
| `KSA_MTL_TESSELLATION=0`, `KSA_MTL_RAYQUERY=0` | hide the feature (the game takes its fallback) |
| `KSA_MTL_TESS_MAX_INDICES` | initial per-draw cap of indirect tessellated indices (scratch sizing, default 524288); it grows to the largest draw the GPU reports |
| `KSA_MTL_TESS_GROW=0` | keep that cap fixed (larger draws lose their last patches: the striped terrain of oblique views) |
| `KSA_MTL_ZERO_IMAGES=0` | do not clear new color images (MoltenVK-like zeroed allocations) |
| `KSA_MTL_SERIAL=1` | end compute encoders at every barrier and dispatch (synchronization debugging) |
| `KSA_MTL_SAFE_MATH=1` | compile MSL without fast math |
| `KSA_FRAMECLOCK_SHOTS`, `_SHOT_UNIT`, `_EXIT_AFTER_SHOTS`, `_EXIT_S` | PNGs at in-game seconds (default), in-game frames or presents, as `<KSA_FRAMECLOCK_FILE>.shot_<n>.png`; exit after the last shot or after a time |
