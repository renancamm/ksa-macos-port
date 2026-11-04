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
        cb->computePipeline = p;
    } else {
        cb->graphicsPipeline = p;
        if (!p->dynamicViewport && !p->viewports.empty()) { cb->viewports = p->viewports; cb->viewportDirty = true; }
        if (!p->dynamicScissor && !p->scissors.empty()) { cb->scissors = p->scissors; cb->scissorDirty = true; }
    }
}

VKFN(void) vkCmdBindDescriptorSets(VkCommandBuffer commandBuffer, VkPipelineBindPoint bp, VkPipelineLayout layout,
                                   uint32_t firstSet, uint32_t count, const VkDescriptorSet* sets, uint32_t dynCount,
                                   const uint32_t* dynOffsets) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    bool compute = bp != VK_PIPELINE_BIND_POINT_GRAPHICS;
    KBoundSet* bound = compute ? cb->computeSets : cb->graphicsSets;
    uint32_t& dirty = compute ? cb->computeSetsDirty : cb->graphicsSetsDirty;
    uint32_t dynUsed = 0;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t idx = firstSet + i;
        auto* s = (KDescriptorSet*)sets[i];
        if (!s || idx >= kMaxSets) continue;
        if (s->destroyedSlots.load(std::memory_order_relaxed)) kmtlReportDestroyedBuffer(s, idx);
        KSetLayout* l = s->layout;
        if (l->dynamicCount == 0) {
            bound[idx].buffer = s->buffer;
            bound[idx].offset = s->offset;
            bound[idx].slots = s->base;
            bound[idx].layout = l;
        } else {
            // Copy the set and add the dynamic offsets to its buffer addresses
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
    memcpy(cb->push + offset, data, size);
    cb->pushSize = std::max(cb->pushSize, offset + size);
    cb->graphicsPushDirty = cb->computePushDirty = true;
}

VKFN(void) vkCmdBindVertexBuffers(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkBuffer* buffers,
                                  const VkDeviceSize* offsets) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    for (uint32_t i = 0; i < count; i++) {
        uint32_t b = first + i;
        if (b >= 16) continue;
        auto* buf = (KBuffer*)buffers[i];
        cb->vertexBuffers[b].buf = buf ? buf->mtl : nil;
        cb->vertexBuffers[b].off = buf ? buf->offset + offsets[i] : 0;
        cb->vertexBuffers[b].size = buf && buf->size > offsets[i] ? buf->size - offsets[i] : 0;
        cb->vertexBuffersDirty |= 1u << b;
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
    cb->indexBuffer = buf->mtl;
    cb->indexOffset = buf->offset + offset;
    cb->indexBytes = buf->size > offset ? buf->size - offset : 0;
    cb->indexType = type == VK_INDEX_TYPE_UINT32 ? MTLIndexTypeUInt32 : MTLIndexTypeUInt16;
    if (type == VK_INDEX_TYPE_UINT8) KERR("8-bit indices not supported");
}

VKFN(void) vkCmdSetViewport(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkViewport* vps) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->viewports.size() < first + count) cb->viewports.resize(first + count);
    for (uint32_t i = 0; i < count; i++) cb->viewports[first + i] = vps[i];
    cb->viewportDirty = true;
}

VKFN(void) vkCmdSetScissor(VkCommandBuffer commandBuffer, uint32_t first, uint32_t count, const VkRect2D* rects) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->scissors.size() < first + count) cb->scissors.resize(first + count);
    for (uint32_t i = 0; i < count; i++) cb->scissors[first + i] = rects[i];
    cb->scissorDirty = true;
}

VKFN(void) vkCmdSetDepthBias(VkCommandBuffer commandBuffer, float constant, float clamp, float slope) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    cb->depthBias[0] = constant;
    cb->depthBias[1] = slope;
    cb->depthBias[2] = clamp;
    cb->depthBiasDirty = true;
}

VKFN(void) vkCmdSetBlendConstants(VkCommandBuffer commandBuffer, const float c[4]) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    memcpy(cb->blendConstants, c, sizeof(cb->blendConstants));
    cb->blendConstantsDirty = true;
}

VKFN(void) vkCmdSetStencilReference(VkCommandBuffer commandBuffer, VkStencilFaceFlags faces, uint32_t ref) {
    KTRACE_FIRST_PLAIN();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (faces & VK_STENCIL_FACE_FRONT_BIT) cb->stencilRef[0] = ref;
    if (faces & VK_STENCIL_FACE_BACK_BIT) cb->stencilRef[1] = ref;
    cb->stencilRefDirty = true;
}

// ---------------------------------------------------------------------------------------------------------------
// Flushing state before draws and dispatches

bool kmtlFlushGraphics(KCommandBuffer* cb) {
    KPipeline* p = cb->graphicsPipeline;
    id<MTLRenderCommandEncoder> enc = cb->render;
    if (!enc || !p || !p->render) return false;
    if (cb->encodedPipeline != p) {
        [enc setRenderPipelineState:p->renderEarly && kmtlEarlyFragmentTests() ? p->renderEarly : p->render];
        [enc setDepthStencilState:p->depthStencil];
        [enc setCullMode:p->cull];
        [enc setFrontFacingWinding:p->winding];
        [enc setTriangleFillMode:p->fill];
        [enc setDepthClipMode:p->depthClip];
        // Static state from the pipeline; what it declares dynamic comes from the command buffer, below
        if (!p->dynamicStencilRef) [enc setStencilFrontReferenceValue:p->stencilRef[0] backReferenceValue:p->stencilRef[1]];
        else cb->stencilRefDirty = true;
        if (!p->dynamicBlendConstants)
            [enc setBlendColorRed:p->blendConstants[0] green:p->blendConstants[1] blue:p->blendConstants[2] alpha:p->blendConstants[3]];
        else cb->blendConstantsDirty = true;
        if (!p->depthBiasEnable) [enc setDepthBias:0 slopeScale:0 clamp:0];
        else if (!p->dynamicDepthBias) [enc setDepthBias:p->depthBias[0] slopeScale:p->depthBias[1] clamp:p->depthBias[2]];
        else cb->depthBiasDirty = true;
        cb->encodedPipeline = p;
    }
    if (cb->depthBiasDirty && p->depthBiasEnable && p->dynamicDepthBias) {
        [enc setDepthBias:cb->depthBias[0] slopeScale:cb->depthBias[1] clamp:cb->depthBias[2]];
        cb->depthBiasDirty = false;
    }
    if (cb->stencilRefDirty && p->dynamicStencilRef) {
        [enc setStencilFrontReferenceValue:cb->stencilRef[0] backReferenceValue:cb->stencilRef[1]];
        cb->stencilRefDirty = false;
    }
    if (cb->blendConstantsDirty && p->dynamicBlendConstants) {
        [enc setBlendColorRed:cb->blendConstants[0] green:cb->blendConstants[1] blue:cb->blendConstants[2] alpha:cb->blendConstants[3]];
        cb->blendConstantsDirty = false;
    }
    if (cb->viewportDirty && !cb->viewports.empty()) {
        MTLViewport v[16];
        uint32_t n = (uint32_t)std::min<size_t>(cb->viewports.size(), 16);
        for (uint32_t i = 0; i < n; i++) {
            const auto& vp = cb->viewports[i];
            v[i] = {vp.x, vp.y, vp.width, vp.height, vp.minDepth, vp.maxDepth};
        }
        if (n == 1) [enc setViewport:v[0]];
        else [enc setViewports:v count:n];
        cb->viewportDirty = false;
    }
    if (cb->scissorDirty && !cb->scissors.empty()) {
        MTLScissorRect r[16];
        uint32_t n = (uint32_t)std::min<size_t>(cb->scissors.size(), 16);
        for (uint32_t i = 0; i < n; i++) {
            // Metal rejects scissors outside the render target: clamp
            int64_t x0 = std::max<int64_t>(0, cb->scissors[i].offset.x);
            int64_t y0 = std::max<int64_t>(0, cb->scissors[i].offset.y);
            int64_t x1 = std::min<int64_t>(cb->rtWidth, (int64_t)cb->scissors[i].offset.x + cb->scissors[i].extent.width);
            int64_t y1 = std::min<int64_t>(cb->rtHeight, (int64_t)cb->scissors[i].offset.y + cb->scissors[i].extent.height);
            if (x1 <= x0 || y1 <= y0) return false;  // empty scissor: nothing to draw (state stays dirty)
            r[i] = {(NSUInteger)x0, (NSUInteger)y0, (NSUInteger)(x1 - x0), (NSUInteger)(y1 - y0)};
        }
        if (n == 1) [enc setScissorRect:r[0]];
        else [enc setScissorRects:r count:n];
        cb->scissorDirty = false;
    }
    while (cb->vertexBuffersDirty) {
        int b = __builtin_ctz(cb->vertexBuffersDirty);
        cb->vertexBuffersDirty &= ~(1u << b);
        if (b < 16 && cb->vertexBuffers[b].buf)
            [enc setVertexBuffer:cb->vertexBuffers[b].buf offset:cb->vertexBuffers[b].off atIndex:vertexBufferIndex(b)];
    }
    // A set goes to the functions whose stages read it (the game's layouts name them exactly; the evaluation stage
    // is Metal's vertex function); a layout without stages goes to both
    const VkShaderStageFlags vertexStages = VK_SHADER_STAGE_VERTEX_BIT | VK_SHADER_STAGE_TESSELLATION_CONTROL_BIT |
        VK_SHADER_STAGE_TESSELLATION_EVALUATION_BIT | VK_SHADER_STAGE_GEOMETRY_BIT;
    while (cb->graphicsSetsDirty) {
        int s = __builtin_ctz(cb->graphicsSetsDirty);
        cb->graphicsSetsDirty &= ~(1u << s);
        if (s >= (int)kMaxSets || !cb->graphicsSets[s].buffer) continue;
        const KBoundSet& b = cb->graphicsSets[s];
        if (!b.stages || (b.stages & vertexStages)) [enc setVertexBuffer:b.buffer offset:b.offset atIndex:s];
        if (!b.stages || (b.stages & VK_SHADER_STAGE_FRAGMENT_BIT)) [enc setFragmentBuffer:b.buffer offset:b.offset atIndex:s];
    }
    if (cb->graphicsPushDirty && cb->pushSize) {
        uint32_t n = kmtlAlignUp(cb->pushSize, 16u);
        [enc setVertexBytes:cb->push length:n atIndex:kPushConstantBufferIndex];
        [enc setFragmentBytes:cb->push length:n atIndex:kPushConstantBufferIndex];
        cb->graphicsPushDirty = false;
    }
    return true;
}

void kmtlSetDrawId(KCommandBuffer* cb, uint32_t id) {
    KPipeline* p = cb->graphicsPipeline;
    if (p->vertexUsesDrawId) [cb->render setVertexBytes:&id length:4 atIndex:kDrawIdBufferIndex];
    if (p->fragmentUsesDrawId) [cb->render setFragmentBytes:&id length:4 atIndex:kDrawIdBufferIndex];
}

bool kmtlFlushCompute(KCommandBuffer* cb) {
    KPipeline* p = cb->computePipeline;
    if (!p || !p->compute) return false;
    id<MTLComputeCommandEncoder> enc = cb->computeEncoder();
    [enc setComputePipelineState:p->compute];
    while (cb->computeSetsDirty) {
        int s = __builtin_ctz(cb->computeSetsDirty);
        cb->computeSetsDirty &= ~(1u << s);
        if (s < (int)kMaxSets && cb->computeSets[s].buffer)
            [enc setBuffer:cb->computeSets[s].buffer offset:cb->computeSets[s].offset atIndex:s];
    }
    if (cb->computePushDirty && cb->pushSize) {
        uint32_t n = kmtlAlignUp(cb->pushSize, 16u);
        [enc setBytes:cb->push length:n atIndex:kPushConstantBufferIndex];
        cb->computePushDirty = false;
    }
    if (p->computeUsesDrawId) {
        uint32_t zero = 0;
        [enc setBytes:&zero length:4 atIndex:kDrawIdBufferIndex];
    }
    return true;
}

// ---------------------------------------------------------------------------------------------------------------
