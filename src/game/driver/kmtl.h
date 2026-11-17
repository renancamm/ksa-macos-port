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

// The library exports only Vulkan's functions and the driver's own (exports.txt). The driver's own are named by what
// they are:
// - vkCmd<Name>KSA(VkCommandBuffer, ...): GPU work the driver records into a command buffer, as Vulkan's vkCmd* do;
//   a struct it takes is Vk<Name>InfoKSA. Defined with VKFN, but not in the entry table (the registry doesn't know
//   them): C# imports them by name, as it does the others
// - ksaMtl<Area>[<What>]: every other call, which sets or reads the driver's state; the area is the feature or the
//   instrument it belongs to, the same for all of its calls (ksaMtlDrain, ksaMtlDrainOpen), and one file, the area's
//   own, defines all of them. A struct such a call passes is KsaMtl<Name>
// A struct either kind passes has a mirror in C# (the hooks, the benchmark), field for field.
#define KMTL_EXPORT extern "C" __attribute__((visibility("default")))
#define VKFN(ret) KMTL_EXPORT VKAPI_ATTR ret VKAPI_CALL
// All of them, with their structs and the C# that mirrors them
#include "exports.h"

// Reported as driverVersion and driverInfo
#define KMTL_VERSION_MAJOR 0
#define KMTL_VERSION_MINOR 1
#define KMTL_VERSION_PATCH 0
#define KMTL_VERSION "0.1.0"

// ---------------------------------------------------------------------------------------------------------------
// Logging. What each level of KSA_MTL_LOG adds:
// - KERR, always: an error, a call that failed or something the driver can't do as asked
// - 0, always too: what a setting or the benchmark asked the driver to report (stalls, screenshots, frame captures,
//   pass dumps), the load's milestones (the first in-game present, the pipelines' cost so far, the shader prewarm), a
//   feature's state when it is set up or changes (MetalFX's sizes, dynamic resolution's view, the GPU clock's source),
//   the first use of an unimplemented call. A line comes once per event, never once per frame or per call: what could
//   repeat goes through KLOG_LIMIT or KERR_LIMIT, or drops to 1 after its first ones (dynamic resolution's level and
//   Steady Frame Rate's rate: 20 at 0)
// - 1: the first call of each entry point, the setup (instance, device, their extensions, surfaces, swapchains),
//   warnings (something ignored or worked around), the changes of a feature's state past its first ones (every
//   level step, with its samples), the present lines (the first five, then every 500th)
// - 2: every other object made, and other detail per call
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

// Every entry point, Vulkan's and the driver's own, starts with one of these: the log of its first call at level 1,
// and an autorelease pool unless the entry point only stores what it is given and calls no Metal method that returns
// an object (the state setters between draws: binds, push constants, viewport, scissor, depth bias, blend constants,
// stencil reference). None is needed in an entry point that only calls another, which has one (vkCmdBeginRenderPass2
// calls vkCmdBeginRenderPass), or that does nothing or only says it is unimplemented (KUNIMPL)
#define KTRACE_FIRST_PLAIN() do { static std::atomic<bool> s_seen{false}; \
    if (g_logLevel >= 1 && !s_seen.exchange(true)) kmtlLog(1, "first call: %s", __func__); } while (0)
#define KTRACE_FIRST() KAutoreleasePool _kpool; KTRACE_FIRST_PLAIN()
#define KUNIMPL() do { static std::atomic<bool> s_seen{false}; \
    if (!s_seen.exchange(true)) kmtlLog(0, "UNIMPLEMENTED: %s", __func__); } while (0)

// ---------------------------------------------------------------------------------------------------------------
// Configuration: the port settings the driver does, read once from their variables (config.mm) into PortSettings, a
// field per setting named after its key (port-settings.h, made from the settings table). README.md documents them in
// two groups. Text is null when unset.
const PortSettings& kmtlConfig();

uint64_t kmtlPresentCount();  // presentation/frametime.mm: presents made
// The benchmark's drain (diagnostics/drain.mm): on once a reader opened it; a wait goes in when it reached the reader's
// threshold
extern std::atomic<bool> g_kmtlDrainOn;
inline bool kmtlDrainOn() { return g_kmtlDrainOn.load(std::memory_order_relaxed); }
void kmtlDrainWait(const char* what, double start, double ms);
// KSA_MTL_STALLS: a scope that logs when it took longer than the threshold; with the drain open it goes there too
struct KStallTimer {
    const char* what;
    double start;
    explicit KStallTimer(const char* w) : what(w), start(kmtlConfig().stalls > 0 || kmtlDrainOn() ? CACurrentMediaTime() : 0) {}
    ~KStallTimer() {
        if (start == 0) return;
        double ms = (CACurrentMediaTime() - start) * 1000.0;
        if (kmtlConfig().stalls > 0 && ms >= kmtlConfig().stalls)
            KLOG(0, "stall: %s %.1f ms at %.3f present %llu", what, ms, start, (unsigned long long)kmtlPresentCount());
        if (kmtlDrainOn()) kmtlDrainWait(what, start, ms);
    }
};

// ---------------------------------------------------------------------------------------------------------------
// A file's state. What a file keeps for the whole process (not on an object) is one struct in an anonymous namespace,
// g_<name>, made with *new and never destroyed: handlers and the driver's threads can still run while the process
// exits, after static destructors, and MetalFX aborts if a scaler is released then (framerate.mm's g_fr, upscale.mm's
// g_up). Its lock, when it has one, is m, its first field; each field says what it holds, and a field that a lock of
// its own guards, or that is read without the lock (an atomic), says so.
//
// Threads and locks. The game calls in from several threads; Metal's completion handlers, Core Animation's presented
// handlers and the driver's own threads (the pipeline workers, the GPU clock's sampler, the screenshot and pass dump
// writers) run on threads of their own. A lock is taken only while every lock held is above it here (outermost
// first); two locks on the same line are never held together:
//   1. g_fr.m (presentation/framerate.mm)
//   2. g_dyn.m (resources/dynres.mm)
//   3. KQueue::submitMutex: every commit of recorded work (a submit, a present, a level change, a new image's clear)
//   4. g_up.m (presentation/upscale.mm), g_rh.m (presentation/rayhistory.mm)
//   5. g_fi.m (presentation/interpolate.mm)
//   6. g_frameMutex (diagnostics/instruments.mm)
//   7. KPresentState::m (presentation/pacing.mm)
//   8. g_setsMutex (resources/descriptor.mm)
//   9. KDevice::residencyMutex
//  10. g_mutex (diagnostics/passdump.mm)
//  11. the rest, under which nothing but the log's is taken: g_swapchainsMutex, g_latencyMutex
//      (presentation/pacing.mm), g_hdrMutex (presentation/hdr.mm), g_fr.samplesMutex (presentation/framerate.mm),
//      g_ft.m (presentation/frametime.mm), g_drainMutex (diagnostics/drain.mm), g_ts.m (commands/timestamps.mm),
//      g_recentMutex (shaders/shadercache.mm), g_addrMutex (resources/accel.mm), KDevice::libraryMutex and its
//      entries' m, KDevice::fanMutex, KDevice::tessScratchMutex, KInternalPipelines::m, KFence::m, KQueryPool::m,
//      KPipeline::Pending::m
//  12. the log's (kmtlLog), innermost: any of them may log
// A handler takes only locks from 7 on, and holds the driver's state it uses by a shared pointer (KPresentState) or
// a copy, never an object the game may destroy before the work completes (a KSwapchain, a KCommandBuffer); a fence
// or a query pool, which Vulkan keeps until then, by pointer. Core Animation runs
// presented handlers while it holds the drawable's lock, which addPresentedHandler takes too: a presented handler is
// added with none of the locks a presented handler takes held, or the present deadlocks with it. Slow work a
// completion handler starts (a screenshot's PNG, a pass dump's files) goes to a queue of its own, as a queue's
// handlers run one at a time; with three screenshots waiting, the handler waits for its own (instruments.mm).
// No lock is held across waitUntilCompleted. A wait with a lock held is for a free drawable (nextDrawable, with
// submitMutex); a condition variable's wait (KPresentState's, a fence's, a query pool's, a compile's, a pipeline's)
// holds no lock but its own, and whatever thread signals it does so under that lock alone.

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

// kmtl.mm: data's FNV-1a hash; a file in a dump folder (KSA_MTL_DUMP_SPV, KSA_MTL_DUMP_MSL), the folder made if
// missing; with keep, a file already there stays
uint64_t kmtlHash(const void* data, size_t size);
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
