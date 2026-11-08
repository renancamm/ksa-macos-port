// The game's GPU profiler as KSA Metal serves it: the counter sample buffers its timestamps take (vkCmdWriteTimestamp,
// commands/transfers.mm), how those timestamps are answered, and the encoders named after its zones. Apple GPUs sample
// counters only at encoder stage boundaries, so a timestamp is the end of a tiny blit encoder ordered after everything
// before it.
//
//   ksaMtlZoneMode(mode)    how the game's timestamps are answered from the next one on:
//                           0 as they come: each one ends the encoder and waits for everything before it; one inside a
//                             render pass is taken when the pass ends, so a zone inside a pass reads about 0
//                           1 labels: answered at once with 0, no encoder and no wait; the game's profiler then only
//                             names the encoders (with labels)
//                           2 split: as 0, but one inside a render pass splits the pass at its next draw (suspended
//                             and resumed), so zones inside it read their own time; needs gpu_zones at launch, which
//                             also gives the attachments kept in tile memory memory of their own (false without it)
//   ksaMtlZoneBegin(commandBuffer, name), ksaMtlZoneEnd(commandBuffer)
//                           a zone of the game's GPU profiler begins or ends in this recording; with labels, an encoder
//                           is named after the zone its first draw or dispatch ran in
#include "kmtl.h"

// Timestamp sample buffers, for the game's timestamp queries. Metal caps the number of live counter sample buffers:
// they are recycled once their command buffer completes.
static std::mutex g_timestampMutex;
static std::vector<id<MTLCounterSampleBuffer>> g_timestampPool;
static std::atomic<uint32_t> g_zoneMode{kZonesAsTheyCome};

static id<MTLCounterSet> timestampSet(KDevice* dev) {
    static id<MTLCounterSet> set = nil;
    static std::once_flag once;
    std::call_once(once, [dev] {
        for (id<MTLCounterSet> cs in dev->mtl.counterSets)
            if ([cs.name isEqualToString:MTLCommonCounterSetTimestamp]) set = cs;
    });
    return set;
}

id<MTLCounterSampleBuffer> kmtlAcquireTimestamps(KDevice* dev, const char* what) {
    {
        std::lock_guard<std::mutex> lock(g_timestampMutex);
        if (!g_timestampPool.empty()) {
            id<MTLCounterSampleBuffer> b = g_timestampPool.back();
            g_timestampPool.pop_back();
            return b;
        }
    }
    id<MTLCounterSet> set = timestampSet(dev);
    MTLCounterSampleBufferDescriptor* d = [MTLCounterSampleBufferDescriptor new];
    d.counterSet = set;
    d.storageMode = MTLStorageModeShared;
    d.sampleCount = kTimestampSamples;
    NSError* e = nil;
    id<MTLCounterSampleBuffer> b = set ? [dev->mtl newCounterSampleBufferWithDescriptor:d error:&e] : nil;
    if (!b) KERR_LIMIT(3, "%s: timestamp sample buffer: %s", what, e ? e.description.UTF8String : "no timestamp counters");
    return b;
}

void kmtlRecycleTimestamps(id<MTLCounterSampleBuffer> b) {
    if (!b) return;
    std::lock_guard<std::mutex> lock(g_timestampMutex);
    g_timestampPool.push_back(b);
}

uint32_t kmtlZoneMode() { return g_zoneMode.load(std::memory_order_relaxed); }

KMTL_EXPORT bool ksaMtlZoneMode(uint32_t mode) {
    if (mode > kZonesSplit || (mode == kZonesSplit && !kmtlConfig().gpuZones)) return false;
    g_zoneMode = mode;
    return true;
}

KMTL_EXPORT void ksaMtlZoneBegin(VkCommandBuffer commandBuffer, const char* name) {
    if (commandBuffer && name) ((KCommandBuffer*)commandBuffer)->zones.push_back(name);
}

KMTL_EXPORT void ksaMtlZoneEnd(VkCommandBuffer commandBuffer) {
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb && !cb->zones.empty()) cb->zones.pop_back();
}

// A draw or dispatch with this pipeline in the current encoder: its name, and a pass dump's image after the draw
void kmtlNoteDraw(KCommandBuffer* cb, KPipeline* p, bool dispatch) {
    if (!kmtlConfig().labels && !kmtlPassDumpRecording()) return;  // the common case: two predictable branches per draw
    cb->notePipelineLabel(p);  // a pass dump names its passes
    if (!dispatch && cb->render && cb->deferredStores && cb->dumpDesc) kmtlPassDumpAfterDraw(cb, p);
}
