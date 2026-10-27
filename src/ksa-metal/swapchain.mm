// Swapchain over the GLFW window's CAMetalLayer, and presentation (pacing, frame interpolation, latency). What is
// measured at each present is in instruments.mm.
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#include <chrono>
#include <algorithm>

// KSA_MTL_LATENCY=<csv>: per present, when its frame began (the game's wait for an earlier present returned, or else
// the previous present), when it was presented, finished on the GPU and was shown (seconds, presentedTime's clock),
// how long the frame waited for an earlier present, and whether pacing ended that wait before the display
static std::mutex g_latencyMutex;
static FILE* g_latencyCsv = nullptr;
static double g_frameBegan = 0, g_frameWaited = 0;
static bool g_framePaced = false;

static void latencyBegin(double waited, bool paced) {
    std::lock_guard<std::mutex> lock(g_latencyMutex);
    g_frameBegan = CACurrentMediaTime();
    g_frameWaited += waited;
    g_framePaced = paced;
}

static void latencyAtPresent(id<CAMetalDrawable> drawable, id<MTLCommandBuffer> cb) {
    static uint64_t presents = 0;
    std::lock_guard<std::mutex> lock(g_latencyMutex);
    if (!g_latencyCsv) {
        g_latencyCsv = fopen(kmtlConfig().latency, "w");
        if (!g_latencyCsv) return;
        fprintf(g_latencyCsv, "present,began,presented,gpu_end,shown,waited_ms,paced\n");
    }
    double now = CACurrentMediaTime(), began = g_frameBegan ? g_frameBegan : now, waited = g_frameWaited * 1e3;
    uint64_t n = ++presents;
    bool paced = g_framePaced;
    g_frameBegan = now;
    g_frameWaited = 0;
    g_framePaced = false;
    FILE* csv = g_latencyCsv;
    [drawable addPresentedHandler:^(id<MTLDrawable> d) {
        std::lock_guard<std::mutex> lock(g_latencyMutex);
        fprintf(csv, "%llu,%.6f,%.6f,%.6f,%.6f,%.3f,%d\n", (unsigned long long)n, began, now, cb.GPUEndTime,
                d.presentedTime, waited, paced ? 1 : 0);
        if (n % 60 == 0) fflush(csv);
    }];
}

// The window was resized since the swapchain was made: report VK_SUBOPTIMAL_KHR like other drivers, which makes the
// game check the surface extent and rebuild (a resize it did not schedule itself, e.g. a display mode change)
static bool outdated(KSwapchain* sc) {
    VkExtent2D e = kmtlLayerExtent(sc->surface->layer);
    return e.width && e.height && (e.width != sc->extent.width || e.height != sc->extent.height);
}

VKFN(VkResult) vkCreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* ci, const VkAllocationCallbacks*, VkSwapchainKHR* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    auto* sc = new KSwapchain();
    sc->surface = (KSurface*)ci->surface;
    sc->extent = ci->imageExtent;
    uint32_t count = std::max(2u, std::min(3u, ci->minImageCount));
    for (uint32_t i = 0; i < count; i++) {
        KImage* img = kmtlCreateSwapchainImage(dev, ci->imageFormat, ci->imageExtent.width, ci->imageExtent.height);
        if (!img) {
            KERR("vkCreateSwapchainKHR: image %u of %ux%u format %d failed", i, ci->imageExtent.width,
                 ci->imageExtent.height, ci->imageFormat);
            for (auto* made : sc->images) kmtlDestroyImage(made);
            delete sc;
            return VK_ERROR_OUT_OF_DEVICE_MEMORY;
        }
        img->mtl.label = [NSString stringWithFormat:@"swapchain %u", i];
        sc->images.push_back(img);
    }
    // The layer changes once the images exist: a failed create leaves the old swapchain's layer as it was. CAMetalLayer
    // drawable properties may be set from any thread; hopping to the main thread could deadlock
    CAMetalLayer* layer = sc->surface->layer;
    layer.device = dev->mtl;
    layer.pixelFormat = kmtlPixelFormat(ci->imageFormat);
    layer.framebufferOnly = NO;
    layer.drawableSize = CGSizeMake(ci->imageExtent.width, ci->imageExtent.height);
    layer.maximumDrawableCount = 3;
    layer.displaySyncEnabled = ci->presentMode == VK_PRESENT_MODE_FIFO_KHR || ci->presentMode == VK_PRESENT_MODE_FIFO_RELAXED_KHR;
    layer.opaque = YES;
    dev->commitResidency();
    KLOG(1, "swapchain %ux%u format %d present mode %d, %u images", ci->imageExtent.width, ci->imageExtent.height,
         ci->imageFormat, ci->presentMode, count);
    *out = (VkSwapchainKHR)sc;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks*) {
    auto* sc = (KSwapchain*)swapchain;
    if (!sc) return;
    vkDeviceWaitIdle(device);
    for (auto* img : sc->images) kmtlDestroyImage(img);
    delete sc;
}

VKFN(VkResult) vkGetSwapchainImagesKHR(VkDevice, VkSwapchainKHR swapchain, uint32_t* count, VkImage* out) {
    KTRACE_FIRST();
    auto* sc = (KSwapchain*)swapchain;
    return kmtlEnumerate((uint32_t)sc->images.size(), count, out, [sc](uint32_t i, VkImage& o) { o = (VkImage)sc->images[i]; });
}

VKFN(VkResult) vkAcquireNextImageKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t, VkSemaphore, VkFence fence, uint32_t* index) {
    KTRACE_FIRST();
    KStallTimer stall("vkAcquireNextImageKHR");
    auto* sc = (KSwapchain*)swapchain;
    *index = sc->next;
    sc->next = (sc->next + 1) % sc->images.size();
    if (fence) {
        // Images are reused in queue order, so they are available as soon as acquired
        auto* f = (KFence*)fence;
        std::lock_guard<std::mutex> lock(f->m);
        f->signaled = true;
        f->cv.notify_all();
    }
    return outdated(sc) ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
}

// Present pacing. With Vsync the game waits for the present before last before it reads input. If a present counted
// only once the display showed it, that wait would also cover the window server's delay (about 21 ms in a window, 10
// in fullscreen) and leave the GPU idle whenever a frame takes less than that delay plus the CPU's part. So a present
// also counts once its GPU work has ended, the present before it was shown, and the GPU is about to need the next
// frame: the previous frame's predicted GPU end, less the CPU's part and a 2 ms margin. Frames then start when the GPU
// can take them instead of queueing up. KSA_MTL_PRESENT_PACING=0: the display only (Vulkan's meaning)
static double median8(const double* v, uint32_t n) {
    double s[8];
    n = std::min(n, 8u);
    std::copy(v, v + n, s);
    std::sort(s, s + n);
    return n ? s[n / 2] : 0;
}

static KPresentState::Frame* findFrame(KPresentState& st, uint64_t present) {
    if (st.frames.empty() || present < st.frames.front().present || present > st.frames.back().present) return nullptr;
    return &st.frames[present - st.frames.front().present];
}

// When pacing lets a wait for this id end (CACurrentMediaTime seconds): 0 now, -1 not yet known
static double pacedEnd(KPresentState& st, uint64_t id) {
    if (st.frames.empty() || id < st.frames.front().id) return 0;
    KPresentState::Frame* f = nullptr;
    for (auto& fr : st.frames) if (fr.id == id) { f = &fr; break; }
    if (!f || !f->gpuDone) return -1;
    KPresentState::Frame* prev = findFrame(st, f->present - 1);
    if (prev && !prev->shown) return -1;
    KPresentState::Frame* next = findFrame(st, f->present + 1);
    double lead = median8(st.gpuMs, st.gpuSamples) - median8(st.cpuMs, st.cpuSamples) - 2.0;
    return lead > 0 ? std::max(f->gpuEnd, next ? next->made : 0.0) + lead * 1e-3 : 0;
}

VKFN(VkResult) vkWaitForPresentKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t presentId, uint64_t timeout) {
    KTRACE_FIRST();
    std::shared_ptr<KPresentState> state = ((KSwapchain*)swapchain)->presentState;
    bool pacing = kmtlConfig().presentPacing;
    double start = CACurrentMediaTime();
    std::unique_lock<std::mutex> lock(state->m);
    auto deadline = std::chrono::steady_clock::now() +
        std::chrono::nanoseconds(timeout > (uint64_t)INT64_MAX / 2 ? (uint64_t)INT64_MAX / 2 : timeout);
    bool done, paced = false;
    while (!(done = state->presented >= presentId)) {
        double at = pacing ? pacedEnd(*state, presentId) : -1, now = CACurrentMediaTime();
        if (at >= 0 && at <= now) { done = paced = true; break; }
        auto until = at > 0 ? std::min(deadline, std::chrono::steady_clock::now() + std::chrono::duration_cast<
                                       std::chrono::steady_clock::duration>(std::chrono::duration<double>(at - now)))
                            : deadline;
        if (state->cv.wait_until(lock, until) == std::cv_status::timeout && std::chrono::steady_clock::now() >= deadline) {
            done = state->presented >= presentId;
            break;
        }
    }
    double end = CACurrentMediaTime();
    state->began = end;
    lock.unlock();
    if (kmtlConfig().latency) latencyBegin(end - start, paced);
    return done ? VK_SUCCESS : VK_TIMEOUT;
}

VKFN(VkResult) vkAcquireNextImage2KHR(VkDevice device, const VkAcquireNextImageInfoKHR* info, uint32_t* index) {
    return vkAcquireNextImageKHR(device, info->swapchain, info->timeout, info->semaphore, info->fence, index);
}

// The image into a drawable (of the image's size, but for a resize in flight: then what both have)
static void present(id<MTLBlitCommandEncoder> b, id<MTLTexture> image, id<MTLTexture> drawable) {
    NSUInteger w = std::min(drawable.width, image.width), h = std::min(drawable.height, image.height);
    [b copyFromTexture:image sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
             toTexture:drawable destinationSlice:0 destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
}

VKFN(VkResult) vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* pi) {
    KTRACE_FIRST();
    KStallTimer stall("vkQueuePresentKHR");
    auto* q = (KQueue*)queue;
    std::lock_guard<std::mutex> lock(q->submitMutex);
    auto* ids = (const VkPresentIdKHR*)findNext(pi->pNext, VK_STRUCTURE_TYPE_PRESENT_ID_KHR);
    VkResult result = VK_SUCCESS;
    for (uint32_t i = 0; i < pi->swapchainCount; i++) {
        @autoreleasepool {
            auto* sc = (KSwapchain*)pi->pSwapchains[i];
            VkResult r = outdated(sc) ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
            KImage* img = sc->images[pi->pImageIndices[i]];
            id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
            cb.label = @"present";
            id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
            [b waitForFence:q->chain];
            // KSA_MTL_FX_INTERPOLATE: a frame between the last one and this one goes to the display first
            if (kmtlConfig().fxInterpolate) {
                [b endEncoding];
                id<MTLTexture> mid = kmtlPresentInterpolate(q->device, cb, img->mtl);
                id<CAMetalDrawable> first = mid ? [sc->surface->layer nextDrawable] : nil;
                if (first) {
                    id<MTLBlitCommandEncoder> copy = [cb blitCommandEncoder];
                    present(copy, mid, first.texture);
                    [copy endEncoding];
                    [first addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, false); }];
                    [cb presentDrawable:first];
                }
                b = [cb blitCommandEncoder];
                [b waitForFence:q->chain];
            }
            id<CAMetalDrawable> drawable;
            {
                KStallTimer stall("nextDrawable");
                drawable = [sc->surface->layer nextDrawable];
            }
            if (drawable) present(b, img->mtl, drawable.texture);
            [b updateFence:q->chain];
            [b endEncoding];
            [cb addCompletedHandler:^(id<MTLCommandBuffer> c) { kmtlNoteGpuTime(c); }];
            kmtlOnPresent(q->device, img, cb);
            {
                // For vkWaitForPresentKHR: when this present was made, ended on the GPU, and was shown
                std::shared_ptr<KPresentState> state = sc->presentState;
                uint64_t presentId = ids && ids->pPresentIds && i < ids->swapchainCount ? ids->pPresentIds[i] : 0, n;
                {
                    std::lock_guard<std::mutex> lock(state->m);
                    double now = CACurrentMediaTime();
                    n = ++state->presents;
                    // The CPU's part of a frame: from the game's wait to the present, when it waited in this frame
                    if (state->began > 0 && (state->frames.empty() || state->began > state->frames.back().made))
                        state->cpuMs[state->cpuSamples++ % 8] = (now - state->began) * 1e3;
                    if (state->frames.size() == 16) state->frames.erase(state->frames.begin());
                    state->frames.push_back({n, presentId, now, 0, false, false});
                }
                bool shownAtEnd = !drawable;
                [cb addCompletedHandler:^(id<MTLCommandBuffer> c) {
                    std::lock_guard<std::mutex> lock(state->m);
                    state->gpuMs[state->gpuSamples++ % 8] = kmtlTakeFrameGpuTime() * 1e3;
                    if (KPresentState::Frame* f = findFrame(*state, n)) {
                        f->gpuDone = true;
                        f->gpuEnd = c.GPUEndTime;
                        f->shown |= shownAtEnd;
                    }
                    if (shownAtEnd) state->presented = std::max(state->presented, presentId);
                    state->cv.notify_all();
                }];
                if (drawable) [drawable addPresentedHandler:^(id<MTLDrawable>) {
                    std::lock_guard<std::mutex> lock(state->m);
                    if (KPresentState::Frame* f = findFrame(*state, n)) f->shown = true;
                    state->presented = std::max(state->presented, presentId);
                    state->cv.notify_all();
                }];
            }
            if (drawable) {
                // When the display showed it (0 for an image it never showed)
                [drawable addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, true); }];
                if (kmtlConfig().latency) latencyAtPresent(drawable, cb);
                [cb presentDrawable:drawable];
            }
            [cb commit];
            if (pi->pResults) pi->pResults[i] = r;
            if (r != VK_SUCCESS) result = r;
        }
    }
    return result;
}
