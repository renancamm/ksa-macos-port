// Barriers, and render passes: vkCmdBeginRenderPass*, vkCmdBeginRendering and their attachments.
#include "kmtl.h"

// Barriers

static void barrier(KCommandBuffer* cb, VkPipelineStageFlags2 srcStages, VkPipelineStageFlags2 dstStages) {
    if (cb->render) {
        // Inside a pass only framebuffer-local dependencies are legal: a memory barrier covers them
        [cb->render memoryBarrierWithScope:MTLBarrierScopeBuffers | MTLBarrierScopeTextures
                               afterStages:MTLRenderStageFragment beforeStages:MTLRenderStageVertex];
        return;
    }
    cb->barrierClasses(kmtlStageClasses(srcStages, true), kmtlStageClasses(dstStages, false));
}

VKFN(void) vkCmdPipelineBarrier(VkCommandBuffer commandBuffer, VkPipelineStageFlags src, VkPipelineStageFlags dst, VkDependencyFlags,
                                uint32_t, const VkMemoryBarrier*, uint32_t, const VkBufferMemoryBarrier*, uint32_t,
                                const VkImageMemoryBarrier*) {
    KTRACE_FIRST();
    barrier((KCommandBuffer*)commandBuffer, src, dst);
}

VKFN(void) vkCmdPipelineBarrier2(VkCommandBuffer commandBuffer, const VkDependencyInfo* di) {
    KTRACE_FIRST();
    // The union of the barriers' scopes (conservative when one call mixes unrelated barriers)
    VkPipelineStageFlags2 src = 0, dst = 0;
    for (uint32_t i = 0; i < di->memoryBarrierCount; i++) { src |= di->pMemoryBarriers[i].srcStageMask; dst |= di->pMemoryBarriers[i].dstStageMask; }
    for (uint32_t i = 0; i < di->bufferMemoryBarrierCount; i++) { src |= di->pBufferMemoryBarriers[i].srcStageMask; dst |= di->pBufferMemoryBarriers[i].dstStageMask; }
    for (uint32_t i = 0; i < di->imageMemoryBarrierCount; i++) { src |= di->pImageMemoryBarriers[i].srcStageMask; dst |= di->pImageMemoryBarriers[i].dstStageMask; }
    barrier((KCommandBuffer*)commandBuffer, src, dst);
}

// ---------------------------------------------------------------------------------------------------------------
// Render passes

MTLLoadAction kmtlLoadAction(VkAttachmentLoadOp op) {
    switch (op) {
    case VK_ATTACHMENT_LOAD_OP_CLEAR: return MTLLoadActionClear;
    case VK_ATTACHMENT_LOAD_OP_DONT_CARE: return MTLLoadActionDontCare;
    default: return MTLLoadActionLoad;
    }
}

MTLStoreAction kmtlStoreAction(VkAttachmentStoreOp op, bool resolve) {
    bool store = op != VK_ATTACHMENT_STORE_OP_DONT_CARE;
    if (resolve) return store ? MTLStoreActionStoreAndMultisampleResolve : MTLStoreActionMultisampleResolve;
    return store ? MTLStoreActionStore : MTLStoreActionDontCare;
}

MTLClearColor kmtlClearColor(VkFormat f, const VkClearColorValue& c) {
    const KFormatInfo* fi = kmtlFormat(f);
    if (fi && fi->isUint) return MTLClearColorMake(c.uint32[0], c.uint32[1], c.uint32[2], c.uint32[3]);
    if (fi && fi->isInt) return MTLClearColorMake(c.int32[0], c.int32[1], c.int32[2], c.int32[3]);
    return MTLClearColorMake(c.float32[0], c.float32[1], c.float32[2], c.float32[3]);
}

static void setAttachmentTexture(MTLRenderPassAttachmentDescriptor* a, KImageView* v) {
    a.texture = v->mtl;
    a.level = 0;
    a.slice = 0;
    if (v->mtl.textureType == MTLTextureType3D) a.depthPlane = v->baseLayer;
}

VKFN(void) vkCmdBeginRenderPass(VkCommandBuffer commandBuffer, const VkRenderPassBeginInfo* bi, VkSubpassContents) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* rp = (KRenderPass*)bi->renderPass;
    auto* fb = (KFramebuffer*)bi->framebuffer;
    KImageView* const* views = fb->attachments.data();
    uint32_t viewCount = (uint32_t)fb->attachments.size();
    if (auto* ia = (const VkRenderPassAttachmentBeginInfo*)findNext(bi->pNext, VK_STRUCTURE_TYPE_RENDER_PASS_ATTACHMENT_BEGIN_INFO)) {
        views = (KImageView* const*)ia->pAttachments;  // imageless framebuffer
        viewCount = ia->attachmentCount;
    }
    const auto& sp = rp->subpasses[0];
    MTLRenderPassDescriptor* d = [MTLRenderPassDescriptor renderPassDescriptor];
    for (size_t i = 0; i < sp.colors.size(); i++) {
        int c = sp.colors[i];
        if (c < 0 || c >= (int)viewCount) continue;
        const auto& a = rp->attachments[c];
        MTLRenderPassColorAttachmentDescriptor* ca = d.colorAttachments[i];
        setAttachmentTexture(ca, views[c]);
        ca.loadAction = kmtlLoadAction(a.loadOp);
        if (c < (int)bi->clearValueCount) ca.clearColor = kmtlClearColor(a.format, bi->pClearValues[c].color);
        int r = sp.resolves[i];
        if (r >= 0 && r < (int)viewCount) {
            ca.resolveTexture = views[r]->mtl;
            ca.resolveLevel = 0;
        }
        ca.storeAction = kmtlStoreAction(a.storeOp, r >= 0);
    }
    if (sp.depth >= 0 && sp.depth < (int)viewCount) {
        const auto& a = rp->attachments[sp.depth];
        const KFormatInfo* fi = kmtlFormat(a.format);
        KImageView* v = views[sp.depth];
        if (fi && fi->depth) {
            setAttachmentTexture(d.depthAttachment, v);
            d.depthAttachment.loadAction = kmtlLoadAction(a.loadOp);
            d.depthAttachment.storeAction = kmtlStoreAction(a.storeOp, false);
            if (sp.depth < (int)bi->clearValueCount) d.depthAttachment.clearDepth = bi->pClearValues[sp.depth].depthStencil.depth;
        }
        if (fi && fi->stencil) {
            setAttachmentTexture(d.stencilAttachment, v);
            d.stencilAttachment.loadAction = kmtlLoadAction(a.stencilLoadOp);
            d.stencilAttachment.storeAction = kmtlStoreAction(a.stencilStoreOp, false);
            if (sp.depth < (int)bi->clearValueCount) d.stencilAttachment.clearStencil = bi->pClearValues[sp.depth].depthStencil.stencil;
        }
    }
    uint32_t w = fb->width, h = fb->height;
    d.renderTargetWidth = w;
    d.renderTargetHeight = h;
    if (fb->layers > 1) d.renderTargetArrayLength = fb->layers;
    if (sp.viewMask) d.renderTargetArrayLength = 32 - __builtin_clz(sp.viewMask);
    cb->renderPass = rp;
    cb->framebuffer = fb;
    cb->barrierClasses(KCommandBuffer::kClassAll, KCommandBuffer::kClassVertex | KCommandBuffer::kClassFragment);
    cb->beginRender(d, w, h);
}

VKFN(void) vkCmdBeginRenderPass2(VkCommandBuffer commandBuffer, const VkRenderPassBeginInfo* bi, const VkSubpassBeginInfo* si) {
    vkCmdBeginRenderPass(commandBuffer, bi, si ? si->contents : VK_SUBPASS_CONTENTS_INLINE);
}

VKFN(void) vkCmdNextSubpass(VkCommandBuffer, VkSubpassContents) { KUNIMPL(); }

VKFN(void) vkCmdEndRenderPass(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->endRender();
}

VKFN(void) vkCmdEndRenderPass2(VkCommandBuffer commandBuffer, const VkSubpassEndInfo*) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->endRender();
}

static void setRenderingAttachment(MTLRenderPassAttachmentDescriptor* a, const VkRenderingAttachmentInfo& ri, bool resuming) {
    auto* v = (KImageView*)ri.imageView;
    setAttachmentTexture(a, v);
    a.loadAction = resuming ? MTLLoadActionLoad : kmtlLoadAction(ri.loadOp);
    bool resolve = ri.resolveMode != VK_RESOLVE_MODE_NONE && ri.resolveImageView;
    if (resolve) {
        a.resolveTexture = ((KImageView*)ri.resolveImageView)->mtl;
        a.resolveLevel = 0;
    }
    a.storeAction = kmtlStoreAction(ri.storeOp, resolve);
}

VKFN(void) vkCmdBeginRendering(VkCommandBuffer commandBuffer, const VkRenderingInfo* ri) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    bool resuming = ri->flags & VK_RENDERING_RESUMING_BIT;
    MTLRenderPassDescriptor* d = [MTLRenderPassDescriptor renderPassDescriptor];
    uint32_t w = ri->renderArea.offset.x + ri->renderArea.extent.width;
    uint32_t h = ri->renderArea.offset.y + ri->renderArea.extent.height;
    for (uint32_t i = 0; i < ri->colorAttachmentCount; i++) {
        const auto& a = ri->pColorAttachments[i];
        if (!a.imageView) continue;
        auto* v = (KImageView*)a.imageView;
        MTLRenderPassColorAttachmentDescriptor* ca = d.colorAttachments[i];
        setRenderingAttachment(ca, a, resuming);
        ca.clearColor = kmtlClearColor(v->format, a.clearValue.color);
        w = std::min<uint32_t>(w, (uint32_t)v->mtl.width);
        h = std::min<uint32_t>(h, (uint32_t)v->mtl.height);
    }
    if (ri->pDepthAttachment && ri->pDepthAttachment->imageView) {
        auto* v = (KImageView*)ri->pDepthAttachment->imageView;
        setRenderingAttachment(d.depthAttachment, *ri->pDepthAttachment, resuming);
        d.depthAttachment.clearDepth = ri->pDepthAttachment->clearValue.depthStencil.depth;
        if (ri->pDepthAttachment->resolveMode == VK_RESOLVE_MODE_MIN_BIT) d.depthAttachment.depthResolveFilter = MTLMultisampleDepthResolveFilterMin;
        else if (ri->pDepthAttachment->resolveMode == VK_RESOLVE_MODE_MAX_BIT) d.depthAttachment.depthResolveFilter = MTLMultisampleDepthResolveFilterMax;
        w = std::min<uint32_t>(w, (uint32_t)v->mtl.width);
        h = std::min<uint32_t>(h, (uint32_t)v->mtl.height);
    }
    if (ri->pStencilAttachment && ri->pStencilAttachment->imageView) {
        const KFormatInfo* fi = kmtlFormat(((KImageView*)ri->pStencilAttachment->imageView)->image->format);
        if (fi && fi->stencil) {
            setRenderingAttachment(d.stencilAttachment, *ri->pStencilAttachment, resuming);
            // Stencil attachments need the combined texture, not a depth-only view
            d.stencilAttachment.texture = ((KImageView*)ri->pStencilAttachment->imageView)->mtl;
            d.stencilAttachment.clearStencil = ri->pStencilAttachment->clearValue.depthStencil.stencil;
        }
    }
    d.renderTargetWidth = w;
    d.renderTargetHeight = h;
    if (ri->viewMask) d.renderTargetArrayLength = 32 - __builtin_clz(ri->viewMask);
    else if (ri->layerCount > 1) d.renderTargetArrayLength = ri->layerCount;
    cb->beginRender(d, w, h);
}

VKFN(void) vkCmdEndRendering(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->endRender();
}

// ---------------------------------------------------------------------------------------------------------------
