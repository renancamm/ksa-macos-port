// The state the vkCmd* calls set between draws (pipelines, descriptor sets, push constants, vertex and index buffers,
// dynamic state), and its flush into the encoder before a draw or dispatch.
#include "kmtl.h"

// State

VKFN(void) vkCmdBindPipeline(VkCommandBuffer commandBuffer, VkPipelineBindPoint bp, VkPipeline pipeline) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* p = (KPipeline*)pipeline;
    p->wait();  // asynchronous creation
    if (bp != VK_PIPELINE_BIND_POINT_GRAPHICS) {  // compute, and ray tracing pipelines run as compute kernels
        cb->bound.computePipeline = p;
    } else {
        cb->bound.graphicsPipeline = p;
        if (!p->dynamicViewport && !p->viewports.empty()) { cb->bound.viewports = p->viewports; cb->bound.viewportDirty = true; }
        if (!p->dynamicScissor && !p->scissors.empty()) { cb->bound.scissors = p->scissors; cb->bound.scissorDirty = true; }
    }
}

VKFN(void) vkCmdBindDescriptorSets(VkCommandBuffer commandBuffer, VkPipelineBindPoint bp, VkPipelineLayout layout,
                                   uint32_t firstSet, uint32_t count, const VkDescriptorSet* sets, uint32_t dynCount,
                                   const uint32_t* dynOffsets) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    bool compute = bp != VK_PIPELINE_BIND_POINT_GRAPHICS;
    KBoundSet* bound = compute ? cb->bound.computeSets : cb->bound.graphicsSets;
    uint32_t& dirty = compute ? cb->bound.computeSetsDirty : cb->bound.graphicsSetsDirty;
    uint32_t dynUsed = 0;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t idx = firstSet + i;
        auto* s = (KDescriptorSet*)sets[i];
        if (!s || idx >= kMaxSets) continue;
        if (s->destroyedSlots.load(std::memory_order_relaxed)) kmtlReportDestroyedBuffer(s, idx);
        KSetLayout* l = s->layout;
        bool levels = s->levelTextures.load(std::memory_order_relaxed);
        if (l->dynamicCount == 0 && !levels) {
            bound[idx].buffer = s->buffer;
            bound[idx].offset = s->offset;
            bound[idx].slots = s->base;
            bound[idx].layout = l;
        } else {
            // Copy the set and add the dynamic offsets to its buffer addresses; a dynamic image's textures at this
            // frame's level (dynres.mm), which the set itself keeps from when it was written
            KUploadRing::Place at = cb->ring.alloc(cb->device, s->size);
            memcpy(at.ptr, s->base, s->size);
            auto* slots = (uint64_t*)at.ptr;
            slots[l->sizeSlot] = at.gpuAddress + at.offset + l->slotCount * 8;
            bound[idx].slots = (const uint8_t*)at.ptr;
            bound[idx].layout = l;
            for (uint32_t d = 0; d < l->dynamicCount && dynUsed < dynCount; d++, dynUsed++) {
                uint32_t slot = l->dynamicSlots[d];
                if (slots[slot] && slots[slot] != g_zeroPageAddress.load(std::memory_order_acquire)) slots[slot] += dynOffsets[dynUsed];
            }
            if (levels) kmtlDynamicTranslate(l, slots);
            bound[idx].buffer = at.buffer;
            bound[idx].offset = at.offset;
        }
        bound[idx].stages = l->stages;
        dirty |= 1u << idx;
    }
}

VKFN(void) vkCmdPushConstants(VkCommandBuffer commandBuffer, VkPipelineLayout, VkShaderStageFlags, uint32_t offset, uint32_t size,
                              const void* data) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (offset + size > kMaxPushConstants) return;
    memcpy(cb->bound.push + offset, data, size);
    cb->bound.pushSize = std::max(cb->bound.pushSize, offset + size);
    cb->bound.graphicsPushDirty = cb->bound.computePushDirty = true;
}

VKFN(void) vkCmdBindVertexBuffers(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkBuffer* buffers,
                                  const VkDeviceSize* offsets) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t b = first + i;
        if (b >= 16) continue;
        auto* buf = (KBuffer*)buffers[i];
        cb->bound.vertexBuffers[b].buf = buf ? buf->mtl : nil;
        cb->bound.vertexBuffers[b].off = buf ? buf->offset + offsets[i] : 0;
        cb->bound.vertexBuffers[b].size = buf && buf->size > offsets[i] ? buf->size - offsets[i] : 0;
        cb->bound.vertexBuffersDirty |= 1u << b;
    }
}

VKFN(void) vkCmdBindVertexBuffers2(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkBuffer* buffers,
                                   const VkDeviceSize* offsets, const VkDeviceSize*, const VkDeviceSize*) {
    vkCmdBindVertexBuffers(commandBuffer, first, count, buffers, offsets);
}

VKFN(void) vkCmdBindIndexBuffer(VkCommandBuffer commandBuffer, VkBuffer buffer, VkDeviceSize offset, VkIndexType type) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* buf = (KBuffer*)buffer;
    cb->bound.indexBuffer = buf->mtl;
    cb->bound.indexOffset = buf->offset + offset;
    cb->bound.indexBytes = buf->size > offset ? buf->size - offset : 0;
    cb->bound.indexType = type == VK_INDEX_TYPE_UINT32 ? MTLIndexTypeUInt32 : MTLIndexTypeUInt16;
    if (type == VK_INDEX_TYPE_UINT8) KERR_LIMIT(10, "8-bit indices not supported");
}

VKFN(void) vkCmdSetViewport(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkViewport* vps) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->bound.viewports.size() < first + count) cb->bound.viewports.resize(first + count);
    for (uint32_t i = 0; i < count; i++) cb->bound.viewports[first + i] = vps[i];
    cb->bound.viewportDirty = true;
}

VKFN(void) vkCmdSetScissor(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkRect2D* rects) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->bound.scissors.size() < first + count) cb->bound.scissors.resize(first + count);
    for (uint32_t i = 0; i < count; i++) cb->bound.scissors[first + i] = rects[i];
    cb->bound.scissorDirty = true;
}

VKFN(void) vkCmdSetDepthBias(VkCommandBuffer commandBuffer, float constant, float clamp, float slope) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    cb->bound.depthBias[0] = constant;
    cb->bound.depthBias[1] = slope;
    cb->bound.depthBias[2] = clamp;
    cb->bound.depthBiasDirty = true;
}

VKFN(void) vkCmdSetBlendConstants(VkCommandBuffer commandBuffer, const float c[4]) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    memcpy(cb->bound.blendConstants, c, sizeof(cb->bound.blendConstants));
    cb->bound.blendConstantsDirty = true;
}

VKFN(void) vkCmdSetStencilReference(VkCommandBuffer commandBuffer, VkStencilFaceFlags faces, uint32_t ref) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (faces & VK_STENCIL_FACE_FRONT_BIT) cb->bound.stencilRef[0] = ref;
    if (faces & VK_STENCIL_FACE_BACK_BIT) cb->bound.stencilRef[1] = ref;
    cb->bound.stencilRefDirty = true;
}

// ---------------------------------------------------------------------------------------------------------------
// Flushing state before draws and dispatches

// A newly bound pipeline's state, and its static state (what it declares dynamic comes from the command buffer)
static void encodePipeline(KCommandBuffer* cb, id<MTLRenderCommandEncoder> enc, KPipeline* p) {
    [enc setRenderPipelineState:p->renderEarly && kmtlEarlyFragmentTests() ? p->renderEarly : p->render];
    [enc setDepthStencilState:p->depthStencil];
    [enc setCullMode:p->cull];
    [enc setFrontFacingWinding:p->winding];
    [enc setTriangleFillMode:p->fill];
    [enc setDepthClipMode:p->depthClip];
    // Static state from the pipeline; what it declares dynamic comes from the command buffer (flushDynamicState)
    if (!p->dynamicStencilRef) [enc setStencilFrontReferenceValue:p->stencilRef[0] backReferenceValue:p->stencilRef[1]];
    else cb->bound.stencilRefDirty = true;
    if (!p->dynamicBlendConstants)
        [enc setBlendColorRed:p->blendConstants[0] green:p->blendConstants[1] blue:p->blendConstants[2] alpha:p->blendConstants[3]];
    else cb->bound.blendConstantsDirty = true;
    if (!p->depthBiasEnable) [enc setDepthBias:0 slopeScale:0 clamp:0];
    else if (!p->dynamicDepthBias) [enc setDepthBias:p->depthBias[0] slopeScale:p->depthBias[1] clamp:p->depthBias[2]];
    else cb->bound.depthBiasDirty = true;
    cb->bound.encodedPipeline = p;
}

// The dynamic state the command buffer set since: depth bias, stencil references, blend constants
static void flushDynamicState(KCommandBuffer* cb, id<MTLRenderCommandEncoder> enc, KPipeline* p) {
    if (cb->bound.depthBiasDirty && p->depthBiasEnable && p->dynamicDepthBias) {
        [enc setDepthBias:cb->bound.depthBias[0] slopeScale:cb->bound.depthBias[1] clamp:cb->bound.depthBias[2]];
        cb->bound.depthBiasDirty = false;
    }
    if (cb->bound.stencilRefDirty && p->dynamicStencilRef) {
        [enc setStencilFrontReferenceValue:cb->bound.stencilRef[0] backReferenceValue:cb->bound.stencilRef[1]];
        cb->bound.stencilRefDirty = false;
    }
    if (cb->bound.blendConstantsDirty && p->dynamicBlendConstants) {
        [enc setBlendColorRed:cb->bound.blendConstants[0] green:cb->bound.blendConstants[1] blue:cb->bound.blendConstants[2] alpha:cb->bound.blendConstants[3]];
        cb->bound.blendConstantsDirty = false;
    }
}

// The viewports, at a dynamic level scaled to it
static void flushViewports(KCommandBuffer* cb, id<MTLRenderCommandEncoder> enc) {
    if (cb->bound.viewportDirty && !cb->bound.viewports.empty()) {
        MTLViewport v[16];
        uint32_t n = (uint32_t)std::min<size_t>(cb->bound.viewports.size(), 16);
        for (uint32_t i = 0; i < n; i++) {
            const auto& vp = cb->bound.viewports[i];
            v[i] = kmtlDynamicViewport(vp, cb->pass.viewportScale[0], cb->pass.viewportScale[1]);
        }
        if (n == 1) [enc setViewport:v[0]];
        else [enc setViewports:v count:n];
        cb->bound.viewportDirty = false;
    }
}

// The scissors, within the render target (false: one is empty, nothing to draw)
static bool flushScissors(KCommandBuffer* cb, id<MTLRenderCommandEncoder> enc) {
    if (cb->bound.scissorDirty && !cb->bound.scissors.empty()) {
        MTLScissorRect r[16];
        uint32_t n = (uint32_t)std::min<size_t>(cb->bound.scissors.size(), 16);
        for (uint32_t i = 0; i < n; i++) {
            // Metal rejects scissors outside the render target: clamp. At a dynamic level, scaled outwards
            const VkRect2D& sc = cb->bound.scissors[i];
            int64_t x0, y0, x1, y1;
            kmtlDynamicRect(cb->pass.viewportScale[0], cb->pass.viewportScale[1], sc.offset.x, sc.offset.y, sc.extent.width,
                            sc.extent.height, cb->pass.width, cb->pass.height, &x0, &y0, &x1, &y1);
            if (x1 <= x0 || y1 <= y0) return false;  // empty scissor: nothing to draw (state stays dirty)
            r[i] = {(NSUInteger)x0, (NSUInteger)y0, (NSUInteger)(x1 - x0), (NSUInteger)(y1 - y0)};
        }
        if (n == 1) [enc setScissorRect:r[0]];
        else [enc setScissorRects:r count:n];
        cb->bound.scissorDirty = false;
    }
    return true;
}

// The resources bound since: vertex buffers, descriptor sets, push constants
static void flushResources(KCommandBuffer* cb, id<MTLRenderCommandEncoder> enc) {
    while (cb->bound.vertexBuffersDirty) {
        int b = __builtin_ctz(cb->bound.vertexBuffersDirty);
        cb->bound.vertexBuffersDirty &= ~(1u << b);
        if (b < 16 && cb->bound.vertexBuffers[b].buf)
            [enc setVertexBuffer:cb->bound.vertexBuffers[b].buf offset:cb->bound.vertexBuffers[b].off atIndex:vertexBufferIndex(b)];
    }
    // A set goes to the functions whose stages read it (the game's layouts name them exactly; the evaluation stage
    // is Metal's vertex function); a layout without stages goes to both
    const VkShaderStageFlags vertexStages = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_TESSELLATION_CONTROL_BIT |
        VK_SHADER_STAGE_TESSELLATION_EVALUATION_BIT | VK_SHADER_STAGE_GEOMETRY_BIT;
    while (cb->bound.graphicsSetsDirty) {
        int s = __builtin_ctz(cb->bound.graphicsSetsDirty);
        cb->bound.graphicsSetsDirty &= ~(1u << s);
        if (s >= (int)kMaxSets || !cb->bound.graphicsSets[s].buffer) continue;
        const KBoundSet& b = cb->bound.graphicsSets[s];
        if (!b.stages || (b.stages & vertexStages)) [enc setVertexBuffer:b.buffer offset:b.offset atIndex:s];
        if (!b.stages || (b.stages & VK_SHADER_STAGE_FRAGMENT_BIT)) [enc setFragmentBuffer:b.buffer offset:b.offset atIndex:s];
    }
    if (cb->bound.graphicsPushDirty && cb->bound.pushSize) {
        uint32_t n = kmtlAlignUp(cb->bound.pushSize, 16u);
        [enc setVertexBytes:cb->bound.push length:n atIndex:kPushConstantBufferIndex];
        [enc setFragmentBytes:cb->bound.push length:n atIndex:kPushConstantBufferIndex];
        cb->bound.graphicsPushDirty = false;
    }
}

bool kmtlFlushGraphics(KCommandBuffer* cb) {
    cb->stampSplit();
    KPipeline* p = cb->bound.graphicsPipeline;
    id<MTLRenderCommandEncoder> enc = cb->render;
    if (!enc || !p || !p->render) return false;
    if (cb->bound.encodedPipeline != p) encodePipeline(cb, enc, p);
    flushDynamicState(cb, enc, p);
    flushViewports(cb, enc);
    if (!flushScissors(cb, enc)) return false;
    flushResources(cb, enc);
    return true;
}

void kmtlSetDrawId(KCommandBuffer* cb, uint32_t id) {
    KPipeline* p = cb->bound.graphicsPipeline;
    if (p->vertexUsesDrawId) [cb->render setVertexBytes:&id length:4 atIndex:kDrawIdBufferIndex];
    if (p->fragmentUsesDrawId) [cb->render setFragmentBytes:&id length:4 atIndex:kDrawIdBufferIndex];
}

bool kmtlFlushCompute(KCommandBuffer* cb) {
    KPipeline* p = cb->bound.computePipeline;
    if (!p || !p->compute) return false;
    id<MTLComputeCommandEncoder> enc = cb->computeEncoder();
    [enc setComputePipelineState:p->compute];
    while (cb->bound.computeSetsDirty) {
        int s = __builtin_ctz(cb->bound.computeSetsDirty);
        cb->bound.computeSetsDirty &= ~(1u << s);
        if (s < (int)kMaxSets && cb->bound.computeSets[s].buffer)
            [enc setBuffer:cb->bound.computeSets[s].buffer offset:cb->bound.computeSets[s].offset atIndex:s];
    }
    if (cb->bound.computePushDirty && cb->bound.pushSize) {
        uint32_t n = kmtlAlignUp(cb->bound.pushSize, 16u);
        [enc setBytes:cb->bound.push length:n atIndex:kPushConstantBufferIndex];
        cb->bound.computePushDirty = false;
    }
    if (p->computeUsesDrawId) {
        uint32_t zero = 0;
        [enc setBytes:&zero length:4 atIndex:kDrawIdBufferIndex];
    }
    return true;
}

// ---------------------------------------------------------------------------------------------------------------
