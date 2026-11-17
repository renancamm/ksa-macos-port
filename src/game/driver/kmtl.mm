// The core's helpers that kmtl.h declares and every layer uses.
#include "kmtl.h"
#include <sys/stat.h>
#include <pthread.h>
#include <unistd.h>

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

double kmtlMedian(const double* v, uint32_t n) {
    double s[64];
    n = std::min(n, 64u);
    std::copy(v, v + n, s);
    std::nth_element(s, s + n / 2, s + n);
    return n ? s[n / 2] : 0;
}

bool kmtlReadFile(const std::string& path, std::string* out) {
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return false;
    out->clear();
    char buf[65536];
    for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) out->append(buf, n);
    bool ok = !ferror(f);
    fclose(f);
    return ok;
}

bool kmtlWriteFileAtomically(const std::string& path, const std::string& data) {
    std::string tmp = path + ".tmp" + std::to_string((unsigned long)pthread_mach_thread_np(pthread_self()));
    FILE* f = fopen(tmp.c_str(), "wb");
    if (!f) return false;
    fwrite(data.data(), 1, data.size(), f);
    if (fclose(f) == 0 && rename(tmp.c_str(), path.c_str()) == 0) return true;
    unlink(tmp.c_str());
    return false;
}

id<MTLLibrary> kmtlLibrary(id<MTLDevice> d, const char* source, MTLCompileOptions* options, NSError** error) {
    return [d newLibraryWithSource:[NSString stringWithUTF8String:source] options:options error:error];
}

std::vector<id<MTLComputePipelineState>> kmtlComputePipelines(id<MTLDevice> d, const char* source,
                                                              std::initializer_list<const char*> names, std::string* error) {
    std::vector<id<MTLComputePipelineState>> pipelines;
    NSError* err = nil;
    id<MTLLibrary> lib = kmtlLibrary(d, source, nil, &err);
    if (!lib && error) *error = err.localizedDescription.UTF8String ?: "no library";
    for (const char* name : names) {
        id<MTLFunction> f = lib ? [lib newFunctionWithName:[NSString stringWithUTF8String:name]] : nil;
        NSError* perr = nil;
        id<MTLComputePipelineState> ps = f ? [d newComputePipelineStateWithFunction:f error:&perr] : nil;
        if (!ps && lib && error && error->empty())
            *error = perr && perr.localizedDescription.UTF8String ? perr.localizedDescription.UTF8String
                                                                  : std::string("no function ") + name;
        pipelines.push_back(ps);
    }
    return pipelines;
}

id<MTLTexture> kmtlPrivateTexture(id<MTLDevice> d, MTLPixelFormat format, NSUInteger width, NSUInteger height,
                                  MTLTextureUsage usage) {
    MTLTextureDescriptor* t = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:format width:width height:height
                                                                             mipmapped:NO];
    t.usage = usage;
    t.storageMode = MTLStorageModePrivate;
    return [d newTextureWithDescriptor:t];
}
