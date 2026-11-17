// KSA Metal's instruments (diagnostics/): the frame log and screenshots, the benchmark's drain, the pass dump, the GPU
// profiler. Included by kmtl.h.
#pragma once

// The benchmark's drain (drain.mm): records for the reader, once it opened the drain (kmtlDrainOn, kmtl.h)
void kmtlDrainPresent(double t, const KsaMtlFrameStats& stats);
void kmtlDrainShown(uint64_t present, double began, double presented, double gpuEnd, double shown, double waitedMs, bool paced);
void kmtlDrainMemory(int64_t bytes, uint32_t type);
void kmtlDrainFrameGpu(uint64_t present, double t, double ms);

// Frame instruments (instruments.mm)
// A present (its number, as frametime.mm counted it)
void kmtlOnPresent(KDevice* dev, KSwapchain* swapchain, KImage* image, id<MTLCommandBuffer> cb, uint64_t presents);
uint64_t kmtlShotsTaken();  // screenshots encoded so far (kmtlOnPresent takes them)
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
// The pass dump's writer queue, which the watch writes on too
dispatch_queue_t kmtlPassDumpWriter();
// The watch (watch.mm): its look at the presented image; a line of its watch.txt
void kmtlWatchAtPresent(KQueue* queue, KImage* image, id<MTLCommandBuffer> cb);
void kmtlWatchLine(const std::string& text);
// instruments.mm: a command buffer failed (ksaMtlGpuErrors)
void kmtlNoteGpuError();
// instruments.mm: 8-bit RGBX pixels as a PNG (false where it couldn't be written); a screenshot's buffer (RGBA or BGRA)
// as one, logged
bool kmtlEncodePNG(uint8_t* rgbx, NSUInteger w, NSUInteger h, const std::string& path);
void kmtlWritePNG(id<MTLBuffer> buf, NSUInteger w, NSUInteger h, bool bgra, const std::string& path);
void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes);

// The game's GPU profiler (zones.mm): how its timestamps are answered (ksaMtlZoneMode; commands/timestamps.mm takes
// them); a draw's or dispatch's note for the encoder's name and a pass dump
constexpr uint32_t kZonesAsTheyCome = 0, kZonesLabels = 1, kZonesSplit = 2;
uint32_t kmtlZoneMode();
void kmtlNoteDraw(KCommandBuffer* cb, KPipeline* p, bool dispatch);

// instruments.mm: a texture (8-bit RGBA or BGRA) saved as a PNG once the command buffer completes, then done (if any);
// the copy waits for the fence after (if any)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void) = nil);
