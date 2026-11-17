// The driver's own API: every function the library exports besides Vulkan's (exports.txt), and the structs they pass,
// named by kmtl.h's rule. Each group says the file that defines it and, after "C#:", the files that import it (the
// benchmark's command line, Run.cs, looks some up by name in the library instead). A struct C# mirrors field for field
// has its size and its fields' places checked below it, as C# lays out its mirror (LayoutKind.Sequential), so that a
// field added on one side only fails the build. Included by kmtl.h.
#pragma once
#include <cstddef>
#include <cstdint>

// ---------------------------------------------------------------------------------------------------------------
// The benchmark's instruments (diagnostics/instruments.mm). C#: src/shared/DriverStats.cs; ksaMtlFrameStats and
// ksaMtlCapture also src/player/benchmark-cli/Run.cs

// A present's numbers, as the benchmark reads them (ksaMtlFrameStats, the drain's present records). The caller passes the
// size of the struct it knows and gets back how much was filled; the benchmark's director takes the stats only when that
// is all of its struct, so a director and a driver from different builds don't misread them. C#'s mirror: FrameStats
struct KsaMtlFrameStats {
    uint64_t present;
    double cpuMs, gpuMs;
    uint32_t draws, dispatches, passes, submits;
    int64_t allocatedMb;
    // NSProcessInfo.thermalState (0 nominal, 1 fair, 2 serious, 3 critical), MSL compiled (not from the cache) and
    // pipeline states made since the start, the presented image's size in pixels
    int32_t thermalState;
    uint32_t shadersCompiled, pipelinesCreated, drawableWidth, drawableHeight, reserved;
    // The process's memory footprint (what Activity Monitor shows), the time between the last two images the display
    // actually showed (0 until known)
    int64_t footprintMb;
    double displayMs;
};
static_assert(sizeof(KsaMtlFrameStats) == 88 && offsetof(KsaMtlFrameStats, allocatedMb) == 40 &&
              offsetof(KsaMtlFrameStats, footprintMb) == 72, "C#'s FrameStats has this layout");

// The latest present's numbers (the KSA_MTL_FRAMES columns): the bytes written, at most size
KMTL_EXPORT uint32_t ksaMtlFrameStats(KsaMtlFrameStats* out, uint32_t size);
// A PNG of the next presented image
KMTL_EXPORT void ksaMtlScreenshot(const char* path);
// Begins or ends a signpost interval "part" with that name (Points of Interest in Instruments), so a trace comes split
// into the benchmark's parts
KMTL_EXPORT void ksaMtlMarker(const char* name, bool begin);
// An Xcode GPU frame capture of the next frames into path (.gputrace): false when one is running, the GPU failed, or
// Metal doesn't allow it (the game needs MTL_CAPTURE_ENABLED=1); then true until the file is written
KMTL_EXPORT bool ksaMtlCapture(const char* path, uint32_t frames);
KMTL_EXPORT bool ksaMtlCaptureRunning();
// The bytes the driver holds now: device memory by type (private, shared write-combined, shared cached), then its own
// (descriptor pool chunks, upload rings); how many of the n it wrote
KMTL_EXPORT uint32_t ksaMtlMemory(int64_t* out, uint32_t n);
// The command buffers that failed, after which nothing the GPU would have to do can be dumped or captured
KMTL_EXPORT uint32_t ksaMtlGpuErrors();

// ---------------------------------------------------------------------------------------------------------------
// The pass dump (diagnostics/passdump.mm). C#: src/shared/DriverStats.cs, src/player/benchmark-cli/Run.cs

// The next frame's passes' images, and the images at its end, into dir: false when one is being written or the GPU
// failed; then true until they are written
KMTL_EXPORT bool ksaMtlPassDump(const char* dir);
KMTL_EXPORT bool ksaMtlPassDumpRunning();

// ---------------------------------------------------------------------------------------------------------------
// The game's GPU profiler (diagnostics/zones.mm). C#: src/shared/DriverStats.cs; ksaMtlZoneMode also
// src/player/benchmark-cli/Run.cs

// How its timestamps are answered (kZonesAsTheyCome, kZonesLabels, kZonesSplit): false for one this game can't have
// (split needs gpu_zones)
KMTL_EXPORT bool ksaMtlZoneMode(uint32_t mode);
// A zone begins or ends in a recording
KMTL_EXPORT void ksaMtlZoneBegin(VkCommandBuffer commandBuffer, const char* name);
KMTL_EXPORT void ksaMtlZoneEnd(VkCommandBuffer commandBuffer);

// ---------------------------------------------------------------------------------------------------------------
// The benchmark's drain (diagnostics/drain.mm). C#: src/shared/DriverStats.cs (Parse reads the records)
//
//   1 present  at each present (instruments.mm): its time and KsaMtlFrameStats
//   2 shown    from the drawable's presented handler (presentation/pacing.mm): when the frame began (the game's wait
//              for an earlier frame ended), was presented, finished on the GPU and was shown (0: never), how long it
//              waited for an earlier frame, and whether pacing ended that wait
//   3 wait     a scope that blocked its caller at least the reader's threshold (KStallTimer, residency commits)
//   4 memory   an allocation (bytes positive) or a free (negative) of 16 MB or more (resources/memory.mm)
//   5 gpu      a frame's own GPU time, when its present's command buffer completed (presentation/pacing.mm): the
//              present (as the present record numbers it), when, and the GPU's busy time since the frame before's
//              present completed. The present record's GPU time is what completed between two presents on the CPU's
//              clock, which lags by the frames in flight and puts two frames' work in one after a stall
//
// Each record is a header (type, size of the whole record, 4 bytes unused) and its fields, times on CACurrentMediaTime's
// clock in seconds. kKsaMtlDrainVersion names this layout: the reader opens the drain with the version it knows, and a
// driver of another layout refuses.
constexpr uint32_t kKsaMtlDrainVersion = 1;
struct KsaMtlDrainHeader {
    uint16_t type, size;
    uint32_t reserved;
};
struct KsaMtlPresentRecord {
    KsaMtlDrainHeader h;
    double t;
    KsaMtlFrameStats stats;
};
struct KsaMtlShownRecord {
    KsaMtlDrainHeader h;
    uint64_t present;
    double began, presented, gpuEnd, shown, waitedMs;
    uint32_t paced, reserved;
};
struct KsaMtlWaitRecord {
    KsaMtlDrainHeader h;
    double t, ms;
    uint64_t present;
    char what[40];
};
struct KsaMtlMemoryRecord {
    KsaMtlDrainHeader h;
    double t;
    int64_t bytes;
    uint32_t type, reserved;
    uint64_t present;
};
struct KsaMtlGpuRecord {
    KsaMtlDrainHeader h;
    uint64_t present;
    double t, ms;
};
// Where Parse reads each record's fields, from the end of the header
static_assert(sizeof(KsaMtlDrainHeader) == 8, "Parse's header");
static_assert(offsetof(KsaMtlPresentRecord, stats) == 8 + 8, "Parse's present record");
static_assert(offsetof(KsaMtlShownRecord, paced) == 8 + 48 && sizeof(KsaMtlShownRecord) >= 8 + 56, "Parse's shown record");
static_assert(offsetof(KsaMtlWaitRecord, what) == 8 + 24 && sizeof(KsaMtlWaitRecord) >= 8 + 64, "Parse's wait record");
static_assert(offsetof(KsaMtlMemoryRecord, present) == 8 + 24 && sizeof(KsaMtlMemoryRecord) >= 8 + 32, "Parse's memory record");
static_assert(offsetof(KsaMtlGpuRecord, ms) == 8 + 16 && sizeof(KsaMtlGpuRecord) >= 8 + 24, "Parse's GPU record");

// Opens the drain for a reader of this layout (false for another), with the waits of at least waitMs recorded, in a
// ring of capacity bytes; the next records into out, as many whole ones as fit (the bytes written); the records
// dropped because the ring was full; closes it, forgetting what wasn't read
KMTL_EXPORT bool ksaMtlDrainOpen(uint32_t version, double waitMs, uint32_t capacity);
KMTL_EXPORT uint32_t ksaMtlDrain(uint8_t* out, uint32_t capacity);
KMTL_EXPORT uint64_t ksaMtlDrainDropped();
KMTL_EXPORT void ksaMtlDrainClose();

// ---------------------------------------------------------------------------------------------------------------
// Early fragment tests (pipelines/compile.mm). C#: src/game/changes/optimizations/HiddenPartsSkip.cs

// Whether draws bind the pipelines' states that test depth first: the hooks' Skip Hidden Vessel Parts, at the start of
// each frame, while no part is selected
KMTL_EXPORT void ksaMtlEarlyFragmentTests(bool on);

// ---------------------------------------------------------------------------------------------------------------
// HDR output (presentation/hdr.mm). C#: src/game/changes/fixes/Hdr.cs

// The game's HDR UI Brightness and HDR Peak Brightness, in nits, whenever it applies its Display settings
KMTL_EXPORT void ksaMtlHdrNits(float uiBrightness, float peak);

// ---------------------------------------------------------------------------------------------------------------
// Dynamic resolution's mechanism (resources/dynres.mm). C#: src/game/changes/features/DynamicResolution.cs

// An image's size as its texture has it now (a dynamic image's at the frame's level)
KMTL_EXPORT void ksaMtlDynamicImageSize(VkImage image, uint32_t* width, uint32_t* height);
// A size of the main view's or a fraction of it, at the level now, as its image's texture has it (in place)
KMTL_EXPORT void ksaMtlDynamicSizeAt(uint32_t* width, uint32_t* height);

// ---------------------------------------------------------------------------------------------------------------
// The Frame Rate section's controller (presentation/framerate.mm). C#: src/game/changes/features/DynamicResolution.cs

// After each rebuild of the main view's targets (and before they are made): its size (0 x 0: none), the display's, and
// the least size a level may have (0 x 0: the display's rule)
KMTL_EXPORT void ksaMtlFrameRateView(uint32_t width, uint32_t height, uint32_t outW, uint32_t outH, uint32_t leastW,
                                     uint32_t leastH);
// The main view's size at the level now, and the level (0: the full size)
KMTL_EXPORT void ksaMtlFrameRateSize(uint32_t* width, uint32_t* height, uint32_t* level);
// At the start of the game's frame, before it records any of it: the rate's step, this frame's level, changed here,
// and the main view's size at it
KMTL_EXPORT void ksaMtlFrameRateBeginFrame(uint32_t* width, uint32_t* height, uint32_t* level);

// ---------------------------------------------------------------------------------------------------------------
// MetalFX (presentation/upscale.mm, interpolate.mm, rayhistory.mm). C#: src/game/changes/features/Upscale.cs;
// ksaMtlUpscaleMaxScale src/game/hooks/tab/MacPortWidgets.cs

// The main view's image scaled into an image of the swapchain's size, between the game's scene and its final composite.
// C#'s mirror: VkUpscaleInfoKSA
struct VkUpscaleInfoKSA {
    VkImage color;           // render size, linear HDR
    VkImage depth;           // render size, D32, reversed (0 = far)
    VkImage output;          // output size, color format, storage usage
    uint32_t inputWidth, inputHeight;
    float jitterX, jitterY;  // pixels the projection moved the image by (y down)
    // Row-vector, row-major matrices. Ego = camera-centred position, ecliptic axes.
    float egoFromClip[16];      // (ndc.xy, depth, 1) * M = homogeneous ego position (inverse jittered view-projection)
    float prevClipFromEgo[16];  // ego * M = the previous frame's unjittered clip position, for the scene
    float prevClipFromEgoObject[16];  // the same for the followed object
    float prevClipFromEgoSky[16];     // the same for the sky (depth 0)
    float prevClipFromEgoNearby[16];  // the same for what is outside the body's sphere: moving with the followed object
    float objectSphere[4];      // its bounding sphere in ego (radius 0: none)
    float bodySphere[4];        // the nearby body's highest ground or sea and 1 km, in ego (radius 0: no body)
    float fovY, nearPlane, farPlane, deltaTime;  // for frame interpolation
    uint32_t flags;          // kUpscaleReset, kUpscaleSpatial
    float exposure;          // what the game's tonemapping multiplies the image by (Tonemap Exposure; 0: 1)
    // The game's clouds and smoke trails, as its cloud upscaler drew them this frame at the render size (none: 0): their
    // colour with the light that passes them in alpha, and their own motion (uv now minus uv before, the lowest half
    // where it has none)
    VkImage cloudColor, cloudMotion;
    // Dynamic resolution (dynres.mm): the size drawn this frame, within the input size the scaler is made for (0: that)
    uint32_t contentWidth, contentHeight;
};
static_assert(sizeof(VkUpscaleInfoKSA) == 440 && offsetof(VkUpscaleInfoKSA, egoFromClip) == 40 &&
              offsetof(VkUpscaleInfoKSA, fovY) == 392 && offsetof(VkUpscaleInfoKSA, cloudColor) == 416 &&
              offsetof(VkUpscaleInfoKSA, contentWidth) == 432, "C#'s VkUpscaleInfoKSA has this layout");
constexpr uint32_t kUpscaleReset = 1, kUpscaleSpatial = 2;  // its flags (C#: Reset, SpatialOnly)

VKFN(void) vkCmdUpscaleKSA(VkCommandBuffer commandBuffer, const VkUpscaleInfoKSA* info);
// A copy of the scene for the reactive mask: stage 0 before the translucent effects, 1 before the bloom, 2 after it
VKFN(void) vkCmdUpscaleSnapshotKSA(VkCommandBuffer commandBuffer, VkImage image, uint32_t stage);
// Inside the game's final pass, between the composited scene and the UI: the scene for frame interpolation
VKFN(void) vkCmdMarkUiKSA(VkCommandBuffer commandBuffer);
// How far the temporal scaler scales on this Mac, past which the spatial one does the rest (the Mac Port tab)
KMTL_EXPORT float ksaMtlUpscaleMaxScale();

// The cockpit's ray-traced lighting images blended with a history of their own, after the game smoothed them.
// C#'s mirror: VkRayHistoryInfoKSA
struct VkRayHistoryInfoKSA {
    VkImage images[4];      // the lighting images, each at its own size (none: 0)
    float maxFrames[4];     // how many frames each one's history holds at most
    float clip[4];          // how far the history may stray from this frame's 3x3 pixels' mean, in their spread (0: free)
    VkImage depth;          // the prepass's depth, render size, reversed (0 = far)
    VkImage normals;        // the prepass's normals (xyz, roughness in w), render size
    // Row-vector, row-major matrices, as in VkUpscaleInfoKSA: this frame's jittered clip to ego, ego to the last frame's
    // plain clip for the scene, the followed object and what is near the object (the sky has no ray-traced surface)
    float egoFromClip[16], scene[16], object[16], nearby[16];
    float objectSphere[4], bodySphere[4];
    float jitterX, jitterY;  // pixels (render size) the projection moved the image by
    uint32_t flags;          // kRayHistoryReset
    uint32_t frame;          // the main view's frame count (the hooks'): a history counts only from the frame just before
};
static_assert(sizeof(VkRayHistoryInfoKSA) == 384 && offsetof(VkRayHistoryInfoKSA, depth) == 64 &&
              offsetof(VkRayHistoryInfoKSA, objectSphere) == 336 && offsetof(VkRayHistoryInfoKSA, flags) == 376,
              "C#'s VkRayHistoryInfoKSA has this layout");
constexpr uint32_t kRayHistoryReset = 1;  // its flag

VKFN(void) vkCmdRayHistoryKSA(VkCommandBuffer commandBuffer, const VkRayHistoryInfoKSA* info);
