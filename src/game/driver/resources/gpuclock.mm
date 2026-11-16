// The GPU's clock, for dynamic resolution's GPU times (dynres.mm). With the frames paced (Steady Frame Rate), macOS runs
// the GPU only as fast as each frame's deadline needs (an M5 at 30 FPS: 340-800 MHz instead of 1620), so a frame's GPU
// time tells the deadline more than the work. Scaled by the clock it ran at over the highest, it tells the work again:
// about the time the frame would take at the full clock (the part bound by memory doesn't follow the clock: a guess on
// the low side, which the frames at the higher clock then correct).
//
// From macOS's IOReport (a private interface, read without root, as Activity Monitor's energy figures are; loaded at
// run time, as the benchmark's power channel reads it): GPU Stats' performance states, their time in each over the last
// kEvery weighted by their frequencies (the IORegistry's pmgr voltage-states9). Started by dynamic resolution only when
// Steady Frame Rate drives it. Missing (another macOS or chip, a table it can't read): the GPU times as measured, said
// once.
#include "kmtl.h"
#include <IOKit/IOKitLib.h>
#include <dlfcn.h>
#include <pthread.h>
#include <thread>

namespace {
using CopyChannelsInGroup = CFMutableDictionaryRef (*)(CFStringRef, CFStringRef, uint64_t, uint64_t, uint64_t);
using CreateSubscription = CFTypeRef (*)(void*, CFMutableDictionaryRef, CFMutableDictionaryRef*, uint64_t, CFTypeRef);
using CreateSamples = CFDictionaryRef (*)(CFTypeRef, CFMutableDictionaryRef, CFTypeRef);
using CreateSamplesDelta = CFDictionaryRef (*)(CFDictionaryRef, CFDictionaryRef, CFTypeRef);
using ChannelString = CFStringRef (*)(CFDictionaryRef);
using StateGetCount = int (*)(CFDictionaryRef);
using StateGetName = CFStringRef (*)(CFDictionaryRef, int);
using StateGetResidency = int64_t (*)(CFDictionaryRef, int);

struct GpuClock {
    std::once_flag started;
    std::atomic<double> share{1.0};  // the last window's clock over the highest (1: unknown, or the full clock)
    std::atomic<double> mhz{0.0};    // the last window's clock (0: unknown)
    std::atomic<bool> known{false};  // a clock read at least once
    std::vector<double> clocks;      // each state's frequency in MHz (the first, off, 0)
};
GpuClock& g_clock = *new GpuClock;
}

// The polling interval: a few frames at 30 to 60 FPS, about as quick as macOS changes the clock
static constexpr auto kEvery = std::chrono::milliseconds(50);

// Each GPU performance state's frequency in MHz, from the IORegistry's pmgr node: 8 bytes a state, the frequency in Hz
// first. None when it isn't there or doesn't read as such a table (another chip's may hold other units): frequencies
// that rise, from 100 MHz to 5 GHz
static std::vector<double> stateClocks() {
    std::vector<double> clocks;
    io_service_t pmgr = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceNameMatching("pmgr"));
    if (!pmgr) return clocks;
    CFTypeRef data = IORegistryEntryCreateCFProperty(pmgr, CFSTR("voltage-states9"), kCFAllocatorDefault, 0);
    IOObjectRelease(pmgr);
    if (!data) return clocks;
    if (CFGetTypeID(data) == CFDataGetTypeID()) {
        const uint8_t* bytes = CFDataGetBytePtr((CFDataRef)data);
        for (CFIndex i = 0; i + 8 <= CFDataGetLength((CFDataRef)data); i += 8) {
            uint32_t hz;
            memcpy(&hz, bytes + i, 4);
            clocks.push_back(hz / 1e6);
        }
    }
    CFRelease(data);
    for (size_t i = 1; i < clocks.size(); i++)
        if (clocks[i] < 100 || clocks[i] > 5000 || (i > 1 && clocks[i] <= clocks[i - 1])) return {};
    return clocks.size() > 1 ? clocks : std::vector<double>{};
}

static bool same(CFStringRef s, CFStringRef text) {
    return s && CFStringCompare(s, text, 0) == kCFCompareEqualTo;
}

static void sample() {
    pthread_setname_np("KSA Metal GPU clock");
    void* lib = dlopen("/usr/lib/libIOReport.dylib", RTLD_LAZY | RTLD_LOCAL);
    auto copyChannels = lib ? (CopyChannelsInGroup)dlsym(lib, "IOReportCopyChannelsInGroup") : nullptr;
    auto subscribe = lib ? (CreateSubscription)dlsym(lib, "IOReportCreateSubscription") : nullptr;
    auto samples = lib ? (CreateSamples)dlsym(lib, "IOReportCreateSamples") : nullptr;
    auto delta = lib ? (CreateSamplesDelta)dlsym(lib, "IOReportCreateSamplesDelta") : nullptr;
    auto channelName = lib ? (ChannelString)dlsym(lib, "IOReportChannelGetChannelName") : nullptr;
    auto stateCount = lib ? (StateGetCount)dlsym(lib, "IOReportStateGetCount") : nullptr;
    auto stateName = lib ? (StateGetName)dlsym(lib, "IOReportStateGetNameForIndex") : nullptr;
    auto residency = lib ? (StateGetResidency)dlsym(lib, "IOReportStateGetResidency") : nullptr;
    if (!copyChannels || !subscribe || !samples || !delta || !channelName || !stateCount || !stateName || !residency) {
        KLOG(0, "GPU clock: no IOReport here, dynamic resolution takes the GPU times as measured");
        return;
    }
    const std::vector<double>& clocks = g_clock.clocks;
    if (clocks.empty()) {
        KLOG(0, "GPU clock: no frequency table in the IORegistry, dynamic resolution takes the GPU times as measured");
        return;
    }
    CFMutableDictionaryRef channels = copyChannels(CFSTR("GPU Stats"), CFSTR("GPU Performance States"), 0, 0, 0);
    CFMutableDictionaryRef subscribed = nullptr;
    CFTypeRef subscription = channels ? subscribe(nullptr, channels, &subscribed, 0, nullptr) : nullptr;
    if (!subscription) {
        KLOG(0, "GPU clock: macOS refused IOReport's GPU states, dynamic resolution takes the GPU times as measured");
        if (channels) CFRelease(channels);
        if (subscribed) CFRelease(subscribed);
        return;
    }
    KLOG(0, "GPU clock: from IOReport, %zu states up to %.0f MHz, every %lld ms", clocks.size(), clocks.back(),
         (long long)kEvery.count());
    bool said = false;
    CFDictionaryRef last = nullptr;
    // Until the process ends: a thread that wakes 20 times a second and reads a few counters
    for (;;) @autoreleasepool {
        CFDictionaryRef now = samples(subscription, subscribed, nullptr);
        if (now && last) {
            CFDictionaryRef d = delta(last, now, nullptr);
            CFArrayRef items = d ? (CFArrayRef)CFDictionaryGetValue(d, CFSTR("IOReportChannels")) : nullptr;
            int64_t on = 0;
            double weighted = 0;
            bool unknown = false;
            for (CFIndex i = 0, n = items ? CFArrayGetCount(items) : 0; i < n; i++) {
                auto c = (CFDictionaryRef)CFArrayGetValueAtIndex(items, i);
                if (!same(channelName(c), CFSTR("GPUPH"))) continue;
                for (int s = 1, count = stateCount(c); s < count; s++) {
                    CFStringRef name = stateName(c, s);
                    if (same(name, CFSTR("OFF")) || same(name, CFSTR("IDLE"))) continue;
                    int64_t r = residency(c, s);
                    on += r;
                    if ((size_t)s < clocks.size()) weighted += r * clocks[s];
                    else if (r > 0) unknown = true;
                }
            }
            if (d) CFRelease(d);
            // A window with the GPU off all along keeps the last clock
            if (on > 0 && !unknown) {
                double mhz = weighted / on;
                g_clock.mhz = mhz;
                g_clock.share = std::min(1.0, mhz / clocks.back());
                g_clock.known = true;
            } else if (unknown && !said) {
                said = true;
                KLOG(0, "GPU clock: a state past the %zu the IORegistry lists, no clock for that time", clocks.size());
            }
        }
        if (now) {
            if (last) CFRelease(last);
            last = now;
        }
        std::this_thread::sleep_for(kEvery);
    }
}

void kmtlGpuClockStart() {
    std::call_once(g_clock.started, [] {
        g_clock.clocks = stateClocks();
        std::thread(sample).detach();
    });
}

double kmtlGpuClockShare() {
    return g_clock.share.load(std::memory_order_relaxed);
}

double kmtlGpuClockMhz() {
    return g_clock.mhz.load(std::memory_order_relaxed);
}

bool kmtlGpuClockKnown() {
    return g_clock.known.load(std::memory_order_relaxed);
}
