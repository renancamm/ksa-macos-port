// The benchmark's drain: what the driver measures, as typed records in one ring that the benchmark empties every frame
// into its run's store (ksaMtlDrainOpen, ksaMtlDrain, ksaMtlDrainDropped, ksaMtlDrainClose; src/shared/DriverStats.cs
// reads the records). Nothing is recorded until a reader opens it. Times are CACurrentMediaTime's clock, in seconds.
//
// The records and their layout (kKsaMtlDrainVersion) are in exports.h. A record that finds the ring full is dropped
// and counted, so nothing ever waits for the reader.
#include "kmtl.h"

namespace {

struct DrainState {
    std::mutex m;  // the ring and where its records are
    std::vector<uint8_t> ring;
    size_t head = 0, used = 0;          // where the oldest record starts, the bytes queued
    std::atomic<uint64_t> dropped{0};   // records that found the ring full
    std::atomic<double> waitMs{0};      // the reader's threshold for wait records
};
DrainState& g_drain = *new DrainState;

template <typename R>
void push(uint16_t type, R& rec) {
    rec.h = {type, (uint16_t)sizeof(R), 0};
    std::lock_guard<std::mutex> lock(g_drain.m);
    if (!g_kmtlDrainOn || g_drain.ring.size() - g_drain.used < sizeof(R)) {
        if (g_kmtlDrainOn) g_drain.dropped++;
        return;
    }
    size_t tail = (g_drain.head + g_drain.used) % g_drain.ring.size(), first = std::min(sizeof(R), g_drain.ring.size() - tail);
    memcpy(&g_drain.ring[tail], &rec, first);
    memcpy(&g_drain.ring[0], (const uint8_t*)&rec + first, sizeof(R) - first);
    g_drain.used += sizeof(R);
}

// n bytes from the ring at position at, across its end
void copyOut(uint8_t* to, size_t at, size_t n) {
    size_t first = std::min(n, g_drain.ring.size() - at);
    memcpy(to, &g_drain.ring[at], first);
    memcpy(to + first, &g_drain.ring[0], n - first);
}

}  // namespace

std::atomic<bool> g_kmtlDrainOn{false};

KMTL_EXPORT bool ksaMtlDrainOpen(uint32_t version, double waitMs, uint32_t capacity) {
    KTRACE_FIRST_PLAIN();
    if (version != kKsaMtlDrainVersion) {
        KLOG(0, "drain: a reader of layout %u, this driver's is %u", version, kKsaMtlDrainVersion);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(g_drain.m);
        if (g_drain.ring.empty()) g_drain.ring.resize(std::clamp<size_t>(capacity, 64u << 10, 64u << 20));
        // A new reader: nothing of a reader before it, which may have gone without closing
        g_drain.head = g_drain.used = 0;
        g_drain.dropped = 0;
        g_drain.waitMs = std::max(waitMs, 0.0);
        g_kmtlDrainOn = true;
    }
    // The latest present first, so that the reader has the driver's numbers before the next one
    KsaMtlFrameStats last{};
    if (ksaMtlFrameStats(&last, sizeof last) == sizeof last && last.present) kmtlDrainPresent(CACurrentMediaTime(), last);
    return true;
}

// Stops recording and forgets what wasn't read
KMTL_EXPORT void ksaMtlDrainClose() {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_drain.m);
    g_kmtlDrainOn = false;
    g_drain.head = g_drain.used = 0;
}

KMTL_EXPORT uint32_t ksaMtlDrain(uint8_t* out, uint32_t capacity) {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_drain.m);
    uint32_t written = 0;
    while (g_drain.used >= sizeof(KsaMtlDrainHeader)) {
        KsaMtlDrainHeader h;
        copyOut((uint8_t*)&h, g_drain.head, sizeof h);
        if (written + h.size > capacity) break;
        copyOut(out + written, g_drain.head, h.size);
        written += h.size;
        g_drain.head = (g_drain.head + h.size) % g_drain.ring.size();
        g_drain.used -= h.size;
    }
    return written;
}

KMTL_EXPORT uint64_t ksaMtlDrainDropped() {
    KTRACE_FIRST_PLAIN();
    return g_drain.dropped;
}

void kmtlDrainPresent(double t, const KsaMtlFrameStats& stats) {
    KsaMtlPresentRecord r{};
    r.t = t;
    r.stats = stats;
    push(1, r);
}

void kmtlDrainShown(uint64_t present, double began, double presented, double gpuEnd, double shown, double waitedMs, bool paced) {
    KsaMtlShownRecord r{};
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
    if (ms < g_drain.waitMs) return;
    KsaMtlWaitRecord r{};
    r.t = start;
    r.ms = ms;
    r.present = kmtlPresentCount();
    strlcpy(r.what, what, sizeof r.what);
    push(3, r);
}

void kmtlDrainFrameGpu(uint64_t present, double t, double ms) {
    KsaMtlGpuRecord r{};
    r.present = present;
    r.t = t;
    r.ms = ms;
    push(5, r);
}

void kmtlDrainMemory(int64_t bytes, uint32_t type) {
    KsaMtlMemoryRecord r{};
    r.t = CACurrentMediaTime();
    r.bytes = bytes;
    r.type = type;
    r.present = kmtlPresentCount();
    push(4, r);
}
