// A pipeline stage made ready and compiled: translated (shaders/translate.mm) or taken from the disk cache, overridden,
// its MSL rewritten (shaders/mslpatch.cpp) and compiled (once per MSL text and device) into its function, on the
// pipeline workers; the pipelines' creation cost; early fragment tests' switch.
#include "compile.h"
#include "spirv_msl.hpp"
#include <memory>
#include <functional>
#include <thread>

using namespace SPIRV_CROSS_NAMESPACE;

// Pipeline creation cost, reported with the first in-game frame (kmtlReportPipelineTimes)
static std::atomic<int64_t> g_usCompile{0}, g_usPipeline{0};
static std::atomic<int> g_nCompile{0}, g_nCompileCached{0}, g_nPipeline{0};
static int64_t usSince(std::chrono::steady_clock::time_point t) {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - t).count();
}
void kmtlReportPipelineTimes() {
    kmtlCacheFlushRecent();
    int nTranslate;
    int64_t usTranslate;
    kmtlTranslateStats(&nTranslate, &usTranslate);
    KLOG(0, "pipeline cost so far: SPIRV-Cross %d modules %.2f s, MSL compile %d (+%d cached) %.2f s, pipeline states %d %.2f s",
         nTranslate, usTranslate / 1e6, g_nCompile.load(), g_nCompileCached.load(), g_usCompile / 1e6,
         g_nPipeline.load(), g_usPipeline / 1e6);
}

void kmtlNotePipelineState(std::chrono::steady_clock::time_point started) {
    g_usPipeline += usSince(started);
    g_nPipeline++;
}

void kmtlPipelineCounts(uint32_t* shaders, uint32_t* pipelines) {
    *shaders = (uint32_t)g_nCompile.load();
    *pipelines = (uint32_t)g_nPipeline.load();
}

// ---------------------------------------------------------------------------------------------------------------
// MSL compilation (cached per device by MSL text hash)

static void dumpMSL(const std::string& name, const std::string& msl, const std::string& err) {
    const char* dir = kmtlConfig().dumpMsl;
    if (!dir) return;
    std::string text = err.empty() ? msl : msl + "\n/* COMPILE ERROR:\n" + err + "\n*/\n";
    kmtlDumpFile(dir, name + ".metal", text.data(), text.size());
}

// One compile per MSL text: a second caller (the game and the prewarm thread) waits for the first.
static id<MTLLibrary> compileMSL(KDevice* dev, const std::string& msl, std::string* err) {
    uint64_t h = kmtlHash(msl.data(), msl.size());
    std::shared_ptr<KDevice::LibraryEntry> entry;
    bool owner = false;
    {
        std::lock_guard<std::mutex> lock(dev->libraries.mutex);
        auto& slot = dev->libraries.cache[h];
        if (!slot) { slot = std::make_shared<KDevice::LibraryEntry>(); owner = true; }
        entry = slot;
    }
    if (!owner) {
        std::unique_lock<std::mutex> lock(entry->m);
        entry->cv.wait(lock, [&] { return entry->done; });
        g_nCompileCached++;
        if (!entry->lib && err) *err = entry->err;
        return entry->lib;
    }
    auto t0 = std::chrono::steady_clock::now();
    MTLCompileOptions* o = [MTLCompileOptions new];
    o.languageVersion = MTLLanguageVersion3_2;
    o.mathMode = kmtlConfig().preciseMath ? MTLMathModeSafe : MTLMathModeFast;
    o.preserveInvariance = YES;
    NSError* e = nil;
    id<MTLLibrary> lib = kmtlLibrary(dev->mtl, msl.c_str(), o, &e);
    g_usCompile += usSince(t0);
    g_nCompile++;
    std::lock_guard<std::mutex> lock(entry->m);
    entry->lib = lib;
    if (!lib) entry->err = e ? e.description.UTF8String : "unknown error";
    if (!lib && err) *err = entry->err;
    entry->done = true;
    entry->cv.notify_all();
    return lib;
}

// A stage's MSL with its uniform structs read in place, or as translated when that doesn't compile
id<MTLLibrary> kmtlCompileStageMSL(KDevice* dev, const std::string& msl, std::string* err) {
    if (kmtlConfig().inPlaceShaderParameters) {
        std::string refs = kmtlMslUniformRefs(msl);
        if (refs != msl) {
            std::string refErr;
            if (id<MTLLibrary> lib = compileMSL(dev, refs, &refErr)) return lib;
            KLOG(1, "uniform struct references don't compile, the translation as it was instead: %s", refErr.c_str());
        }
    }
    return compileMSL(dev, msl, err);
}

// ---------------------------------------------------------------------------------------------------------------
// Stages: translation (translate.mm) or the disk cache, overrides, compilation

static char stageChar(spv::ExecutionModel m, KTessRole role) {
    if (role == KTessRole::VertexKernel) return 'k';
    switch (m) {
    case spv::ExecutionModelTessellationControl: return 'h';
    case spv::ExecutionModelTessellationEvaluation: return 'd';
    case spv::ExecutionModelVertex: return 'v';
    case spv::ExecutionModelFragment: return 'f';
    case spv::ExecutionModelGLCompute: return 'c';
    default: return 'x';
    }
}

// Hand-written replacements for translated shaders (KSA_MTL_SHADER_OVERRIDES=<dir>): <dir>/<translation key>.metal
// for one exact translation, or <dir>/<module hash>_<stage><index type>.metal (the KSA_MTL_DUMP_MSL file name) for
// every translation of a module stage. The replacement keeps the translated interface: same entry point name, same
// argument buffer bindings (the set layouts fix them). Specialization values come from the translation it replaces
static void applyOverride(const char* name, uint64_t key, KTranslated* tr) {
    const char* dir = kmtlConfig().shaderOverrides;
    if (!dir) return;
    char path[1200];
    snprintf(path, sizeof(path), "%s/%016llx.metal", dir, (unsigned long long)key);
    std::string msl;
    if (!kmtlReadFile(path, &msl)) {
        snprintf(path, sizeof(path), "%s/%s.metal", dir, name);
        if (!kmtlReadFile(path, &msl)) return;
    }
    // One file for every translation of a module stage holds the specialization values of the translation it was copied
    // from: those lines are taken from this one
    tr->msl = kmtlMslKeepSpecialization(msl, tr->msl);
    KLOG(0, "shader override: %s", path);
}

// Translation of one stage (SPIRV-Cross or the disk cache, then overrides): needs the create info, so it runs on the
// calling thread. *name is the stage's dump/override name
bool kmtlPrepareStage(const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model, KPipelineLayout* layout,
                      const KStageOptions& so, KTranslated* tr, std::string* outName) {
    auto* mod = (KShaderModule*)st->module;
    if (!mod) {
        KERR("pipeline stage without a module (inline SPIR-V not supported)");
        return false;
    }
    std::string err;
    char name[64];
    snprintf(name, sizeof(name), "%016llx_%c%d", (unsigned long long)mod->hash, stageChar(model, so.tess), (int)so.indexType);
    *outName = name;
    // More sets than maxBoundDescriptorSets: the last ones would take the push constants' buffer index. KSA skips a
    // pipeline that fails to build (2026.10.25), and the driver draws nothing with it otherwise.
    if (layout && layout->sets.size() > kMaxSets) {
        KERR("shader %s: the pipeline layout has %zu descriptor sets, more than %u", name, layout->sets.size(), (unsigned)kMaxSets);
        return false;
    }
    uint64_t key = kmtlTranslationKey(mod, st->pName, model, layout, st->pSpecializationInfo, so);
    // A corpus records this build's translations, not the cache's
    if (kmtlConfig().shaderCorpus || !kmtlCacheLoad(key, tr)) {
        if (!kmtlTranslate(mod, st->pName, model, layout, st->pSpecializationInfo, so, tr, &err)) {
            KERR("shader %s: %s", name, err.c_str());
            dumpMSL(name, "", err);
            return false;
        }
        kmtlCacheStore(key, *tr);
    }
    kmtlCacheNoteUsed(key);
    if (kmtlConfig().shaderCorpus) kmtlCorpusRecord(key, mod, st->pName, model, layout, st->pSpecializationInfo, so, *tr);
    applyOverride(name, key, tr);
    return true;
}

// Early fragment tests (KSA_MTL_EARLY_FRAGMENT_TESTS, on by default). A fragment shader that stores to an image runs
// before the depth test, as Vulkan and Metal require for its stores, so every hidden fragment of its draws is shaded and
// the GPU's hidden surface removal can't skip them. KSA's vessel shaders store only for selected parts, into the mask
// the selection outline is drawn from (selectedMap). Such a pipeline also gets a state whose fragment function tests
// depth first, which draws bind while the hooks say no part is selected; with nothing selected the stores never run, so
// the image is the same. Only for shaders whose every store is to that mask, without atomics, writable buffers,
// discard or outputs besides colour (depth, sample mask, stencil); the pipeline checks alpha to coverage.
static std::atomic<bool> g_earlyFragmentTests{false};

KMTL_EXPORT void ksaMtlEarlyFragmentTests(bool on) { g_earlyFragmentTests.store(on, std::memory_order_relaxed); }

bool kmtlEarlyFragmentTests() { return g_earlyFragmentTests.load(std::memory_order_relaxed); }

// MSL compile and function of a prepared stage: thread-safe, so asynchronous pipeline creation runs it on workers. A
// ray tracing kernel traces with intersectors (KSA_MTL_IVA_RAY_SEARCH), or as translated when that doesn't compile;
// earlyTests: a fragment function with early fragment tests (kmtlMslStoresOnlySelection)
id<MTLFunction> kmtlCompileStage(KDevice* dev, const KTranslated& t, const std::string& nameStr, bool rtLaunch,
                                 bool earlyTests) {
    const KTranslated* tr = &t;
    const std::string dumpName = earlyTests ? nameStr + "_early" : nameStr;
    const char* name = dumpName.c_str();
    std::string err, msl = tr->msl;
    if (earlyTests && (msl = kmtlMslWithEarlyTests(msl)).empty()) return nil;
    id<MTLLibrary> lib = nil;
    if (rtLaunch && kmtlConfig().ivaRaySearch) {
        std::string rt = kmtlMslRtIntersectors(msl), rtErr;
        if (rt != msl) {
            if ((lib = kmtlCompileStageMSL(dev, rt, &rtErr))) msl = rt;
            else KLOG(1, "ray tracing kernel with intersectors doesn't compile, the translation as it was instead: %s", rtErr.c_str());
        }
    }
    if (!lib) lib = kmtlCompileStageMSL(dev, msl, &err);
    dumpMSL(name, kmtlConfig().inPlaceShaderParameters ? kmtlMslUniformRefs(msl) : msl, err);
    if (!lib) {
        KERR("shader %s: MSL compile failed: %s", name, err.c_str());
        return nil;
    }
    NSString* entry = [NSString stringWithUTF8String:tr->entry.c_str()];
    id<MTLFunction> fn = [lib newFunctionWithName:entry];
    if (!fn) {
        KERR("shader %s: entry point %s not found", name, tr->entry.c_str());
        return nil;
    }
    if (fn.functionConstantsDictionary.count) {
        // Specialization constants are baked into the IR defaults; leaving the function constants undefined
        // selects those defaults in SPIRV-Cross's output
        NSError* e = nil;
        fn = [lib newFunctionWithName:entry constantValues:[MTLFunctionConstantValues new] error:&e];
        if (!fn) KERR("shader %s: specialization failed: %s", name, e.description.UTF8String);
    }
    return fn;
}

unsigned kmtlWorkerCount() {
    unsigned cores = std::thread::hardware_concurrency();
    return cores > 2 ? cores - 2 : 1;
}

// Asynchronous pipeline creation (KSA_MTL_BACKGROUND_SHADER_COMPILING=0 turns it off): KSA creates its pipelines one
// at a time, so on a cold shader cache the MSL compiles run serially; workers compile them in parallel instead, and
// the first bind of a pipeline waits for its compile
void kmtlRunPipelineJob(std::function<void()> job) {
    static dispatch_queue_t queue = dispatch_queue_create("ksa-metal.pipelines", DISPATCH_QUEUE_CONCURRENT);
    static dispatch_semaphore_t slots = dispatch_semaphore_create(kmtlWorkerCount());
    dispatch_async(queue, ^{
        dispatch_semaphore_wait(slots, DISPATCH_TIME_FOREVER);
        @autoreleasepool { job(); }
        dispatch_semaphore_signal(slots);
    });
}

id<MTLFunction> kmtlMakeFunction(KDevice* dev, const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model,
                                 KPipelineLayout* layout, const KStageOptions& so, KTranslated* tr) {
    std::string name;
    if (!kmtlPrepareStage(st, model, layout, so, tr, &name)) return nil;
    return kmtlCompileStage(dev, *tr, name);
}
