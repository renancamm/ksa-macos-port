// Device and queue, residency, synchronization, submission (resources/ has the memory and the objects in it).
#include "kmtl.h"

std::atomic<int64_t> g_allocatedBytes{0};
std::atomic<int64_t> g_allocatedByType[3] = {{0}, {0}, {0}};
std::atomic<int64_t> g_internalBytes{0};  // descriptor pool chunks and upload rings

// ---------------------------------------------------------------------------------------------------------------
// Residency: every heap and standalone texture lives in one residency set attached to the queue, so resources
// reached only through argument buffers are resident without per-encoder useResource calls.

// KSA_MTL_STALLS: what changed in the set since its last commit, and on which threads, for the log of a slow commit
static int g_resAdds, g_resRemoves; static int64_t g_resAddBytes, g_resRemoveBytes; static std::string g_resThreads;
static void noteChange(bool add, id<MTLAllocation> a) {
    if (kmtlConfig().stalls <= 0) return;
    (add ? g_resAdds : g_resRemoves)++;
    (add ? g_resAddBytes : g_resRemoveBytes) += (int64_t)a.allocatedSize;
    char name[64] = "";
    pthread_getname_np(pthread_self(), name, sizeof name);
    if (!name[0]) snprintf(name, sizeof name, "%p", (void*)pthread_self());
    if (g_resThreads.find(name) == std::string::npos && g_resThreads.size() < 200) g_resThreads += std::string(name) + " ";
}

void KDevice::addResident(id<MTLAllocation> a) {
    std::lock_guard<std::mutex> lock(residencyMutex);
    [residency addAllocation:a];
    residencyDirty = true;
    noteChange(true, a);
}

void KDevice::removeResident(id<MTLAllocation> a) {
    std::lock_guard<std::mutex> lock(residencyMutex);
    [residency removeAllocation:a];
    residencyDirty = true;
    noteChange(false, a);
}

void KDevice::commitResidency() {
    std::lock_guard<std::mutex> lock(residencyMutex);
    if (residencyDirty) {
        double t0 = CACurrentMediaTime();
        [residency commit];
        double ms = (CACurrentMediaTime() - t0) * 1000.0;
        if (kmtlDrainOn()) kmtlDrainWait("residency commit", t0, ms);
        if (kmtlConfig().stalls > 0 && ms >= kmtlConfig().stalls) {
            char name[64] = "";
            pthread_getname_np(pthread_self(), name, sizeof name);
            KLOG(0, "stall: residency commit %.1f ms on '%s': +%d (%lld MB) -%d (%lld MB), set %llu allocations; changed on %s",
                 ms, name, g_resAdds, (long long)(g_resAddBytes >> 20), g_resRemoves, (long long)(g_resRemoveBytes >> 20),
                 (unsigned long long)residency.allocationCount, g_resThreads.c_str());
        }
        g_resAdds = g_resRemoves = 0; g_resAddBytes = g_resRemoveBytes = 0; g_resThreads.clear();
        residencyDirty = false;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Device and queue

// A queue and its chain fence, updated once so the first real encoder has something to wait on; null (logged) when
// Metal makes either one not
static KQueue* makeQueue(KDevice* dev, uint32_t family, NSUInteger maxBuffers, NSString* label, NSString* chainLabel) {
    auto* q = new KQueue();
    q->device = dev;
    q->family = family;
    q->mtl = [dev->mtl newCommandQueueWithMaxCommandBufferCount:maxBuffers];
    q->chain = [dev->mtl newFence];
    if (!q->mtl || !q->chain) {
        KERR("vkCreateDevice: %s failed", label.UTF8String);
        delete q;
        return nullptr;
    }
    q->mtl.label = label;
    [q->mtl addResidencySet:dev->residency];
    q->chain.label = chainLabel;
    id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b updateFence:q->chain];
    [b endEncoding];
    [cb commit];
    return q;
}

VKFN(VkResult) vkCreateDevice(VkPhysicalDevice physicalDevice, const VkDeviceCreateInfo* ci, const VkAllocationCallbacks*, VkDevice* out) {
    KTRACE_FIRST();
    auto* pd = (KPhysicalDevice*)physicalDevice;
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) KLOG(1, "device extension: %s", ci->ppEnabledExtensionNames[i]);
    auto* dev = new KDevice();
    dev->physical = pd;
    dev->mtl = pd->mtl;
    NSError* err = nil;
    MTLResidencySetDescriptor* rd = [MTLResidencySetDescriptor new];
    rd.label = @"KSA Metal";
    rd.initialCapacity = 1024;
    dev->residency = [dev->mtl newResidencySetWithDescriptor:rd error:&err];
    if (!dev->residency) {
        KERR("vkCreateDevice: residency set failed: %s", err.description.UTF8String);
        delete dev;
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    dev->timestampScratch = [dev->mtl newBufferWithLength:256 options:MTLResourceStorageModePrivate];
    if (!dev->timestampScratch) {
        KERR("vkCreateDevice: timestamp scratch buffer failed");
        delete dev;
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    dev->queue = makeQueue(dev, 0, 256, @"KSA Metal queue", @"KSA Metal encoder chain");
    // A transfer-only queue family: KSA's texture streamer then uploads on its own queue
    dev->transferQueue = dev->queue ? makeQueue(dev, 1, 64, @"KSA Metal transfer queue", @"KSA Metal transfer chain") : nullptr;
    if (!dev->transferQueue) {
        delete dev->queue;
        delete dev;
        return VK_ERROR_INITIALIZATION_FAILED;
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
        kmtlNoteGpuError();
        KERR_LIMIT(20, "command buffer failed: %s", cb.error.description.UTF8String);
        // With KSA_MTL_GPU_ERRORS=1, which encoders ran, faulted or never started
        NSArray* infos = cb.error.userInfo[MTLCommandBufferEncoderInfoErrorKey];
        for (id<MTLCommandBufferEncoderInfo> info in infos) {
            const char* state = info.errorState == MTLCommandEncoderErrorStateCompleted ? "completed"
                : info.errorState == MTLCommandEncoderErrorStateFaulted ? "FAULTED"
                : info.errorState == MTLCommandEncoderErrorStateAffected ? "affected"
                : info.errorState == MTLCommandEncoderErrorStatePending ? "pending" : "unknown";
            KERR_LIMIT(400, "  encoder %s: %s", state, info.label.UTF8String ?: "(no label)");
        }
    }
}

// Handles in an array: bare (VkSubmitInfo) or a field of each element (VkSubmitInfo2's VkSemaphoreSubmitInfo and
// VkCommandBufferSubmitInfo), so one submission path serves both
struct KHandles {
    const void* first = nullptr;
    size_t stride = 0;
    uint32_t count = 0;
    template <typename T> T at(uint32_t i) const { return *(const T*)((const uint8_t*)first + i * stride); }
};
template <typename T> static KHandles handles(const T* p, uint32_t n) { return {p, sizeof(T), n}; }
template <typename S, typename T> static KHandles handles(const S* p, uint32_t n, T S::*field) {
    return {n ? (const void*)&(p->*field) : nullptr, sizeof(S), n};
}

// Waits on semaphores another queue signals: a command buffer that waits for the events, then advances this queue's
// chain fence, which the following command buffers' first encoders wait on
static void waitSemaphores(KQueue* q, KHandles sems, std::vector<id<MTLCommandBuffer>>& toCommit) {
    id<MTLCommandBuffer> w = nil;
    for (uint32_t i = 0; i < sems.count; i++) {
        auto* s = (KSemaphore*)sems.at<VkSemaphore>(i);
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
static void signalSemaphores(KQueue* q, KHandles sems, std::vector<id<MTLCommandBuffer>>& toCommit) {
    for (uint32_t i = 0; i < sems.count; i++) {
        auto* s = (KSemaphore*)sems.at<VkSemaphore>(i);
        if (!s) continue;
        if (!s->event) s->event = [q->device->mtl newSharedEvent];
        if (toCommit.empty()) toCommit.push_back([q->mtl commandBuffer]);
        [toCommit.back() encodeSignalEvent:s->event value:++s->value];
        s->signaledBy = q;
    }
}

// The submission: handlesOf(s, &waits, &commandBuffers, &signals) gives submission s's handles
template <typename F> static VkResult submit(VkQueue queue, uint32_t submitCount, VkFence fence, F handlesOf) {
    auto* q = (KQueue*)queue;
    q->device->commitResidency();
    std::unique_lock<std::mutex> lock(q->submitMutex, std::defer_lock);
    {
        KStallTimer stall("submit lock");
        lock.lock();
    }
    std::vector<id<MTLCommandBuffer>> toCommit;
    for (uint32_t s = 0; s < submitCount; s++) {
        KHandles waits, cbs, signals;
        handlesOf(s, &waits, &cbs, &signals);
        waitSemaphores(q, waits, toCommit);
        for (uint32_t i = 0; i < cbs.count; i++) {
            auto* cb = (KCommandBuffer*)cbs.at<VkCommandBuffer>(i);
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
        signalSemaphores(q, signals, toCommit);
    }
    if (fence) {
        // Encoders are chained by the queue fence, so the last command buffer completing means all did
        auto* f = (KFence*)fence;
        if (toCommit.empty()) toCommit.push_back([q->mtl commandBuffer]);
        [toCommit.back() addCompletedHandler:^(id<MTLCommandBuffer>) { signalFence(f); }];
    }
    if (kmtlConfig().stalls > 0) {
        // KSA_MTL_STALLS: command buffers that started on the GPU long after they were committed
        double now = CACurrentMediaTime();
        uint32_t family = q->family;
        for (id<MTLCommandBuffer> cb : toCommit) {
            NSString* label = cb.label;
            [cb addCompletedHandler:^(id<MTLCommandBuffer> c) {
                double delay = (c.GPUStartTime - now) * 1000.0, run = (c.GPUEndTime - c.GPUStartTime) * 1000.0;
                if (c.GPUStartTime > 0 && delay >= 2 * kmtlConfig().stalls)
                    KLOG(0, "stall: gpu start %.1f ms after commit (ran %.1f ms) queue %u '%s' at %.3f present %llu", delay,
                         run, family, label ? label.UTF8String : "", now, (unsigned long long)kmtlPresentCount());
            }];
        }
    }
    {
        KStallTimer stall("commit");
        for (id<MTLCommandBuffer> cb : toCommit) [cb commit];
    }
    return VK_SUCCESS;
}

VKFN(VkResult) vkQueueSubmit(VkQueue queue, uint32_t submitCount, const VkSubmitInfo* submits, VkFence fence) {
    KTRACE_FIRST();
    KStallTimer stall("vkQueueSubmit");
    return submit(queue, submitCount, fence, [&](uint32_t s, KHandles* waits, KHandles* cbs, KHandles* signals) {
        const VkSubmitInfo& si = submits[s];
        *waits = handles(si.pWaitSemaphores, si.waitSemaphoreCount);
        *cbs = handles(si.pCommandBuffers, si.commandBufferCount);
        *signals = handles(si.pSignalSemaphores, si.signalSemaphoreCount);
    });
}

VKFN(VkResult) vkQueueSubmit2(VkQueue queue, uint32_t submitCount, const VkSubmitInfo2* submits, VkFence fence) {
    KTRACE_FIRST();
    KStallTimer stall("vkQueueSubmit2");
    return submit(queue, submitCount, fence, [&](uint32_t s, KHandles* waits, KHandles* cbs, KHandles* signals) {
        const VkSubmitInfo2& si = submits[s];
        *waits = handles(si.pWaitSemaphoreInfos, si.waitSemaphoreInfoCount, &VkSemaphoreSubmitInfo::semaphore);
        *cbs = handles(si.pCommandBufferInfos, si.commandBufferInfoCount, &VkCommandBufferSubmitInfo::commandBuffer);
        *signals = handles(si.pSignalSemaphoreInfos, si.signalSemaphoreInfoCount, &VkSemaphoreSubmitInfo::semaphore);
    });
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
    KStallTimer stall("vkWaitForFences");
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
