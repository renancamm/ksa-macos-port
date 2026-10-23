// Shader cache: SPIR-V -> MSL translations on disk, and a background compile of the previous run's shaders.
//
// Translations are pure functions of their key (see translationKey in pipeline.mm), so a hit skips SPIRV-Cross. The
// MSL front-end compile is cached by the system shader cache, which macOS flushes on updates; the prewarm compiles
// the shaders of the previous run on worker threads at device creation, so after such a flush the game's pipeline
// creation finds them compiled instead of compiling them one by one.
//
//   KSA_MTL_CACHE=0          no disk cache, no prewarm
//   KSA_MTL_CACHE_DIR=<dir>  cache location (default ~/Library/Caches/ksa-metal)
#include "kmtl.h"
#include <dirent.h>
#include <sys/stat.h>
#include <set>
#include <thread>

static const char* kFormat = "KSAMTL-TR 1";

static const std::string& cacheDir() {
    static const std::string dir = [] {
        if (!kmtlEnvInt("KSA_MTL_CACHE", 1)) return std::string();
        std::string d;
        if (const char* e = kmtlEnv("KSA_MTL_CACHE_DIR")) d = e;
        else if (const char* home = getenv("HOME")) d = std::string(home) + "/Library/Caches/ksa-metal";
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

static bool readFile(const std::string& path, std::string* out) {
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return false;
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    out->resize(n > 0 ? (size_t)n : 0);
    bool ok = n <= 0 || fread(&(*out)[0], 1, (size_t)n, f) == (size_t)n;
    fclose(f);
    return ok;
}

bool kmtlCacheLoad(uint64_t key, KTranslated* out) {
    if (cacheDir().empty()) return false;
    std::string data;
    if (!readFile(entryPath(key), &data)) return false;
    // Header line, then: entry, flags line, MSL (rest of the file)
    size_t a = data.find('\n'), b = a == std::string::npos ? a : data.find('\n', a + 1);
    size_t c = b == std::string::npos ? b : data.find('\n', b + 1);
    if (c == std::string::npos || data.compare(0, a, kFormat) != 0) return false;
    KTranslated t;
    t.entry = data.substr(a + 1, b - a - 1);
    unsigned long wx, wy, wz;
    int drawId, quads, cw, partition, hasPartition, hasWinding, hasDomain, passthrough;
    unsigned outputVertices;
    if (sscanf(data.c_str() + b + 1, "%d %lu %lu %lu %u %d %d %d %d %d %d %d", &drawId, &wx, &wy, &wz, &outputVertices, &quads, &cw,
               &partition, &hasPartition, &hasWinding, &hasDomain, &passthrough) != 12)
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
    t.passthrough = passthrough;
    t.msl = data.substr(c + 1);
    *out = std::move(t);
    return true;
}

void kmtlCacheStore(uint64_t key, const KTranslated& t) {
    if (cacheDir().empty()) return;
    std::string path = entryPath(key);
    std::string tmp = path + ".tmp" + std::to_string((unsigned long)pthread_mach_thread_np(pthread_self()));
    FILE* f = fopen(tmp.c_str(), "wb");
    if (!f) return;
    fprintf(f, "%s\n%s\n%d %lu %lu %lu %u %d %d %d %d %d %d %d\n", kFormat, t.entry.c_str(), (int)t.usesDrawId,
            (unsigned long)t.workgroup.width, (unsigned long)t.workgroup.height, (unsigned long)t.workgroup.depth, t.outputVertices,
            (int)t.quads, (int)t.cw, (int)t.partition, (int)t.hasPartition, (int)t.hasWinding, (int)t.hasDomain, (int)t.passthrough);
    fwrite(t.msl.data(), 1, t.msl.size(), f);
    bool ok = fclose(f) == 0;
    if (ok) rename(tmp.c_str(), path.c_str());
    else unlink(tmp.c_str());
}

// Keys used by this run, in first-use order: the next run prewarms them
static std::mutex g_recentMutex;
static std::vector<uint64_t> g_recent;
static std::set<uint64_t> g_recentSet;
static size_t g_recentFlushed = 0;

void kmtlCacheNoteUsed(uint64_t key) {
    if (cacheDir().empty()) return;
    bool flush;
    {
        std::lock_guard<std::mutex> lock(g_recentMutex);
        if (!g_recentSet.insert(key).second) return;
        g_recent.push_back(key);
        // Pipelines created after the first in-game frame are added in batches
        flush = g_recentFlushed && g_recent.size() >= g_recentFlushed + 64;
    }
    if (flush) kmtlCacheFlushRecent();
}

void kmtlCacheFlushRecent() {
    if (cacheDir().empty()) return;
    std::lock_guard<std::mutex> lock(g_recentMutex);
    std::string tmp = cacheDir() + "/recent.tmp", path = cacheDir() + "/recent";
    FILE* f = fopen(tmp.c_str(), "w");
    if (!f) return;
    for (uint64_t k : g_recent) fprintf(f, "%016llx\n", (unsigned long long)k);
    if (fclose(f) == 0) rename(tmp.c_str(), path.c_str());
    g_recentFlushed = g_recent.size();
}

void kmtlShaderPrewarm(KDevice* dev) {
    if (cacheDir().empty()) return;
    std::string list;
    if (!readFile(cacheDir() + "/recent", &list)) return;
    auto keys = std::make_shared<std::vector<uint64_t>>();
    for (size_t pos = 0; pos < list.size();) {
        size_t nl = list.find('\n', pos);
        if (nl == std::string::npos) nl = list.size();
        if (nl > pos) keys->push_back(strtoull(list.c_str() + pos, nullptr, 16));
        pos = nl + 1;
    }
    if (keys->empty()) return;
    // Leave two cores to the game's loading threads
    unsigned workers = std::max(1u, std::thread::hardware_concurrency() > 2 ? std::thread::hardware_concurrency() - 2 : 1u);
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
                        kmtlCompileMSL(dev, t.msl, &err);
                    }
                }
            }
            if (++*done == workers)
                KLOG(0, "shader prewarm: %zu shaders in %.2f s", keys->size(),
                     std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count());
        }).detach();
    }
}
