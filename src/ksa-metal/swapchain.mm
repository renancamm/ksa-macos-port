// Swapchain over the GLFW window's CAMetalLayer, presentation, and the frame instruments (Vulkan layers cannot run
// on this driver: it is the loader).
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <chrono>
#include <algorithm>

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
        layer.drawableSize = CGSizeMake(ci->imageExtent.width, ci->imageExtent.height);
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

VKFN(VkResult) vkWaitForPresentKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t presentId, uint64_t timeout) {
    KTRACE_FIRST();
    std::shared_ptr<KPresentState> state = ((KSwapchain*)swapchain)->presentState;
    std::unique_lock<std::mutex> lock(state->m);
    auto deadline = std::chrono::steady_clock::now() +
        std::chrono::nanoseconds(timeout > (uint64_t)INT64_MAX / 2 ? (uint64_t)INT64_MAX / 2 : timeout);
    return state->cv.wait_until(lock, deadline, [&] { return state->presented >= presentId; }) ? VK_SUCCESS : VK_TIMEOUT;
}

VKFN(VkResult) vkAcquireNextImage2KHR(VkDevice device, const VkAcquireNextImageInfoKHR* info, uint32_t* index) {
    return vkAcquireNextImageKHR(device, info->swapchain, info->timeout, info->semaphore, info->fence, index);
}

VKFN(VkResult) vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* pi) {
    KTRACE_FIRST();
    auto* q = (KQueue*)queue;
    std::lock_guard<std::mutex> lock(q->submitMutex);
    auto* ids = (const VkPresentIdKHR*)findNext(pi->pNext, VK_STRUCTURE_TYPE_PRESENT_ID_KHR);
    VkResult result = VK_SUCCESS;
    for (uint32_t i = 0; i < pi->swapchainCount; i++) {
        @autoreleasepool {
            auto* sc = (KSwapchain*)pi->pSwapchains[i];
            VkResult r = outdated(sc) ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
            KImage* img = sc->images[pi->pImageIndices[i]];
            id<CAMetalDrawable> drawable = [sc->surface->layer nextDrawable];
            id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
            cb.label = @"present";
            id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
            [b waitForFence:q->chain];
            if (drawable) {
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
            uint64_t presentId = ids && ids->pPresentIds && i < ids->swapchainCount ? ids->pPresentIds[i] : 0;
            if (presentId) {
                std::shared_ptr<KPresentState> state = sc->presentState;
                if (drawable) [drawable addPresentedHandler:^(id<MTLDrawable>) { state->mark(presentId); }];
                else [cb addCompletedHandler:^(id<MTLCommandBuffer>) { state->mark(presentId); }];
            }
            if (drawable) [cb presentDrawable:drawable];
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

void kmtlNoteGpuTime(id<MTLCommandBuffer> cb) {
    double s = cb.GPUStartTime, e = cb.GPUEndTime;
    if (e <= s) return;
    std::lock_guard<std::mutex> lock(g_gpuMutex);
    g_gpuBusy += e - std::max(s, std::min(e, g_gpuLastEnd));
    g_gpuLastEnd = std::max(g_gpuLastEnd, e);
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
    g_start = g_lastPresent = std::chrono::steady_clock::now();
    if (const char* f = kmtlEnv("KSA_MTL_FRAMES")) {
        g_frameCsv = fopen(f, "w");
        if (g_frameCsv) fprintf(g_frameCsv, "present,ms,draws,dispatches,passes,submits,allocated_mb,gpu_ms\n");
    }
    if (const char* s = kmtlEnv("KSA_MTL_SHOTS")) {
        const char* dir = kmtlEnv("KSA_MTL_SHOT_DIR");
        const char* unit = kmtlEnv("KSA_MTL_SHOT_UNIT");
        g_shotUnit = unit && !strcmp(unit, "frames") ? ShotUnit::InGameFrames
                   : unit && !strcmp(unit, "seconds") ? ShotUnit::InGameSeconds : ShotUnit::Presents;
        for (double v : parseList(s)) {
            char name[64];
            snprintf(name, sizeof(name), "/shot_%.10g.png", v);
            g_shots.push_back({v, std::string(dir ? dir : ".") + name});
        }
        g_exitAfterShots = kmtlEnvInt("KSA_MTL_EXIT_AFTER_SHOTS", 0) != 0;
    }
    std::sort(g_shots.begin(), g_shots.end(), [](const Shot& a, const Shot& b) { return a.at < b.at; });
    if (const char* e = kmtlEnv("KSA_MTL_EXIT_AFTER_S")) g_exitSeconds = atof(e);
    g_exitAfter = kmtlEnvInt("KSA_MTL_EXIT_AFTER", 0);
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
    CGImageDestinationAddImage(dest, image, nullptr);
    bool ok = CGImageDestinationFinalize(dest);
    CFRelease(dest);
    CFRelease(url);
    CGImageRelease(image);
    CGContextRelease(ctx);
    CGColorSpaceRelease(cs);
    KLOG(0, "screenshot %s: %s", ok ? "written" : "FAILED", path.c_str());
}

static void takeShot(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb, const std::string& path, bool last) {
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
    if (g_frameCsv) {
        double gpu;
        {
            std::lock_guard<std::mutex> lock(g_gpuMutex);
            gpu = g_gpuBusy * 1e3;
            g_gpuBusy = 0;
        }
        fprintf(g_frameCsv, "%llu,%.3f,%u,%u,%u,%u,%lld,%.3f\n", (unsigned long long)g_presents, ms, g_frameDraws, g_frameDispatches,
                g_framePasses, g_frameSubmits, (long long)(g_allocatedBytes.load() >> 20), gpu);
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
    if (g_exitAfter && g_presents == (uint64_t)g_exitAfter) exitSoon("KSA_MTL_EXIT_AFTER reached");
    if (g_exitSeconds > 0 && std::chrono::duration<double>(now - g_start).count() >= g_exitSeconds) {
        g_exitSeconds = 0;
        exitSoon("KSA_MTL_EXIT_AFTER_S reached");
    }
}
