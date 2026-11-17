// Shader cache: SPIR-V -> MSL translations on disk, and a background compile of the previous run's shaders.
//
// Translations are pure functions of their key (see kmtlTranslationKey in translate.mm), so a hit skips SPIRV-Cross. The
// MSL front-end compile is cached by the system shader cache, which macOS flushes on updates; the prewarm compiles
// the shaders of the previous run on worker threads at device creation, so after such a flush the game's pipeline
// creation finds them compiled instead of compiling them one by one.
//
//   KSA_MTL_SHADER_CACHE=0    no disk cache, no prewarm
//   KSA_MTL_CACHE_DIR=<dir>   cache location (default ~/Library/Caches/Kitten Space Agency)
#include "kmtl.h"
#include <dirent.h>
#include <sys/stat.h>
#include <set>
#include <thread>

static const char* kFormat = "KSAMTL-TR 2";

static const std::string& cacheDir() {
    static const std::string dir = [] {
        if (!kmtlConfig().shaderCache) return std::string();
        std::string d;
        if (const char* e = kmtlConfig().cacheDir) d = e;
        else if (const char* home = getenv("HOME")) d = std::string(home) + "/Library/Caches/Kitten Space Agency";
        else return std::string();
        mkdir(d.c_str(), 0755);
        mkdir((d + "/tr").c_str(), 0755);
        return d;
    }();
    return dir;
}

static std::string entryPath(uint64_t key) {
    char name[40];
    snprintf(name, sizeof(name), "/tr/%016llx", (unsigned long long)key);
    return cacheDir() + name;
}

std::string kmtlTranslatedText(const KTranslated& t) {
    char flags[160];
    snprintf(flags, sizeof(flags), "%d %lu %lu %lu %u %d %d %d %d %d %d\n", (int)t.usesDrawId, (unsigned long)t.workgroup.width,
             (unsigned long)t.workgroup.height, (unsigned long)t.workgroup.depth, t.outputVertices, (int)t.quads, (int)t.cw,
             (int)t.partition, (int)t.hasPartition, (int)t.hasWinding, (int)t.hasDomain);
    return std::string(kFormat) + "\n" + t.entry + "\n" + flags + t.msl;
}

bool kmtlTranslatedParse(const std::string& data, KTranslated* out) {
    // Header line, then: entry, flags line, MSL (rest of the file)
    size_t a = data.find('\n'), b = a == std::string::npos ? a : data.find('\n', a + 1);
    size_t c = b == std::string::npos ? b : data.find('\n', b + 1);
    if (c == std::string::npos || data.compare(0, a, kFormat) != 0) return false;
    KTranslated t;
    t.entry = data.substr(a + 1, b - a - 1);
    unsigned long wx, wy, wz;
    int drawId, quads, cw, partition, hasPartition, hasWinding, hasDomain;
    unsigned outputVertices;
    if (sscanf(data.c_str() + b + 1, "%d %lu %lu %lu %u %d %d %d %d %d %d", &drawId, &wx, &wy, &wz, &outputVertices, &quads, &cw,
               &partition, &hasPartition, &hasWinding, &hasDomain) != 11)
        return false;
    t.usesDrawId = drawId;
    t.workgroup = MTLSizeMake(wx, wy, wz);
    t.outputVertices = outputVertices;
    t.quads = quads;
    t.cw = cw;
    t.partition = (MTLTessellationPartitionMode)partition;
    t.hasPartition = hasPartition;
    t.hasWinding = hasWinding;
    t.hasDomain = hasDomain;
    t.msl = data.substr(c + 1);
    *out = std::move(t);
    return true;
}

bool kmtlCacheLoad(uint64_t key, KTranslated* out) {
    if (cacheDir().empty()) return false;
    std::string data;
    return kmtlReadFile(entryPath(key), &data) && kmtlTranslatedParse(data, out);
}

void kmtlCacheStore(uint64_t key, const KTranslated& t) {
    if (cacheDir().empty()) return;
    kmtlWriteFileAtomically(entryPath(key), kmtlTranslatedText(t));
}

// Keys used by this run, in first-use order: the next run prewarms them
namespace {
struct RecentKeys {
    std::mutex m;  // all of it
    std::vector<uint64_t> keys;  // the keys this run used, in first-use order: the next run prewarms them
    std::set<uint64_t> seen;     // the same, to find a key fast
    size_t flushed = 0;  // keys written to the recent list
};
RecentKeys& g_recent = *new RecentKeys;
}  // namespace

void kmtlCacheNoteUsed(uint64_t key) {
    if (cacheDir().empty()) return;
    bool flush;
    {
        std::lock_guard<std::mutex> lock(g_recent.m);
        if (!g_recent.seen.insert(key).second) return;
        g_recent.keys.push_back(key);
        // Pipelines created after the first in-game frame are added in batches
        flush = g_recent.flushed && g_recent.keys.size() >= g_recent.flushed + 64;
    }
    if (flush) kmtlCacheFlushRecent();
}

void kmtlCacheFlushRecent() {
    if (cacheDir().empty()) return;
    std::lock_guard<std::mutex> lock(g_recent.m);
    std::string list;
    char line[24];
    for (uint64_t k : g_recent.keys) {
        snprintf(line, sizeof line, "%016llx\n", (unsigned long long)k);
        list += line;
    }
    if (!kmtlWriteFileAtomically(cacheDir() + "/recent", list)) return;
    g_recent.flushed = g_recent.keys.size();
}

void kmtlShaderPrewarm(KDevice* dev) {
    if (cacheDir().empty()) return;
    std::string list;
    if (!kmtlReadFile(cacheDir() + "/recent", &list)) return;
    auto keys = std::make_shared<std::vector<uint64_t>>();
    for (size_t pos = 0; pos < list.size();) {
        size_t nl = list.find('\n', pos);
        if (nl == std::string::npos) nl = list.size();
        if (nl > pos) keys->push_back(strtoull(list.c_str() + pos, nullptr, 16));
        pos = nl + 1;
    }
    if (keys->empty()) return;
    // Threads of their own, beside the pipeline workers: the game's first pipelines don't queue behind the prewarm
    unsigned workers = kmtlWorkerCount();
    auto next = std::make_shared<std::atomic<size_t>>(0);
    auto done = std::make_shared<std::atomic<unsigned>>(0);
    auto t0 = std::chrono::steady_clock::now();
    KLOG(1, "shader prewarm: %zu shaders on %u threads", keys->size(), workers);
    for (unsigned w = 0; w < workers; w++) {
        std::thread([dev, keys, next, done, workers, t0] {
            @autoreleasepool {
                for (size_t i; (i = (*next)++) < keys->size();) {
                    @autoreleasepool {
                        KTranslated t;
                        if (!kmtlCacheLoad((*keys)[i], &t)) continue;
                        std::string err;
                        kmtlCompileStageMSL(dev, t.msl, &err);
                    }
                }
            }
            if (++*done == workers)
                KLOG(0, "shader prewarm: %zu shaders in %.2f s", keys->size(),
                     std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count());
        }).detach();
    }
}
