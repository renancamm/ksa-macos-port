// Query pools, pipeline caches and debug names.
#include "kmtl.h"

// ---------------------------------------------------------------------------------------------------------------
// Query pools and pipeline caches (KSA writes timestamps only while its profiler is open, commands/transfers.mm; caches are kept
// empty)

VKFN(VkResult) vkCreateQueryPool(VkDevice, const VkQueryPoolCreateInfo* ci, const VkAllocationCallbacks*, VkQueryPool* out) {
    KTRACE_FIRST();
    auto* p = new KQueryPool();
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
