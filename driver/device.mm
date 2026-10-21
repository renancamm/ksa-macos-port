// Device, queue, memory, buffers, images, views, samplers, synchronization, submission.
#include "kmtl.h"

std::atomic<int64_t> g_allocatedBytes{0};
std::atomic<int64_t> g_allocatedByType[3] = {{0}, {0}, {0}};
std::atomic<int64_t> g_internalBytes{0};  // descriptor pool chunks and upload rings

// ---------------------------------------------------------------------------------------------------------------
// Residency: every heap and standalone texture lives in one residency set attached to the queue, so resources
// reached only through argument buffers are resident without per-encoder useResource calls.

void KDevice::addResident(id<MTLAllocation> a) {
    std::lock_guard<std::mutex> lock(residencyMutex);
    [residency addAllocation:a];
    residencyDirty = true;
}

void KDevice::removeResident(id<MTLAllocation> a) {
    std::lock_guard<std::mutex> lock(residencyMutex);
    [residency removeAllocation:a];
    residencyDirty = true;
}

void KDevice::commitResidency() {
    std::lock_guard<std::mutex> lock(residencyMutex);
    if (residencyDirty) {
        [residency commit];
        residencyDirty = false;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Device and queue

VKFN(VkResult) vkCreateDevice(VkPhysicalDevice physicalDevice, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks*, VkDevice* out) {
    KTRACE_FIRST();
    auto* pd = (KPhysicalDevice*)physicalDevice;
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) KLOG(1, "device extension: %s", ci->ppEnabledExtensionNames[i]);
    auto* dev = new KDevice();
    dev->physical = pd;
    dev->mtl = pd->mtl;
    NSError* err = nil;
    MTLResidencySetDescriptor* rd = [MTLResidencySetDescriptor new];
    rd.label = @"ksamtl";
    rd.initialCapacity = 1024;
    dev->residency = [dev->mtl newResidencySetWithDescriptor:rd error:&err];
    if (!dev->residency) {
        KERR("residency set: %s", err.description.UTF8String);
        delete dev;
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    auto* q = new KQueue();
    q->device = dev;
    q->mtl = [dev->mtl newCommandQueueWithMaxCommandBufferCount:256];
    q->mtl.label = @"ksamtl queue";
    [q->mtl addResidencySet:dev->residency];
    q->chain = [dev->mtl newFence];
    q->chain.label = @"ksamtl encoder chain";
    // Update the chain fence once so the first real encoder has something to wait on
    id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b updateFence:q->chain];
    [b endEncoding];
    [cb commit];
    dev->queue = q;
    // A transfer-only queue family: KSA's texture streamer then uploads on its own queue
    if (kmtlTransferQueueEnabled()) {
        auto* t = new KQueue();
        t->device = dev;
        t->family = 1;
        t->mtl = [dev->mtl newCommandQueueWithMaxCommandBufferCount:64];
        t->mtl.label = @"ksamtl transfer queue";
        [t->mtl addResidencySet:dev->residency];
        t->chain = [dev->mtl newFence];
        t->chain.label = @"ksamtl transfer chain";
        id<MTLCommandBuffer> tc = [t->mtl commandBuffer];
        id<MTLBlitCommandEncoder> tb = [tc blitCommandEncoder];
        [tb updateFence:t->chain];
        [tb endEncoding];
        [tc commit];
        dev->transferQueue = t;
    }
    kmtlInitInternal(dev);
    kmtlShaderPrewarm(dev);
    KLOG(1, "device created");
    *out = (VkDevice)dev;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyDevice(VkDevice device, const VkAllocationCallbacks*) {
    auto* dev = (KDevice*)device;
    if (!dev) return;
    delete dev->queue;
    delete dev->transferQueue;
    delete dev;
}

VKFN(void) vkGetDeviceQueue(VkDevice device, uint32_t family, uint32_t index, VkQueue* out) {
    KTRACE_FIRST();
    *out = (VkQueue)((KDevice*)device)->queueOf(family);
}

VKFN(void) vkGetDeviceQueue2(VkDevice device, const VkDeviceQueueInfo2* info, VkQueue* out) {
    *out = (VkQueue)((KDevice*)device)->queueOf(info->queueFamilyIndex);
}

static void waitIdle(KQueue* q) {
    q->device->commitResidency();
    id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
    [cb commit];
    [cb waitUntilCompleted];
}

VKFN(VkResult) vkDeviceWaitIdle(VkDevice device) {
    KTRACE_FIRST();
    if (((KDevice*)device)->transferQueue) waitIdle(((KDevice*)device)->transferQueue);
    waitIdle(((KDevice*)device)->queue);
    return VK_SUCCESS;
}

VKFN(VkResult) vkQueueWaitIdle(VkQueue queue) {
    KTRACE_FIRST();
    waitIdle((KQueue*)queue);
    return VK_SUCCESS;
}

static void signalFence(KFence* f) {
    std::lock_guard<std::mutex> lock(f->m);
    f->signaled = true;
    f->cv.notify_all();
}

static void checkCommandBufferError(id<MTLCommandBuffer> cb) {
    if (cb.status == MTLCommandBufferStatusError) {
        static std::atomic<int> reported{0};
        if (reported++ < 20) KERR("command buffer failed: %s", cb.error.description.UTF8String);
    }
}

// Waits on semaphores another queue signals: a command buffer that waits for the events, then advances this queue's
// chain fence, which the following command buffers' first encoders wait on
static void waitSemaphores(KQueue* q, uint32_t n, const VkSemaphore* sems, std::vector<id<MTLCommandBuffer>>& toCommit) {
    id<MTLCommandBuffer> w = nil;
    for (uint32_t i = 0; i < n; i++) {
        auto* s = (KSemaphore*)sems[i];
        if (!s || !s->signaledBy || s->signaledBy == q) continue;
        if (!w) { w = [q->mtl commandBuffer]; w.label = @"semaphore wait"; }
        [w encodeWaitForEvent:s->event value:s->value];
    }
    if (!w) return;
    id<MTLBlitCommandEncoder> b = [w blitCommandEncoder];
    [b waitForFence:q->chain];
    [b updateFence:q->chain];
    [b endEncoding];
    toCommit.push_back(w);
}

// Signals after the submission's work: the event is raised at the end of its last command buffer
static void signalSemaphores(KQueue* q, uint32_t n, const VkSemaphore* sems, std::vector<id<MTLCommandBuffer>>& toCommit) {
    if (!n) return;
    for (uint32_t i = 0; i < n; i++) {
        auto* s = (KSemaphore*)sems[i];
        if (!s) continue;
        if (!s->event) s->event = [q->device->mtl newSharedEvent];
        if (toCommit.empty()) toCommit.push_back([q->mtl commandBuffer]);
        [toCommit.back() encodeSignalEvent:s->event value:++s->value];
        s->signaledBy = q;
    }
}

VKFN(VkResult) vkQueueSubmit(VkQueue queue, uint32_t submitCount, const VkSubmitInfo* submits, VkFence fence) {
    KTRACE_FIRST();
    auto* q = (KQueue*)queue;
    q->device->commitResidency();
    std::lock_guard<std::mutex> lock(q->submitMutex);
    std::vector<id<MTLCommandBuffer>> toCommit;
    for (uint32_t s = 0; s < submitCount; s++) {
        waitSemaphores(q, submits[s].waitSemaphoreCount, submits[s].pWaitSemaphores, toCommit);
        for (uint32_t i = 0; i < submits[s].commandBufferCount; i++) {
            auto* cb = (KCommandBuffer*)submits[s].pCommandBuffers[i];
            if (!cb->mtl) {
                if (cb->submittedOnce) KERR("command buffer %p submitted again without re-recording (unsupported)", cb);
                continue;
            }
            cb->endEncoder();
            [cb->mtl addCompletedHandler:^(id<MTLCommandBuffer> c) { checkCommandBufferError(c); kmtlNoteGpuTime(c); }];
            kmtlProfSubmit(cb, cb->mtl);
            cb->submitTimestamps(cb->mtl);
            toCommit.push_back(cb->mtl);
            kmtlCountWork(cb->draws, cb->dispatches, cb->passes);
            cb->mtl = nil;
            cb->submittedOnce = true;
        }
        signalSemaphores(q, submits[s].signalSemaphoreCount, submits[s].pSignalSemaphores, toCommit);
    }
    if (fence) {
        // Encoders are chained by the queue fence, so the last command buffer completing means all did
        auto* f = (KFence*)fence;
        if (toCommit.empty()) toCommit.push_back([q->mtl commandBuffer]);
        [toCommit.back() addCompletedHandler:^(id<MTLCommandBuffer>) { signalFence(f); }];
    }
    for (id<MTLCommandBuffer> cb : toCommit) [cb commit];
    return VK_SUCCESS;
}

VKFN(VkResult) vkQueueSubmit2(VkQueue queue, uint32_t submitCount, const VkSubmitInfo2* submits, VkFence fence) {
    KTRACE_FIRST();
    std::vector<VkSubmitInfo> s1(submitCount);
    std::vector<std::vector<VkCommandBuffer>> cbs(submitCount);
    std::vector<std::vector<VkSemaphore>> waits(submitCount), signals(submitCount);
    for (uint32_t s = 0; s < submitCount; s++) {
        for (uint32_t i = 0; i < submits[s].commandBufferInfoCount; i++)
            cbs[s].push_back(submits[s].pCommandBufferInfos[i].commandBuffer);
        for (uint32_t i = 0; i < submits[s].waitSemaphoreInfoCount; i++) waits[s].push_back(submits[s].pWaitSemaphoreInfos[i].semaphore);
        for (uint32_t i = 0; i < submits[s].signalSemaphoreInfoCount; i++) signals[s].push_back(submits[s].pSignalSemaphoreInfos[i].semaphore);
        s1[s] = {VK_STRUCTURE_TYPE_SUBMIT_INFO};
        s1[s].commandBufferCount = (uint32_t)cbs[s].size();
        s1[s].pCommandBuffers = cbs[s].data();
        s1[s].waitSemaphoreCount = (uint32_t)waits[s].size();
        s1[s].pWaitSemaphores = waits[s].data();
        s1[s].signalSemaphoreCount = (uint32_t)signals[s].size();
        s1[s].pSignalSemaphores = signals[s].data();
    }
    return vkQueueSubmit(queue, submitCount, s1.data(), fence);
}

// ---------------------------------------------------------------------------------------------------------------
// Fences and semaphores

VKFN(VkResult) vkCreateFence(VkDevice, const VkFenceCreateInfo* ci, const VkAllocationCallbacks*, VkFence* out) {
    KTRACE_FIRST();
    auto* f = new KFence();
    f->signaled = (ci->flags & VK_FENCE_CREATE_SIGNALED_BIT) != 0;
    *out = (VkFence)f;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyFence(VkDevice, VkFence fence, const VkAllocationCallbacks*) {
    // A completion handler may still hold it; fences are tiny, so leak those that are pending
    auto* f = (KFence*)fence;
    if (!f) return;
    std::unique_lock<std::mutex> lock(f->m);
    if (f->signaled) { lock.unlock(); delete f; }
}

VKFN(VkResult) vkResetFences(VkDevice, uint32_t n, const VkFence* fences) {
    KTRACE_FIRST();
    for (uint32_t i = 0; i < n; i++) {
        auto* f = (KFence*)fences[i];
        std::lock_guard<std::mutex> lock(f->m);
        f->signaled = false;
    }
    return VK_SUCCESS;
}

VKFN(VkResult) vkGetFenceStatus(VkDevice, VkFence fence) {
    auto* f = (KFence*)fence;
    std::lock_guard<std::mutex> lock(f->m);
    return f->signaled ? VK_SUCCESS : VK_NOT_READY;
}

VKFN(VkResult) vkWaitForFences(VkDevice, uint32_t n, const VkFence* fences, VkBool32 waitAll, uint64_t timeout) {
    KTRACE_FIRST();
    auto deadline = std::chrono::steady_clock::now() +
        std::chrono::nanoseconds(timeout > (uint64_t)INT64_MAX / 2 ? (uint64_t)INT64_MAX / 2 : timeout);
    if (waitAll || n == 1) {
        for (uint32_t i = 0; i < n; i++) {
            auto* f = (KFence*)fences[i];
            std::unique_lock<std::mutex> lock(f->m);
            if (!f->cv.wait_until(lock, deadline, [f] { return f->signaled; })) return VK_TIMEOUT;
        }
        return VK_SUCCESS;
    }
    while (true) {
        for (uint32_t i = 0; i < n; i++) {
            auto* f = (KFence*)fences[i];
            std::lock_guard<std::mutex> lock(f->m);
            if (f->signaled) return VK_SUCCESS;
        }
        if (std::chrono::steady_clock::now() >= deadline) return VK_TIMEOUT;
        usleep(100);
    }
}

VKFN(VkResult) vkCreateSemaphore(VkDevice, const VkSemaphoreCreateInfo*, const VkAllocationCallbacks*, VkSemaphore* out) {
    KTRACE_FIRST();
    *out = (VkSemaphore) new KSemaphore();
    return VK_SUCCESS;
}

VKFN(void) vkDestroySemaphore(VkDevice, VkSemaphore s, const VkAllocationCallbacks*) {
    delete (KSemaphore*)s;
}

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
    hd.size = (sa.size + 0xFFFF) & ~(NSUInteger)0xFFFF;
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
    if (shared) m->mapped = whole.contents;
    dev->addResident(heap);
    kmtlRegisterMemory(m);
    g_allocatedBytes += (int64_t)hd.size;
    g_allocatedByType[std::min(ai->memoryTypeIndex, 2u)] += (int64_t)hd.size;
    KLOG(2, "allocate memory %llu bytes type %u -> %p", (unsigned long long)ai->allocationSize, ai->memoryTypeIndex, m);
    *out = (VkDeviceMemory)m;
    return VK_SUCCESS;
}

VKFN(void) vkFreeMemory(VkDevice, VkDeviceMemory memory, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    auto* m = (KDeviceMemory*)memory;
    if (!m) return;
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
    b->device = (KDevice*)device;
    b->size = ci->size;
    b->usage = ci->usage;
    if (auto* u2 = (const VkBufferUsageFlags2CreateInfo*)findNext(ci->pNext, VK_STRUCTURE_TYPE_BUFFER_USAGE_FLAGS_2_CREATE_INFO))
        b->usage = (VkBufferUsageFlags)u2->usage;
    *out = (VkBuffer)b;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyBuffer(VkDevice, VkBuffer buffer, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KBuffer*)buffer;
}

static void bufferRequirements(KBuffer* b, VkMemoryRequirements* r) {
    r->size = (b->size + 255) & ~255ull;
    r->alignment = 256;
    r->memoryTypeBits = 0x7;
}

VKFN(void) vkGetBufferMemoryRequirements(VkDevice, VkBuffer buffer, VkMemoryRequirements* r) {
    KTRACE_FIRST();
    bufferRequirements((KBuffer*)buffer, r);
}

static void fillDedicated(void* pNext, bool prefer) {
    if (auto* d = (VkMemoryDedicatedRequirements*)findNext(pNext, VK_STRUCTURE_TYPE_MEMORY_DEDICATED_REQUIREMENTS)) {
        d->prefersDedicatedAllocation = prefer ? VK_TRUE : VK_FALSE;
        d->requiresDedicatedAllocation = VK_FALSE;
    }
}

VKFN(void) vkGetBufferMemoryRequirements2(VkDevice, const VkBufferMemoryRequirementsInfo2* info, VkMemoryRequirements2* r) {
    KTRACE_FIRST();
    bufferRequirements((KBuffer*)info->buffer, &r->memoryRequirements);
    fillDedicated(r->pNext, false);
}

VKFN(void) vkGetDeviceBufferMemoryRequirements(VkDevice device, const VkDeviceBufferMemoryRequirements* info, VkMemoryRequirements2* r) {
    KBuffer tmp;
    tmp.size = info->pCreateInfo->size;
    bufferRequirements(&tmp, &r->memoryRequirements);
    fillDedicated(r->pNext, false);
}

static VkResult bindBuffer(VkBuffer buffer, VkDeviceMemory memory, VkDeviceSize offset) {
    auto* b = (KBuffer*)buffer;
    auto* m = (KDeviceMemory*)memory;
    b->memory = m;
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
// Images

static MTLTextureDescriptor* textureDescriptor(const VkImageCreateInfo* ci, MTLPixelFormat pf, const KFormatInfo* fi) {
    MTLTextureDescriptor* d = [MTLTextureDescriptor new];
    d.pixelFormat = pf;
    d.width = ci->extent.width;
    d.height = ci->extent.height;
    d.depth = ci->extent.depth;
    d.mipmapLevelCount = ci->mipLevels;
    d.sampleCount = ci->samples;
    uint32_t layers = ci->arrayLayers;
    bool cube = (ci->flags & VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT) && layers % 6 == 0 && ci->samples == 1;
    switch (ci->imageType) {
    case VK_IMAGE_TYPE_1D:
        d.textureType = layers > 1 ? MTLTextureType1DArray : MTLTextureType1D;
        d.arrayLength = layers;
        break;
    case VK_IMAGE_TYPE_3D:
        d.textureType = MTLTextureType3D;
        d.arrayLength = 1;
        break;
    default:
        if (ci->samples > 1) {
            d.textureType = layers > 1 ? MTLTextureType2DMultisampleArray : MTLTextureType2DMultisample;
            d.arrayLength = layers;
        } else if (cube) {
            d.textureType = layers > 6 ? MTLTextureTypeCubeArray : MTLTextureTypeCube;
            d.arrayLength = layers / 6;
        } else {
            d.textureType = layers > 1 ? MTLTextureType2DArray : MTLTextureType2D;
            d.arrayLength = layers;
        }
        break;
    }
    MTLTextureUsage u = MTLTextureUsageShaderRead;
    VkImageUsageFlags vu = ci->usage;
    if (vu & VK_IMAGE_USAGE_STORAGE_BIT) u |= MTLTextureUsageShaderWrite;
    if (vu & (VK_IMAGE_USAGE_STORAGE_BIT)) {
        if (pf == MTLPixelFormatR32Uint || pf == MTLPixelFormatR32Sint || pf == MTLPixelFormatRG32Uint) u |= MTLTextureUsageShaderAtomic;
    }
    if (fi->renderable && (vu & (VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
                                 VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT |
                                 VK_IMAGE_USAGE_STORAGE_BIT)))
        u |= MTLTextureUsageRenderTarget;
    if ((ci->flags & VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT) || fi->depth || fi->stencil) u |= MTLTextureUsagePixelFormatView;
    d.usage = u;
    d.storageMode = MTLStorageModePrivate;
    d.hazardTrackingMode = MTLHazardTrackingModeUntracked;
    return d;
}

VKFN(VkResult) vkCreateImage(VkDevice device, const VkImageCreateInfo* ci, const VkAllocationCallbacks*, VkImage* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    const KFormatInfo* fi = kmtlFormat(ci->format);
    if (!fi) {
        KERR("vkCreateImage: unsupported format %d", ci->format);
        return VK_ERROR_FORMAT_NOT_SUPPORTED;
    }
    auto* img = new KImage();
    img->device = dev;
    img->info = *ci;
    img->info.pNext = nullptr;
    img->info.pQueueFamilyIndices = nullptr;
    img->format = ci->format;
    img->pixelFormat = fi->mtl;
    img->desc = textureDescriptor(ci, fi->mtl, fi);
    if (ci->tiling == VK_IMAGE_TILING_LINEAR) img->desc.storageMode = MTLStorageModeShared;
    img->sizeAlign = [dev->mtl heapTextureSizeAndAlignWithDescriptor:img->desc];
    KLOG(2, "image %p %ux%ux%u fmt %d mips %u layers %u samples %u usage 0x%x -> %lu bytes", img, ci->extent.width,
         ci->extent.height, ci->extent.depth, ci->format, ci->mipLevels, ci->arrayLayers, ci->samples, ci->usage,
         (unsigned long)img->sizeAlign.size);
    *out = (VkImage)img;
    return VK_SUCCESS;
}

void kmtlDestroyImage(KImage* img) {
    if (!img) return;
    if (img->ownsTexture && img->mtl) img->device->removeResident(img->mtl);
    delete img;
}

VKFN(void) vkDestroyImage(VkDevice, VkImage image, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    kmtlDestroyImage((KImage*)image);
}

// Transient attachments live only in tile memory (KSA_MTL_MEMORYLESS=0 turns it off): Metal memoryless textures,
// outside the heaps; the memory the game binds them to is a token allocation
static bool memoryless(const VkImageCreateInfo& ci) {
    static const bool on = kmtlEnvInt("KSA_MTL_MEMORYLESS", 1) != 0;
    const VkImageUsageFlags attachmentOnly = VK_IMAGE_USAGE_TRANSIENT_ATTACHMENT_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                             VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT;
    return on && (ci.usage & VK_IMAGE_USAGE_TRANSIENT_ATTACHMENT_BIT) && !(ci.usage & ~attachmentOnly) && ci.tiling == VK_IMAGE_TILING_OPTIMAL;
}

static void imageRequirements(KImage* img, VkMemoryRequirements* r) {
    if (memoryless(img->info)) {
        r->size = 4096;
        r->alignment = 4096;
        r->memoryTypeBits = 0x1;
        return;
    }
    r->size = img->sizeAlign.size;
    r->alignment = std::max<VkDeviceSize>(img->sizeAlign.align, 256);
    r->memoryTypeBits = img->info.tiling == VK_IMAGE_TILING_LINEAR ? 0x6 : 0x1;
}

VKFN(void) vkGetImageMemoryRequirements(VkDevice, VkImage image, VkMemoryRequirements* r) {
    KTRACE_FIRST();
    imageRequirements((KImage*)image, r);
}

VKFN(void) vkGetImageMemoryRequirements2(VkDevice, const VkImageMemoryRequirementsInfo2* info, VkMemoryRequirements2* r) {
    KTRACE_FIRST();
    auto* img = (KImage*)info->image;
    imageRequirements(img, &r->memoryRequirements);
    fillDedicated(r->pNext, img->sizeAlign.size >= (64u << 20));
}

VKFN(void) vkGetDeviceImageMemoryRequirements(VkDevice device, const VkDeviceImageMemoryRequirements* info, VkMemoryRequirements2* r) {
    VkImage tmp;
    vkCreateImage(device, info->pCreateInfo, nullptr, &tmp);
    imageRequirements((KImage*)tmp, &r->memoryRequirements);
    fillDedicated(r->pNext, false);
    kmtlDestroyImage((KImage*)tmp);
}

void kmtlZeroImage(KImage* img);

static VkResult bindImage(VkImage image, VkDeviceMemory memory, VkDeviceSize offset) {
    auto* img = (KImage*)image;
    auto* m = (KDeviceMemory*)memory;
    img->memory = m;
    if (memoryless(img->info)) {
        img->desc.storageMode = MTLStorageModeMemoryless;
        img->desc.usage = MTLTextureUsageRenderTarget;
        img->mtl = [img->device->mtl newTextureWithDescriptor:img->desc];
        if (img->mtl) {
            KLOG(1, "memoryless attachment %ux%u format %d samples %u", img->info.extent.width, img->info.extent.height,
                 img->format, img->info.samples);
            return VK_SUCCESS;
        }
        KERR("memoryless texture failed; placing it in memory");
        img->desc = nil;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    img->desc.storageMode = m->heap.storageMode;
    img->desc.cpuCacheMode = m->heap.cpuCacheMode;
    img->mtl = [m->heap newTextureWithDescriptor:img->desc offset:offset];
    if (!img->mtl) {
        KERR("vkBindImageMemory: texture placement failed (offset %llu, heap %lu)", (unsigned long long)offset,
             (unsigned long)m->heap.size);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    const KFormatInfo* fi = kmtlFormat(img->format);
    static const bool zero = kmtlEnvInt("KSA_MTL_ZERO_IMAGES", 1) != 0;
    if (zero && fi && !fi->depth && !fi->stencil && (img->mtl.usage & MTLTextureUsageRenderTarget) &&
        (img->info.usage & (VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT)))
        kmtlZeroImage(img);
    return VK_SUCCESS;
}

VKFN(VkResult) vkBindImageMemory(VkDevice, VkImage image, VkDeviceMemory memory, VkDeviceSize offset) {
    KTRACE_FIRST();
    return bindImage(image, memory, offset);
}

VKFN(VkResult) vkBindImageMemory2(VkDevice, uint32_t n, const VkBindImageMemoryInfo* infos) {
    for (uint32_t i = 0; i < n; i++) {
        VkResult r = bindImage(infos[i].image, infos[i].memory, infos[i].memoryOffset);
        if (r != VK_SUCCESS) return r;
    }
    return VK_SUCCESS;
}

VKFN(void) vkGetImageSubresourceLayout(VkDevice, VkImage image, const VkImageSubresource* sub, VkSubresourceLayout* l) {
    auto* img = (KImage*)image;
    const KFormatInfo* fi = kmtlFormat(img->format);
    uint32_t w = std::max(1u, img->info.extent.width >> sub->mipLevel);
    uint32_t h = std::max(1u, img->info.extent.height >> sub->mipLevel);
    l->offset = 0;
    l->rowPitch = (w + fi->blockW - 1) / fi->blockW * fi->blockBytes;
    l->depthPitch = l->rowPitch * ((h + fi->blockH - 1) / fi->blockH);
    l->arrayPitch = l->depthPitch;
    l->size = l->depthPitch;
}

KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h) {
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = format;
    ci.extent = {w, h, 1};
    ci.mipLevels = 1;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
        VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    VkImage handle;
    if (vkCreateImage((VkDevice)dev, &ci, nullptr, &handle) != VK_SUCCESS) return nullptr;
    auto* img = (KImage*)handle;
    img->mtl = [dev->mtl newTextureWithDescriptor:img->desc];
    img->ownsTexture = true;
    dev->addResident(img->mtl);
    return img;
}

// ---------------------------------------------------------------------------------------------------------------
// Image views

static MTLTextureSwizzle swz(VkComponentSwizzle s, MTLTextureSwizzle identity) {
    switch (s) {
    case VK_COMPONENT_SWIZZLE_ZERO: return MTLTextureSwizzleZero;
    case VK_COMPONENT_SWIZZLE_ONE: return MTLTextureSwizzleOne;
    case VK_COMPONENT_SWIZZLE_R: return MTLTextureSwizzleRed;
    case VK_COMPONENT_SWIZZLE_G: return MTLTextureSwizzleGreen;
    case VK_COMPONENT_SWIZZLE_B: return MTLTextureSwizzleBlue;
    case VK_COMPONENT_SWIZZLE_A: return MTLTextureSwizzleAlpha;
    default: return identity;
    }
}

VKFN(VkResult) vkCreateImageView(VkDevice device, const VkImageViewCreateInfo* ci, const VkAllocationCallbacks*, VkImageView* out) {
    KTRACE_FIRST();
    auto* img = (KImage*)ci->image;
    if (!img->mtl) {
        KERR("vkCreateImageView: image %p has no memory bound", img);
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    auto* v = new KImageView();
    v->device = (KDevice*)device;
    v->image = img;
    v->info = *ci;
    v->info.pNext = nullptr;
    v->format = ci->format;
    const VkImageSubresourceRange& r = ci->subresourceRange;
    v->baseLevel = r.baseMipLevel;
    v->levelCount = r.levelCount == VK_REMAINING_MIP_LEVELS ? img->info.mipLevels - r.baseMipLevel : r.levelCount;
    v->baseLayer = r.baseArrayLayer;
    v->layerCount = r.layerCount == VK_REMAINING_ARRAY_LAYERS ? img->info.arrayLayers - r.baseArrayLayer : r.layerCount;

    MTLPixelFormat pf = kmtlPixelFormat(ci->format);
    const KFormatInfo* fi = kmtlFormat(ci->format);
    // Aspect-only views of combined depth/stencil formats
    if (fi && fi->depth && fi->stencil) {
        if (r.aspectMask == VK_IMAGE_ASPECT_STENCIL_BIT) pf = MTLPixelFormatX32_Stencil8;
    }
    MTLTextureType tt = img->mtl.textureType;
    switch (ci->viewType) {
    case VK_IMAGE_VIEW_TYPE_1D: tt = MTLTextureType1D; break;
    case VK_IMAGE_VIEW_TYPE_1D_ARRAY: tt = MTLTextureType1DArray; break;
    case VK_IMAGE_VIEW_TYPE_2D: tt = img->info.samples > 1 ? MTLTextureType2DMultisample : MTLTextureType2D; break;
    case VK_IMAGE_VIEW_TYPE_2D_ARRAY: tt = img->info.samples > 1 ? MTLTextureType2DMultisampleArray : MTLTextureType2DArray; break;
    case VK_IMAGE_VIEW_TYPE_CUBE: tt = MTLTextureTypeCube; break;
    case VK_IMAGE_VIEW_TYPE_CUBE_ARRAY: tt = MTLTextureTypeCubeArray; break;
    case VK_IMAGE_VIEW_TYPE_3D: tt = MTLTextureType3D; break;
    default: break;
    }
    MTLTextureSwizzleChannels sw = MTLTextureSwizzleChannelsMake(
        swz(ci->components.r, MTLTextureSwizzleRed), swz(ci->components.g, MTLTextureSwizzleGreen),
        swz(ci->components.b, MTLTextureSwizzleBlue), swz(ci->components.a, MTLTextureSwizzleAlpha));
    bool identitySwizzle = sw.red == MTLTextureSwizzleRed && sw.green == MTLTextureSwizzleGreen &&
        sw.blue == MTLTextureSwizzleBlue && sw.alpha == MTLTextureSwizzleAlpha;
    bool whole = pf == img->mtl.pixelFormat && tt == img->mtl.textureType && v->baseLevel == 0 &&
        v->levelCount == img->mtl.mipmapLevelCount && v->baseLayer == 0 && v->layerCount == img->info.arrayLayers &&
        identitySwizzle;
    if (whole) {
        v->mtl = img->mtl;
    } else {
        NSRange levels = NSMakeRange(v->baseLevel, v->levelCount);
        NSRange slices = NSMakeRange(v->baseLayer, tt == MTLTextureType3D ? 1 : v->layerCount);
        if (identitySwizzle)
            v->mtl = [img->mtl newTextureViewWithPixelFormat:pf textureType:tt levels:levels slices:slices];
        else
            v->mtl = [img->mtl newTextureViewWithPixelFormat:pf textureType:tt levels:levels slices:slices swizzle:sw];
        if (!v->mtl) {
            KERR("vkCreateImageView: view failed (fmt %d type %d levels %u+%u layers %u+%u)", ci->format, ci->viewType,
                 v->baseLevel, v->levelCount, v->baseLayer, v->layerCount);
            delete v;
            return VK_ERROR_INITIALIZATION_FAILED;
        }
    }
    *out = (VkImageView)v;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyImageView(VkDevice, VkImageView view, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KImageView*)view;
}

// ---------------------------------------------------------------------------------------------------------------
// Samplers

static MTLSamplerAddressMode addressMode(VkSamplerAddressMode m) {
    switch (m) {
    case VK_SAMPLER_ADDRESS_MODE_REPEAT: return MTLSamplerAddressModeRepeat;
    case VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT: return MTLSamplerAddressModeMirrorRepeat;
    case VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE: return MTLSamplerAddressModeClampToEdge;
    case VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER: return MTLSamplerAddressModeClampToBorderColor;
    case VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE: return MTLSamplerAddressModeMirrorClampToEdge;
    default: return MTLSamplerAddressModeRepeat;
    }
}

MTLCompareFunction kmtlCompare(VkCompareOp op) {
    switch (op) {
    case VK_COMPARE_OP_NEVER: return MTLCompareFunctionNever;
    case VK_COMPARE_OP_LESS: return MTLCompareFunctionLess;
    case VK_COMPARE_OP_EQUAL: return MTLCompareFunctionEqual;
    case VK_COMPARE_OP_LESS_OR_EQUAL: return MTLCompareFunctionLessEqual;
    case VK_COMPARE_OP_GREATER: return MTLCompareFunctionGreater;
    case VK_COMPARE_OP_NOT_EQUAL: return MTLCompareFunctionNotEqual;
    case VK_COMPARE_OP_GREATER_OR_EQUAL: return MTLCompareFunctionGreaterEqual;
    default: return MTLCompareFunctionAlways;
    }
}

VKFN(VkResult) vkCreateSampler(VkDevice device, const VkSamplerCreateInfo* ci, const VkAllocationCallbacks*, VkSampler* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    MTLSamplerDescriptor* d = [MTLSamplerDescriptor new];
    d.minFilter = ci->minFilter == VK_FILTER_LINEAR ? MTLSamplerMinMagFilterLinear : MTLSamplerMinMagFilterNearest;
    d.magFilter = ci->magFilter == VK_FILTER_LINEAR ? MTLSamplerMinMagFilterLinear : MTLSamplerMinMagFilterNearest;
    d.mipFilter = ci->mipmapMode == VK_SAMPLER_MIPMAP_MODE_LINEAR ? MTLSamplerMipFilterLinear : MTLSamplerMipFilterNearest;
    d.sAddressMode = addressMode(ci->addressModeU);
    d.tAddressMode = addressMode(ci->addressModeV);
    d.rAddressMode = addressMode(ci->addressModeW);
    d.maxAnisotropy = ci->anisotropyEnable ? std::max(1, std::min(16, (int)ci->maxAnisotropy)) : 1;
    d.lodMinClamp = ci->minLod;
    d.lodMaxClamp = ci->maxLod;
    d.normalizedCoordinates = !ci->unnormalizedCoordinates;
    if (ci->compareEnable) d.compareFunction = kmtlCompare(ci->compareOp);
    switch (ci->borderColor) {
    case VK_BORDER_COLOR_FLOAT_OPAQUE_BLACK: case VK_BORDER_COLOR_INT_OPAQUE_BLACK:
        d.borderColor = MTLSamplerBorderColorOpaqueBlack; break;
    case VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE: case VK_BORDER_COLOR_INT_OPAQUE_WHITE:
        d.borderColor = MTLSamplerBorderColorOpaqueWhite; break;
    default: d.borderColor = MTLSamplerBorderColorTransparentBlack; break;
    }
    // S4.6 precision, [-16, 15.999] (macOS 26+)
    if (ci->mipLodBias != 0.0f) d.lodBias = std::max(-16.0f, std::min(15.99f, ci->mipLodBias));
    if (findNext(ci->pNext, VK_STRUCTURE_TYPE_SAMPLER_REDUCTION_MODE_CREATE_INFO))
        KLOG(1, "sampler reduction mode ignored");
    d.supportArgumentBuffers = YES;
    auto* s = new KSampler();
    s->mtl = [dev->mtl newSamplerStateWithDescriptor:d];
    if (!s->mtl) {
        delete s;
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    *out = (VkSampler)s;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySampler(VkDevice, VkSampler sampler, const VkAllocationCallbacks*) {
    delete (KSampler*)sampler;
}

// ---------------------------------------------------------------------------------------------------------------
// Query pools and pipeline caches (KSA creates query pools but never uses them; caches are kept empty)

VKFN(VkResult) vkCreateQueryPool(VkDevice, const VkQueryPoolCreateInfo* ci, const VkAllocationCallbacks*, VkQueryPool* out) {
    KTRACE_FIRST();
    auto* p = new KQueryPool();
    p->type = ci->queryType;
    p->count = ci->queryCount;
    p->values.assign(ci->queryCount, 0);
    p->available.assign(ci->queryCount, 0);
    if (ci->queryType != VK_QUERY_TYPE_TIMESTAMP) KLOG(1, "query pool type %d: only timestamps are implemented", ci->queryType);
    *out = (VkQueryPool)p;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyQueryPool(VkDevice, VkQueryPool p, const VkAllocationCallbacks*) { delete (KQueryPool*)p; }

VKFN(void) vkResetQueryPool(VkDevice, VkQueryPool pool, uint32_t first, uint32_t n) { ((KQueryPool*)pool)->reset(first, n); }

VKFN(VkResult) vkGetQueryPoolResults(VkDevice, VkQueryPool pool, uint32_t first, uint32_t count, size_t dataSize, void* data,
                                     VkDeviceSize stride, VkQueryResultFlags flags) {
    KTRACE_FIRST();
    auto* p = (KQueryPool*)pool;
    bool is64 = flags & VK_QUERY_RESULT_64_BIT, withAvail = flags & VK_QUERY_RESULT_WITH_AVAILABILITY_BIT;
    VkResult res = VK_SUCCESS;
    std::unique_lock<std::mutex> lock(p->m);
    for (uint32_t i = 0; i < count && first + i < p->count; i++) {
        uint32_t q = first + i;
        if ((flags & VK_QUERY_RESULT_WAIT_BIT) && !p->available[q])
            p->cv.wait_for(lock, std::chrono::seconds(2), [&] { return p->available[q] != 0; });
        uint8_t* dst = (uint8_t*)data + (size_t)i * stride;
        if ((size_t)i * stride + (is64 ? 8 : 4) * (withAvail ? 2 : 1) > dataSize) break;
        bool avail = p->available[q];
        if (!avail) res = VK_NOT_READY;
        if (avail || (flags & VK_QUERY_RESULT_PARTIAL_BIT)) {
            uint64_t v = avail ? p->values[q] : 0;
            if (is64) memcpy(dst, &v, 8); else { uint32_t v32 = (uint32_t)v; memcpy(dst, &v32, 4); }
        }
        if (withAvail) {
            uint64_t a = avail;
            if (is64) memcpy(dst + 8, &a, 8); else { uint32_t a32 = avail; memcpy(dst + 4, &a32, 4); }
        }
    }
    return res;
}

VKFN(VkResult) vkCreatePipelineCache(VkDevice, const VkPipelineCacheCreateInfo*, const VkAllocationCallbacks*, VkPipelineCache* out) {
    KTRACE_FIRST();
    *out = (VkPipelineCache) new KPipelineCache();
    return VK_SUCCESS;
}

VKFN(void) vkDestroyPipelineCache(VkDevice, VkPipelineCache c, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KPipelineCache*)c;
}

VKFN(VkResult) vkGetPipelineCacheData(VkDevice, VkPipelineCache, size_t* size, void*) {
    *size = 0;
    return VK_SUCCESS;
}

VKFN(VkResult) vkMergePipelineCaches(VkDevice, VkPipelineCache, uint32_t, const VkPipelineCache*) { return VK_SUCCESS; }

// ---------------------------------------------------------------------------------------------------------------
// Debug utils names (not advertised; harmless if called)

VKFN(VkResult) vkSetDebugUtilsObjectNameEXT(VkDevice, const VkDebugUtilsObjectNameInfoEXT*) { return VK_SUCCESS; }

// ---------------------------------------------------------------------------------------------------------------
// Buffer views (texel buffers): Metal texture buffers over the buffer's memory

struct KBufferView {
    id<MTLTexture> mtl = nil;
};

VKFN(VkResult) vkCreateBufferView(VkDevice, const VkBufferViewCreateInfo* ci, const VkAllocationCallbacks*, VkBufferView* out) {
    KTRACE_FIRST();
    auto* b = (KBuffer*)ci->buffer;
    const KFormatInfo* fi = kmtlFormat(ci->format);
    if (!fi || !b->mtl) return VK_ERROR_FORMAT_NOT_SUPPORTED;
    VkDeviceSize range = ci->range == VK_WHOLE_SIZE ? b->size - ci->offset : ci->range;
    NSUInteger texels = (NSUInteger)(range / fi->blockBytes);
    MTLTextureDescriptor* d = [MTLTextureDescriptor textureBufferDescriptorWithPixelFormat:fi->mtl width:texels
        resourceOptions:b->mtl.resourceOptions usage:MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite];
    auto* v = new KBufferView();
    v->mtl = [b->mtl newTextureWithDescriptor:d offset:b->offset + ci->offset bytesPerRow:texels * fi->blockBytes];
    if (!v->mtl) {
        delete v;
        return VK_ERROR_INITIALIZATION_FAILED;
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
