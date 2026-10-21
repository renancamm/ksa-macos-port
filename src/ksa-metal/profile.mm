// GPU profiler: per-encoder GPU time from stage-boundary timestamps (Apple GPUs sample only at encoder stage
// boundaries, not per draw or dispatch; KSA_MTL_SERIAL=1 gives each dispatch its own encoder).
//
//   KSA_MTL_GPUPROF=<csv>   one row per encoder of every profiled frame:
//                           frame,cb,encoder,type,draws,dispatches,start_us,vertex_us,fragment_us,total_us,pipelines
//                           plus one "cb" row per command buffer (GPUStartTime..GPUEndTime)
//   KSA_MTL_GPUPROF_FRAMES=<first>,<count>   presents to profile (default 2000,300)
//   KSA_MTL_GPUPROF_SPLIT=1 end the render encoder after every draw call (per-draw attribution; adds a store and
//                           load of the attachments per draw, so absolute times grow)
//   KSA_MTL_DUMP_SPV=<dir>  every shader module as <hash>.spv (pipelines are named by these hashes)
#include "kmtl.h"
#include <sys/stat.h>

struct KProfEncoder {
    char type = '?';  // r render, c compute, b blit
    uint32_t sample = 0;
    uint32_t draws = 0, dispatches = 0;
    std::string pipelines;
    uint64_t lastPipeline = 0;
};

struct KProfState {
    id<MTLCounterSampleBuffer> samples = nil;
    uint32_t used = 0, capacity = 0;
    std::vector<KProfEncoder> encoders;
    int current = -1;
    uint64_t frame = 0;
};

static FILE* g_prof = nullptr;
static uint64_t g_profFirst = 2000, g_profCount = 300;
static std::atomic<uint64_t> g_profPresent{0};
static std::mutex g_profMutex;
static id<MTLCounterSet> g_timestampSet = nil;
static std::unordered_map<KCommandBuffer*, KProfState*> g_states;
// Metal caps the number of live counter sample buffers: they are recycled after their command buffer completes
static std::vector<id<MTLCounterSampleBuffer>> g_samplePool;

static bool profInit(KDevice* dev) {
    static std::once_flag once;
    std::call_once(once, [dev] {
        const char* path = kmtlEnv("KSA_MTL_GPUPROF");
        if (!path) return;
        if (![dev->mtl supportsCounterSampling:MTLCounterSamplingPointAtStageBoundary]) {
            KERR("GPU profiler: no stage-boundary counter sampling");
            return;
        }
        for (id<MTLCounterSet> cs in dev->mtl.counterSets)
            if ([cs.name isEqualToString:MTLCommonCounterSetTimestamp]) g_timestampSet = cs;
        if (!g_timestampSet) return;
        if (const char* f = kmtlEnv("KSA_MTL_GPUPROF_FRAMES")) sscanf(f, "%llu,%llu", (unsigned long long*)&g_profFirst, (unsigned long long*)&g_profCount);
        g_prof = fopen(path, "w");
        if (g_prof) fprintf(g_prof, "frame,cb,encoder,type,draws,dispatches,start_us,vertex_us,fragment_us,total_us,pipelines\n");
    });
    return g_prof != nullptr;
}

static bool profiling(KDevice* dev) {
    if (!profInit(dev)) return false;
    uint64_t p = g_profPresent.load();
    return p >= g_profFirst && p < g_profFirst + g_profCount;
}

void kmtlProfPresent() { g_profPresent++; }

static KProfState* state(KCommandBuffer* cb) {
    std::lock_guard<std::mutex> lock(g_profMutex);
    auto it = g_states.find(cb);
    return it == g_states.end() ? nullptr : it->second;
}

void kmtlProfBegin(KCommandBuffer* cb) {
    {
        std::lock_guard<std::mutex> lock(g_profMutex);
        auto it = g_states.find(cb);
        if (it != g_states.end()) { delete it->second; g_states.erase(it); }
    }
    if (!profiling(cb->device)) return;
    auto* s = new KProfState();
    {
        std::lock_guard<std::mutex> lock(g_profMutex);
        if (!g_samplePool.empty()) { s->samples = g_samplePool.back(); g_samplePool.pop_back(); }
    }
    if (!s->samples) {
        MTLCounterSampleBufferDescriptor* d = [MTLCounterSampleBufferDescriptor new];
        d.counterSet = g_timestampSet;
        d.storageMode = MTLStorageModeShared;
        d.sampleCount = 4096;
        NSError* e = nil;
        s->samples = [cb->device->mtl newCounterSampleBufferWithDescriptor:d error:&e];
        if (!s->samples) {
            static std::atomic<int> warned{0};
            if (warned++ < 3) KERR("GPU profiler: sample buffer: %s", e.description.UTF8String);
            delete s;
            return;
        }
    }
    s->capacity = 4096;
    s->frame = g_profPresent.load();
    std::lock_guard<std::mutex> lock(g_profMutex);
    g_states[cb] = s;
}

// Reserves samples for a new encoder; returns the first index or -1
static int newEncoder(KProfState* s, char type, uint32_t n) {
    if (s->used + n > s->capacity) { s->current = -1; return -1; }
    KProfEncoder e;
    e.type = type;
    e.sample = s->used;
    s->used += n;
    s->encoders.push_back(e);
    s->current = (int)s->encoders.size() - 1;
    return (int)e.sample;
}

void kmtlProfRenderPass(KCommandBuffer* cb, MTLRenderPassDescriptor* rp) {
    KProfState* s = state(cb);
    if (!s) return;
    int i = newEncoder(s, 'r', 4);
    if (i < 0) return;
    MTLRenderPassSampleBufferAttachmentDescriptor* a = rp.sampleBufferAttachments[0];
    a.sampleBuffer = s->samples;
    a.startOfVertexSampleIndex = i;
    a.endOfVertexSampleIndex = i + 1;
    a.startOfFragmentSampleIndex = i + 2;
    a.endOfFragmentSampleIndex = i + 3;
}

id<MTLComputeCommandEncoder> kmtlProfComputeEncoder(KCommandBuffer* cb) {
    KProfState* s = state(cb);
    int i = s ? newEncoder(s, 'c', 2) : -1;
    // KSA_MTL_CONCURRENT=1: dispatches between Vulkan barriers may overlap (barriers are memory barriers in the encoder)
    static const MTLDispatchType type = (kmtlEnvInt("KSA_MTL_CONCURRENT", 0) || kmtlEnvInt("KSA_MTL_NOSYNC", 0) == 3) ? MTLDispatchTypeConcurrent : MTLDispatchTypeSerial;
    if (i < 0) return [cb->mtl computeCommandEncoderWithDispatchType:type];
    MTLComputePassDescriptor* d = [MTLComputePassDescriptor computePassDescriptor];
    d.dispatchType = type;
    d.sampleBufferAttachments[0].sampleBuffer = s->samples;
    d.sampleBufferAttachments[0].startOfEncoderSampleIndex = i;
    d.sampleBufferAttachments[0].endOfEncoderSampleIndex = i + 1;
    return [cb->mtl computeCommandEncoderWithDescriptor:d];
}

id<MTLBlitCommandEncoder> kmtlProfBlitEncoder(KCommandBuffer* cb) {
    KProfState* s = state(cb);
    int i = s ? newEncoder(s, 'b', 2) : -1;
    if (i < 0) return [cb->mtl blitCommandEncoder];
    MTLBlitPassDescriptor* d = [MTLBlitPassDescriptor blitPassDescriptor];
    d.sampleBufferAttachments[0].sampleBuffer = s->samples;
    d.sampleBufferAttachments[0].startOfEncoderSampleIndex = i;
    d.sampleBufferAttachments[0].endOfEncoderSampleIndex = i + 1;
    return [cb->mtl blitCommandEncoderWithDescriptor:d];
}

// A draw or dispatch with this pipeline in the current encoder
void kmtlProfNote(KCommandBuffer* cb, KPipeline* p, bool dispatch, uint32_t n) {
    KProfState* s = state(cb);
    if (!s || s->current < 0) return;
    KProfEncoder& e = s->encoders[s->current];
    if (dispatch) e.dispatches += n; else e.draws += n;
    if (p && p->profId != e.lastPipeline) {
        if (!e.pipelines.empty()) e.pipelines += ' ';
        e.pipelines += p->profName;
        e.lastPipeline = p->profId;
    }
    static const bool split = kmtlEnvInt("KSA_MTL_GPUPROF_SPLIT", 0) != 0;
    if (split && !dispatch && cb->render && cb->deferredStores) {
        cb->suspendRender();
        cb->resumeRender();
    }
}

void kmtlProfEncoderEnded(KCommandBuffer* cb) {
    if (KProfState* s = state(cb)) s->current = -1;
}

void kmtlProfSubmit(KCommandBuffer* cb, id<MTLCommandBuffer> mtl) {
    KProfState* s;
    {
        std::lock_guard<std::mutex> lock(g_profMutex);
        auto it = g_states.find(cb);
        if (it == g_states.end()) return;
        s = it->second;
        g_states.erase(it);
    }
    static std::atomic<uint32_t> cbCounter{0};
    uint32_t cbIndex = cbCounter++;
    [mtl addCompletedHandler:^(id<MTLCommandBuffer> c) {
        NSData* data = s->used ? [s->samples resolveCounterRange:NSMakeRange(0, s->used)] : nil;
        const uint64_t* ts = data ? (const uint64_t*)data.bytes : nullptr;
        uint64_t base = (uint64_t)(c.GPUStartTime * 1e9);
        auto us = [](uint64_t a, uint64_t b) { return (a == MTLCounterErrorValue || b == MTLCounterErrorValue || b < a) ? -1.0 : (b - a) / 1000.0; };
        std::lock_guard<std::mutex> lock(g_profMutex);
        fprintf(g_prof, "%llu,%u,-1,cb,0,0,0,0,0,%.1f,\n", (unsigned long long)s->frame, cbIndex, (c.GPUEndTime - c.GPUStartTime) * 1e6);
        for (size_t i = 0; ts && i < s->encoders.size(); i++) {
            const KProfEncoder& e = s->encoders[i];
            const uint64_t* t = ts + e.sample;
            double vtx = -1, frag = -1, total;
            uint64_t start = t[0];
            if (e.type == 'r') {
                vtx = us(t[0], t[1]);
                frag = us(t[2], t[3]);
                uint64_t a = std::min(t[0], t[2]), b = 0;
                for (int k = 0; k < 4; k++) if (t[k] != MTLCounterErrorValue) b = std::max(b, t[k]);
                start = a;
                total = us(a, b);
            } else {
                total = us(t[0], t[1]);
            }
            double startUs = start == MTLCounterErrorValue || start < base ? -1 : (start - base) / 1000.0;
            fprintf(g_prof, "%llu,%u,%zu,%c,%u,%u,%.1f,%.1f,%.1f,%.1f,%s\n", (unsigned long long)s->frame, cbIndex, i, e.type,
                    e.draws, e.dispatches, startUs, vtx, frag, total, e.pipelines.c_str());
        }
        if (s->frame + 1 >= g_profFirst + g_profCount) fflush(g_prof);
        g_samplePool.push_back(s->samples);
        delete s;
    }];
}

void kmtlProfDumpSpirv(const uint32_t* code, size_t bytes, uint64_t hash) {
    static const char* dir = kmtlEnv("KSA_MTL_DUMP_SPV");
    if (!dir) return;
    mkdir(dir, 0755);
    char path[1200];
    snprintf(path, sizeof(path), "%s/%016llx.spv", dir, (unsigned long long)hash);
    struct stat st;
    if (stat(path, &st) == 0) return;
    if (FILE* f = fopen(path, "wb")) {
        fwrite(code, 1, bytes, f);
        fclose(f);
    }
}
