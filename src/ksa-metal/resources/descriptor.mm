// Descriptor set layouts, pools, sets and pipeline layouts.
//
// A descriptor set is a Metal 3 argument buffer written directly: every binding owns `count` consecutive 8-byte
// slots (buffer GPU address, texture or sampler gpuResourceID); a combined image sampler owns `count` texture slots
// followed by `count` sampler slots. One trailing slot holds the address of a uint table of buffer sizes (for
// OpArrayLength), indexed by slot. SPIRV-Cross gets the same slot map with padding enabled, so the MSL struct
// layout matches slot * 8 for every shader that uses the set.
#include "kmtl.h"
#include <algorithm>

static bool isDynamic(VkDescriptorType t) {
    return t == VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC || t == VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC;
}

static bool isBuffer(VkDescriptorType t) {
    return t == VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER || t == VK_DESCRIPTOR_TYPE_STORAGE_BUFFER || isDynamic(t);
}

// A set may keep a descriptor on a destroyed buffer as long as no shader reads it. A shader that does would read
// whatever the allocator has put in that memory since, as if it were its data. So destroying a buffer moves such
// descriptors to 1 MiB of zeros with size 0, and binding the set logs it. A set's buffer slots hold addresses, so
// finding them needs no record per descriptor, only a scan for the buffers ever written to one (about 10 us each).
static constexpr NSUInteger kZeroPageBytes = 1 << 20;
static std::mutex g_setsMutex;  // the pools' sets and their slots
static std::vector<KDescriptorPool*> g_pools;
static id<MTLBuffer> g_zeroPage = nil;
std::atomic<uint64_t> g_zeroPageAddress{0};

static uint64_t zeroPage(KDevice* dev) {
    if (!g_zeroPage) {
        g_zeroPage = [dev->mtl newBufferWithLength:kZeroPageBytes options:MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked];
        g_zeroPage.label = @"zero page";
        memset(g_zeroPage.contents, 0, kZeroPageBytes);
        dev->addResident(g_zeroPage);
        g_zeroPageAddress.store(g_zeroPage.gpuAddress, std::memory_order_release);
    }
    return g_zeroPageAddress.load(std::memory_order_relaxed);
}

void kmtlForgetBuffer(KBuffer* buf) {
    uint64_t first = buf->gpuAddress();
    if (!first) return;
    uint64_t end = first + buf->size;
    std::lock_guard<std::mutex> lock(g_setsMutex);
    for (auto* p : g_pools) {
        for (auto* s : p->sets) {
            for (const auto& b : s->layout->bindings) {
                if (!isBuffer(b.type)) continue;
                for (uint32_t i = 0; i < b.count; i++) {
                    uint64_t& slot = s->slots()[b.id + i];
                    if (slot < first || slot >= end) continue;
                    s->destroyedBinding = b.binding;
                    s->destroyedBytes = s->sizes()[b.id + i];
                    slot = zeroPage(p->device);
                    s->sizes()[b.id + i] = 0;
                    s->destroyedSlots++;
                }
            }
        }
    }
}

void kmtlReportDestroyedBuffer(KDescriptorSet* s, uint32_t setIndex) {
    if (s->destroyedReported.exchange(true, std::memory_order_relaxed)) return;
    KERR_LIMIT(20, "descriptor set %u bound with a destroyed buffer (binding %u, %u bytes); a shader reading it gets zeros", setIndex,
               s->destroyedBinding, s->destroyedBytes);
}

VKFN(VkResult) vkCreateDescriptorSetLayout(VkDevice, const VkDescriptorSetLayoutCreateInfo* ci, const VkAllocationCallbacks*,
                                           VkDescriptorSetLayout* out) {
    KTRACE_FIRST();
    auto* l = new KSetLayout();
    for (uint32_t i = 0; i < ci->bindingCount; i++) {
        const auto& b = ci->pBindings[i];
        if (b.descriptorCount == 0) continue;
        KBinding kb;
        kb.binding = b.binding;
        kb.type = b.descriptorType;
        kb.count = b.descriptorCount;
        kb.stages = b.stageFlags;
        if (b.pImmutableSamplers && (b.descriptorType == VK_DESCRIPTOR_TYPE_SAMPLER ||
                                     b.descriptorType == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER)) {
            for (uint32_t j = 0; j < b.descriptorCount; j++) kb.immutableSamplers.push_back((KSampler*)b.pImmutableSamplers[j]);
        }
        l->bindings.push_back(kb);
    }
    std::sort(l->bindings.begin(), l->bindings.end(), [](const KBinding& a, const KBinding& b) { return a.binding < b.binding; });
    uint32_t slot = 0;
    for (auto& kb : l->bindings) {
        l->stages |= kb.stages;
        kb.id = slot;
        if (kb.type == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) {
            kb.samplerId = slot + kb.count;
            slot += 2 * kb.count;
        } else {
            slot += kb.count;
        }
        if (isDynamic(kb.type)) {
            for (uint32_t j = 0; j < kb.count; j++) l->dynamicSlots.push_back(kb.id + j);
            l->dynamicCount += kb.count;
        }
    }
    l->sizeSlot = slot;
    l->slotCount = slot + 1;
    KLOG(2, "set layout %p: %zu bindings, %u slots, %u dynamic", l, l->bindings.size(), l->slotCount, l->dynamicCount);
    *out = (VkDescriptorSetLayout)l;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyDescriptorSetLayout(VkDevice, VkDescriptorSetLayout l, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    if (auto* layout = (KSetLayout*)l) layout->release();
}

VKFN(void) vkGetDescriptorSetLayoutSupport(VkDevice, const VkDescriptorSetLayoutCreateInfo*, VkDescriptorSetLayoutSupport* s) {
    s->supported = VK_TRUE;
}

// ---------------------------------------------------------------------------------------------------------------
// Pools

VKFN(VkResult) vkCreateDescriptorPool(VkDevice device, const VkDescriptorPoolCreateInfo* ci, const VkAllocationCallbacks*,
                                      VkDescriptorPool* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    uint64_t descriptors = 0;
    for (uint32_t i = 0; i < ci->poolSizeCount; i++) {
        uint64_t n = ci->pPoolSizes[i].descriptorCount;
        if (ci->pPoolSizes[i].type == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) n *= 2;
        descriptors += n;
    }
    // slots (8 bytes) + sizes (4 bytes) per descriptor, plus the per-set trailing slot and padding
    VkDeviceSize cap = descriptors * 12 + (VkDeviceSize)ci->maxSets * 96 + 256;
    auto* p = new KDescriptorPool();
    p->device = dev;
    p->chunkSize = std::max<VkDeviceSize>(cap, 4096);
    {
        std::lock_guard<std::mutex> lock(g_setsMutex);
        g_pools.push_back(p);
    }
    KLOG(2, "descriptor pool %p: maxSets %u, %llu bytes", p, ci->maxSets, (unsigned long long)cap);
    *out = (VkDescriptorPool)p;
    return VK_SUCCESS;
}

static uint32_t addChunk(KDescriptorPool* p, VkDeviceSize minSize) {
    KDescriptorPool::Chunk c;
    c.capacity = std::max(p->chunkSize, minSize);
    c.buffer = [p->device->mtl newBufferWithLength:c.capacity options:MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked];
    c.buffer.label = @"descriptor pool";
    c.base = (uint8_t*)c.buffer.contents;

    g_internalBytes += (int64_t)c.capacity;
    p->chunks.push_back(c);
    return (uint32_t)p->chunks.size() - 1;
}

VKFN(void) vkDestroyDescriptorPool(VkDevice, VkDescriptorPool pool, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    auto* p = (KDescriptorPool*)pool;
    if (!p) return;
    std::lock_guard<std::mutex> lock(g_setsMutex);
    g_pools.erase(std::find(g_pools.begin(), g_pools.end(), p));
    for (auto* s : p->sets) delete s;

    for (auto& c : p->chunks) g_internalBytes -= (int64_t)c.capacity;
    delete p;
}

VKFN(VkResult) vkResetDescriptorPool(VkDevice, VkDescriptorPool pool, VkDescriptorPoolResetFlags) {
    auto* p = (KDescriptorPool*)pool;
    std::lock_guard<std::mutex> lock(g_setsMutex);
    for (auto* s : p->sets) delete s;
    p->sets.clear();
    for (auto& c : p->chunks) c.used = 0;
    p->freeBySize.clear();
    return VK_SUCCESS;
}

static void writeImmutableSamplers(KDescriptorSet* s) {
    uint64_t* slots = s->slots();
    for (auto& b : s->layout->bindings) {
        if (b.immutableSamplers.empty()) continue;
        uint32_t base = b.type == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER ? b.samplerId : b.id;
        for (uint32_t j = 0; j < b.count && j < b.immutableSamplers.size(); j++)
            slots[base + j] = b.immutableSamplers[j]->mtl.gpuResourceID._impl;
    }
}

VKFN(VkResult) vkAllocateDescriptorSets(VkDevice, const VkDescriptorSetAllocateInfo* ai, VkDescriptorSet* out) {
    KTRACE_FIRST();
    auto* p = (KDescriptorPool*)ai->descriptorPool;
    std::lock_guard<std::mutex> lock(g_setsMutex);
    for (uint32_t i = 0; i < ai->descriptorSetCount; i++) {
        auto* l = (KSetLayout*)ai->pSetLayouts[i];
        VkDeviceSize size = kmtlAlignUp<VkDeviceSize>(l->bytes(), 64);
        uint32_t chunk = UINT32_MAX;
        VkDeviceSize off = 0;
        if (auto it = p->freeBySize.find(size); it != p->freeBySize.end() && !it->second.empty()) {
            chunk = it->second.back().chunk;
            off = it->second.back().offset;
            it->second.pop_back();
        }
        if (chunk == UINT32_MAX) {
            if (p->chunks.empty() || p->chunks.back().used + size > p->chunks.back().capacity) addChunk(p, size);
            chunk = (uint32_t)p->chunks.size() - 1;
            off = p->chunks[chunk].used;
            p->chunks[chunk].used += size;
        }
        auto* s = new KDescriptorSet();
        s->layout = l;
        l->retain();
        s->chunk = chunk;
        s->buffer = p->chunks[chunk].buffer;
        s->base = p->chunks[chunk].base + off;
        s->offset = off;
        s->size = size;
        memset(s->base, 0, size);
        // The size-table slot points at the set's own uint table
        s->slots()[l->sizeSlot] = s->buffer.gpuAddress + off + l->slotCount * 8;
        writeImmutableSamplers(s);
        s->index = (uint32_t)p->sets.size();
        p->sets.push_back(s);
        out[i] = (VkDescriptorSet)s;
    }
    return VK_SUCCESS;
}

VKFN(VkResult) vkFreeDescriptorSets(VkDevice, VkDescriptorPool pool, uint32_t n, const VkDescriptorSet* sets) {
    KTRACE_FIRST();
    auto* p = (KDescriptorPool*)pool;
    std::lock_guard<std::mutex> lock(g_setsMutex);
    for (uint32_t i = 0; i < n; i++) {
        auto* s = (KDescriptorSet*)sets[i];
        if (!s) continue;
        p->freeBySize[s->size].push_back({s->chunk, s->offset});
        KDescriptorSet* last = p->sets.back();  // the last set takes the freed one's index
        p->sets[s->index] = last;
        last->index = s->index;
        p->sets.pop_back();
        delete s;
    }
    return VK_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------------
// Writes

static uint64_t textureId(VkImageView v) {
    auto* view = (KImageView*)v;
    return view && view->mtl ? view->mtl.gpuResourceID._impl : 0;
}

static void writeOne(KDescriptorSet* s, const KBinding* b, uint32_t elem, VkDescriptorType type,
                     const VkDescriptorImageInfo* ii, const VkDescriptorBufferInfo* bi, const VkBufferView* tb) {
    uint64_t* slots = s->slots();
    uint32_t* sizes = s->sizes();
    switch (type) {
    case VK_DESCRIPTOR_TYPE_SAMPLER:
        if (b->immutableSamplers.empty() && ii->sampler) slots[b->id + elem] = ((KSampler*)ii->sampler)->mtl.gpuResourceID._impl;
        break;
    case VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER:
        slots[b->id + elem] = textureId(ii->imageView);
        if (b->immutableSamplers.empty() && ii->sampler)
            slots[b->samplerId + elem] = ((KSampler*)ii->sampler)->mtl.gpuResourceID._impl;
        break;
    case VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE:
    case VK_DESCRIPTOR_TYPE_STORAGE_IMAGE:
    case VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT:
        slots[b->id + elem] = textureId(ii->imageView);
        break;
    case VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER:
    case VK_DESCRIPTOR_TYPE_STORAGE_BUFFER:
    case VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC:
    case VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC: {
        auto* buf = (KBuffer*)bi->buffer;
        uint64_t zero = g_zeroPageAddress.load(std::memory_order_relaxed);
        if (zero && slots[b->id + elem] == zero) s->destroyedSlots--;
        if (!buf) { slots[b->id + elem] = 0; sizes[b->id + elem] = 0; break; }
        buf->inDescriptor = true;
        VkDeviceSize range = bi->range == VK_WHOLE_SIZE ? buf->size - bi->offset : bi->range;
        slots[b->id + elem] = buf->gpuAddress() + bi->offset;
        sizes[b->id + elem] = (uint32_t)std::min<VkDeviceSize>(range, UINT32_MAX);
        break;
    }
    case VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER:
    case VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER:
        slots[b->id + elem] = tb ? kmtlBufferViewId(*tb) : 0;
        break;
    default: {
        KERR_ONCE("descriptor type %d not supported", type);
        break;
    }
    }
}

VKFN(void) vkUpdateDescriptorSets(VkDevice, uint32_t writeCount, const VkWriteDescriptorSet* writes, uint32_t copyCount,
                                  const VkCopyDescriptorSet* copies) {
    KTRACE_FIRST();
    std::lock_guard<std::mutex> lock(g_setsMutex);
    for (uint32_t w = 0; w < writeCount; w++) {
        const auto& wr = writes[w];
        auto* s = (KDescriptorSet*)wr.dstSet;
        uint32_t binding = wr.dstBinding;
        uint32_t elem = wr.dstArrayElement;
        const KBinding* b = s->layout->find(binding);
        auto* asw = wr.descriptorType == VK_DESCRIPTOR_TYPE_ACCELERATION_STRUCTURE_KHR
            ? (const VkWriteDescriptorSetAccelerationStructureKHR*)findNext(wr.pNext, VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET_ACCELERATION_STRUCTURE_KHR)
            : nullptr;
        for (uint32_t i = 0; i < wr.descriptorCount; i++) {
            // Writes past the end of a binding continue into the next binding
            while (b && elem >= b->count) {
                elem -= b->count;
                b = s->layout->find(++binding);
            }
            if (!b) {
                KERR("descriptor write past the last binding (set layout %p, binding %u)", s->layout, wr.dstBinding);
                break;
            }
            if (asw) {
                s->slots()[b->id + elem] = i < asw->accelerationStructureCount ? kmtlAccelId(asw->pAccelerationStructures[i]) : 0;
                elem++;
                continue;
            }
            writeOne(s, b, elem, wr.descriptorType, wr.pImageInfo ? &wr.pImageInfo[i] : nullptr,
                     wr.pBufferInfo ? &wr.pBufferInfo[i] : nullptr,
                     wr.pTexelBufferView ? &wr.pTexelBufferView[i] : nullptr);
            elem++;
        }
    }
    for (uint32_t c = 0; c < copyCount; c++) {
        const auto& cp = copies[c];
        auto* src = (KDescriptorSet*)cp.srcSet;
        auto* dst = (KDescriptorSet*)cp.dstSet;
        const KBinding* sb = src->layout->find(cp.srcBinding);
        const KBinding* db = dst->layout->find(cp.dstBinding);
        if (!sb || !db) continue;
        for (uint32_t i = 0; i < cp.descriptorCount; i++) {
            uint64_t& to = dst->slots()[db->id + cp.dstArrayElement + i];
            uint64_t from = src->slots()[sb->id + cp.srcArrayElement + i];
            uint64_t zero = g_zeroPageAddress.load(std::memory_order_relaxed);
            if (zero && isBuffer(db->type)) {
                dst->destroyedSlots += (from == zero) - (to == zero);
                if (from == zero) {
                    dst->destroyedBinding = db->binding;
                    dst->destroyedBytes = 0;
                }
            }
            to = from;
            dst->sizes()[db->id + cp.dstArrayElement + i] = src->sizes()[sb->id + cp.srcArrayElement + i];
            if (sb->type == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER)
                dst->slots()[db->samplerId + cp.dstArrayElement + i] = src->slots()[sb->samplerId + cp.srcArrayElement + i];
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Pipeline layouts

VKFN(VkResult) vkCreatePipelineLayout(VkDevice, const VkPipelineLayoutCreateInfo* ci, const VkAllocationCallbacks*, VkPipelineLayout* out) {
    KTRACE_FIRST();
    auto* pl = new KPipelineLayout();
    for (uint32_t i = 0; i < ci->setLayoutCount; i++) {
        auto* l = (KSetLayout*)ci->pSetLayouts[i];
        if (l) l->retain();
        pl->sets.push_back(l);
    }
    for (uint32_t i = 0; i < ci->pushConstantRangeCount; i++)
        pl->pushConstantSize = std::max(pl->pushConstantSize, ci->pPushConstantRanges[i].offset + ci->pPushConstantRanges[i].size);
    *out = (VkPipelineLayout)pl;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyPipelineLayout(VkDevice, VkPipelineLayout l, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KPipelineLayout*)l;
}
