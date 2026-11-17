// The core's helpers that kmtl.h declares and every layer uses.
#include "kmtl.h"
#include <sys/stat.h>

uint64_t kmtlHash(const void* data, size_t size) {
    // FNV-1a 64
    uint64_t h = 1469598103934665603ull;
    auto* p = (const uint8_t*)data;
    for (size_t i = 0; i < size; i++) { h ^= p[i]; h *= 1099511628211ull; }
    return h;
}

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
