// Device memory, buffers and buffer views (texel buffers).
#include "kmtl.h"

// ---------------------------------------------------------------------------------------------------------------
// Memory

VKFN(VkResult) vkAllocateMemory(VkDevice device, const VkMemoryAllocateInfo* ai, const VkAllocationCallbacks*, VkDeviceMemory* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    bool shared = ai->memoryTypeIndex != 0;
    MTLResourceOptions opts = (shared ? MTLResourceStorageModeShared : MTLResourceStorageModePrivate) |
        MTLResourceHazardTrackingModeUntracked;
    if (ai->memoryTypeIndex == 1) opts |= MTLResourceCPUCacheModeWriteCombined;
    MTLSizeAndAlign sa = [dev->mtl heapBufferSizeAndAlignWithLength:ai->allocationSize options:opts];
    MTLHeapDescriptor* hd = [MTLHeapDescriptor new];
    hd.type = MTLHeapTypePlacement;
    hd.storageMode = shared ? MTLStorageModeShared : MTLStorageModePrivate;
    hd.cpuCacheMode = ai->memoryTypeIndex == 1 ? MTLCPUCacheModeWriteCombined : MTLCPUCacheModeDefaultCache;
    hd.hazardTrackingMode = MTLHazardTrackingModeUntracked;
    hd.size = kmtlAlignUp<NSUInteger>(sa.size, 0x10000);
    id<MTLHeap> heap = [dev->mtl newHeapWithDescriptor:hd];
    if (!heap) {
        KERR("vkAllocateMemory: heap of %llu bytes failed", (unsigned long long)ai->allocationSize);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    id<MTLBuffer> whole = [heap newBufferWithLength:ai->allocationSize options:opts offset:0];
    if (!whole) {
        KERR("vkAllocateMemory: whole buffer of %llu bytes failed", (unsigned long long)ai->allocationSize);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    auto* m = new KDeviceMemory();
    m->device = dev;
    m->size = ai->allocationSize;
    m->typeIndex = ai->memoryTypeIndex;
    m->heap = heap;
    m->whole = whole;
    kmtlPassDumpTrackMemory(whole);
    if (shared) m->mapped = whole.contents;
    dev->addResident(heap);
    kmtlRegisterMemory(m);
    g_allocatedBytes += (int64_t)hd.size;
    g_allocatedByType[std::min(ai->memoryTypeIndex, 2u)] += (int64_t)hd.size;
    KLOG(2, "allocate memory %llu bytes type %u -> %p", (unsigned long long)ai->allocationSize, ai->memoryTypeIndex, m);
    if (kmtlDrainOn() && ai->allocationSize >= (16u << 20)) kmtlDrainMemory((int64_t)ai->allocationSize, ai->memoryTypeIndex);
    if (kmtlConfig().stalls > 0 && ai->allocationSize >= (16u << 20)) {
        char name[64] = "";
        pthread_getname_np(pthread_self(), name, sizeof name);
        KLOG(0, "stall: memory +%llu MB type %u on '%s' at %.3f present %llu", (unsigned long long)(ai->allocationSize >> 20),
             ai->memoryTypeIndex, name, CACurrentMediaTime(), (unsigned long long)kmtlPresentCount());
    }
    *out = (VkDeviceMemory)m;
    return VK_SUCCESS;
}

VKFN(void) vkFreeMemory(VkDevice, VkDeviceMemory memory, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    auto* m = (KDeviceMemory*)memory;
    if (!m) return;
    kmtlPassDumpForgetMemory(m->whole);
    if (kmtlDrainOn() && m->size >= (16u << 20)) kmtlDrainMemory(-(int64_t)m->size, m->typeIndex);
    if (kmtlConfig().stalls > 0 && m->size >= (16u << 20))
        KLOG(0, "stall: memory -%llu MB type %u at %.3f present %llu", (unsigned long long)(m->size >> 20), m->typeIndex,
             CACurrentMediaTime(), (unsigned long long)kmtlPresentCount());
    if (g_logLevel >= 2) {
        // Retains beyond ours mean something (a buffer, texture or command buffer) keeps the heap alive
        long rw = CFGetRetainCount((__bridge CFTypeRef)m->whole), rh = CFGetRetainCount((__bridge CFTypeRef)m->heap);
        KLOG(2, "free memory %p type %u %llu MB: whole retains %ld, heap retains %ld", m, m->typeIndex,
             (unsigned long long)(m->size >> 20), rw, rh);
    }
    m->device->removeResident(m->heap);
    kmtlUnregisterMemory(m);
    g_allocatedBytes -= (int64_t)m->heap.size;
    g_allocatedByType[std::min(m->typeIndex, 2u)] -= (int64_t)m->heap.size;
    delete m;
}

VKFN(VkResult) vkMapMemory(VkDevice, VkDeviceMemory memory, VkDeviceSize offset, VkDeviceSize, VkMemoryMapFlags, void** pp) {
    KTRACE_FIRST();
    auto* m = (KDeviceMemory*)memory;
    if (!m->mapped) {
        KERR("vkMapMemory on non-host-visible memory");
        return VK_ERROR_MEMORY_MAP_FAILED;
    }
    *pp = (uint8_t*)m->mapped + offset;
    return VK_SUCCESS;
}

VKFN(void) vkUnmapMemory(VkDevice, VkDeviceMemory) { KTRACE_FIRST(); }

VKFN(VkResult) vkFlushMappedMemoryRanges(VkDevice, uint32_t, const VkMappedMemoryRange*) { KTRACE_FIRST(); return VK_SUCCESS; }
VKFN(VkResult) vkInvalidateMappedMemoryRanges(VkDevice, uint32_t, const VkMappedMemoryRange*) { return VK_SUCCESS; }

VKFN(void) vkGetDeviceMemoryCommitment(VkDevice, VkDeviceMemory m, VkDeviceSize* c) { *c = ((KDeviceMemory*)m)->size; }

// ---------------------------------------------------------------------------------------------------------------
// Buffers

VKFN(VkResult) vkCreateBuffer(VkDevice device, const VkBufferCreateInfo* ci, const VkAllocationCallbacks*, VkBuffer* out) {
    KTRACE_FIRST();
    auto* b = new KBuffer();
    b->size = ci->size;
    b->accelStorage = (ci->usage & VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_STORAGE_BIT_KHR) != 0;
    *out = (VkBuffer)b;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyBuffer(VkDevice, VkBuffer buffer, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    auto* b = (KBuffer*)buffer;
    if (b && b->inDescriptor) kmtlForgetBuffer(b);
    delete b;
}

// Buffers of 4 MB or more ask for memory of their own (dedicated_large_buffers), so that freeing one gives its memory back
// to macOS instead of leaving a hole in one of VMA's shared blocks (up to 256 MB); KSA's terrain levels are 20-60 MB
// each. Measured in the solar system with far terrain let go: VMA's unused block space 0.98 GB, 0.58 at 16 MB, 0.11 at
// 4 MB
static bool preferDedicatedBuffer(VkDeviceSize size) {
    return kmtlConfig().dedicatedLargeBuffers && size >= (4u << 20);
}

static void bufferRequirements(VkDevice device, KBuffer* b, VkMemoryRequirements* r) {
    r->size = kmtlAlignUp<VkDeviceSize>(b->size, 256);
    r->alignment = 256;
    // Acceleration structures are placed in their buffer's heap (accel.mm), at Metal's alignment for them (1024 on
    // the M5)
    if (b->accelStorage && kmtlConfig().compactRayTracingMemory)
        r->alignment = std::max<VkDeviceSize>(256, [((KDevice*)device)->mtl heapAccelerationStructureSizeAndAlignWithSize:256].align);
    r->memoryTypeBits = 0x7;
}

VKFN(void) vkGetBufferMemoryRequirements(VkDevice device, VkBuffer buffer, VkMemoryRequirements* r) {
    KTRACE_FIRST();
    bufferRequirements(device, (KBuffer*)buffer, r);
}

void kmtlFillDedicated(void* pNext, bool prefer) {
    if (auto* d = (VkMemoryDedicatedRequirements*)findNext(pNext, VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS)) {
        d->prefersDedicatedAllocation = prefer ? VK_TRUE : VK_FALSE;
        d->requiresDedicatedAllocation = VK_FALSE;
    }
}

VKFN(void) vkGetBufferMemoryRequirements2(VkDevice device, const VkBufferMemoryRequirementsInfo2* info, VkMemoryRequirements2* r) {
    KTRACE_FIRST();
    bufferRequirements(device, (KBuffer*)info->buffer, &r->memoryRequirements);
    kmtlFillDedicated(r->pNext, preferDedicatedBuffer(((KBuffer*)info->buffer)->size));
}

VKFN(void) vkGetDeviceBufferMemoryRequirements(VkDevice device, const VkDeviceBufferMemoryRequirements* info, VkMemoryRequirements2* r) {
    KBuffer tmp;
    tmp.size = info->pCreateInfo->size;
    tmp.accelStorage = (info->pCreateInfo->usage & VK_BUFFER_USAGE_ACCELERATION_STRUCTURE_STORAGE_BIT_KHR) != 0;
    bufferRequirements(device, &tmp, &r->memoryRequirements);
    kmtlFillDedicated(r->pNext, preferDedicatedBuffer(tmp.size));
}

static VkResult bindBuffer(VkBuffer buffer, VkDeviceMemory memory, VkDeviceSize offset) {
    auto* b = (KBuffer*)buffer;
    auto* m = (KDeviceMemory*)memory;
    b->offset = offset;
    b->mtl = m->whole;
    return VK_SUCCESS;
}

VKFN(VkResult) vkBindBufferMemory(VkDevice, VkBuffer buffer, VkDeviceMemory memory, VkDeviceSize offset) {
    KTRACE_FIRST();
    return bindBuffer(buffer, memory, offset);
}

VKFN(VkResult) vkBindBufferMemory2(VkDevice, uint32_t n, const VkBindBufferMemoryInfo* infos) {
    for (uint32_t i = 0; i < n; i++) bindBuffer(infos[i].buffer, infos[i].memory, infos[i].memoryOffset);
    return VK_SUCCESS;
}

VKFN(VkDeviceAddress) vkGetBufferDeviceAddress(VkDevice, const VkBufferDeviceAddressInfo* info) {
    KTRACE_FIRST();
    return ((KBuffer*)info->buffer)->gpuAddress();
}

// ---------------------------------------------------------------------------------------------------------------
// Buffer views (texel buffers): Metal texture buffers over the buffer's memory

struct KBufferView {
    id<MTLTexture> mtl = nil;
};

VKFN(VkResult) vkCreateBufferView(VkDevice, const VkBufferViewCreateInfo* ci, const VkAllocationCallbacks*, VkBufferView* out) {
    KTRACE_FIRST();
    auto* b = (KBuffer*)ci->buffer;
    const KFormatInfo* fi = kmtlFormat(ci->format);
    if (!fi || !b->mtl) {
        if (!fi) KERR("vkCreateBufferView: unsupported format %d", ci->format);
        else KERR("vkCreateBufferView: buffer %p has no memory bound", b);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    VkDeviceSize range = ci->range == VK_WHOLE_SIZE ? b->size - ci->offset : ci->range;
    NSUInteger texels = (NSUInteger)(range / fi->blockBytes);
    MTLTextureDescriptor* d = [MTLTextureDescriptor textureBufferDescriptorWithPixelFormat:fi->mtl width:texels
        resourceOptions:b->mtl.resourceOptions usage:MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite];
    auto* v = new KBufferView();
    v->mtl = [b->mtl newTextureWithDescriptor:d offset:b->offset + ci->offset bytesPerRow:texels * fi->blockBytes];
    if (!v->mtl) {
        KERR("vkCreateBufferView: texture buffer of %lu texels format %d failed", (unsigned long)texels, ci->format);
        delete v;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    *out = (VkBufferView)v;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyBufferView(VkDevice, VkBufferView view, const VkAllocationCallbacks*) {
    delete (KBufferView*)view;
}

uint64_t kmtlBufferViewId(VkBufferView view) {
    auto* v = (KBufferView*)view;
    return v && v->mtl ? v->mtl.gpuResourceID._impl : 0;
}
