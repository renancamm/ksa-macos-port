// Command pools, command buffers and every vkCmd* the game records.
//
// Commands are encoded straight into an MTLCommandBuffer while recording (KSA re-records its command buffers every
// frame). Encoders are ordered by fences that follow the Vulkan barriers (see "Encoder ordering").
#include "kmtl.h"

static const MTLRenderStages kAllRenderStages = MTLRenderStageVertex | MTLRenderStageFragment;

// KSA_MTL_SYNC=strict: every encoder is of every class and every barrier covers every class, so encoders run in
// submission order
static bool strictSync() { return kmtlConfig().strictSync; }

// ---------------------------------------------------------------------------------------------------------------
// Encoder ordering
//
// Stage classes: compute, transfer, acceleration-structure build, vertex, fragment. An encoder's roles are the
// classes of the Vulkan work it carries (a clear done as a render pass is transfer work, the vertex and control
// kernels of a tessellated draw are vertex work). Each class keeps the fence updated by its last encoder (after the
// vertex stage, or after the fragment stage, for render encoders), and encoders of one class run in order. A Vulkan
// barrier adds its source classes to the pending mask of each destination class; the next encoder of that class
// waits on their last fences. So a render pass's vertex work can start under the previous pass's fragment work,
// and compute that no barrier ties to a render pass runs beside it. Strict mode is the degenerate case: every
// encoder and every barrier is of every class (stageClasses, waitForRoles).

static uint32_t classIndex(uint32_t bit) { return (uint32_t)__builtin_ctz(bit); }

// Vulkan stage masks (synchronization2 bits are a superset of the legacy ones) -> stage classes
static uint32_t stageClasses(VkPipelineStageFlags2 m, bool src) {
    using K = KCommandBuffer;
    if (strictSync()) return m ? K::kClassAll : 0;
    uint32_t c = 0;
    if (m & (VK_PIPELINE_STAGE_2_ALL_COMMANDS_BIT)) c |= K::kClassAll;
    if (m & VK_PIPELINE_STAGE_2_TOP_OF_PIPE_BIT && !src) c |= K::kClassAll;
    if (m & VK_PIPELINE_STAGE_2_BOTTOM_OF_PIPE_BIT && src) c |= K::kClassAll;
    if (m & VK_PIPELINE_STAGE_2_ALL_GRAPHICS_BIT) c |= K::kClassVertex | K::kClassFragment;
    if (m & (VK_PIPELINE_STAGE_2_DRAW_INDIRECT_BIT)) c |= K::kClassVertex | K::kClassCompute;
    if (m & (VK_PIPELINE_STAGE_2_VERTEX_INPUT_BIT | VK_PIPELINE_STAGE_2_VERTEX_SHADER_BIT |
             VK_PIPELINE_STAGE_2_TESSELLATION_CONTROL_SHADER_BIT | VK_PIPELINE_STAGE_2_TESSELLATION_EVALUATION_SHADER_BIT |
             VK_PIPELINE_STAGE_2_GEOMETRY_SHADER_BIT | VK_PIPELINE_STAGE_2_INDEX_INPUT_BIT |
             VK_PIPELINE_STAGE_2_VERTEX_ATTRIBUTE_INPUT_BIT | VK_PIPELINE_STAGE_2_PRE_RASTERIZATION_SHADERS_BIT))
        c |= K::kClassVertex;
    if (m & (VK_PIPELINE_STAGE_2_FRAGMENT_SHADER_BIT | VK_PIPELINE_STAGE_2_EARLY_FRAGMENT_TESTS_BIT |
             VK_PIPELINE_STAGE_2_LATE_FRAGMENT_TESTS_BIT | VK_PIPELINE_STAGE_2_COLOR_ATTACHMENT_OUTPUT_BIT))
        c |= K::kClassFragment;
    if (m & (VK_PIPELINE_STAGE_2_COMPUTE_SHADER_BIT | VK_PIPELINE_STAGE_2_RAY_TRACING_SHADER_BIT_KHR)) c |= K::kClassCompute;
    if (m & (VK_PIPELINE_STAGE_2_TRANSFER_BIT | VK_PIPELINE_STAGE_2_COPY_BIT | VK_PIPELINE_STAGE_2_BLIT_BIT |
             VK_PIPELINE_STAGE_2_RESOLVE_BIT | VK_PIPELINE_STAGE_2_CLEAR_BIT))
        c |= K::kClassTransfer;
    if (m & (VK_PIPELINE_STAGE_2_ACCELERATION_STRUCTURE_BUILD_BIT_KHR | VK_PIPELINE_STAGE_2_ACCELERATION_STRUCTURE_COPY_BIT_KHR))
        c |= K::kClassAccel;
    return c;
}

void KCommandBuffer::beginCommands() {
    for (auto& f : lastFence) f = queue->chain;
    for (auto& m : needMask) m = 0;
    encRoles = 0;
    encWaited.clear();
    fenceNext = 0;
    resolveWaited = resolveSignaled;
    passResolves = false;
}

id<MTLFence> KCommandBuffer::newFence() {
    if (fenceNext == fencePool.size()) fencePool.push_back([device->mtl newFence]);
    return fencePool[fenceNext++];
}

id<MTLFence> KCommandBuffer::finishFence(uint32_t roles) {
    id<MTLFence> f = newFence();
    for (uint32_t r = roles; r; r &= r - 1) lastFence[classIndex(r & -r)] = f;
    return f;
}

// The fences an encoder with these roles waits on; clears the pending masks it satisfies
static void collectWaits(KCommandBuffer* cb, uint32_t roles, std::vector<id<MTLFence>>& out) {
    for (uint32_t r = roles; r; r &= r - 1) {
        uint32_t i = classIndex(r & -r);
        uint32_t need = cb->needMask[i] | (1u << i);
        cb->needMask[i] = 0;
        for (uint32_t s = need; s; s &= s - 1) {
            id<MTLFence> f = cb->lastFence[classIndex(s & -s)];
            if (f && std::find(out.begin(), out.end(), f) == out.end()) out.push_back(f);
        }
    }
}

template <typename E> void KCommandBuffer::waitForRoles(E enc, uint32_t roles) {
    if (strictSync()) roles = kClassAll;
    encRoles = roles;
    encWaited.clear();
    collectWaits(this, roles, encWaited);
    for (id<MTLFence> f : encWaited) [enc waitForFence:f];
}
template <> void KCommandBuffer::waitForRoles(id<MTLRenderCommandEncoder> enc, uint32_t roles) {
    if (strictSync()) roles = kClassAll;
    encRoles = roles;
    encWaited.clear();
    // Waiting before the vertex stage covers the fragment stage too
    collectWaits(this, roles & ~kClassFragment, encWaited);
    for (id<MTLFence> f : encWaited) [enc waitForFence:f beforeStages:MTLRenderStageVertex];
    if (roles & kClassFragment) {
        std::vector<id<MTLFence>> frag;
        collectWaits(this, kClassFragment, frag);
        for (id<MTLFence> f : frag) {
            if (std::find(encWaited.begin(), encWaited.end(), f) != encWaited.end()) continue;
            [enc waitForFence:f beforeStages:MTLRenderStageFragment];
            encWaited.push_back(f);
        }
    }
}
template void KCommandBuffer::waitForRoles(id<MTLAccelerationStructureCommandEncoder>, uint32_t);

// A Vulkan barrier outside a render pass (inside one, barriers are memory barriers in the render encoder)
void KCommandBuffer::barrierClasses(uint32_t src, uint32_t dst) {
    if (!src || !dst) return;
    for (uint32_t d = dst; d; d &= d - 1) needMask[classIndex(d & -d)] |= src;
    if (blit && (dst & kClassTransfer)) { endEncoder(); return; }  // blit commands of one encoder may overlap
    if (!compute) return;
    if (!(dst & encRoles)) return;
    // Later work in the open compute encoder needs the sources: fine if they are this encoder's own earlier work
    // (a memory barrier orders it) or encoders it already waited on
    bool reopen = false;
    for (uint32_t s = src & ~encRoles; s; s &= s - 1) {
        id<MTLFence> f = lastFence[classIndex(s & -s)];
        if (f && std::find(encWaited.begin(), encWaited.end(), f) == encWaited.end()) reopen = true;
    }
    if (reopen) endEncoder();
    else [compute memoryBarrierWithScope:MTLBarrierScopeBuffers | MTLBarrierScopeTextures];
}

// End of recording: one encoder that waits on the last fence of every class and updates the chain fence
void KCommandBuffer::joinCommands() {
    if (!mtl) return;
    endEncoder();
    // Later command buffers start after the chain fence, which the join updates after resolves still in flight
    bool resolving = resolveWaited != resolveSignaled;
    if (resolving) {
        [mtl encodeWaitForEvent:resolveEvent value:resolveSignaled];
        resolveWaited = resolveSignaled;
    }
    id<MTLBlitCommandEncoder> b = [mtl blitCommandEncoder];
    std::vector<id<MTLFence>> seen;
    for (id<MTLFence> f : lastFence) {
        if (!f || std::find(seen.begin(), seen.end(), f) != seen.end()) continue;
        seen.push_back(f);
        [b waitForFence:f];
    }
    [b updateFence:queue->chain];
    [b endEncoding];
    for (auto& f : lastFence) f = queue->chain;
}


// ---------------------------------------------------------------------------------------------------------------
// Upload ring (dynamic-offset descriptor copies, vkCmdUpdateBuffer data)

void* KUploadRing::alloc(KDevice* dev, size_t size, id<MTLBuffer>* outBuf, NSUInteger* outOff) {
    size = (size + 255) & ~(size_t)255;
    while (true) {
        if (chunk < chunks.size()) {
            id<MTLBuffer> b = chunks[chunk];
            if (offset + size <= b.length) {
                *outBuf = b;
                *outOff = offset;
                void* p = (uint8_t*)b.contents + offset;
                offset += size;
                return p;
            }
            chunk++;
            offset = 0;
            continue;
        }
        size_t len = std::max(size, kChunkSize);
        id<MTLBuffer> b = [dev->mtl newBufferWithLength:len options:MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked];
        b.label = @"upload ring";

        g_internalBytes += (int64_t)len;
        chunks.push_back(b);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Encoders

// The encoder's name: its first pipeline (shader hashes, as the GPU profiler names them), how many others it ran, and
// a render pass's size
void KCommandBuffer::notePipelineLabel(KPipeline* p) {
    if (!p || p->profId == encLastPipeline) return;
    if (encLabel.empty()) encLabel = p->profName;
    encPipelines++;
    encLastPipeline = p->profId;
}

template <typename E> void KCommandBuffer::nameEncoder(E enc) {
    if (!encLabel.empty()) {
        std::string name = encLabel;
        if (encPipelines > 1) name += " +" + std::to_string(encPipelines - 1);
        if (render) name = std::to_string(rtWidth) + "x" + std::to_string(rtHeight) + " " + name;
        enc.label = [NSString stringWithUTF8String:name.c_str()];
    }
    encLabel.clear();
    encPipelines = 0;
    encLastPipeline = 0;
}

void KCommandBuffer::endEncoder() {
    if (render && deferredStores) {
        for (int i = 0; i < 8; i++) {
            id<MTLTexture> t = passDesc.colorAttachments[i].texture;
            if (!t) continue;
            bool tileOnly = t.storageMode == MTLStorageModeMemoryless;
            if (suspending && tileOnly) {
                static std::atomic<bool> warned{false};
                if (!warned.exchange(true)) KERR("tessellated draw in a pass with a memoryless attachment: its contents are lost");
            }
            [render setColorStoreAction:suspending && !tileOnly ? MTLStoreActionStore : colorStores[i] atIndex:i];
        }
        if (passDesc.depthAttachment.texture) [render setDepthStoreAction:suspending ? MTLStoreActionStore : depthStore];
        if (passDesc.stencilAttachment.texture) [render setStencilStoreAction:suspending ? MTLStoreActionStore : stencilStore];
        if (!suspending) passDesc = nil;
    }
    if (render) {
        if (encRoles & kClassVertex) [render updateFence:finishFence(kClassVertex) afterStages:MTLRenderStageVertex];
        uint32_t late = encRoles & ~kClassVertex;
        if (late) [render updateFence:finishFence(late) afterStages:kAllRenderStages];
        nameEncoder(render);
        [render endEncoding];
        render = nil;
        inRenderPass = false;
        if (passResolves && !suspending) {
            passResolves = false;
            if (!resolveEvent) resolveEvent = [device->mtl newEvent];
            [mtl encodeSignalEvent:resolveEvent value:++resolveSignaled];
        }
    }
    if (compute) {
        [compute updateFence:finishFence(encRoles)];
        nameEncoder(compute);
        [compute endEncoding];
        compute = nil;
    }
    if (blit) {
        [blit updateFence:finishFence(encRoles)];
        nameEncoder(blit);
        [blit endEncoding];
        blit = nil;
    }
    encRoles = 0;
    kmtlProfEncoderEnded(this);
}

// Before a compute or blit encoder: MSAA resolves of earlier render passes are not covered by their fences (on Apple
// GPUs, work ordered after a pass's fragment fence could read the resolve texture's previous contents: KSA's ocean
// copies the resolved scene with a compute kernel), so an encoder that depends on fragment work waits for the
// resolves through the event. One wait covers every earlier resolve of the command buffer. Render passes do not wait:
// KSA's passes after a resolve load the multisample attachments, and those that sample resolved images come after
// compute work that has waited; waiting there would cost the overlap of every pass with the previous one.
void KCommandBuffer::waitForResolves(uint32_t roles) {
    if (resolveWaited == resolveSignaled) return;
    bool dependent = false;
    for (uint32_t r = roles; r; r &= r - 1)
        if (needMask[classIndex(r & -r)] & kClassFragment) dependent = true;
    if (!dependent) return;
    [mtl encodeWaitForEvent:resolveEvent value:resolveSignaled];
    resolveWaited = resolveSignaled;
}

id<MTLComputeCommandEncoder> KCommandBuffer::computeEncoder(uint32_t roles) {
    if (compute && (encRoles & roles) == roles) return compute;
    endEncoder();
    waitForResolves(roles);
    compute = kmtlProfComputeEncoder(this);
    waitForRoles(compute, roles);
    computeSetsDirty = ~0u;
    computePushDirty = true;
    return compute;
}

id<MTLBlitCommandEncoder> KCommandBuffer::blitEncoder() {
    if (blit) return blit;
    endEncoder();
    waitForResolves(kClassTransfer);
    blit = kmtlProfBlitEncoder(this);
    waitForRoles(blit, kClassTransfer);
    return blit;
}

void KCommandBuffer::beginRender(MTLRenderPassDescriptor* rp, uint32_t w, uint32_t h, bool resuming, uint32_t roles) {
    endEncoder();
    if (!resuming) {
        passResolves = rp.depthAttachment.resolveTexture || rp.stencilAttachment.resolveTexture;
        for (int i = 0; i < 8; i++) passResolves |= rp.colorAttachments[i].resolveTexture != nil;
    }
    deferredStores = kmtlConfig().tessellation;
    if (deferredStores) {
        if (!resuming) {
            for (int i = 0; i < 8; i++) colorStores[i] = rp.colorAttachments[i].storeAction;
            depthStore = rp.depthAttachment.storeAction;
            stencilStore = rp.stencilAttachment.storeAction;
        }
        for (int i = 0; i < 8; i++)
            if (rp.colorAttachments[i].texture) rp.colorAttachments[i].storeAction = MTLStoreActionUnknown;
        if (rp.depthAttachment.texture) rp.depthAttachment.storeAction = MTLStoreActionUnknown;
        if (rp.stencilAttachment.texture) rp.stencilAttachment.storeAction = MTLStoreActionUnknown;
        passDesc = rp;
    }
    kmtlProfRenderPass(this, rp);
    render = [mtl renderCommandEncoderWithDescriptor:rp];
    if (!render) {
        KERR("render encoder creation failed (%ux%u)", w, h);
        return;
    }
    waitForRoles(render, roles);
    rtWidth = w;
    rtHeight = h;
    inRenderPass = true;
    encodedPipeline = nullptr;
    graphicsSetsDirty = ~0u;
    graphicsPushDirty = true;
    vertexBuffersDirty = ~0u;
    viewportDirty = scissorDirty = true;
    passes++;
}

void KCommandBuffer::suspendRender() {
    suspending = true;
    endEncoder();
    suspending = false;
}

void KCommandBuffer::resumeRender() {
    MTLRenderPassDescriptor* d = [passDesc copy];
    for (int i = 0; i < 8; i++)
        if (d.colorAttachments[i].texture)
            d.colorAttachments[i].loadAction = d.colorAttachments[i].texture.storageMode == MTLStorageModeMemoryless
                ? MTLLoadActionDontCare : MTLLoadActionLoad;
    if (d.depthAttachment.texture) d.depthAttachment.loadAction = MTLLoadActionLoad;
    if (d.stencilAttachment.texture) d.stencilAttachment.loadAction = MTLLoadActionLoad;
    beginRender(d, rtWidth, rtHeight, true);
    passes--;
}

void KCommandBuffer::endRender() {
    endEncoder();
    renderPass = nullptr;
    framebuffer = nullptr;
    for (auto& d : tsDeferred) writeTimestamp(d.first, d.second);
    tsDeferred.clear();
}

void KCommandBuffer::resetState() {
    graphicsPipeline = computePipeline = nullptr;
    for (auto& s : graphicsSets) s = KBoundSet();
    for (auto& s : computeSets) s = KBoundSet();
    for (auto& v : vertexBuffers) v = VB();
    indexBuffer = nil;
    viewports.clear();
    scissors.clear();
    pushSize = 0;
    draws = dispatches = passes = 0;
    encodedPipeline = nullptr;
}

// ---------------------------------------------------------------------------------------------------------------
// Pools and buffers

VKFN(VkResult) vkCreateCommandPool(VkDevice device, const VkCommandPoolCreateInfo* ci, const VkAllocationCallbacks*, VkCommandPool* out) {
    KTRACE_FIRST();
    auto* p = new KCommandPool();
    p->device = (KDevice*)device;
    p->queue = p->device->queueOf(ci->queueFamilyIndex);
    *out = (VkCommandPool)p;
    return VK_SUCCESS;
}

static void dropRecording(KCommandBuffer* cb) {
    if (cb->mtl) {
        cb->endEncoder();
        cb->mtl = nil;
    }
    cb->releaseTimestamps();
    kmtlProfDrop(cb);
}

VKFN(void) vkDestroyCommandPool(VkDevice, VkCommandPool pool, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    auto* p = (KCommandPool*)pool;
    if (!p) return;
    for (auto* cb : p->buffers) {
        dropRecording(cb);
        delete cb;
    }
    delete p;
}

VKFN(VkResult) vkResetCommandPool(VkDevice, VkCommandPool pool, VkCommandPoolResetFlags) {
    KTRACE_FIRST();
    for (auto* cb : ((KCommandPool*)pool)->buffers) dropRecording(cb);
    return VK_SUCCESS;
}

VKFN(void) vkTrimCommandPool(VkDevice, VkCommandPool, VkCommandPoolTrimFlags) {}

VKFN(VkResult) vkAllocateCommandBuffers(VkDevice device, const VkCommandBufferAllocateInfo* ai, VkCommandBuffer* out) {
    KTRACE_FIRST();
    auto* p = (KCommandPool*)ai->commandPool;
    if (ai->level != VK_COMMAND_BUFFER_LEVEL_PRIMARY) KERR("secondary command buffers are not supported");
    for (uint32_t i = 0; i < ai->commandBufferCount; i++) {
        auto* cb = new KCommandBuffer();
        cb->device = (KDevice*)device;
        cb->pool = p;
        cb->queue = p->queue;
        p->buffers.push_back(cb);
        out[i] = (VkCommandBuffer)cb;
    }
    return VK_SUCCESS;
}

VKFN(void) vkFreeCommandBuffers(VkDevice, VkCommandPool pool, uint32_t n, const VkCommandBuffer* cbs) {
    KTRACE_FIRST();
    auto* p = (KCommandPool*)pool;
    for (uint32_t i = 0; i < n; i++) {
        auto* cb = (KCommandBuffer*)cbs[i];
        if (!cb) continue;
        dropRecording(cb);
        p->buffers.erase(std::remove(p->buffers.begin(), p->buffers.end(), cb), p->buffers.end());
        delete cb;
    }
}

VKFN(VkResult) vkResetCommandBuffer(VkCommandBuffer commandBuffer, VkCommandBufferResetFlags) {
    KTRACE_FIRST();
    dropRecording((KCommandBuffer*)commandBuffer);
    return VK_SUCCESS;
}

VKFN(VkResult) vkBeginCommandBuffer(VkCommandBuffer commandBuffer, const VkCommandBufferBeginInfo*) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    dropRecording(cb);
    cb->mtl = [cb->queue->mtl commandBuffer];
    kmtlProfBegin(cb);
    cb->beginCommands();
    cb->resetState();
    cb->ring.reset();
    return VK_SUCCESS;
}

VKFN(VkResult) vkEndCommandBuffer(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->inRenderPass) KERR("vkEndCommandBuffer inside a render pass");
    cb->endEncoder();
    cb->joinCommands();
    return VK_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------------
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
        KSetLayout* l = s->layout;
        if (l->dynamicCount == 0) {
            bound[idx].buffer = s->buffer;
            bound[idx].offset = s->offset;
        } else {
            // Copy the set and add the dynamic offsets to its buffer addresses
            id<MTLBuffer> buf;
            NSUInteger off;
            uint8_t* dst = (uint8_t*)cb->ring.alloc(cb->device, s->size, &buf, &off);
            memcpy(dst, s->base, s->size);
            auto* slots = (uint64_t*)dst;
            slots[l->sizeSlot] = buf.gpuAddress + off + l->slotCount * 8;
            for (uint32_t d = 0; d < l->dynamicCount && dynUsed < dynCount; d++, dynUsed++) {
                uint32_t slot = l->dynamicSlots[d];
                if (slots[slot]) slots[slot] += dynOffsets[dynUsed];
            }
            bound[idx].buffer = buf;
            bound[idx].offset = off;
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
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->render) [cb->render setBlendColorRed:c[0] green:c[1] blue:c[2] alpha:c[3]];
}

VKFN(void) vkCmdSetStencilReference(VkCommandBuffer commandBuffer, VkStencilFaceFlags, uint32_t ref) {
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->render) [cb->render setStencilReferenceValue:ref];
}

// ---------------------------------------------------------------------------------------------------------------
// Flushing state before draws and dispatches

static bool flushGraphics(KCommandBuffer* cb) {
    KPipeline* p = cb->graphicsPipeline;
    id<MTLRenderCommandEncoder> enc = cb->render;
    if (!enc || !p || !p->render) return false;
    if (cb->encodedPipeline != p) {
        [enc setRenderPipelineState:p->render];
        [enc setDepthStencilState:p->depthStencil];
        [enc setCullMode:p->cull];
        [enc setFrontFacingWinding:p->winding];
        [enc setTriangleFillMode:p->fill];
        [enc setDepthClipMode:p->depthClip];
        [enc setStencilFrontReferenceValue:p->stencilRef[0] backReferenceValue:p->stencilRef[1]];
        [enc setBlendColorRed:p->blendConstants[0] green:p->blendConstants[1] blue:p->blendConstants[2] alpha:p->blendConstants[3]];
        if (!p->depthBiasEnable) [enc setDepthBias:0 slopeScale:0 clamp:0];
        else if (!p->dynamicDepthBias) [enc setDepthBias:p->depthBias[0] slopeScale:p->depthBias[1] clamp:p->depthBias[2]];
        else cb->depthBiasDirty = true;
        cb->encodedPipeline = p;
    }
    if (cb->depthBiasDirty && p->depthBiasEnable && p->dynamicDepthBias) {
        [enc setDepthBias:cb->depthBias[0] slopeScale:cb->depthBias[1] clamp:cb->depthBias[2]];
        cb->depthBiasDirty = false;
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
        uint32_t n = (cb->pushSize + 15) & ~15u;
        [enc setVertexBytes:cb->push length:n atIndex:kPushConstantBufferIndex];
        [enc setFragmentBytes:cb->push length:n atIndex:kPushConstantBufferIndex];
        cb->graphicsPushDirty = false;
    }
    return true;
}

// Multiview: one draw per view in the mask, with spvViewMask = {view, 1}
template <typename F> static void forEachView(KCommandBuffer* cb, F&& draw) {
    uint32_t mask = cb->graphicsPipeline->viewMask;
    if (!mask) { draw(); return; }
    while (mask) {
        uint32_t v = __builtin_ctz(mask);
        mask &= mask - 1;
        uint32_t vm[2] = {v, 1};
        [cb->render setVertexBytes:vm length:sizeof(vm) atIndex:kViewMaskBufferIndex];
        [cb->render setFragmentBytes:vm length:sizeof(vm) atIndex:kViewMaskBufferIndex];
        draw();
    }
}

static void setDrawId(KCommandBuffer* cb, uint32_t id) {
    KPipeline* p = cb->graphicsPipeline;
    if (p->vertexUsesDrawId) [cb->render setVertexBytes:&id length:4 atIndex:kDrawIdBufferIndex];
    if (p->fragmentUsesDrawId) [cb->render setFragmentBytes:&id length:4 atIndex:kDrawIdBufferIndex];
}

static bool flushCompute(KCommandBuffer* cb) {
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
        uint32_t n = (cb->pushSize + 15) & ~15u;
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
// Tessellated draws: the render pass is suspended, the vertex stage (as a kernel with stage-in vertex fetch) and the
// control stage (multi-patch kernel) write their outputs into scratch buffers, then the pass resumes and Metal's
// tessellator feeds the evaluation stage. Indirect draws get their stage-in region, dispatch sizes and patch
// count from a one-thread prep kernel; their scratch is sized from an index cap that grows to the largest draw seen
// (kmtlTessCap), within the bound index buffer's capacity.

struct KTessDraw {
    bool indexed = false, indirect = false;
    uint32_t count = 0, instances = 1, first = 0, firstInstance = 0;
    int32_t vertexOffset = 0;
    KBuffer* indirectBuf = nullptr;
    VkDeviceSize indirectOffset = 0;
};

static uint32_t lcm(uint32_t a, uint32_t b) {
    uint32_t x = a, y = b;
    while (y) { uint32_t t = x % y; x = y; y = t; }
    return a / x * b;
}

id<MTLComputePipelineState> kmtlTessPrepPipeline(KDevice* dev);
id<MTLBuffer> kmtlTessStats(KDevice* dev);
uint32_t kmtlTessCap(KDevice* dev);

// The shared scratch buffer, at least `size` bytes. A grown buffer replaces the old one for later draws; command
// buffers already recorded keep the old one alive until they complete
static id<MTLBuffer> tessScratch(KDevice* dev, uint64_t size) {
    std::lock_guard<std::mutex> lock(dev->tessScratchMutex);
    if (dev->tessScratch && dev->tessScratch.length >= size) return dev->tessScratch;
    uint64_t len = (size + (16 << 20) - 1) & ~(uint64_t)((16 << 20) - 1);
    id<MTLBuffer> b = [dev->mtl newBufferWithLength:len options:MTLResourceStorageModePrivate | MTLResourceHazardTrackingModeUntracked];
    if (!b) {
        KERR("tessellation scratch of %llu MB could not be allocated", (unsigned long long)(len >> 20));
        return nil;
    }
    b.label = @"tessellation scratch";
    g_internalBytes += (int64_t)len - (int64_t)(dev->tessScratch ? dev->tessScratch.length : 0);
    KLOG(1, "tessellation scratch: %llu MB", (unsigned long long)(len >> 20));
    dev->tessScratch = b;
    return b;
}

static void tessDraw(KCommandBuffer* cb, const KTessDraw& t) {
    KPipeline* p = cb->graphicsPipeline;
    KDevice* dev = cb->device;
    int idxType = t.indexed ? (cb->indexType == MTLIndexTypeUInt16 ? 1 : 2) : 0;
    NSUInteger idxSize = idxType == 1 ? 2 : 4;
    uint64_t maxVerts;
    if (t.indirect) {
        if (!t.indexed) {
            static std::atomic<bool> warned{false};
            if (!warned.exchange(true)) KERR("non-indexed indirect tessellated draws are not supported");
            return;
        }
        maxVerts = std::min<uint64_t>(cb->indexBytes / idxSize, kmtlTessCap(dev));
    } else {
        maxVerts = (uint64_t)t.count * t.instances;
    }
    uint64_t maxPatches = maxVerts / p->tessInCP;
    // Fused (KSA_MTL_TESS_FUSE): 32-bit indices, as the control point index buffer, and no instances or base vertex
    // for direct draws (indirect ones are checked by the prep kernel's statistics)
    bool fused = p->renderFused && idxType == 2 && t.instances == 1 && (t.indirect || t.vertexOffset == 0);
    const KTessStages& st = fused ? p->tessFused : p->tessPlain;
    if (!maxPatches || !st.vertex[idxType] || !st.control) return;
    // Scratch layout: vertex outputs, control outputs, per-patch outputs, tessellation factors
    auto align = [](uint64_t n) { return (n + 255) & ~255ull; };
    NSUInteger levelSize = p->tessQuads ? sizeof(MTLQuadTessellationFactorsHalf) : sizeof(MTLTriangleTessellationFactorsHalf);
    NSUInteger vtxOff = 0;
    NSUInteger tcOff = vtxOff + align(std::max<uint64_t>(maxVerts * st.vertexStride, 16));
    uint64_t tcBytes = p->tessPassthrough ? 16 : maxPatches * p->tessOutCP * st.controlStride;  // fused is passthrough too
    NSUInteger patchOff = tcOff + align(std::max<uint64_t>(tcBytes, 16));
    NSUInteger levelsOff = patchOff + align(maxPatches * st.patchStride);
    uint64_t scratchSize = levelsOff + align(maxPatches * levelSize);
    id<MTLBuffer> scratch = tessScratch(dev, scratchSize);
    if (!scratch) return;
    {
        static std::atomic<int> logged{0};
        if (g_logLevel >= 1 && logged++ < 12)
            KLOG(1, "tessellated draw: %s%s, up to %llu vertices / %llu patches, scratch %.1f MB", t.indexed ? "indexed" : "",
                 t.indirect ? " indirect" : "", (unsigned long long)maxVerts, (unsigned long long)maxPatches, scratchSize / 1048576.0);
    }
    id<MTLComputePipelineState> vps = st.vertex[idxType];
    uint32_t vw = (uint32_t)vps.threadExecutionWidth;
    uint32_t wg = lcm(p->tessOutCP, 32);
    while (wg > st.control.maxTotalThreadsPerThreadgroup && wg > p->tessOutCP) wg /= 2;
    uint32_t patches = t.indirect ? 0 : (t.count / p->tessInCP) * t.instances;

    // The game's graphics resources, bound for a kernel (the control kernel has no stage-in, so no vertex buffers)
    auto bindGame = [&](id<MTLComputeCommandEncoder> e, bool vertexBuffers) {
        for (uint32_t s = 0; s < kMaxSets; s++)
            if (cb->graphicsSets[s].buffer) [e setBuffer:cb->graphicsSets[s].buffer offset:cb->graphicsSets[s].offset atIndex:s];
        if (cb->pushSize) [e setBytes:cb->push length:(cb->pushSize + 15) & ~15u atIndex:kPushConstantBufferIndex];
        for (uint32_t b = 0; vertexBuffers && b < 16; b++)
            if (cb->vertexBuffers[b].buf) [e setBuffer:cb->vertexBuffers[b].buf offset:cb->vertexBuffers[b].off atIndex:vertexBufferIndex(b)];
    };

    // The vertex and control kernels are the draw's vertex work: they wait on what the draw's vertex stage would
    cb->suspendRender();
    id<MTLComputeCommandEncoder> enc = cb->computeEncoder(KCommandBuffer::kClassVertex);
    id<MTLBuffer> args = nil;
    NSUInteger argsOff = 0;
    if (t.indirect) {
        // args: [0..5] stage-in region, [6..8] vertex groups, [9..11] control groups, [12..13] control params,
        // [14..17] drawPatches arguments
        cb->ring.alloc(dev, 19 * 4, &args, &argsOff);
        uint32_t prm[4] = {p->tessInCP, p->tessOutCP, vw, wg};
        uint32_t maxIdx = (uint32_t)maxVerts;
        [enc setBytes:&maxIdx length:4 atIndex:3];
        [enc setBuffer:kmtlTessStats(dev) offset:0 atIndex:4];
        [enc setComputePipelineState:kmtlTessPrepPipeline(dev)];
        [enc setBuffer:t.indirectBuf->mtl offset:t.indirectBuf->offset + t.indirectOffset atIndex:0];
        [enc setBuffer:args offset:argsOff atIndex:1];
        [enc setBytes:prm length:sizeof(prm) atIndex:2];
        [enc dispatchThreads:MTLSizeMake(1, 1, 1) threadsPerThreadgroup:MTLSizeMake(1, 1, 1)];
        [enc memoryBarrierWithScope:MTLBarrierScopeBuffers];
        static std::atomic<int> sampled{0};
        if (g_logLevel >= 2 && sampled++ % 200 == 0) {
            const uint32_t* a = (const uint32_t*)((uint8_t*)args.contents + argsOff);
            [cb->mtl addCompletedHandler:^(id<MTLCommandBuffer>) {
                KLOG(2, "tessellated draw actual: %u indices (%u requested), %u patches", a[3], a[18], a[13]);
            }];
        }
    }
    bindGame(enc, true);

    // Vertex stage
    id<MTLBuffer> vtxOut = scratch, tcOut = scratch, patchOut = st.patchStride ? scratch : nil, levels = scratch;
    [enc setComputePipelineState:vps];
    [enc setBuffer:vtxOut offset:vtxOff atIndex:kTessOutputBufferIndex];
    if (t.indexed)
        [enc setBuffer:cb->indexBuffer offset:cb->indexOffset + (t.indirect ? 0 : t.first * idxSize) atIndex:kTessIndexBufferIndex];
    if (t.indirect) {
        [enc setStageInRegionWithIndirectBuffer:args indirectBufferOffset:argsOff];
        [enc dispatchThreadgroupsWithIndirectBuffer:args indirectBufferOffset:argsOff + 6 * 4 threadsPerThreadgroup:MTLSizeMake(vw, 1, 1)];
    } else {
        NSUInteger originX = t.indexed ? (NSUInteger)(int64_t)t.vertexOffset : t.first;
        [enc setStageInRegion:MTLRegionMake2D(originX, t.firstInstance, t.count, t.instances)];
        [enc dispatchThreads:MTLSizeMake(t.count, t.instances, 1) threadsPerThreadgroup:MTLSizeMake(vw, 1, 1)];
    }
    [enc memoryBarrierWithScope:MTLBarrierScopeBuffers];
    if (kmtlConfig().gpuprofSplit) {
        // Profiling: the control stage in its own encoder
        kmtlProfNote(cb, p, true, 1);
        cb->endEncoder();
        enc = cb->computeEncoder(KCommandBuffer::kClassVertex);
        bindGame(enc, false);
    }

    // Control stage
    [enc setComputePipelineState:st.control];
    [enc setBuffer:vtxOut offset:vtxOff atIndex:kTessInputBufferIndex];
    [enc setBuffer:tcOut offset:tcOff atIndex:kTessOutputBufferIndex];
    if (patchOut) [enc setBuffer:patchOut offset:patchOff atIndex:kTessPatchBufferIndex];
    [enc setBuffer:levels offset:levelsOff atIndex:kTessFactorBufferIndex];
    if (t.indirect) {
        [enc setBuffer:args offset:argsOff + 12 * 4 atIndex:kTessParamsBufferIndex];
        [enc dispatchThreadgroupsWithIndirectBuffer:args indirectBufferOffset:argsOff + 9 * 4 threadsPerThreadgroup:MTLSizeMake(wg, 1, 1)];
    } else {
        uint32_t prm[2] = {p->tessInCP, patches};
        [enc setBytes:prm length:sizeof(prm) atIndex:kTessParamsBufferIndex];
        [enc dispatchThreads:MTLSizeMake((NSUInteger)patches * p->tessOutCP, 1, 1) threadsPerThreadgroup:MTLSizeMake(wg, 1, 1)];
    }
    kmtlProfNote(cb, p, true, 2);
    cb->endEncoder();  // the resumed pass waits on the kernels (vertex class) and the suspended pass (fragment class)

    // Evaluation and rasterization
    cb->resumeRender();
    if (!flushGraphics(cb)) return;
    id<MTLRenderCommandEncoder> r = cb->render;
    if (fused) {
        // The evaluation stage fetches each control point's vertex attributes itself
        [r setRenderPipelineState:p->renderFused];
        cb->encodedPipeline = nullptr;  // the next draw sets its pipeline state again
        [r setVertexBuffer:levels offset:levelsOff atIndex:kTessFactorBufferIndex];
        [r setTessellationFactorBuffer:levels offset:levelsOff instanceStride:0];
        id<MTLBuffer> cpIndices = cb->indexBuffer;
        if (t.indirect)
            [r drawIndexedPatches:p->tessOutCP patchIndexBuffer:nil patchIndexBufferOffset:0 controlPointIndexBuffer:cpIndices
                controlPointIndexBufferOffset:cb->indexOffset indirectBuffer:args indirectBufferOffset:argsOff + 14 * 4];
        else
            [r drawIndexedPatches:p->tessOutCP patchStart:0 patchCount:patches patchIndexBuffer:nil patchIndexBufferOffset:0
                controlPointIndexBuffer:cpIndices controlPointIndexBufferOffset:cb->indexOffset + t.first * idxSize
                instanceCount:1 baseInstance:0];
        kmtlProfNote(cb, p, false, 1);
        cb->draws++;
        return;
    }
    // A passthrough control stage left the control points where the vertex kernel wrote them
    [r setVertexBuffer:p->tessPassthrough ? vtxOut : tcOut offset:p->tessPassthrough ? vtxOff : tcOff atIndex:kTessInputBufferIndex];
    if (patchOut) [r setVertexBuffer:patchOut offset:patchOff atIndex:kTessPatchBufferIndex];
    [r setVertexBuffer:levels offset:levelsOff atIndex:kTessFactorBufferIndex];
    [r setTessellationFactorBuffer:levels offset:levelsOff instanceStride:0];
    if (t.indirect)
        [r drawPatches:p->tessOutCP patchIndexBuffer:nil patchIndexBufferOffset:0 indirectBuffer:args indirectBufferOffset:argsOff + 14 * 4];
    else
        [r drawPatches:p->tessOutCP patchStart:0 patchCount:patches patchIndexBuffer:nil patchIndexBufferOffset:0 instanceCount:1 baseInstance:0];
    kmtlProfNote(cb, p, false, 1);
    cb->draws++;
}

// ---------------------------------------------------------------------------------------------------------------
// Draws and dispatches

// Metal has no triangle fans: a shared index buffer holds (0, i, i+1) and the fan's first vertex is the base vertex
static id<MTLBuffer> fanIndexBuffer(KDevice* dev, uint32_t vertexCount) {
    std::lock_guard<std::mutex> lock(dev->fanMutex);
    if (vertexCount > dev->fanVertexCapacity) {
        uint32_t cap = std::max(vertexCount, 1024u);
        id<MTLBuffer> b = [dev->mtl newBufferWithLength:(cap - 2) * 3 * 4 options:MTLResourceStorageModeShared];
        auto* idx = (uint32_t*)b.contents;
        for (uint32_t i = 1; i + 1 < cap; i++) {
            idx[(i - 1) * 3 + 0] = 0;
            idx[(i - 1) * 3 + 1] = i;
            idx[(i - 1) * 3 + 2] = i + 1;
        }
        dev->fanIndices = b;
        dev->fanVertexCapacity = cap;
    }
    return dev->fanIndices;
}

VKFN(void) vkCmdDraw(VkCommandBuffer commandBuffer, uint32_t vertexCount, uint32_t instanceCount, uint32_t firstVertex,
                     uint32_t firstInstance) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->graphicsPipeline && cb->graphicsPipeline->tess) {
        if (!vertexCount || !instanceCount) return;
        KTessDraw t;
        t.count = vertexCount; t.instances = instanceCount; t.first = firstVertex; t.firstInstance = firstInstance;
        tessDraw(cb, t);
        return;
    }
    if (!flushGraphics(cb) || !vertexCount || !instanceCount) return;
    setDrawId(cb, 0);
    if (cb->graphicsPipeline->triangleFan) {
        if (vertexCount < 3) return;
        id<MTLBuffer> fan = fanIndexBuffer(cb->device, vertexCount);
        forEachView(cb, [&] {
            [cb->render drawIndexedPrimitives:MTLPrimitiveTypeTriangle indexCount:(vertexCount - 2) * 3 indexType:MTLIndexTypeUInt32
                                  indexBuffer:fan indexBufferOffset:0
                                instanceCount:instanceCount baseVertex:firstVertex baseInstance:firstInstance];
        });
        kmtlProfNote(cb, cb->graphicsPipeline, false, 1);
        cb->draws++;
        return;
    }
    forEachView(cb, [&] {
        [cb->render drawPrimitives:cb->graphicsPipeline->primitive vertexStart:firstVertex vertexCount:vertexCount
                     instanceCount:instanceCount baseInstance:firstInstance];
    });
    kmtlProfNote(cb, cb->graphicsPipeline, false, 1);
    cb->draws++;
}

static NSUInteger indexSize(KCommandBuffer* cb) { return cb->indexType == MTLIndexTypeUInt32 ? 4 : 2; }

VKFN(void) vkCmdDrawIndexed(VkCommandBuffer commandBuffer, uint32_t indexCount, uint32_t instanceCount, uint32_t firstIndex,
                            int32_t vertexOffset, uint32_t firstInstance) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->graphicsPipeline && cb->graphicsPipeline->tess) {
        if (!indexCount || !instanceCount || !cb->indexBuffer) return;
        KTessDraw t;
        t.indexed = true; t.count = indexCount; t.instances = instanceCount; t.first = firstIndex;
        t.vertexOffset = vertexOffset; t.firstInstance = firstInstance;
        tessDraw(cb, t);
        return;
    }
    if (!flushGraphics(cb) || !indexCount || !instanceCount || !cb->indexBuffer) return;
    if (cb->graphicsPipeline->triangleFan) {
        static std::atomic<bool> warned{false};
        if (!warned.exchange(true)) KERR("indexed triangle fans are not emulated (drawn as lists)");
    }
    setDrawId(cb, 0);
    forEachView(cb, [&] {
        [cb->render drawIndexedPrimitives:cb->graphicsPipeline->primitive indexCount:indexCount indexType:cb->indexType
                              indexBuffer:cb->indexBuffer indexBufferOffset:cb->indexOffset + firstIndex * indexSize(cb)
                            instanceCount:instanceCount baseVertex:vertexOffset baseInstance:firstInstance];
    });
    kmtlProfNote(cb, cb->graphicsPipeline, false, 1);
    cb->draws++;
}

VKFN(void) vkCmdDrawIndirect(VkCommandBuffer commandBuffer, VkBuffer buffer, VkDeviceSize offset, uint32_t drawCount, uint32_t stride) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!flushGraphics(cb)) return;
    auto* buf = (KBuffer*)buffer;
    if (stride == 0) stride = sizeof(VkDrawIndirectCommand);
    for (uint32_t i = 0; i < drawCount; i++) {
        setDrawId(cb, i);
        forEachView(cb, [&] {
            [cb->render drawPrimitives:cb->graphicsPipeline->primitive indirectBuffer:buf->mtl
                  indirectBufferOffset:buf->offset + offset + (VkDeviceSize)i * stride];
        });
    }
    kmtlProfNote(cb, cb->graphicsPipeline, false, drawCount);
    cb->draws += drawCount;
}

VKFN(void) vkCmdDrawIndexedIndirect(VkCommandBuffer commandBuffer, VkBuffer buffer, VkDeviceSize offset, uint32_t drawCount,
                                    uint32_t stride) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->graphicsPipeline && cb->graphicsPipeline->tess) {
        if (!cb->indexBuffer) return;
        if (stride == 0) stride = sizeof(VkDrawIndexedIndirectCommand);
        for (uint32_t i = 0; i < drawCount; i++) {
            KTessDraw t;
            t.indexed = t.indirect = true;
            t.indirectBuf = (KBuffer*)buffer;
            t.indirectOffset = offset + (VkDeviceSize)i * stride;
            tessDraw(cb, t);
        }
        return;
    }
    if (!flushGraphics(cb) || !cb->indexBuffer) return;
    auto* buf = (KBuffer*)buffer;
    if (stride == 0) stride = sizeof(VkDrawIndexedIndirectCommand);
    for (uint32_t i = 0; i < drawCount; i++) {
        setDrawId(cb, i);
        forEachView(cb, [&] {
            [cb->render drawIndexedPrimitives:cb->graphicsPipeline->primitive indexType:cb->indexType indexBuffer:cb->indexBuffer
                            indexBufferOffset:cb->indexOffset indirectBuffer:buf->mtl
                         indirectBufferOffset:buf->offset + offset + (VkDeviceSize)i * stride];
        });
    }
    kmtlProfNote(cb, cb->graphicsPipeline, false, drawCount);
    cb->draws += drawCount;
}

VKFN(void) vkCmdDispatch(VkCommandBuffer commandBuffer, uint32_t x, uint32_t y, uint32_t z) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!x || !y || !z || !flushCompute(cb)) return;
    [cb->compute dispatchThreadgroups:MTLSizeMake(x, y, z) threadsPerThreadgroup:cb->computePipeline->workgroup];
    kmtlProfNote(cb, cb->computePipeline, true, 1);
    cb->dispatches++;
    if (kmtlConfig().gpuprofSplit) cb->endEncoder();
}

VKFN(void) vkCmdDispatchIndirect(VkCommandBuffer commandBuffer, VkBuffer buffer, VkDeviceSize offset) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!flushCompute(cb)) return;
    auto* buf = (KBuffer*)buffer;
    [cb->compute dispatchThreadgroupsWithIndirectBuffer:buf->mtl indirectBufferOffset:buf->offset + offset
                                  threadsPerThreadgroup:cb->computePipeline->workgroup];
    kmtlProfNote(cb, cb->computePipeline, true, 1);
    cb->dispatches++;
    if (kmtlConfig().gpuprofSplit) cb->endEncoder();
}

// Ray tracing pipelines are compute kernels of 8x8 threads; the launch size goes to kRtLaunchBufferIndex and the
// shader binding table is not read (see rtpipeline.mm)
VKFN(void) vkCmdTraceRaysKHR(VkCommandBuffer commandBuffer, const VkStridedDeviceAddressRegionKHR*, const VkStridedDeviceAddressRegionKHR*,
                             const VkStridedDeviceAddressRegionKHR*, const VkStridedDeviceAddressRegionKHR*, uint32_t w, uint32_t h, uint32_t d) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!w || !h || !d || !flushCompute(cb)) return;
    uint32_t size[4] = {w, h, d, 0};
    [cb->compute setBytes:size length:sizeof(size) atIndex:kRtLaunchBufferIndex];
    MTLSize wg = cb->computePipeline->workgroup;
    [cb->compute dispatchThreadgroups:MTLSizeMake((w + wg.width - 1) / wg.width, (h + wg.height - 1) / wg.height, d)
                threadsPerThreadgroup:wg];
    kmtlProfNote(cb, cb->computePipeline, true, 1);
    cb->dispatches++;
}

VKFN(void) vkCmdDispatchBase(VkCommandBuffer commandBuffer, uint32_t bx, uint32_t by, uint32_t bz, uint32_t x, uint32_t y, uint32_t z) {
    if (bx || by || bz) KUNIMPL();
    vkCmdDispatch(commandBuffer, x, y, z);
}

// ---------------------------------------------------------------------------------------------------------------
// Barriers

static void barrier(KCommandBuffer* cb, VkPipelineStageFlags2 srcStages, VkPipelineStageFlags2 dstStages) {
    if (cb->render) {
        // Inside a pass only framebuffer-local dependencies are legal: a memory barrier covers them
        [cb->render memoryBarrierWithScope:MTLBarrierScopeBuffers | MTLBarrierScopeTextures
                               afterStages:MTLRenderStageFragment beforeStages:MTLRenderStageVertex];
        return;
    }
    cb->barrierClasses(stageClasses(srcStages, true), stageClasses(dstStages, false));
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

static MTLLoadAction loadAction(VkAttachmentLoadOp op) {
    switch (op) {
    case VK_ATTACHMENT_LOAD_OP_CLEAR: return MTLLoadActionClear;
    case VK_ATTACHMENT_LOAD_OP_DONT_CARE: return MTLLoadActionDontCare;
    default: return MTLLoadActionLoad;
    }
}

static MTLStoreAction storeAction(VkAttachmentStoreOp op, bool resolve) {
    bool store = op != VK_ATTACHMENT_STORE_OP_DONT_CARE;
    if (resolve) return store ? MTLStoreActionStoreAndMultisampleResolve : MTLStoreActionMultisampleResolve;
    return store ? MTLStoreActionStore : MTLStoreActionDontCare;
}

static MTLClearColor clearColor(VkFormat f, const VkClearColorValue& c) {
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
        ca.loadAction = loadAction(a.loadOp);
        if (c < (int)bi->clearValueCount) ca.clearColor = clearColor(a.format, bi->pClearValues[c].color);
        int r = sp.resolves[i];
        if (r >= 0 && r < (int)viewCount) {
            ca.resolveTexture = views[r]->mtl;
            ca.resolveLevel = 0;
        }
        ca.storeAction = storeAction(a.storeOp, r >= 0);
    }
    if (sp.depth >= 0 && sp.depth < (int)viewCount) {
        const auto& a = rp->attachments[sp.depth];
        const KFormatInfo* fi = kmtlFormat(a.format);
        KImageView* v = views[sp.depth];
        if (fi && fi->depth) {
            setAttachmentTexture(d.depthAttachment, v);
            d.depthAttachment.loadAction = loadAction(a.loadOp);
            d.depthAttachment.storeAction = storeAction(a.storeOp, false);
            if (sp.depth < (int)bi->clearValueCount) d.depthAttachment.clearDepth = bi->pClearValues[sp.depth].depthStencil.depth;
        }
        if (fi && fi->stencil) {
            setAttachmentTexture(d.stencilAttachment, v);
            d.stencilAttachment.loadAction = loadAction(a.stencilLoadOp);
            d.stencilAttachment.storeAction = storeAction(a.stencilStoreOp, false);
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
    ((KCommandBuffer*)commandBuffer)->endRender();
}

static void setRenderingAttachment(MTLRenderPassAttachmentDescriptor* a, const VkRenderingAttachmentInfo& ri, bool resuming) {
    auto* v = (KImageView*)ri.imageView;
    setAttachmentTexture(a, v);
    a.loadAction = resuming ? MTLLoadActionLoad : loadAction(ri.loadOp);
    bool resolve = ri.resolveMode != VK_RESOLVE_MODE_NONE && ri.resolveImageView;
    if (resolve) {
        a.resolveTexture = ((KImageView*)ri.resolveImageView)->mtl;
        a.resolveLevel = 0;
    }
    a.storeAction = storeAction(ri.storeOp, resolve);
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
        ca.clearColor = clearColor(v->format, a.clearValue.color);
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
// Transfers

// R: VkBufferCopy or VkBufferCopy2 (the same three fields)
template <typename R> static void copyBuffer(KCommandBuffer* cb, VkBuffer src, VkBuffer dst, uint32_t n, const R* regions) {
    auto* s = (KBuffer*)src;
    auto* d = (KBuffer*)dst;
    id<MTLBlitCommandEncoder> b = cb->blitEncoder();
    for (uint32_t i = 0; i < n; i++) {
        if (!regions[i].size) continue;
        [b copyFromBuffer:s->mtl sourceOffset:s->offset + regions[i].srcOffset toBuffer:d->mtl
        destinationOffset:d->offset + regions[i].dstOffset size:regions[i].size];
    }
}

VKFN(void) vkCmdCopyBuffer(VkCommandBuffer commandBuffer, VkBuffer src, VkBuffer dst, uint32_t n, const VkBufferCopy* regions) {
    KTRACE_FIRST();
    copyBuffer((KCommandBuffer*)commandBuffer, src, dst, n, regions);
}

VKFN(void) vkCmdCopyBuffer2(VkCommandBuffer commandBuffer, const VkCopyBufferInfo2* info) {
    KTRACE_FIRST();
    copyBuffer((KCommandBuffer*)commandBuffer, info->srcBuffer, info->dstBuffer, info->regionCount, info->pRegions);
}

static MTLBlitOption aspectOption(KImage* img, VkImageAspectFlags aspect) {
    const KFormatInfo* fi = kmtlFormat(img->format);
    if (fi && fi->depth && fi->stencil) {
        if (aspect == VK_IMAGE_ASPECT_DEPTH_BIT) return MTLBlitOptionDepthFromDepthStencil;
        if (aspect == VK_IMAGE_ASPECT_STENCIL_BIT) return MTLBlitOptionStencilFromDepthStencil;
    }
    return MTLBlitOptionNone;
}

struct KBufferImageLayout {
    NSUInteger bytesPerRow, bytesPerImage;
};

static KBufferImageLayout bufferImageLayout(KImage* img, const VkBufferImageCopy& r) {
    const KFormatInfo* fi = kmtlFormat(img->format);
    uint32_t rowTexels = r.bufferRowLength ? r.bufferRowLength : r.imageExtent.width;
    uint32_t rows = r.bufferImageHeight ? r.bufferImageHeight : r.imageExtent.height;
    uint32_t bpb = fi->blockBytes;
    if (fi->depth && fi->stencil) {
        // Depth or stencil aspect copies use the aspect's own texel size
        bpb = r.imageSubresource.aspectMask == VK_IMAGE_ASPECT_STENCIL_BIT ? 1 : 4;
    }
    NSUInteger bpr = (NSUInteger)((rowTexels + fi->blockW - 1) / fi->blockW) * bpb;
    NSUInteger bpi = bpr * ((rows + fi->blockH - 1) / fi->blockH);
    return {bpr, bpi};
}

static uint32_t imageSlice(KImage* img, uint32_t layer) { return img->info.imageType == VK_IMAGE_TYPE_3D ? 0 : layer; }

VKFN(void) vkCmdCopyBufferToImage(VkCommandBuffer commandBuffer, VkBuffer src, VkImage dst, VkImageLayout, uint32_t n,
                                  const VkBufferImageCopy* regions) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* s = (KBuffer*)src;
    auto* img = (KImage*)dst;
    id<MTLBlitCommandEncoder> b = cb->blitEncoder();
    for (uint32_t i = 0; i < n; i++) {
        const auto& r = regions[i];
        KBufferImageLayout l = bufferImageLayout(img, r);
        uint32_t layers = r.imageSubresource.layerCount == VK_REMAINING_ARRAY_LAYERS ? img->info.arrayLayers - r.imageSubresource.baseArrayLayer
                                                                                     : r.imageSubresource.layerCount;
        bool is3D = img->info.imageType == VK_IMAGE_TYPE_3D;
        for (uint32_t layer = 0; layer < layers; layer++) {
            [b copyFromBuffer:s->mtl sourceOffset:s->offset + r.bufferOffset + layer * l.bytesPerImage * (is3D ? r.imageExtent.depth : 1)
               sourceBytesPerRow:l.bytesPerRow sourceBytesPerImage:(is3D || layers > 1) ? l.bytesPerImage : 0
                      sourceSize:MTLSizeMake(r.imageExtent.width, r.imageExtent.height, r.imageExtent.depth)
                       toTexture:img->mtl destinationSlice:imageSlice(img, r.imageSubresource.baseArrayLayer + layer)
                destinationLevel:r.imageSubresource.mipLevel
               destinationOrigin:MTLOriginMake(r.imageOffset.x, r.imageOffset.y, r.imageOffset.z)
                         options:aspectOption(img, r.imageSubresource.aspectMask)];
        }
    }
}

VKFN(void) vkCmdCopyImageToBuffer(VkCommandBuffer commandBuffer, VkImage src, VkImageLayout, VkBuffer dst, uint32_t n,
                                  const VkBufferImageCopy* regions) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* img = (KImage*)src;
    auto* d = (KBuffer*)dst;
    id<MTLBlitCommandEncoder> b = cb->blitEncoder();
    for (uint32_t i = 0; i < n; i++) {
        const auto& r = regions[i];
        KBufferImageLayout l = bufferImageLayout(img, r);
        uint32_t layers = r.imageSubresource.layerCount == VK_REMAINING_ARRAY_LAYERS ? img->info.arrayLayers - r.imageSubresource.baseArrayLayer
                                                                                     : r.imageSubresource.layerCount;
        bool is3D = img->info.imageType == VK_IMAGE_TYPE_3D;
        for (uint32_t layer = 0; layer < layers; layer++) {
            [b copyFromTexture:img->mtl sourceSlice:imageSlice(img, r.imageSubresource.baseArrayLayer + layer)
                   sourceLevel:r.imageSubresource.mipLevel
                  sourceOrigin:MTLOriginMake(r.imageOffset.x, r.imageOffset.y, r.imageOffset.z)
                    sourceSize:MTLSizeMake(r.imageExtent.width, r.imageExtent.height, r.imageExtent.depth)
                      toBuffer:d->mtl
             destinationOffset:d->offset + r.bufferOffset + layer * l.bytesPerImage * (is3D ? r.imageExtent.depth : 1)
        destinationBytesPerRow:l.bytesPerRow destinationBytesPerImage:(is3D || layers > 1) ? l.bytesPerImage : 0
                       options:aspectOption(img, r.imageSubresource.aspectMask)];
        }
    }
}

VKFN(void) vkCmdCopyImage(VkCommandBuffer commandBuffer, VkImage src, VkImageLayout, VkImage dst, VkImageLayout, uint32_t n,
                          const VkImageCopy* regions) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* s = (KImage*)src;
    auto* d = (KImage*)dst;
    id<MTLTexture> srcTex = s->mtl;
    if (s->pixelFormat != d->pixelFormat) {
        // Size-compatible formats: copy through a view of the source in the destination format
        srcTex = [s->mtl newTextureViewWithPixelFormat:d->pixelFormat];
        if (!srcTex) {
            KERR("vkCmdCopyImage between formats %d and %d unsupported", s->format, d->format);
            return;
        }
    }
    id<MTLBlitCommandEncoder> b = cb->blitEncoder();
    for (uint32_t i = 0; i < n; i++) {
        const auto& r = regions[i];
        uint32_t layers = r.srcSubresource.layerCount;
        bool src3D = s->info.imageType == VK_IMAGE_TYPE_3D, dst3D = d->info.imageType == VK_IMAGE_TYPE_3D;
        uint32_t depth = (src3D && dst3D) ? r.extent.depth : 1;
        uint32_t iters = (src3D && dst3D) ? 1 : std::max(layers, r.extent.depth);
        for (uint32_t l = 0; l < iters; l++) {
            [b copyFromTexture:srcTex sourceSlice:src3D ? 0 : r.srcSubresource.baseArrayLayer + l sourceLevel:r.srcSubresource.mipLevel
                  sourceOrigin:MTLOriginMake(r.srcOffset.x, r.srcOffset.y, src3D ? r.srcOffset.z + (dst3D ? 0 : l) : 0)
                    sourceSize:MTLSizeMake(r.extent.width, r.extent.height, depth)
                     toTexture:d->mtl destinationSlice:dst3D ? 0 : r.dstSubresource.baseArrayLayer + l
              destinationLevel:r.dstSubresource.mipLevel
             destinationOrigin:MTLOriginMake(r.dstOffset.x, r.dstOffset.y, dst3D ? r.dstOffset.z + (src3D ? 0 : l) : 0)];
        }
    }
}

VKFN(void) vkCmdBlitImage(VkCommandBuffer commandBuffer, VkImage src, VkImageLayout, VkImage dst, VkImageLayout, uint32_t n,
                          const VkImageBlit* regions, VkFilter filter) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* s = (KImage*)src;
    auto* d = (KImage*)dst;
    for (uint32_t i = 0; i < n; i++) {
        const auto& r = regions[i];
        KLOG(2, "blit %p(type %d fmt %d) L%u [%d,%d,%d]-[%d,%d,%d] -> %p(type %d fmt %d) L%u [%d,%d,%d]-[%d,%d,%d] layers %u",
             s, s->info.imageType, s->format, r.srcSubresource.mipLevel, r.srcOffsets[0].x, r.srcOffsets[0].y, r.srcOffsets[0].z,
             r.srcOffsets[1].x, r.srcOffsets[1].y, r.srcOffsets[1].z, d, d->info.imageType, d->format, r.dstSubresource.mipLevel,
             r.dstOffsets[0].x, r.dstOffsets[0].y, r.dstOffsets[0].z, r.dstOffsets[1].x, r.dstOffsets[1].y, r.dstOffsets[1].z,
             r.dstSubresource.layerCount);
        int sw = r.srcOffsets[1].x - r.srcOffsets[0].x, sh = r.srcOffsets[1].y - r.srcOffsets[0].y;
        int dw = r.dstOffsets[1].x - r.dstOffsets[0].x, dh = r.dstOffsets[1].y - r.dstOffsets[0].y;
        bool sameSize = sw == dw && sh == dh && sw > 0 && sh > 0 && s->pixelFormat == d->pixelFormat &&
            r.srcOffsets[1].z - r.srcOffsets[0].z == r.dstOffsets[1].z - r.dstOffsets[0].z;
        if (sameSize) {
            VkImageCopy c{};
            c.srcSubresource = r.srcSubresource;
            c.dstSubresource = r.dstSubresource;
            c.srcOffset = r.srcOffsets[0];
            c.dstOffset = r.dstOffsets[0];
            c.extent = {(uint32_t)sw, (uint32_t)sh, (uint32_t)std::max(1, r.srcOffsets[1].z - r.srcOffsets[0].z)};
            vkCmdCopyImage(commandBuffer, src, VK_IMAGE_LAYOUT_GENERAL, dst, VK_IMAGE_LAYOUT_GENERAL, 1, &c);
            continue;
        }
        VkImageBlit2 b2{VK_STRUCTURE_TYPE_IMAGE_BLIT_2};
        b2.srcSubresource = r.srcSubresource;
        b2.dstSubresource = r.dstSubresource;
        memcpy(b2.srcOffsets, r.srcOffsets, sizeof(b2.srcOffsets));
        memcpy(b2.dstOffsets, r.dstOffsets, sizeof(b2.dstOffsets));
        kmtlInternalBlit(cb, s, d, b2, filter);
    }
}

VKFN(void) vkCmdFillBuffer(VkCommandBuffer commandBuffer, VkBuffer dst, VkDeviceSize offset, VkDeviceSize size, uint32_t data) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* d = (KBuffer*)dst;
    if (size == VK_WHOLE_SIZE) size = (d->size - offset) & ~3ull;
    if (!size) return;
    uint8_t b0 = data & 0xFF;
    if (data == (uint32_t)b0 * 0x01010101u) {
        [cb->blitEncoder() fillBuffer:d->mtl range:NSMakeRange(d->offset + offset, size) value:b0];
    } else {
        kmtlInternalFillBuffer(cb, d->mtl, d->offset + offset, size, data);
    }
}

VKFN(void) vkCmdUpdateBuffer(VkCommandBuffer commandBuffer, VkBuffer dst, VkDeviceSize offset, VkDeviceSize size, const void* data) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* d = (KBuffer*)dst;
    id<MTLBuffer> buf;
    NSUInteger off;
    void* p = cb->ring.alloc(cb->device, size, &buf, &off);
    memcpy(p, data, size);
    [cb->blitEncoder() copyFromBuffer:buf sourceOffset:off toBuffer:d->mtl destinationOffset:d->offset + offset size:size];
}

// Image clears are load-clear render passes, one per level and layer
static void clearImage(KCommandBuffer* cb, KImage* img, const VkClearColorValue* color, const VkClearDepthStencilValue* ds,
                       const VkImageSubresourceRange& r) {
    const KFormatInfo* fi = kmtlFormat(img->format);
    if (!fi || !fi->renderable || !(img->mtl.usage & MTLTextureUsageRenderTarget)) {
        static std::atomic<int> warned{0};
        if (warned++ < 5) KERR("clear of non-renderable image (format %d) skipped", img->format);
        return;
    }
    uint32_t levels = r.levelCount == VK_REMAINING_MIP_LEVELS ? img->info.mipLevels - r.baseMipLevel : r.levelCount;
    uint32_t layers = r.layerCount == VK_REMAINING_ARRAY_LAYERS ? img->info.arrayLayers - r.baseArrayLayer : r.layerCount;
    bool is3D = img->info.imageType == VK_IMAGE_TYPE_3D;
    for (uint32_t lv = r.baseMipLevel; lv < r.baseMipLevel + levels; lv++) {
        uint32_t w = std::max(1u, img->info.extent.width >> lv), h = std::max(1u, img->info.extent.height >> lv);
        uint32_t slices = is3D ? std::max(1u, img->info.extent.depth >> lv) : layers;
        for (uint32_t sl = 0; sl < slices; sl++) {
            MTLRenderPassDescriptor* d = [MTLRenderPassDescriptor renderPassDescriptor];
            auto setup = ^(MTLRenderPassAttachmentDescriptor* a) {
                a.texture = img->mtl;
                a.level = lv;
                if (is3D) a.depthPlane = sl; else a.slice = r.baseArrayLayer + sl;
                a.loadAction = MTLLoadActionClear;
                a.storeAction = MTLStoreActionStore;
            };
            if (color) {
                setup(d.colorAttachments[0]);
                d.colorAttachments[0].clearColor = clearColor(img->format, *color);
            } else {
                if (fi->depth && (r.aspectMask & VK_IMAGE_ASPECT_DEPTH_BIT)) {
                    setup(d.depthAttachment);
                    d.depthAttachment.clearDepth = ds->depth;
                }
                if (fi->stencil && (r.aspectMask & VK_IMAGE_ASPECT_STENCIL_BIT)) {
                    setup(d.stencilAttachment);
                    d.stencilAttachment.clearStencil = ds->stencil;
                }
            }
            cb->beginRender(d, w, h, false, KCommandBuffer::kClassTransfer);
            cb->passes--;  // not a real pass in the frame log
            cb->endEncoder();
        }
    }
}

VKFN(void) vkCmdClearColorImage(VkCommandBuffer commandBuffer, VkImage image, VkImageLayout, const VkClearColorValue* color,
                                uint32_t n, const VkImageSubresourceRange* ranges) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    for (uint32_t i = 0; i < n; i++) clearImage(cb, (KImage*)image, color, nullptr, ranges[i]);
}

VKFN(void) vkCmdClearDepthStencilImage(VkCommandBuffer commandBuffer, VkImage image, VkImageLayout, const VkClearDepthStencilValue* ds,
                                       uint32_t n, const VkImageSubresourceRange* ranges) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    for (uint32_t i = 0; i < n; i++) clearImage(cb, (KImage*)image, nullptr, ds, ranges[i]);
}

VKFN(void) vkCmdClearAttachments(VkCommandBuffer, uint32_t, const VkClearAttachment*, uint32_t, const VkClearRect*) { KUNIMPL(); }

VKFN(void) vkCmdResolveImage(VkCommandBuffer, VkImage, VkImageLayout, VkImage, VkImageLayout, uint32_t, const VkImageResolve*) { KUNIMPL(); }

// ---------------------------------------------------------------------------------------------------------------
// Queries and debug labels (KSA never records them in the census run)

// Timestamps. Apple GPUs sample counters only at encoder stage boundaries, so a timestamp is the end of a tiny blit
// encoder ordered after everything before it. Used only while the game's profiler is open.

static std::mutex g_tsPoolMutex;
static std::vector<id<MTLCounterSampleBuffer>> g_tsPool;  // Metal caps live sample buffers: recycle them
static const uint32_t kTimestampSamples = 4096;

void KCommandBuffer::writeTimestamp(KQueryPool* pool, uint32_t query) {
    if (render) {
        tsDeferred.push_back({pool, query});
        return;
    }
    if (!tsSamples) {
        {
            std::lock_guard<std::mutex> lock(g_tsPoolMutex);
            if (!g_tsPool.empty()) { tsSamples = g_tsPool.back(); g_tsPool.pop_back(); }
        }
        if (!tsSamples) {
            id<MTLCounterSet> set = nil;
            for (id<MTLCounterSet> cs in device->mtl.counterSets)
                if ([cs.name isEqualToString:MTLCommonCounterSetTimestamp]) set = cs;
            MTLCounterSampleBufferDescriptor* d = [MTLCounterSampleBufferDescriptor new];
            d.counterSet = set;
            d.storageMode = MTLStorageModeShared;
            d.sampleCount = kTimestampSamples;
            NSError* e = nil;
            tsSamples = set ? [device->mtl newCounterSampleBufferWithDescriptor:d error:&e] : nil;
            if (!tsSamples) {
                static std::atomic<int> warned{0};
                if (warned++ < 3) KERR("timestamp sample buffer: %s", e ? e.description.UTF8String : "no timestamp counters");
                tsWrites.push_back({pool, query, UINT32_MAX});
                return;
            }
        }
        tsUsed = 0;
    }
    if (tsUsed + 2 > kTimestampSamples) {
        tsWrites.push_back({pool, query, UINT32_MAX});
        return;
    }
    endEncoder();
    if (!device->timestampScratch) {
        std::lock_guard<std::mutex> lock(device->tessScratchMutex);
        if (!device->timestampScratch) device->timestampScratch = [device->mtl newBufferWithLength:256 options:MTLResourceStorageModePrivate];
    }
    MTLBlitPassDescriptor* d = [MTLBlitPassDescriptor blitPassDescriptor];
    d.sampleBufferAttachments[0].sampleBuffer = tsSamples;
    d.sampleBufferAttachments[0].startOfEncoderSampleIndex = tsUsed;
    d.sampleBufferAttachments[0].endOfEncoderSampleIndex = tsUsed + 1;
    id<MTLBlitCommandEncoder> b = [mtl blitCommandEncoderWithDescriptor:d];
    waitForRoles(b, kClassAll);  // after everything recorded so far
    [b fillBuffer:device->timestampScratch range:NSMakeRange(0, 4) value:0];
    [b updateFence:finishFence(kClassAll)];
    [b endEncoding];
    encRoles = 0;
    tsWrites.push_back({pool, query, tsUsed + 1});
    tsUsed += 2;
}

// At submit: resolve the samples into the pools when the command buffer completes
void KCommandBuffer::submitTimestamps(id<MTLCommandBuffer> c) {
    if (tsWrites.empty()) { releaseTimestamps(); return; }
    id<MTLCounterSampleBuffer> samples = tsSamples;
    uint32_t used = tsUsed;
    auto writes = std::make_shared<std::vector<TimestampWrite>>(std::move(tsWrites));
    tsSamples = nil;
    tsWrites.clear();
    [c addCompletedHandler:^(id<MTLCommandBuffer>) {
        NSData* data = samples && used ? [samples resolveCounterRange:NSMakeRange(0, used)] : nil;
        const uint64_t* ts = data ? (const uint64_t*)data.bytes : nullptr;
        for (auto& w : *writes) {
            uint64_t v = ts && w.sample < used ? ts[w.sample] : 0;
            if (v == MTLCounterErrorValue) v = 0;
            std::lock_guard<std::mutex> lock(w.pool->m);
            if (w.query < w.pool->count) {
                w.pool->values[w.query] = v;
                w.pool->available[w.query] = 1;
            }
            w.pool->cv.notify_all();
        }
        if (samples) {
            std::lock_guard<std::mutex> lock(g_tsPoolMutex);
            g_tsPool.push_back(samples);
        }
    }];
}

void KCommandBuffer::releaseTimestamps() {
    if (tsSamples) {
        std::lock_guard<std::mutex> lock(g_tsPoolMutex);
        g_tsPool.push_back(tsSamples);
    }
    tsSamples = nil;
    tsWrites.clear();
    tsDeferred.clear();
}

VKFN(void) vkCmdResetQueryPool(VkCommandBuffer, VkQueryPool pool, uint32_t first, uint32_t n) {
    KTRACE_FIRST();
    ((KQueryPool*)pool)->reset(first, n);
}

VKFN(void) vkCmdWriteTimestamp(VkCommandBuffer commandBuffer, VkPipelineStageFlagBits, VkQueryPool pool, uint32_t query) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->writeTimestamp((KQueryPool*)pool, query);
}

VKFN(void) vkCmdWriteTimestamp2(VkCommandBuffer commandBuffer, VkPipelineStageFlags2, VkQueryPool pool, uint32_t query) {
    KTRACE_FIRST();
    ((KCommandBuffer*)commandBuffer)->writeTimestamp((KQueryPool*)pool, query);
}
VKFN(void) vkCmdBeginDebugUtilsLabelEXT(VkCommandBuffer, const VkDebugUtilsLabelEXT*) {}
VKFN(void) vkCmdEndDebugUtilsLabelEXT(VkCommandBuffer) {}
VKFN(void) vkCmdInsertDebugUtilsLabelEXT(VkCommandBuffer, const VkDebugUtilsLabelEXT*) {}

// Fresh images start from recycled heap memory (VMA reuses blocks), and KSA's cloud shadow volumes accumulate with
// imageLoad before they are ever cleared, so they would start from old data. New color images that can be rendered
// to are cleared to zero once, on a command buffer committed at bind time.
void kmtlZeroImage(KImage* img) {
    KDevice* dev = img->device;
    KCommandBuffer cb;
    cb.device = dev;
    KQueue* q = dev->queue;
    cb.queue = q;
    std::lock_guard<std::mutex> lock(q->submitMutex);
    cb.mtl = [q->mtl commandBuffer];
    cb.mtl.label = @"zero image";
    cb.beginCommands();
    VkClearColorValue zero{};
    VkImageSubresourceRange r{VK_IMAGE_ASPECT_COLOR_BIT, 0, VK_REMAINING_MIP_LEVELS, 0, VK_REMAINING_ARRAY_LAYERS};
    clearImage(&cb, img, &zero, nullptr, r);
    cb.endEncoder();
    cb.joinCommands();
    dev->commitResidency();
    [cb.mtl commit];
}
