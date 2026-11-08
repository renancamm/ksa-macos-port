// KSA Metal's instruments (diagnostics/): the frame log and screenshots, the benchmark's drain, the pass dump, the GPU
// profiler. Included by kmtl.h.
#pragma once

// A present's numbers, as the benchmark reads them (ksaMtlFrameStats, the drain's present records). The caller passes the
// size of the struct it knows and gets back how much was filled; the benchmark's director takes the stats only when that
// is all of its struct, so a director and a driver from different builds don't misread them.
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
extern "C" uint32_t ksaMtlFrameStats(KsaMtlFrameStats* out, uint32_t size);

// The benchmark's drain (drain.mm): records for the reader, once it opened the drain (kmtlDrainOn, kmtl.h)
void kmtlDrainPresent(double t, const KsaMtlFrameStats& stats);
void kmtlDrainShown(uint64_t present, double began, double presented, double gpuEnd, double shown, double waitedMs, bool paced);
void kmtlDrainMemory(int64_t bytes, uint32_t type);
void kmtlDrainFrameGpu(uint64_t present, double t, double ms);

// Frame instruments (instruments.mm)
void kmtlOnPresent(KDevice* dev, KSwapchain* swapchain, KImage* image, id<MTLCommandBuffer> cb);
void kmtlOnSwapchainDestroyed(KSwapchain* swapchain);
// Pass dump (passdump.mm)
void kmtlPassDumpTrack(KImage* image);
void kmtlPassDumpForget(KImage* image);
void kmtlPassDumpTrackMemory(id<MTLBuffer> whole);
void kmtlPassDumpForgetMemory(id<MTLBuffer> whole);
void kmtlPassDumpSwapchainGone(KSwapchain* swapchain);
bool kmtlPassDumpRecording();
void kmtlPassDumpEncoder(KCommandBuffer* cb, const std::string& name, MTLRenderPassDescriptor* pass);
void kmtlPassDumpAfterDraw(KCommandBuffer* cb, KPipeline* pipeline);
void kmtlPassDumpAtPresent(KQueue* queue, KSwapchain* swapchain, id<MTLCommandBuffer> cb);
void kmtlWatchAtPresent(KQueue* queue, KImage* image, id<MTLCommandBuffer> cb);
void kmtlNoteGpuError();
extern "C" uint32_t ksaMtlGpuErrors();
void kmtlWritePNG(id<MTLBuffer> buf, NSUInteger w, NSUInteger h, bool bgra, const std::string& path);
void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes);
void kmtlNoteGpuTime(id<MTLCommandBuffer> cb);  // from completion handlers: GPU busy time for the frame log
double kmtlTakeFrameGpuTime();  // one frame's GPU busy time in seconds (since the last present completed), reset
// From swapchain.mm's presented handlers: the display showed a drawable at t (presentedTime); timed: a game frame,
// whose interval since the last one is the frame log's display_ms (interpolated frames only count)
void kmtlNoteShown(double t, bool timed);

// The game's GPU profiler (zones.mm): the timestamp sample buffers of its queries, recycled, since Metal caps how many
// are alive (null, logged a few times, when one can't be made); how its timestamps are answered (ksaMtlZoneMode); a
// draw's or dispatch's note for the encoder's name and a pass dump
constexpr uint32_t kTimestampSamples = 4096;
constexpr uint32_t kZonesAsTheyCome = 0, kZonesLabels = 1, kZonesSplit = 2;
id<MTLCounterSampleBuffer> kmtlAcquireTimestamps(KDevice* dev, const char* what);
void kmtlRecycleTimestamps(id<MTLCounterSampleBuffer> b);
uint32_t kmtlZoneMode();
void kmtlNoteDraw(KCommandBuffer* cb, KPipeline* p, bool dispatch);

// instruments.mm: a texture (8-bit RGBA or BGRA) saved as a PNG once the command buffer completes, then done (if any);
// the copy waits for the fence after (if any)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void) = nil);
