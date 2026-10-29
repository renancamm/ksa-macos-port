// The KSA_MTL_* environment variables, read once into KConfig (kmtl.h), and the log they set up (KSA_MTL_LOG,
// KSA_MTL_LOG_FILE). README.md documents them: "Configuration" has the switches, "Test" the debugging variables.
#include "kmtl.h"
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

static const char* env(const char* name) {
    const char* v = getenv(name);
    return v && *v ? v : nullptr;  // empty counts as unset
}
static int envInt(const char* name, int def) {
    const char* v = env(name);
    return v ? atoi(v) : def;
}
static bool envOn(const char* name, bool def) { return envInt(name, def) != 0; }

const KConfig& kmtlConfig() {
    static const KConfig config = [] {
        KConfig c;
        // Switches
        c.tessellation = envOn("KSA_MTL_TESSELLATION", true);
        c.tessFuse = envOn("KSA_MTL_TESS_FUSE", true);
        c.rayQuery = envOn("KSA_MTL_RAYQUERY", true);
        c.rtPipeline = c.rayQuery && envOn("KSA_MTL_RTPIPELINE", true);
        c.asyncPipelines = envOn("KSA_MTL_ASYNC_PIPELINES", true);
        c.cache = envOn("KSA_MTL_CACHE", true);
        const char* sync = env("KSA_MTL_SYNC");
        c.strictSync = sync && !strcmp(sync, "strict");
        c.safeMath = envOn("KSA_MTL_SAFE_MATH", false);
        c.constantArgumentBuffers = envOn("KSA_MTL_AB_CONSTANT", true);
        c.uncompressedTextures = envOn("KSA_MTL_UNCOMPRESSED_TEXTURES", true);
        c.budgetMb = envInt("KSA_MTL_BUDGET_MB", 0);
        c.presentPacing = envOn("KSA_MTL_PRESENT_PACING", true);
        c.fxInterpolate = envOn("KSA_MTL_FX_INTERPOLATE", false);
        // Debugging
        c.logLevel = envInt("KSA_MTL_LOG", 0);
        c.logFile = env("KSA_MTL_LOG_FILE");
        c.strictPipelines = envOn("KSA_MTL_STRICT", false);
        // A pipeline that fails on a worker has a handle already: strict errors need the game's thread
        if (c.strictPipelines) c.asyncPipelines = false;
        c.dumpMsl = env("KSA_MTL_DUMP_MSL");
        c.dumpSpv = env("KSA_MTL_DUMP_SPV");
        c.overrides = env("KSA_MTL_OVERRIDES");
        c.spec = env("KSA_MTL_SPEC");
        c.cacheDir = env("KSA_MTL_CACHE_DIR");
        c.shots = env("KSA_MTL_SHOTS");
        c.shotUnit = env("KSA_MTL_SHOT_UNIT");
        c.shotDir = env("KSA_MTL_SHOT_DIR");
        c.exitAfter = envInt("KSA_MTL_EXIT_AFTER", 0);
        c.exitAfterSeconds = env("KSA_MTL_EXIT_AFTER_S") ? atof(env("KSA_MTL_EXIT_AFTER_S")) : 0;
        c.exitAfterShots = envOn("KSA_MTL_EXIT_AFTER_SHOTS", false);
        c.frames = env("KSA_MTL_FRAMES");
        c.latency = env("KSA_MTL_LATENCY");
        if (const char* st = env("KSA_MTL_STALLS")) c.stallMs = atof(st);
        c.gpuprof = env("KSA_MTL_GPUPROF");
        c.gpuprofFrames = env("KSA_MTL_GPUPROF_FRAMES");
        c.gpuprofSplit = envOn("KSA_MTL_GPUPROF_SPLIT", false);
        c.labels = envOn("KSA_MTL_LABELS", false);
        c.fxDebug = envOn("KSA_MTL_FX_DEBUG", false);
        c.fxShots = env("KSA_MTL_FX_SHOTS");
        c.throttle = env("KSA_MTL_THROTTLE");
        c.uniformRefs = envOn("KSA_MTL_UNIFORM_REFS", true);
        c.rtIntersector = envOn("KSA_MTL_RT_INTERSECTOR", true);
        c.unlitShadowSkip = envOn("KSA_MTL_UNLIT_SHADOW_SKIP", true);
        c.earlyFragmentTests = envOn("KSA_MTL_EARLY_FRAGMENT_TESTS", true);
        return c;
    }();
    return config;
}

// KSA_MTL_THROTTLE=<hash>:<N>,...: a pipeline whose name holds one of the module hashes runs every N-th present
int kmtlThrottleFor(const std::string& profName) {
    const char* t = kmtlConfig().throttle;
    if (!t) return -1;
    std::string list = t;
    size_t pos = 0;
    while (pos < list.size()) {
        size_t end = list.find(',', pos);
        if (end == std::string::npos) end = list.size();
        std::string item = list.substr(pos, end - pos);
        size_t colon = item.find(':');
        if (colon != std::string::npos && profName.find(item.substr(0, colon)) != std::string::npos)
            return atoi(item.c_str() + colon + 1);
        pos = end + 1;
    }
    return -1;
}

// ---------------------------------------------------------------------------------------------------------------
// The log

int g_logLevel = 0;
static FILE* g_logFile = nullptr;

__attribute__((constructor)) static void kmtlInitLogging() {
    g_logLevel = kmtlConfig().logLevel;
    const char* path = kmtlConfig().logFile;
    g_logFile = path ? fopen(path, "w") : nullptr;
    if (!g_logFile) g_logFile = stdout;
    setvbuf(g_logFile, nullptr, _IOLBF, 0);
}

void kmtlLog(int level, const char* fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    static std::mutex m;
    std::lock_guard<std::mutex> lock(m);
    fprintf(g_logFile, "[ksa-metal%s] %s\n", level < 0 ? " ERROR" : "", buf);
}
