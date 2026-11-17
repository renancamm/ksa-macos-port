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

namespace {
struct InstrumentState {
    std::mutex m;  // the fields up to the atomics
    // The frame log: the first in-game present, the work counted since the last present, the CSV, the times
    uint64_t ingamePresent = 0;
    uint32_t draws = 0, dispatches = 0, passes = 0, submits = 0;
    FILE* frameCsv = nullptr;
    std::chrono::steady_clock::time_point start, lastPresent, ingameTime;
    // Shots and exits (KSA_MTL_SHOTS, KSA_MTL_EXIT_AFTER*)
    std::vector<Shot> shots;
    size_t nextShot = 0;
    ShotUnit shotUnit = ShotUnit::Presents;
    double exitSeconds = 0;
    // The benchmark's interface (ksaMtlFrameStats, ksaMtlScreenshot, ksaMtlMarker, ksaMtlCapture, ksaMtlMemory):
    // exports.h
    KsaMtlFrameStats lastFrame{};
    std::vector<std::string> shotRequests;
    // Frame capture: the file, the swapchain whose presents count (a pop-out window's come in the same frame), the
    // presents left to capture (0 when none is running), whether one waits to start
    std::string capturePath;
    KSwapchain* captureSwapchain = nullptr;
    std::atomic<uint32_t> captureFrames{0};
    std::atomic<bool> captureStart{false}, capturing{false};
    std::atomic<int> shotsPending{0};   // shots whose PNG isn't written yet
    std::atomic<int> shotsWaiting{0};   // shots waiting for the writer's queue (writeShot)
    // Screenshots encoded so far: a present that took one (its frame's GPU time holds the copy) is left out of dynamic
    // resolution's samples (presentation/pacing.mm)
    std::atomic<uint64_t> shotsTaken{0};
    // Command buffers that failed: after a hang the GPU refuses the process's work, so captures and pass dumps refuse
    // to start
    std::atomic<uint32_t> gpuErrors{0};
};
InstrumentState& g_ins = *new InstrumentState;
}  // namespace

KMTL_EXPORT uint32_t ksaMtlFrameStats(KsaMtlFrameStats* out, uint32_t size) {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_ins.m);
    uint32_t n = std::min<uint32_t>(size, sizeof(KsaMtlFrameStats));
    memcpy(out, &g_ins.lastFrame, n);
    return n;
}

KMTL_EXPORT void ksaMtlScreenshot(const char* path) {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_ins.m);
    g_ins.shotRequests.push_back(path);
}

static os_log_t benchmarkLog() {
    static os_log_t log = os_log_create("io.github.ksa-macos-port", OS_LOG_CATEGORY_POINTS_OF_INTEREST);
    return log;
}

KMTL_EXPORT void ksaMtlMarker(const char* name, bool begin) {
    KTRACE_FIRST_PLAIN();
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

void kmtlNoteGpuError() {
    if (g_ins.gpuErrors++ == 0 && kmtlConfig().watch) kmtlWatchLine("the GPU failed a command buffer: nothing after this can be dumped");
}

KMTL_EXPORT uint32_t ksaMtlGpuErrors() {
    KTRACE_FIRST_PLAIN();
    return g_ins.gpuErrors;
}

KMTL_EXPORT bool ksaMtlCapture(const char* path, uint32_t frames) {
    KTRACE_FIRST_PLAIN();
    // After a GPU failure the GPU refuses this process's work: a capture would replay as nonsense
    if (g_ins.capturing || ksaMtlGpuErrors() || ![[MTLCaptureManager sharedCaptureManager] supportsDestination:MTLCaptureDestinationGPUTraceDocument])
        return false;
    std::lock_guard<std::mutex> lock(g_ins.m);
    g_ins.capturePath = path;
    g_ins.captureFrames = std::max(1u, frames);
    g_ins.capturing = true;
    g_ins.captureStart = true;
    return true;
}

KMTL_EXPORT bool ksaMtlCaptureRunning() {
    KTRACE_FIRST_PLAIN();
    return g_ins.capturing;
}

KMTL_EXPORT uint32_t ksaMtlMemory(int64_t* out, uint32_t n) {
    KTRACE_FIRST_PLAIN();
    const int64_t all[4] = {g_allocatedByType[0].load(), g_allocatedByType[1].load(), g_allocatedByType[2].load(), g_internalBytes.load()};
    uint32_t written = std::min<uint32_t>(n, 4);
    for (uint32_t i = 0; i < written; i++) out[i] = all[i];
    return written;
}

// At a present: starts a requested capture (the next frames' command buffers go into it) or ends a running one
static void captureAtPresent(KDevice* dev, KSwapchain* swapchain) {
    MTLCaptureManager* manager = [MTLCaptureManager sharedCaptureManager];
    if (g_ins.captureStart.exchange(false)) {
        g_ins.captureSwapchain = swapchain;
        MTLCaptureDescriptor* d = [MTLCaptureDescriptor new];
        d.captureObject = dev->mtl;
        d.destination = MTLCaptureDestinationGPUTraceDocument;
        d.outputURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:g_ins.capturePath.c_str()]];
        NSError* e = nil;
        if ([manager startCaptureWithDescriptor:d error:&e]) {
            KLOG(0, "frame capture started: %s", g_ins.capturePath.c_str());
        } else {
            KERR("frame capture failed to start: %s", e.localizedDescription.UTF8String);
            g_ins.captureFrames = 0;
            g_ins.capturing = false;
        }
        return;
    }
    // With its swapchain gone (a resize, a closed pop-out), any present counts, or the capture would never end
    if ((!g_ins.captureSwapchain || swapchain == g_ins.captureSwapchain) && g_ins.captureFrames && manager.isCapturing &&
        --g_ins.captureFrames == 0) {
        [manager stopCapture];
        KLOG(0, "frame capture written: %s", g_ins.capturePath.c_str());
        g_ins.capturing = false;
    }
}

void kmtlOnSwapchainDestroyed(KSwapchain* swapchain) {
    kmtlPassDumpSwapchainGone(swapchain);
    std::lock_guard<std::mutex> lock(g_ins.m);
    if (g_ins.captureSwapchain == swapchain) g_ins.captureSwapchain = nullptr;
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
    g_ins.start = g_ins.lastPresent = std::chrono::steady_clock::now();
    if (cfg.frames) {
        g_ins.frameCsv = fopen(cfg.frames, "w");
        if (g_ins.frameCsv) fprintf(g_ins.frameCsv, "present,ms,draws,dispatches,passes,submits,allocated_mb,gpu_ms\n");
    }
    if (cfg.shots) {
        const char* unit = cfg.shotUnit;
        g_ins.shotUnit = unit && !strcmp(unit, "frames") ? ShotUnit::InGameFrames
                   : unit && !strcmp(unit, "seconds") ? ShotUnit::InGameSeconds : ShotUnit::Presents;
        for (double v : parseList(cfg.shots)) {
            char name[64];
            snprintf(name, sizeof(name), "/shot_%.10g.png", v);
            g_ins.shots.push_back({v, std::string(cfg.shotDir ? cfg.shotDir : ".") + name});
        }
    }
    std::sort(g_ins.shots.begin(), g_ins.shots.end(), [](const Shot& a, const Shot& b) { return a.at < b.at; });
    g_ins.exitSeconds = cfg.exitAfterS;
}

static void ensureInstruments() {
    static std::once_flag once;
    std::call_once(once, initInstruments);
}

void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_ins.m);
    g_ins.draws += draws;
    g_ins.dispatches += dispatches;
    g_ins.passes += passes;
    g_ins.submits++;
}

static void exitSoon(const char* why) {
    KLOG(0, "%s: exiting", why);
    if (g_ins.frameCsv) fflush(g_ins.frameCsv);
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
constexpr int kShotsWaiting = 3;

static void writeShot(void (^write)(void), void (^done)(void)) {
    static dispatch_queue_t q = dispatch_queue_create("ksa-metal.screenshots", DISPATCH_QUEUE_SERIAL);
    void (^run)(void) = ^{
        @autoreleasepool {
            write();
        }
        if (done) done();
        g_ins.shotsWaiting--;
    };
    if (g_ins.shotsWaiting++ >= kShotsWaiting) dispatch_sync(q, run);
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

uint64_t kmtlShotsTaken() { return g_ins.shotsTaken.load(std::memory_order_relaxed); }

static void takeShot(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb, const std::string& path, bool last) {
    g_ins.shotsPending++;
    g_ins.shotsTaken++;
    bool exitAfter = last && kmtlConfig().exitAfterShots;
    // A second encoder after the present's blit (which updated the chain fence)
    kmtlSavePNG(cb, image->mtl, dev->queue->chain, path, ^{
        if (--g_ins.shotsPending == 0 && exitAfter) exitSoon("KSA_MTL_EXIT_AFTER_SHOTS");
    });
}

void kmtlOnPresent(KDevice* dev, KSwapchain* swapchain, KImage* image, id<MTLCommandBuffer> cb, uint64_t presents) {
    ensureInstruments();
    std::lock_guard<std::mutex> lock(g_ins.m);
    auto now = std::chrono::steady_clock::now();
    double ms = std::chrono::duration<double, std::milli>(now - g_ins.lastPresent).count();
    g_ins.lastPresent = now;
    if (!g_ins.ingamePresent && g_ins.draws + g_ins.dispatches > 50) {
        g_ins.ingamePresent = presents;
        g_ins.ingameTime = now;
        KLOG(0, "in-game at present %llu, %.1f s", (unsigned long long)presents,
             std::chrono::duration<double>(now - g_ins.start).count());
        kmtlReportPipelineTimes();
    }
    double gpu = kmtlTakePresentGpuTime() * 1e3;
    g_ins.lastFrame = {presents, ms, gpu, g_ins.draws, g_ins.dispatches, g_ins.passes, g_ins.submits,
                   (int64_t)(g_allocatedBytes.load() >> 20)};
    sampleSystem(g_ins.lastFrame);
    kmtlPipelineCounts(&g_ins.lastFrame.shadersCompiled, &g_ins.lastFrame.pipelinesCreated);
    g_ins.lastFrame.drawableWidth = (uint32_t)image->mtl.width;
    g_ins.lastFrame.drawableHeight = (uint32_t)image->mtl.height;
    g_ins.lastFrame.displayMs = kmtlDisplayMs();
    if (kmtlDrainOn()) kmtlDrainPresent(CACurrentMediaTime(), g_ins.lastFrame);
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
    if (g_ins.capturing) captureAtPresent(dev, swapchain);
    kmtlPassDumpAtPresent(dev->queue, swapchain, cb);
    kmtlWatchAtPresent(dev->queue, image, cb);
    if (g_ins.frameCsv) {
        fprintf(g_ins.frameCsv, "%llu,%.3f,%u,%u,%u,%u,%lld,%.3f\n", (unsigned long long)presents, ms, g_ins.draws, g_ins.dispatches,
                g_ins.passes, g_ins.submits, (long long)g_ins.lastFrame.allocatedMb, gpu);
        if (presents % 60 == 0) fflush(g_ins.frameCsv);
    }
    if (g_logLevel >= 1 && (presents <= 5 || presents % 500 == 0)) {

        KLOG(1, "present %llu: %.2f ms, %u draws, %u dispatches, %u passes, %u submits; memory MB: private %lld, "
             "shared %lld, shared cached %lld, internal %lld", (unsigned long long)presents, ms, g_ins.draws,
             g_ins.dispatches, g_ins.passes, g_ins.submits, (long long)(g_allocatedByType[0] >> 20),
             (long long)(g_allocatedByType[1] >> 20), (long long)(g_allocatedByType[2] >> 20), (long long)(g_internalBytes >> 20));
    }
    g_ins.draws = g_ins.dispatches = g_ins.passes = g_ins.submits = 0;

    while (g_ins.nextShot < g_ins.shots.size()) {
        double since;
        if (g_ins.shotUnit == ShotUnit::Presents) since = (double)presents;
        else if (!g_ins.ingamePresent) break;
        else if (g_ins.shotUnit == ShotUnit::InGameFrames) since = (double)(presents - g_ins.ingamePresent);
        else since = std::chrono::duration<double>(now - g_ins.ingameTime).count();
        if (since < g_ins.shots[g_ins.nextShot].at) break;
        bool last = g_ins.nextShot + 1 == g_ins.shots.size();
        takeShot(dev, image, cb, g_ins.shots[g_ins.nextShot].path, last);
        g_ins.nextShot++;
    }
    for (const std::string& path : g_ins.shotRequests) takeShot(dev, image, cb, path, false);
    g_ins.shotRequests.clear();
    if (kmtlConfig().exitAfter && presents == (uint64_t)kmtlConfig().exitAfter) exitSoon("KSA_MTL_EXIT_AFTER reached");
    if (g_ins.exitSeconds > 0 && std::chrono::duration<double>(now - g_ins.start).count() >= g_ins.exitSeconds) {
        g_ins.exitSeconds = 0;
        exitSoon("KSA_MTL_EXIT_AFTER_S reached");
    }
}
