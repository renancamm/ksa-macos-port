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
    for (auto& f : order.lastFence) f = queue->chain;
    for (auto& m : order.needMask) m = 0;
    order.roles = 0;
    ts.last = false;
    order.waited.clear();
    order.fenceNext = 0;
    resolves.waited = resolves.signaled;
    resolves.inPass = false;
}

id<MTLFence> KCommandBuffer::newFence() {
    if (order.fenceNext == order.fencePool.size()) order.fencePool.push_back([device->mtl newFence]);
    return order.fencePool[order.fenceNext++];
}

id<MTLFence> KCommandBuffer::finishFence(uint32_t roles) {
    id<MTLFence> f = newFence();
    for (uint32_t r = roles; r; r &= r - 1) order.lastFence[classIndex(r & -r)] = f;
    return f;
}

// The fences an encoder with these roles waits on; clears the pending masks it satisfies
static void collectWaits(KCommandBuffer* cb, uint32_t roles, std::vector<id<MTLFence>>& out) {
    for (uint32_t r = roles; r; r &= r - 1) {
        uint32_t i = classIndex(r & -r);
        uint32_t need = cb->order.needMask[i] | (1u << i);
        cb->order.needMask[i] = 0;
        for (uint32_t s = need; s; s &= s - 1) {
            id<MTLFence> f = cb->order.lastFence[classIndex(s & -s)];
            if (f && std::find(out.begin(), out.end(), f) == out.end()) out.push_back(f);
        }
    }
}

template <typename E> void KCommandBuffer::waitForRoles(E enc, uint32_t roles) {
    if (strictSync()) roles = kClassAll;
    order.roles = roles;
    ts.last = false;
    order.waited.clear();
    collectWaits(this, roles, order.waited);
    for (id<MTLFence> f : order.waited) [enc waitForFence:f];
}
template <> void KCommandBuffer::waitForRoles(id<MTLRenderCommandEncoder> enc, uint32_t roles) {
    if (strictSync()) roles = kClassAll;
    order.roles = roles;
    ts.last = false;
    order.waited.clear();
    // Waiting before the vertex stage covers the fragment stage too
    collectWaits(this, roles & ~kClassFragment, order.waited);
    for (id<MTLFence> f : order.waited) [enc waitForFence:f beforeStages:MTLRenderStageVertex];
    if (roles & kClassFragment) {
        std::vector<id<MTLFence>> frag;
        collectWaits(this, kClassFragment, frag);
        for (id<MTLFence> f : frag) {
            if (std::find(order.waited.begin(), order.waited.end(), f) != order.waited.end()) continue;
            [enc waitForFence:f beforeStages:MTLRenderStageFragment];
            order.waited.push_back(f);
        }
    }
}
template void KCommandBuffer::waitForRoles(id<MTLAccelerationStructureCommandEncoder>, uint32_t);

// A Vulkan barrier outside a render pass (inside one, barriers are memory barriers in the render encoder)
void KCommandBuffer::barrierClasses(uint32_t src, uint32_t dst) {
    if (!src || !dst) return;
    for (uint32_t d = dst; d; d &= d - 1) order.needMask[classIndex(d & -d)] |= src;
    if (blit && (dst & kClassTransfer)) { endEncoder(); return; }  // blit commands of one encoder may overlap
    if (!compute) return;
    if (!(dst & order.roles)) return;
    // Later work in the open compute encoder needs the sources: fine if they are this encoder's own earlier work
    // (a memory barrier orders it) or encoders it already waited on
    bool reopen = false;
    for (uint32_t s = src & ~order.roles; s; s &= s - 1) {
        id<MTLFence> f = order.lastFence[classIndex(s & -s)];
        if (f && std::find(order.waited.begin(), order.waited.end(), f) == order.waited.end()) reopen = true;
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
    for (id<MTLFence> f : order.lastFence) {
        if (!f || std::find(seen.begin(), seen.end(), f) != seen.end()) continue;
        seen.push_back(f);
        [b waitForFence:f];
    }
    [b updateFence:queue->chain];
    [b endEncoding];
    for (auto& f : order.lastFence) f = queue->chain;
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

// The encoder's name: the game's GPU zone it began in (zones.mm), its first pipeline (shader hashes, the pipeline's
// name), how many others it ran, and a render pass's size
void KCommandBuffer::notePipelineLabel(KPipeline* p) {
    if (!p || p->profId == labels.lastPipeline) return;
    if (labels.encoder.empty()) {
        labels.encoder = p->profName;
        if (!labels.zones.empty()) labels.zone = labels.zones.back();
    }
    labels.pipelines++;
    labels.lastPipeline = p->profId;
}

template <typename E> void KCommandBuffer::nameEncoder(E enc) {
    if (!labels.encoder.empty()) {
        std::string name = labels.encoder;
        if (labels.pipelines > 1) name += " +" + std::to_string(labels.pipelines - 1);
        if (!labels.zone.empty()) name = labels.zone + ": " + name;
        if (render) name = std::to_string(pass.width) + "x" + std::to_string(pass.height) + " " + name;
        enc.label = [NSString stringWithUTF8String:name.c_str()];
    }
    labels.encoder.clear();
    labels.zone.clear();
    labels.pipelines = 0;
    labels.lastPipeline = 0;
}

// A suspended pass keeps everything for its resumed half. An attachment with a resolve texture must end in a resolve
// (Metal's validation: "Store action ... must be one of MTLStoreActionMultisampleResolve or
// MTLStoreActionStoreAndMultisampleResolve"), so it stores and resolves: the resumed half's end resolves it again
static MTLStoreAction suspendedStore(MTLRenderPassAttachmentDescriptor* a) {
    return a.resolveTexture ? MTLStoreActionStoreAndMultisampleResolve : MTLStoreActionStore;
}

void KCommandBuffer::endEncoder() {
    bool dumping = !dump.dropping && kmtlPassDumpRecording();
    if (render && pass.deferredStores) {
        for (int i = 0; i < 8; i++) {
            id<MTLTexture> t = pass.desc.colorAttachments[i].texture;
            if (!t) continue;
            bool tileOnly = t.storageMode == MTLStorageModeMemoryless;
            if (pass.suspending && tileOnly)
                KERR_ONCE("render pass suspended with a memoryless attachment (a tessellated draw): "
                          "its contents are lost");
            [render setColorStoreAction:pass.suspending && !tileOnly ? suspendedStore(pass.desc.colorAttachments[i]) : pass.colorStores[i] atIndex:i];
        }
        if (pass.desc.depthAttachment.texture) [render setDepthStoreAction:pass.suspending ? suspendedStore(pass.desc.depthAttachment) : pass.depthStore];
        if (pass.desc.stencilAttachment.texture) [render setStencilStoreAction:pass.suspending ? suspendedStore(pass.desc.stencilAttachment) : pass.stencilStore];
        if (!pass.suspending) pass.desc = nil;
    }
    if (render) {
        if (order.roles & kClassVertex) [render updateFence:finishFence(kClassVertex) afterStages:MTLRenderStageVertex];
        uint32_t late = order.roles & ~kClassVertex;
        if (late) [render updateFence:finishFence(late) afterStages:kAllRenderStages];
        nameEncoder(render);
        if (dumping) {
            if (dump.name.empty())
                dump.name = render.label ? std::string(render.label.UTF8String)
                    : std::to_string(pass.width) + "x" + std::to_string(pass.height) + " " + (bound.encodedPipeline ? bound.encodedPipeline->profName : "no pipeline");
        }
        [render endEncoding];
        render = nil;
        pass.active = false;
        if (dumping && !pass.suspending) {
            kmtlPassDumpEncoder(this, dump.name, dump.desc);
            dump.name.clear();
            dump.desc = nil;
        }
        if (resolves.inPass && !pass.suspending) {
            resolves.inPass = false;
            if (!resolves.event) resolves.event = [device->mtl newEvent];
            [mtl encodeSignalEvent:resolves.event value:++resolves.signaled];
        }
    }
    if (compute) {
        [compute updateFence:finishFence(order.roles)];
        nameEncoder(compute);
        std::string name = !dumping ? "" : compute.label ? std::string(compute.label.UTF8String)
            : bound.computePipeline ? bound.computePipeline->profName : "compute";
        [compute endEncoding];
        compute = nil;
        if (dumping) kmtlPassDumpEncoder(this, name, nil);
    }
    if (blit) {
        [blit updateFence:finishFence(order.roles)];
        nameEncoder(blit);
        [blit endEncoding];
        blit = nil;
    }
    order.roles = 0;
}

// Before a compute or blit encoder: MSAA resolves of earlier render passes are not covered by their fences (on Apple
// GPUs, work ordered after a pass's fragment fence could read the resolve texture's previous contents: KSA's ocean
// copies the resolved scene with a compute kernel), so an encoder that depends on fragment work waits for the
// resolves through the event. One wait covers every earlier resolve of the command buffer. Render passes do not wait:
// KSA's passes after a resolve load the multisample attachments, and those that sample resolved images come after
// compute work that has waited; waiting there would cost the overlap of every pass with the previous one.
void KCommandBuffer::waitForResolves(uint32_t roles) {
    if (resolves.waited == resolves.signaled) return;
    bool dependent = false;
    for (uint32_t r = roles; r; r &= r - 1)
        if (order.needMask[classIndex(r & -r)] & kClassFragment) dependent = true;
    if (dependent) waitForAllResolves();
}

void KCommandBuffer::waitForAllResolves() {
    if (resolves.waited == resolves.signaled) return;
    [mtl encodeWaitForEvent:resolves.event value:resolves.signaled];
    resolves.waited = resolves.signaled;
}

id<MTLComputeCommandEncoder> KCommandBuffer::computeEncoder(uint32_t roles) {
    if (compute && (order.roles & roles) == roles) return compute;
    endEncoder();
    waitForResolves(roles);
    compute = [mtl computeCommandEncoderWithDispatchType:MTLDispatchTypeSerial];
    waitForRoles(compute, roles);
    bound.computeSetsDirty = ~0u;
    bound.computePushDirty = true;
    return compute;
}

id<MTLBlitCommandEncoder> KCommandBuffer::blitEncoder() {
    if (blit) return blit;
    endEncoder();
    waitForResolves(kClassTransfer);
    blit = [mtl blitCommandEncoder];
    waitForRoles(blit, kClassTransfer);
    return blit;
}

void KCommandBuffer::beginRender(MTLRenderPassDescriptor* rp, uint32_t w, uint32_t h, bool resuming, uint32_t roles) {
    endEncoder();
    if (!resuming) {
        resolves.inPass = rp.depthAttachment.resolveTexture || rp.stencilAttachment.resolveTexture;
        for (int i = 0; i < 8; i++) resolves.inPass |= rp.colorAttachments[i].resolveTexture != nil;
        dump.desc = kmtlPassDumpRecording() ? rp : nil;
        dump.draws = 0;
        dump.drawsSkipped = false;
        dump.name.clear();
        if (dump.desc) {
            for (int i = 0; i < 8; i++) dump.loads[i] = rp.colorAttachments[i].loadAction;
            dump.depthLoad = rp.depthAttachment.loadAction;
        }
    }
    // A tessellated draw, or a game timestamp while zones are split (gpu_zones), suspends the pass and resumes it; a pass
    // begun before the split takes its timestamps at its end (writeTimestamp). A resumed pass keeps what it began with
    if (!resuming)
        pass.deferredStores = kmtlConfig().tessellation || (kmtlConfig().gpuZones && kmtlZoneMode() == kZonesSplit);
    if (pass.deferredStores) {
        if (!resuming) {
            for (int i = 0; i < 8; i++) pass.colorStores[i] = rp.colorAttachments[i].storeAction;
            pass.depthStore = rp.depthAttachment.storeAction;
            pass.stencilStore = rp.stencilAttachment.storeAction;
        }
        for (int i = 0; i < 8; i++)
            if (rp.colorAttachments[i].texture) rp.colorAttachments[i].storeAction = MTLStoreActionUnknown;
        if (rp.depthAttachment.texture) rp.depthAttachment.storeAction = MTLStoreActionUnknown;
        if (rp.stencilAttachment.texture) rp.stencilAttachment.storeAction = MTLStoreActionUnknown;
        pass.desc = rp;
    }
    render = [mtl renderCommandEncoderWithDescriptor:rp];
    if (!render) {
        KERR("render encoder creation failed (%ux%u)", w, h);
        return;
    }
    waitForRoles(render, roles);
    pass.width = w;
    pass.height = h;
    pass.active = true;
    bound.encodedPipeline = nullptr;
    bound.graphicsSetsDirty = ~0u;
    bound.graphicsPushDirty = true;
    bound.vertexBuffersDirty = ~0u;
    bound.viewportDirty = bound.scissorDirty = true;
    counts.passes++;
}

void KCommandBuffer::suspendRender() {
    pass.suspending = true;
    endEncoder();
    pass.suspending = false;
}

void KCommandBuffer::resumeRender() {
    MTLRenderPassDescriptor* d = [pass.desc copy];
    for (int i = 0; i < 8; i++)
        if (d.colorAttachments[i].texture)
            d.colorAttachments[i].loadAction = d.colorAttachments[i].texture.storageMode == MTLStorageModeMemoryless
                ? MTLLoadActionDontCare : MTLLoadActionLoad;
    if (d.depthAttachment.texture) d.depthAttachment.loadAction = MTLLoadActionLoad;
    if (d.stencilAttachment.texture) d.stencilAttachment.loadAction = MTLLoadActionLoad;
    beginRender(d, pass.width, pass.height, true);
    counts.passes--;
}

void KCommandBuffer::endRender() {
    endEncoder();
    pass.renderPass = nullptr;
    pass.framebuffer = nullptr;
    for (auto& d : ts.deferred) writeTimestamp(d.first, d.second);
    ts.deferred.clear();
    ts.split = false;
}

void KCommandBuffer::resetState() {
    bound.graphicsPipeline = bound.computePipeline = nullptr;
    for (auto& s : bound.graphicsSets) s = KBoundSet();
    for (auto& s : bound.computeSets) s = KBoundSet();
    for (auto& v : bound.vertexBuffers) v = VB();
    bound.indexBuffer = nil;
    bound.viewports.clear();
    bound.scissors.clear();
    bound.pushSize = 0;
    memset(bound.blendConstants, 0, sizeof(bound.blendConstants));
    bound.stencilRef[0] = bound.stencilRef[1] = 0;
    bound.depthBiasDirty = bound.blendConstantsDirty = bound.stencilRefDirty = false;
    counts.draws = counts.dispatches = counts.passes = 0;
    fx.snapshots = 0;
    bound.encodedPipeline = nullptr;
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
        cb->dump.dropping = true;
        cb->endEncoder();
        cb->dump.dropping = false;
        cb->dump.desc = nil;
        cb->dump.name.clear();
        cb->mtl = nil;
    }
    cb->releaseTimestamps();
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
    cb->labels.zones.clear();
    cb->beginCommands();
    cb->resetState();
    cb->ring.reset();
    return VK_SUCCESS;
}

VKFN(VkResult) vkEndCommandBuffer(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb->pass.active) KERR("vkEndCommandBuffer inside a render pass");
    // The game closes every zone of a frame: one left open means a zone's end was not seen (the benchmark's postfix
    // missed a call the JIT inlined), and the encoders after it carry the wrong zone's name
    if (!cb->labels.zones.empty())
        KERR_ONCE("zone labels: %zu zones left open at the end of a recording, from \"%s\": encoder names may name the wrong zone",
                  cb->labels.zones.size(), cb->labels.zones.front().c_str());
    cb->endEncoder();
    cb->joinCommands();
    return VK_SUCCESS;
}

// ---------------------------------------------------------------------------------------------------------------
