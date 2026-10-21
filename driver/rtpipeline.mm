// Ray tracing pipelines (VK_KHR_ray_tracing_pipeline) as compute pipelines with ray queries.
//
// Metal has no ray tracing pipelines. A pipeline whose any-hit shaders never run (instances forced opaque, as KSA's
// are) and whose closest-hit and miss shaders do not trace rays themselves is a compute kernel: the ray generation
// shader becomes the kernel, every OpTraceRayKHR becomes a ray query, and the closest-hit and miss shaders become
// functions it calls on the committed intersection. The rewrite works on SPIR-V:
//
//   closest-hit / miss: the entry point becomes an exported function (ksa_chit, ksa_miss_<n>); its incoming payload,
//     hit attributes and ray-tracing built-ins become imported Private variables (ksa_payload_<location>,
//     ksa_hitattr, ksa_bi_<builtin>)
//   ray generation: GLCompute 8x8; LaunchId -> GlobalInvocationId; LaunchSize from a uniform at set 15 binding 0
//     (buffer kRtLaunchBufferIndex) with an early return outside the launch; payloads become exported Private
//     variables; each trace site becomes initialize + proceed + a branch that sets the built-ins and calls ksa_chit or
//     ksa_miss_<missIndex>
//
// SPIRV-Tools links the modules (merging types and resolving the imports), and the linked module is translated like
// any compute shader. Shader group handles name the group; the shader binding table is not read: the miss index of a
// trace is the n-th miss group of the pipeline, and the hit group is the first triangles hit group.
#include "kmtl.h"
#include "rtspirv.h"

KPipeline* kmtlCreateComputeFromStage(KDevice* dev, KPipelineLayout* layout, const VkPipelineShaderStageCreateInfo* stage,
                                      bool rtLaunch);



// ---------------------------------------------------------------------------------------------------------------
// Pipelines

static const uint32_t kGroupHandleSize = 32;

bool kmtlRayTracingPipelinesEnabled() {
    static const bool v = kmtlRayQueryEnabled() && kmtlEnvInt("KSA_MTL_RTPIPELINE", 1) != 0;
    return v;
}

static KPipeline* createRayTracingPipeline(KDevice* dev, const VkRayTracingPipelineCreateInfoKHR* ci) {
    auto module = [&](uint32_t stage) -> KShaderModule* { return stage < ci->stageCount ? (KShaderModule*)ci->pStages[stage].module : nullptr; };
    int raygen = -1, chit = -1;
    std::vector<int> misses;
    for (uint32_t g = 0; g < ci->groupCount; g++) {
        const auto& gr = ci->pGroups[g];
        if (gr.type == VK_RAY_TRACING_SHADER_GROUP_TYPE_GENERAL_KHR && gr.generalShader < ci->stageCount) {
            VkShaderStageFlagBits st = ci->pStages[gr.generalShader].stage;
            if (st == VK_SHADER_STAGE_RAYGEN_BIT_KHR && raygen < 0) raygen = (int)gr.generalShader;
            else if (st == VK_SHADER_STAGE_MISS_BIT_KHR) misses.push_back((int)gr.generalShader);
        } else if (gr.type == VK_RAY_TRACING_SHADER_GROUP_TYPE_TRIANGLES_HIT_GROUP_KHR && chit < 0 && gr.closestHitShader != VK_SHADER_UNUSED_KHR) {
            chit = (int)gr.closestHitShader;
            if (gr.anyHitShader != VK_SHADER_UNUSED_KHR) KLOG(1, "ray tracing pipeline: any-hit shader ignored (instances must be opaque)");
        }
    }
    if (raygen < 0) {
        KERR("ray tracing pipeline without a ray generation group");
        return nullptr;
    }
    std::string err;
    std::vector<std::vector<uint32_t>> missModules;
    for (int m : misses) missModules.push_back(module(m)->spirv);
    std::vector<uint32_t> linked;
    if (!kmtlLinkRayTracing(module(raygen)->spirv, chit >= 0 ? &module(chit)->spirv : nullptr, missModules, &linked, &err)) {
        KERR("ray tracing pipeline: %s", err.c_str());
        return nullptr;
    }
    if (const char* dir = kmtlEnv("KSA_MTL_DUMP_SPV")) {
        char path[1200];
        snprintf(path, sizeof(path), "%s/rt_%016llx.spv", dir, (unsigned long long)module(raygen)->hash);
        if (FILE* f = fopen(path, "wb")) { fwrite(linked.data(), 4, linked.size(), f); fclose(f); }
    }
    // The linked kernel goes through the compute path; the module lives as long as the call
    KShaderModule km;
    km.spirv = std::move(linked);
    km.hash = kmtlHash(km.spirv.data(), km.spirv.size() * 4);
    VkPipelineShaderStageCreateInfo st = ci->pStages[raygen];
    st.stage = VK_SHADER_STAGE_COMPUTE_BIT;
    st.module = (VkShaderModule)&km;
    KPipeline* p = kmtlCreateComputeFromStage(dev, (KPipelineLayout*)ci->layout, &st, true);
    if (!p) return nullptr;
    p->bindPoint = VK_PIPELINE_BIND_POINT_RAY_TRACING_KHR;
    p->rtGroupCount = ci->groupCount;
    KLOG(1, "ray tracing pipeline: %u groups, %zu miss shaders, closest-hit %s -> compute kernel %lux%lu",
         ci->groupCount, misses.size(), chit >= 0 ? "yes" : "no", (unsigned long)p->workgroup.width, (unsigned long)p->workgroup.height);
    return p;
}

VKFN(VkResult) vkCreateRayTracingPipelinesKHR(VkDevice device, VkDeferredOperationKHR, VkPipelineCache, uint32_t n,
                                              const VkRayTracingPipelineCreateInfoKHR* cis, const VkAllocationCallbacks*, VkPipeline* out) {
    KTRACE_FIRST();
    VkResult res = VK_SUCCESS;
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            out[i] = (VkPipeline)createRayTracingPipeline((KDevice*)device, &cis[i]);
            if (!out[i]) res = VK_ERROR_INITIALIZATION_FAILED;
        }
    }
    return res;
}

VKFN(VkResult) vkGetRayTracingShaderGroupHandlesKHR(VkDevice, VkPipeline pipeline, uint32_t first, uint32_t count, size_t size, void* data) {
    KTRACE_FIRST();
    auto* p = (KPipeline*)pipeline;
    if (size < (size_t)count * kGroupHandleSize) return VK_ERROR_OUT_OF_HOST_MEMORY;
    memset(data, 0, size);
    for (uint32_t i = 0; i < count; i++) {
        uint32_t h[2] = {0x5452534Bu /* "KSRT" */, first + i};
        memcpy((uint8_t*)data + (size_t)i * kGroupHandleSize, h, sizeof(h));
    }
    (void)p;
    return VK_SUCCESS;
}

VKFN(VkDeviceSize) vkGetRayTracingShaderGroupStackSizeKHR(VkDevice, VkPipeline, uint32_t, VkShaderGroupShaderKHR) { return 0; }

VKFN(void) vkCmdSetRayTracingPipelineStackSizeKHR(VkCommandBuffer, uint32_t) {}

uint32_t kmtlRtGroupHandleSize() { return kGroupHandleSize; }
