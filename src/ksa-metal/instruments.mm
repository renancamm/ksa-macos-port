// Frame instruments: the frame log, shots and exits, the benchmark's interface (ksaMtl*), frame captures, signposts,
// and the display and GPU times measured at each present (swapchain.mm reports them). Vulkan layers cannot run on
// this driver (it is the loader), so it carries its own.
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <chrono>
#include <algorithm>
#include <mach/mach.h>
#include <os/signpost.h>

// The drawables the display showed, interpolated ones too; when it last showed a game frame (presentedTime) and the
// time between the last two
static std::atomic<uint64_t> g_displayed{0};
static std::atomic<double> g_lastPresented{0}, g_displayMs{0};

void kmtlNoteShown(double t, bool timed) {
    g_displayed++;
    if (!timed) return;
    double last = g_lastPresented.exchange(t);
    if (last > 0) g_displayMs = (t - last) * 1e3;
}

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
// Read without the lock by game threads (kmtlPresentCount: the throttle, stall logs, the GPU profiler)
static std::atomic<uint64_t> g_presents{0};
static uint64_t g_ingamePresent = 0;
uint64_t kmtlPresentCount() { return g_presents; }
static uint32_t g_frameDraws = 0, g_frameDispatches = 0, g_framePasses = 0, g_frameSubmits = 0;
static FILE* g_frameCsv = nullptr;
static std::chrono::steady_clock::time_point g_start, g_lastPresent, g_ingameTime;
static std::vector<Shot> g_shots;
static size_t g_nextShot = 0;
static ShotUnit g_shotUnit = ShotUnit::Presents;
static double g_exitSeconds = 0;
static std::atomic<int> g_shotsPending{0};
// GPU busy time: union of command buffer GPU intervals, in completion order, summed until the next present (so the
// column lags the CPU interval by the frames in flight)
static std::mutex g_gpuMutex;
static double g_gpuBusy = 0, g_gpuLastEnd = 0;
static double g_gpuBusyFrame = 0;  // the same, since the last present completed: one frame's GPU time

// Interface for the benchmark's director (src/benchmark), which calls these through P/Invoke:
//   ksaMtlFrameStats(out, size)  the latest present's numbers (the KSA_MTL_FRAMES columns); returns the bytes written
//   ksaMtlScreenshot(path)       PNG of the next presented image
//   ksaMtlMarker(name, begin)    begins or ends a signpost interval "part" with that name (Points of Interest in
//                                Instruments), so a trace comes split into the benchmark's parts
//   ksaMtlCapture(path, frames)  Xcode GPU frame capture of the next frames into path (.gputrace); false when Metal
//                                does not allow it (the game needs MTL_CAPTURE_ENABLED=1); ksaMtlCapturing() is true
//                                until the file is written
// The caller passes the size of the struct it knows and gets back how much was filled; the benchmark's director takes
// the stats only when that is all of its struct, so a director and a driver from different builds don't misread them.
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
    }
    std::sort(g_shots.begin(), g_shots.end(), [](const Shot& a, const Shot& b) { return a.at < b.at; });
    g_exitSeconds = cfg.exitAfterSeconds;
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

// The path is taken by value: the completion block keeps its own copy (a captured reference would outlive requests)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void)) {
    NSUInteger w = tex.width, h = tex.height;
    id<MTLBuffer> buf = [tex.device newBufferWithLength:w * h * 4 options:MTLResourceStorageModeShared];
    bool bgra = tex.pixelFormat == MTLPixelFormatBGRA8Unorm || tex.pixelFormat == MTLPixelFormatBGRA8Unorm_sRGB;
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    if (after) [b waitForFence:after];
    [b copyFromTexture:tex sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
              toBuffer:buf destinationOffset:0 destinationBytesPerRow:w * 4 destinationBytesPerImage:w * h * 4];
    [b endEncoding];
    [cb addCompletedHandler:^(id<MTLCommandBuffer>) {
        writePNG(buf, w, h, bgra, path);
        if (done) done();
    }];
}

static void takeShot(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb, const std::string& path, bool last) {
    g_shotsPending++;
    bool exitAfter = last && kmtlConfig().exitAfterShots;
    // A second encoder after the present's blit (which updated the chain fence)
    kmtlSavePNG(cb, image->mtl, dev->queue->chain, path, ^{
        if (--g_shotsPending == 0 && exitAfter) exitSoon("KSA_MTL_EXIT_AFTER_SHOTS");
    });
}

void kmtlOnPresent(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_frameMutex);
    g_presents++;
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
    if (kmtlConfig().exitAfter && g_presents == (uint64_t)kmtlConfig().exitAfter) exitSoon("KSA_MTL_EXIT_AFTER reached");
    if (g_exitSeconds > 0 && std::chrono::duration<double>(now - g_start).count() >= g_exitSeconds) {
        g_exitSeconds = 0;
        exitSoon("KSA_MTL_EXIT_AFTER_S reached");
    }
}
