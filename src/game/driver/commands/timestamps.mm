// Timestamp queries, the game's GPU profiler's: one counter sample per write, at the end of a tiny blit encoder ordered
// after everything recorded before it (Apple GPUs sample counters only at encoder stage boundaries). A write inside a
// render pass is taken when the pass ends, or, with zones split, at the pass's next draw, which suspends it for them
// (stampSplit); with labels, a write is answered at once. How they are answered: diagnostics/zones.mm. The values reach
// the query pools (resources/queries.mm) as the command buffer completes.
#include "kmtl.h"

namespace {
constexpr uint32_t kTimestampSamples = 4096;  // per sample buffer
// The sample buffers. Metal caps how many counter sample buffers are alive: they are recycled once their command
// buffer completes; under m
struct TimestampState {
    std::mutex m;
    std::vector<id<MTLCounterSampleBuffer>> pool;
};
TimestampState& g_ts = *new TimestampState;
}  // namespace

static id<MTLCounterSet> timestampSet(KDevice* dev) {
    static id<MTLCounterSet> set = nil;
    static std::once_flag once;
    std::call_once(once, [dev] {
        for (id<MTLCounterSet> cs in dev->mtl.counterSets)
            if ([cs.name isEqualToString:MTLCommonCounterSetTimestamp]) set = cs;
    });
    return set;
}

static id<MTLCounterSampleBuffer> acquireSamples(KDevice* dev, const char* what) {
    {
        std::lock_guard<std::mutex> lock(g_ts.m);
        if (!g_ts.pool.empty()) {
            id<MTLCounterSampleBuffer> b = g_ts.pool.back();
            g_ts.pool.pop_back();
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

static void recycleSamples(id<MTLCounterSampleBuffer> b) {
    if (!b) return;
    std::lock_guard<std::mutex> lock(g_ts.m);
    g_ts.pool.push_back(b);
}

void KCommandBuffer::writeTimestamp(KQueryPool* pool, uint32_t query) {
    uint32_t mode = kmtlZoneMode();
    // Labels: answered at once, no encoder and no wait
    if (mode == kZonesLabels) {
        std::lock_guard<std::mutex> lock(pool->m);
        if (query < pool->count) {
            pool->values[query] = 0;
            pool->available[query] = 1;
        }
        pool->cv.notify_all();
        return;
    }
    if (render) {
        // Taken when the pass ends; split: at its next draw (stampSplit)
        ts.deferred.push_back({pool, query});
        if (mode == kZonesSplit && pass.deferredStores && pass.desc) ts.split = true;
        return;
    }
    stampTimestamp(pool, query);
}

// A tiny blit encoder after everything recorded so far, whose end is the timestamp
void KCommandBuffer::stampTimestamp(KQueryPool* pool, uint32_t query) {
    if (!ts.samples) {
        ts.samples = acquireSamples(device, "vkCmdWriteTimestamp");
        if (!ts.samples) {
            ts.writes.push_back({pool, query, UINT32_MAX});
            return;
        }
        ts.used = 0;
    }
    if (ts.used + 2 > kTimestampSamples) {
        ts.writes.push_back({pool, query, UINT32_MAX});
        return;
    }
    endEncoder();
    // It waits for everything before it, MSAA resolves too: its wait clears what later encoders would wait for
    // (waitForResolves), so they would no longer wait for those
    waitForAllResolves();
    // A blit with counter samples doesn't keep the fences' order: work after it can start before the work before it is
    // done (a split render pass's second half read the first half's stores stale: the cockpit lost its lights in 4 of 9
    // split game sessions, 0 of 8 with the same blit without its samples, 0 of 5 with no blit between the halves). An
    // event before it waits for everything recorded so far; timestamps back to back share one
    if (!ts.last) {
        if (!ts.event) ts.event = [device->mtl newEvent];
        [mtl encodeSignalEvent:ts.event value:++ts.signaled];
        [mtl encodeWaitForEvent:ts.event value:ts.signaled];
    }
    MTLBlitPassDescriptor* d = [MTLBlitPassDescriptor blitPassDescriptor];
    d.sampleBufferAttachments[0].sampleBuffer = ts.samples;
    d.sampleBufferAttachments[0].startOfEncoderSampleIndex = ts.used;
    d.sampleBufferAttachments[0].endOfEncoderSampleIndex = ts.used + 1;
    id<MTLBlitCommandEncoder> b = [mtl blitCommandEncoderWithDescriptor:d];
    waitForRoles(b, kClassAll);  // after everything recorded so far
    [b fillBuffer:device->timestampScratch range:NSMakeRange(0, 4) value:0];
    [b updateFence:finishFence(kClassAll)];
    [b endEncoding];
    order.roles = 0;
    ts.last = true;
    ts.writes.push_back({pool, query, ts.used + 1});
    ts.used += 2;
}

// At submit: resolve the samples into the pools when the command buffer completes
void KCommandBuffer::submitTimestamps(id<MTLCommandBuffer> c) {
    if (ts.writes.empty()) { releaseTimestamps(); return; }
    id<MTLCounterSampleBuffer> samples = ts.samples;
    uint32_t used = ts.used;
    auto writes = std::make_shared<std::vector<TimestampWrite>>(std::move(ts.writes));
    ts.samples = nil;
    ts.writes.clear();
    [c addCompletedHandler:^(id<MTLCommandBuffer>) {
        NSData* data = samples && used ? [samples resolveCounterRange:NSMakeRange(0, used)] : nil;
        const uint64_t* stamps = data ? (const uint64_t*)data.bytes : nullptr;
        for (auto& w : *writes) {
            uint64_t v = stamps && w.sample < used ? stamps[w.sample] : 0;
            if (v == MTLCounterErrorValue) v = 0;
            std::lock_guard<std::mutex> lock(w.pool->m);
            if (w.query < w.pool->count) {
                w.pool->values[w.query] = v;
                w.pool->available[w.query] = 1;
            }
            w.pool->cv.notify_all();
        }
        recycleSamples(samples);
    }];
}

void KCommandBuffer::releaseTimestamps() {
    recycleSamples(ts.samples);
    ts.samples = nil;
    ts.writes.clear();
    ts.deferred.clear();
    ts.split = false;
}

VKFN(void) vkCmdResetQueryPool(VkCommandBuffer, VkQueryPool pool, uint32_t first, uint32_t n) {
    KTRACE_FIRST();
    ((KQueryPool*)pool)->reset(first, n);
}

VKFN(void) vkCmdWriteTimestamp(VkCommandBuffer commandBuffer, VkPipelineStageFlagBits, VkQueryPool pool, uint32_t query) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->writeTimestamp((KQueryPool*)pool, query);
}

VKFN(void) vkCmdWriteTimestamp2(VkCommandBuffer commandBuffer, VkPipelineStageFlags2, VkQueryPool pool, uint32_t query) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->writeTimestamp((KQueryPool*)pool, query);
}

// The game's timestamps since the last draw, while zones are split: the pass ends there and goes on after them.
// Timestamps back to back split it once, and those after its last draw are taken when it ends, its stores and resolves
// in the zone they close; what is encoded between a timestamp and the next draw counts in the zone before it
void KCommandBuffer::stampSplit() {
    if (!ts.split) return;
    suspendRender();
    takeSplitStamps();
    resumeRender();
}

// The split's timestamps, the pass suspended (each waits for the first half through stampTimestamp's event)
void KCommandBuffer::takeSplitStamps() {
    if (!ts.split) return;
    ts.split = false;
    for (auto& d : ts.deferred) stampTimestamp(d.first, d.second);
    ts.deferred.clear();
}
