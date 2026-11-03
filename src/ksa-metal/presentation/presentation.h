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
float kmtlHdrPaperWhite();

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
};

// Pixel size of a surface's layer (bounds x contentsScale): the surface's current extent
VkExtent2D kmtlLayerExtent(CAMetalLayer* layer);

struct KSwapchain {
    std::shared_ptr<KPresentState> presentState = std::make_shared<KPresentState>();
    KSurface* surface = nullptr;
    std::vector<KImage*> images;
    uint32_t next = 0;
    VkExtent2D extent{};
};

// upscale.mm: the present's MetalFX passes
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color);
