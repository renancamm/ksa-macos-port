// KSA Metal's resources (resources/): device memory, buffers, images, views, samplers, fences and semaphores, query
// pools, descriptor sets and their layouts, formats, dynamic resolution, acceleration structures. Included by kmtl.h;
// a section per file, the non-dispatchable objects (handles are pointers to these) with their helpers.
#pragma once

struct KFormatInfo;
struct KImageView;
struct KSetLayout;

// ---------------------------------------------------------------------------------------------------------------
// Device memory, buffers, buffer views (memory.mm)
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
    bool inDescriptor = false;  // written to a descriptor: destroying it looks for those left behind
    bool accelStorage = false;  // holds acceleration structures: aligned for placing them in its heap
    uint64_t gpuAddress() const { return mtl ? mtl.gpuAddress + offset : 0; }
};
// A VkMemoryDedicatedRequirements in the chain, filled (prefer: its prefersDedicatedAllocation)
void kmtlFillDedicated(void* pNext, bool prefer);
uint64_t kmtlBufferViewId(VkBufferView view);

// ---------------------------------------------------------------------------------------------------------------
// Images, image views, samplers (images.mm)
struct KImage {
    KDevice* device = nullptr;
    VkImageCreateInfo info{};
    MTLTextureDescriptor* desc = nil;
    MTLSizeAndAlign sizeAlign{};
    id<MTLTexture> mtl = nil;
    bool ownsTexture = false;  // swapchain images
    VkFormat format = VK_FORMAT_UNDEFINED;
    MTLPixelFormat pixelFormat = MTLPixelFormatInvalid;
    // Where its memory is (a memoryless attachment: nil)
    id<MTLHeap> heap = nil;
    VkDeviceSize heapOffset = 0;
    // Dynamic resolution (dynamicresolution.mm): an image the main view's size or a fraction of it, drawn at the
    // frame's level. mtl is the texture of that level, placed in the same memory as the one at the image's own size
    // (full)
    bool dynamic = false;
    uint32_t level = 0;  // the level mtl has (0: full)
    // How its size follows the main view's (mainW x mainH at its bind) at a level, each way (dynamicresolution.mm,
    // KFraction)
    uint32_t mainW = 0, mainH = 0;
    struct KFraction { uint8_t k = 0; bool up = true; } fractionW, fractionH;
    id<MTLTexture> full = nil;
    std::vector<id<MTLTexture>> levels;  // by level, made when first used
    std::vector<KImageView*> views;      // its views, which follow it to another level
};

struct KImageView {
    KImage* image = nullptr;
    id<MTLTexture> mtl = nil;
    VkFormat format = VK_FORMAT_UNDEFINED;
    uint32_t baseLevel = 0, levelCount = 1, baseLayer = 0, layerCount = 1;
    // How it views the image's texture (whole: the texture itself), to view another level's the same way
    MTLPixelFormat viewFormat = MTLPixelFormatInvalid;
    MTLTextureType viewType = MTLTextureType2D;
    MTLTextureSwizzleChannels swizzle{};
    bool identitySwizzle = true, whole = true;
    // A view of a dynamic image (dynamicresolution.mm), which follows its level: its own flag, as the image may be
    // destroyed first
    bool dynamic = false;
    std::vector<id<MTLTexture>> levels;  // by level
};

struct KSampler {
    id<MTLSamplerState> mtl = nil;
};
KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h);
void kmtlDestroyImage(KImage* img);
MTLCompareFunction kmtlCompare(VkCompareOp op);

// ---------------------------------------------------------------------------------------------------------------
// Dynamic resolution (dynamicresolution.mm): the main view at a level of its size; which level:
// presentation/framerate.mm
constexpr uint32_t kDynamicParts = 64;     // a level is this many parts of the main view's size (0: the full size)
constexpr uint32_t kDynamicMinLevel = 16;  // the lowest a level can be
uint32_t kmtlDynamicScaled(uint32_t size, uint32_t level);  // a size at a level, rounded
void kmtlDynamicSetView(uint32_t width, uint32_t height, const std::vector<uint32_t>& levels);  // images of this size dynamic from now on, these levels made ahead (0 x 0: none)
void kmtlDynamicPrepare();                 // before a frame: the levels' textures of images bound since
void kmtlDynamicSetLevel(uint32_t level);  // between frames: every dynamic image at this level, what it holds carried
void kmtlDynamicBind(KImage* img);
void kmtlDynamicForget(KImage* img);
void kmtlDynamicAddView(KImageView* v);
void kmtlDynamicRemoveView(KImageView* v);
void kmtlDynamicScale(const KImage* img, float* sx, float* sy);
// What the game gives at an image's own size, at a level (scaled by sx, sy): a viewport, a rectangle covering what it
// covered within limitW x limitH ([x0, x1) x [y0, y1)), a point
MTLViewport kmtlDynamicViewport(const VkViewport& vp, double sx, double sy);
void kmtlDynamicRect(double sx, double sy, int64_t x, int64_t y, int64_t w, int64_t h, int64_t limitW, int64_t limitH,
                     int64_t* x0, int64_t* y0, int64_t* x1, int64_t* y1);
int32_t kmtlDynamicPoint(int32_t v, double s);
id<MTLTexture> kmtlViewTexture(KImageView* v, id<MTLTexture> tex, MTLPixelFormat format = MTLPixelFormatInvalid);
bool kmtlDynamicView(VkImageView view);                       // a view of a dynamic image
void kmtlDynamicTranslate(const KSetLayout* l, uint64_t* slots);  // a set's copy: its textures at the frame's level
uint32_t kmtlDynamicLevelNow();  // the level the frame being presented was drawn at

// ---------------------------------------------------------------------------------------------------------------
// Fences and semaphores (setup/device.mm)
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

// ---------------------------------------------------------------------------------------------------------------
// Query pools, pipeline caches (queries.mm)
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

// ---------------------------------------------------------------------------------------------------------------
// Descriptor sets and their layouts, pipeline layouts (descriptor.mm)
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
    std::atomic<uint32_t> destroyedSlots{0};  // buffer slots on the zero page (vkDestroyBuffer); binding them is logged
    uint32_t destroyedBinding = 0;            // the last of them, for the log
    uint32_t destroyedBytes = 0;
    std::atomic<bool> destroyedReported{false};
    // Holds a dynamic image's texture (dynamicresolution.mm): bound through a copy that takes the frame's level
    std::atomic<bool> levelTextures{false};
    uint64_t* slots() const { return (uint64_t*)base; }
    uint32_t* sizes() const { return (uint32_t*)(base + layout->slotCount * 8); }
    ~KDescriptorSet() { if (layout) layout->release(); }
};

extern std::atomic<uint64_t> g_zeroPageAddress;  // where descriptors left on a destroyed buffer point; 0 until one is
void kmtlForgetBuffer(KBuffer* b);
void kmtlReportDestroyedBuffer(KDescriptorSet* s, uint32_t setIndex);

struct KPipelineLayout {
    std::vector<KSetLayout*> sets;
    uint32_t pushConstantSize = 0;
    ~KPipelineLayout() { for (auto* l : sets) if (l) l->release(); }
};

// ---------------------------------------------------------------------------------------------------------------
// Formats (format.mm)
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
// Acceleration structures (accel.mm)
struct KAccel {
    id<MTLAccelerationStructure> mtl = nil;
    uint64_t address = 0;  // what vkGetAccelerationStructureDeviceAddressKHR hands out
    bool placed = false;   // in the heap of its Vulkan buffer (resident with it), not memory of its own
};
// A structure's descriptor from a build's inputs; with resolveBuffers, its buffers found from their device addresses
// and a top-level structure's instances converted into *outInstances (nil where the inputs can't be described)
MTLAccelerationStructureDescriptor* kmtlAccelDescriptor(const VkAccelerationStructureBuildGeometryInfoKHR* info,
                                                       const uint32_t* primitiveCounts, bool resolveBuffers,
                                                       const VkAccelerationStructureBuildRangeInfoKHR* ranges,
                                                       id<MTLDevice> dev, id<MTLBuffer>* outInstances);
void kmtlRegisterMemory(KDeviceMemory* m);
void kmtlUnregisterMemory(KDeviceMemory* m);
uint64_t kmtlAccelId(VkAccelerationStructureKHR as);
