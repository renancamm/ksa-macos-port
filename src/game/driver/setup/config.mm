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
