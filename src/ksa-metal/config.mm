// The KSA_MTL_* environment variables, read once into KConfig (kmtl.h). README.md documents them: "Configuration" has
// the switches, "Test" the debugging variables.
#include "kmtl.h"
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
        c.budgetMb = envInt("KSA_MTL_BUDGET_MB", 0);
        c.presentPacing = envOn("KSA_MTL_PRESENT_PACING", true);
        // Debugging
        c.logLevel = envInt("KSA_MTL_LOG", 0);
        c.logFile = env("KSA_MTL_LOG_FILE");
        c.strictPipelines = envOn("KSA_MTL_STRICT", false);
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
        c.gpuprof = env("KSA_MTL_GPUPROF");
        c.gpuprofFrames = env("KSA_MTL_GPUPROF_FRAMES");
        c.gpuprofSplit = envOn("KSA_MTL_GPUPROF_SPLIT", false);
        c.labels = envOn("KSA_MTL_LABELS", false);
        return c;
    }();
    return config;
}
