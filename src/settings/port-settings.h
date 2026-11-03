// The port settings of the native code, KSA Metal and the KTX shim: made from port-settings.txt by
// KsaPortSettingsGenerator (README.md in this folder), which build.sh runs. Edit the table, not this.
//
// portSettingsRead() reads each one from its variable, taking the values the settings' library takes: a switch
// 0 or 1, a number in the table's range, an int of digits only; unset, empty or anything else, the table's
// default. Text is NULL when unset. A variable set outside the game is taken without its setting's needs,
// which the hooks' loader applies to those it sets.
#pragma once

#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

typedef struct PortSettings {
    bool skipShadowsOnUnlitSides;           // KSA_MTL_SKIP_SHADOWS_ON_UNLIT_SIDES
    bool fusedTessellation;                 // KSA_MTL_FUSED_TESSELLATION
    bool skipGpuTextureCompression;         // KSA_MTL_SKIP_GPU_TEXTURE_COMPRESSION
    bool ivaRaySearch;                      // KSA_MTL_IVA_RAY_SEARCH
    bool vsyncPacing;                       // KSA_MTL_VSYNC_PACING
    bool backgroundShaderCompiling;         // KSA_MTL_BACKGROUND_SHADER_COMPILING
    bool shaderCache;                       // KSA_MTL_SHADER_CACHE
    bool shaderConstantMemory;              // KSA_MTL_SHADER_CONSTANT_MEMORY
    bool inPlaceShaderParameters;           // KSA_MTL_IN_PLACE_SHADER_PARAMETERS
    bool textureFileMapping;                // KSA_MTL_TEXTURE_FILE_MAPPING
    bool tessellation;                      // KSA_MTL_TESSELLATION
    bool rayQueries;                        // KSA_MTL_RAY_QUERIES
    bool rayTracing;                        // KSA_MTL_RAY_TRACING
    bool hdrOutput;                         // KSA_MTL_HDR_OUTPUT
    int log;                                // KSA_MTL_LOG
    const char* logFile;                    // KSA_MTL_LOG_FILE
    double stalls;                          // KSA_MTL_STALLS
    const char* frames;                     // KSA_MTL_FRAMES
    const char* latency;                    // KSA_MTL_LATENCY
    bool labels;                            // KSA_MTL_LABELS
    bool frameCapture;                      // MTL_CAPTURE_ENABLED
    const char* gpuProfile;                 // KSA_MTL_GPU_PROFILE
    const char* gpuProfileFrames;           // KSA_MTL_GPU_PROFILE_FRAMES
    bool gpuProfileSplit;                   // KSA_MTL_GPU_PROFILE_SPLIT
    bool gpuErrors;                         // KSA_MTL_GPU_ERRORS
    const char* watch;                      // KSA_MTL_WATCH
    double watchWhite;                      // KSA_MTL_WATCH_WHITE
    const char* dumpMsl;                    // KSA_MTL_DUMP_MSL
    const char* dumpSpv;                    // KSA_MTL_DUMP_SPV
    const char* shaderCorpus;               // KSA_MTL_SHADER_CORPUS
    const char* shaderOverrides;            // KSA_MTL_SHADER_OVERRIDES
    const char* spec;                       // KSA_MTL_SPEC
    const char* throttle;                   // KSA_MTL_THROTTLE
    bool strictPipelines;                   // KSA_MTL_STRICT_PIPELINES
    bool strictSync;                        // KSA_MTL_STRICT_SYNC
    bool earlyFragmentTests;                // KSA_MTL_EARLY_FRAGMENT_TESTS
    bool preciseMath;                       // KSA_MTL_PRECISE_MATH
    const char* shots;                      // KSA_MTL_SHOTS
    const char* shotUnit;                   // KSA_MTL_SHOT_UNIT
    const char* shotDir;                    // KSA_MTL_SHOT_DIR
    bool exitAfterShots;                    // KSA_MTL_EXIT_AFTER_SHOTS
    int exitAfter;                          // KSA_MTL_EXIT_AFTER
    double exitAfterS;                      // KSA_MTL_EXIT_AFTER_S
    bool fxDebug;                           // KSA_MTL_FX_DEBUG
    const char* fxShots;                    // KSA_MTL_FX_SHOTS
    bool frameInterpolation;                // KSA_MTL_FRAME_INTERPOLATION
    const char* cacheDir;                   // KSA_MTL_CACHE_DIR
    int textureBudgetMb;                    // KSA_MTL_TEXTURE_BUDGET_MB
} PortSettings;

static inline const char* portSettingText(const char* name) {
    const char* v = getenv(name);
    return v && *v ? v : NULL;
}
static inline bool portSettingOn(const char* name, bool def) {
    const char* v = portSettingText(name);
    return v && !strcmp(v, "0") ? false : v && !strcmp(v, "1") ? true : def;
}
// A number in [min, max] (none, such as nan, isn't); an int, digits only
static inline double portSettingNumber(const char* name, double def, double min, double max) {
    const char* v = portSettingText(name);
    char* end = NULL;
    double d = v ? strtod(v, &end) : def;
    return v && (end == v || *end || !(d >= min && d <= max)) ? def : d;
}
static inline int portSettingInt(const char* name, int def, int min, int max) {
    const char* v = portSettingText(name);
    if (!v) return def;
    for (const char* c = v; *c; c++)
        if (*c < '0' || *c > '9') return def;
    long n = strtol(v, NULL, 10);
    return n >= min && n <= max ? (int)n : def;
}

static inline PortSettings portSettingsRead(void) {
    PortSettings s;
    s.skipShadowsOnUnlitSides = portSettingOn("KSA_MTL_SKIP_SHADOWS_ON_UNLIT_SIDES", true);
    s.fusedTessellation = portSettingOn("KSA_MTL_FUSED_TESSELLATION", true);
    s.skipGpuTextureCompression = portSettingOn("KSA_MTL_SKIP_GPU_TEXTURE_COMPRESSION", true);
    s.ivaRaySearch = portSettingOn("KSA_MTL_IVA_RAY_SEARCH", true);
    s.vsyncPacing = portSettingOn("KSA_MTL_VSYNC_PACING", true);
    s.backgroundShaderCompiling = portSettingOn("KSA_MTL_BACKGROUND_SHADER_COMPILING", true);
    s.shaderCache = portSettingOn("KSA_MTL_SHADER_CACHE", true);
    s.shaderConstantMemory = portSettingOn("KSA_MTL_SHADER_CONSTANT_MEMORY", true);
    s.inPlaceShaderParameters = portSettingOn("KSA_MTL_IN_PLACE_SHADER_PARAMETERS", true);
    s.textureFileMapping = portSettingOn("KSA_MTL_TEXTURE_FILE_MAPPING", true);
    s.tessellation = portSettingOn("KSA_MTL_TESSELLATION", true);
    s.rayQueries = portSettingOn("KSA_MTL_RAY_QUERIES", true);
    s.rayTracing = portSettingOn("KSA_MTL_RAY_TRACING", true);
    s.hdrOutput = portSettingOn("KSA_MTL_HDR_OUTPUT", true);
    s.log = portSettingInt("KSA_MTL_LOG", 0, 0, 2);
    s.logFile = portSettingText("KSA_MTL_LOG_FILE");
    s.stalls = portSettingNumber("KSA_MTL_STALLS", 0, 0, 10000);
    s.frames = portSettingText("KSA_MTL_FRAMES");
    s.latency = portSettingText("KSA_MTL_LATENCY");
    s.labels = portSettingOn("KSA_MTL_LABELS", false);
    s.frameCapture = portSettingOn("MTL_CAPTURE_ENABLED", false);
    s.gpuProfile = portSettingText("KSA_MTL_GPU_PROFILE");
    s.gpuProfileFrames = portSettingText("KSA_MTL_GPU_PROFILE_FRAMES");
    s.gpuProfileSplit = portSettingOn("KSA_MTL_GPU_PROFILE_SPLIT", false);
    s.gpuErrors = portSettingOn("KSA_MTL_GPU_ERRORS", false);
    s.watch = portSettingText("KSA_MTL_WATCH");
    s.watchWhite = portSettingNumber("KSA_MTL_WATCH_WHITE", 30, 0, 100);
    s.dumpMsl = portSettingText("KSA_MTL_DUMP_MSL");
    s.dumpSpv = portSettingText("KSA_MTL_DUMP_SPV");
    s.shaderCorpus = portSettingText("KSA_MTL_SHADER_CORPUS");
    s.shaderOverrides = portSettingText("KSA_MTL_SHADER_OVERRIDES");
    s.spec = portSettingText("KSA_MTL_SPEC");
    s.throttle = portSettingText("KSA_MTL_THROTTLE");
    s.strictPipelines = portSettingOn("KSA_MTL_STRICT_PIPELINES", false);
    s.strictSync = portSettingOn("KSA_MTL_STRICT_SYNC", false);
    s.earlyFragmentTests = portSettingOn("KSA_MTL_EARLY_FRAGMENT_TESTS", true);
    s.preciseMath = portSettingOn("KSA_MTL_PRECISE_MATH", false);
    s.shots = portSettingText("KSA_MTL_SHOTS");
    s.shotUnit = portSettingText("KSA_MTL_SHOT_UNIT");
    s.shotDir = portSettingText("KSA_MTL_SHOT_DIR");
    s.exitAfterShots = portSettingOn("KSA_MTL_EXIT_AFTER_SHOTS", false);
    s.exitAfter = portSettingInt("KSA_MTL_EXIT_AFTER", 0, 0, 100000000);
    s.exitAfterS = portSettingNumber("KSA_MTL_EXIT_AFTER_S", 0, 0, 100000);
    s.fxDebug = portSettingOn("KSA_MTL_FX_DEBUG", false);
    s.fxShots = portSettingText("KSA_MTL_FX_SHOTS");
    s.frameInterpolation = portSettingOn("KSA_MTL_FRAME_INTERPOLATION", false);
    s.cacheDir = portSettingText("KSA_MTL_CACHE_DIR");
    s.textureBudgetMb = portSettingInt("KSA_MTL_TEXTURE_BUDGET_MB", 0, 0, 1048576);
    return s;
}
