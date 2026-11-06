// SPIR-V -> MSL: one shader stage with SPIRV-Cross, the set layouts' slots, specialization and the MSL patches
// (translate.h)
#include "translate.h"
#include "spirv_parser.hpp"
#include <regex>

using namespace SPIRV_CROSS_NAMESPACE;

static std::atomic<int64_t> g_usTranslate{0};
static std::atomic<int> g_nTranslate{0};
static int64_t usSince(std::chrono::steady_clock::time_point t) {
    return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - t).count();
}

void kmtlTranslateStats(int* count, int64_t* us) {
    *count = g_nTranslate.load();
    *us = g_usTranslate.load();
}

// The tessellation execution modes a stage declares (the control and evaluation stages each declare part of them)
static void readTessModes(CompilerMSL& c, KTranslated* out) {
    auto& modes = c.get_execution_mode_bitset();
    if (modes.get(spv::ExecutionModeOutputVertices)) out->outputVertices = c.get_execution_mode_argument(spv::ExecutionModeOutputVertices);
    if (modes.get(spv::ExecutionModeQuads)) { out->quads = true; out->hasDomain = true; }
    if (modes.get(spv::ExecutionModeTriangles)) { out->quads = false; out->hasDomain = true; }
    if (modes.get(spv::ExecutionModeVertexOrderCw)) { out->cw = true; out->hasWinding = true; }
    if (modes.get(spv::ExecutionModeVertexOrderCcw)) { out->cw = false; out->hasWinding = true; }
    if (modes.get(spv::ExecutionModeSpacingEqual)) { out->partition = MTLTessellationPartitionModeInteger; out->hasPartition = true; }
    if (modes.get(spv::ExecutionModeSpacingFractionalEven)) { out->partition = MTLTessellationPartitionModeFractionalEven; out->hasPartition = true; }
    if (modes.get(spv::ExecutionModeSpacingFractionalOdd)) { out->partition = MTLTessellationPartitionModeFractionalOdd; out->hasPartition = true; }
}

// Reads the tessellation execution modes of one stage module
void kmtlTessModes(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KTranslated* out) {
    try {
        CompilerMSL c(mod->spirv);
        c.set_entry_point(entry, model);
        readTessModes(c, out);
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

// PORT_SKIP_UNLIT_SHADOWS and PORT_SHARED_SHADOW_TAPS, the specialization constants of the shadow edits
// (src/game/changes/optimizations/unlit-shadows.edits), true in the source; off when their setting is
static const uint32_t kUnlitShadowSkipConstant = 900, kSharedShadowTapsConstant = 901;

// The edits' constants this configuration turns off
static std::vector<uint32_t> switchesOff() {
    std::vector<uint32_t> off;
    if (!kmtlConfig().skipShadowsOnUnlitSides) off.push_back(kUnlitShadowSkipConstant);
    if (!kmtlConfig().sharedShadowSamples) off.push_back(kSharedShadowTapsConstant);
    return off;
}

// Whether the module declares a specialization constant with this id (OpDecorate <id> SpecId <specId>)
static bool hasSpecId(const KShaderModule* mod, uint32_t specId) {
    const std::vector<uint32_t>& w = mod->spirv;
    for (size_t i = 5, n; i < w.size(); i += n) {
        n = w[i] >> 16;
        if (n == 0 || i + n > w.size()) break;
        if ((w[i] & 0xFFFF) == spv::OpDecorate && n == 4 && w[i + 2] == spv::DecorationSpecId && w[i + 3] == specId)
            return true;
    }
    return false;
}

bool kmtlTranslate(KShaderModule* mod, const char* entryName, spv::ExecutionModel model, KPipelineLayout* layout,
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
            mo.raw_buffer_tese_input = !so.tessFusedInput;
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
                // Shaders read their argument buffers from the constant address space, which lets the compiler load
                // resource handles as uniforms (2-5% faster across the benchmark than device;
                // KSA_MTL_SHADER_CONSTANT_MEMORY=0: device everywhere). Ray tracing kernels (rtspirv.cpp) pass their
                // resources to functions declared for device memory, and the constant address space holds at most 64
                // KiB
                if (!kmtlConfig().shaderConstantMemory || so.rtLaunch || sl->bytes() > 65536)
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
        if (const char* presets = kmtlConfig().spec) {
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

        // KSA_MTL_SKIP_SHADOWS_ON_UNLIT_SIDES=0, KSA_MTL_SHARED_SHADOW_SAMPLES=0: the shadow edits read these constants
        for (uint32_t id : switchesOff())
            for (auto& sc : c.get_specialization_constants())
                if (sc.constant_id == id) c.get_constant(sc.id).m.c[0].r[0].u32 = 0;

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
        if (so.tess == KTessRole::ControlKernel || so.tess == KTessRole::Evaluation) readTessModes(c, out);
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

// Everything that shapes a stage's translation: the module, entry, stage options, the slot map of every set,
// specialization data, and the translator version. Bump kTranslatorVersion when kmtlTranslate() or its MSL patches change
uint64_t kmtlTranslationKey(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KPipelineLayout* layout,
                            const VkSpecializationInfo* spec, const KStageOptions& so) {
    static const uint32_t kTranslatorVersion = 4;
    std::vector<uint32_t> k = {kTranslatorVersion, 0x300681bbu /* SPIRV-Cross pin */, (uint32_t)model, so.fragMask,
        so.sampleRate, so.viewMask, (uint32_t)so.tess, (uint32_t)so.indexType, so.tessControlPoints, so.tessQuads,
        (uint32_t)mod->hash, (uint32_t)(mod->hash >> 32), so.rtLaunch, (uint32_t)kmtlConfig().shaderConstantMemory,
        so.tessFusedInput};
    if (const char* presets = kmtlConfig().spec) k.push_back((uint32_t)kmtlHash(presets, strlen(presets)));
    for (uint32_t id : switchesOff())
        if (hasSpecId(mod, id)) k.push_back(id);
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
