// Instance, physical device queries, surface queries.
#include "kmtl.h"
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <pthread.h>

int g_logLevel = 0;
static FILE* g_logFile = nullptr;

__attribute__((constructor)) static void kmtlInitLogging() {
    g_logLevel = kmtlConfig().logLevel;
    const char* path = kmtlConfig().logFile;
    g_logFile = path ? fopen(path, "w") : nullptr;
    if (!g_logFile) g_logFile = stdout;
    setvbuf(g_logFile, nullptr, _IOLBF, 0);
}

void kmtlLog(int level, const char* fmt, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    static std::mutex m;
    std::lock_guard<std::mutex> lock(m);
    fprintf(g_logFile, "[ksa-metal%s] %s\n", level < 0 ? " ERROR" : "", buf);
}

// ---------------------------------------------------------------------------------------------------------------
// Global functions

static const VkExtensionProperties kInstanceExtensions[] = {
    {VK_KHR_SURFACE_EXTENSION_NAME, VK_KHR_SURFACE_SPEC_VERSION},
    {VK_EXT_METAL_SURFACE_EXTENSION_NAME, VK_EXT_METAL_SURFACE_SPEC_VERSION},
    {VK_KHR_PORTABILITY_ENUMERATION_EXTENSION_NAME, VK_KHR_PORTABILITY_ENUMERATION_SPEC_VERSION},
    {VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_EXTENSION_NAME, VK_KHR_GET_PHYSICAL_DEVICE_PROPERTIES_2_SPEC_VERSION},
    {VK_KHR_GET_SURFACE_CAPABILITIES_2_EXTENSION_NAME, VK_KHR_GET_SURFACE_CAPABILITIES_2_SPEC_VERSION},
};

static const VkExtensionProperties kDeviceExtensions[] = {
    {VK_KHR_SWAPCHAIN_EXTENSION_NAME, VK_KHR_SWAPCHAIN_SPEC_VERSION},
    {VK_EXT_MEMORY_BUDGET_EXTENSION_NAME, VK_EXT_MEMORY_BUDGET_SPEC_VERSION},
    {VK_KHR_MULTIVIEW_EXTENSION_NAME, VK_KHR_MULTIVIEW_SPEC_VERSION},
    {VK_KHR_SYNCHRONIZATION_2_EXTENSION_NAME, VK_KHR_SYNCHRONIZATION_2_SPEC_VERSION},
    {VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME, VK_KHR_DYNAMIC_RENDERING_SPEC_VERSION},
    {VK_EXT_DESCRIPTOR_INDEXING_EXTENSION_NAME, VK_EXT_DESCRIPTOR_INDEXING_SPEC_VERSION},
    {VK_KHR_BUFFER_DEVICE_ADDRESS_EXTENSION_NAME, VK_KHR_BUFFER_DEVICE_ADDRESS_SPEC_VERSION},
    {VK_KHR_SHADER_DRAW_PARAMETERS_EXTENSION_NAME, VK_KHR_SHADER_DRAW_PARAMETERS_SPEC_VERSION},
    {VK_KHR_MAINTENANCE_4_EXTENSION_NAME, VK_KHR_MAINTENANCE_4_SPEC_VERSION},
    {VK_KHR_SPIRV_1_4_EXTENSION_NAME, VK_KHR_SPIRV_1_4_SPEC_VERSION},
    {VK_KHR_SHADER_FLOAT_CONTROLS_EXTENSION_NAME, VK_KHR_SHADER_FLOAT_CONTROLS_SPEC_VERSION},
    {VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME, VK_KHR_DEDICATED_ALLOCATION_SPEC_VERSION},
    {VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME, VK_KHR_GET_MEMORY_REQUIREMENTS_2_SPEC_VERSION},
    {VK_KHR_BIND_MEMORY_2_EXTENSION_NAME, VK_KHR_BIND_MEMORY_2_SPEC_VERSION},
    {VK_KHR_PRESENT_ID_EXTENSION_NAME, VK_KHR_PRESENT_ID_SPEC_VERSION},
    {VK_KHR_PRESENT_WAIT_EXTENSION_NAME, VK_KHR_PRESENT_WAIT_SPEC_VERSION},
};

// Ray query (acceleration structures and ray queries; KSA_MTL_RAYQUERY=0 hides them, the game then uses its software
// BVH for grass placement) and ray tracing pipelines are advertised by the configuration, as is tessellation
static const VkExtensionProperties kRayQueryExtensions[] = {
    {VK_KHR_ACCELERATION_STRUCTURE_EXTENSION_NAME, VK_KHR_ACCELERATION_STRUCTURE_SPEC_VERSION},
    {VK_KHR_RAY_QUERY_EXTENSION_NAME, VK_KHR_RAY_QUERY_SPEC_VERSION},
    {VK_KHR_DEFERRED_HOST_OPERATIONS_EXTENSION_NAME, VK_KHR_DEFERRED_HOST_OPERATIONS_SPEC_VERSION},
};

static VkResult fillExtensions(const VkExtensionProperties* src, uint32_t n, uint32_t* count, VkExtensionProperties* out) {
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    memcpy(out, src, c * sizeof(*out));
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
}

VKFN(VkResult) vkEnumerateInstanceExtensionProperties(const char* layer, uint32_t* count, VkExtensionProperties* props) {
    KTRACE_FIRST();
    if (layer) { *count = 0; return VK_ERROR_LAYER_NOT_PRESENT; }
    return fillExtensions(kInstanceExtensions, sizeof(kInstanceExtensions) / sizeof(kInstanceExtensions[0]), count, props);
}

VKFN(VkResult) vkEnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties*) {
    KTRACE_FIRST();
    *count = 0;
    return VK_SUCCESS;
}

VKFN(VkResult) vkEnumerateInstanceVersion(uint32_t* v) {
    KTRACE_FIRST();
    *v = VK_API_VERSION_1_3;
    return VK_SUCCESS;
}

VKFN(VkResult) vkCreateInstance(const VkInstanceCreateInfo* ci, const VkAllocationCallbacks*, VkInstance* out) {
    KTRACE_FIRST();
    for (uint32_t i = 0; i < ci->enabledExtensionCount; i++) KLOG(1, "instance extension: %s", ci->ppEnabledExtensionNames[i]);
    for (uint32_t i = 0; i < ci->enabledLayerCount; i++) KLOG(0, "instance layer ignored: %s", ci->ppEnabledLayerNames[i]);
    id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
    if (!dev) { KERR("no Metal device"); return VK_ERROR_INITIALIZATION_FAILED; }
    auto* inst = new KInstance();
    if (ci->pApplicationInfo && ci->pApplicationInfo->apiVersion) inst->apiVersion = ci->pApplicationInfo->apiVersion;
    inst->physical = new KPhysicalDevice();
    inst->physical->instance = inst;
    inst->physical->mtl = dev;
    KLOG(1, "instance created on %s (app api %u.%u)", dev.name.UTF8String, VK_API_VERSION_MAJOR(inst->apiVersion),
         VK_API_VERSION_MINOR(inst->apiVersion));
    *out = (VkInstance)inst;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyInstance(VkInstance instance, const VkAllocationCallbacks*) {
    auto* inst = (KInstance*)instance;
    if (!inst) return;
    delete inst->physical;
    delete inst;
}

VKFN(VkResult) vkEnumeratePhysicalDevices(VkInstance instance, uint32_t* count, VkPhysicalDevice* out) {
    KTRACE_FIRST();
    if (!out) { *count = 1; return VK_SUCCESS; }
    if (*count < 1) return VK_INCOMPLETE;
    out[0] = (VkPhysicalDevice)((KInstance*)instance)->physical;
    *count = 1;
    return VK_SUCCESS;
}

VKFN(VkResult) vkEnumerateDeviceExtensionProperties(VkPhysicalDevice, const char* layer, uint32_t* count, VkExtensionProperties* props) {
    KTRACE_FIRST();
    if (layer) { *count = 0; return VK_ERROR_LAYER_NOT_PRESENT; }
    std::vector<VkExtensionProperties> all(kDeviceExtensions, kDeviceExtensions + sizeof(kDeviceExtensions) / sizeof(kDeviceExtensions[0]));
    if (kmtlConfig().rayQuery) all.insert(all.end(), kRayQueryExtensions, kRayQueryExtensions + 3);
    if (kmtlConfig().rtPipeline) all.push_back({VK_KHR_RAY_TRACING_PIPELINE_EXTENSION_NAME, VK_KHR_RAY_TRACING_PIPELINE_SPEC_VERSION});
    return fillExtensions(all.data(), (uint32_t)all.size(), count, props);
}

VKFN(VkResult) vkEnumerateDeviceLayerProperties(VkPhysicalDevice, uint32_t* count, VkLayerProperties*) {
    *count = 0;
    return VK_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------------
// Properties

static const uint8_t kPipelineCacheUUID[VK_UUID_SIZE] = {'K', 'S', 'A', 'M', 'T', 'L', 0, 1, 0, 0, 0, 0, 0, 0, 0, 1};

VKFN(void) vkGetPhysicalDeviceProperties(VkPhysicalDevice pd, VkPhysicalDeviceProperties* p) {
    KTRACE_FIRST();
    id<MTLDevice> d = ((KPhysicalDevice*)pd)->mtl;
    memset(p, 0, sizeof(*p));
    p->apiVersion = VK_MAKE_API_VERSION(0, 1, 3, 290);
    p->driverVersion = VK_MAKE_VERSION(KMTL_VERSION_MAJOR, KMTL_VERSION_MINOR, KMTL_VERSION_PATCH);
    p->vendorID = 0x106B;
    p->deviceID = 0x4D35;  // "M5"
    p->deviceType = VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU;
    snprintf(p->deviceName, sizeof(p->deviceName), "%s (KSA Metal)", d.name.UTF8String);
    memcpy(p->pipelineCacheUUID, kPipelineCacheUUID, VK_UUID_SIZE);
    auto& l = p->limits;
    l.maxImageDimension1D = 16384;
    l.maxImageDimension2D = 16384;
    l.maxImageDimension3D = 2048;
    l.maxImageDimensionCube = 16384;
    l.maxImageArrayLayers = 2048;
    l.maxTexelBufferElements = 64 * 1024 * 1024;
    l.maxUniformBufferRange = 64 * 1024 * 1024;
    l.maxStorageBufferRange = 1u << 30;
    l.maxPushConstantsSize = kMaxPushConstants;
    l.maxMemoryAllocationCount = 1u << 30;
    l.maxSamplerAllocationCount = 1u << 20;
    l.bufferImageGranularity = 64;
    l.sparseAddressSpaceSize = 0;
    l.maxBoundDescriptorSets = kMaxSets;
    l.maxPerStageDescriptorSamplers = 1u << 20;
    l.maxPerStageDescriptorUniformBuffers = 1u << 20;
    l.maxPerStageDescriptorStorageBuffers = 1u << 20;
    l.maxPerStageDescriptorSampledImages = 1u << 20;
    l.maxPerStageDescriptorStorageImages = 1u << 20;
    l.maxPerStageDescriptorInputAttachments = 8;
    l.maxPerStageResources = 1u << 20;
    l.maxDescriptorSetSamplers = 1u << 20;
    l.maxDescriptorSetUniformBuffers = 1u << 20;
    l.maxDescriptorSetUniformBuffersDynamic = 1024;
    l.maxDescriptorSetStorageBuffers = 1u << 20;
    l.maxDescriptorSetStorageBuffersDynamic = 1024;
    l.maxDescriptorSetSampledImages = 1u << 20;
    l.maxDescriptorSetStorageImages = 1u << 20;
    l.maxDescriptorSetInputAttachments = 8;
    l.maxVertexInputAttributes = 31;
    l.maxVertexInputBindings = 16;
    l.maxVertexInputAttributeOffset = 2047;
    l.maxVertexInputBindingStride = 2048;
    l.maxVertexOutputComponents = 124;
    l.maxTessellationGenerationLevel = 64;
    l.maxTessellationPatchSize = 32;
    l.maxTessellationControlPerVertexInputComponents = 124;
    l.maxTessellationControlPerVertexOutputComponents = 124;
    l.maxTessellationControlPerPatchOutputComponents = 120;
    l.maxTessellationControlTotalOutputComponents = 4096;
    l.maxTessellationEvaluationInputComponents = 124;
    l.maxTessellationEvaluationOutputComponents = 124;
    l.maxFragmentInputComponents = 124;
    l.maxFragmentOutputAttachments = 8;
    l.maxFragmentDualSrcAttachments = 1;
    l.maxFragmentCombinedOutputResources = 1u << 20;
    l.maxComputeSharedMemorySize = 32768;
    l.maxComputeWorkGroupCount[0] = l.maxComputeWorkGroupCount[1] = l.maxComputeWorkGroupCount[2] = 65535;
    l.maxComputeWorkGroupInvocations = 1024;
    l.maxComputeWorkGroupSize[0] = 1024;
    l.maxComputeWorkGroupSize[1] = 1024;
    l.maxComputeWorkGroupSize[2] = 1024;
    l.subPixelPrecisionBits = 8;
    l.subTexelPrecisionBits = 8;
    l.mipmapPrecisionBits = 8;
    l.maxDrawIndexedIndexValue = 0xFFFFFFFFu;
    l.maxDrawIndirectCount = 1u << 30;
    l.maxSamplerLodBias = 16;
    l.maxSamplerAnisotropy = 16;
    l.maxViewports = 16;
    l.maxViewportDimensions[0] = l.maxViewportDimensions[1] = 16384;
    l.viewportBoundsRange[0] = -32768;
    l.viewportBoundsRange[1] = 32767;
    l.viewportSubPixelBits = 8;
    l.minMemoryMapAlignment = 64;
    l.minTexelBufferOffsetAlignment = 16;
    l.minUniformBufferOffsetAlignment = 256;
    l.minStorageBufferOffsetAlignment = 64;
    l.minTexelOffset = -8;
    l.maxTexelOffset = 7;
    l.minTexelGatherOffset = -8;
    l.maxTexelGatherOffset = 7;
    l.minInterpolationOffset = -0.5f;
    l.maxInterpolationOffset = 0.5f;
    l.subPixelInterpolationOffsetBits = 4;
    l.maxFramebufferWidth = 16384;
    l.maxFramebufferHeight = 16384;
    l.maxFramebufferLayers = 2048;
    VkSampleCountFlags sc = VK_SAMPLE_COUNT_1_BIT | VK_SAMPLE_COUNT_2_BIT | VK_SAMPLE_COUNT_4_BIT | VK_SAMPLE_COUNT_8_BIT;
    l.framebufferColorSampleCounts = sc;
    l.framebufferDepthSampleCounts = sc;
    l.framebufferStencilSampleCounts = sc;
    l.framebufferNoAttachmentsSampleCounts = sc;
    l.maxColorAttachments = 8;
    l.sampledImageColorSampleCounts = sc;
    l.sampledImageIntegerSampleCounts = sc;
    l.sampledImageDepthSampleCounts = sc;
    l.sampledImageStencilSampleCounts = sc;
    l.storageImageSampleCounts = VK_SAMPLE_COUNT_1_BIT;
    l.maxSampleMaskWords = 1;
    l.timestampComputeAndGraphics = VK_TRUE;
    l.timestampPeriod = 1.0f;
    l.maxClipDistances = 8;
    l.maxCullDistances = 0;
    l.maxCombinedClipAndCullDistances = 8;
    l.discreteQueuePriorities = 2;
    l.pointSizeRange[0] = 1;
    l.pointSizeRange[1] = 511;
    l.lineWidthRange[0] = 1;
    l.lineWidthRange[1] = 1;
    l.pointSizeGranularity = 1;
    l.lineWidthGranularity = 1;
    l.strictLines = VK_FALSE;
    l.standardSampleLocations = VK_TRUE;
    l.optimalBufferCopyOffsetAlignment = 16;
    l.optimalBufferCopyRowPitchAlignment = 16;
    l.nonCoherentAtomSize = 64;
}

static void fillSubgroup(VkPhysicalDeviceVulkan11Properties* v) {
    v->subgroupSize = 32;
    v->subgroupSupportedStages = VK_SHADER_STAGE_COMPUTE_BIT | VK_SHADER_STAGE_FRAGMENT_BIT;
    v->subgroupSupportedOperations = VK_SUBGROUP_FEATURE_BASIC_BIT | VK_SUBGROUP_FEATURE_VOTE_BIT |
        VK_SUBGROUP_FEATURE_ARITHMETIC_BIT | VK_SUBGROUP_FEATURE_BALLOT_BIT | VK_SUBGROUP_FEATURE_SHUFFLE_BIT |
        VK_SUBGROUP_FEATURE_SHUFFLE_RELATIVE_BIT | VK_SUBGROUP_FEATURE_QUAD_BIT;
    v->subgroupQuadOperationsInAllStages = VK_FALSE;
    v->maxMultiviewViewCount = 32;
    v->maxMultiviewInstanceIndex = 1u << 27;
    v->maxPerSetDescriptors = 1u << 20;
    v->maxMemoryAllocationSize = 1ull << 34;
    v->pointClippingBehavior = VK_POINT_CLIPPING_BEHAVIOR_ALL_CLIP_PLANES;
}

VKFN(void) vkGetPhysicalDeviceProperties2(VkPhysicalDevice pd, VkPhysicalDeviceProperties2* p) {
    KTRACE_FIRST();
    vkGetPhysicalDeviceProperties(pd, &p->properties);
    for (auto* s = (VkBaseOutStructure*)p->pNext; s; s = s->pNext) {
        switch (s->sType) {
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_PROPERTIES:
            fillSubgroup((VkPhysicalDeviceVulkan11Properties*)s);
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SUBGROUP_PROPERTIES: {
            VkPhysicalDeviceVulkan11Properties v{};
            fillSubgroup(&v);
            auto* o = (VkPhysicalDeviceSubgroupProperties*)s;
            o->subgroupSize = v.subgroupSize;
            o->supportedStages = v.subgroupSupportedStages;
            o->supportedOperations = v.subgroupSupportedOperations;
            o->quadOperationsInAllStages = VK_FALSE;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_TRACING_PIPELINE_PROPERTIES_KHR: {
            auto* o = (VkPhysicalDeviceRayTracingPipelinePropertiesKHR*)s;
            o->shaderGroupHandleSize = 32;
            o->maxRayRecursionDepth = 31;  // only ray generation may trace; deeper recursion fails at pipeline creation
            o->maxShaderGroupStride = 4096;
            o->shaderGroupBaseAlignment = 64;
            o->shaderGroupHandleCaptureReplaySize = 32;
            o->maxRayDispatchInvocationCount = 1u << 30;
            o->shaderGroupHandleAlignment = 32;
            o->maxRayHitAttributeSize = 32;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MULTIVIEW_PROPERTIES: {
            auto* o = (VkPhysicalDeviceMultiviewProperties*)s;
            o->maxMultiviewViewCount = 32;
            o->maxMultiviewInstanceIndex = 1u << 27;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MAINTENANCE_3_PROPERTIES: {
            auto* o = (VkPhysicalDeviceMaintenance3Properties*)s;
            o->maxPerSetDescriptors = 1u << 20;
            o->maxMemoryAllocationSize = 1ull << 34;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MAINTENANCE_4_PROPERTIES:
            ((VkPhysicalDeviceMaintenance4Properties*)s)->maxBufferSize = 1ull << 34;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_PROPERTIES: {
            auto* o = (VkPhysicalDeviceVulkan12Properties*)s;
            o->driverID = (VkDriverId)0;  // Khronos registers no ID for this driver
            snprintf(o->driverName, sizeof(o->driverName), "KSA Metal");
            snprintf(o->driverInfo, sizeof(o->driverInfo), "%s", KMTL_VERSION);
            o->conformanceVersion = {1, 3, 0, 0};
            o->maxUpdateAfterBindDescriptorsInAllPools = 1u << 20;
            o->shaderSampledImageArrayNonUniformIndexingNative = VK_TRUE;
            o->robustBufferAccessUpdateAfterBind = VK_FALSE;
            o->quadDivergentImplicitLod = VK_FALSE;
            o->maxPerStageDescriptorUpdateAfterBindSamplers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindUniformBuffers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindStorageBuffers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindSampledImages = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindStorageImages = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindInputAttachments = 8;
            o->maxPerStageUpdateAfterBindResources = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindSamplers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindUniformBuffers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindUniformBuffersDynamic = 1024;
            o->maxDescriptorSetUpdateAfterBindStorageBuffers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindStorageBuffersDynamic = 1024;
            o->maxDescriptorSetUpdateAfterBindSampledImages = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindStorageImages = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindInputAttachments = 8;
            o->supportedDepthResolveModes = VK_RESOLVE_MODE_SAMPLE_ZERO_BIT | VK_RESOLVE_MODE_MIN_BIT | VK_RESOLVE_MODE_MAX_BIT;
            o->supportedStencilResolveModes = VK_RESOLVE_MODE_SAMPLE_ZERO_BIT;
            o->independentResolveNone = VK_TRUE;
            o->independentResolve = VK_TRUE;
            o->filterMinmaxSingleComponentFormats = VK_FALSE;
            o->maxTimelineSemaphoreValueDifference = UINT64_MAX;
            o->framebufferIntegerColorSampleCounts = VK_SAMPLE_COUNT_1_BIT;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_INDEXING_PROPERTIES: {
            auto* o = (VkPhysicalDeviceDescriptorIndexingProperties*)s;
            o->maxUpdateAfterBindDescriptorsInAllPools = 1u << 20;
            o->shaderSampledImageArrayNonUniformIndexingNative = VK_TRUE;
            o->maxPerStageDescriptorUpdateAfterBindSamplers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindUniformBuffers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindStorageBuffers = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindSampledImages = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindStorageImages = 1u << 20;
            o->maxPerStageDescriptorUpdateAfterBindInputAttachments = 8;
            o->maxPerStageUpdateAfterBindResources = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindSamplers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindUniformBuffers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindUniformBuffersDynamic = 1024;
            o->maxDescriptorSetUpdateAfterBindStorageBuffers = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindStorageBuffersDynamic = 1024;
            o->maxDescriptorSetUpdateAfterBindSampledImages = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindStorageImages = 1u << 20;
            o->maxDescriptorSetUpdateAfterBindInputAttachments = 8;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_PROPERTIES: {
            auto* o = (VkPhysicalDeviceVulkan13Properties*)s;
            o->minSubgroupSize = 32;
            o->maxSubgroupSize = 32;
            o->maxComputeWorkgroupSubgroups = 32;
            o->requiredSubgroupSizeStages = VK_SHADER_STAGE_COMPUTE_BIT;
            o->maxInlineUniformBlockSize = 0;
            o->maxBufferSize = 1ull << 34;
            o->storageTexelBufferOffsetAlignmentBytes = 16;
            o->uniformTexelBufferOffsetAlignmentBytes = 16;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DRIVER_PROPERTIES: {
            auto* o = (VkPhysicalDeviceDriverProperties*)s;
            o->driverID = (VkDriverId)0;
            snprintf(o->driverName, sizeof(o->driverName), "KSA Metal");
            snprintf(o->driverInfo, sizeof(o->driverInfo), "%s", KMTL_VERSION);
            o->conformanceVersion = {1, 3, 0, 0};
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DEPTH_STENCIL_RESOLVE_PROPERTIES: {
            auto* o = (VkPhysicalDeviceDepthStencilResolveProperties*)s;
            o->supportedDepthResolveModes = VK_RESOLVE_MODE_SAMPLE_ZERO_BIT | VK_RESOLVE_MODE_MIN_BIT | VK_RESOLVE_MODE_MAX_BIT;
            o->supportedStencilResolveModes = VK_RESOLVE_MODE_SAMPLE_ZERO_BIT;
            o->independentResolveNone = VK_TRUE;
            o->independentResolve = VK_TRUE;
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_PROPERTIES_KHR: {
            auto* o = (VkPhysicalDeviceAccelerationStructurePropertiesKHR*)s;
            o->maxGeometryCount = 1u << 24;
            o->maxInstanceCount = 1u << 24;
            o->maxPrimitiveCount = 1u << 29;
            o->maxPerStageDescriptorAccelerationStructures = 64;
            o->maxPerStageDescriptorUpdateAfterBindAccelerationStructures = 64;
            o->maxDescriptorSetAccelerationStructures = 64;
            o->maxDescriptorSetUpdateAfterBindAccelerationStructures = 64;
            o->minAccelerationStructureScratchOffsetAlignment = 256;
            break;
        }
        default:
            KLOG(2, "properties2: unhandled sType %d", s->sType);
            break;
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Features

static void fillFeatures(VkPhysicalDeviceFeatures* f) {
    memset(f, 0, sizeof(*f));
    f->robustBufferAccess = VK_FALSE;
    f->fullDrawIndexUint32 = VK_TRUE;
    f->imageCubeArray = VK_TRUE;
    f->independentBlend = VK_TRUE;
    f->geometryShader = VK_FALSE;
    f->tessellationShader = kmtlConfig().tessellation;
    f->sampleRateShading = VK_TRUE;
    f->dualSrcBlend = VK_TRUE;
    f->logicOp = VK_FALSE;
    f->multiDrawIndirect = VK_TRUE;
    f->drawIndirectFirstInstance = VK_TRUE;
    f->depthClamp = VK_TRUE;
    f->depthBiasClamp = VK_TRUE;
    f->fillModeNonSolid = VK_TRUE;
    f->depthBounds = VK_FALSE;
    f->wideLines = VK_FALSE;
    f->largePoints = VK_TRUE;
    f->alphaToOne = VK_TRUE;
    f->multiViewport = VK_TRUE;
    f->samplerAnisotropy = VK_TRUE;
    f->textureCompressionBC = VK_TRUE;
    f->occlusionQueryPrecise = VK_TRUE;
    f->vertexPipelineStoresAndAtomics = VK_TRUE;
    f->fragmentStoresAndAtomics = VK_TRUE;
    f->shaderImageGatherExtended = VK_TRUE;
    f->shaderStorageImageExtendedFormats = VK_TRUE;
    f->shaderStorageImageMultisample = VK_FALSE;
    f->shaderStorageImageReadWithoutFormat = VK_TRUE;
    f->shaderStorageImageWriteWithoutFormat = VK_TRUE;
    f->shaderUniformBufferArrayDynamicIndexing = VK_TRUE;
    f->shaderSampledImageArrayDynamicIndexing = VK_TRUE;
    f->shaderStorageBufferArrayDynamicIndexing = VK_TRUE;
    f->shaderStorageImageArrayDynamicIndexing = VK_TRUE;
    f->shaderClipDistance = VK_TRUE;
    f->shaderFloat64 = VK_FALSE;
    f->shaderInt64 = VK_TRUE;
    f->shaderInt16 = VK_TRUE;
}

VKFN(void) vkGetPhysicalDeviceFeatures(VkPhysicalDevice, VkPhysicalDeviceFeatures* f) {
    KTRACE_FIRST();
    fillFeatures(f);
}

#define ON(field) o->field = VK_TRUE
VKFN(void) vkGetPhysicalDeviceFeatures2(VkPhysicalDevice, VkPhysicalDeviceFeatures2* p) {
    KTRACE_FIRST();
    fillFeatures(&p->features);
    for (auto* s = (VkBaseOutStructure*)p->pNext; s; s = s->pNext) {
        switch (s->sType) {
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_1_FEATURES: {
            auto* o = (VkPhysicalDeviceVulkan11Features*)s;
            ON(storageBuffer16BitAccess); ON(uniformAndStorageBuffer16BitAccess); ON(multiview);
            ON(shaderDrawParameters);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_2_FEATURES: {
            auto* o = (VkPhysicalDeviceVulkan12Features*)s;
            ON(samplerMirrorClampToEdge); ON(drawIndirectCount); ON(storageBuffer8BitAccess);
            ON(uniformAndStorageBuffer8BitAccess); ON(shaderFloat16); ON(shaderInt8); ON(descriptorIndexing);
            ON(shaderSampledImageArrayNonUniformIndexing); ON(shaderStorageBufferArrayNonUniformIndexing);
            ON(shaderStorageImageArrayNonUniformIndexing); ON(shaderUniformBufferArrayNonUniformIndexing);
            ON(descriptorBindingSampledImageUpdateAfterBind); ON(descriptorBindingStorageImageUpdateAfterBind);
            ON(descriptorBindingStorageBufferUpdateAfterBind); ON(descriptorBindingUniformBufferUpdateAfterBind);
            ON(descriptorBindingUpdateUnusedWhilePending); ON(descriptorBindingPartiallyBound);
            ON(descriptorBindingVariableDescriptorCount); ON(runtimeDescriptorArray); ON(scalarBlockLayout);
            ON(imagelessFramebuffer); ON(uniformBufferStandardLayout); ON(shaderSubgroupExtendedTypes);
            ON(separateDepthStencilLayouts); ON(hostQueryReset); ON(timelineSemaphore); ON(bufferDeviceAddress);
            ON(vulkanMemoryModel); ON(vulkanMemoryModelDeviceScope); ON(shaderOutputViewportIndex);
            ON(shaderOutputLayer); ON(subgroupBroadcastDynamicId);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_VULKAN_1_3_FEATURES: {
            auto* o = (VkPhysicalDeviceVulkan13Features*)s;
            ON(shaderDemoteToHelperInvocation); ON(shaderTerminateInvocation); ON(subgroupSizeControl);
            ON(computeFullSubgroups); ON(synchronization2); ON(shaderZeroInitializeWorkgroupMemory);
            ON(dynamicRendering); ON(shaderIntegerDotProduct); ON(maintenance4); ON(pipelineCreationCacheControl);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MULTIVIEW_FEATURES: {
            auto* o = (VkPhysicalDeviceMultiviewFeatures*)s;
            ON(multiview);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_DRAW_PARAMETERS_FEATURES:
            ((VkPhysicalDeviceShaderDrawParametersFeatures*)s)->shaderDrawParameters = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DESCRIPTOR_INDEXING_FEATURES: {
            auto* o = (VkPhysicalDeviceDescriptorIndexingFeatures*)s;
            ON(shaderSampledImageArrayNonUniformIndexing); ON(shaderStorageBufferArrayNonUniformIndexing);
            ON(shaderStorageImageArrayNonUniformIndexing); ON(shaderUniformBufferArrayNonUniformIndexing);
            ON(descriptorBindingSampledImageUpdateAfterBind); ON(descriptorBindingStorageImageUpdateAfterBind);
            ON(descriptorBindingStorageBufferUpdateAfterBind); ON(descriptorBindingUniformBufferUpdateAfterBind);
            ON(descriptorBindingUpdateUnusedWhilePending); ON(descriptorBindingPartiallyBound);
            ON(descriptorBindingVariableDescriptorCount); ON(runtimeDescriptorArray);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_BUFFER_DEVICE_ADDRESS_FEATURES:
            ((VkPhysicalDeviceBufferDeviceAddressFeatures*)s)->bufferDeviceAddress = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SYNCHRONIZATION_2_FEATURES:
            ((VkPhysicalDeviceSynchronization2Features*)s)->synchronization2 = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DYNAMIC_RENDERING_FEATURES:
            ((VkPhysicalDeviceDynamicRenderingFeatures*)s)->dynamicRendering = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MAINTENANCE_4_FEATURES:
            ((VkPhysicalDeviceMaintenance4Features*)s)->maintenance4 = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SCALAR_BLOCK_LAYOUT_FEATURES:
            ((VkPhysicalDeviceScalarBlockLayoutFeatures*)s)->scalarBlockLayout = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_SHADER_DEMOTE_TO_HELPER_INVOCATION_FEATURES:
            ((VkPhysicalDeviceShaderDemoteToHelperInvocationFeatures*)s)->shaderDemoteToHelperInvocation = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES:
            ((VkPhysicalDeviceTimelineSemaphoreFeatures*)s)->timelineSemaphore = VK_FALSE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_HOST_QUERY_RESET_FEATURES:
            ((VkPhysicalDeviceHostQueryResetFeatures*)s)->hostQueryReset = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PORTABILITY_SUBSET_FEATURES_KHR: {
            // Not advertised, but answer sanely if asked: everything the game might ask for works
            auto* o = (VkPhysicalDevicePortabilitySubsetFeaturesKHR*)s;
            ON(constantAlphaColorBlendFactors); ON(events); ON(imageViewFormatReinterpretation);
            ON(imageViewFormatSwizzle); ON(imageView2DOn3DImage); ON(multisampleArrayImage);
            ON(mutableComparisonSamplers); ON(pointPolygons); ON(samplerMipLodBias); ON(separateStencilMaskRef);
            ON(shaderSampleRateInterpolationFunctions); ON(tessellationIsolines); ON(tessellationPointMode);
            ON(triangleFans); ON(vertexAttributeAccessBeyondStride);
            break;
        }
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_ACCELERATION_STRUCTURE_FEATURES_KHR:
            ((VkPhysicalDeviceAccelerationStructureFeaturesKHR*)s)->accelerationStructure = kmtlConfig().rayQuery;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_QUERY_FEATURES_KHR:
            ((VkPhysicalDeviceRayQueryFeaturesKHR*)s)->rayQuery = kmtlConfig().rayQuery;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_RAY_TRACING_PIPELINE_FEATURES_KHR:
            // Pipelines run as compute kernels with ray queries (rtpipeline.mm): no indirect traces, no primitive culling
            ((VkPhysicalDeviceRayTracingPipelineFeaturesKHR*)s)->rayTracingPipeline = kmtlConfig().rtPipeline;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_ID_FEATURES_KHR:
            ((VkPhysicalDevicePresentIdFeaturesKHR*)s)->presentId = VK_TRUE;
            break;
        case VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_PRESENT_WAIT_FEATURES_KHR:
            ((VkPhysicalDevicePresentWaitFeaturesKHR*)s)->presentWait = VK_TRUE;
            break;
        default:
            KLOG(2, "features2: unhandled sType %d (left zero)", s->sType);
            break;
        }
    }
}
#undef ON

// ---------------------------------------------------------------------------------------------------------------
// Queues and memory

VKFN(void) vkGetPhysicalDeviceQueueFamilyProperties(VkPhysicalDevice, uint32_t* count, VkQueueFamilyProperties* out) {
    KTRACE_FIRST();
    // Family 0 does everything; family 1 only transfers. No compute-only family: KSA would put only its ocean
    // readbacks there and make every frame wait for that queue
    uint32_t n = 2;
    if (!out) { *count = n; return; }
    n = std::min(n, *count);
    for (uint32_t f = 0; f < n; f++) {
        out[f] = {};
        out[f].queueFlags = f == 0 ? VK_QUEUE_GRAPHICS_BIT | VK_QUEUE_COMPUTE_BIT | VK_QUEUE_TRANSFER_BIT : VK_QUEUE_TRANSFER_BIT;
        out[f].queueCount = 1;
        out[f].timestampValidBits = 64;
        out[f].minImageTransferGranularity = {1, 1, 1};
    }
    *count = n;
}

VKFN(void) vkGetPhysicalDeviceQueueFamilyProperties2(VkPhysicalDevice pd, uint32_t* count, VkQueueFamilyProperties2* out) {
    VkQueueFamilyProperties props[2];
    uint32_t n = 2;
    vkGetPhysicalDeviceQueueFamilyProperties(pd, &n, props);
    if (!out) { *count = n; return; }
    n = std::min(n, *count);
    for (uint32_t f = 0; f < n; f++) out[f].queueFamilyProperties = props[f];
    *count = n;
}

// Memory types: 0 device-local (private heap), 1 host-visible coherent, 2 host-visible coherent cached (shared heaps)
VKFN(void) vkGetPhysicalDeviceMemoryProperties(VkPhysicalDevice pd, VkPhysicalDeviceMemoryProperties* p) {
    KTRACE_FIRST();
    memset(p, 0, sizeof(*p));
    p->memoryHeapCount = 1;
    p->memoryHeaps[0].size = ((KPhysicalDevice*)pd)->mtl.recommendedMaxWorkingSetSize;
    p->memoryHeaps[0].flags = VK_MEMORY_HEAP_DEVICE_LOCAL_BIT;
    p->memoryTypeCount = 3;
    p->memoryTypes[0] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0};
    p->memoryTypes[1] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                         VK_MEMORY_PROPERTY_HOST_COHERENT_BIT, 0};
    p->memoryTypes[2] = {VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT | VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT |
                         VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT, 0};
}


VKFN(void) vkGetPhysicalDeviceMemoryProperties2(VkPhysicalDevice pd, VkPhysicalDeviceMemoryProperties2* p) {
    vkGetPhysicalDeviceMemoryProperties(pd, &p->memoryProperties);
    if (auto* b = (VkPhysicalDeviceMemoryBudgetPropertiesEXT*)findNext(p->pNext, VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_EXT)) {
        memset(b->heapBudget, 0, sizeof(b->heapBudget));
        memset(b->heapUsage, 0, sizeof(b->heapUsage));
        id<MTLDevice> d = ((KPhysicalDevice*)pd)->mtl;
        // KSA_MTL_BUDGET_MB overrides the reported budget (the game's texture streamer sizes itself from it)
        int mb = kmtlConfig().budgetMb;
        b->heapBudget[0] = mb > 0 ? (VkDeviceSize)mb << 20 : d.recommendedMaxWorkingSetSize;
        b->heapUsage[0] = (VkDeviceSize)g_allocatedBytes.load();
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Formats

VKFN(void) vkGetPhysicalDeviceFormatProperties(VkPhysicalDevice, VkFormat format, VkFormatProperties* p) {
    KTRACE_FIRST();
    memset(p, 0, sizeof(*p));
    const KFormatInfo* f = kmtlFormat(format);
    if (f && f->mtl != MTLPixelFormatInvalid) {
        VkFormatFeatureFlags t = VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT | VK_FORMAT_FEATURE_TRANSFER_SRC_BIT |
            VK_FORMAT_FEATURE_TRANSFER_DST_BIT | VK_FORMAT_FEATURE_BLIT_SRC_BIT;
        if (!f->isInt && !f->isUint) t |= VK_FORMAT_FEATURE_SAMPLED_IMAGE_FILTER_LINEAR_BIT;
        if (f->renderable) {
            if (f->depth || f->stencil) t |= VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT;
            else t |= VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BIT | VK_FORMAT_FEATURE_COLOR_ATTACHMENT_BLEND_BIT |
                     VK_FORMAT_FEATURE_BLIT_DST_BIT | VK_FORMAT_FEATURE_STORAGE_IMAGE_BIT;
        }
        p->optimalTilingFeatures = t;
        p->linearTilingFeatures = (f->blockW == 1 && !f->depth && !f->stencil) ? t : 0;
    }
    if (kmtlVertexFormat(format) != MTLVertexFormatInvalid) p->bufferFeatures |= VK_FORMAT_FEATURE_VERTEX_BUFFER_BIT;
}

VKFN(void) vkGetPhysicalDeviceFormatProperties2(VkPhysicalDevice pd, VkFormat format, VkFormatProperties2* p) {
    vkGetPhysicalDeviceFormatProperties(pd, format, &p->formatProperties);
}

VKFN(VkResult) vkGetPhysicalDeviceImageFormatProperties(VkPhysicalDevice, VkFormat format, VkImageType type, VkImageTiling,
                                                        VkImageUsageFlags, VkImageCreateFlags, VkImageFormatProperties* p) {
    KTRACE_FIRST();
    const KFormatInfo* f = kmtlFormat(format);
    if (!f || f->mtl == MTLPixelFormatInvalid) return VK_ERROR_FORMAT_NOT_SUPPORTED;
    p->maxExtent = {16384, type == VK_IMAGE_TYPE_1D ? 1u : 16384u, type == VK_IMAGE_TYPE_3D ? 2048u : 1u};
    p->maxMipLevels = 15;
    p->maxArrayLayers = type == VK_IMAGE_TYPE_3D ? 1 : 2048;
    p->sampleCounts = (type == VK_IMAGE_TYPE_2D && f->blockW == 1) ? 0xF : VK_SAMPLE_COUNT_1_BIT;
    p->maxResourceSize = 1ull << 34;
    return VK_SUCCESS;
}

VKFN(VkResult) vkGetPhysicalDeviceImageFormatProperties2(VkPhysicalDevice pd, const VkPhysicalDeviceImageFormatInfo2* i,
                                                         VkImageFormatProperties2* p) {
    return vkGetPhysicalDeviceImageFormatProperties(pd, i->format, i->type, i->tiling, i->usage, i->flags, &p->imageFormatProperties);
}

// ---------------------------------------------------------------------------------------------------------------
// Surface

VKFN(VkResult) vkCreateMetalSurfaceEXT(VkInstance, const VkMetalSurfaceCreateInfoEXT* ci, const VkAllocationCallbacks*, VkSurfaceKHR* out) {
    KTRACE_FIRST();
    auto* s = new KSurface();
    s->layer = (CAMetalLayer*)ci->pLayer;
    KLOG(1, "surface on CAMetalLayer %p", ci->pLayer);
    *out = (VkSurfaceKHR)s;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySurfaceKHR(VkInstance, VkSurfaceKHR surface, const VkAllocationCallbacks*) {
    delete (KSurface*)surface;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceSupportKHR(VkPhysicalDevice, uint32_t family, VkSurfaceKHR, VkBool32* supported) {
    *supported = family == 0;  // presentation is on the graphics queue
    return VK_SUCCESS;
}

VkExtent2D kmtlLayerExtent(CAMetalLayer* layer) {
    CGSize size = layer.bounds.size;  // property reads are safe off the main thread
    CGFloat scale = kmtlConfig().retina ? 1.0 : layer.contentsScale;  // KSA_MTL_RETINA: the game draws in points
    return {(uint32_t)(size.width * scale + 0.5), (uint32_t)(size.height * scale + 0.5)};
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceCapabilitiesKHR(VkPhysicalDevice, VkSurfaceKHR surface, VkSurfaceCapabilitiesKHR* c) {
    KTRACE_FIRST();
    auto* s = (KSurface*)surface;
    VkExtent2D e = kmtlLayerExtent(s->layer);
    if (e.width == 0) e.width = 1;
    if (e.height == 0) e.height = 1;
    c->minImageCount = 2;
    c->maxImageCount = 3;
    c->currentExtent = e;
    c->minImageExtent = {1, 1};
    c->maxImageExtent = {16384, 16384};
    c->maxImageArrayLayers = 1;
    c->supportedTransforms = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c->currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c->supportedCompositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR | VK_COMPOSITE_ALPHA_POST_MULTIPLIED_BIT_KHR;
    c->supportedUsageFlags = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
        VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    KLOG(1, "surface extent %ux%u", e.width, e.height);
    return VK_SUCCESS;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceCapabilities2KHR(VkPhysicalDevice pd, const VkPhysicalDeviceSurfaceInfo2KHR* info, VkSurfaceCapabilities2KHR* c) {
    return vkGetPhysicalDeviceSurfaceCapabilitiesKHR(pd, info->surface, &c->surfaceCapabilities);
}

static const VkSurfaceFormatKHR kSurfaceFormats[] = {
    {VK_FORMAT_B8G8R8A8_UNORM, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
    {VK_FORMAT_B8G8R8A8_SRGB, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
};

VKFN(VkResult) vkGetPhysicalDeviceSurfaceFormatsKHR(VkPhysicalDevice, VkSurfaceKHR, uint32_t* count, VkSurfaceFormatKHR* out) {
    KTRACE_FIRST();
    const uint32_t n = 2;
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    memcpy(out, kSurfaceFormats, c * sizeof(*out));
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceFormats2KHR(VkPhysicalDevice, const VkPhysicalDeviceSurfaceInfo2KHR*, uint32_t* count, VkSurfaceFormat2KHR* out) {
    const uint32_t n = 2;
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    for (uint32_t i = 0; i < c; i++) out[i].surfaceFormat = kSurfaceFormats[i];
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfacePresentModesKHR(VkPhysicalDevice, VkSurfaceKHR, uint32_t* count, VkPresentModeKHR* out) {
    KTRACE_FIRST();
    static const VkPresentModeKHR modes[] = {VK_PRESENT_MODE_FIFO_KHR, VK_PRESENT_MODE_IMMEDIATE_KHR, VK_PRESENT_MODE_MAILBOX_KHR};
    const uint32_t n = 3;
    if (!out) { *count = n; return VK_SUCCESS; }
    uint32_t c = std::min(*count, n);
    memcpy(out, modes, c * sizeof(*out));
    *count = c;
    return c < n ? VK_INCOMPLETE : VK_SUCCESS;
}
