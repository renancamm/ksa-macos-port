// Shader modules, SPIR-V -> MSL translation, render passes, framebuffers, graphics and compute pipelines.
#include "kmtl.h"
#include "spirv_msl.hpp"
#include "spirv_parser.hpp"
#include <sys/stat.h>
#include <memory>
#include <regex>
#include <functional>
#include <thread>

using namespace SPIRV_CROSS_NAMESPACE;

// Pipeline creation cost, reported with the first in-game frame (kmtlReportPipelineTimes)
static std::atomic<int64_t> g_usTranslate{0}, g_usCompile{0}, g_usPipeline{0};
static std::atomic<int> g_nTranslate{0}, g_nCompile{0}, g_nCompileCached{0}, g_nPipeline{0};
static int64_t usSince(std::chrono::steady_clock::time_point t) {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - t).count();
}
void kmtlReportPipelineTimes() {
    kmtlCacheFlushRecent();
    KLOG(0, "pipeline cost so far: SPIRV-Cross %d modules %.2f s, MSL compile %d (+%d cached) %.2f s, pipeline states %d %.2f s",
         g_nTranslate.load(), g_usTranslate / 1e6, g_nCompile.load(), g_nCompileCached.load(), g_usCompile / 1e6,
         g_nPipeline.load(), g_usPipeline / 1e6);
}

MTLCompareFunction kmtlCompare(VkCompareOp op);

uint64_t kmtlHash(const void* data, size_t size) {
    // FNV-1a 64
    uint64_t h = 1469598103934665603ull;
    auto* p = (const uint8_t*)data;
    for (size_t i = 0; i < size; i++) { h ^= p[i]; h *= 1099511628211ull; }
    return h;
}

// ---------------------------------------------------------------------------------------------------------------
// Shader modules

VKFN(VkResult) vkCreateShaderModule(VkDevice, const VkShaderModuleCreateInfo* ci, const VkAllocationCallbacks*, VkShaderModule* out) {
    KTRACE_FIRST();
    auto* m = new KShaderModule();
    m->spirv.assign(ci->pCode, ci->pCode + ci->codeSize / 4);
    m->hash = kmtlHash(ci->pCode, ci->codeSize);
    kmtlProfDumpSpirv(ci->pCode, ci->codeSize, m->hash);
    *out = (VkShaderModule)m;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyShaderModule(VkDevice, VkShaderModule m, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KShaderModule*)m;
}

// ---------------------------------------------------------------------------------------------------------------
// MSL compilation (cached per device by MSL text hash)

static void dumpMSL(const std::string& name, const std::string& msl, const std::string& err) {
    const char* dir = kmtlEnv("KSA_MTL_DUMP_MSL");
    if (!dir) return;
    mkdir(dir, 0755);
    std::string path = std::string(dir) + "/" + name + ".metal";
    if (FILE* f = fopen(path.c_str(), "w")) {
        fwrite(msl.data(), 1, msl.size(), f);
        if (!err.empty()) fprintf(f, "\n/* COMPILE ERROR:\n%s\n*/\n", err.c_str());
        fclose(f);
    }
}

// One compile per MSL text: a second caller (the game and the prewarm thread) waits for the first.
// KSA_MTL_MSL_SALT=<text> prepends a comment so every compile misses the system shader cache (cold-start tests)
id<MTLLibrary> kmtlCompileMSL(KDevice* dev, const std::string& msl, std::string* err) {
    uint64_t h = kmtlHash(msl.data(), msl.size());
    std::shared_ptr<KDevice::LibraryEntry> entry;
    bool owner = false;
    {
        std::lock_guard<std::mutex> lock(dev->libraryMutex);
        auto& slot = dev->libraryCache[h];
        if (!slot) { slot = std::make_shared<KDevice::LibraryEntry>(); owner = true; }
        entry = slot;
    }
    if (!owner) {
        std::unique_lock<std::mutex> lock(entry->m);
        entry->cv.wait(lock, [&] { return entry->done; });
        g_nCompileCached++;
        if (!entry->lib && err) *err = entry->err;
        return entry->lib;
    }
    auto t0 = std::chrono::steady_clock::now();
    MTLCompileOptions* o = [MTLCompileOptions new];
    o.languageVersion = MTLLanguageVersion3_2;
    o.mathMode = kmtlEnvInt("KSA_MTL_SAFE_MATH", 0) ? MTLMathModeSafe : MTLMathModeFast;
    o.preserveInvariance = YES;
    static const char* salt = kmtlEnv("KSA_MTL_MSL_SALT");
    std::string src = salt ? std::string("// ") + salt + "\n" + msl : msl;
    NSError* e = nil;
    id<MTLLibrary> lib = [dev->mtl newLibraryWithSource:[NSString stringWithUTF8String:src.c_str()] options:o error:&e];
    g_usCompile += usSince(t0);
    g_nCompile++;
    std::lock_guard<std::mutex> lock(entry->m);
    entry->lib = lib;
    if (!lib) entry->err = e ? e.description.UTF8String : "unknown error";
    if (!lib && err) *err = entry->err;
    entry->done = true;
    entry->cv.notify_all();
    return lib;
}

// ---------------------------------------------------------------------------------------------------------------
// SPIR-V -> MSL


// Per-stage translation options
enum class KTessRole { None, VertexKernel, ControlKernel, Evaluation };
struct KStageOptions {
    uint32_t fragMask = 0xffffffff;
    bool sampleRate = false;
    uint32_t viewMask = 0;
    KTessRole tess = KTessRole::None;
    CompilerMSL::Options::IndexType indexType = CompilerMSL::Options::IndexType::None;
    // Tessellation modes merged from both stages (each declares only part of them)
    uint32_t tessControlPoints = 0;
    bool tessQuads = false;
    bool rtLaunch = false;  // ray tracing kernel: launch size uniform at set 15 -> kRtLaunchBufferIndex
};

// Reads the tessellation execution modes of one stage module
static void tessModes(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KTranslated* out) {
    try {
        CompilerMSL c(mod->spirv);
        c.set_entry_point(entry, model);
        auto& modes = c.get_execution_mode_bitset();
        if (modes.get(spv::ExecutionModeOutputVertices)) out->outputVertices = c.get_execution_mode_argument(spv::ExecutionModeOutputVertices);
        if (modes.get(spv::ExecutionModeQuads)) { out->quads = true; out->hasDomain = true; }
        if (modes.get(spv::ExecutionModeTriangles)) { out->quads = false; out->hasDomain = true; }
        if (modes.get(spv::ExecutionModeVertexOrderCw)) { out->cw = true; out->hasWinding = true; }
        if (modes.get(spv::ExecutionModeVertexOrderCcw)) { out->cw = false; out->hasWinding = true; }
        if (modes.get(spv::ExecutionModeSpacingEqual)) { out->partition = MTLTessellationPartitionModeInteger; out->hasPartition = true; }
        if (modes.get(spv::ExecutionModeSpacingFractionalEven)) { out->partition = MTLTessellationPartitionModeFractionalEven; out->hasPartition = true; }
        if (modes.get(spv::ExecutionModeSpacingFractionalOdd)) { out->partition = MTLTessellationPartitionModeFractionalOdd; out->hasPartition = true; }
    } catch (const std::exception& e) {
        KERR("tessellation mode reflection: %s", e.what());
    }
}

static SPIRType::BaseType bindingBaseType(VkDescriptorType t) {
    switch (t) {
    case VK_DESCRIPTOR_TYPE_SAMPLER: return SPIRType::Sampler;
    case VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER: return SPIRType::SampledImage;
    case VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE:
    case VK_DESCRIPTOR_TYPE_STORAGE_IMAGE:
    case VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT:
    case VK_DESCRIPTOR_TYPE_UNIFORM_TEXEL_BUFFER:
    case VK_DESCRIPTOR_TYPE_STORAGE_TEXEL_BUFFER: return SPIRType::Image;
    default: return SPIRType::Void;  // buffers, acceleration structures: 8-byte pointers
    }
}

static uint32_t constantValue(CompilerMSL& c, uint32_t id) {
    return c.get_constant(id).scalar();
}

// A runtime-sized array of descriptors (images, samplers, acceleration structures) gets the descriptor count of its
// layout binding, as Vulkan sizes it. SPIRV-Cross declares a runtime array in an argument buffer as [1] but pads the
// buffer for the layout's count, so every later binding of the set is read one slot off per missing element (KSA's
// IVA TLAS array has one structure per frame in flight: its uniform, with the frame's TLAS index, was read from the
// second structure's slot, and every other frame traced the previous frame's TLAS)
static ParsedIR sizeRuntimeArrays(const std::vector<uint32_t>& spirv, KPipelineLayout* layout) {
    Parser parser(spirv);
    parser.parse();
    ParsedIR ir = std::move(parser.get_parsed_ir());
    if (!layout) return ir;
    std::unordered_map<uint32_t, uint32_t> sized;  // type id -> count given
    ir.for_each_typed_id<SPIRVariable>([&](uint32_t id, SPIRVariable& var) {
        if (var.storage != spv::StorageClassUniformConstant) return;
        // The pointer type carries a copy of the pointee's array sizes: both are sized
        auto& ptr = ir.ids[var.basetype].get<SPIRType>();
        if (!ptr.pointer || ptr.array.empty() || !ptr.array_size_literal.back()) return;
        auto& arr = ir.ids[ptr.parent_type].get<SPIRType>();
        if (arr.op != spv::OpTypeRuntimeArray && !sized.count(ptr.parent_type)) return;
        uint32_t set = ir.get_decoration(id, spv::DecorationDescriptorSet);
        uint32_t binding = ir.get_decoration(id, spv::DecorationBinding);
        const KBinding* b = set < layout->sets.size() && layout->sets[set] ? layout->sets[set]->find(binding) : nullptr;
        if (!b) return;
        for (uint32_t t : {(uint32_t)var.basetype, (uint32_t)ptr.parent_type}) {
            auto it = sized.find(t);
            if (it != sized.end() && it->second != b->count)
                KERR("runtime array type %u bound with %u and %u descriptors: sized %u", t, it->second, b->count, it->second);
            sized.emplace(t, b->count);
        }
        ptr.array.back() = sized[var.basetype];
        arr.array.back() = sized[ptr.parent_type];
        arr.op = spv::OpTypeArray;
    });
    return ir;
}

static bool translate(KShaderModule* mod, const char* entryName, spv::ExecutionModel model, KPipelineLayout* layout,
                      const VkSpecializationInfo* spec, const KStageOptions& so, KTranslated* out, std::string* err) {
    uint32_t fragOutputMask = so.fragMask, viewMask = so.viewMask;
    bool sampleRate = so.sampleRate;
    auto t0 = std::chrono::steady_clock::now();
    struct Timer { std::chrono::steady_clock::time_point t; ~Timer() { g_usTranslate += usSince(t); g_nTranslate++; } } timer{t0};
    try {
        CompilerMSL c(sizeRuntimeArrays(mod->spirv, layout));
        c.set_entry_point(entryName, model);
        if (so.tess == KTessRole::ControlKernel || so.tess == KTessRole::Evaluation) {
            c.set_execution_mode(so.tessQuads ? spv::ExecutionModeQuads : spv::ExecutionModeTriangles);
            if (so.tessControlPoints) c.set_execution_mode(spv::ExecutionModeOutputVertices, so.tessControlPoints);
        }

        CompilerMSL::Options mo;
        mo.platform = CompilerMSL::Options::macOS;
        mo.set_msl_version(3, 2);
        mo.argument_buffers = true;
        mo.argument_buffers_tier = CompilerMSL::Options::ArgumentBuffersTier::Tier2;
        mo.pad_argument_buffer_resources = true;
        mo.texture_buffer_native = true;
        mo.swizzle_buffer_index = kSwizzleBufferIndex;
        mo.buffer_size_buffer_index = kBufferSizeBufferIndex;
        mo.view_mask_buffer_index = kViewMaskBufferIndex;
        mo.dynamic_offsets_buffer_index = kDynamicOffsetBufferIndex;
        mo.draw_id_buffer_index = kDrawIdBufferIndex;
        mo.agx_manual_cube_grad_fixup = true;
        mo.enable_frag_output_mask = fragOutputMask;
        mo.force_sample_rate_shading = sampleRate;
        // Multiview: the driver draws once per view with spvViewMask = {view, 1} (indirect draws cannot have their
        // instance count multiplied), and the layer assignment is patched below to use the view index directly
        mo.multiview = viewMask != 0;
        mo.multiview_layered_rendering = true;
        // Tessellation (Metal has no hull stage): vertex and control stages run as compute kernels writing buffers,
        // the evaluation stage is a post-tessellation vertex function reading them
        switch (so.tess) {
        case KTessRole::VertexKernel:
            mo.vertex_for_tessellation = true;
            mo.capture_output_to_buffer = true;
            mo.disable_rasterization = true;
            mo.shader_output_buffer_index = kTessOutputBufferIndex;
            mo.shader_index_buffer_index = kTessIndexBufferIndex;
            mo.vertex_index_type = so.indexType;
            break;
        case KTessRole::ControlKernel:
            mo.capture_output_to_buffer = true;
            mo.multi_patch_workgroup = true;
            mo.shader_input_buffer_index = kTessInputBufferIndex;
            mo.shader_output_buffer_index = kTessOutputBufferIndex;
            mo.shader_patch_output_buffer_index = kTessPatchBufferIndex;
            mo.shader_tess_factor_buffer_index = kTessFactorBufferIndex;
            mo.indirect_params_buffer_index = kTessParamsBufferIndex;
            mo.fixed_subgroup_size = 32;
            break;
        case KTessRole::Evaluation:
            mo.raw_buffer_tese_input = true;
            mo.shader_input_buffer_index = kTessInputBufferIndex;
            mo.shader_patch_input_buffer_index = kTessPatchBufferIndex;
            mo.shader_tess_factor_buffer_index = kTessFactorBufferIndex;
            break;
        default:
            break;
        }
        c.set_msl_options(mo);

        // Y is flipped in the last stage before rasterization
        CompilerGLSL::Options co = c.get_common_options();
        co.vertex.flip_vert_y = (model == spv::ExecutionModelVertex && so.tess == KTessRole::None) ||
                                so.tess == KTessRole::Evaluation;
        c.set_common_options(co);

        if (layout) {
            for (uint32_t s = 0; s < layout->sets.size(); s++) {
                KSetLayout* sl = layout->sets[s];
                if (!sl) continue;
                c.set_argument_buffer_device_address_space(s, true);
                MSLResourceBinding ab;
                ab.stage = model;
                ab.desc_set = s;
                ab.binding = kArgumentBufferBinding;
                ab.basetype = SPIRType::Void;  // padding is on: every binding needs a base type
                ab.msl_buffer = s;
                c.add_msl_resource_binding(ab);
                for (auto& b : sl->bindings) {
                    MSLResourceBinding rb;
                    rb.stage = model;
                    rb.desc_set = s;
                    rb.binding = b.binding;
                    rb.count = b.count;
                    rb.basetype = bindingBaseType(b.type);
                    rb.msl_buffer = rb.msl_texture = rb.msl_sampler = b.id;
                    if (b.type == VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER) rb.msl_sampler = b.samplerId;
                    c.add_msl_resource_binding(rb);
                }
                MSLResourceBinding sz;
                sz.stage = model;
                sz.desc_set = s;
                sz.binding = kBufferSizeBufferBinding;
                sz.count = 1;
                sz.basetype = SPIRType::UInt;
                sz.msl_buffer = sl->sizeSlot;
                c.add_msl_resource_binding(sz);
            }
        }
        if (so.rtLaunch) {
            c.add_discrete_descriptor_set(15);
            MSLResourceBinding ls;
            ls.stage = model;
            ls.desc_set = 15;
            ls.binding = 0;
            ls.count = 1;
            ls.basetype = SPIRType::Void;
            ls.msl_buffer = kRtLaunchBufferIndex;
            c.add_msl_resource_binding(ls);
        }
        MSLResourceBinding pc;
        pc.stage = model;
        pc.desc_set = kPushConstDescSet;
        pc.binding = kPushConstBinding;
        pc.basetype = SPIRType::Void;
        pc.msl_buffer = kPushConstantBufferIndex;
        c.add_msl_resource_binding(pc);

        // Specialization: overwrite the default values in the IR (SPIRV-Cross's function-constant fallbacks use them)
        if (spec) {
            for (auto& sc : c.get_specialization_constants()) {
                for (uint32_t i = 0; i < spec->mapEntryCount; i++) {
                    const auto& me = spec->pMapEntries[i];
                    if (me.constantID != sc.constant_id) continue;
                    SPIRConstant& k = c.get_constant(sc.id);
                    const uint8_t* src = (const uint8_t*)spec->pData + me.offset;
                    if (me.size == 8) memcpy(&k.m.c[0].r[0].u64, src, 8);
                    else if (me.size == 4) memcpy(&k.m.c[0].r[0].u32, src, 4);
                    else if (me.size == 2) { uint16_t v; memcpy(&v, src, 2); k.m.c[0].r[0].u32 = v; }
                    else if (me.size == 1) k.m.c[0].r[0].u32 = *src;
                }
            }
        }

        // Quality presets (KSA_MTL_SPEC=<module hash>:<constant id>=<value>[,...]): replace a specialization constant
        // of one module whatever the game passes, e.g. fewer iterations of an expensive loop
        if (const char* presets = kmtlEnv("KSA_MTL_SPEC")) {
            char want[24];
            snprintf(want, sizeof(want), "%016llx:", (unsigned long long)mod->hash);
            for (const char* q = strstr(presets, want); q; q = strstr(q + 1, want)) {
                unsigned id = 0, value = 0;
                if (sscanf(q + strlen(want), "%u=%u", &id, &value) != 2) continue;
                for (auto& sc : c.get_specialization_constants())
                    if (sc.constant_id == id) {
                        c.get_constant(sc.id).m.c[0].r[0].u32 = value;
                    }
            }
        }

        out->msl = c.compile();
        if (viewMask) {
            // "layer = gl_ViewIndex - spvViewMask[0];" -> "layer = gl_ViewIndex;" and drop the fragment re-offset
            std::string& m = out->msl;
            for (size_t pos; (pos = m.find(" - spvViewMask[0];")) != std::string::npos;) m.replace(pos, 18, ";");
            for (size_t pos; (pos = m.find(" += spvViewMask[0];")) != std::string::npos;) m.replace(pos, 19, " += 0;");
        }
        // SPIRV-Cross declares an array of acceleration structures in an argument buffer as a C array but passes it to
        // helper functions as spvUnsafeArray&: take it as a device pointer instead (the array decays to one)
        {
            static const std::regex asArray(
                R"((?:thread )?const (?:device )?spvUnsafeArray<(raytracing::acceleration_structure<[^>]*>), \d+>(?: const device)?& (\w+))");
            if (out->msl.find("spvUnsafeArray<raytracing::acceleration_structure") != std::string::npos)
                out->msl = std::regex_replace(out->msl, asArray, "const device $1* $2");
        }
        out->entry = c.get_cleansed_entry_point_name(entryName, model);
        if (so.tess == KTessRole::ControlKernel || so.tess == KTessRole::Evaluation) {
            auto& modes = c.get_execution_mode_bitset();
            if (modes.get(spv::ExecutionModeOutputVertices))
                out->outputVertices = c.get_execution_mode_argument(spv::ExecutionModeOutputVertices);
            if (modes.get(spv::ExecutionModeQuads)) { out->quads = true; out->hasDomain = true; }
            if (modes.get(spv::ExecutionModeTriangles)) { out->quads = false; out->hasDomain = true; }
            if (modes.get(spv::ExecutionModeVertexOrderCw)) { out->cw = true; out->hasWinding = true; }
            if (modes.get(spv::ExecutionModeVertexOrderCcw)) { out->cw = false; out->hasWinding = true; }
            if (modes.get(spv::ExecutionModeSpacingEqual)) { out->partition = MTLTessellationPartitionModeInteger; out->hasPartition = true; }
            if (modes.get(spv::ExecutionModeSpacingFractionalEven)) { out->partition = MTLTessellationPartitionModeFractionalEven; out->hasPartition = true; }
            if (modes.get(spv::ExecutionModeSpacingFractionalOdd)) { out->partition = MTLTessellationPartitionModeFractionalOdd; out->hasPartition = true; }
        }
        out->usesDrawId = out->msl.find("spvDrawIndex") != std::string::npos;

        if (model == spv::ExecutionModelGLCompute) {
            SpecializationConstant x, y, z;
            c.get_work_group_size_specialization_constants(x, y, z);
            uint32_t wx = c.get_execution_mode_argument(spv::ExecutionModeLocalSize, 0);
            uint32_t wy = c.get_execution_mode_argument(spv::ExecutionModeLocalSize, 1);
            uint32_t wz = c.get_execution_mode_argument(spv::ExecutionModeLocalSize, 2);
            auto& modes = c.get_execution_mode_bitset();
            if (modes.get(spv::ExecutionModeLocalSizeId)) {
                wx = constantValue(c, c.get_execution_mode_argument(spv::ExecutionModeLocalSizeId, 0));
                wy = constantValue(c, c.get_execution_mode_argument(spv::ExecutionModeLocalSizeId, 1));
                wz = constantValue(c, c.get_execution_mode_argument(spv::ExecutionModeLocalSizeId, 2));
            }
            if (x.id) wx = constantValue(c, x.id);
            if (y.id) wy = constantValue(c, y.id);
            if (z.id) wz = constantValue(c, z.id);
            out->workgroup = MTLSizeMake(std::max(1u, wx), std::max(1u, wy), std::max(1u, wz));
        }
        return true;
    } catch (const std::exception& e) {
        *err = std::string("SPIRV-Cross: ") + e.what();
        return false;
    }
}

static char stageChar(spv::ExecutionModel m, KTessRole role) {
    if (role == KTessRole::VertexKernel) return 'k';
    switch (m) {
    case spv::ExecutionModelTessellationControl: return 'h';
    case spv::ExecutionModelTessellationEvaluation: return 'd';
    case spv::ExecutionModelVertex: return 'v';
    case spv::ExecutionModelFragment: return 'f';
    case spv::ExecutionModelGLCompute: return 'c';
    default: return 'x';
    }
}

static bool stripPassthroughControl(std::string& msl);

// Hand-written replacements for translated shaders (KSA_MTL_OVERRIDES=<dir>): <dir>/<translation key>.metal for one
// exact translation, or <dir>/<module hash>_<stage><index type>.metal (the KSA_MTL_DUMP_MSL file name) for every
// translation of a module stage. The replacement keeps the translated interface: same entry point name, same
// argument buffer bindings (the set layouts fix them)
static void applyOverride(const char* name, uint64_t key, KTranslated* tr) {
    static const char* dir = kmtlEnv("KSA_MTL_OVERRIDES");
    if (!dir) return;
    char path[1200];
    snprintf(path, sizeof(path), "%s/%016llx.metal", dir, (unsigned long long)key);
    FILE* f = fopen(path, "rb");
    if (!f) {
        snprintf(path, sizeof(path), "%s/%s.metal", dir, name);
        f = fopen(path, "rb");
    }
    if (!f) return;
    std::string msl;
    char buf[65536];
    for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) msl.append(buf, n);
    fclose(f);
    tr->msl = msl;
    KLOG(0, "shader override: %s", path);
}

static bool passthroughEnabled() {
    static const bool v = kmtlEnvInt("KSA_MTL_TESS_PASSTHROUGH", 1) != 0;
    return v;
}

// Everything that shapes a stage's translation: the module, entry, stage options, the slot map of every set,
// specialization data, and the translator version. Bump kTranslatorVersion when translate() or its MSL patches change
static uint64_t translationKey(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KPipelineLayout* layout,
                               const VkSpecializationInfo* spec, const KStageOptions& so) {
    static const uint32_t kTranslatorVersion = 3;
    std::vector<uint32_t> k = {kTranslatorVersion, 0x300681bbu /* SPIRV-Cross pin */, (uint32_t)model, so.fragMask,
        so.sampleRate, so.viewMask, (uint32_t)so.tess, (uint32_t)so.indexType, so.tessControlPoints, so.tessQuads,
        passthroughEnabled(), (uint32_t)mod->hash, (uint32_t)(mod->hash >> 32), so.rtLaunch};
    if (const char* presets = kmtlEnv("KSA_MTL_SPEC")) k.push_back((uint32_t)kmtlHash(presets, strlen(presets)));
    for (const char* c = entry; *c; c++) k.push_back((uint8_t)*c);
    if (layout) {
        for (KSetLayout* sl : layout->sets) {
            k.push_back(0xFFFFFFFFu);
            if (!sl) continue;
            k.push_back(sl->sizeSlot);
            for (auto& b : sl->bindings) { k.push_back(b.binding); k.push_back(b.type); k.push_back(b.count); k.push_back(b.id); k.push_back(b.samplerId); }
        }
    }
    if (spec) {
        for (uint32_t i = 0; i < spec->mapEntryCount; i++) {
            const auto& me = spec->pMapEntries[i];
            k.push_back(me.constantID);
            k.push_back((uint32_t)me.size);
            uint64_t v = 0;
            memcpy(&v, (const uint8_t*)spec->pData + me.offset, std::min<size_t>(me.size, 8));
            k.push_back((uint32_t)v);
            k.push_back((uint32_t)(v >> 32));
        }
    }
    return kmtlHash(k.data(), k.size() * 4);
}

// Translates and compiles one stage; returns the MTLFunction or nil (errors logged, MSL dumped)
// Translation of one stage (SPIRV-Cross or the disk cache, then overrides): needs the create info, so it runs on the
// calling thread. *name is the stage's dump/override name
static bool prepareStage(const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model, KPipelineLayout* layout,
                         const KStageOptions& so, KTranslated* tr, std::string* outName) {
    auto* mod = (KShaderModule*)st->module;
    if (!mod) {
        KERR("pipeline stage without a module (inline SPIR-V not supported)");
        return false;
    }
    std::string err;
    char name[64];
    snprintf(name, sizeof(name), "%016llx_%c%d", (unsigned long long)mod->hash, stageChar(model, so.tess), (int)so.indexType);
    *outName = name;
    uint64_t key = translationKey(mod, st->pName, model, layout, st->pSpecializationInfo, so);
    if (!kmtlCacheLoad(key, tr)) {
        if (!translate(mod, st->pName, model, layout, st->pSpecializationInfo, so, tr, &err)) {
            KERR("shader %s: %s", name, err.c_str());
            dumpMSL(name, "", err);
            return false;
        }
        if (so.tess == KTessRole::ControlKernel && passthroughEnabled() && stripPassthroughControl(tr->msl))
            tr->passthrough = true;
        kmtlCacheStore(key, *tr);
    }
    kmtlCacheNoteUsed(key);
    applyOverride(name, key, tr);
    return true;
}

// MSL compile and function of a prepared stage: thread-safe, so asynchronous pipeline creation runs it on workers
static id<MTLFunction> compileStage(KDevice* dev, const KTranslated& t, const std::string& nameStr) {
    const KTranslated* tr = &t;
    const char* name = nameStr.c_str();
    std::string err;
    id<MTLLibrary> lib = kmtlCompileMSL(dev, tr->msl, &err);
    dumpMSL(name, tr->msl, err);
    if (!lib) {
        KERR("shader %s: MSL compile failed: %s", name, err.c_str());
        return nil;
    }
    NSString* entry = [NSString stringWithUTF8String:tr->entry.c_str()];
    id<MTLFunction> fn = [lib newFunctionWithName:entry];
    if (!fn) {
        KERR("shader %s: entry point %s not found", name, tr->entry.c_str());
        return nil;
    }
    if (fn.functionConstantsDictionary.count) {
        // Specialization constants are baked into the IR defaults; leaving the function constants undefined
        // selects those defaults in SPIRV-Cross's output
        NSError* e = nil;
        fn = [lib newFunctionWithName:entry constantValues:[MTLFunctionConstantValues new] error:&e];
        if (!fn) KERR("shader %s: specialization failed: %s", name, e.description.UTF8String);
    }
    return fn;
}

// Asynchronous pipeline creation (KSA_MTL_ASYNC_PIPELINES=0 turns it off): KSA creates its pipelines one at a time,
// so on a cold shader cache the MSL compiles run serially; workers compile them in parallel instead, and the first
// bind of a pipeline waits for its compile
static bool asyncPipelines() {
    static const bool v = kmtlEnvInt("KSA_MTL_ASYNC_PIPELINES", 1) != 0;
    return v;
}

static void runPipelineJob(std::function<void()> job) {
    static dispatch_queue_t queue = dispatch_queue_create("ksamtl.pipelines", DISPATCH_QUEUE_CONCURRENT);
    // Leave two cores to the game
    static dispatch_semaphore_t slots = dispatch_semaphore_create(std::max(1, (int)std::thread::hardware_concurrency() - 2));
    dispatch_async(queue, ^{
        dispatch_semaphore_wait(slots, DISPATCH_TIME_FOREVER);
        @autoreleasepool { job(); }
        dispatch_semaphore_signal(slots);
    });
}

static id<MTLFunction> makeFunction(KDevice* dev, const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model,
                                    KPipelineLayout* layout, const KStageOptions& so, KTranslated* tr) {
    std::string name;
    if (!prepareStage(st, model, layout, so, tr, &name)) return nil;
    return compileStage(dev, *tr, name);
}

// ---------------------------------------------------------------------------------------------------------------
// Render passes and framebuffers

static void addSubpass(KRenderPass* rp, uint32_t colorCount, const VkAttachmentReference* colors,
                       const VkAttachmentReference* resolves, const VkAttachmentReference* depth, uint32_t viewMask) {
    KRenderPass::Subpass sp;
    for (uint32_t i = 0; i < colorCount; i++) {
        sp.colors.push_back(colors[i].attachment == VK_ATTACHMENT_UNUSED ? -1 : (int)colors[i].attachment);
        sp.resolves.push_back(resolves && resolves[i].attachment != VK_ATTACHMENT_UNUSED ? (int)resolves[i].attachment : -1);
    }
    sp.depth = depth && depth->attachment != VK_ATTACHMENT_UNUSED ? (int)depth->attachment : -1;
    sp.viewMask = viewMask;
    rp->subpasses.push_back(sp);
}

VKFN(VkResult) vkCreateRenderPass(VkDevice, const VkRenderPassCreateInfo* ci, const VkAllocationCallbacks*, VkRenderPass* out) {
    KTRACE_FIRST();
    auto* rp = new KRenderPass();
    for (uint32_t i = 0; i < ci->attachmentCount; i++) {
        const auto& a = ci->pAttachments[i];
        VkAttachmentDescription2 a2{VK_STRUCTURE_TYPE_ATTACHMENT_DESCRIPTION_2};
        a2.flags = a.flags;
        a2.format = a.format;
        a2.samples = a.samples;
        a2.loadOp = a.loadOp;
        a2.storeOp = a.storeOp;
        a2.stencilLoadOp = a.stencilLoadOp;
        a2.stencilStoreOp = a.stencilStoreOp;
        a2.initialLayout = a.initialLayout;
        a2.finalLayout = a.finalLayout;
        rp->attachments.push_back(a2);
    }
    auto* mv = (const VkRenderPassMultiviewCreateInfo*)findNext(ci->pNext, VK_STRUCTURE_TYPE_RENDER_PASS_MULTIVIEW_CREATE_INFO);
    for (uint32_t i = 0; i < ci->subpassCount; i++) {
        const auto& s = ci->pSubpasses[i];
        addSubpass(rp, s.colorAttachmentCount, s.pColorAttachments, s.pResolveAttachments, s.pDepthStencilAttachment,
                   mv && i < mv->subpassCount ? mv->pViewMasks[i] : 0);
    }
    if (ci->subpassCount > 1) KLOG(1, "render pass with %u subpasses: only the first is supported", ci->subpassCount);
    *out = (VkRenderPass)rp;
    return VK_SUCCESS;
}

VKFN(VkResult) vkCreateRenderPass2(VkDevice, const VkRenderPassCreateInfo2* ci, const VkAllocationCallbacks*, VkRenderPass* out) {
    KTRACE_FIRST();
    auto* rp = new KRenderPass();
    for (uint32_t i = 0; i < ci->attachmentCount; i++) {
        VkAttachmentDescription2 a = ci->pAttachments[i];
        a.pNext = nullptr;
        rp->attachments.push_back(a);
    }
    for (uint32_t i = 0; i < ci->subpassCount; i++) {
        const auto& s = ci->pSubpasses[i];
        std::vector<VkAttachmentReference> colors, resolves;
        for (uint32_t j = 0; j < s.colorAttachmentCount; j++) {
            colors.push_back({s.pColorAttachments[j].attachment, s.pColorAttachments[j].layout});
            if (s.pResolveAttachments) resolves.push_back({s.pResolveAttachments[j].attachment, s.pResolveAttachments[j].layout});
        }
        VkAttachmentReference depth{VK_ATTACHMENT_UNUSED};
        if (s.pDepthStencilAttachment) depth.attachment = s.pDepthStencilAttachment->attachment;
        addSubpass(rp, s.colorAttachmentCount, colors.data(), s.pResolveAttachments ? resolves.data() : nullptr, &depth, s.viewMask);
    }
    *out = (VkRenderPass)rp;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyRenderPass(VkDevice, VkRenderPass rp, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KRenderPass*)rp;
}

VKFN(VkResult) vkCreateFramebuffer(VkDevice, const VkFramebufferCreateInfo* ci, const VkAllocationCallbacks*, VkFramebuffer* out) {
    KTRACE_FIRST();
    auto* fb = new KFramebuffer();
    if (!(ci->flags & VK_FRAMEBUFFER_CREATE_IMAGELESS_BIT))
        for (uint32_t i = 0; i < ci->attachmentCount; i++) fb->attachments.push_back((KImageView*)ci->pAttachments[i]);
    fb->width = ci->width;
    fb->height = ci->height;
    fb->layers = ci->layers;
    *out = (VkFramebuffer)fb;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyFramebuffer(VkDevice, VkFramebuffer fb, const VkAllocationCallbacks*) {
    delete (KFramebuffer*)fb;
}

VKFN(void) vkGetRenderAreaGranularity(VkDevice, VkRenderPass, VkExtent2D* g) { *g = {1, 1}; }

// ---------------------------------------------------------------------------------------------------------------
// Graphics pipelines

static MTLBlendFactor blendFactor(VkBlendFactor f) {
    switch (f) {
    case VK_BLEND_FACTOR_ZERO: return MTLBlendFactorZero;
    case VK_BLEND_FACTOR_ONE: return MTLBlendFactorOne;
    case VK_BLEND_FACTOR_SRC_COLOR: return MTLBlendFactorSourceColor;
    case VK_BLEND_FACTOR_ONE_MINUS_SRC_COLOR: return MTLBlendFactorOneMinusSourceColor;
    case VK_BLEND_FACTOR_DST_COLOR: return MTLBlendFactorDestinationColor;
    case VK_BLEND_FACTOR_ONE_MINUS_DST_COLOR: return MTLBlendFactorOneMinusDestinationColor;
    case VK_BLEND_FACTOR_SRC_ALPHA: return MTLBlendFactorSourceAlpha;
    case VK_BLEND_FACTOR_ONE_MINUS_SRC_ALPHA: return MTLBlendFactorOneMinusSourceAlpha;
    case VK_BLEND_FACTOR_DST_ALPHA: return MTLBlendFactorDestinationAlpha;
    case VK_BLEND_FACTOR_ONE_MINUS_DST_ALPHA: return MTLBlendFactorOneMinusDestinationAlpha;
    case VK_BLEND_FACTOR_CONSTANT_COLOR: return MTLBlendFactorBlendColor;
    case VK_BLEND_FACTOR_ONE_MINUS_CONSTANT_COLOR: return MTLBlendFactorOneMinusBlendColor;
    case VK_BLEND_FACTOR_CONSTANT_ALPHA: return MTLBlendFactorBlendAlpha;
    case VK_BLEND_FACTOR_ONE_MINUS_CONSTANT_ALPHA: return MTLBlendFactorOneMinusBlendAlpha;
    case VK_BLEND_FACTOR_SRC_ALPHA_SATURATE: return MTLBlendFactorSourceAlphaSaturated;
    case VK_BLEND_FACTOR_SRC1_COLOR: return MTLBlendFactorSource1Color;
    case VK_BLEND_FACTOR_ONE_MINUS_SRC1_COLOR: return MTLBlendFactorOneMinusSource1Color;
    case VK_BLEND_FACTOR_SRC1_ALPHA: return MTLBlendFactorSource1Alpha;
    case VK_BLEND_FACTOR_ONE_MINUS_SRC1_ALPHA: return MTLBlendFactorOneMinusSource1Alpha;
    default: return MTLBlendFactorOne;
    }
}

static MTLBlendOperation blendOp(VkBlendOp op) {
    switch (op) {
    case VK_BLEND_OP_SUBTRACT: return MTLBlendOperationSubtract;
    case VK_BLEND_OP_REVERSE_SUBTRACT: return MTLBlendOperationReverseSubtract;
    case VK_BLEND_OP_MIN: return MTLBlendOperationMin;
    case VK_BLEND_OP_MAX: return MTLBlendOperationMax;
    default: return MTLBlendOperationAdd;
    }
}

static MTLStencilOperation stencilOp(VkStencilOp op) {
    switch (op) {
    case VK_STENCIL_OP_ZERO: return MTLStencilOperationZero;
    case VK_STENCIL_OP_REPLACE: return MTLStencilOperationReplace;
    case VK_STENCIL_OP_INCREMENT_AND_CLAMP: return MTLStencilOperationIncrementClamp;
    case VK_STENCIL_OP_DECREMENT_AND_CLAMP: return MTLStencilOperationDecrementClamp;
    case VK_STENCIL_OP_INVERT: return MTLStencilOperationInvert;
    case VK_STENCIL_OP_INCREMENT_AND_WRAP: return MTLStencilOperationIncrementWrap;
    case VK_STENCIL_OP_DECREMENT_AND_WRAP: return MTLStencilOperationDecrementWrap;
    default: return MTLStencilOperationKeep;
    }
}

static MTLStencilDescriptor* stencilDesc(const VkStencilOpState& s) {
    MTLStencilDescriptor* d = [MTLStencilDescriptor new];
    d.stencilCompareFunction = kmtlCompare(s.compareOp);
    d.stencilFailureOperation = stencilOp(s.failOp);
    d.depthFailureOperation = stencilOp(s.depthFailOp);
    d.depthStencilPassOperation = stencilOp(s.passOp);
    d.readMask = s.compareMask;
    d.writeMask = s.writeMask;
    return d;
}

struct KAttachmentFormats {
    std::vector<MTLPixelFormat> colors;
    MTLPixelFormat depth = MTLPixelFormatInvalid;
    MTLPixelFormat stencil = MTLPixelFormatInvalid;
    uint32_t viewMask = 0;
};

static KAttachmentFormats attachmentFormats(const VkGraphicsPipelineCreateInfo* ci) {
    KAttachmentFormats f;
    if (ci->renderPass) {
        auto* rp = (KRenderPass*)ci->renderPass;
        const auto& sp = rp->subpasses[ci->subpass < rp->subpasses.size() ? ci->subpass : 0];
        for (int c : sp.colors) f.colors.push_back(c >= 0 ? kmtlPixelFormat(rp->attachments[c].format) : MTLPixelFormatInvalid);
        if (sp.depth >= 0) {
            const KFormatInfo* fi = kmtlFormat(rp->attachments[sp.depth].format);
            if (fi && fi->depth) f.depth = fi->mtl;
            if (fi && fi->stencil) f.stencil = fi->mtl;
        }
        f.viewMask = sp.viewMask;
    } else if (auto* ri = (const VkPipelineRenderingCreateInfo*)findNext(ci->pNext, VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO)) {
        for (uint32_t i = 0; i < ri->colorAttachmentCount; i++) f.colors.push_back(kmtlPixelFormat(ri->pColorAttachmentFormats[i]));
        if (ri->depthAttachmentFormat) f.depth = kmtlPixelFormat(ri->depthAttachmentFormat);
        if (ri->stencilAttachmentFormat) f.stencil = kmtlPixelFormat(ri->stencilAttachmentFormat);
        f.viewMask = ri->viewMask;
    }
    return f;
}

static MTLPrimitiveTopologyClass topologyClass(VkPrimitiveTopology t) {
    switch (t) {
    case VK_PRIMITIVE_TOPOLOGY_POINT_LIST: return MTLPrimitiveTopologyClassPoint;
    case VK_PRIMITIVE_TOPOLOGY_LINE_LIST: case VK_PRIMITIVE_TOPOLOGY_LINE_STRIP: return MTLPrimitiveTopologyClassLine;
    default: return MTLPrimitiveTopologyClassTriangle;
    }
}

static MTLPrimitiveType primitiveType(VkPrimitiveTopology t) {
    switch (t) {
    case VK_PRIMITIVE_TOPOLOGY_POINT_LIST: return MTLPrimitiveTypePoint;
    case VK_PRIMITIVE_TOPOLOGY_LINE_LIST: return MTLPrimitiveTypeLine;
    case VK_PRIMITIVE_TOPOLOGY_LINE_STRIP: return MTLPrimitiveTypeLineStrip;
    case VK_PRIMITIVE_TOPOLOGY_TRIANGLE_STRIP: return MTLPrimitiveTypeTriangleStrip;
    default: return MTLPrimitiveTypeTriangle;
    }
}

// Stride of the struct a tessellation kernel writes at `index`, from pipeline reflection
static NSUInteger outputStride(MTLComputePipelineReflection* refl, NSUInteger index) {
    for (id<MTLBinding> b in refl.bindings) {
        if (b.type == MTLBindingTypeBuffer && b.index == index) return ((id<MTLBufferBinding>)b).bufferDataSize;
    }
    return 0;
}

// Passthrough control stage (on by default, KSA_MTL_TESS_PASSTHROUGH=0 turns it off): when every control-point output is copied from the same
// input of the same type in the same order, the copies are removed from the control kernel and the evaluation stage
// reads the vertex kernel's output directly (control point k of the draw is vertex-kernel output k)
static std::vector<std::string> structFieldTypes(const std::string& msl, const std::string& name, std::vector<std::string>* names) {
    std::vector<std::string> types;
    size_t p = msl.find("struct " + name + "\n{");
    if (p == std::string::npos) return types;
    size_t e = msl.find("};", p);
    std::regex field(R"(^\s+([^;]+?)\s+(\w+)(\s*\[\[[^\]]*\]\])?;\s*$)");
    std::string body = msl.substr(p, e - p);
    size_t pos = 0;
    while (pos < body.size()) {
        size_t nl = body.find('\n', pos);
        std::string line = body.substr(pos, nl == std::string::npos ? std::string::npos : nl - pos);
        std::smatch m;
        if (std::regex_match(line, m, field)) { types.push_back(m[1]); names->push_back(m[2]); }
        if (nl == std::string::npos) break;
        pos = nl + 1;
    }
    return types;
}

static bool stripPassthroughControl(std::string& msl) {
    std::vector<std::string> outNames, inNames;
    auto outTypes = structFieldTypes(msl, "main0_out", &outNames);
    auto inTypes = structFieldTypes(msl, "main0_in", &inNames);
    if (outTypes.empty() || outTypes != inTypes) return false;
    std::regex copy(R"(\n\s*gl_out\[gl_InvocationID\]\.(\w+) = gl_in\[gl_InvocationID\]\.(\w+);)");
    std::vector<bool> seen(outNames.size(), false);
    std::string stripped;
    size_t last = 0;
    for (auto it = std::sregex_iterator(msl.begin(), msl.end(), copy); it != std::sregex_iterator(); ++it) {
        auto o = std::find(outNames.begin(), outNames.end(), (*it)[1].str()) - outNames.begin();
        auto i = std::find(inNames.begin(), inNames.end(), (*it)[2].str()) - inNames.begin();
        if (o != i || o >= (long)outNames.size()) return false;
        seen[o] = true;
        stripped += msl.substr(last, it->position() - last);
        last = it->position() + it->length();
    }
    stripped += msl.substr(last);
    for (bool s : seen) if (!s) return false;
    // Any other use of gl_out means the stage computes outputs
    size_t uses = 0;
    for (size_t q = stripped.find("gl_out"); q != std::string::npos; q = stripped.find("gl_out", q + 1)) uses++;
    if (uses != 1) return false;  // only its declaration is left
    msl = stripped;
    return true;
}

// Builds the compute stages of a tessellation pipeline: the vertex shader as three kernels (by index type) with a
// stage-in descriptor for its vertex attributes, and the control shader as a multi-patch kernel
static bool buildTessKernels(KDevice* dev, const VkGraphicsPipelineCreateInfo* ci, const VkPipelineShaderStageCreateInfo* vs,
                             const VkPipelineShaderStageCreateInfo* tcs, KPipelineLayout* layout, KPipeline* p, KTranslated* htr,
                             const KTranslated& modes) {
    static const CompilerMSL::Options::IndexType types[3] = {CompilerMSL::Options::IndexType::None,
        CompilerMSL::Options::IndexType::UInt16, CompilerMSL::Options::IndexType::UInt32};
    for (int t = 0; t < 3; t++) {
        KTranslated vtr;
        KStageOptions so;
        so.tess = KTessRole::VertexKernel;
        so.indexType = types[t];
        id<MTLFunction> fn = makeFunction(dev, vs, spv::ExecutionModelVertex, layout, so, &vtr);
        if (!fn) return false;
        MTLComputePipelineDescriptor* d = [MTLComputePipelineDescriptor new];
        d.computeFunction = fn;
        MTLStageInputOutputDescriptor* sd = [MTLStageInputOutputDescriptor stageInputOutputDescriptor];
        if (const auto* vi = ci->pVertexInputState) {
            for (uint32_t i = 0; i < vi->vertexBindingDescriptionCount; i++) {
                const auto& b = vi->pVertexBindingDescriptions[i];
                MTLBufferLayoutDescriptor* l = sd.layouts[vertexBufferIndex(b.binding)];
                l.stride = b.stride ? b.stride : 16;
                if (b.stride == 0) { l.stepFunction = MTLStepFunctionConstant; l.stepRate = 0; }
                else if (b.inputRate == VK_VERTEX_INPUT_RATE_INSTANCE) { l.stepFunction = MTLStepFunctionThreadPositionInGridY; l.stepRate = 1; }
                else { l.stepFunction = t ? MTLStepFunctionThreadPositionInGridXIndexed : MTLStepFunctionThreadPositionInGridX; l.stepRate = 1; }
            }
            for (uint32_t i = 0; i < vi->vertexAttributeDescriptionCount; i++) {
                const auto& a = vi->pVertexAttributeDescriptions[i];
                MTLAttributeDescriptor* ad = sd.attributes[a.location];
                ad.format = (MTLAttributeFormat)kmtlVertexFormat(a.format);  // same raw values as MTLVertexFormat
                ad.offset = a.offset;
                ad.bufferIndex = vertexBufferIndex(a.binding);
            }
        }
        if (t) {
            sd.indexType = t == 1 ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
            sd.indexBufferIndex = kTessIndexBufferIndex;
        }
        d.stageInputDescriptor = sd;
        NSError* err = nil;
        MTLComputePipelineReflection* refl = nil;
        p->tessVertex[t] = [dev->mtl newComputePipelineStateWithDescriptor:d
            options:MTLPipelineOptionBindingInfo | MTLPipelineOptionBufferTypeInfo reflection:&refl error:&err];
        if (!p->tessVertex[t]) {
            KERR("tessellation vertex kernel: %s", err.description.UTF8String);
            return false;
        }
        p->tessVertexStride = outputStride(refl, kTessOutputBufferIndex);
    }
    KStageOptions so;
    so.tess = KTessRole::ControlKernel;
    so.tessControlPoints = modes.outputVertices;
    so.tessQuads = modes.quads;
    id<MTLFunction> hfn = makeFunction(dev, tcs, spv::ExecutionModelTessellationControl, layout, so, htr);
    if (!hfn) return false;
    MTLComputePipelineDescriptor* d = [MTLComputePipelineDescriptor new];
    d.computeFunction = hfn;
    NSError* err = nil;
    MTLComputePipelineReflection* refl = nil;
    p->tessControl = [dev->mtl newComputePipelineStateWithDescriptor:d
        options:MTLPipelineOptionBindingInfo | MTLPipelineOptionBufferTypeInfo reflection:&refl error:&err];
    if (!p->tessControl) {
        KERR("tessellation control kernel: %s", err.description.UTF8String);
        return false;
    }
    p->tessControlStride = outputStride(refl, kTessOutputBufferIndex);
    p->tessPatchStride = outputStride(refl, kTessPatchBufferIndex);
    p->tessPassthrough = htr->passthrough && p->tessControlStride == p->tessVertexStride && !p->tessPatchStride;
    p->tessInCP = ci->pTessellationState ? ci->pTessellationState->patchControlPoints : 3;
    p->tessOutCP = modes.outputVertices ? modes.outputVertices : p->tessInCP;
    KLOG(1, "tessellation pipeline: %u -> %u control points, strides vertex %lu control %lu patch %lu%s", p->tessInCP,
         p->tessOutCP, (unsigned long)p->tessVertexStride, (unsigned long)p->tessControlStride, (unsigned long)p->tessPatchStride,
         p->tessPassthrough ? ", passthrough control stage" : "");
    return true;
}

static KPipeline* createGraphicsPipeline(KDevice* dev, const VkGraphicsPipelineCreateInfo* ci) {
    auto* layout = (KPipelineLayout*)ci->layout;
    KAttachmentFormats af = attachmentFormats(ci);
    const VkPipelineShaderStageCreateInfo* vs = nullptr;
    const VkPipelineShaderStageCreateInfo* fs = nullptr;
    const VkPipelineShaderStageCreateInfo* tcs = nullptr;
    const VkPipelineShaderStageCreateInfo* tes = nullptr;
    for (uint32_t i = 0; i < ci->stageCount; i++) {
        if (ci->pStages[i].stage == VK_SHADER_STAGE_VERTEX_BIT) vs = &ci->pStages[i];
        else if (ci->pStages[i].stage == VK_SHADER_STAGE_FRAGMENT_BIT) fs = &ci->pStages[i];
        else if (ci->pStages[i].stage == VK_SHADER_STAGE_TESSELLATION_CONTROL_BIT) tcs = &ci->pStages[i];
        else if (ci->pStages[i].stage == VK_SHADER_STAGE_TESSELLATION_EVALUATION_BIT) tes = &ci->pStages[i];
        else {
            KERR("graphics pipeline stage 0x%x not supported", ci->pStages[i].stage);
            return nullptr;
        }
    }
    bool tess = tcs && tes;
    if ((tcs || tes) && (!tess || !kmtlTessellationEnabled())) {
        KERR("tessellation pipeline skipped (tessellation disabled by KSA_MTL_TESSELLATION=0)");
        return nullptr;
    }
    std::unique_ptr<KPipeline> tessParts;
    KTranslated htr, dtr, tmodes;
    if (tess) {
        // Merge both stages' modes: evaluation first, the control stage's declarations win
        tessModes((KShaderModule*)tes->module, tes->pName, spv::ExecutionModelTessellationEvaluation, &tmodes);
        KTranslated hm;
        tessModes((KShaderModule*)tcs->module, tcs->pName, spv::ExecutionModelTessellationControl, &hm);
        if (hm.outputVertices) tmodes.outputVertices = hm.outputVertices;
        if (hm.hasDomain) { tmodes.quads = hm.quads; tmodes.hasDomain = true; }
        if (hm.hasWinding && !tmodes.hasWinding) { tmodes.cw = hm.cw; tmodes.hasWinding = true; }
        if (hm.hasPartition && !tmodes.hasPartition) { tmodes.partition = hm.partition; tmodes.hasPartition = true; }
        tessParts.reset(new KPipeline());
        if (!buildTessKernels(dev, ci, vs, tcs, layout, tessParts.get(), &htr, tmodes)) return nullptr;
    }
    if (!vs) {
        KERR("graphics pipeline without vertex shader");
        return nullptr;
    }
    const auto* ms = ci->pMultisampleState;
    bool sampleRate = ms && ms->sampleShadingEnable;
    uint32_t fragMask = 0;
    for (size_t i = 0; i < af.colors.size(); i++)
        if (af.colors[i] != MTLPixelFormatInvalid) fragMask |= 1u << i;

    KTranslated vtr, ftr;
    KStageOptions vso;
    vso.viewMask = af.viewMask;
    if (tess) {
        vso.tess = KTessRole::Evaluation;
        vso.tessControlPoints = tmodes.outputVertices;
        vso.tessQuads = tmodes.quads;
    }
    // With tessellation the evaluation stage is Metal's (post-tessellation) vertex function
    // Translation here; MSL compile and pipeline state on a worker unless the pipeline is tessellated (its kernels
    // are built above) or asynchronous creation is off
    bool async = !tess && asyncPipelines();
    std::string vname, fname;
    id<MTLFunction> vfn = nil, ffn = nil;
    if (tess) {
        vfn = makeFunction(dev, tes, spv::ExecutionModelTessellationEvaluation, layout, vso, &dtr);
        if (!vfn) return nullptr;
    } else {
        if (!prepareStage(vs, spv::ExecutionModelVertex, layout, vso, &vtr, &vname)) return nullptr;
        if (!async && !(vfn = compileStage(dev, vtr, vname))) return nullptr;
    }
    if (fs) {
        KStageOptions fso;
        fso.fragMask = fragMask;
        fso.sampleRate = sampleRate;
        fso.viewMask = af.viewMask;
        if (!prepareStage(fs, spv::ExecutionModelFragment, layout, fso, &ftr, &fname)) return nullptr;
        if (!async && !(ffn = compileStage(dev, ftr, fname))) return nullptr;
    }

    MTLRenderPipelineDescriptor* d = [MTLRenderPipelineDescriptor new];
    d.vertexFunction = vfn;
    d.fragmentFunction = ffn;
    d.supportIndirectCommandBuffers = NO;

    if (tess) {
        bool cw = tmodes.cw;
        d.maxTessellationFactor = 64;
        d.tessellationFactorFormat = MTLTessellationFactorFormatHalf;
        d.tessellationFactorStepFunction = MTLTessellationFactorStepFunctionPerPatch;
        d.tessellationControlPointIndexType = MTLTessellationControlPointIndexTypeNone;
        d.tessellationPartitionMode = tmodes.partition;
        // Reversed because of the Y flip; reversed again for a lower-left domain origin
        MTLWinding w = cw ? MTLWindingCounterClockwise : MTLWindingClockwise;
        auto* dom = ci->pTessellationState ? (const VkPipelineTessellationDomainOriginStateCreateInfo*)findNext(
            ci->pTessellationState->pNext, VK_STRUCTURE_TYPE_PIPELINE_TESSELLATION_DOMAIN_ORIGIN_STATE_CREATE_INFO) : nullptr;
        if (dom && dom->domainOrigin == VK_TESSELLATION_DOMAIN_ORIGIN_LOWER_LEFT)
            w = w == MTLWindingClockwise ? MTLWindingCounterClockwise : MTLWindingClockwise;
        d.tessellationOutputWindingOrder = w;
        tessParts->tessQuads = tmodes.quads;
    }

    // Vertex input (tessellation pipelines read it in the vertex kernel instead)
    if (const auto* vi = tess ? nullptr : ci->pVertexInputState) {
        MTLVertexDescriptor* vd = [MTLVertexDescriptor vertexDescriptor];
        for (uint32_t i = 0; i < vi->vertexBindingDescriptionCount; i++) {
            const auto& b = vi->pVertexBindingDescriptions[i];
            MTLVertexBufferLayoutDescriptor* l = vd.layouts[vertexBufferIndex(b.binding)];
            if (b.stride == 0) {
                l.stepFunction = MTLVertexStepFunctionConstant;
                l.stepRate = 0;
                l.stride = 16;
            } else {
                l.stride = b.stride;
                l.stepFunction = b.inputRate == VK_VERTEX_INPUT_RATE_INSTANCE ? MTLVertexStepFunctionPerInstance : MTLVertexStepFunctionPerVertex;
                l.stepRate = 1;
            }
        }
        for (uint32_t i = 0; i < vi->vertexAttributeDescriptionCount; i++) {
            const auto& a = vi->pVertexAttributeDescriptions[i];
            MTLVertexAttributeDescriptor* ad = vd.attributes[a.location];
            ad.format = kmtlVertexFormat(a.format);
            if (ad.format == MTLVertexFormatInvalid) KERR("vertex format %d unsupported", a.format);
            ad.offset = a.offset;
            ad.bufferIndex = vertexBufferIndex(a.binding);
        }
        if (vi->vertexAttributeDescriptionCount) d.vertexDescriptor = vd;
    }

    // Attachments and blending
    const auto* cb = ci->pColorBlendState;
    for (size_t i = 0; i < af.colors.size(); i++) {
        MTLRenderPipelineColorAttachmentDescriptor* ca = d.colorAttachments[i];
        ca.pixelFormat = af.colors[i];
        if (af.colors[i] == MTLPixelFormatInvalid || !cb || i >= cb->attachmentCount) continue;
        const auto& b = cb->pAttachments[i];
        ca.blendingEnabled = b.blendEnable;
        ca.sourceRGBBlendFactor = blendFactor(b.srcColorBlendFactor);
        ca.destinationRGBBlendFactor = blendFactor(b.dstColorBlendFactor);
        ca.rgbBlendOperation = blendOp(b.colorBlendOp);
        ca.sourceAlphaBlendFactor = blendFactor(b.srcAlphaBlendFactor);
        ca.destinationAlphaBlendFactor = blendFactor(b.dstAlphaBlendFactor);
        ca.alphaBlendOperation = blendOp(b.alphaBlendOp);
        MTLColorWriteMask wm = MTLColorWriteMaskNone;
        if (b.colorWriteMask & VK_COLOR_COMPONENT_R_BIT) wm |= MTLColorWriteMaskRed;
        if (b.colorWriteMask & VK_COLOR_COMPONENT_G_BIT) wm |= MTLColorWriteMaskGreen;
        if (b.colorWriteMask & VK_COLOR_COMPONENT_B_BIT) wm |= MTLColorWriteMaskBlue;
        if (b.colorWriteMask & VK_COLOR_COMPONENT_A_BIT) wm |= MTLColorWriteMaskAlpha;
        ca.writeMask = wm;
    }
    d.depthAttachmentPixelFormat = af.depth;
    d.stencilAttachmentPixelFormat = af.stencil;
    d.rasterSampleCount = ms ? ms->rasterizationSamples : 1;
    if (ms) {
        d.alphaToCoverageEnabled = ms->alphaToCoverageEnable;
        d.alphaToOneEnabled = ms->alphaToOneEnable;
    }
    VkPrimitiveTopology topo = ci->pInputAssemblyState ? ci->pInputAssemblyState->topology : VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
    if (!tess) d.inputPrimitiveTopology = topologyClass(topo);
    const auto* rs = ci->pRasterizationState;
    if (rs && rs->rasterizerDiscardEnable) d.rasterizationEnabled = NO;

    uint64_t vsHash = ((KShaderModule*)vs->module)->hash, fsHash = fs ? ((KShaderModule*)fs->module)->hash : 0;
    auto makeState = [dev, vsHash, fsHash](MTLRenderPipelineDescriptor* desc) -> id<MTLRenderPipelineState> {
        NSError* err = nil;
        auto tp = std::chrono::steady_clock::now();
        id<MTLRenderPipelineState> ps = [dev->mtl newRenderPipelineStateWithDescriptor:desc error:&err];
        g_usPipeline += usSince(tp);
        g_nPipeline++;
        if (!ps) KERR("render pipeline state failed: %s (vs %016llx fs %016llx)", err.description.UTF8String,
                      (unsigned long long)vsHash, (unsigned long long)fsHash);
        return ps;
    };
    id<MTLRenderPipelineState> ps = nil;
    if (!async && !(ps = makeState(d))) return nullptr;

    auto* p = new KPipeline();
    p->bindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    p->render = ps;
    if (async) {
        // A failure leaves render nil: draws with the pipeline are skipped, as for a failed synchronous creation
        p->pending = std::make_shared<KPipeline::Pending>();
        auto pending = p->pending;
        std::string vn = vname, fn = fname;
        KTranslated vt = vtr, ft = ftr;
        bool hasFs = fs != nullptr;
        runPipelineJob([=] {
            id<MTLFunction> v = compileStage(dev, vt, vn);
            id<MTLFunction> f = hasFs ? compileStage(dev, ft, fn) : nil;
            id<MTLRenderPipelineState> state = nil;
            if (v && (f || !hasFs)) {
                d.vertexFunction = v;
                d.fragmentFunction = f;
                state = makeState(d);
            }
            std::lock_guard<std::mutex> lock(pending->m);
            p->render = state;
            pending->done = true;
            pending->cv.notify_all();
        });
    }
    p->primitive = primitiveType(topo);
    if (tess) {
        p->tess = true;
        for (int t = 0; t < 3; t++) p->tessVertex[t] = tessParts->tessVertex[t];
        p->tessControl = tessParts->tessControl;
        p->tessInCP = tessParts->tessInCP;
        p->tessOutCP = tessParts->tessOutCP;
        p->tessVertexStride = tessParts->tessVertexStride;
        p->tessControlStride = tessParts->tessControlStride;
        p->tessPatchStride = tessParts->tessPatchStride;
        p->tessQuads = tessParts->tessQuads;
        p->tessPassthrough = tessParts->tessPassthrough;
    }
    p->triangleFan = topo == VK_PRIMITIVE_TOPOLOGY_TRIANGLE_FAN;
    p->vertexUsesDrawId = vtr.usesDrawId;
    p->fragmentUsesDrawId = ftr.usesDrawId;
    p->viewMask = af.viewMask;
    p->hasFragment = fs != nullptr;
    {
        char nm[80];
        auto* vm = (KShaderModule*)(tess ? tes->module : vs->module);
        snprintf(nm, sizeof(nm), "%c%016llx", tess ? 't' : 'v', (unsigned long long)vm->hash);
        p->profName = nm;
        if (fs) {
            snprintf(nm, sizeof(nm), "+f%016llx", (unsigned long long)((KShaderModule*)fs->module)->hash);
            p->profName += nm;
        }
        p->profId = kmtlHash(p->profName.data(), p->profName.size());
    }
    if (rs) {
        p->cull = rs->cullMode == VK_CULL_MODE_FRONT_BIT ? MTLCullModeFront
                : rs->cullMode == VK_CULL_MODE_BACK_BIT ? MTLCullModeBack : MTLCullModeNone;
        if (rs->cullMode == VK_CULL_MODE_FRONT_AND_BACK) KLOG(1, "cull front-and-back unsupported");
        p->winding = rs->frontFace == VK_FRONT_FACE_CLOCKWISE ? MTLWindingClockwise : MTLWindingCounterClockwise;
        p->fill = rs->polygonMode == VK_POLYGON_MODE_LINE ? MTLTriangleFillModeLines : MTLTriangleFillModeFill;
        p->depthClip = rs->depthClampEnable ? MTLDepthClipModeClamp : MTLDepthClipModeClip;
        p->depthBiasEnable = rs->depthBiasEnable;
        p->depthBias[0] = rs->depthBiasConstantFactor;
        p->depthBias[1] = rs->depthBiasSlopeFactor;
        p->depthBias[2] = rs->depthBiasClamp;
    }
    if (cb) memcpy(p->blendConstants, cb->blendConstants, sizeof(p->blendConstants));

    // Depth/stencil state
    MTLDepthStencilDescriptor* dsd = [MTLDepthStencilDescriptor new];
    const auto* ds = ci->pDepthStencilState;
    if (ds && af.depth != MTLPixelFormatInvalid && ds->depthTestEnable) {
        dsd.depthCompareFunction = kmtlCompare(ds->depthCompareOp);
        dsd.depthWriteEnabled = ds->depthWriteEnable;
    }
    if (ds && af.stencil != MTLPixelFormatInvalid && ds->stencilTestEnable) {
        dsd.frontFaceStencil = stencilDesc(ds->front);
        dsd.backFaceStencil = stencilDesc(ds->back);
        p->stencilRef[0] = ds->front.reference;
        p->stencilRef[1] = ds->back.reference;
    }
    p->depthStencil = [dev->mtl newDepthStencilStateWithDescriptor:dsd];

    // Dynamic and static viewport/scissor
    if (const auto* dy = ci->pDynamicState) {
        for (uint32_t i = 0; i < dy->dynamicStateCount; i++) {
            switch (dy->pDynamicStates[i]) {
            case VK_DYNAMIC_STATE_VIEWPORT: case VK_DYNAMIC_STATE_VIEWPORT_WITH_COUNT: p->dynamicViewport = true; break;
            case VK_DYNAMIC_STATE_SCISSOR: case VK_DYNAMIC_STATE_SCISSOR_WITH_COUNT: p->dynamicScissor = true; break;
            case VK_DYNAMIC_STATE_DEPTH_BIAS: p->dynamicDepthBias = true; break;
            case VK_DYNAMIC_STATE_STENCIL_REFERENCE: p->dynamicStencilRef = true; break;
            default: KLOG(1, "dynamic state %d ignored", dy->pDynamicStates[i]); break;
            }
        }
    }
    if (const auto* vp = ci->pViewportState) {
        if (!p->dynamicViewport && vp->pViewports) p->viewports.assign(vp->pViewports, vp->pViewports + vp->viewportCount);
        if (!p->dynamicScissor && vp->pScissors) p->scissors.assign(vp->pScissors, vp->pScissors + vp->scissorCount);
    }
    return p;
}

VKFN(VkResult) vkCreateGraphicsPipelines(VkDevice device, VkPipelineCache, uint32_t n, const VkGraphicsPipelineCreateInfo* cis,
                                         const VkAllocationCallbacks*, VkPipeline* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    VkResult res = VK_SUCCESS;
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            KPipeline* p = createGraphicsPipeline(dev, &cis[i]);
            out[i] = (VkPipeline)p;
            if (!p) res = VK_ERROR_INITIALIZATION_FAILED;
        }
    }
    // A failed pipeline is reported but does not stop the game: KSA_MTL_STRICT=1 returns the error instead
    if (res != VK_SUCCESS && !kmtlEnvInt("KSA_MTL_STRICT", 0)) {
        for (uint32_t i = 0; i < n; i++)
            if (!out[i]) out[i] = (VkPipeline) new KPipeline();  // null pipeline: draws with it are skipped
        return VK_SUCCESS;
    }
    return res;
}

// ---------------------------------------------------------------------------------------------------------------
// Compute pipelines

KPipeline* kmtlCreateComputeFromStage(KDevice* dev, KPipelineLayout* layout, const VkPipelineShaderStageCreateInfo* stage, bool rtLaunch) {
    KTranslated tr;
    KStageOptions cso;
    cso.fragMask = 0;
    cso.rtLaunch = rtLaunch;
    std::string name;
    if (!prepareStage(stage, spv::ExecutionModelGLCompute, layout, cso, &tr, &name)) return nullptr;
    uint64_t csHash = ((KShaderModule*)stage->module)->hash;
    auto build = [dev, csHash](const KTranslated& t, const std::string& n) -> id<MTLComputePipelineState> {
        id<MTLFunction> fn = compileStage(dev, t, n);
        if (!fn) return nil;
        MTLComputePipelineDescriptor* d = [MTLComputePipelineDescriptor new];
        d.computeFunction = fn;
        d.maxTotalThreadsPerThreadgroup = t.workgroup.width * t.workgroup.height * t.workgroup.depth;
        NSError* err = nil;
        auto tp = std::chrono::steady_clock::now();
        id<MTLComputePipelineState> ps = [dev->mtl newComputePipelineStateWithDescriptor:d options:MTLPipelineOptionNone reflection:nil error:&err];
        g_usPipeline += usSince(tp);
        g_nPipeline++;
        if (!ps) KERR("compute pipeline state failed: %s (cs %016llx)", err.description.UTF8String, (unsigned long long)csHash);
        return ps;
    };
    // Ray tracing kernels translate a module that lives only for this call: they compile here
    bool async = asyncPipelines() && !rtLaunch;
    id<MTLComputePipelineState> ps = nil;
    if (!async && !(ps = build(tr, name))) return nullptr;
    auto* p = new KPipeline();
    p->bindPoint = VK_PIPELINE_BIND_POINT_COMPUTE;
    p->compute = ps;
    if (async) {
        p->pending = std::make_shared<KPipeline::Pending>();
        auto pending = p->pending;
        runPipelineJob([=] {
            id<MTLComputePipelineState> state = build(tr, name);
            std::lock_guard<std::mutex> lock(pending->m);
            p->compute = state;
            pending->done = true;
            pending->cv.notify_all();
        });
    }
    p->workgroup = tr.workgroup;
    p->computeUsesDrawId = tr.usesDrawId;
    {
        char nm[40];
        snprintf(nm, sizeof(nm), "c%016llx", (unsigned long long)((KShaderModule*)stage->module)->hash);
        p->profName = nm;
        p->profId = kmtlHash(nm, strlen(nm));
    }
    return p;
}

static KPipeline* createComputePipeline(KDevice* dev, const VkComputePipelineCreateInfo* ci) {
    return kmtlCreateComputeFromStage(dev, (KPipelineLayout*)ci->layout, &ci->stage, false);
}

VKFN(VkResult) vkCreateComputePipelines(VkDevice device, VkPipelineCache, uint32_t n, const VkComputePipelineCreateInfo* cis,
                                        const VkAllocationCallbacks*, VkPipeline* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    VkResult res = VK_SUCCESS;
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            KPipeline* p = createComputePipeline(dev, &cis[i]);
            out[i] = (VkPipeline)p;
            if (!p) res = VK_ERROR_INITIALIZATION_FAILED;
        }
    }
    if (res != VK_SUCCESS && !kmtlEnvInt("KSA_MTL_STRICT", 0)) {
        for (uint32_t i = 0; i < n; i++)
            if (!out[i]) {
                auto* p = new KPipeline();
                p->bindPoint = VK_PIPELINE_BIND_POINT_COMPUTE;
                out[i] = (VkPipeline)p;
            }
        return VK_SUCCESS;
    }
    return res;
}

VKFN(void) vkDestroyPipeline(VkDevice, VkPipeline p, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    if (p) ((KPipeline*)p)->wait();  // a worker may still write it
    delete (KPipeline*)p;
}
