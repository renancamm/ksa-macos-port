// Render passes, framebuffers, graphics and compute pipelines: their Metal states, from stages made ready by
// stages.mm (stages.h).
#include "stages.h"
#include "shaders/tessspirv.h"
#include "spirv_msl.hpp"
#include <memory>
#include <set>
#include <regex>
#include <functional>

using namespace SPIRV_CROSS_NAMESPACE;

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

// The field types (and names) of a struct in translated MSL: how the records two kernels exchange are compared
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

// Builds the compute stages of a tessellation pipeline: the vertex shader as three kernels (by index type) with a
// stage-in descriptor for its vertex attributes, and the control shader as a multi-patch kernel. With *passthrough
// given, a control stage that only copies its control points is built without the copies (see below)
static bool buildTessKernels(KDevice* dev, const VkGraphicsPipelineCreateInfo* ci, const VkPipelineShaderStageCreateInfo* vs,
                             const VkPipelineShaderStageCreateInfo* tcs, KPipelineLayout* layout, KTessStages* st, KTranslated* htr,
                             const KTranslated& modes, bool* passthrough, KTranslated* vtrOut = nullptr) {
    static const CompilerMSL::Options::IndexType types[3] = {CompilerMSL::Options::IndexType::None,
        CompilerMSL::Options::IndexType::UInt16, CompilerMSL::Options::IndexType::UInt32};
    for (int t = 0; t < 3; t++) {
        KTranslated vtr;
        KStageOptions so;
        so.tess = KTessRole::VertexKernel;
        so.indexType = types[t];
        id<MTLFunction> fn = kmtlMakeFunction(dev, vs, spv::ExecutionModelVertex, layout, so, &vtr);
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
        st->vertex[t] = [dev->mtl newComputePipelineStateWithDescriptor:d
            options:MTLPipelineOptionBindingInfo | MTLPipelineOptionBufferTypeInfo reflection:&refl error:&err];
        if (!st->vertex[t]) {
            KERR("tessellation vertex kernel: %s", err.description.UTF8String);
            return false;
        }
        st->vertexStride = outputStride(refl, kTessOutputBufferIndex);
        if (vtrOut) *vtrOut = vtr;
    }
    KStageOptions so;
    so.tess = KTessRole::ControlKernel;
    so.tessControlPoints = modes.outputVertices;
    so.tessQuads = modes.quads;
    auto buildControl = [&](const VkPipelineShaderStageCreateInfo* stage) {
        id<MTLFunction> hfn = kmtlMakeFunction(dev, stage, spv::ExecutionModelTessellationControl, layout, so, htr);
        if (!hfn) return false;
        MTLComputePipelineDescriptor* d = [MTLComputePipelineDescriptor new];
        d.computeFunction = hfn;
        NSError* err = nil;
        MTLComputePipelineReflection* refl = nil;
        st->control = [dev->mtl newComputePipelineStateWithDescriptor:d
            options:MTLPipelineOptionBindingInfo | MTLPipelineOptionBufferTypeInfo reflection:&refl error:&err];
        if (!st->control) {
            KERR("tessellation control kernel: %s", err.description.UTF8String);
            return false;
        }
        st->controlStride = outputStride(refl, kTessOutputBufferIndex);
        st->patchStride = outputStride(refl, kTessPatchBufferIndex);
        return true;
    };
    // A passthrough control stage (tessspirv.cpp) loses its copies, and the evaluation stage reads the vertex kernel's
    // records in their place: so the stripped kernel's record must be the vertex kernel's (the translated records
    // compared the way the fused path compares its own, and the reflected strides). Otherwise, and for a stage that
    // computes something per control point, the stage runs as written
    auto* tcsModule = (KShaderModule*)tcs->module;
    std::string why;
    if (passthrough) *passthrough = false;
    std::vector<uint32_t> stripped;
    if (passthrough && tcsModule && kmtlStripPassthroughControl(tcsModule->spirv, &stripped, &why)) {
        KShaderModule km;
        km.spirv = std::move(stripped);
        km.hash = kmtlHash(km.spirv.data(), km.spirv.size() * 4);
        if (const char* dir = kmtlConfig().dumpSpv) {
            char name[64];
            snprintf(name, sizeof(name), "passthrough_control_%016llx.spv", (unsigned long long)tcsModule->hash);
            kmtlDumpFile(dir, name, km.spirv.data(), km.spirv.size() * 4);
        }
        VkPipelineShaderStageCreateInfo cst = *tcs;
        cst.module = (VkShaderModule)&km;
        std::vector<std::string> outNames, inNames;
        bool built = buildControl(&cst);
        auto outTypes = built ? structFieldTypes(htr->msl, "main0_out", &outNames) : std::vector<std::string>();
        auto inTypes = built ? structFieldTypes(htr->msl, "main0_in", &inNames) : std::vector<std::string>();
        if (built && !outTypes.empty() && outTypes == inTypes && st->controlStride == st->vertexStride && !st->patchStride)
            *passthrough = true;
        else
            why = "the stripped kernel's record is not the vertex kernel's";
    }
    if (passthrough && !*passthrough)
        KLOG(1, "tessellation control stage %016llx runs as written: %s", (unsigned long long)(tcsModule ? tcsModule->hash : 0), why.c_str());
    return (passthrough && *passthrough) || buildControl(tcs);
}

// Fused tessellation (KSA_MTL_FUSED_TESSELLATION=0 turns it off; tessspirv.cpp has the rewrites). With a passthrough
// control stage the vertex kernel's outputs only travel to the evaluation stage through memory: here the evaluation
// stage computes them itself from the vertex attributes Metal fetches per control point (through the draw's 32-bit
// index buffer), and the kernels shrink to what the tessellation factors need. Fills p's *Fused members; false leaves
// the pipeline as it was.
static bool buildFusedTess(KDevice* dev, const VkGraphicsPipelineCreateInfo* ci, const VkPipelineShaderStageCreateInfo* vs,
                           const VkPipelineShaderStageCreateInfo* tcs, const VkPipelineShaderStageCreateInfo* tes,
                           KPipelineLayout* layout, const KTranslated& modes, KStageOptions eso, MTLRenderPipelineDescriptor* d,
                           const std::function<id<MTLRenderPipelineState>(MTLRenderPipelineDescriptor*)>& makeState, KPipeline* p) {
    const auto* vi = ci->pVertexInputState;
    if (!vi || !vi->vertexAttributeDescriptionCount || p->tessInCP != p->tessOutCP || p->tessPlain.patchStride) return false;
    auto* vsm = (KShaderModule*)vs->module;
    uint64_t teHash = ((KShaderModule*)tes->module)->hash;
    std::string err;
    std::vector<uint32_t> ctl, vred, eval, inputs, raw;
    bool position;
    if (!kmtlReduceTessControl(((KShaderModule*)tcs->module)->spirv, &ctl, &inputs, &position, &err) ||
        !kmtlReduceVertexOutputs(vsm->spirv, inputs, &vred, &err) ||
        !kmtlFuseTessEvaluation(vsm->spirv, ((KShaderModule*)tes->module)->spirv, p->tessOutCP, &eval, &raw, &err)) {
        KLOG(1, "fused tessellation unavailable: %s", err.c_str());
        return false;
    }
    if (const char* dir = kmtlConfig().dumpSpv) {
        auto dump = [&](const char* what, const std::vector<uint32_t>& w) {
            char name[64];
            snprintf(name, sizeof(name), "fused_%s_%016llx.spv", what, (unsigned long long)vsm->hash);
            kmtlDumpFile(dir, name, w.data(), w.size() * 4);
        };
        dump("control", ctl); dump("vertex", vred); dump("eval", eval);
    }
    // The rewritten modules go through the usual paths; they live as long as this call
    KShaderModule km[3];
    km[0].spirv = std::move(vred);
    km[1].spirv = std::move(ctl);
    km[2].spirv = std::move(eval);
    for (auto& m : km) m.hash = kmtlHash(m.spirv.data(), m.spirv.size() * 4);
    VkPipelineShaderStageCreateInfo vst = *vs, cst = *tcs, est = *tes;
    vst.module = (VkShaderModule)&km[0];
    cst.module = (VkShaderModule)&km[1];
    est.module = (VkShaderModule)&km[2];
    KTessStages parts;
    KTranslated htr, vtr;
    if (!buildTessKernels(dev, ci, &vst, &cst, layout, &parts, &htr, modes, nullptr, &vtr)) return false;
    // The vertex kernel's record must be what the control kernel reads
    std::vector<std::string> outNames, inNames;
    auto outTypes = structFieldTypes(vtr.msl, "main0_out", &outNames);
    auto inTypes = structFieldTypes(htr.msl, "main0_in", &inNames);
    if (outTypes.empty() || outTypes != inTypes) {
        KLOG(1, "fused tessellation unavailable: reduced vertex and control interfaces differ");
        return false;
    }
    KTranslated etr;
    eso.tessFusedInput = true;
    id<MTLFunction> efn = kmtlMakeFunction(dev, &est, spv::ExecutionModelTessellationEvaluation, layout, eso, &etr);
    if (!efn) return false;
    // Each control point's attributes, fetched through the control point index buffer
    MTLVertexDescriptor* vd = [MTLVertexDescriptor vertexDescriptor];
    for (uint32_t loc : raw) {
        const VkVertexInputAttributeDescription* a = nullptr;
        for (uint32_t i = 0; i < vi->vertexAttributeDescriptionCount; i++)
            if (vi->pVertexAttributeDescriptions[i].location == loc) a = &vi->pVertexAttributeDescriptions[i];
        const VkVertexInputBindingDescription* b = nullptr;
        for (uint32_t i = 0; a && i < vi->vertexBindingDescriptionCount; i++)
            if (vi->pVertexBindingDescriptions[i].binding == a->binding) b = &vi->pVertexBindingDescriptions[i];
        if (!a || !b || !b->stride || b->inputRate != VK_VERTEX_INPUT_RATE_VERTEX) {
            KLOG(1, "fused tessellation unavailable: vertex attribute %u is not per vertex", loc);
            return false;
        }
        MTLVertexAttributeDescriptor* ad = vd.attributes[loc];
        ad.format = kmtlVertexFormat(a->format);
        ad.offset = a->offset;
        ad.bufferIndex = vertexBufferIndex(a->binding);
        MTLVertexBufferLayoutDescriptor* l = vd.layouts[vertexBufferIndex(a->binding)];
        l.stride = b->stride;
        l.stepFunction = MTLVertexStepFunctionPerPatchControlPoint;
        l.stepRate = 1;
    }
    MTLRenderPipelineDescriptor* fd = [d copy];
    fd.vertexFunction = efn;
    fd.vertexDescriptor = vd;
    fd.tessellationControlPointIndexType = MTLTessellationControlPointIndexTypeUInt32;
    id<MTLRenderPipelineState> ps = makeState(fd);
    if (!ps) return false;
    p->renderFused = ps;
    // The reduced control kernel writes no control point or patch records (the draw gives it a token buffer)
    parts.controlStride = parts.patchStride = 0;
    p->tessFused = parts;
    KLOG(1, "fused tessellation (vs %016llx te %016llx fs %s, color %d depth %d samples %lu): vertex record %lu -> %lu bytes, "
         "%zu attributes per control point", (unsigned long long)vsm->hash, (unsigned long long)teHash,
         d.fragmentFunction ? "yes" : "no", (int)d.colorAttachments[0].pixelFormat, (int)d.depthAttachmentPixelFormat,
         (unsigned long)d.rasterSampleCount,
         (unsigned long)p->tessPlain.vertexStride, (unsigned long)parts.vertexStride, raw.size());
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
    if ((tcs || tes) && (!tess || !kmtlConfig().tessellation)) {
        KERR("tessellation pipeline skipped (%s)", tess ? "tessellation disabled by KSA_MTL_TESSELLATION=0"
                                                         : "it has only one of the control and evaluation stages");
        return nullptr;
    }
    KTessStages tessPlain;
    bool passthrough = false;
    KTranslated htr, dtr, tmodes;
    if (tess) {
        // Merge both stages' modes: evaluation first, the control stage's declarations win
        kmtlTessModes((KShaderModule*)tes->module, tes->pName, spv::ExecutionModelTessellationEvaluation, &tmodes);
        KTranslated hm;
        kmtlTessModes((KShaderModule*)tcs->module, tcs->pName, spv::ExecutionModelTessellationControl, &hm);
        if (hm.outputVertices) tmodes.outputVertices = hm.outputVertices;
        if (hm.hasDomain) { tmodes.quads = hm.quads; tmodes.hasDomain = true; }
        if (hm.hasWinding && !tmodes.hasWinding) { tmodes.cw = hm.cw; tmodes.hasWinding = true; }
        if (hm.hasPartition && !tmodes.hasPartition) { tmodes.partition = hm.partition; tmodes.hasPartition = true; }
        if (!buildTessKernels(dev, ci, vs, tcs, layout, &tessPlain, &htr, tmodes, &passthrough)) return nullptr;
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
    bool async = !tess && kmtlConfig().backgroundShaderCompiling;
    std::string vname, fname;
    id<MTLFunction> vfn = nil, ffn = nil;
    if (tess) {
        vfn = kmtlMakeFunction(dev, tes, spv::ExecutionModelTessellationEvaluation, layout, vso, &dtr);
        if (!vfn) return nullptr;
    } else {
        if (!kmtlPrepareStage(vs, spv::ExecutionModelVertex, layout, vso, &vtr, &vname)) return nullptr;
        if (!async && !(vfn = kmtlCompileStage(dev, vtr, vname))) return nullptr;
    }
    if (fs) {
        KStageOptions fso;
        fso.fragMask = fragMask;
        fso.sampleRate = sampleRate;
        fso.viewMask = af.viewMask;
        if (!kmtlPrepareStage(fs, spv::ExecutionModelFragment, layout, fso, &ftr, &fname)) return nullptr;
        if (!async && !(ffn = kmtlCompileStage(dev, ftr, fname))) return nullptr;
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
        kmtlNotePipelineState(tp);
        if (!ps) KERR("render pipeline state failed: %s (vs %016llx fs %016llx)", err.description.UTF8String,
                      (unsigned long long)vsHash, (unsigned long long)fsHash);
        return ps;
    };
    id<MTLRenderPipelineState> ps = nil;
    if (!async && !(ps = makeState(d))) return nullptr;

    bool early = fs && !tess && kmtlConfig().earlyFragmentTests && !(ms && ms->alphaToCoverageEnable) &&
                 kmtlStoresFromFragment(ftr.msl);
    auto* p = new KPipeline();
    p->render = ps;
    if (!async && early) {
        if (id<MTLFunction> fe = kmtlCompileStage(dev, ftr, fname, false, true)) {
            d.fragmentFunction = fe;
            p->renderEarly = makeState(d);
            d.fragmentFunction = ffn;
        }
    }
    if (async) {
        // A failure leaves render nil: draws with the pipeline are skipped, as for a failed synchronous creation
        p->pending = std::make_shared<KPipeline::Pending>();
        auto pending = p->pending;
        std::string vn = vname, fn = fname;
        KTranslated vt = vtr, ft = ftr;
        bool hasFs = fs != nullptr;
        kmtlRunPipelineJob([=] {
            id<MTLFunction> v = kmtlCompileStage(dev, vt, vn);
            id<MTLFunction> f = hasFs ? kmtlCompileStage(dev, ft, fn) : nil;
            id<MTLRenderPipelineState> state = nil, earlyState = nil;
            if (v && (f || !hasFs)) {
                d.vertexFunction = v;
                d.fragmentFunction = f;
                state = makeState(d);
                id<MTLFunction> fe = state && early ? kmtlCompileStage(dev, ft, fn, false, true) : nil;
                if (fe) {
                    d.fragmentFunction = fe;
                    earlyState = makeState(d);
                }
            }
            std::lock_guard<std::mutex> lock(pending->m);
            p->render = state;
            p->renderEarly = earlyState;
            pending->done.store(true, std::memory_order_release);
            pending->cv.notify_all();
        });
    }
    p->primitive = primitiveType(topo);
    if (tess) {
        p->tess = true;
        p->tessPlain = tessPlain;
        p->tessInCP = ci->pTessellationState ? ci->pTessellationState->patchControlPoints : 3;
        p->tessOutCP = tmodes.outputVertices ? tmodes.outputVertices : p->tessInCP;
        p->tessQuads = tmodes.quads;
        p->tessPassthrough = passthrough;  // the control points stay as the vertex kernel wrote them
        KLOG(1, "tessellation pipeline: %u -> %u control points, strides vertex %lu control %lu patch %lu%s", p->tessInCP,
             p->tessOutCP, (unsigned long)tessPlain.vertexStride, (unsigned long)tessPlain.controlStride,
             (unsigned long)tessPlain.patchStride, p->tessPassthrough ? ", passthrough control stage" : "");
        if (kmtlConfig().fusedTessellation && p->tessPassthrough) buildFusedTess(dev, ci, vs, tcs, tes, layout, tmodes, vso, d, makeState, p);
    }
    p->triangleFan = topo == VK_PRIMITIVE_TOPOLOGY_TRIANGLE_FAN;
    p->vertexUsesDrawId = vtr.usesDrawId;
    p->fragmentUsesDrawId = ftr.usesDrawId;
    p->viewMask = af.viewMask;
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
        p->throttle = kmtlThrottleFor(p->profName);
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
            case VK_DYNAMIC_STATE_BLEND_CONSTANTS: p->dynamicBlendConstants = true; break;
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

// The end of a vkCreate*Pipelines call whose failed pipelines are null in out (each failure logged where it happened).
// A failed pipeline does not stop the game: it becomes an empty pipeline (no Metal state), whose draws, dispatches and
// ray traces are skipped. KSA_MTL_STRICT_PIPELINES=1 returns the error instead, the failed handles left null.
VkResult kmtlFinishPipelines(uint32_t n, VkPipeline* out) {
    bool failed = false;
    for (uint32_t i = 0; i < n; i++) failed |= !out[i];
    if (!failed) return VK_SUCCESS;
    if (kmtlConfig().strictPipelines) return VK_ERROR_INITIALIZATION_FAILED;
    for (uint32_t i = 0; i < n; i++)
        if (!out[i]) out[i] = (VkPipeline) new KPipeline();
    return VK_SUCCESS;
}

VKFN(VkResult) vkCreateGraphicsPipelines(VkDevice device, VkPipelineCache, uint32_t n, const VkGraphicsPipelineCreateInfo* cis,
                                         const VkAllocationCallbacks*, VkPipeline* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            out[i] = (VkPipeline)createGraphicsPipeline(dev, &cis[i]);
        }
    }
    return kmtlFinishPipelines(n, out);
}

// ---------------------------------------------------------------------------------------------------------------
// Compute pipelines

KPipeline* kmtlCreateComputeFromStage(KDevice* dev, KPipelineLayout* layout, const VkPipelineShaderStageCreateInfo* stage, bool rtLaunch) {
    KTranslated tr;
    KStageOptions cso;
    cso.fragMask = 0;
    cso.rtLaunch = rtLaunch;
    std::string name;
    if (!kmtlPrepareStage(stage, spv::ExecutionModelGLCompute, layout, cso, &tr, &name)) return nullptr;
    uint64_t csHash = ((KShaderModule*)stage->module)->hash;
    auto build = [dev, csHash, rtLaunch](const KTranslated& t, const std::string& n) -> id<MTLComputePipelineState> {
        id<MTLFunction> fn = kmtlCompileStage(dev, t, n, rtLaunch);
        if (!fn) return nil;
        MTLComputePipelineDescriptor* d = [MTLComputePipelineDescriptor new];
        d.computeFunction = fn;
        NSUInteger threads = t.workgroup.width * t.workgroup.height * t.workgroup.depth;
        d.maxTotalThreadsPerThreadgroup = std::max<NSUInteger>(threads, kmtlThreadgroupHintFor(csHash));
        NSError* err = nil;
        auto tp = std::chrono::steady_clock::now();
        id<MTLComputePipelineState> ps = [dev->mtl newComputePipelineStateWithDescriptor:d options:MTLPipelineOptionNone reflection:nil error:&err];
        kmtlNotePipelineState(tp);
        if (!ps) KERR("compute pipeline state failed: %s (cs %016llx)", err.description.UTF8String, (unsigned long long)csHash);
        return ps;
    };
    // Ray tracing kernels translate a module that lives only for this call: they compile here
    bool async = kmtlConfig().backgroundShaderCompiling && !rtLaunch;
    id<MTLComputePipelineState> ps = nil;
    if (!async && !(ps = build(tr, name))) return nullptr;
    auto* p = new KPipeline();
    p->compute = ps;
    if (async) {
        p->pending = std::make_shared<KPipeline::Pending>();
        auto pending = p->pending;
        kmtlRunPipelineJob([=] {
            id<MTLComputePipelineState> state = build(tr, name);
            std::lock_guard<std::mutex> lock(pending->m);
            p->compute = state;
            pending->done.store(true, std::memory_order_release);
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
        p->throttle = kmtlThrottleFor(p->profName);
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
    for (uint32_t i = 0; i < n; i++) {
        @autoreleasepool {
            out[i] = (VkPipeline)createComputePipeline(dev, &cis[i]);
        }
    }
    return kmtlFinishPipelines(n, out);
}

VKFN(void) vkDestroyPipeline(VkDevice, VkPipeline p, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    if (p) ((KPipeline*)p)->wait();  // a worker may still write it
    delete (KPipeline*)p;
}
