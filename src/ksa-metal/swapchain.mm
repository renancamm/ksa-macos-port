// Swapchain over the GLFW window's CAMetalLayer, presentation, and the frame instruments (Vulkan layers cannot run
// on this driver: it is the loader).
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <chrono>
#include <algorithm>
#include <mach/mach.h>
#include <os/signpost.h>

// When the display last showed one of the images (presentedTime), and the time between the last two it showed
static std::atomic<double> g_lastPresented{0}, g_displayMs{0};

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
    sc->device = dev;
    sc->surface = (KSurface*)ci->surface;
    sc->extent = ci->imageExtent;
    sc->format = ci->imageFormat;
    sc->presentMode = ci->presentMode;
    CAMetalLayer* layer = sc->surface->layer;
    MTLPixelFormat pf = kmtlPixelFormat(ci->imageFormat);
    auto configure = ^{
        layer.device = dev->mtl;
        layer.pixelFormat = pf;
        layer.framebufferOnly = NO;
        // KSA_MTL_RETINA: the images are in points, the drawable at the display's scale (upscaled at present)
        CGFloat scale = kmtlConfig().retina ? layer.contentsScale : 1.0;
        layer.drawableSize = CGSizeMake(ci->imageExtent.width * scale, ci->imageExtent.height * scale);
        layer.maximumDrawableCount = 3;
        layer.displaySyncEnabled = ci->presentMode == VK_PRESENT_MODE_FIFO_KHR || ci->presentMode == VK_PRESENT_MODE_FIFO_RELAXED_KHR;
        layer.opaque = YES;
    };
    // CAMetalLayer drawable properties may be set from any thread; hopping to the main thread could deadlock
    configure();
    uint32_t count = std::max(2u, std::min(3u, ci->minImageCount));
    for (uint32_t i = 0; i < count; i++) {
        KImage* img = kmtlCreateSwapchainImage(dev, ci->imageFormat, ci->imageExtent.width, ci->imageExtent.height);
        if (!img) return VK_ERROR_OUT_OF_DEVICE_MEMORY;
        img->mtl.label = [NSString stringWithFormat:@"swapchain %u", i];
        sc->images.push_back(img);
    }
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
    uint32_t n = (uint32_t)sc->images.size();
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    for (uint32_t i = 0; i < c; i++) out[i] = (VkImage)sc->images[i];
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
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

// Present pacing. With V-Sync the game waits for the present before last before it reads input. If a present counted
// only once the display showed it, that wait would also cover the window server's delay (about 21 ms in a window, 10
// in fullscreen) and leave the GPU idle whenever a frame takes less than that delay plus the CPU's part. So a present
// also counts once its GPU work has ended, the present before it was shown, and the GPU is about to need the next
// frame: the previous frame's predicted GPU end, less the CPU's part and a 2 ms margin. Frames then start when the GPU
// can take them instead of queueing up. KSA_MTL_PRESENT_PACING=0: the display only (Vulkan's meaning)
double kmtlTakeFrameGpuTime();

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

static std::atomic<uint64_t> g_displayed{0};  // drawables the display showed, interpolated ones too

// The image into a drawable: copied, or upscaled with MetalFX when the drawable is larger (KSA_MTL_RETINA)
static bool present(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> image, id<MTLTexture> drawable) {
    if (drawable.width != image.width || drawable.height != image.height)
        return kmtlPresentUpscale(dev, cb, image, drawable);
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b copyFromTexture:image toTexture:drawable];
    [b endEncoding];
    return true;
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
                if (first && present(q->device, cb, mid, first.texture)) {
                    [first addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) g_displayed++; }];
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
            if (drawable && (drawable.texture.width != img->mtl.width || drawable.texture.height != img->mtl.height)) {
                [b endEncoding];
                if (!kmtlPresentUpscale(q->device, cb, img->mtl, drawable.texture)) drawable = nil;
                b = [cb blitCommandEncoder];
                [b waitForFence:q->chain];
            } else if (drawable) {
                id<MTLTexture> t = drawable.texture;
                NSUInteger w = std::min(t.width, img->mtl.width), h = std::min(t.height, img->mtl.height);
                [b copyFromTexture:img->mtl sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
                        sourceSize:MTLSizeMake(w, h, 1) toTexture:t destinationSlice:0 destinationLevel:0
                 destinationOrigin:MTLOriginMake(0, 0, 0)];
            }
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
                [drawable addPresentedHandler:^(id<MTLDrawable> d) {
                    double t = d.presentedTime;
                    if (t <= 0) return;
                    g_displayed++;
                    double last = g_lastPresented.exchange(t);
                    if (last > 0) g_displayMs = (t - last) * 1e3;
                }];
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

// ---------------------------------------------------------------------------------------------------------------
// Frame instruments (layers cannot run on this driver, so it carries its own)
//
//   KSA_MTL_FRAMES=<csv>             per-present CPU interval and work counts
//   KSA_MTL_SHOTS=<n,...>            PNG of the presented image at these points, as KSA_MTL_SHOT_DIR/shot_<n>.png
//   KSA_MTL_SHOT_UNIT=<unit>         presents (default), or frames or seconds since the first in-game frame
//   KSA_MTL_EXIT_AFTER=<n>           exit after n presents
//   KSA_MTL_EXIT_AFTER_SHOTS=1       exit once the last shot is written
//   KSA_MTL_EXIT_AFTER_S=<s>         exit s seconds after start

enum class ShotUnit { Presents, InGameFrames, InGameSeconds };
struct Shot { double at; std::string path; };

static std::mutex g_frameMutex;
static uint64_t g_presents = 0, g_ingamePresent = 0;
uint64_t kmtlPresentCount() { return g_presents; }
static uint32_t g_frameDraws = 0, g_frameDispatches = 0, g_framePasses = 0, g_frameSubmits = 0;
static FILE* g_frameCsv = nullptr;
static std::chrono::steady_clock::time_point g_start, g_lastPresent, g_ingameTime;
static std::vector<Shot> g_shots;
static size_t g_nextShot = 0;
static ShotUnit g_shotUnit = ShotUnit::Presents;
static bool g_exitAfterShots = false;
static double g_exitSeconds = 0;
static int g_exitAfter = 0;
static std::atomic<int> g_shotsPending{0};
// GPU busy time: union of command buffer GPU intervals, in completion order, summed until the next present (so the
// column lags the CPU interval by the frames in flight)
static std::mutex g_gpuMutex;
static double g_gpuBusy = 0, g_gpuLastEnd = 0;
static double g_gpuBusyFrame = 0;  // the same, since the last present completed: one frame's GPU time

// Interface for the benchmark mod (src/benchmark), which calls these through P/Invoke:
//   ksaMtlFrameStats(out, size)  the latest present's numbers (the KSA_MTL_FRAMES columns); returns the bytes written
//   ksaMtlScreenshot(path)       PNG of the next presented image
//   ksaMtlMarker(name, begin)    begins or ends a signpost interval "part" with that name (Points of Interest in
//                                Instruments), so a trace comes split into the benchmark's parts
//   ksaMtlCapture(path, frames)  Xcode GPU frame capture of the next frames into path (.gputrace); false when Metal
//                                does not allow it (the game needs MTL_CAPTURE_ENABLED=1); ksaMtlCapturing() is true
//                                until the file is written
// Fields are only ever appended, so a mod built for an older layout still reads the ones it knows.
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
static KsaMtlFrameStats g_lastFrame{};
static std::vector<std::string> g_shotRequests;
// Frame capture: the file, the presents left to capture (0 when none is running), whether one waits to start
static std::string g_capturePath;
static std::atomic<uint32_t> g_captureFrames{0};
static std::atomic<bool> g_captureStart{false}, g_capturing{false};

KMTL_EXPORT uint32_t ksaMtlFrameStats(KsaMtlFrameStats* out, uint32_t size) {
    std::lock_guard<std::mutex> lock(g_frameMutex);
    uint32_t n = std::min<uint32_t>(size, sizeof(KsaMtlFrameStats));
    memcpy(out, &g_lastFrame, n);
    return n;
}

KMTL_EXPORT void ksaMtlScreenshot(const char* path) {
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_shotRequests.push_back(path);
}

static os_log_t benchmarkLog() {
    static os_log_t log = os_log_create("io.github.ksa-macos-port", OS_LOG_CATEGORY_POINTS_OF_INTEREST);
    return log;
}

KMTL_EXPORT void ksaMtlMarker(const char* name, bool begin) {
    // Parts never overlap: one interval at a time
    static os_signpost_id_t open = OS_SIGNPOST_ID_NULL;
    os_log_t log = benchmarkLog();
    if (begin) {
        open = os_signpost_id_generate(log);
        os_signpost_interval_begin(log, open, "part", "%{public}s", name);
    } else if (open != OS_SIGNPOST_ID_NULL) {
        os_signpost_interval_end(log, open, "part", "%{public}s", name);
        open = OS_SIGNPOST_ID_NULL;
    }
}

KMTL_EXPORT bool ksaMtlCapture(const char* path, uint32_t frames) {
    if (g_capturing || ![[MTLCaptureManager sharedCaptureManager] supportsDestination:MTLCaptureDestinationGPUTraceDocument])
        return false;
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_capturePath = path;
    g_captureFrames = std::max(1u, frames);
    g_capturing = true;
    g_captureStart = true;
    return true;
}

KMTL_EXPORT bool ksaMtlCapturing() { return g_capturing; }

// At a present: starts a requested capture (the next frames' command buffers go into it) or ends a running one
static void captureAtPresent(KDevice* dev) {
    MTLCaptureManager* manager = [MTLCaptureManager sharedCaptureManager];
    if (g_captureStart.exchange(false)) {
        MTLCaptureDescriptor* d = [MTLCaptureDescriptor new];
        d.captureObject = dev->mtl;
        d.destination = MTLCaptureDestinationGPUTraceDocument;
        d.outputURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:g_capturePath.c_str()]];
        NSError* e = nil;
        if ([manager startCaptureWithDescriptor:d error:&e]) {
            KLOG(0, "frame capture started: %s", g_capturePath.c_str());
        } else {
            KERR("frame capture failed to start: %s", e.localizedDescription.UTF8String);
            g_captureFrames = 0;
            g_capturing = false;
        }
        return;
    }
    if (g_captureFrames && manager.isCapturing && --g_captureFrames == 0) {
        [manager stopCapture];
        KLOG(0, "frame capture written: %s", g_capturePath.c_str());
        g_capturing = false;
    }
}

// Thermal state and memory footprint, sampled every 30 presents (a system call each)
static void sampleSystem(KsaMtlFrameStats& f) {
    static int32_t thermal = 0;
    static int64_t footprint = 0;
    if (f.present % 30 == 1) {
        thermal = (int32_t)[NSProcessInfo processInfo].thermalState;
        task_vm_info_data_t info;
        mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
        if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) == KERN_SUCCESS)
            footprint = (int64_t)(info.phys_footprint >> 20);
    }
    f.thermalState = thermal;
    f.footprintMb = footprint;
}

void kmtlNoteGpuTime(id<MTLCommandBuffer> cb) {
    double s = cb.GPUStartTime, e = cb.GPUEndTime;
    if (e <= s) return;
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    double busy = e - std::max(s, std::min(e, g_gpuLastEnd));
    g_gpuBusy += busy;
    g_gpuBusyFrame += busy;
    g_gpuLastEnd = std::max(g_gpuLastEnd, e);
}

double kmtlTakeFrameGpuTime() {
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    return std::exchange(g_gpuBusyFrame, 0.0);
}

static std::vector<double> parseList(const char* s) {
    std::vector<double> v;
    std::string str(s);
    size_t pos = 0;
    while (pos < str.size()) {
        size_t comma = str.find(',', pos);
        v.push_back(atof(str.substr(pos, comma - pos).c_str()));
        if (comma == std::string::npos) break;
        pos = comma + 1;
    }
    return v;
}

static void initInstruments() {
    const KConfig& cfg = kmtlConfig();
    g_start = g_lastPresent = std::chrono::steady_clock::now();
    if (cfg.frames) {
        g_frameCsv = fopen(cfg.frames, "w");
        if (g_frameCsv) fprintf(g_frameCsv, "present,ms,draws,dispatches,passes,submits,allocated_mb,gpu_ms\n");
    }
    if (cfg.shots) {
        const char* unit = cfg.shotUnit;
        g_shotUnit = unit && !strcmp(unit, "frames") ? ShotUnit::InGameFrames
                   : unit && !strcmp(unit, "seconds") ? ShotUnit::InGameSeconds : ShotUnit::Presents;
        for (double v : parseList(cfg.shots)) {
            char name[64];
            snprintf(name, sizeof(name), "/shot_%.10g.png", v);
            g_shots.push_back({v, std::string(cfg.shotDir ? cfg.shotDir : ".") + name});
        }
        g_exitAfterShots = cfg.exitAfterShots;
    }
    std::sort(g_shots.begin(), g_shots.end(), [](const Shot& a, const Shot& b) { return a.at < b.at; });
    g_exitSeconds = cfg.exitAfterSeconds;
    g_exitAfter = cfg.exitAfter;
}

static void ensureInstruments() {
    static std::once_flag once;
    std::call_once(once, initInstruments);
}

void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_frameDraws += draws;
    g_frameDispatches += dispatches;
    g_framePasses += passes;
    g_frameSubmits++;
}

void kmtlReportPipelineTimes();

static void exitSoon(const char* why) {
    KLOG(0, "%s: exiting", why);
    if (g_frameCsv) fflush(g_frameCsv);
    dispatch_async(dispatch_get_global_queue(0, 0), ^{ usleep(300000); _exit(0); });
}

static void writePNG(id<MTLBuffer> buf, NSUInteger w, NSUInteger h, bool bgra, const std::string& path) {
    uint8_t* px = (uint8_t*)buf.contents;
    if (bgra) {
        for (NSUInteger i = 0; i < w * h; i++) std::swap(px[i * 4], px[i * 4 + 2]);
    }
    for (NSUInteger i = 0; i < w * h; i++) px[i * 4 + 3] = 255;
    CGColorSpaceRef cs = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    CGContextRef ctx = CGBitmapContextCreate(px, w, h, 8, w * 4, cs, kCGImageAlphaNoneSkipLast);
    CGImageRef image = CGBitmapContextCreateImage(ctx);
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(nullptr, (const UInt8*)path.c_str(), path.size(), false);
    CGImageDestinationRef dest = CGImageDestinationCreateWithURL(url, (__bridge CFStringRef)UTTypePNG.identifier, 1, nullptr);
    bool ok = false;
    if (dest) {
        CGImageDestinationAddImage(dest, image, nullptr);
        ok = CGImageDestinationFinalize(dest);
        CFRelease(dest);
    }
    CFRelease(url);
    CGImageRelease(image);
    CGContextRelease(ctx);
    CGColorSpaceRelease(cs);
    KLOG(0, "screenshot %s: %s", ok ? "written" : "FAILED", path.c_str());
}

void kmtlWritePNG(id<MTLBuffer> buf, NSUInteger w, NSUInteger h, bool bgra, std::string path) { writePNG(buf, w, h, bgra, path); }

// The path is taken by value: the completion block keeps its own copy (a captured reference would outlive requests)
static void takeShot(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb, std::string path, bool last) {
    NSUInteger w = image->mtl.width, h = image->mtl.height;
    id<MTLBuffer> buf = [dev->mtl newBufferWithLength:w * h * 4 options:MTLResourceStorageModeShared];
    bool bgra = image->pixelFormat == MTLPixelFormatBGRA8Unorm || image->pixelFormat == MTLPixelFormatBGRA8Unorm_sRGB;
    // A second encoder after the present's blit (which updated the chain fence)
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b waitForFence:dev->queue->chain];
    [b copyFromTexture:image->mtl sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
              toBuffer:buf destinationOffset:0 destinationBytesPerRow:w * 4 destinationBytesPerImage:w * h * 4];
    [b endEncoding];
    g_shotsPending++;
    bool exitAfter = last && g_exitAfterShots;
    [cb addCompletedHandler:^(id<MTLCommandBuffer>) {
        writePNG(buf, w, h, bgra, path);
        if (--g_shotsPending == 0 && exitAfter) exitSoon("KSA_MTL_EXIT_AFTER_SHOTS");
    }];
}

void kmtlOnPresent(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_presents++;
    kmtlProfPresent();
    auto now = std::chrono::steady_clock::now();
    double ms = std::chrono::duration<double, std::milli>(now - g_lastPresent).count();
    g_lastPresent = now;
    if (!g_ingamePresent && g_frameDraws + g_frameDispatches > 50) {
        g_ingamePresent = g_presents;
        g_ingameTime = now;
        KLOG(0, "in-game at present %llu, %.1f s", (unsigned long long)g_presents,
             std::chrono::duration<double>(now - g_start).count());
        kmtlReportPipelineTimes();
    }
    double gpu;
    {
        std::lock_guard<std::mutex> lock(g_gpuMutex);
        gpu = g_gpuBusy * 1e3;
        g_gpuBusy = 0;
    }
    g_lastFrame = {g_presents, ms, gpu, g_frameDraws, g_frameDispatches, g_framePasses, g_frameSubmits,
                   (int64_t)(g_allocatedBytes.load() >> 20)};
    sampleSystem(g_lastFrame);
    kmtlPipelineCounts(&g_lastFrame.shadersCompiled, &g_lastFrame.pipelinesCreated);
    g_lastFrame.drawableWidth = (uint32_t)image->mtl.width;
    g_lastFrame.drawableHeight = (uint32_t)image->mtl.height;
    g_lastFrame.displayMs = g_displayMs.load();
    if (kmtlConfig().fxInterpolate && g_presents % 600 == 0) {
        static uint64_t lastShown = 0, lastPresents = 0;
        static auto lastTime = now;
        double secs = std::chrono::duration<double>(now - lastTime).count();
        uint64_t shown = g_displayed.load();
        if (secs > 0 && lastPresents)
            KLOG(1, "frame interpolation: game %.1f fps, display %.1f fps", (g_presents - lastPresents) / secs,
                 (shown - lastShown) / secs);
        lastShown = shown;
        lastPresents = g_presents;
        lastTime = now;
    }
    if (g_capturing) captureAtPresent(dev);
    if (g_frameCsv) {
        fprintf(g_frameCsv, "%llu,%.3f,%u,%u,%u,%u,%lld,%.3f\n", (unsigned long long)g_presents, ms, g_frameDraws, g_frameDispatches,
                g_framePasses, g_frameSubmits, (long long)g_lastFrame.allocatedMb, gpu);
        if (g_presents % 60 == 0) fflush(g_frameCsv);
    }
    if (g_logLevel >= 1 && (g_presents <= 5 || g_presents % 500 == 0)) {


        KLOG(1, "present %llu: %.2f ms, %u draws, %u dispatches, %u passes, %u submits; memory MB: private %lld, "
             "shared %lld, shared cached %lld, internal %lld", (unsigned long long)g_presents, ms, g_frameDraws,
             g_frameDispatches, g_framePasses, g_frameSubmits, (long long)(g_allocatedByType[0] >> 20),
             (long long)(g_allocatedByType[1] >> 20), (long long)(g_allocatedByType[2] >> 20), (long long)(g_internalBytes >> 20));
    }
    g_frameDraws = g_frameDispatches = g_framePasses = g_frameSubmits = 0;

    while (g_nextShot < g_shots.size()) {
        double since;
        if (g_shotUnit == ShotUnit::Presents) since = (double)g_presents;
        else if (!g_ingamePresent) break;
        else if (g_shotUnit == ShotUnit::InGameFrames) since = (double)(g_presents - g_ingamePresent);
        else since = std::chrono::duration<double>(now - g_ingameTime).count();
        if (since < g_shots[g_nextShot].at) break;
        bool last = g_nextShot + 1 == g_shots.size();
        takeShot(dev, image, cb, g_shots[g_nextShot].path, last);
        g_nextShot++;
    }
    for (const std::string& path : g_shotRequests) takeShot(dev, image, cb, path, false);
    g_shotRequests.clear();
    if (g_exitAfter && g_presents == (uint64_t)g_exitAfter) exitSoon("KSA_MTL_EXIT_AFTER reached");
    if (g_exitSeconds > 0 && std::chrono::duration<double>(now - g_start).count() >= g_exitSeconds) {
        g_exitSeconds = 0;
        exitSoon("KSA_MTL_EXIT_AFTER_S reached");
    }
}
