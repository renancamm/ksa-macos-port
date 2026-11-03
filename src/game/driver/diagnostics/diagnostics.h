// KSA Metal's instruments (diagnostics/): the frame log and screenshots, the pass dump, the GPU profiler.
// Included by kmtl.h.
#pragma once

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

// GPU profiler (profile.mm, KSA_MTL_GPU_PROFILE), and the timestamp sample buffers it shares with the game's
// timestamp queries: recycled, since Metal caps how many are alive; null (logged a few times) when one can't be made
constexpr uint32_t kTimestampSamples = 4096;
id<MTLCounterSampleBuffer> kmtlAcquireTimestamps(KDevice* dev, const char* what);
void kmtlRecycleTimestamps(id<MTLCounterSampleBuffer> b);
void kmtlProfBegin(KCommandBuffer* cb);
void kmtlProfDrop(KCommandBuffer* cb);  // a recording dropped without a submit: frees its state
void kmtlProfRenderPass(KCommandBuffer* cb, MTLRenderPassDescriptor* rp);
id<MTLComputeCommandEncoder> kmtlProfComputeEncoder(KCommandBuffer* cb);
id<MTLBlitCommandEncoder> kmtlProfBlitEncoder(KCommandBuffer* cb);
void kmtlProfNote(KCommandBuffer* cb, KPipeline* p, bool dispatch, uint32_t n);
void kmtlProfEncoderEnded(KCommandBuffer* cb);
void kmtlProfSubmit(KCommandBuffer* cb, id<MTLCommandBuffer> mtl);

// instruments.mm: a texture (8-bit RGBA or BGRA) saved as a PNG once the command buffer completes, then done (if any);
// the copy waits for the fence after (if any)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void) = nil);
