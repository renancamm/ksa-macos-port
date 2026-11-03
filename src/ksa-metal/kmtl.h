// KSA Metal: a Vulkan driver on Metal that implements the part of Vulkan Kitten Space Agency uses.
// Shared declarations. README.md ("KSA Metal") describes the design.
#pragma once

#define VK_USE_PLATFORM_METAL_EXT 1
#define VK_ENABLE_BETA_EXTENSIONS 1
#include <vulkan/vulkan.h>

#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>

#include "port-settings.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#define KMTL_EXPORT extern "C" __attribute__((visibility("default")))
#define VKFN(ret) KMTL_EXPORT VKAPI_ATTR ret VKAPI_CALL

// Reported as driverVersion and driverInfo
#define KMTL_VERSION_MAJOR 0
#define KMTL_VERSION_MINOR 1
#define KMTL_VERSION_PATCH 0
#define KMTL_VERSION "0.1.0"

// ---------------------------------------------------------------------------------------------------------------
// Logging. KSA_MTL_LOG: 0 errors only, 1 + first call of each entry point and warnings, 2 + object creation.
extern int g_logLevel;
void kmtlLog(int level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
#define KLOG(lvl, ...) do { if (g_logLevel >= (lvl)) kmtlLog(lvl, __VA_ARGS__); } while (0)
#define KERR(...) kmtlLog(-1, __VA_ARGS__)
// The first n times only, for what would repeat every frame (the count stops at n, so it never wraps around)
#define KLOG_LIMIT(lvl, n, ...) do { static std::atomic<int> s_count{0}; \
    if (g_logLevel >= (lvl) && s_count.load(std::memory_order_relaxed) < (n) && s_count++ < (n)) kmtlLog(lvl, __VA_ARGS__); } while (0)
#define KERR_LIMIT(n, ...) KLOG_LIMIT(-1, n, __VA_ARGS__)
#define KERR_ONCE(...) KERR_LIMIT(1, __VA_ARGS__)
// Autorelease pool for the duration of an entry point: the game calls in from .NET threads that have no pool, so
// autoreleased Metal objects (command buffers, encoders, descriptors) would otherwise never be released, and every
// command buffer would keep the resources it used alive forever
extern "C" void* objc_autoreleasePoolPush(void);
extern "C" void objc_autoreleasePoolPop(void*);
struct KAutoreleasePool {
    void* token = objc_autoreleasePoolPush();
    ~KAutoreleasePool() { objc_autoreleasePoolPop(token); }
};

// Every entry point starts with one of these: the log of its first call at level 1, and an autorelease pool unless
// the entry point only stores what it is given and calls no Metal method that returns an object (the state setters
// between draws: binds, push constants, viewport, scissor, depth bias, blend constants, stencil reference)
#define KTRACE_FIRST_PLAIN() do { static std::atomic<bool> s_seen{false}; \
    if (g_logLevel >= 1 && !s_seen.exchange(true)) kmtlLog(1, "first call: %s", __func__); } while (0)
#define KTRACE_FIRST() KAutoreleasePool _kpool; KTRACE_FIRST_PLAIN()
#define KUNIMPL() do { static std::atomic<bool> s_seen{false}; \
    if (!s_seen.exchange(true)) kmtlLog(0, "UNIMPLEMENTED: %s", __func__); } while (0)

// ---------------------------------------------------------------------------------------------------------------
// Configuration: the port settings the driver does, read once from their variables (config.mm) into PortSettings, a
// field per setting named after its key (src/settings/port-settings.h, made from the settings table). README.md
// documents them in two groups. Text is null when unset.
const PortSettings& kmtlConfig();
// Whether draws bind the early-test states of pipelines that have one (ksaMtlEarlyFragmentTests, pipelines/stages.mm)
bool kmtlEarlyFragmentTests();

uint64_t kmtlPresentCount();  // instruments.mm
// KSA_MTL_STALLS: a scope that logs when it took longer than the threshold
struct KStallTimer {
    const char* what;
    double start;
    explicit KStallTimer(const char* w) : what(w), start(kmtlConfig().stalls > 0 ? CACurrentMediaTime() : 0) {}
    ~KStallTimer() {
        if (start == 0) return;
        double ms = (CACurrentMediaTime() - start) * 1000.0;
        if (ms >= kmtlConfig().stalls) KLOG(0, "stall: %s %.1f ms at %.3f present %llu", what, ms, start, (unsigned long long)kmtlPresentCount());
    }
};

// ---------------------------------------------------------------------------------------------------------------
// Buffer indices per stage (vertex buffers count down from 30, so the low indices stay free for sets and push constants)
enum : uint32_t {
    kMaxSets = 10,  // set n -> buffer index n, below the push constants (maxBoundDescriptorSets)
    kPushConstantBufferIndex = 10,
    kDrawIdBufferIndex = 11,
    kBufferSizeBufferIndex = 12,
    kViewMaskBufferIndex = 13,
    // Given to SPIRV-Cross so its defaults for them (23, and 30: a vertex buffer) stay out of the way; never bound,
    // since dynamic offsets are added when a set is copied and texture swizzling is off
    kDynamicOffsetBufferIndex = 14,
    kSwizzleBufferIndex = 15,
    // Tessellation stage buffers (vertex/control kernels, evaluation function)
    kTessOutputBufferIndex = 16,
    kTessIndexBufferIndex = 17,
    kTessInputBufferIndex = 18,
    kTessPatchBufferIndex = 19,
    kTessFactorBufferIndex = 20,
    kTessParamsBufferIndex = 21,
    kRtLaunchBufferIndex = 22,  // launch size of a ray tracing pipeline run as a compute kernel
    kVertexBufferTop = 30,  // binding n -> buffer index 30 - n
    kMaxPushConstants = 4096,
};
static_assert(kMaxSets <= kPushConstantBufferIndex, "a set's buffer index would be the push constants'");
inline uint32_t vertexBufferIndex(uint32_t binding) { return kVertexBufferTop - binding; }

struct KDevice;
struct KQueue;
struct KSampler;
struct KProfState;  // GPU profiler state of a command buffer (profile.mm)

// ---------------------------------------------------------------------------------------------------------------
// Dispatchable objects. The loader-magic word is kept first so a pointer that any Khronos tool inspects looks sane.
struct KDispatchable { uintptr_t loaderMagic = 0x01CDC0DE; };

struct KInstance : KDispatchable {
    struct KPhysicalDevice* physical = nullptr;
    uint32_t apiVersion = VK_API_VERSION_1_3;
};

struct KPhysicalDevice : KDispatchable {
    id<MTLDevice> mtl = nil;
};

struct KQueue : KDispatchable {
    KDevice* device = nullptr;
    uint32_t family = 0;  // 0 graphics/compute/transfer, 1 transfer only
    id<MTLCommandQueue> mtl = nil;
    // Command buffers start after and end before this fence, so they run in submission order (commands/encoders.mm)
    id<MTLFence> chain = nil;
    std::mutex submitMutex;
};

struct KInternalPipelines;

struct KDevice : KDispatchable {
    KPhysicalDevice* physical = nullptr;
    id<MTLDevice> mtl = nil;
    KQueue* queue = nullptr;          // family 0
    KQueue* transferQueue = nullptr;  // family 1, when exposed
    KQueue* queueOf(uint32_t family) { return family == 1 && transferQueue ? transferQueue : queue; }
    id<MTLResidencySet> residency = nil;
    std::mutex residencyMutex;
    bool residencyDirty = false;
    KInternalPipelines* internal = nullptr;
    std::mutex libraryMutex;
    struct LibraryEntry {
        std::mutex m;
        std::condition_variable cv;
        bool done = false;
        id<MTLLibrary> lib = nil;
        std::string err;
    };
    std::unordered_map<uint64_t, std::shared_ptr<LibraryEntry>> libraryCache;  // MSL hash -> library
    id<MTLBuffer> fanIndices = nil;  // triangle-fan emulation: (0, i, i+1) for i = 1..n-2
    uint32_t fanVertexCapacity = 0;
    std::mutex fanMutex;
    // Tessellation stage outputs: one GPU-only buffer shared by every tessellated draw of every command buffer
    // (all encoders run in queue order through the chain fence), grown to the largest draw
    id<MTLBuffer> tessScratch = nil;
    id<MTLBuffer> timestampScratch = nil;  // target of the fill in timestamp encoders (made with the device)
    std::mutex tessScratchMutex;

    void addResident(id<MTLAllocation> a);
    void removeResident(id<MTLAllocation> a);
    void commitResidency();
};

// ---------------------------------------------------------------------------------------------------------------
// Helpers shared between files
// n rounded up to a multiple of a, a power of two
template <typename T> constexpr T kmtlAlignUp(T n, T a) { return (n + a - 1) & ~(a - 1); }

// Vulkan's two-call enumerations of n items: with out null, *count = n; else up to *count items through
// write(i, out[i]), VK_INCOMPLETE when they didn't all fit
template <typename T, typename F> VkResult kmtlEnumerate(uint32_t n, uint32_t* count, T* out, F write) {
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    for (uint32_t i = 0; i < c; i++) write(i, out[i]);
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
}

inline const void* findNext(const void* chain, VkStructureType type) {
    for (auto* p = (const VkBaseInStructure*)chain; p; p = p->pNext)
        if (p->sType == type) return p;
    return nullptr;
}

uint64_t kmtlHash(const void* data, size_t size);
// pipelines/stages.mm: a file in a dump folder (KSA_MTL_DUMP_SPV, KSA_MTL_DUMP_MSL), the folder made if missing; with keep, a
// file already there stays
void kmtlDumpFile(const char* dir, const std::string& name, const void* data, size_t size, bool keep = false);

// Memory accounting (frame log and budget)
extern std::atomic<int64_t> g_allocatedBytes;
extern std::atomic<int64_t> g_allocatedByType[3];
extern std::atomic<int64_t> g_internalBytes;  // descriptor pool chunks and upload rings

// The layers, each in its folder with its header, in the order their types need each other
#include "resources/resources.h"
#include "shaders/shaders.h"
#include "pipelines/pipelines.h"
#include "commands/commands.h"
#include "presentation/presentation.h"
#include "diagnostics/diagnostics.h"
