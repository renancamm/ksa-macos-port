// KSA Metal's resources (resources/): device memory, buffers, images, views, samplers, query pools, descriptor
// sets and their layouts, formats, acceleration structures. Included by kmtl.h.
#pragma once

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
    bool inDescriptor = false;  // written to a descriptor: destroying it looks for those left behind
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
    std::atomic<uint32_t> destroyedSlots{0};  // buffer slots on the zero page (vkDestroyBuffer); binding them is logged
    uint32_t destroyedBinding = 0;            // the last of them, for the log
    uint32_t destroyedBytes = 0;
    std::atomic<bool> destroyedReported{false};
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

// Helpers (memory.mm, images.mm)
KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h);
void kmtlDestroyImage(KImage* img);
// memory.mm: a VkMemoryDedicatedRequirements in the chain, filled (prefer: its prefersDedicatedAllocation)
void kmtlFillDedicated(void* pNext, bool prefer);
uint64_t kmtlBufferViewId(VkBufferView view);
MTLCompareFunction kmtlCompare(VkCompareOp op);

// Acceleration structures (accel.mm)
void kmtlRegisterMemory(KDeviceMemory* m);
void kmtlUnregisterMemory(KDeviceMemory* m);
uint64_t kmtlAccelId(VkAccelerationStructureKHR as);
