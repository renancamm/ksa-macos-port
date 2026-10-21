// KSA Metal driver: a Vulkan-shaped Metal implementation of the Vulkan surface Kitten Space Agency uses.
// Shared declarations. See driver/README.md for the design.
#pragma once

#define VK_USE_PLATFORM_METAL_EXT 1
#define VK_ENABLE_BETA_EXTENSIONS 1
#include <vulkan/vulkan.h>

#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#define KMTL_EXPORT extern "C" __attribute__((visibility("default")))
#define VKFN(ret) KMTL_EXPORT VKAPI_ATTR ret VKAPI_CALL

// ---------------------------------------------------------------------------------------------------------------
// Logging. KSA_MTL_LOG: 0 errors only, 1 + first call of each entry point and warnings, 2 + object creation,
// 3 + per-command trace.
extern int g_logLevel;
void kmtlLog(int level, const char* fmt, ...) __attribute__((format(printf, 2, 3)));
#define KLOG(lvl, ...) do { if (g_logLevel >= (lvl)) kmtlLog(lvl, __VA_ARGS__); } while (0)
#define KERR(...) kmtlLog(-1, __VA_ARGS__)
// Autorelease pool for the duration of an entry point: the game calls in from .NET threads that have no pool, so
// autoreleased Metal objects (command buffers, encoders, descriptors) would otherwise never be released, and every
// command buffer would keep the resources it used alive forever
extern "C" void* objc_autoreleasePoolPush(void);
extern "C" void objc_autoreleasePoolPop(void*);
struct KAutoreleasePool {
    void* token = objc_autoreleasePoolPush();
    ~KAutoreleasePool() { objc_autoreleasePoolPop(token); }
};

// Every entry point starts with this: an autorelease pool, and a log of its first call at level 1
#define KTRACE_FIRST() KAutoreleasePool _kpool; do { static std::atomic<bool> s_seen{false}; \
    if (g_logLevel >= 1 && !s_seen.exchange(true)) kmtlLog(1, "first call: %s", __func__); } while (0)
#define KUNIMPL() do { static std::atomic<bool> s_seen{false}; \
    if (!s_seen.exchange(true)) kmtlLog(0, "UNIMPLEMENTED: %s", __func__); } while (0)

const char* kmtlEnv(const char* name);
int kmtlEnvInt(const char* name, int def);

// ---------------------------------------------------------------------------------------------------------------
// Buffer indices per stage (vertex buffers count down from 30 like MoltenVK, so the low indices stay ours)
enum : uint32_t {
    kMaxSets = 16,
    kPushConstantBufferIndex = 10,
    kDrawIdBufferIndex = 11,
    kBufferSizeBufferIndex = 12,
    kViewMaskBufferIndex = 13,
    kDynamicOffsetBufferIndex = 14,
    kSwizzleBufferIndex = 15,
    // Tessellation stage buffers (vertex/control kernels, evaluation function)
    kTessOutputBufferIndex = 16,
    kTessIndexBufferIndex = 17,
    kTessInputBufferIndex = 18,
    kTessPatchBufferIndex = 19,
    kTessFactorBufferIndex = 20,
    kTessParamsBufferIndex = 21,
    kVertexBufferTop = 30,  // binding n -> buffer index 30 - n
    kMaxPushConstants = 4096,
};
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
    KInstance* instance = nullptr;
    id<MTLDevice> mtl = nil;
};

struct KQueue : KDispatchable {
    KDevice* device = nullptr;
    id<MTLCommandQueue> mtl = nil;
    // Every encoder waits on and updates this fence, so encoders run in submission order (barriers are no-ops)
    id<MTLFence> chain = nil;
    std::mutex submitMutex;
};

struct KInternalPipelines;

struct KDevice : KDispatchable {
    KPhysicalDevice* physical = nullptr;
    id<MTLDevice> mtl = nil;
    KQueue* queue = nullptr;
    id<MTLResidencySet> residency = nil;
    std::mutex residencyMutex;
    bool residencyDirty = false;
    KInternalPipelines* internal = nullptr;
    std::mutex libraryMutex;
    std::unordered_map<uint64_t, id<MTLLibrary>> libraryCache;  // MSL hash -> library
    id<MTLBuffer> fanIndices = nil;  // triangle-fan emulation: (0, i, i+1) for i = 1..n-2
    uint32_t fanVertexCapacity = 0;
    std::mutex fanMutex;
    // Tessellation stage outputs: one GPU-only buffer shared by every tessellated draw of every command buffer
    // (all encoders run in queue order through the chain fence), grown to the largest draw
    id<MTLBuffer> tessScratch = nil;
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
    KDevice* device = nullptr;
    VkDeviceSize size = 0;
    VkBufferUsageFlags usage = 0;
    KDeviceMemory* memory = nullptr;
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
    KDeviceMemory* memory = nullptr;
    bool ownsTexture = false;  // swapchain images
    VkFormat format = VK_FORMAT_UNDEFINED;
    MTLPixelFormat pixelFormat = MTLPixelFormatInvalid;
};

struct KImageView {
    KDevice* device = nullptr;
    KImage* image = nullptr;
    VkImageViewCreateInfo info{};
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

struct KSemaphore { int unused = 0; };

struct KQueryPool {
    VkQueryType type;
    uint32_t count;
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
    int dynamicIndex = -1;   // index among the set's dynamic buffers, in binding order
};

struct KSetLayout {
    std::vector<KBinding> bindings;  // sorted by binding number
    uint32_t slotCount = 0;          // including the trailing buffer-size slot
    uint32_t sizeSlot = 0;           // slot holding the address of the buffer-size table
    uint32_t dynamicCount = 0;
    std::vector<uint32_t> dynamicSlots;  // slot of each dynamic descriptor (binding order, array elements)
    uint32_t bytes() const { return slotCount * 12 + 16; }  // slots + uint sizes table, padded
    const KBinding* find(uint32_t b) const {
        for (auto& x : bindings) if (x.binding == b) return &x;
        return nullptr;
    }
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
    struct Free { uint32_t chunk; VkDeviceSize offset, size; };
    std::vector<Free> freeList;
    std::vector<struct KDescriptorSet*> sets;
};

struct KDescriptorSet {
    KDescriptorPool* pool = nullptr;
    KSetLayout* layout = nullptr;
    id<MTLBuffer> buffer = nil;  // the pool chunk holding this set
    uint8_t* base = nullptr;     // CPU address of the set
    uint32_t chunk = 0;
    VkDeviceSize offset = 0;     // within the chunk
    VkDeviceSize size = 0;
    uint64_t* slots() const { return (uint64_t*)base; }
    uint32_t* sizes() const { return (uint32_t*)(base + layout->slotCount * 8); }
};

struct KPipelineLayout {
    std::vector<KSetLayout*> sets;
    // Owned copies: Vulkan lets the app destroy set layouts after creating the pipeline layout
    std::vector<KSetLayout> setCopies;
    uint32_t pushConstantSize = 0;
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

struct KPipeline {
    VkPipelineBindPoint bindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    KPipelineLayout* layout = nullptr;
    // Graphics
    id<MTLRenderPipelineState> render = nil;
    id<MTLDepthStencilState> depthStencil = nil;
    MTLPrimitiveType primitive = MTLPrimitiveTypeTriangle;
    bool triangleFan = false;  // emulated with a generated index buffer
    // Tessellation: vertex stage as compute kernels (no index, 16-bit, 32-bit), control stage as a compute kernel
    bool tess = false;
    id<MTLComputePipelineState> tessVertex[3] = {nil, nil, nil};
    id<MTLComputePipelineState> tessControl = nil;
    uint32_t tessInCP = 3, tessOutCP = 3;
    NSUInteger tessVertexStride = 0, tessControlStride = 0, tessPatchStride = 0;
    bool tessQuads = false;
    MTLCullMode cull = MTLCullModeNone;
    MTLWinding winding = MTLWindingCounterClockwise;
    MTLTriangleFillMode fill = MTLTriangleFillModeFill;
    MTLDepthClipMode depthClip = MTLDepthClipModeClip;
    bool depthBiasEnable = false;
    float depthBias[3] = {0, 0, 0};  // constant, slope, clamp
    bool dynamicViewport = false, dynamicScissor = false, dynamicDepthBias = false, dynamicStencilRef = false;
    std::vector<VkViewport> viewports;
    std::vector<VkRect2D> scissors;
    uint32_t stencilRef[2] = {0, 0};
    float blendConstants[4] = {0, 0, 0, 0};
    bool vertexUsesDrawId = false;
    bool fragmentUsesDrawId = false;
    uint32_t viewMask = 0;
    bool hasFragment = false;
    // Compute
    id<MTLComputePipelineState> compute = nil;
    MTLSize workgroup = MTLSizeMake(1, 1, 1);
    bool computeUsesDrawId = false;
};

// ---------------------------------------------------------------------------------------------------------------
// Command buffers
struct KCommandPool {
    KDevice* device = nullptr;
    std::vector<struct KCommandBuffer*> buffers;
};

struct KBoundSet {
    id<MTLBuffer> buffer = nil;
    NSUInteger offset = 0;
};

struct KUploadRing {
    std::vector<id<MTLBuffer>> chunks;
    size_t chunk = 0;
    size_t offset = 0;
    static constexpr size_t kChunkSize = 1 << 20;
    // Returns CPU pointer, buffer and offset of `size` bytes aligned to 256
    void* alloc(KDevice* dev, size_t size, id<MTLBuffer>* outBuf, NSUInteger* outOff);
    void reset() { chunk = 0; offset = 0; }
};

struct KCommandBuffer : KDispatchable {
    KDevice* device = nullptr;
    KCommandPool* pool = nullptr;
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

    // Encoder ordering. Default: every encoder waits on and updates the queue's chain fence (strict order).
    // KSA_MTL_OVERLAP=1: encoders wait only on the encoders before the last Vulkan barrier (fence sets per
    // command buffer); command buffers still join the chain at their end, so they stay ordered against each other
    std::vector<id<MTLFence>> waitSet, curSet, fencePool;
    size_t fenceNext = 0;
    bool encoderSpansBarrier = false;
    void beginCommands();
    void joinCommands();
    void epochBarrier();
    id<MTLFence> encoderDoneFence();
    template <typename E> void waitForPrior(E enc);

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
    struct VB { id<MTLBuffer> buf = nil; NSUInteger off = 0; };
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
    bool pipelineDirty = false;
    KPipeline* encodedPipeline = nullptr;

    // Counters for the frame log
    uint32_t draws = 0, dispatches = 0, passes = 0;

    void endEncoder();
    id<MTLComputeCommandEncoder> computeEncoder();
    id<MTLBlitCommandEncoder> blitEncoder();
    void beginRender(MTLRenderPassDescriptor* rp, uint32_t w, uint32_t h, bool resuming = false);
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

struct KSwapchain {
    KDevice* device = nullptr;
    KSurface* surface = nullptr;
    std::vector<KImage*> images;
    uint32_t next = 0;
    VkExtent2D extent{};
    VkFormat format = VK_FORMAT_B8G8R8A8_UNORM;
    VkPresentModeKHR presentMode = VK_PRESENT_MODE_FIFO_KHR;
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
template <typename T, typename H> inline T* K(H h) { return reinterpret_cast<T*>(h); }
#define KH(T, h) (reinterpret_cast<T*>(h))
template <typename H, typename T> inline H toHandle(T* p) { return reinterpret_cast<H>(p); }

inline const void* findNext(const void* chain, VkStructureType type) {
    for (auto* p = (const VkBaseInStructure*)chain; p; p = p->pNext)
        if (p->sType == type) return p;
    return nullptr;
}

KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h);
void kmtlDestroyImage(KImage* img);
id<MTLTexture> kmtlBindTexture(KImage* img);  // creates the texture once memory is bound
uint64_t kmtlHash(const void* data, size_t size);
id<MTLLibrary> kmtlCompileMSL(KDevice* dev, const std::string& msl, std::string* err);

// Internal shaders (fills, image blits)
void kmtlInitInternal(KDevice* dev);
void kmtlInternalFillBuffer(KCommandBuffer* cb, id<MTLBuffer> buf, NSUInteger offset, NSUInteger size, uint32_t data);
void kmtlInternalBlit(KCommandBuffer* cb, KImage* src, KImage* dst, const VkImageBlit2& region, VkFilter filter);

// Frame instruments
void kmtlOnPresent(KDevice* dev, KImage* image, id<MTLCommandBuffer> cb);
void kmtlCountWork(uint32_t draws, uint32_t dispatches, uint32_t passes);

PFN_vkVoidFunction kmtlLookup(const char* name, int level);

// Acceleration structures (accel.mm)
void kmtlRegisterMemory(KDeviceMemory* m);
void kmtlUnregisterMemory(KDeviceMemory* m);
uint64_t kmtlAccelId(VkAccelerationStructureKHR as);
bool kmtlRayQueryEnabled();
bool kmtlTessellationEnabled();  // level 0 global, 1 instance, 2 device

// Memory accounting (frame log and budget)
extern std::atomic<int64_t> g_allocatedBytes;
extern std::atomic<int64_t> g_allocatedByType[3];
extern std::atomic<int64_t> g_internalBytes;  // descriptor pool chunks and upload rings
