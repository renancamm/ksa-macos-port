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
    int budgetMb = 0;                     // KSA_MTL_BUDGET_MB: GPU memory budget reported (0: Metal's recommendation)
    bool presentPacing = true;            // KSA_MTL_PRESENT_PACING=0: a present counts only once the display shows it
    bool fxInterpolate = false;           // KSA_MTL_FX_INTERPOLATE=1: a MetalFX frame between every two the game presents
    // Debugging
    int logLevel = 0;                     // KSA_MTL_LOG (see Logging above)
    const char* logFile = nullptr;        // KSA_MTL_LOG_FILE
    bool strictPipelines = false;         // KSA_MTL_STRICT=1: pipeline errors go back to the game
    const char* dumpMsl = nullptr;        // KSA_MTL_DUMP_MSL=<dir>
    const char* dumpSpv = nullptr;        // KSA_MTL_DUMP_SPV=<dir>
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
    bool fxDebug = false;                 // KSA_MTL_FX_DEBUG=1: the upscale shows its motion vectors
    const char* fxShots = nullptr;        // KSA_MTL_FX_SHOTS=<dir>: PNGs of what MetalFX made now and then
    const char* throttle = nullptr;       // KSA_MTL_THROTTLE=<hash>:<N>,...: experiment, run a shader every N-th present (0 never)
    bool uniformRefs = true;              // KSA_MTL_UNIFORM_REFS=0: helpers get copies of uniform structs, as translated
};
const KConfig& kmtlConfig();

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
    kMaxSets = 16,
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
    // Command buffers start after and end before this fence, so they run in submission order (command.mm)
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
// Non-dispatchable objects (handles are pointers to these)
struct KDeviceMemory {
    KDevice* device = nullptr;
    VkDeviceSize size = 0;
    uint32_t typeIndex = 0;
    id<MTLHeap> heap = nil;
    id<MTLBuffer> whole = nil;  // whole-heap buffer at offset 0: buffers bind to it, the host maps it
    void* mapped = nullptr;
};

struct KBuffer {
    VkDeviceSize size = 0;
    VkDeviceSize offset = 0;
    id<MTLBuffer> mtl = nil;  // = memory->whole
    uint64_t gpuAddress() const { return mtl ? mtl.gpuAddress + offset : 0; }
};

struct KFormatInfo;

struct KImage {
    KDevice* device = nullptr;
    VkImageCreateInfo info{};
    MTLTextureDescriptor* desc = nil;
    MTLSizeAndAlign sizeAlign{};
    id<MTLTexture> mtl = nil;
    bool ownsTexture = false;  // swapchain images
    VkFormat format = VK_FORMAT_UNDEFINED;
    MTLPixelFormat pixelFormat = MTLPixelFormatInvalid;
};

struct KImageView {
    KImage* image = nullptr;
    id<MTLTexture> mtl = nil;
    // For depth/stencil aspect-only views of combined formats the render pass still uses the image
    VkFormat format = VK_FORMAT_UNDEFINED;
    uint32_t baseLevel = 0, levelCount = 1, baseLayer = 0, layerCount = 1;
};

struct KSampler {
    id<MTLSamplerState> mtl = nil;
};

struct KFence {
    std::mutex m;
    std::condition_variable cv;
    bool signaled = false;
};

// Binary semaphore: within one queue submission order already holds (no-op); across queues a shared event that the
// signaling submission raises and the waiting one waits for
struct KSemaphore {
    id<MTLSharedEvent> event = nil;
    uint64_t value = 0;
    KQueue* signaledBy = nullptr;
};

// Timestamp query pool: values resolve from counter samples when the command buffer completes
struct KQueryPool {
    uint32_t count;
    std::vector<uint64_t> values;
    std::vector<uint8_t> available;
    std::mutex m;
    std::condition_variable cv;
    void reset(uint32_t first, uint32_t n) {
        std::lock_guard<std::mutex> lock(m);
        for (uint32_t i = first; i < first + n && i < count; i++) available[i] = 0;
    }
};

struct KPipelineCache { int unused = 0; };

// Descriptor set layout: every binding gets consecutive 8-byte slots ("ids") in the set's argument buffer
struct KBinding {
    uint32_t binding = 0;
    VkDescriptorType type = VK_DESCRIPTOR_TYPE_MAX_ENUM;
    uint32_t count = 0;
    uint32_t id = 0;         // first slot
    uint32_t samplerId = 0;  // combined image sampler: first sampler slot (id + count)
    VkShaderStageFlags stages = 0;
    std::vector<KSampler*> immutableSamplers;
};

struct KSetLayout {
    std::vector<KBinding> bindings;  // sorted by binding number
    VkShaderStageFlags stages = 0;   // of all the bindings: the stages that read the set
    uint32_t slotCount = 0;          // including the trailing buffer-size slot
    uint32_t sizeSlot = 0;           // slot holding the address of the buffer-size table
    uint32_t dynamicCount = 0;
    std::vector<uint32_t> dynamicSlots;  // slot of each dynamic descriptor (binding order, array elements)
    uint32_t bytes() const { return slotCount * 12 + 16; }  // slots + uint sizes table, padded
    const KBinding* find(uint32_t b) const {
        for (auto& x : bindings) if (x.binding == b) return &x;
        return nullptr;
    }
    // The app may destroy a set layout while sets and pipeline layouts made from it still use it, so they hold a reference
    std::atomic<uint32_t> refs{1};
    void retain() { refs++; }
    void release() { if (--refs == 0) delete this; }
};

struct KDescriptorPool {
    KDevice* device = nullptr;
    // Chunks grow on demand, so the app's pool sizes never have to match our per-set layout overhead
    struct Chunk {
        id<MTLBuffer> buffer = nil;
        uint8_t* base = nullptr;
        VkDeviceSize capacity = 0, used = 0;
    };
    std::vector<Chunk> chunks;
    VkDeviceSize chunkSize = 0;
    struct Free { uint32_t chunk; VkDeviceSize offset; };
    std::unordered_map<VkDeviceSize, std::vector<Free>> freeBySize;  // places of freed sets, by their size
    std::vector<struct KDescriptorSet*> sets;  // the live sets; each knows its index, so a free swaps with the last
};

struct KDescriptorSet {
    KSetLayout* layout = nullptr;
    id<MTLBuffer> buffer = nil;  // the pool chunk holding this set
    uint8_t* base = nullptr;     // CPU address of the set
    uint32_t chunk = 0;
    uint32_t index = 0;          // in pool->sets
    VkDeviceSize offset = 0;     // within the chunk
    VkDeviceSize size = 0;
    uint64_t* slots() const { return (uint64_t*)base; }
    uint32_t* sizes() const { return (uint32_t*)(base + layout->slotCount * 8); }
    ~KDescriptorSet() { if (layout) layout->release(); }
};

struct KPipelineLayout {
    std::vector<KSetLayout*> sets;
    uint32_t pushConstantSize = 0;
    ~KPipelineLayout() { for (auto* l : sets) if (l) l->release(); }
};

struct KShaderModule {
    std::vector<uint32_t> spirv;
    uint64_t hash = 0;
};

struct KRenderPass {
    std::vector<VkAttachmentDescription2> attachments;
    struct Subpass {
        std::vector<int> colors;    // attachment index or -1
        std::vector<int> resolves;  // attachment index or -1
        int depth = -1;
        int depthResolve = -1;
        uint32_t viewMask = 0;
    };
    std::vector<Subpass> subpasses;
};

struct KFramebuffer {
    std::vector<KImageView*> attachments;
    uint32_t width = 0, height = 0, layers = 1;
};

// The compute stages of a tessellation pipeline: the vertex shader as a kernel per index type (none, 16-bit, 32-bit)
// and the control shader as a multi-patch kernel, with the byte strides of the records they write (per vertex, per
// control point, per patch)
struct KTessStages {
    id<MTLComputePipelineState> vertex[3] = {nil, nil, nil};
    id<MTLComputePipelineState> control = nil;
    NSUInteger vertexStride = 0, controlStride = 0, patchStride = 0;
};

struct KPipeline {
    // Graphics
    id<MTLRenderPipelineState> render = nil;
    id<MTLDepthStencilState> depthStencil = nil;
    MTLPrimitiveType primitive = MTLPrimitiveTypeTriangle;
    bool triangleFan = false;  // emulated with a generated index buffer
    // Tessellation: the plain stages, and, when the control stage is a passthrough, the fused ones (32-bit indexed
    // draws; KSA_MTL_TESS_FUSE=0 turns them off): their evaluation stage runs the vertex shader on attributes Metal
    // fetches per control point, so their kernels only write what the tessellation factors need
    bool tess = false;
    KTessStages tessPlain, tessFused;
    id<MTLRenderPipelineState> renderFused = nil;
    uint32_t tessInCP = 3, tessOutCP = 3;
    bool tessQuads = false;
    bool tessPassthrough = false;  // control stage writes only tessellation factors; evaluation reads the vertex outputs
    MTLCullMode cull = MTLCullModeNone;
    MTLWinding winding = MTLWindingCounterClockwise;
    MTLTriangleFillMode fill = MTLTriangleFillModeFill;
    MTLDepthClipMode depthClip = MTLDepthClipModeClip;
    bool depthBiasEnable = false;
    float depthBias[3] = {0, 0, 0};  // constant, slope, clamp
    bool dynamicViewport = false, dynamicScissor = false, dynamicDepthBias = false, dynamicStencilRef = false,
         dynamicBlendConstants = false;
    std::vector<VkViewport> viewports;
    std::vector<VkRect2D> scissors;
    uint32_t stencilRef[2] = {0, 0};
    float blendConstants[4] = {0, 0, 0, 0};
    bool vertexUsesDrawId = false;
    bool fragmentUsesDrawId = false;
    uint32_t viewMask = 0;
    // Compute (ray tracing pipelines run as compute kernels, rtpipeline.mm)
    id<MTLComputePipelineState> compute = nil;
    MTLSize workgroup = MTLSizeMake(1, 1, 1);
    bool computeUsesDrawId = false;
    // Asynchronous creation: render / compute are set by a worker; binding the pipeline waits for it, and once it is
    // done every later bind sees that without the lock
    struct Pending {
        std::mutex m;
        std::condition_variable cv;
        std::atomic<bool> done{false};  // stored with release after the state, under the lock
    };
    std::shared_ptr<Pending> pending;
    void wait() {
        if (!pending || pending->done.load(std::memory_order_acquire)) return;
        std::unique_lock<std::mutex> lock(pending->m);
        pending->cv.wait(lock, [&] { return pending->done.load(std::memory_order_acquire); });
    }
    // GPU profiler name: shader module hashes
    uint64_t profId = 0;
    std::string profName;
    int throttle = -1;  // KSA_MTL_THROTTLE: run every N-th present (0 never, -1 always)
};
// instruments.mm: a texture (8-bit RGBA or BGRA) saved as a PNG once the command buffer completes, then done (if any);
// the copy waits for the fence after (if any)
void kmtlSavePNG(id<MTLCommandBuffer> cb, id<MTLTexture> tex, id<MTLFence> after, std::string path, void (^done)(void) = nil);
// upscale.mm: the present's MetalFX passes
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color);
int kmtlThrottleFor(const std::string& profName);
inline bool kmtlThrottled(const KPipeline* p) {
    if (!p || p->throttle < 0) return false;
    return p->throttle == 0 || kmtlPresentCount() % (uint64_t)p->throttle != 0;
}

// ---------------------------------------------------------------------------------------------------------------
// Command buffers
struct KCommandPool {
    KDevice* device = nullptr;
    KQueue* queue = nullptr;  // of the pool's queue family
    std::vector<struct KCommandBuffer*> buffers;
};

struct KBoundSet {
    id<MTLBuffer> buffer = nil;
    NSUInteger offset = 0;
    VkShaderStageFlags stages = 0;  // the layout's: a graphics set is bound to the functions that read it
};

struct KUploadRing {
    // A chunk's properties are read once when it is made: placing data in it then sends the buffer no message (the
    // game's threads have no autorelease pool, and a message that autoreleases the buffer would leak it)
    struct Chunk { id<MTLBuffer> buffer = nil; uint8_t* contents = nullptr; uint64_t gpuAddress = 0; size_t length = 0; };
    std::vector<Chunk> chunks;
    size_t chunk = 0;
    size_t offset = 0;
    static constexpr size_t kChunkSize = 1 << 20;
    struct Place { id<MTLBuffer> buffer; uint64_t gpuAddress; NSUInteger offset; void* ptr; };
    Place alloc(KDevice* dev, size_t size);  // `size` bytes aligned to 256: their buffer, its GPU address, offset, CPU pointer
    void reset() { chunk = 0; offset = 0; }
};

struct KCommandBuffer : KDispatchable {
    KDevice* device = nullptr;
    KQueue* queue = nullptr;  // the queue of its pool's family: encoders order against that queue's chain fence
    id<MTLCommandBuffer> mtl = nil;
    id<MTLRenderCommandEncoder> render = nil;
    id<MTLComputeCommandEncoder> compute = nil;
    id<MTLBlitCommandEncoder> blit = nil;
    KUploadRing ring;
    bool submittedOnce = false;

    // Deferred store actions: with tessellation on, render passes start with MTLStoreActionUnknown so a pass can be
    // suspended for the compute stages (store everything) and resumed (load everything); the real store actions are
    // applied when the pass really ends
    MTLRenderPassDescriptor* passDesc = nil;
    MTLStoreAction colorStores[8] = {}, depthStore = MTLStoreActionStore, stencilStore = MTLStoreActionStore;
    bool deferredStores = false, suspending = false;

    // Encoder ordering (command.mm): every encoder has stage classes (roles); each class keeps the fence of its last
    // encoder, and a Vulkan barrier makes later encoders of its destination classes wait on the last fences of its
    // source classes. Command buffers start after and end before the queue's chain fence.
    enum : uint32_t { kClassCompute = 1, kClassTransfer = 2, kClassAccel = 4, kClassVertex = 8, kClassFragment = 16, kClassAll = 31 };
    id<MTLFence> lastFence[5] = {};
    uint32_t needMask[5] = {};
    uint32_t encRoles = 0;
    std::vector<id<MTLFence>> encWaited;  // fences the open encoder waited on
    std::vector<id<MTLFence>> fencePool;
    size_t fenceNext = 0;
    // MSAA resolves: a render pass's fences can signal before its resolve store actions have written the resolve
    // textures, so a pass that resolves also signals this event, and the next compute or blit encoder that depends on
    // fragment work (and the end of the command buffer) waits for it
    id<MTLEvent> resolveEvent = nil;
    uint64_t resolveSignaled = 0, resolveWaited = 0;
    bool passResolves = false;
    // vkCmdUpscaleKSA (upscale.mm): full barriers around MetalFX
    id<MTLEvent> fxEvent = nil;
    uint64_t fxValue = 0;
    bool fxSnapshot = false;  // vkCmdUpscaleSnapshotKSA ran in this recording
    void waitForResolves(uint32_t roles);  // an encoder of these roles that depends on fragment work
    void waitForAllResolves();             // whatever comes next (the end of recording, MetalFX)
    void beginCommands();
    void joinCommands();
    id<MTLFence> newFence();
    template <typename E> void waitForRoles(E enc, uint32_t roles);
    id<MTLFence> finishFence(uint32_t roles);  // a new fence that becomes the last fence of these classes
    void barrierClasses(uint32_t src, uint32_t dst);

    // Render pass state
    uint32_t rtWidth = 0, rtHeight = 0;
    bool inRenderPass = false;
    KRenderPass* renderPass = nullptr;
    KFramebuffer* framebuffer = nullptr;
    uint32_t subpass = 0;

    // Bound state
    KPipeline* graphicsPipeline = nullptr;
    KPipeline* computePipeline = nullptr;
    KBoundSet graphicsSets[kMaxSets], computeSets[kMaxSets];
    uint32_t graphicsSetsDirty = 0, computeSetsDirty = 0;
    uint8_t push[kMaxPushConstants];
    uint32_t pushSize = 0;
    bool graphicsPushDirty = true, computePushDirty = true;
    struct VB { id<MTLBuffer> buf = nil; NSUInteger off = 0; VkDeviceSize size = 0; };
    VB vertexBuffers[16];
    uint32_t vertexBuffersDirty = 0;
    id<MTLBuffer> indexBuffer = nil;
    NSUInteger indexOffset = 0;
    VkDeviceSize indexBytes = 0;  // bytes of the bound VkBuffer from the binding offset
    MTLIndexType indexType = MTLIndexTypeUInt16;
    std::vector<VkViewport> viewports;
    std::vector<VkRect2D> scissors;
    bool viewportDirty = false, scissorDirty = false;
    float depthBias[3] = {0, 0, 0};
    bool depthBiasDirty = false;
    float blendConstants[4] = {0, 0, 0, 0};  // dynamic state, applied when the pipeline declares it dynamic
    uint32_t stencilRef[2] = {0, 0};         // front, back
    bool blendConstantsDirty = false, stencilRefDirty = false;
    bool pipelineDirty = false;
    KPipeline* encodedPipeline = nullptr;

    // Timestamp queries: one counter sample per write (end of a tiny ordered encoder); writes inside a render pass
    // are taken when the pass ends
    struct TimestampWrite { KQueryPool* pool; uint32_t query; uint32_t sample; };
    id<MTLCounterSampleBuffer> tsSamples = nil;
    uint32_t tsUsed = 0;
    std::vector<TimestampWrite> tsWrites;
    std::vector<std::pair<KQueryPool*, uint32_t>> tsDeferred;
    void writeTimestamp(KQueryPool* pool, uint32_t query);
    void submitTimestamps(id<MTLCommandBuffer> c);
    void releaseTimestamps();

    // Counters for the frame log
    uint32_t draws = 0, dispatches = 0, passes = 0;

    // GPU profiler (profile.mm): set while this recording is profiled, null otherwise, so the hooks cost one test
    KProfState* prof = nullptr;

    // KSA_MTL_LABELS=1: the open encoder's name, from the pipelines it ran (the game sets no debug labels in its
    // release builds); Instruments, the Metal HUD and Xcode's frame capture show it
    std::string encLabel;
    uint32_t encPipelines = 0;
    uint64_t encLastPipeline = 0;
    void notePipelineLabel(KPipeline* p);
    template <typename E> void nameEncoder(E enc);

    void endEncoder();
    id<MTLComputeCommandEncoder> computeEncoder(uint32_t roles = kClassCompute);
    id<MTLBlitCommandEncoder> blitEncoder();
    void beginRender(MTLRenderPassDescriptor* rp, uint32_t w, uint32_t h, bool resuming = false,
                     uint32_t roles = kClassVertex | kClassFragment);
    void suspendRender();
    void resumeRender();
    void endRender();
    void resetState();
};

// ---------------------------------------------------------------------------------------------------------------
// Surface and swapchain
struct KSurface {
    CAMetalLayer* layer = nil;
};

// Present ids of a swapchain, waited on by vkWaitForPresentKHR, and the recent presents its pacing looks at
// (swapchain.mm). Shared with the presents' handlers, which may run after the swapchain is destroyed
struct KPresentState {
    struct Frame { uint64_t present, id; double made, gpuEnd; bool gpuDone, shown; };
    std::mutex m;
    std::condition_variable cv;
    uint64_t presented = 0;     // the latest id the display showed
    uint64_t presents = 0;      // presents made
    std::vector<Frame> frames;  // the last 16, oldest first
    double began = 0;           // when the game's last wait for a present ended
    double cpuMs[8] = {}, gpuMs[8] = {};  // recent frames: from that wait to the present; GPU time
    uint32_t cpuSamples = 0, gpuSamples = 0;
};

// Pixel size of a surface's layer (bounds x contentsScale): the surface's current extent
VkExtent2D kmtlLayerExtent(CAMetalLayer* layer);

struct KSwapchain {
    std::shared_ptr<KPresentState> presentState = std::make_shared<KPresentState>();
    KSurface* surface = nullptr;
    std::vector<KImage*> images;
    uint32_t next = 0;
    VkExtent2D extent{};
};

// ---------------------------------------------------------------------------------------------------------------
// Formats
struct KFormatInfo {
    VkFormat vk;
    MTLPixelFormat mtl;
    uint32_t blockBytes;     // bytes per texel, or per block for compressed formats
    uint32_t blockW, blockH; // 1x1 or 4x4
    bool depth, stencil;
    bool renderable;
    bool isInt, isUint;      // integer formats (clears, blits)
};
const KFormatInfo* kmtlFormat(VkFormat f);
MTLPixelFormat kmtlPixelFormat(VkFormat f);
MTLVertexFormat kmtlVertexFormat(VkFormat f);

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

KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h);
void kmtlDestroyImage(KImage* img);
void kmtlZeroImage(KImage* img);  // command.mm: a new color image cleared to zero at bind time
uint64_t kmtlBufferViewId(VkBufferView view);
MTLCompareFunction kmtlCompare(VkCompareOp op);
uint64_t kmtlHash(const void* data, size_t size);
// pipeline.mm: a file in a dump folder (KSA_MTL_DUMP_SPV, KSA_MTL_DUMP_MSL), the folder made if missing; with keep, a
// file already there stays
void kmtlDumpFile(const char* dir, const std::string& name, const void* data, size_t size, bool keep = false);
id<MTLLibrary> kmtlCompileStageMSL(KDevice* dev, const std::string& msl, std::string* err);

// Result of translating one pipeline stage (SPIR-V -> MSL plus reflection); cached on disk by shadercache.mm
struct KTranslated {
    std::string msl;
    std::string entry;
    bool usesDrawId = false;
    MTLSize workgroup = MTLSizeMake(1, 1, 1);
    // Tessellation execution modes (from whichever of the two stages declares them)
    uint32_t outputVertices = 0;
    bool quads = false, cw = false;
    MTLTessellationPartitionMode partition = MTLTessellationPartitionModeInteger;
    bool hasPartition = false, hasWinding = false, hasDomain = false;
};

// Shader cache (shadercache.mm): translations on disk keyed by everything that shapes them, and a background
// compile of the previous run's shaders at device creation
bool kmtlCacheLoad(uint64_t key, KTranslated* out);
void kmtlCacheStore(uint64_t key, const KTranslated& tr);
void kmtlCacheNoteUsed(uint64_t key);
void kmtlCacheFlushRecent();
void kmtlShaderPrewarm(KDevice* dev);
// pipeline.mm: threads per shader-compile pool (the pipeline workers, and the prewarm's own): all cores but two; the
// two pools can overlap while the game loads
unsigned kmtlWorkerCount();

// Internal shaders (fills, image blits, the prep kernel of indirect tessellated draws)
void kmtlInitInternal(KDevice* dev);
void kmtlInternalFillBuffer(KCommandBuffer* cb, id<MTLBuffer> buf, NSUInteger offset, NSUInteger size, uint32_t data);
void kmtlInternalBlit(KCommandBuffer* cb, KImage* src, KImage* dst, const VkImageBlit2& region, VkFilter filter);
id<MTLComputePipelineState> kmtlTessPrepPipeline(KDevice* dev);
id<MTLBuffer> kmtlTessStats(KDevice* dev);
uint32_t kmtlTessCap(KDevice* dev);

// Frame instruments (instruments.mm)
void kmtlOnPresent(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb);
void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes);
void kmtlNoteGpuTime(id<MTLCommandBuffer> cb);  // from completion handlers: GPU busy time for the frame log
double kmtlTakeFrameGpuTime();  // one frame's GPU busy time in seconds (since the last present completed), reset
// From swapchain.mm's presented handlers: the display showed a drawable at t (presentedTime); timed: a game frame,
// whose interval since the last one is the frame log's display_ms (interpolated frames only count)
void kmtlNoteShown(double t, bool timed);
void kmtlPipelineCounts(uint32_t* shaders, uint32_t* pipelines);  // pipeline.mm: MSL compiled (not from the cache), states made
void kmtlReportPipelineTimes();  // pipeline.mm: the pipelines' creation cost, with the first in-game frame

// GPU profiler (profile.mm, KSA_MTL_GPUPROF), and the timestamp sample buffers it shares with the game's timestamp
// queries: recycled, since Metal caps how many are alive; null (logged a few times) when one can't be made
constexpr uint32_t kTimestampSamples = 4096;
id<MTLCounterSampleBuffer> kmtlAcquireTimestamps(KDevice* dev, const char* what);
void kmtlRecycleTimestamps(id<MTLCounterSampleBuffer> b);
void kmtlProfBegin(KCommandBuffer* cb);
void kmtlProfDrop(KCommandBuffer* cb);  // a recording dropped without a submit: frees its state
void kmtlProfRenderPass(KCommandBuffer* cb, MTLRenderPassDescriptor* rp);
id<MTLComputeCommandEncoder> kmtlProfComputeEncoder(KCommandBuffer* cb);
id<MTLBlitCommandEncoder> kmtlProfBlitEncoder(KCommandBuffer* cb);
void kmtlProfNote(KCommandBuffer* cb, KPipeline* p, bool dispatch, uint32_t n);
void kmtlProfEncoderEnded(KCommandBuffer* cb);
void kmtlProfSubmit(KCommandBuffer* cb, id<MTLCommandBuffer> mtl);

// Acceleration structures (accel.mm)
void kmtlRegisterMemory(KDeviceMemory* m);
void kmtlUnregisterMemory(KDeviceMemory* m);
uint64_t kmtlAccelId(VkAccelerationStructureKHR as);

// Ray tracing pipelines (rtpipeline.mm): the size of a shader group handle, which the device properties report, and
// the compute pipeline a ray tracing pipeline runs as (pipeline.mm)
uint32_t kmtlRtGroupHandleSize();
KPipeline* kmtlCreateComputeFromStage(KDevice* dev, KPipelineLayout* layout, const VkPipelineShaderStageCreateInfo* stage,
                                      bool rtLaunch);

// The end every vkCreate*Pipelines call shares (pipeline.mm): failed pipelines, KSA_MTL_STRICT
VkResult kmtlFinishPipelines(uint32_t n, VkPipeline* out);

// Memory accounting (frame log and budget)
extern std::atomic<int64_t> g_allocatedBytes;
extern std::atomic<int64_t> g_allocatedByType[3];
extern std::atomic<int64_t> g_internalBytes;  // descriptor pool chunks and upload rings
