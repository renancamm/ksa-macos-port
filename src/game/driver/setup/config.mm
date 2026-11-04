// The driver's port settings, read once from their variables (portSettingsRead, port-settings.h), and the log they set
// up (log, log_file). README.md documents them: "Configuration" has the switches, "Test" the debugging settings.
#include "kmtl.h"
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>

const PortSettings& kmtlConfig() {
    static const PortSettings config = [] {
        PortSettings c = portSettingsRead();
        // Ray tracing pipelines are ray queries' too
        c.rayTracing = c.rayTracing && c.rayQueries;
        // A pipeline that fails on a worker has a handle already: strict errors need the game's thread
        if (c.strictPipelines) c.backgroundShaderCompiling = false;
        // A frame capture names the encoders too
        c.labels = c.labels || c.frameCapture;
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

// KSA_MTL_THREADGROUP_HINT=<n> or <hash>:<n>,...: the largest threadgroup a compute pipeline is compiled for (0: none)
// (clamped to 1024, the most a threadgroup has on Apple GPUs; a value of 0 or less is none)
static uint32_t threadgroupHint(const char* value) { return (uint32_t)std::max(0, std::min(1024, atoi(value))); }

uint32_t kmtlThreadgroupHintFor(uint64_t hash) {
    const char* t = kmtlConfig().threadgroupHint;
    if (!t) return 0;
    if (!strchr(t, ':')) return threadgroupHint(t);
    char name[17];
    snprintf(name, sizeof(name), "%016llx", (unsigned long long)hash);
    std::string list = t;
    size_t pos = 0;
    while (pos < list.size()) {
        size_t end = list.find(',', pos);
        if (end == std::string::npos) end = list.size();
        std::string item = list.substr(pos, end - pos);
        size_t colon = item.find(':');
        if (colon != std::string::npos && item.compare(0, colon, name) == 0) return threadgroupHint(item.c_str() + colon + 1);
        pos = end + 1;
    }
    return 0;
}

// ---------------------------------------------------------------------------------------------------------------
// The log

int g_logLevel = 0;
static FILE* g_logFile = nullptr;

__attribute__((constructor)) static void kmtlInitLogging() {
    g_logLevel = kmtlConfig().log;
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
