// KSA Metal's presentation (presentation/): surfaces and swapchains. Included by kmtl.h.
#pragma once

// ---------------------------------------------------------------------------------------------------------------
// Surface and swapchain
struct KSurface {
    CAMetalLayer* layer = nil;
    bool hdr = false;                         // the layer shows HDR10 with EDR (swapchain.mm)
    CGColorSpaceRef sdrColorspace = nullptr;  // the layer's colour space before, given back for SDR
};

// HDR output (swapchain.mm): whether the display the layer's window is on can show more than SDR white; the surface's
// layer set up for HDR10 or given back its SDR state
bool kmtlEdrAvailable(CAMetalLayer* layer);
void kmtlSetHdr(KSurface* surface, bool hdr);
float kmtlHdrUiBrightness();

// Present ids of a swapchain, waited on by vkWaitForPresentKHR, and the recent presents its pacing looks at
// (swapchain.mm). Shared with the presents' handlers, which may run after the swapchain is destroyed
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

// Pixel size of a surface's layer (bounds x contentsScale): the surface's current extent
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
    NSArray* observers = nil;  // display changes that set the cadence again (swapchain.mm)
};

// The Frame Rate section (framerate.mm): the least share of the main view's size a level may have (1: no levels), for
// MetalFX's scalers; a frame's GPU time and its swapchain's size as its present completes, for the level's and the
// rate's choice
float kmtlDynamicMinContent();
void kmtlDynamicNoteFrame(uint32_t level, double gpuMs, uint32_t width, uint32_t height);
// swapchain.mm: Steady Frame Rate's rate (0: off); the frame time the cadence holds, of the swapchain of this size (0:
// none); its next step (dir 1: slower, -1: faster) and that step's frame time (0: none), with take taken
int kmtlSteadyFps();
double kmtlFrameMs(uint32_t width, uint32_t height);
double kmtlSteadyStep(uint32_t width, uint32_t height, int dir, bool take);
// upscale.mm: how far MetalFX's temporal scaler scales on this Mac; its output from a content size, scaled that far
float kmtlUpscaleMaxScale();
uint32_t kmtlUpscaleReach(uint32_t content, float most);

// upscale.mm: the present's MetalFX passes
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color);
