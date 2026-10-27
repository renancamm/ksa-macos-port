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
#include <set>

// ---------------------------------------------------------------------------------------------------------------
// Traces as intersector calls (KSA_MTL_RT_INTERSECTOR, on by default)
//
// SPIRV-Cross writes each trace of the rewritten kernel as an intersection_query: reset, one next(), then the
// committed hit's getters. With opaque geometry (the precondition of the whole rewrite: KSA's instances are opaque)
// next() never stops at a candidate, so this is a closest-hit (or, with accept-any, any-hit) query, which Metal's
// intersector answers in one intersect() call that it traverses faster: the ray tracing kernel ~15 % faster, the same
// image. A site becomes
//
//   float3 _ksaRayN_o = <origin>; float3 _ksaRayN_d = <direction>;
//   intersector<...> _ksaRayN_x; ksaConfigIntersector(_ksaRayN_x, <ray flags>);
//   intersection_result<...> _ksaRayN = _ksaRayN_x.intersect(ray(_ksaRayN_o, _ksaRayN_d, <tmin>, <tmax>), <as>, <mask>);
//
// and the getters after it in the same function, up to the query's next reset, read _ksaRayN. Any other use of a
// rewritten query, or a trace of another shape, leaves the kernel as translated.

static bool isIdentChar(char c) { return isalnum((unsigned char)c) || c == '_'; }

// The first use of identifier q followed by what, from at: not part of a longer name or a member access
static size_t findUse(const std::string& s, const std::string& q, const std::string& what, size_t at) {
    for (; (at = s.find(q + what, at)) != std::string::npos; at++)
        if (at == 0 || (!isIdentChar(s[at - 1]) && s[at - 1] != '.' && s[at - 1] != '>')) return at;
    return std::string::npos;
}

// The arguments of the call whose '(' is at open, split at top-level commas; *close gets the matching ')'
static bool callArgs(const std::string& s, size_t open, std::vector<std::string>* args, size_t* close) {
    int depth = 0;
    size_t start = open + 1;
    for (size_t i = open; i < s.size(); i++) {
        char c = s[i];
        if (c == '(' || c == '[' || c == '{') depth++;
        else if (c == ')' || c == ']' || c == '}') {
            if (--depth == 0) {
                args->push_back(s.substr(start, i - start));
                *close = i;
                return true;
            }
        } else if (c == ',' && depth == 1) {
            args->push_back(s.substr(start, i - start));
            start = i + 1;
            while (start < s.size() && s[start] == ' ') start++;
        }
    }
    return false;
}

std::string kmtlRtIntersectors(const std::string& msl) {
    static const char* kTags = "raytracing::instancing, raytracing::triangle_data, raytracing::world_space_data";
    static const char* kHelper = R"(
template <typename X> static inline __attribute__((always_inline)) void ksaConfigIntersector(thread X& x, uint flags)
{
    if ((flags & 1) != 0) x.force_opacity(forced_opacity::opaque);
    if ((flags & 2) != 0) x.force_opacity(forced_opacity::non_opaque);
    if ((flags & 4) != 0) x.accept_any_intersection(true);
    if ((flags & 16) != 0) x.set_triangle_cull_mode(triangle_cull_mode::back);
    if ((flags & 32) != 0) x.set_triangle_cull_mode(triangle_cull_mode::front);
    if ((flags & 64) != 0) x.set_opacity_cull_mode(opacity_cull_mode::opaque);
    if ((flags & 128) != 0) x.set_opacity_cull_mode(opacity_cull_mode::non_opaque);
    if ((flags & 256) != 0) x.set_geometry_cull_mode(geometry_cull_mode::triangle);
    if ((flags & 512) != 0) x.set_geometry_cull_mode(geometry_cull_mode::bounding_box);
    x.assume_geometry_type(raytracing::geometry_type::triangle);
}
)";
    const std::string helperAfter = "intersection_params spvMakeIntersectionParams(uint flags)\n{";
    size_t helperAt = msl.find(helperAfter);
    if (helperAt == std::string::npos || (helperAt = msl.find("\n}\n", helperAt)) == std::string::npos) return msl;
    std::string out = msl.substr(0, helperAt + 3) + kHelper + msl.substr(helperAt + 3);

    const std::string reset = ".reset(ray(";
    int site = 0;
    std::set<std::string> queries;
    for (size_t at = 0; (at = out.find(reset, at)) != std::string::npos;) {
        size_t qb = at;
        while (qb > 0 && isIdentChar(out[qb - 1])) qb--;
        const std::string q = out.substr(qb, at - qb);
        if (qb > 0 && (out[qb - 1] == '.' || out[qb - 1] == '>')) return msl;
        std::vector<std::string> args, rayArgs, flags;
        size_t close, rayClose, flagsClose;
        if (q.empty() || !callArgs(out, at + 6, &args, &close) || args.size() != 4 ||
            !callArgs(out, at + 10, &rayArgs, &rayClose) || rayArgs.size() != 4 ||
            args[3].compare(0, 26, "spvMakeIntersectionParams(") != 0 ||
            !callArgs(args[3], 25, &flags, &flagsClose) || flags.size() != 1 || flagsClose != args[3].size() - 1 ||
            out.compare(close, 2, ");") != 0)
            return msl;
        // The next two statements: "bool _a = q.next();" and "uint _b = uint(q.get_committed_intersection_type());"
        size_t lineEnd = out.find('\n', close);
        size_t nextEnd = out.find('\n', lineEnd + 1), typeEnd = out.find('\n', nextEnd + 1);
        if (typeEnd == std::string::npos) return msl;
        std::string nextLine = out.substr(lineEnd + 1, nextEnd - lineEnd - 1);
        std::string typeLine = out.substr(nextEnd + 1, typeEnd - nextEnd - 1);
        size_t ni = nextLine.find_first_not_of(' '), ti = typeLine.find_first_not_of(' ');
        const std::string nextCall = " = " + q + ".next();", typeCall = " = uint(" + q + ".get_committed_intersection_type());";
        if (ni == std::string::npos || ti == std::string::npos || nextLine.compare(ni, 5, "bool ") != 0 ||
            nextLine.size() < nextCall.size() || nextLine.compare(nextLine.size() - nextCall.size(), nextCall.size(), nextCall) != 0 ||
            typeLine.compare(ti, 5, "uint ") != 0 || typeLine.size() < typeCall.size() ||
            typeLine.compare(typeLine.size() - typeCall.size(), typeCall.size(), typeCall) != 0)
            return msl;
        std::string indent = out.substr(lineEnd + 1, ni);
        std::string nextVar = nextLine.substr(ni + 5, nextLine.size() - nextCall.size() - ni - 5);
        std::string typeVar = typeLine.substr(ti + 5, typeLine.size() - typeCall.size() - ti - 5);
        std::string r = "_ksaRay" + std::to_string(site++);
        std::string code = "float3 " + r + "_o = " + rayArgs[0] + "; float3 " + r + "_d = " + rayArgs[1] + ";\n" +
            indent + "raytracing::intersector<" + kTags + "> " + r + "_x;\n" +
            indent + "ksaConfigIntersector(" + r + "_x, " + flags[0] + ");\n" +
            indent + "raytracing::intersection_result<" + kTags + "> " + r + " = " + r + "_x.intersect(ray(" + r + "_o, " +
            r + "_d, " + rayArgs[2] + ", " + rayArgs[3] + "), " + args[1] + ", " + args[2] + ");\n" +
            // With opaque geometry next() returns false: the query never stops at a candidate
            indent + "bool " + nextVar + " = false;\n" +
            indent + "uint " + typeVar + " = uint(" + r + ".type);";
        // The getters up to the query's next reset or the function's end, whichever comes first
        size_t end = std::min(findUse(out, q, reset, typeEnd), out.find("\n}\n", typeEnd));
        if (end == std::string::npos) end = out.size();
        std::string body = out.substr(typeEnd, end - typeEnd);
        const std::pair<std::string, std::string> getters[] = {
            {".get_committed_primitive_id()", "int(" + r + ".primitive_id)"},
            {".get_committed_distance()", r + ".distance"},
            {".get_committed_user_instance_id()", "int(" + r + ".user_instance_id)"},
            {".get_committed_world_to_object_transform()", r + ".world_to_object_transform"},
            {".get_committed_triangle_barycentric_coord()", r + ".triangle_barycentric_coord"},
            {".get_world_space_ray_origin()", r + "_o"},
            {".get_world_space_ray_direction()", r + "_d"},
        };
        for (const auto& [getter, field] : getters)
            for (size_t g; (g = findUse(body, q, getter, 0)) != std::string::npos;) body.replace(g, q.size() + getter.size(), field);
        if (findUse(body, q, ".", 0) != std::string::npos) return msl;
        queries.insert(q);
        out = out.substr(0, qb) + code + body + out.substr(end);
        at = qb + code.size() + body.size();
    }
    // A rewritten query read anywhere else (a getter in another function, before its first reset) would read a query
    // that is no longer run
    for (const std::string& q : queries)
        if (findUse(out, q, ".", 0) != std::string::npos) return msl;
    return site ? out : msl;
}

// ---------------------------------------------------------------------------------------------------------------
// Pipelines

static const uint32_t kGroupHandleSize = 32;

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
    if (const char* dir = kmtlConfig().dumpSpv) {
        char name[64];
        snprintf(name, sizeof(name), "rt_%016llx.spv", (unsigned long long)module(raygen)->hash);
        kmtlDumpFile(dir, name, linked.data(), linked.size() * 4);
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
    KLOG(1, "ray tracing pipeline: %u groups, %zu miss shaders, closest-hit %s -> compute kernel %lux%lu",
         ci->groupCount, misses.size(), chit >= 0 ? "yes" : "no", (unsigned long)p->workgroup.width, (unsigned long)p->workgroup.height);
    return p;
}

VKFN(VkResult) vkCreateRayTracingPipelinesKHR(VkDevice device, VkDeferredOperationKHR, VkPipelineCache, uint32_t n,
                                              const VkRayTracingPipelineCreateInfoKHR* cis, const VkAllocationCallbacks*, VkPipeline* out) {
    KTRACE_FIRST();
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            out[i] = (VkPipeline)createRayTracingPipeline((KDevice*)device, &cis[i]);
        }
    }
    return kmtlFinishPipelines(n, out);
}

VKFN(VkResult) vkGetRayTracingShaderGroupHandlesKHR(VkDevice, VkPipeline, uint32_t first, uint32_t count, size_t size, void* data) {
    KTRACE_FIRST();
    if (size < (size_t)count * kGroupHandleSize) return VK_ERROR_OUT_OF_HOST_MEMORY;
    memset(data, 0, size);
    for (uint32_t i = 0; i < count; i++) {
        uint32_t h[2] = {0x5452534Bu /* "KSRT" */, first + i};
        memcpy((uint8_t*)data + (size_t)i * kGroupHandleSize, h, sizeof(h));
    }
    return VK_SUCCESS;
}

VKFN(VkDeviceSize) vkGetRayTracingShaderGroupStackSizeKHR(VkDevice, VkPipeline, uint32_t, VkShaderGroupShaderKHR) { return 0; }

VKFN(void) vkCmdSetRayTracingPipelineStackSizeKHR(VkCommandBuffer, uint32_t) {}

uint32_t kmtlRtGroupHandleSize() { return kGroupHandleSize; }
