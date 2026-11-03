// KSA Metal: a Vulkan driver on Metal that implements the part of Vulkan Kitten Space Agency uses.
// Shared declarations. README.md ("KSA Metal") describes the design.
#pragma once

#define VK_USE_PLATFORM_METAL_EXT 1
#define VK_ENABLE_BETA_EXTENSIONS 1
#include <vulkan/vulkan.h>

#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>

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
// Configuration: the KSA_MTL_* environment variables, read once (config.mm). README.md documents them in the same two
// groups. Strings are null when unset.
struct KConfig {
    // Switches: turn a feature off or change a default
    bool tessellation = true;             // KSA_MTL_TESSELLATION=0 hides tessellation from the game
    bool tessFuse = true;                 // KSA_MTL_TESS_FUSE=0 tessellates without the fused path
    bool rayQuery = true;                 // KSA_MTL_RAYQUERY=0 hides ray queries and ray tracing
    bool rtPipeline = true;               // KSA_MTL_RTPIPELINE=0 hides ray tracing pipelines (off with rayQuery)
    bool asyncPipelines = true;           // KSA_MTL_ASYNC_PIPELINES=0 compiles pipelines on the game's thread
    bool cache = true;                    // KSA_MTL_CACHE=0: no shader cache on disk, no prewarm
    bool strictSync = false;              // KSA_MTL_SYNC=strict: all GPU work in submission order
    bool safeMath = false;                // KSA_MTL_SAFE_MATH=1: shaders without fast math
    bool constantArgumentBuffers = true;  // KSA_MTL_AB_CONSTANT=0: argument buffers in device memory
    bool uncompressedTextures = true;     // KSA_MTL_UNCOMPRESSED_TEXTURES=0: lossless compression wherever Metal uses it
    int budgetMb = 0;                     // KSA_MTL_BUDGET_MB: GPU memory budget reported (0: Metal's recommendation)
    bool presentPacing = true;            // KSA_MTL_PRESENT_PACING=0: a present counts only once the display shows it
    bool fxInterpolate = false;           // KSA_MTL_FX_INTERPOLATE=1: a MetalFX frame between every two the game presents
    bool hdr = true;                      // KSA_MTL_HDR=0: no HDR output offered (the game's HDR rows stay hidden)
    // Debugging
    int logLevel = 0;                     // KSA_MTL_LOG (see Logging above)
    const char* logFile = nullptr;        // KSA_MTL_LOG_FILE
    bool strictPipelines = false;         // KSA_MTL_STRICT=1: pipeline errors go back to the game
    const char* dumpMsl = nullptr;        // KSA_MTL_DUMP_MSL=<dir>
    const char* dumpSpv = nullptr;        // KSA_MTL_DUMP_SPV=<dir>
    const char* corpus = nullptr;         // KSA_MTL_CORPUS=<dir>
    const char* overrides = nullptr;      // KSA_MTL_OVERRIDES=<dir>
    const char* spec = nullptr;           // KSA_MTL_SPEC=<hash>:<id>=<value>,...
    const char* cacheDir = nullptr;       // KSA_MTL_CACHE_DIR=<dir>
    const char* shots = nullptr;          // KSA_MTL_SHOTS=<n,...>
    const char* shotUnit = nullptr;       // KSA_MTL_SHOT_UNIT=presents|frames|seconds
    const char* shotDir = nullptr;        // KSA_MTL_SHOT_DIR=<dir>
    int exitAfter = 0;                    // KSA_MTL_EXIT_AFTER=<presents>
    double exitAfterSeconds = 0;          // KSA_MTL_EXIT_AFTER_S=<seconds>
    bool exitAfterShots = false;          // KSA_MTL_EXIT_AFTER_SHOTS=1
    const char* frames = nullptr;         // KSA_MTL_FRAMES=<csv>
    const char* latency = nullptr;        // KSA_MTL_LATENCY=<csv>
    double stallMs = 0;                   // KSA_MTL_STALLS=<ms>: log what blocks the game longer than that
    const char* gpuprof = nullptr;        // KSA_MTL_GPUPROF=<csv>
    const char* gpuprofFrames = nullptr;  // KSA_MTL_GPUPROF_FRAMES=<first>,<count>
    bool gpuprofSplit = false;            // KSA_MTL_GPUPROF_SPLIT=1: every draw and dispatch in its own encoder
    bool labels = false;                  // KSA_MTL_LABELS=1: encoders named after their pipelines
    bool gpuErrors = false;               // KSA_MTL_GPU_ERRORS=1: a failed command buffer logs each encoder's state
    const char* watch = nullptr;          // KSA_MTL_WATCH=<dir>: a look at the image a few times a second, a pass dump when it looks broken
    double watchWhite = 30;               // KSA_MTL_WATCH_WHITE: the share of pure white that counts as broken, in percent
    bool fxDebug = false;                 // KSA_MTL_FX_DEBUG=1: the upscale shows its motion vectors
    const char* fxShots = nullptr;        // KSA_MTL_FX_SHOTS=<dir>: PNGs of what MetalFX made now and then
    const char* throttle = nullptr;       // KSA_MTL_THROTTLE=<hash>:<N>,...: experiment, run a shader every N-th present (0 never)
    bool uniformRefs = true;              // KSA_MTL_UNIFORM_REFS=0: helpers get copies of uniform structs, as translated
    bool rtIntersector = true;            // KSA_MTL_RT_INTERSECTOR=0: ray tracing kernels trace with ray queries, as translated
    bool unlitShadowSkip = true;          // KSA_MTL_UNLIT_SHADOW_SKIP=0: the port's shader edits look up shadows also where unlit
    bool earlyFragmentTests = true;       // KSA_MTL_EARLY_FRAGMENT_TESTS=0: no early-test states for fragment shaders that store
};
const KConfig& kmtlConfig();
// Whether draws bind the early-test states of pipelines that have one (ksaMtlEarlyFragmentTests, pipelines/stages.mm)
bool kmtlEarlyFragmentTests();

uint64_t kmtlPresentCount();  // instruments.mm
// KSA_MTL_STALLS: a scope that logs when it took longer than the threshold
struct KStallTimer {
    const char* what;
    double start;
    explicit KStallTimer(const char* w) : what(w), start(kmtlConfig().stallMs > 0 ? CACurrentMediaTime() : 0) {}
    ~KStallTimer() {
        if (start == 0) return;
        double ms = (CACurrentMediaTime() - start) * 1000.0;
        if (ms >= kmtlConfig().stallMs) KLOG(0, "stall: %s %.1f ms at %.3f present %llu", what, ms, start, (unsigned long long)kmtlPresentCount());
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
