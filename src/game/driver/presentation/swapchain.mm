// Swapchain over the GLFW window's CAMetalLayer, and presentation (pacing, frame interpolation, latency). What is
// measured at each present is in instruments.mm.
#include "kmtl.h"
#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#include <chrono>
#include <algorithm>
#include <thread>

// KSA_MTL_LATENCY=<csv>, and the benchmark's drain while it is open (shown records, diagnostics/drain.mm): per present,
// when its frame began (the game's wait for an earlier present returned, or else the previous present), when it was
// presented, finished on the GPU and was shown (seconds, presentedTime's clock), how long the frame waited for an earlier
// present, and whether pacing ended that wait before the display
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
    // The handler is added after the mutex is released: Core Animation runs presented handlers while it holds the
    // drawable's lock, which addPresentedHandler takes too, and the handler takes this mutex (a deadlock otherwise:
    // the game hung in vkQueuePresentKHR)
    double now, began, waited;
    uint64_t n, present = kmtlPresentCount();  // kmtlOnPresent counted this present already
    bool paced, drain = kmtlDrainOn();
    FILE* csv;
    {
        std::lock_guard<std::mutex> lock(g_latencyMutex);
        if (!g_latencyCsv && kmtlConfig().latency) {
            g_latencyCsv = fopen(kmtlConfig().latency, "w");
            if (g_latencyCsv) fprintf(g_latencyCsv, "present,began,presented,gpu_end,shown,waited_ms,paced\n");
        }
        if (!g_latencyCsv && !drain) return;
        now = CACurrentMediaTime();
        began = g_frameBegan ? g_frameBegan : now;
        waited = g_frameWaited * 1e3;
        n = ++presents;
        paced = g_framePaced;
        g_frameBegan = now;
        g_frameWaited = 0;
        g_framePaced = false;
        csv = g_latencyCsv;
    }
    [drawable addPresentedHandler:^(id<MTLDrawable> d) {
        if (drain) kmtlDrainShown(present, began, now, cb.GPUEndTime, d.presentedTime, waited, paced);
        if (!csv) return;
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

// HDR output. The game offers HDR in its Display settings when the surface has an HDR format, which instance.mm gives
// on a display with EDR (extended dynamic range: brighter than SDR white, as Apple's XDR displays and HDR monitors in
// HDR mode): HDR10, the game's absolute nits in PQ. The layer shows it with EDR, and its metadata maps the game's paper
// white (Display > HDR Paper White) to the display's SDR white: its UI then sits at the brightness of other windows,
// and its highlights reach up to HDR Peak Brightness, tone-mapped by macOS to what the display has to spare. The game
// sends the peak with vkSetHdrMetadataEXT only when it rebuilds the swapchain and keeps paper white to itself, so the
// hooks pass both in (ksaMtlHdrNits) whenever the game applies its Display settings.
static std::mutex g_hdrMutex;
static float g_hdrPaperWhite = 200, g_hdrPeak = 1000, g_hdrMin = 0;  // the game's defaults, until it or the hooks say
static CAMetalLayer* g_hdrLayer = nil;  // the HDR layer, which the metadata goes to: KSA has one HDR window

bool kmtlEdrAvailable(CAMetalLayer* layer) {
    // The display of the window the layer is in (AppKit calls: the game makes surfaces and swapchains on its main
    // thread); else any display
    for (NSWindow* window in NSApp.windows) {
        if (window.contentView.layer == layer && window.screen)
            return window.screen.maximumPotentialExtendedDynamicRangeColorComponentValue > 1;
    }
    for (NSScreen* screen in NSScreen.screens) {
        if (screen.maximumPotentialExtendedDynamicRangeColorComponentValue > 1) return true;
    }
    return false;
}

// The window a layer is in (AppKit calls, on the game's main thread)
static NSWindow* layerWindow(CAMetalLayer* layer) {
    for (NSWindow* window in NSApp.windows) {
        if (window.contentView.layer == layer) return window;
    }
    return nil;
}

// The highest refresh rate of the display the layer's window is on (a ProMotion display: 120), else the main display's
static double displayRefresh(CAMetalLayer* layer) {
    @autoreleasepool {
        NSScreen* screen = layerWindow(layer).screen ?: NSScreen.mainScreen;
        NSInteger hz = screen.maximumFramesPerSecond;
        return hz > 0 ? (double)hz : 60.0;
    }
}

// A swapchain's cadence and drawables. KSA_MTL_STEADY_FRAME_RATE (with Vsync): half the refresh rate of the display the
// window is on, 60 fps at 120 Hz, again whenever the window moves to another display or a display changes;
// KSA_MTL_FRAME_CADENCE sets the interval itself. 2 drawables hold the display queue to one frame: at 25 ms a frame or
// more (40 fps or less), one frame less latency with the same cadence; at 60 fps the game blocks on a drawable and
// misses slots, so 3 (KSA_MTL_DRAWABLE_COUNT=2 or 3 decides instead). Frame interpolation takes two drawables in one
// present, so it needs 3
static void applyCadence(KSwapchain* sc) {
    CAMetalLayer* layer = sc->surface->layer;
    sc->cadenceMs = kmtlConfig().frameCadence > 0 ? kmtlConfig().frameCadence
                  : kmtlConfig().steadyFrameRate && sc->vsync ? 2000.0 / displayRefresh(layer) : 0;
    int forced = kmtlConfig().drawableCount;
    bool two = forced == 2 || (forced != 3 && sc->cadenceMs >= 25);
    layer.maximumDrawableCount = two && !kmtlConfig().frameInterpolation ? 2 : 3;
}

static void applyHdrMetadata() {  // g_hdrMutex held
    if (!g_hdrLayer) return;
    g_hdrLayer.EDRMetadata = [CAEDRMetadata HDR10MetadataWithMinLuminance:g_hdrMin maxLuminance:g_hdrPeak
                                                       opticalOutputScale:g_hdrPaperWhite];
}

void kmtlSetHdr(KSurface* surface, bool hdr) {
    CAMetalLayer* layer = surface->layer;
    std::lock_guard<std::mutex> lock(g_hdrMutex);
    if (hdr && !surface->hdr) surface->sdrColorspace = CGColorSpaceRetain(layer.colorspace);
    if (hdr) {
        CGColorSpaceRef pq = CGColorSpaceCreateWithName(kCGColorSpaceITUR_2100_PQ);
        layer.colorspace = pq;
        CGColorSpaceRelease(pq);
        layer.wantsExtendedDynamicRangeContent = YES;
        g_hdrLayer = layer;
        applyHdrMetadata();
    } else if (surface->hdr) {
        layer.colorspace = surface->sdrColorspace;
        CGColorSpaceRelease(surface->sdrColorspace);
        surface->sdrColorspace = nullptr;
        layer.wantsExtendedDynamicRangeContent = NO;
        layer.EDRMetadata = nil;
        if (g_hdrLayer == layer) g_hdrLayer = nil;
    }
    surface->hdr = hdr;
}

float kmtlHdrPaperWhite() {
    std::lock_guard<std::mutex> lock(g_hdrMutex);
    return g_hdrPaperWhite;
}

KMTL_EXPORT void ksaMtlHdrNits(float paperWhite, float peak) {
    if (paperWhite <= 0 || peak <= 0) return;
    std::lock_guard<std::mutex> lock(g_hdrMutex);
    if (g_hdrPaperWhite == paperWhite && g_hdrPeak == peak) return;
    g_hdrPaperWhite = paperWhite;
    g_hdrPeak = peak;
    applyHdrMetadata();
    KLOG(1, "HDR paper white %.0f nits, peak %.0f nits", paperWhite, peak);
}

VKFN(void) vkSetHdrMetadataEXT(VkDevice, uint32_t count, const VkSwapchainKHR*, const VkHdrMetadataEXT* metadata) {
    KTRACE_FIRST();
    if (!count) return;
    std::lock_guard<std::mutex> lock(g_hdrMutex);
    if (metadata[0].maxLuminance > 0) g_hdrPeak = metadata[0].maxLuminance;
    g_hdrMin = std::max(metadata[0].minLuminance, 0.0f);
    applyHdrMetadata();
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
    sc->vsync = ci->presentMode == VK_PRESENT_MODE_FIFO_KHR || ci->presentMode == VK_PRESENT_MODE_FIFO_RELAXED_KHR;
    applyCadence(sc);
    if (kmtlConfig().steadyFrameRate && kmtlConfig().frameCadence <= 0 && sc->vsync) {
        // The game makes a new swapchain only when the window's size changes. Both on the main thread, as the presents
        NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
        auto again = ^(NSNotification*) {
            applyCadence(sc);
            KLOG(1, "swapchain: cadence %.2f ms, %lu drawables", sc->cadenceMs, (unsigned long)layer.maximumDrawableCount);
        };
        sc->observers = @[
            [center addObserverForName:NSWindowDidChangeScreenNotification object:layerWindow(layer)
                                 queue:NSOperationQueue.mainQueue usingBlock:again],
            [center addObserverForName:NSApplicationDidChangeScreenParametersNotification object:nil
                                 queue:NSOperationQueue.mainQueue usingBlock:again],
        ];
    }
    layer.displaySyncEnabled = sc->vsync;
    // The game's extra windows (detached gauges, pop-outs) ask for a see-through one and clear it to alpha 0
    layer.opaque = ci->compositeAlpha == VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    kmtlSetHdr(sc->surface, ci->imageColorSpace == VK_COLOR_SPACE_HDR10_ST2084_EXT);
    dev->commitResidency();
    KLOG(1, "swapchain %ux%u format %d colour space %d present mode %d, %u images, %lu drawables, cadence %.2f ms",
         ci->imageExtent.width, ci->imageExtent.height, ci->imageFormat, ci->imageColorSpace, ci->presentMode, count,
         (unsigned long)layer.maximumDrawableCount, sc->cadenceMs);
    *out = (VkSwapchainKHR)sc;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks*) {
    auto* sc = (KSwapchain*)swapchain;
    if (!sc) return;
    vkDeviceWaitIdle(device);
    for (id observer in sc->observers) [NSNotificationCenter.defaultCenter removeObserver:observer];
    kmtlOnSwapchainDestroyed(sc);
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
// can take them instead of queueing up. KSA_MTL_VSYNC_PACING=0: the display only (Vulkan's meaning)
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
    bool pacing = kmtlConfig().vsyncPacing;
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
    // late_frame_start's delay, within the caller's timeout
    double left = std::chrono::duration<double>(deadline - std::chrono::steady_clock::now()).count();
    double delay = done ? std::min(state->startDelay, std::max(left, 0.0)) : 0;
    lock.unlock();
    if (delay > 0) std::this_thread::sleep_for(std::chrono::duration<double>(delay));
    double end = CACurrentMediaTime();
    // The drain only: under V-Sync the game waits here every frame by design, which the stalls log would drown in
    if (kmtlDrainOn()) kmtlDrainWait("vkWaitForPresentKHR", start, (end - start) * 1000.0);
    lock.lock();
    state->began = end;
    lock.unlock();
    if (kmtlConfig().latency || kmtlDrainOn()) latencyBegin(end - start, paced);
    return done ? VK_SUCCESS : VK_TIMEOUT;
}

// late_frame_start: with a cadence the game's wait above ends a frame or two before the display slot its frame will
// get, and the input it reads then is that old when shown. Each game frame shown on its slot (within 2 ms of the
// cadence after the one before) stretches the wait by late_frame_start's milliseconds, up to three quarters of the
// cadence; one shown later, or not at all, halves it: about one frame in (half the wait / the step) misses its slot.
// The wait settles just short of the frames missing their slots, whatever the CPU, GPU and window server take. The
// cadence must be a whole number of the display's refresh intervals (steady_frame_rate's is), or every frame counts
// as late
static void noteShownForStart(KPresentState& st, uint64_t present, double shownAt, double cadenceMs, double stepMs) {
    if (shownAt <= 0) {
        st.startDelay *= 0.5;
        st.lastShownPresent = 0;  // the next frame shown only starts the count again
        return;
    }
    if (st.lastShownPresent && st.lastShownPresent + 1 == present) {
        double interval = (shownAt - st.lastShown) * 1e3;
        st.startDelay = interval <= cadenceMs + 2.0 ? std::min(st.startDelay + stepMs * 1e-3, cadenceMs * 0.75e-3)
                                                    : st.startDelay * 0.5;
    } else if (st.lastShownPresent) {
        st.startDelay *= 0.5;
    }
    st.startDelay = std::min(st.startDelay, cadenceMs * 0.75e-3);  // the cadence can shrink (another display)
    st.lastShown = shownAt;
    st.lastShownPresent = present;
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

// The swapchain's cadence (steady_frame_rate, frame_cadence): each drawable is shown at least that long after the one
// before (with frame interpolation two are shown per game frame, so half as long), so the frame rate holds one step of
// the display's refresh instead of moving between them
static void presentPaced(KSwapchain* sc, id<MTLCommandBuffer> cb, id<CAMetalDrawable> d, bool interpolated) {
    double ms = sc->cadenceMs;
    if (ms > 0) [cb presentDrawable:d afterMinimumDuration:(interpolated ? ms / 2 : ms) / 1000.0];
    else [cb presentDrawable:d];
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
            // KSA_MTL_FRAME_INTERPOLATION: a frame between the last one and this one goes to the display first
            id<CAMetalDrawable> first = nil;
            if (kmtlConfig().frameInterpolation) {
                [b endEncoding];
                id<MTLTexture> mid = kmtlPresentInterpolate(q->device, cb, img->mtl);
                first = mid ? [sc->surface->layer nextDrawable] : nil;
                if (first) {
                    id<MTLBlitCommandEncoder> copy = [cb blitCommandEncoder];
                    present(copy, mid, first.texture);
                    [copy endEncoding];
                    [first addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, false); }];
                    presentPaced(sc, cb, first, true);
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
            kmtlOnPresent(q->device, sc, img, cb);
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
                bool late = kmtlConfig().lateFrameStart > 0 && !kmtlConfig().frameInterpolation;
                double cadence = late ? sc->cadenceMs : 0;
                double step = kmtlConfig().lateFrameStart;
                if (drawable) [drawable addPresentedHandler:^(id<MTLDrawable> d) {
                    std::lock_guard<std::mutex> lock(state->m);
                    if (cadence > 0) noteShownForStart(*state, n, d.presentedTime, cadence, step);
                    if (KPresentState::Frame* f = findFrame(*state, n)) f->shown = true;
                    state->presented = std::max(state->presented, presentId);
                    state->cv.notify_all();
                }];
            }
            if (drawable) {
                // When the display showed it (0 for an image it never showed)
                [drawable addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, true); }];
                if (kmtlConfig().latency || kmtlDrainOn()) latencyAtPresent(drawable, cb);
                presentPaced(sc, cb, drawable, first != nil);
            }
            [cb commit];
            if (pi->pResults) pi->pResults[i] = r;
            if (r != VK_SUCCESS) result = r;
        }
    }
    return result;
}
