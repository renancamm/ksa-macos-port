// Frame instruments: the frame log, shots and exits, the benchmark's interface (exports.h), frame captures, signposts,
// the GPU's failed command buffers. The display and GPU times they report come from presentation/frametime.mm. Vulkan
// layers cannot run on this driver (it is the loader), so it carries its own.
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#import <CoreImage/CoreImage.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <chrono>
#include <algorithm>
#include <mach/mach.h>
#include <os/signpost.h>

// ---------------------------------------------------------------------------------------------------------------
// Settings
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
static uint64_t g_ingamePresent = 0;
static uint32_t g_frameDraws = 0, g_frameDispatches = 0, g_framePasses = 0, g_frameSubmits = 0;
static FILE* g_frameCsv = nullptr;
static std::chrono::steady_clock::time_point g_start, g_lastPresent, g_ingameTime;
static std::vector<Shot> g_shots;
static size_t g_nextShot = 0;
static ShotUnit g_shotUnit = ShotUnit::Presents;
static double g_exitSeconds = 0;
static std::atomic<int> g_shotsPending{0};

// The benchmark's interface (ksaMtlFrameStats, ksaMtlScreenshot, ksaMtlMarker, ksaMtlCapture, ksaMtlMemory): exports.h
static KsaMtlFrameStats g_lastFrame{};
static std::vector<std::string> g_shotRequests;
// Frame capture: the file, the swapchain whose presents count (a pop-out window's come in the same frame), the presents
// left to capture (0 when none is running), whether one waits to start
static std::string g_capturePath;
static KSwapchain* g_captureSwapchain = nullptr;
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

// Command buffers that failed: after a hang the GPU refuses the process's work, so captures and pass dumps refuse to start
static std::atomic<uint32_t> g_gpuErrors{0};

void kmtlNoteGpuError() {
    if (g_gpuErrors++ == 0 && kmtlConfig().watch) kmtlWatchLine("the GPU failed a command buffer: nothing after this can be dumped");
}

KMTL_EXPORT uint32_t ksaMtlGpuErrors() { return g_gpuErrors; }

KMTL_EXPORT bool ksaMtlCapture(const char* path, uint32_t frames) {
    // After a GPU failure the GPU refuses this process's work: a capture would replay as nonsense
    if (g_capturing || ksaMtlGpuErrors() || ![[MTLCaptureManager sharedCaptureManager] supportsDestination:MTLCaptureDestinationGPUTraceDocument])
        return false;
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_capturePath = path;
    g_captureFrames = std::max(1u, frames);
    g_capturing = true;
    g_captureStart = true;
    return true;
}

KMTL_EXPORT bool ksaMtlCaptureRunning() { return g_capturing; }

KMTL_EXPORT uint32_t ksaMtlMemory(int64_t* out, uint32_t n) {
    const int64_t all[4] = {g_allocatedByType[0].load(), g_allocatedByType[1].load(), g_allocatedByType[2].load(), g_internalBytes.load()};
    uint32_t written = std::min<uint32_t>(n, 4);
    for (uint32_t i = 0; i < written; i++) out[i] = all[i];
    return written;
}

// At a present: starts a requested capture (the next frames' command buffers go into it) or ends a running one
static void captureAtPresent(KDevice* dev, KSwapchain* swapchain) {
    MTLCaptureManager* manager = [MTLCaptureManager sharedCaptureManager];
    if (g_captureStart.exchange(false)) {
        g_captureSwapchain = swapchain;
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
    // With its swapchain gone (a resize, a closed pop-out), any present counts, or the capture would never end
    if ((!g_captureSwapchain || swapchain == g_captureSwapchain) && g_captureFrames && manager.isCapturing &&
        --g_captureFrames == 0) {
        [manager stopCapture];
        KLOG(0, "frame capture written: %s", g_capturePath.c_str());
        g_capturing = false;
    }
}

void kmtlOnSwapchainDestroyed(KSwapchain* swapchain) {
    kmtlPassDumpSwapchainGone(swapchain);
    std::lock_guard<std::mutex> lock(g_frameMutex);
    if (g_captureSwapchain == swapchain) g_captureSwapchain = nullptr;
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
    const PortSettings& cfg = kmtlConfig();
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
    }
    std::sort(g_shots.begin(), g_shots.end(), [](const Shot& a, const Shot& b) { return a.at < b.at; });
    g_exitSeconds = cfg.exitAfterS;
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

static void exitSoon(const char* why) {
    KLOG(0, "%s: exiting", why);
    if (g_frameCsv) fflush(g_frameCsv);
    dispatch_async(dispatch_get_global_queue(0, 0), ^{ usleep(300000); _exit(0); });
}

bool kmtlEncodePNG(uint8_t* rgbx, NSUInteger w, NSUInteger h, const std::string& path) {
    CGColorSpaceRef cs = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    CGContextRef ctx = CGBitmapContextCreate(rgbx, w, h, 8, w * 4, cs, kCGImageAlphaNoneSkipLast);
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
    return ok;
}

void kmtlWritePNG(id<MTLBuffer> buf, NSUInteger w, NSUInteger h, bool bgra, const std::string& path) {
    uint8_t* px = (uint8_t*)buf.contents;
    if (bgra) {
        for (NSUInteger i = 0; i < w * h; i++) std::swap(px[i * 4], px[i * 4 + 2]);
    }
    for (NSUInteger i = 0; i < w * h; i++) px[i * 4 + 3] = 255;
    bool ok = kmtlEncodePNG(px, w, h, path);
    KLOG(0, "screenshot %s: %s", ok ? "written" : "FAILED", path.c_str());
}

// An HDR10 image (PQ in Rec.2020, presentation/hdr.mm) as an SDR PNG: CoreImage converts it to sRGB with the game's HDR UI
// Brightness at white, as macOS shows it; highlights above it clip
static void writeHdrPNG(id<MTLTexture> tex, const std::string& path) {
    constexpr double kPqReferenceNits = 203;  // 1.0 of CoreImage's PQ colour space (BT.2408's reference white)
    static CIContext* context = [CIContext contextWithMTLDevice:tex.device];
    CGColorSpaceRef pq = CGColorSpaceCreateWithName(kCGColorSpaceITUR_2100_PQ);
    CGColorSpaceRef srgb = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    CIImage* image = [CIImage imageWithMTLTexture:tex options:@{kCIImageColorSpace: (__bridge id)pq}];
    image = [image imageByApplyingOrientation:kCGImagePropertyOrientationDownMirrored];  // Metal's rows start at the top
    image = [image imageByApplyingFilter:@"CIExposureAdjust"
                     withInputParameters:@{kCIInputEVKey: @(log2(kPqReferenceNits / kmtlHdrUiBrightness()))}];
    image = [image imageBySettingAlphaOneInExtent:image.extent];  // opaque, as writePNG's
    NSURL* url = [NSURL fileURLWithPath:@(path.c_str())];
    NSError* err = nil;
    bool ok = [context writePNGRepresentationOfImage:image toURL:url format:kCIFormatRGBA8 colorSpace:srgb options:@{}
                                               error:&err];
    CGColorSpaceRelease(pq);
    CGColorSpaceRelease(srgb);
    if (ok) KLOG(0, "screenshot written: %s", path.c_str());
    else KLOG(0, "screenshot FAILED: %s (%s)", path.c_str(), err.localizedDescription.UTF8String);
}

// Screenshots are encoded and written on a queue of their own, not in the completion handler: a queue's handlers run one
// at a time, and a PNG of a 6 MP frame (~170 ms) held the presents behind it from being shown, and the game's wait for
// them timed out. Past a few shots waiting (a film asks faster than they're written, each holding its frame's copy), the
// handler waits for its own: the game slows to the writer's pace, as before, and the order stays
static std::atomic<int> g_shotsWaiting{0};
constexpr int kShotsWaiting = 3;

static void writeShot(void (^write)(void), void (^done)(void)) {
    static dispatch_queue_t q = dispatch_queue_create("ksa-metal.screenshots", DISPATCH_QUEUE_SERIAL);
    void (^run)(void) = ^{
        @autoreleasepool {
            write();
        }
        if (done) done();
        g_shotsWaiting--;
    };
    if (g_shotsWaiting++ >= kShotsWaiting) dispatch_sync(q, run);
    else dispatch_async(q, run);
}

// The path is taken by value: the completion block keeps its own copy (a captured reference would outlive requests)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void)) {
    NSUInteger w = tex.width, h = tex.height;
    if (tex.pixelFormat == MTLPixelFormatRGB10A2Unorm) {
        id<MTLTexture> copy = kmtlPrivateTexture(tex.device, tex.pixelFormat, w, h, MTLTextureUsageShaderRead);
        id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
        if (after) [b waitForFence:after];
        [b copyFromTexture:tex toTexture:copy];
        [b endEncoding];
        [cb addCompletedHandler:^(id<MTLCommandBuffer>) {
            writeShot(^{ writeHdrPNG(copy, path); }, done);
        }];
        return;
    }
    id<MTLBuffer> buf = [tex.device newBufferWithLength:w * h * 4 options:MTLResourceStorageModeShared];
    bool bgra = tex.pixelFormat == MTLPixelFormatBGRA8Unorm || tex.pixelFormat == MTLPixelFormatBGRA8Unorm_sRGB;
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    if (after) [b waitForFence:after];
    [b copyFromTexture:tex sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
              toBuffer:buf destinationOffset:0 destinationBytesPerRow:w * 4 destinationBytesPerImage:w * h * 4];
    [b endEncoding];
    [cb addCompletedHandler:^(id<MTLCommandBuffer>) {
        writeShot(^{ kmtlWritePNG(buf, w, h, bgra, path); }, done);
    }];
}

// Screenshots encoded so far: a present that took one (its frame's GPU time holds the copy) is left out of dynamic
// resolution's samples (presentation/pacing.mm)
static std::atomic<uint64_t> g_shotsTaken{0};
uint64_t kmtlShotsTaken() { return g_shotsTaken.load(std::memory_order_relaxed); }

static void takeShot(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb, const std::string& path, bool last) {
    g_shotsPending++;
    g_shotsTaken++;
    bool exitAfter = last && kmtlConfig().exitAfterShots;
    // A second encoder after the present's blit (which updated the chain fence)
    kmtlSavePNG(cb, image->mtl, dev->queue->chain, path, ^{
        if (--g_shotsPending == 0 && exitAfter) exitSoon("KSA_MTL_EXIT_AFTER_SHOTS");
    });
}

void kmtlOnPresent(KDevice* dev, KSwapchain* swapchain, KImage* image, id<MTLCommandBuffer> cb, uint64_t presents) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_frameMutex);
    auto now = std::chrono::steady_clock::now();
    double ms = std::chrono::duration<double, std::milli>(now - g_lastPresent).count();
    g_lastPresent = now;
    if (!g_ingamePresent && g_frameDraws + g_frameDispatches > 50) {
        g_ingamePresent = presents;
        g_ingameTime = now;
        KLOG(0, "in-game at present %llu, %.1f s", (unsigned long long)presents,
             std::chrono::duration<double>(now - g_start).count());
        kmtlReportPipelineTimes();
    }
    double gpu = kmtlTakePresentGpuTime() * 1e3;
    g_lastFrame = {presents, ms, gpu, g_frameDraws, g_frameDispatches, g_framePasses, g_frameSubmits,
                   (int64_t)(g_allocatedBytes.load() >> 20)};
    sampleSystem(g_lastFrame);
    kmtlPipelineCounts(&g_lastFrame.shadersCompiled, &g_lastFrame.pipelinesCreated);
    g_lastFrame.drawableWidth = (uint32_t)image->mtl.width;
    g_lastFrame.drawableHeight = (uint32_t)image->mtl.height;
    g_lastFrame.displayMs = kmtlDisplayMs();
    if (kmtlDrainOn()) kmtlDrainPresent(CACurrentMediaTime(), g_lastFrame);
    if (kmtlConfig().frameInterpolation && presents % 600 == 0) {
        static uint64_t lastShown = 0, lastPresents = 0;
        static auto lastTime = now;
        double secs = std::chrono::duration<double>(now - lastTime).count();
        uint64_t shown = kmtlShownCount();
        if (secs > 0 && lastPresents)
            KLOG(1, "frame interpolation: game %.1f fps, display %.1f fps", (presents - lastPresents) / secs,
                 (shown - lastShown) / secs);
        lastShown = shown;
        lastPresents = presents;
        lastTime = now;
    }
    if (g_capturing) captureAtPresent(dev, swapchain);
    kmtlPassDumpAtPresent(dev->queue, swapchain, cb);
    kmtlWatchAtPresent(dev->queue, image, cb);
    if (g_frameCsv) {
        fprintf(g_frameCsv, "%llu,%.3f,%u,%u,%u,%u,%lld,%.3f\n", (unsigned long long)presents, ms, g_frameDraws, g_frameDispatches,
                g_framePasses, g_frameSubmits, (long long)g_lastFrame.allocatedMb, gpu);
        if (presents % 60 == 0) fflush(g_frameCsv);
    }
    if (g_logLevel >= 1 && (presents <= 5 || presents % 500 == 0)) {


        KLOG(1, "present %llu: %.2f ms, %u draws, %u dispatches, %u passes, %u submits; memory MB: private %lld, "
             "shared %lld, shared cached %lld, internal %lld", (unsigned long long)presents, ms, g_frameDraws,
             g_frameDispatches, g_framePasses, g_frameSubmits, (long long)(g_allocatedByType[0] >> 20),
             (long long)(g_allocatedByType[1] >> 20), (long long)(g_allocatedByType[2] >> 20), (long long)(g_internalBytes >> 20));
    }
    g_frameDraws = g_frameDispatches = g_framePasses = g_frameSubmits = 0;

    while (g_nextShot < g_shots.size()) {
        double since;
        if (g_shotUnit == ShotUnit::Presents) since = (double)presents;
        else if (!g_ingamePresent) break;
        else if (g_shotUnit == ShotUnit::InGameFrames) since = (double)(presents - g_ingamePresent);
        else since = std::chrono::duration<double>(now - g_ingameTime).count();
        if (since < g_shots[g_nextShot].at) break;
        bool last = g_nextShot + 1 == g_shots.size();
        takeShot(dev, image, cb, g_shots[g_nextShot].path, last);
        g_nextShot++;
    }
    for (const std::string& path : g_shotRequests) takeShot(dev, image, cb, path, false);
    g_shotRequests.clear();
    if (kmtlConfig().exitAfter && presents == (uint64_t)kmtlConfig().exitAfter) exitSoon("KSA_MTL_EXIT_AFTER reached");
    if (g_exitSeconds > 0 && std::chrono::duration<double>(now - g_start).count() >= g_exitSeconds) {
        g_exitSeconds = 0;
        exitSoon("KSA_MTL_EXIT_AFTER_S reached");
    }
}
