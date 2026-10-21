// Name table behind vkGetInstanceProcAddr / vkGetDeviceProcAddr. Unknown names return NULL so gaps fail loudly.
#include "kmtl.h"

struct KEntry {
    const char* name;
    PFN_vkVoidFunction fn;
    int level;  // 0 global, 1 instance, 2 device
};

#define G(n) {#n, (PFN_vkVoidFunction)n, 0}
#define I(n) {#n, (PFN_vkVoidFunction)n, 1}
#define D(n) {#n, (PFN_vkVoidFunction)n, 2}
#define IA(alias, n) {#alias, (PFN_vkVoidFunction)n, 1}
#define DA(alias, n) {#alias, (PFN_vkVoidFunction)n, 2}

VKFN(PFN_vkVoidFunction) vkGetInstanceProcAddr(VkInstance instance, const char* name);
VKFN(PFN_vkVoidFunction) vkGetDeviceProcAddr(VkDevice device, const char* name);

static const KEntry kEntries[] = {
    G(vkGetInstanceProcAddr),
    G(vkEnumerateInstanceExtensionProperties),
    G(vkEnumerateInstanceLayerProperties),
    G(vkEnumerateInstanceVersion),
    G(vkCreateInstance),

    I(vkDestroyInstance),
    I(vkEnumeratePhysicalDevices),
    I(vkEnumerateDeviceExtensionProperties),
    I(vkEnumerateDeviceLayerProperties),
    I(vkGetPhysicalDeviceProperties),
    I(vkGetPhysicalDeviceProperties2),
    IA(vkGetPhysicalDeviceProperties2KHR, vkGetPhysicalDeviceProperties2),
    I(vkGetPhysicalDeviceFeatures),
    I(vkGetPhysicalDeviceFeatures2),
    IA(vkGetPhysicalDeviceFeatures2KHR, vkGetPhysicalDeviceFeatures2),
    I(vkGetPhysicalDeviceQueueFamilyProperties),
    I(vkGetPhysicalDeviceQueueFamilyProperties2),
    IA(vkGetPhysicalDeviceQueueFamilyProperties2KHR, vkGetPhysicalDeviceQueueFamilyProperties2),
    I(vkGetPhysicalDeviceMemoryProperties),
    I(vkGetPhysicalDeviceMemoryProperties2),
    IA(vkGetPhysicalDeviceMemoryProperties2KHR, vkGetPhysicalDeviceMemoryProperties2),
    I(vkGetPhysicalDeviceFormatProperties),
    I(vkGetPhysicalDeviceFormatProperties2),
    IA(vkGetPhysicalDeviceFormatProperties2KHR, vkGetPhysicalDeviceFormatProperties2),
    I(vkGetPhysicalDeviceImageFormatProperties),
    I(vkGetPhysicalDeviceImageFormatProperties2),
    IA(vkGetPhysicalDeviceImageFormatProperties2KHR, vkGetPhysicalDeviceImageFormatProperties2),
    I(vkCreateDevice),
    I(vkCreateMetalSurfaceEXT),
    I(vkDestroySurfaceKHR),
    I(vkGetPhysicalDeviceSurfaceSupportKHR),
    I(vkGetPhysicalDeviceSurfaceCapabilitiesKHR),
    I(vkGetPhysicalDeviceSurfaceCapabilities2KHR),
    I(vkGetPhysicalDeviceSurfaceFormatsKHR),
    I(vkGetPhysicalDeviceSurfaceFormats2KHR),
    I(vkGetPhysicalDeviceSurfacePresentModesKHR),

    D(vkGetDeviceProcAddr),
    D(vkDestroyDevice),
    D(vkGetDeviceQueue),
    D(vkGetDeviceQueue2),
    D(vkDeviceWaitIdle),
    D(vkQueueWaitIdle),
    D(vkQueueSubmit),
    D(vkQueueSubmit2),
    DA(vkQueueSubmit2KHR, vkQueueSubmit2),
    D(vkCreateFence),
    D(vkDestroyFence),
    D(vkResetFences),
    D(vkGetFenceStatus),
    D(vkWaitForFences),
    D(vkCreateSemaphore),
    D(vkDestroySemaphore),
    D(vkAllocateMemory),
    D(vkFreeMemory),
    D(vkMapMemory),
    D(vkUnmapMemory),
    D(vkFlushMappedMemoryRanges),
    D(vkInvalidateMappedMemoryRanges),
    D(vkGetDeviceMemoryCommitment),
    D(vkCreateBuffer),
    D(vkDestroyBuffer),
    D(vkGetBufferMemoryRequirements),
    D(vkGetBufferMemoryRequirements2),
    DA(vkGetBufferMemoryRequirements2KHR, vkGetBufferMemoryRequirements2),
    D(vkGetDeviceBufferMemoryRequirements),
    DA(vkGetDeviceBufferMemoryRequirementsKHR, vkGetDeviceBufferMemoryRequirements),
    D(vkBindBufferMemory),
    D(vkBindBufferMemory2),
    DA(vkBindBufferMemory2KHR, vkBindBufferMemory2),
    D(vkGetBufferDeviceAddress),
    DA(vkGetBufferDeviceAddressKHR, vkGetBufferDeviceAddress),
    DA(vkGetBufferDeviceAddressEXT, vkGetBufferDeviceAddress),
    D(vkCreateImage),
    D(vkDestroyImage),
    D(vkGetImageMemoryRequirements),
    D(vkGetImageMemoryRequirements2),
    DA(vkGetImageMemoryRequirements2KHR, vkGetImageMemoryRequirements2),
    D(vkGetDeviceImageMemoryRequirements),
    DA(vkGetDeviceImageMemoryRequirementsKHR, vkGetDeviceImageMemoryRequirements),
    D(vkBindImageMemory),
    D(vkBindImageMemory2),
    DA(vkBindImageMemory2KHR, vkBindImageMemory2),
    D(vkGetImageSubresourceLayout),
    D(vkCreateBufferView),
    D(vkDestroyBufferView),
    D(vkCreateImageView),
    D(vkDestroyImageView),
    D(vkCreateSampler),
    D(vkDestroySampler),
    D(vkCreateQueryPool),
    D(vkDestroyQueryPool),
    D(vkResetQueryPool),
    DA(vkResetQueryPoolEXT, vkResetQueryPool),
    D(vkGetQueryPoolResults),
    D(vkCreatePipelineCache),
    D(vkDestroyPipelineCache),
    D(vkGetPipelineCacheData),
    D(vkMergePipelineCaches),
    D(vkCreateDescriptorSetLayout),
    D(vkDestroyDescriptorSetLayout),
    D(vkGetDescriptorSetLayoutSupport),
    D(vkCreateDescriptorPool),
    D(vkDestroyDescriptorPool),
    D(vkResetDescriptorPool),
    D(vkAllocateDescriptorSets),
    D(vkFreeDescriptorSets),
    D(vkUpdateDescriptorSets),
    D(vkCreatePipelineLayout),
    D(vkDestroyPipelineLayout),
    D(vkCreateShaderModule),
    D(vkDestroyShaderModule),
    D(vkCreateRenderPass),
    D(vkCreateRenderPass2),
    DA(vkCreateRenderPass2KHR, vkCreateRenderPass2),
    D(vkDestroyRenderPass),
    D(vkCreateFramebuffer),
    D(vkDestroyFramebuffer),
    D(vkGetRenderAreaGranularity),
    D(vkCreateGraphicsPipelines),
    D(vkCreateComputePipelines),
    D(vkDestroyPipeline),
    D(vkCreateCommandPool),
    D(vkDestroyCommandPool),
    D(vkResetCommandPool),
    D(vkTrimCommandPool),
    D(vkAllocateCommandBuffers),
    D(vkFreeCommandBuffers),
    D(vkResetCommandBuffer),
    D(vkBeginCommandBuffer),
    D(vkEndCommandBuffer),
    D(vkCmdBindPipeline),
    D(vkCmdBindDescriptorSets),
    D(vkCmdPushConstants),
    D(vkCmdBindVertexBuffers),
    D(vkCmdBindVertexBuffers2),
    D(vkCmdBindIndexBuffer),
    D(vkCmdSetViewport),
    D(vkCmdSetScissor),
    D(vkCmdSetDepthBias),
    D(vkCmdSetBlendConstants),
    D(vkCmdSetStencilReference),
    D(vkCmdDraw),
    D(vkCmdDrawIndexed),
    D(vkCmdDrawIndirect),
    D(vkCmdDrawIndexedIndirect),
    D(vkCmdDispatch),
    D(vkCmdDispatchIndirect),
    D(vkCmdDispatchBase),
    D(vkCmdPipelineBarrier),
    D(vkCmdPipelineBarrier2),
    DA(vkCmdPipelineBarrier2KHR, vkCmdPipelineBarrier2),
    D(vkCmdBeginRenderPass),
    D(vkCmdBeginRenderPass2),
    D(vkCmdNextSubpass),
    D(vkCmdEndRenderPass),
    D(vkCmdEndRenderPass2),
    D(vkCmdBeginRendering),
    DA(vkCmdBeginRenderingKHR, vkCmdBeginRendering),
    D(vkCmdEndRendering),
    DA(vkCmdEndRenderingKHR, vkCmdEndRendering),
    D(vkCmdCopyBuffer),
    D(vkCmdCopyBuffer2),
    D(vkCmdCopyBufferToImage),
    D(vkCmdCopyImageToBuffer),
    D(vkCmdCopyImage),
    D(vkCmdBlitImage),
    D(vkCmdFillBuffer),
    D(vkCmdUpdateBuffer),
    D(vkCmdClearColorImage),
    D(vkCmdClearDepthStencilImage),
    D(vkCmdClearAttachments),
    D(vkCmdResolveImage),
    D(vkCmdResetQueryPool),
    D(vkCmdWriteTimestamp),
    D(vkCmdWriteTimestamp2),
    D(vkCmdBeginDebugUtilsLabelEXT),
    D(vkCmdEndDebugUtilsLabelEXT),
    D(vkCmdInsertDebugUtilsLabelEXT),
    D(vkSetDebugUtilsObjectNameEXT),
    D(vkGetAccelerationStructureBuildSizesKHR),
    D(vkCreateAccelerationStructureKHR),
    D(vkDestroyAccelerationStructureKHR),
    D(vkGetAccelerationStructureDeviceAddressKHR),
    D(vkCmdBuildAccelerationStructuresKHR),
    D(vkCreateSwapchainKHR),
    D(vkDestroySwapchainKHR),
    D(vkGetSwapchainImagesKHR),
    D(vkAcquireNextImageKHR),
    D(vkAcquireNextImage2KHR),
    D(vkQueuePresentKHR),
};

static const KEntry* findEntry(const char* name) {
    static std::unordered_map<std::string, const KEntry*> map = [] {
        std::unordered_map<std::string, const KEntry*> m;
        for (auto& e : kEntries) m[e.name] = &e;
        return m;
    }();
    auto it = map.find(name);
    if (it == map.end()) {
        KLOG(2, "proc addr miss: %s", name);
        return nullptr;
    }
    return it->second;
}

PFN_vkVoidFunction kmtlLookup(const char* name, int maxLevel) {
    const KEntry* e = findEntry(name);
    return e && e->level <= maxLevel ? e->fn : nullptr;
}

VKFN(PFN_vkVoidFunction) vkGetInstanceProcAddr(VkInstance instance, const char* name) {
    if (!name) return nullptr;
    // Without an instance only global functions resolve (plus vkGetInstanceProcAddr itself)
    return kmtlLookup(name, instance ? 2 : 0);
}

VKFN(PFN_vkVoidFunction) vkGetDeviceProcAddr(VkDevice, const char* name) {
    if (!name) return nullptr;
    // Device-level queries return only device-level functions
    const KEntry* e = findEntry(name);
    return e && e->level == 2 ? e->fn : nullptr;
}
