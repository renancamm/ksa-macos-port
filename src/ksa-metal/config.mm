// The KSA_MTL_* environment variables, read once into KConfig (kmtl.h), and the log they set up (KSA_MTL_LOG,
// KSA_MTL_LOG_FILE). README.md documents them: "Configuration" has the switches, "Test" the debugging variables.
#include "kmtl.h"
#include "port-settings.h"
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
// 0 is off and 1 on; anything else means the default, as the port settings' table reads it
static bool envOn(const char* name, bool def) {
    const char* v = env(name);
    return v && !strcmp(v, "0") ? false : v && !strcmp(v, "1") ? true : def;
}
// A switch whose on value is a word of its own (KSA_MTL_SYNC=strict)
static bool envIs(const char* name, const char* on) {
    const char* v = env(name);
    return v && !strcmp(v, on);
}
static double envNumber(const char* name, double def) {
    const char* v = env(name);
    return v ? atof(v) : def;
}
// A switch whose default is another's value, not its own in the table
static bool envOnOr(const char* name, bool /* the table's default */, bool def) { return envOn(name, def); }

const KConfig& kmtlConfig() {
    static const KConfig config = [] {
        KConfig c;
        // Switches
        c.tessellation = envOn(PORT_SETTING_TESSELLATION);
        c.tessFuse = envOn(PORT_SETTING_FUSED_TESSELLATION);
        c.rayQuery = envOn(PORT_SETTING_RAY_QUERIES);
        c.rtPipeline = c.rayQuery && envOn(PORT_SETTING_RAY_TRACING);
        c.asyncPipelines = envOn(PORT_SETTING_BACKGROUND_SHADER_COMPILING);
        c.cache = envOn(PORT_SETTING_SHADER_CACHE);
        c.strictSync = envIs(PORT_SETTING_STRICT_SYNC);
        c.safeMath = envOn(PORT_SETTING_PRECISE_MATH);
        c.constantArgumentBuffers = envOn(PORT_SETTING_SHADER_CONSTANT_MEMORY);
        c.uncompressedTextures = envOn(PORT_SETTING_SKIP_GPU_TEXTURE_COMPRESSION);
        c.budgetMb = envInt(PORT_SETTING_TEXTURE_BUDGET_MB);
        c.presentPacing = envOn(PORT_SETTING_VSYNC_PACING);
        c.fxInterpolate = envOn(PORT_SETTING_FRAME_INTERPOLATION);
        c.hdr = envOn(PORT_SETTING_HDR_OUTPUT);
        // Debugging
        c.logLevel = envInt(PORT_SETTING_LOG);
        c.logFile = env(PORT_SETTING_LOG_FILE);
        c.strictPipelines = envOn(PORT_SETTING_STRICT_PIPELINES);
        // A pipeline that fails on a worker has a handle already: strict errors need the game's thread
        if (c.strictPipelines) c.asyncPipelines = false;
        c.dumpMsl = env(PORT_SETTING_DUMP_MSL);
        c.dumpSpv = env(PORT_SETTING_DUMP_SPV);
        c.corpus = env(PORT_SETTING_SHADER_CORPUS);
        c.overrides = env(PORT_SETTING_SHADER_OVERRIDES);
        c.spec = env(PORT_SETTING_SPEC);
        c.cacheDir = env(PORT_SETTING_CACHE_DIR);
        c.shots = env(PORT_SETTING_SHOTS);
        c.shotUnit = env(PORT_SETTING_SHOT_UNIT);
        c.shotDir = env(PORT_SETTING_SHOT_DIR);
        c.exitAfter = envInt(PORT_SETTING_EXIT_AFTER);
        c.exitAfterSeconds = envNumber(PORT_SETTING_EXIT_AFTER_S);
        c.exitAfterShots = envOn(PORT_SETTING_EXIT_AFTER_SHOTS);
        c.frames = env(PORT_SETTING_FRAMES);
        c.latency = env(PORT_SETTING_LATENCY);
        c.stallMs = envNumber(PORT_SETTING_STALLS);
        c.gpuprof = env(PORT_SETTING_GPU_PROFILE);
        c.gpuprofFrames = env(PORT_SETTING_GPU_PROFILE_FRAMES);
        c.gpuprofSplit = envOn(PORT_SETTING_GPU_PROFILE_SPLIT);
        c.labels = envOnOr(PORT_SETTING_LABELS, envOn(PORT_SETTING_FRAME_CAPTURE));
        c.gpuErrors = envOn("KSA_MTL_GPU_ERRORS", false);
        c.watch = env("KSA_MTL_WATCH");
        c.watchWhite = env("KSA_MTL_WATCH_WHITE") ? atof(env("KSA_MTL_WATCH_WHITE")) : 30;
        c.fxDebug = envOn(PORT_SETTING_FX_DEBUG);
        c.fxShots = env(PORT_SETTING_FX_SHOTS);
        c.throttle = env(PORT_SETTING_THROTTLE);
        c.uniformRefs = envOn(PORT_SETTING_IN_PLACE_SHADER_PARAMETERS);
        c.rtIntersector = envOn(PORT_SETTING_IVA_RAY_SEARCH);
        c.unlitShadowSkip = envOn(PORT_SETTING_SKIP_SHADOWS_ON_UNLIT_SIDES);
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
