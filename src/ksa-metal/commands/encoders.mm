// Command buffers' encoders: their ordering, the upload ring, the encoders themselves, and the command pools and
// buffers.
//
// Commands are encoded straight into an MTLCommandBuffer while recording (KSA re-records its command buffers every
// frame). Encoders are ordered by fences that follow the Vulkan barriers (see "Encoder ordering").
#include "kmtl.h"

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
// encoder and every barrier is of every class (kmtlStageClasses, waitForRoles).

static uint32_t classIndex(uint32_t bit) { return (uint32_t)__builtin_ctz(bit); }

// Vulkan stage masks (synchronization2 bits are a superset of the legacy ones) -> stage classes
uint32_t kmtlStageClasses(VkPipelineStageFlags2 m, bool src) {
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
    waitForAllResolves();
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

KUploadRing::Place KUploadRing::alloc(KDevice* dev, size_t size) {
    size = kmtlAlignUp<size_t>(size, 256);
    while (true) {
        if (chunk < chunks.size()) {
            const Chunk& c = chunks[chunk];
            if (offset + size <= c.length) {
                Place p{c.buffer, c.gpuAddress, offset, c.contents + offset};
                offset += size;
                return p;
            }
            chunk++;
            offset = 0;
            continue;
        }
        // A new chunk: its properties are read here, under a pool of our own, since a bind runs without one
        KAutoreleasePool pool;
        size_t len = std::max(size, kChunkSize);
        id<MTLBuffer> b = [dev->mtl newBufferWithLength:len options:MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked];
        b.label = @"upload ring";
        g_internalBytes += (int64_t)len;
        chunks.push_back({b, (uint8_t*)b.contents, b.gpuAddress, len});
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
    bool dump = !dropping && kmtlPassDumpRecording();
    if (render && deferredStores) {
        for (int i = 0; i < 8; i++) {
            id<MTLTexture> t = passDesc.colorAttachments[i].texture;
            if (!t) continue;
            bool tileOnly = t.storageMode == MTLStorageModeMemoryless;
            if (suspending && tileOnly)
                KERR_ONCE("render pass suspended with a memoryless attachment (a tessellated draw, or KSA_MTL_GPU_PROFILE_SPLIT=1): "
                          "its contents are lost");
            MTLStoreAction suspended = dumpResolves && passDesc.colorAttachments[i].resolveTexture ? MTLStoreActionStoreAndMultisampleResolve : MTLStoreActionStore;
            [render setColorStoreAction:suspending && !tileOnly ? suspended : colorStores[i] atIndex:i];
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
        if (dump) {
            if (dumpName.empty())
                dumpName = render.label ? std::string(render.label.UTF8String)
                    : std::to_string(rtWidth) + "x" + std::to_string(rtHeight) + " " + (encodedPipeline ? encodedPipeline->profName : "no pipeline");
        }
        [render endEncoding];
        render = nil;
        inRenderPass = false;
        if (dump && !suspending) {
            kmtlPassDumpEncoder(this, dumpName, dumpDesc);
            dumpName.clear();
            dumpDesc = nil;
        }
        if (passResolves && !suspending) {
            passResolves = false;
            if (!resolveEvent) resolveEvent = [device->mtl newEvent];
            [mtl encodeSignalEvent:resolveEvent value:++resolveSignaled];
        }
    }
    if (compute) {
        [compute updateFence:finishFence(encRoles)];
        nameEncoder(compute);
        std::string dumpName = !dump ? "" : compute.label ? std::string(compute.label.UTF8String)
            : computePipeline ? computePipeline->profName : "compute";
        [compute endEncoding];
        compute = nil;
        if (dump) kmtlPassDumpEncoder(this, dumpName, nil);
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
    if (dependent) waitForAllResolves();
}

void KCommandBuffer::waitForAllResolves() {
    if (resolveWaited == resolveSignaled) return;
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
        dumpDesc = kmtlPassDumpRecording() ? rp : nil;
        dumpDraws = 0;
        dumpDrawsSkipped = false;
        dumpName.clear();
        if (dumpDesc) {
            for (int i = 0; i < 8; i++) dumpLoads[i] = rp.colorAttachments[i].loadAction;
            dumpDepthLoad = rp.depthAttachment.loadAction;
        }
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
    memset(blendConstants, 0, sizeof(blendConstants));
    stencilRef[0] = stencilRef[1] = 0;
    depthBiasDirty = blendConstantsDirty = stencilRefDirty = false;
    draws = dispatches = passes = 0;
    fxSnapshot = false;
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
        cb->dropping = true;
        cb->endEncoder();
        cb->dropping = false;
        cb->dumpDesc = nil;
        cb->dumpName.clear();
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
    if (kmtlConfig().gpuErrors) {
        MTLCommandBufferDescriptor* d = [MTLCommandBufferDescriptor new];
        d.errorOptions = MTLCommandBufferErrorOptionEncoderExecutionStatus;
        cb->mtl = [cb->queue->mtl commandBufferWithDescriptor:d];
    } else {
        cb->mtl = [cb->queue->mtl commandBuffer];
    }
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
