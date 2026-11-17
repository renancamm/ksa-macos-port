// KSA Metal's presentation (presentation/): surfaces and swapchains, the frames' timing, the Frame Rate section, MetalFX.
// Included by kmtl.h.
#pragma once

// ---------------------------------------------------------------------------------------------------------------
// Surface and swapchain
struct KSurface {
    CAMetalLayer* layer = nil;
    bool hdr = false;                         // the layer shows HDR10 with EDR (hdr.mm)
    CGColorSpaceRef sdrColorspace = nullptr;  // the layer's colour space before, given back for SDR
};

// HDR output (hdr.mm): whether the display the layer's window is on can show more than SDR white; the surface's
// layer set up for HDR10 or given back its SDR state
bool kmtlEdrAvailable(CAMetalLayer* layer);
void kmtlSetHdr(KSurface* surface, bool hdr);
float kmtlHdrUiBrightness();

// Present ids of a swapchain, waited on by vkWaitForPresentKHR, and the recent presents its pacing looks at
// (pacing.mm). Shared with the presents' handlers, which may run after the swapchain is destroyed
struct KPresentState {
    struct Frame { uint64_t present, id; double made, gpuEnd; bool gpuDone, shown; };
    std::mutex m;
    std::condition_variable cv;
    uint64_t presented = 0;     // the latest id the display showed
    uint64_t presents = 0;      // presents made
    std::vector<Frame> frames;  // the last 16, oldest first
    double began = 0;           // when the game's last wait for a present ended
    double cpuMs[8] = {}, gpuMs[8] = {};  // recent frames: from that wait to the present; GPU time
    uint32_t cpuSamples = 0, gpuSamples = 0;
    double startDelay = 0;                 // late_frame_start: how much longer the game's wait lasts (seconds)
    double lastShown = 0;                  // when the display showed the last game frame, and its present
    uint64_t lastShownPresent = 0;
};

// Pixel size of a surface's layer (bounds x contentsScale): the surface's current extent (surface.mm)
VkExtent2D kmtlLayerExtent(CAMetalLayer* layer);

struct KSwapchain {
    std::shared_ptr<KPresentState> presentState = std::make_shared<KPresentState>();
    KSurface* surface = nullptr;
    std::vector<KImage*> images;
    uint32_t next = 0;
    VkExtent2D extent{};
    bool vsync = false;
    std::atomic<double> cadenceMs{0};  // each image shown at least this long after the one before (0: as soon as Vsync allows)
    std::atomic<double> frameMs{0};    // the game's frame time the cadence holds (dynamic resolution's target): also at one refresh
    // Steady Frame Rate's steps on this display, the frame time of each, its own rate first (then 40 and 30 where
    // slower), and the one taken: Lower Rate Under Heavy Load steps to a slower one when the lowest level (the full
    // size without dynamic resolution) can't hold the rate
    std::vector<double> steadySteps;
    size_t steadyStep = 0;
    double refreshHz = 0;
    NSArray* observers = nil;  // display changes that set the cadence again (pacing.mm)
};

// pacing.mm, for swapchain.mm: a new swapchain's cadence and drawables (again at each change of its display); the live
// swapchains' list; at a present, the pacing's record of it and its handlers (its present id, its number, whether a
// screenshot's copy is in its GPU time); a drawable shown after the cadence (interpolated: half of it); the latency
// log's present
void kmtlPaceSwapchain(KSwapchain* sc);
void kmtlListSwapchain(KSwapchain* sc);
void kmtlUnlistSwapchain(KSwapchain* sc);
void kmtlPaceAtPresent(KSwapchain* sc, id<MTLCommandBuffer> cb, id<CAMetalDrawable> drawable, uint64_t presentId,
                       uint64_t counted, bool shot);
void kmtlPresentPaced(KSwapchain* sc, id<MTLCommandBuffer> cb, id<CAMetalDrawable> d, bool interpolated);
void kmtlLatencyAtPresent(id<CAMetalDrawable> drawable, id<MTLCommandBuffer> cb);

// The Frame Rate section (framerate.mm): the least share of the main view's size a level may have (1: no levels), for
// MetalFX's scalers; a frame's GPU time and its swapchain as its present completes, for the level's and the rate's
// choice
float kmtlFrameRateMinContent();
void kmtlFrameRateNoteFrame(uint32_t level, double gpuMs, VkSwapchainKHR swapchain);
// pacing.mm: Steady Frame Rate's rate (0: off); the frame time a swapchain's cadence holds (0: none, or not a live
// swapchain); its next step (dir 1: slower, -1: faster) and that step's frame time (0: none), with take taken
int kmtlSteadyFps();
double kmtlFrameMs(VkSwapchainKHR swapchain);
double kmtlSteadyStep(VkSwapchainKHR swapchain, int dir, bool take);
// upscale.mm: how far MetalFX's temporal scaler scales on this Mac; its output from a content size, scaled that far
float kmtlUpscaleMaxScale();
uint32_t kmtlUpscaleReach(uint32_t content, float most);
// upscale.mm, for MetalFX's passes: everything encoded before finishes before anything after starts (a signal and a
// wait on an event); the same around MetalFX's encoders in a command buffer of the game's
void kmtlEventBarrier(id<MTLCommandBuffer> cb, id<MTLEvent> event, uint64_t* value);
void kmtlFxBarrier(KCommandBuffer* cb);

// Players' frame timing (frametime.mm): a present counted (its number); from completion handlers, a command buffer's
// GPU busy time; one frame's GPU busy time in seconds since the last present completed, and since the instruments
// took it at the last present, each reset; from the presented handlers, the display showed a drawable at t
// (presentedTime), timed: a game frame, whose interval since the last one is display_ms (interpolated frames only
// count); the drawables shown, the latest display_ms
uint64_t kmtlCountPresent();
void kmtlNoteGpuTime(id<MTLCommandBuffer> cb);
double kmtlTakeFrameGpuTime();
double kmtlTakePresentGpuTime();
void kmtlNoteShown(double t, bool timed);
uint64_t kmtlShownCount();
double kmtlDisplayMs();

// The GPU's clock (gpuclock.mm, IOReport), for the frame rate's GPU times under paced frames: its sampler started,
// once; the clock lately over its highest (1: unknown); the clock lately (0: unknown); a clock read at least once
void kmtlGpuClockStart();
double kmtlGpuClockShare();
double kmtlGpuClockMhz();
bool kmtlGpuClockKnown();

// interpolate.mm: the present's MetalFX pass, a frame between the last one and this one (the texture to present
// first, or nil); the upscale's handover of a temporal frame's depth, motion, input size, jitter and camera
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color);
void kmtlInterpolateTakeFrame(id<MTLTexture> depth, id<MTLTexture> motion, uint32_t inW, uint32_t inH,
                              const VkUpscaleInfoKSA* info);
