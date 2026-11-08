// The benchmark's drain: what the driver measures, as typed records in one ring that the benchmark empties every frame
// into its run's store (ksaMtlDrainOpen, ksaMtlDrain, ksaMtlDrainDropped, ksaMtlDrainClose; src/shared/DriverStats.cs
// reads the records). Nothing is recorded until a reader opens it. Times are CACurrentMediaTime's clock, in seconds.
//
//   1 present  at each present (instruments.mm): its time and KsaMtlFrameStats
//   2 shown    from the drawable's presented handler (presentation/swapchain.mm): when the frame began (the game's wait
//              for an earlier frame ended), was presented, finished on the GPU and was shown (0: never), how long it
//              waited for an earlier frame, and whether pacing ended that wait
//   3 wait     a scope that blocked its caller at least the reader's threshold (KStallTimer, residency commits)
//   4 memory   an allocation (bytes positive) or a free (negative) of 16 MB or more (resources/memory.mm)
//   5 gpu      a frame's own GPU time, when its present's command buffer completed (presentation/swapchain.mm): the
//              present (as the present record numbers it), when, and the GPU's busy time since the frame before's
//              present completed. The present record's GPU time is what completed between two presents on the CPU's
//              clock, which lags by the frames in flight and puts two frames' work in one after a stall
//
// Each record is a header (type, size of the whole record, 4 bytes unused) and its fields; a record that finds the ring
// full is dropped and counted, so nothing ever waits for the reader. kDrainVersion names this layout: the reader opens
// the drain with the version it knows, and a driver of another layout refuses.
#include "kmtl.h"

namespace {

constexpr uint32_t kDrainVersion = 1;

struct Header {
    uint16_t type, size;
    uint32_t reserved;
};
struct PresentRec {
    Header h;
    double t;
    KsaMtlFrameStats stats;
};
struct ShownRec {
    Header h;
    uint64_t present;
    double began, presented, gpuEnd, shown, waitedMs;
    uint32_t paced, reserved;
};
struct WaitRec {
    Header h;
    double t, ms;
    uint64_t present;
    char what[40];
};
struct MemoryRec {
    Header h;
    double t;
    int64_t bytes;
    uint32_t type, reserved;
    uint64_t present;
};
struct GpuRec {
    Header h;
    uint64_t present;
    double t, ms;
};

std::mutex g_drainMutex;
std::vector<uint8_t> g_ring;
size_t g_head = 0, g_used = 0;  // where the oldest record starts, the bytes queued
std::atomic<uint64_t> g_dropped{0};
std::atomic<double> g_waitMs{0};

template <typename R>
void push(uint16_t type, R& rec) {
    rec.h = {type, (uint16_t)sizeof(R), 0};
    std::lock_guard<std::mutex> lock(g_drainMutex);
    if (!g_kmtlDrainOn || g_ring.size() - g_used < sizeof(R)) {
        if (g_kmtlDrainOn) g_dropped++;
        return;
    }
    size_t tail = (g_head + g_used) % g_ring.size(), first = std::min(sizeof(R), g_ring.size() - tail);
    memcpy(&g_ring[tail], &rec, first);
    memcpy(&g_ring[0], (const uint8_t*)&rec + first, sizeof(R) - first);
    g_used += sizeof(R);
}

// n bytes from the ring at position at, across its end
void copyOut(uint8_t* to, size_t at, size_t n) {
    size_t first = std::min(n, g_ring.size() - at);
    memcpy(to, &g_ring[at], first);
    memcpy(to + first, &g_ring[0], n - first);
}

}  // namespace

std::atomic<bool> g_kmtlDrainOn{false};

KMTL_EXPORT bool ksaMtlDrainOpen(uint32_t version, double waitMs, uint32_t capacity) {
    if (version != kDrainVersion) {
        KLOG(0, "drain: a reader of layout %u, this driver's is %u", version, kDrainVersion);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(g_drainMutex);
        if (g_ring.empty()) g_ring.resize(std::clamp<size_t>(capacity, 64u << 10, 64u << 20));
        // A new reader: nothing of a reader before it, which may have gone without closing
        g_head = g_used = 0;
        g_dropped = 0;
        g_waitMs = std::max(waitMs, 0.0);
        g_kmtlDrainOn = true;
    }
    // The latest present first, so that the reader has the driver's numbers before the next one
    KsaMtlFrameStats last{};
    if (ksaMtlFrameStats(&last, sizeof last) == sizeof last && last.present) kmtlDrainPresent(CACurrentMediaTime(), last);
    return true;
}

// Stops recording and forgets what wasn't read
KMTL_EXPORT void ksaMtlDrainClose() {
    std::lock_guard<std::mutex> lock(g_drainMutex);
    g_kmtlDrainOn = false;
    g_head = g_used = 0;
}

KMTL_EXPORT uint32_t ksaMtlDrain(uint8_t* out, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_drainMutex);
    uint32_t written = 0;
    while (g_used >= sizeof(Header)) {
        Header h;
        copyOut((uint8_t*)&h, g_head, sizeof h);
        if (written + h.size > capacity) break;
        copyOut(out + written, g_head, h.size);
        written += h.size;
        g_head = (g_head + h.size) % g_ring.size();
        g_used -= h.size;
    }
    return written;
}

KMTL_EXPORT uint64_t ksaMtlDrainDropped() { return g_dropped; }

void kmtlDrainPresent(double t, const KsaMtlFrameStats& stats) {
    PresentRec r{};
    r.t = t;
    r.stats = stats;
    push(1, r);
}

void kmtlDrainShown(uint64_t present, double began, double presented, double gpuEnd, double shown, double waitedMs, bool paced) {
    ShownRec r{};
    r.present = present;
    r.began = began;
    r.presented = presented;
    r.gpuEnd = gpuEnd;
    r.shown = shown;
    r.waitedMs = waitedMs;
    r.paced = paced ? 1 : 0;
    push(2, r);
}

void kmtlDrainWait(const char* what, double start, double ms) {
    if (ms < g_waitMs) return;
    WaitRec r{};
    r.t = start;
    r.ms = ms;
    r.present = kmtlPresentCount();
    strlcpy(r.what, what, sizeof r.what);
    push(3, r);
}

void kmtlDrainFrameGpu(uint64_t present, double t, double ms) {
    GpuRec r{};
    r.present = present;
    r.t = t;
    r.ms = ms;
    push(5, r);
}

void kmtlDrainMemory(int64_t bytes, uint32_t type) {
    MemoryRec r{};
    r.t = CACurrentMediaTime();
    r.bytes = bytes;
    r.type = type;
    r.present = kmtlPresentCount();
    push(4, r);
}
