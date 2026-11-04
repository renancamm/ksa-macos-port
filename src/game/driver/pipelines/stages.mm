// Shader modules, MSL compilation (cached per device by the MSL's text), and a pipeline stage made ready: translated
// (shaders/translate.mm) or taken from the disk cache, overridden, compiled into its function.
#include "stages.h"
#include "spirv_msl.hpp"
#include <sys/stat.h>
#include <memory>
#include <set>
#include <regex>
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

uint64_t kmtlHash(const void* data, size_t size) {
    // FNV-1a 64
    uint64_t h = 1469598103934665603ull;
    auto* p = (const uint8_t*)data;
    for (size_t i = 0; i < size; i++) { h ^= p[i]; h *= 1099511628211ull; }
    return h;
}

// ---------------------------------------------------------------------------------------------------------------
// Shader modules

VKFN(VkResult) vkCreateShaderModule(VkDevice, const VkShaderModuleCreateInfo* ci, const VkAllocationCallbacks*, VkShaderModule* out) {
    KTRACE_FIRST();
    auto* m = new KShaderModule();
    m->spirv.assign(ci->pCode, ci->pCode + ci->codeSize / 4);
    m->hash = kmtlHash(ci->pCode, ci->codeSize);
    if (const char* dir = kmtlConfig().dumpSpv) {
        char name[32];
        snprintf(name, sizeof(name), "%016llx.spv", (unsigned long long)m->hash);
        kmtlDumpFile(dir, name, ci->pCode, ci->codeSize, true);
    }
    *out = (VkShaderModule)m;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyShaderModule(VkDevice, VkShaderModule m, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KShaderModule*)m;
}

// ---------------------------------------------------------------------------------------------------------------
// MSL compilation (cached per device by MSL text hash)

void kmtlDumpFile(const char* dir, const std::string& name, const void* data, size_t size, bool keep) {
    mkdir(dir, 0755);
    std::string path = std::string(dir) + "/" + name;
    struct stat st;
    if (keep && stat(path.c_str(), &st) == 0) return;
    if (FILE* f = fopen(path.c_str(), "wb")) {
        fwrite(data, 1, size, f);
        fclose(f);
    }
}

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
        std::lock_guard<std::mutex> lock(dev->libraryMutex);
        auto& slot = dev->libraryCache[h];
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
    o.mathMode = kmtlConfig().preciseMath ? MTLMathModeSafe : kmtlConfig().relaxedMath ? MTLMathModeRelaxed : MTLMathModeFast;
    o.preserveInvariance = YES;
    NSError* e = nil;
    id<MTLLibrary> lib = [dev->mtl newLibraryWithSource:[NSString stringWithUTF8String:msl.c_str()] options:o error:&e];
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

// Uniform structs read in place (KSA_MTL_IN_PLACE_SHADER_PARAMETERS, on by default). GLSL passes structs by value,
// and SPIRV-Cross gives every helper that takes a uniform block's struct a copy in a local: "T _N; _N.a = ubo.s.a;
// ..." field by field, or "T param = p;", at each call, inside raymarch loops too. KSA's trail parameters are 1.2 KB
// with their arrays, and a copy whose arrays a helper indexes lives on the stack. Here the copies become references
// to the block (its std140 twin T_1 has the same fields) and the helpers take "constant T_1&". Only copies of an
// expression rooted in a name declared "constant X& name" are touched; anything this makes invalid (a copy that is
// written to, a helper also called with a local struct) fails to compile, and kmtlCompileStageMSL then compiles the
// translation as it was.
static bool isIdent(char c) { return isalnum((unsigned char)c) || c == '_'; }

// Whether T's block twin is a member of a struct that is read as "constant S&" and never as "device S&" (a storage
// buffer's struct would need device references instead), or of another struct's twin that is
static bool inConstantBlock(const std::string& msl, const std::string& t) {
    bool constant = false;
    const std::string member = "    " + t + "_1 ";
    for (size_t at = 0; (at = msl.find(member, at)) != std::string::npos; at += member.size()) {
        size_t open = msl.rfind("\nstruct ", at);
        if (open == std::string::npos) continue;
        size_t b = open + 8, e = msl.find('\n', b);
        std::string s = msl.substr(b, e - b);
        if (msl.find("device " + s + "&") != std::string::npos) return false;
        if (msl.find("constant " + s + "&") != std::string::npos) constant = true;
        // Nested: a member of another block twin
        else if (s.size() > 2 && s.compare(s.size() - 2, 2, "_1") == 0 && s != t + "_1" &&
                 msl.find("\nstruct " + s.substr(0, s.size() - 2) + "\n") != std::string::npos &&
                 inConstantBlock(msl, s.substr(0, s.size() - 2)))
            constant = true;
    }
    return constant;
}

static std::string uniformStructRefs(const std::string& msl) {
    // Structs with a block-layout twin
    std::vector<std::string> twins;
    for (size_t at = 0; (at = msl.find("\nstruct ", at)) != std::string::npos; at += 8) {
        size_t b = at + 8, e = b;
        while (e < msl.size() && isIdent(msl[e])) e++;
        if (e - b > 2 && msl.compare(e - 2, 2, "_1") == 0 && msl[e] == '\n') {
            std::string t = msl.substr(b, e - b - 2);
            if (msl.find("\nstruct " + t + "\n") != std::string::npos && inConstantBlock(msl, t)) twins.push_back(t);
        }
    }
    if (twins.empty()) return msl;
    std::string out = msl;
    for (const std::string& t : twins) {
        const std::string from = "thread const " + t + "& ", to = "constant " + t + "_1& ";
        for (size_t at = 0; (at = out.find(from, at)) != std::string::npos; at += to.size()) out.replace(at, from.size(), to);
    }
    // Names declared "constant X& name"
    std::set<std::string> roots;
    for (size_t at = 0; (at = out.find("constant ", at)) != std::string::npos; at += 9) {
        if (at > 0 && isIdent(out[at - 1])) continue;
        size_t amp = out.find("& ", at);
        if (amp == std::string::npos || out.find_first_of(",;(){}\n", at) < amp) continue;
        size_t b = amp + 2, e = b;
        while (e < out.size() && isIdent(out[e])) e++;
        if (e > b) roots.insert(out.substr(b, e - b));
    }
    auto rooted = [&](const std::string& expr) {
        size_t b = expr.compare(0, 2, "(*") == 0 ? 2 : 0, e = b;
        while (e < expr.size() && isIdent(expr[e])) e++;
        if (e == b || !roots.count(expr.substr(b, e - b))) return false;
        for (char c : expr) if (!isIdent(c) && !strchr(".[]()*", c)) return false;
        return true;
    };
    auto split = [](const std::string& text) {
        std::vector<std::string> lines;
        for (size_t b = 0; b <= text.size();) {
            size_t e = text.find('\n', b);
            if (e == std::string::npos) e = text.size();
            lines.push_back(text.substr(b, e - b));
            b = e + 1;
        }
        return lines;
    };
    auto join = [](const std::vector<std::string>& lines) {
        std::string text;
        for (size_t i = 0; i < lines.size(); i++) { text += lines[i]; if (i + 1 < lines.size()) text += '\n'; }
        return text;
    };
    // The struct a line declares: "<indent>T name..." for a twin T, its name's end in *nameEnd
    auto declares = [&](const std::string& l, std::string* name, size_t* nameEnd) -> const std::string* {
        size_t ind = l.find_first_not_of(' ');
        if (ind == std::string::npos) return nullptr;
        for (const std::string& t : twins) {
            if (l.compare(ind, t.size() + 1, t + " ") != 0) continue;
            size_t nb = ind + t.size() + 1, ne = nb;
            while (ne < l.size() && isIdent(l[ne])) ne++;
            if (ne == nb) return nullptr;
            *name = l.substr(nb, ne - nb);
            *nameEnd = ne;
            return &t;
        }
        return nullptr;
    };
    // Pass 1: "T _N;" then "_N.f = SRC.f;" for every field: _N becomes SRC
    std::vector<std::string> lines = split(out), kept;
    std::vector<std::pair<std::string, std::string>> renames;
    for (size_t i = 0; i < lines.size(); i++) {
        const std::string& l = lines[i];
        std::string name;
        size_t ne;
        if (declares(l, &name, &ne) && ne == l.size() - 1 && l[ne] == ';') {
            std::string src;
            size_t j = i + 1;
            for (; j < lines.size(); j++) {
                const std::string& f = lines[j];
                size_t fi = f.find_first_not_of(' ');
                if (fi == std::string::npos || f.compare(fi, name.size() + 1, name + ".") != 0) break;
                size_t eq = f.find(" = ", fi);
                if (eq == std::string::npos || f.back() != ';') break;
                std::string field = f.substr(fi + name.size(), eq - fi - name.size());  // ".f" or ".f[2]"
                std::string rhs = f.substr(eq + 3, f.size() - eq - 4);
                if (rhs.size() <= field.size() || rhs.compare(rhs.size() - field.size(), field.size(), field) != 0) break;
                std::string s2 = rhs.substr(0, rhs.size() - field.size());
                if (!src.empty() && s2 != src) break;
                src = s2;
            }
            if (!src.empty() && j > i + 1 && rooted(src)) {
                renames.emplace_back(name, src);
                i = j - 1;
                continue;
            }
        }
        kept.push_back(l);
    }
    std::string joined = join(kept);
    for (auto& [name, src] : renames) {
        std::string r;
        r.reserve(joined.size());
        for (size_t at = 0;;) {
            size_t f = joined.find(name, at);
            if (f == std::string::npos) { r.append(joined, at, std::string::npos); break; }
            bool whole = (f == 0 || !isIdent(joined[f - 1])) && (f + name.size() >= joined.size() || !isIdent(joined[f + name.size()]));
            r.append(joined, at, f - at);
            r += whole ? src : name;
            at = f + name.size();
        }
        joined.swap(r);
    }
    // Pass 2: "T x = SRC;" becomes "constant T_1& x = SRC;"
    lines = split(joined);
    bool changed = !renames.empty() || out != msl;
    for (std::string& l : lines) {
        std::string name;
        size_t ne;
        const std::string* t = declares(l, &name, &ne);
        if (!t || l.compare(ne, 3, " = ") != 0 || l.back() != ';') continue;
        std::string src = l.substr(ne + 3, l.size() - ne - 4);
        if (!rooted(src)) continue;
        l = l.substr(0, l.find_first_not_of(' ')) + "constant " + *t + "_1& " + name + " = " + src + ";";
        roots.insert(name);
        changed = true;
    }
    return changed ? join(lines) : msl;
}

// A stage's MSL with its uniform structs read in place, or as translated when that doesn't compile
id<MTLLibrary> kmtlCompileStageMSL(KDevice* dev, const std::string& msl, std::string* err) {
    if (kmtlConfig().inPlaceShaderParameters) {
        std::string refs = uniformStructRefs(msl);
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
    FILE* f = fopen(path, "rb");
    if (!f) {
        snprintf(path, sizeof(path), "%s/%s.metal", dir, name);
        f = fopen(path, "rb");
    }
    if (!f) return;
    std::string msl;
    char buf[65536];
    for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) msl.append(buf, n);
    fclose(f);
    // A translation bakes specialization values in as function constant defaults
    // ("constant T x = is_function_constant_defined(x_tmp) ? x_tmp : <value>;"): one file for every translation of a
    // module stage holds the values of the translation it was copied from, so those lines are taken from this one
    for (size_t at = 0; (at = tr->msl.find("is_function_constant_defined(", at)) != std::string::npos; at++) {
        size_t ls = tr->msl.rfind('\n', at) + 1, le = tr->msl.find('\n', at);
        size_t eq = tr->msl.find(" = ", ls);
        if (eq == std::string::npos || eq > at) continue;
        std::string head = tr->msl.substr(ls, eq - ls), line = tr->msl.substr(ls, le - ls);
        size_t os = msl.find(head + " = ");
        if (os != std::string::npos) msl.replace(os, msl.find('\n', os) - os, line);
    }
    tr->msl = msl;
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

static size_t countOf(const std::string& text, const char* what) {
    size_t n = 0, len = strlen(what);
    for (size_t at = 0; (at = text.find(what, at)) != std::string::npos; at += len) n++;
    return n;
}

bool kmtlStoresFromFragment(const std::string& msl) {
    size_t stores = countOf(msl, ".write(");
    // Writable buffers: a "device " that isn't "const device ", but for spvUnsafeArray's own accessor
    for (size_t at = 0; (at = msl.find("device ", at)) != std::string::npos; at += 7) {
        bool constant = at >= 6 && msl.compare(at - 6, 6, "const ") == 0;
        if (!constant && msl.compare(at + 7, 14, "T& operator []") != 0) return false;
    }
    return msl.find("\nfragment ") != std::string::npos && stores > 0 && stores == countOf(msl, "selectedMap.write(") &&
           msl.find("atomic_") == std::string::npos && msl.find("discard_fragment") == std::string::npos &&
           msl.find("[[depth(") == std::string::npos && msl.find("[[sample_mask]]") == std::string::npos &&
           msl.find("[[stencil]]") == std::string::npos && msl.find("early_fragment_tests") == std::string::npos;
}

// MSL compile and function of a prepared stage: thread-safe, so asynchronous pipeline creation runs it on workers. A
// ray tracing kernel traces with intersectors (KSA_MTL_IVA_RAY_SEARCH), or as translated when that doesn't compile;
// earlyTests: a fragment function with early fragment tests (kmtlStoresFromFragment)
id<MTLFunction> kmtlCompileStage(KDevice* dev, const KTranslated& t, const std::string& nameStr, bool rtLaunch,
                                 bool earlyTests) {
    const KTranslated* tr = &t;
    const std::string dumpName = earlyTests ? nameStr + "_early" : nameStr;
    const char* name = dumpName.c_str();
    std::string err, msl = tr->msl;
    if (earlyTests) {
        size_t at = msl.find("\nfragment ");
        if (at == std::string::npos) return nil;
        msl.insert(at + 1, "[[early_fragment_tests]] ");
    }
    id<MTLLibrary> lib = nil;
    if (rtLaunch && kmtlConfig().ivaRaySearch) {
        std::string rt = kmtlRtIntersectors(msl), rtErr;
        if (rt != msl) {
            if ((lib = kmtlCompileStageMSL(dev, rt, &rtErr))) msl = rt;
            else KLOG(1, "ray tracing kernel with intersectors doesn't compile, the translation as it was instead: %s", rtErr.c_str());
        }
    }
    if (!lib) lib = kmtlCompileStageMSL(dev, msl, &err);
    dumpMSL(name, kmtlConfig().inPlaceShaderParameters ? uniformStructRefs(msl) : msl, err);
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
