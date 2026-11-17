// The watch (KSA_MTL_WATCH=<dir>): every kWatchEvery presents, a copy of the presented image and a look at a sample of it,
// off the game's thread (on the pass dump's writer queue). An image that is broken without doubt, by what no clean view
// has shown, is kept and gets a pass dump of the next frame (passdump.mm); watch.txt has when each fault began and ended.
// Dark or partly wrong images need a clean image of the same view to compare with: the benchmark's --catch does that.
#include "kmtl.h"
#include <atomic>
#include <chrono>
#include <cstdio>
#include <ctime>
#include <string>

// The share of a sample of the image with what clean views don't show, in percent. Measured on 4400 screenshots of 44 views:
// white at most 14 % on a clean view and from 50 % on a broken one, the others only on broken ones
namespace {

constexpr uint64_t kWatchEvery = 31;  // odd: the game alternates between two sets of frame data, and a fault can be in one
constexpr NSUInteger kWatchStride = 8, kWatchMinWidth = 640;
constexpr double kWatchBlack = 99.9, kWatchColour = 0.1;
constexpr int kWatchDumps = 3, kWatchDumpSeconds = 10, kWatchBlackLooks = 4;

struct WatchSample { double white, black, green, primary; };

WatchSample sampleImage(const uint8_t* bgra, NSUInteger width, NSUInteger height) {
    uint64_t n = 0, white = 0, black = 0, green = 0, primary = 0;
    for (NSUInteger y = 0; y < height; y += kWatchStride) {
        const uint8_t* row = bgra + y * width * 4;
        for (NSUInteger x = 0; x < width; x += kWatchStride) {
            int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
            n++;
            white += r >= 250 && g >= 250 && b >= 250;
            black += r <= 1 && g <= 1 && b <= 1;
            green += g > 60 && r * 6 < g && b * 6 < g;
            bool full[3] = {r >= 240, g >= 240, b >= 240}, none[3] = {r <= 16, g <= 16, b <= 16};
            primary += (full[0] && full[1] && none[2]) || (full[0] && none[1] && none[2]) ||
                       (none[0] && full[1] && none[2]) || (none[0] && none[1] && full[2]);
        }
    }
    double scale = 100.0 / std::max<uint64_t>(n, 1);
    return {white * scale, black * scale, green * scale, primary * scale};
}

}  // namespace

// A line of watch.txt, after the time (the watch on)
void kmtlWatchLine(const std::string& text) {
    if (FILE* f = fopen((std::string(kmtlConfig().watch) + "/watch.txt").c_str(), "a")) {
        time_t now = time(nullptr);
        char when[32];
        strftime(when, sizeof when, "%H:%M:%S", localtime(&now));
        fprintf(f, "%s %s\n", when, text.c_str());
        fclose(f);
    }
}

namespace {

struct WatchState {
    id<MTLBuffer> buffer;            // the copy of the presented image
    std::atomic<bool> busy{false};   // a copy on its way: the next looks wait
    uint64_t presents = 0;           // presents seen, on the presenting thread
    // On the pass dump's writer queue only
    bool broken = false, seenClean = false;
    int blackLooks = 0;
    int dumped = 0;
    std::chrono::steady_clock::time_point lastDump, brokenSince;
};
WatchState& g_watch = *new WatchState;

void watchLook(id<MTLBuffer> buffer, NSUInteger width, NSUInteger height, uint64_t present) {
    WatchSample s = sampleImage((const uint8_t*)buffer.contents, width, height);
    // A fade or a loading screen is black too: black counts when it stays, and for one dump
    g_watch.blackLooks = s.black >= kWatchBlack ? g_watch.blackLooks + 1 : 0;
    bool colours = s.white >= kmtlConfig().watchWhite || s.green >= kWatchColour || s.primary >= kWatchColour;
    bool broken = colours || g_watch.blackLooks >= kWatchBlackLooks;
    auto now = std::chrono::steady_clock::now();
    if (FILE* f = fopen((std::string(kmtlConfig().watch) + "/watch.csv").c_str(), "a")) {
        fseek(f, 0, SEEK_END);
        if (ftell(f) == 0) fprintf(f, "present,seconds,white,black,green,primary\n");
        fprintf(f, "%llu,%.2f,%.2f,%.2f,%.2f,%.2f\n", (unsigned long long)present, std::chrono::duration<double>(now.time_since_epoch()).count(), s.white, s.black, s.green, s.primary);
        fclose(f);
    }
    // Loading and fades can be black: only an image that follows a clean one counts (a limit of 0 is a test, always broken)
    g_watch.seenClean |= !broken;
    if (!g_watch.seenClean && kmtlConfig().watchWhite > 0) return;
    if (broken != g_watch.broken) {
        char line[160];
        if (broken) {
            g_watch.brokenSince = now;
            snprintf(line, sizeof line, "present %llu broken: white %.2f %% black %.2f %% green %.2f %% primary %.2f %%", (unsigned long long)present, s.white, s.black, s.green, s.primary);
        } else {
            snprintf(line, sizeof line, "present %llu clear after %.1f s", (unsigned long long)present, std::chrono::duration<double>(now - g_watch.brokenSince).count());
        }
        kmtlWatchLine(line);
        g_watch.broken = broken;
    }
    if (!broken || g_watch.dumped >= (colours ? kWatchDumps : 1) || (g_watch.dumped && now - g_watch.lastDump < std::chrono::seconds(kWatchDumpSeconds)))
        return;
    std::string name = "watch_" + std::to_string(g_watch.dumped + 1);
    std::string dir = std::string(kmtlConfig().watch) + "/" + name;
    if (!ksaMtlPassDump(dir.c_str())) {
        kmtlWatchLine("present " + std::to_string(present) + " no pass dump: another is running or the GPU has failed");
        return;
    }
    g_watch.dumped++;
    g_watch.lastDump = now;
    kmtlWritePNG(buffer, width, height, true, std::string(kmtlConfig().watch) + "/" + name + ".png");
    kmtlWatchLine("present " + std::to_string(present) + " " + name + ": the image, and the next frame's passes");
}

}  // namespace

void kmtlWatchAtPresent(KQueue* queue, KImage* image, id<MTLCommandBuffer> cb) {
    if (!kmtlConfig().watch || !image->mtl || image->mtl.width < kWatchMinWidth || ++g_watch.presents % kWatchEvery) return;
    id<MTLTexture> t = image->mtl;
    if (t.pixelFormat != MTLPixelFormatBGRA8Unorm && t.pixelFormat != MTLPixelFormatBGRA8Unorm_sRGB) {
        KERR_ONCE("watch: this image's format can't be read: the watch does nothing");
        return;
    }
    NSUInteger size = t.width * t.height * 4;
    if (g_watch.busy.exchange(true)) return;
    static std::once_flag folder;
    std::call_once(folder, [] {
        [[NSFileManager defaultManager] createDirectoryAtPath:@(kmtlConfig().watch) withIntermediateDirectories:YES attributes:nil error:nil];
    });
    if (g_watch.buffer.length != size) g_watch.buffer = [t.device newBufferWithLength:size options:MTLResourceStorageModeShared];
    id<MTLBuffer> buffer = g_watch.buffer;
    if (!buffer) {
        g_watch.busy = false;
        return;
    }
    id<MTLBlitCommandEncoder> blit = [cb blitCommandEncoder];
    blit.label = @"watch";
    [blit waitForFence:queue->chain];
    [blit copyFromTexture:t sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(t.width, t.height, 1)
                 toBuffer:buffer destinationOffset:0 destinationBytesPerRow:t.width * 4 destinationBytesPerImage:size];
    [blit endEncoding];
    NSUInteger width = t.width, height = t.height;
    uint64_t present = g_watch.presents;
    [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
        if (done.status == MTLCommandBufferStatusError) {
            kmtlNoteGpuError();
            g_watch.busy = false;
            return;
        }
        dispatch_async(kmtlPassDumpWriter(), ^{
            @autoreleasepool {
                watchLook(buffer, width, height, present);
            }
            g_watch.busy = false;
        });
    }];
}

