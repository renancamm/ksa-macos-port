// Draws and dispatches, and tessellated draws: the vertex stage as a kernel, the control stage as a kernel, the
// evaluation stage as the post-tessellation vertex function.
#include "kmtl.h"

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

// The shared scratch buffer, at least `size` bytes. A grown buffer replaces the old one for later draws; command
// buffers already recorded keep the old one alive until they complete
static id<MTLBuffer> tessScratch(KDevice* dev, uint64_t size) {
    std::lock_guard<std::mutex> lock(dev->tessScratchMutex);
    if (dev->tessScratch && dev->tessScratch.length >= size) return dev->tessScratch;
    uint64_t len = kmtlAlignUp<uint64_t>(size, 16 << 20);
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
            KERR_ONCE("non-indexed indirect tessellated draws are not supported");
            return;
        }
        maxVerts = std::min<uint64_t>(cb->indexBytes / idxSize, kmtlTessCap(dev));
    } else {
        maxVerts = (uint64_t)t.count * t.instances;
    }
    uint64_t maxPatches = maxVerts / p->tessInCP;
    const KTessStages& st = p->tessPlain;
    if (!maxPatches || !st.vertex[idxType] || !st.control) return;
    // Scratch layout: vertex outputs, control outputs, per-patch outputs, tessellation factors
    auto align = [](uint64_t n) { return kmtlAlignUp<uint64_t>(n, 256); };
    NSUInteger levelSize = p->tessQuads ? sizeof(MTLQuadTessellationFactorsHalf) : sizeof(MTLTriangleTessellationFactorsHalf);
    NSUInteger vtxOff = 0;
    NSUInteger tcOff = vtxOff + align(std::max<uint64_t>(maxVerts * st.vertexStride, 16));
    uint64_t tcBytes = p->tessPassthrough ? 16 : maxPatches * p->tessOutCP * st.controlStride;
    NSUInteger patchOff = tcOff + align(std::max<uint64_t>(tcBytes, 16));
    NSUInteger levelsOff = patchOff + align(maxPatches * st.patchStride);
    uint64_t scratchSize = levelsOff + align(maxPatches * levelSize);
    id<MTLBuffer> scratch = tessScratch(dev, scratchSize);
    if (!scratch) return;
    KLOG_LIMIT(1, 12, "tessellated draw: %s%s, up to %llu vertices / %llu patches, scratch %.1f MB", t.indexed ? "indexed" : "",
               t.indirect ? " indirect" : "", (unsigned long long)maxVerts, (unsigned long long)maxPatches, scratchSize / 1048576.0);
    id<MTLComputePipelineState> vps = st.vertex[idxType];
    uint32_t vw = (uint32_t)vps.threadExecutionWidth;
    uint32_t wg = lcm(p->tessOutCP, 32);
    while (wg > st.control.maxTotalThreadsPerThreadgroup && wg > p->tessOutCP) wg /= 2;
    uint32_t patches = t.indirect ? 0 : (t.count / p->tessInCP) * t.instances;

    // The game's graphics resources, bound for a kernel (the control kernel has no stage-in, so no vertex buffers)
    auto bindGame = [&](id<MTLComputeCommandEncoder> e, bool vertexBuffers) {
        for (uint32_t s = 0; s < kMaxSets; s++)
            if (cb->graphicsSets[s].buffer) [e setBuffer:cb->graphicsSets[s].buffer offset:cb->graphicsSets[s].offset atIndex:s];
        if (cb->pushSize) [e setBytes:cb->push length:kmtlAlignUp(cb->pushSize, 16u) atIndex:kPushConstantBufferIndex];
        for (uint32_t b = 0; vertexBuffers && b < 16; b++)
            if (cb->vertexBuffers[b].buf) [e setBuffer:cb->vertexBuffers[b].buf offset:cb->vertexBuffers[b].off atIndex:vertexBufferIndex(b)];
    };

    // The vertex and control kernels are the draw's vertex work: they wait on what the draw's vertex stage would
    cb->suspendRender();
    id<MTLComputeCommandEncoder> enc = cb->computeEncoder(KCommandBuffer::kClassVertex);
    id<MTLBuffer> args = nil;
    NSUInteger argsOff = 0;
    const uint32_t* argsCpu = nullptr;
    if (t.indirect) {
        // args: [0..5] stage-in region, [6..8] vertex groups, [9..11] control groups, [12..13] control params,
        // [14..17] drawPatches arguments
        KUploadRing::Place at = cb->ring.alloc(dev, 19 * 4);
        args = at.buffer;
        argsOff = at.offset;
        argsCpu = (const uint32_t*)at.ptr;
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
            const uint32_t* a = argsCpu;
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
    if (kmtlConfig().gpuProfileSplit) {
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
    if (!kmtlFlushGraphics(cb)) return;
    id<MTLRenderCommandEncoder> r = cb->render;
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
    if (kmtlThrottled(cb->graphicsPipeline)) return;
    if (cb->graphicsPipeline && cb->graphicsPipeline->tess) {
        if (!vertexCount || !instanceCount) return;
        KTessDraw t;
        t.count = vertexCount; t.instances = instanceCount; t.first = firstVertex; t.firstInstance = firstInstance;
        tessDraw(cb, t);
        return;
    }
    if (!kmtlFlushGraphics(cb) || !vertexCount || !instanceCount) return;
    kmtlSetDrawId(cb, 0);
    if (cb->graphicsPipeline->triangleFan) {
        if (vertexCount < 3) return;
        id<MTLBuffer> fan = fanIndexBuffer(cb->device, vertexCount);
        kmtlForEachView(cb, [&] {
            [cb->render drawIndexedPrimitives:MTLPrimitiveTypeTriangle indexCount:(vertexCount - 2) * 3 indexType:MTLIndexTypeUInt32
                                  indexBuffer:fan indexBufferOffset:0
                                instanceCount:instanceCount baseVertex:firstVertex baseInstance:firstInstance];
        });
        kmtlProfNote(cb, cb->graphicsPipeline, false, 1);
        cb->draws++;
        return;
    }
    kmtlForEachView(cb, [&] {
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
    if (kmtlThrottled(cb->graphicsPipeline)) return;
    if (cb->graphicsPipeline && cb->graphicsPipeline->tess) {
        if (!indexCount || !instanceCount || !cb->indexBuffer) return;
        KTessDraw t;
        t.indexed = true; t.count = indexCount; t.instances = instanceCount; t.first = firstIndex;
        t.vertexOffset = vertexOffset; t.firstInstance = firstInstance;
        tessDraw(cb, t);
        return;
    }
    if (!kmtlFlushGraphics(cb) || !indexCount || !instanceCount || !cb->indexBuffer) return;
    if (cb->graphicsPipeline->triangleFan) KERR_ONCE("indexed triangle fans are not emulated (drawn as lists)");
    kmtlSetDrawId(cb, 0);
    kmtlForEachView(cb, [&] {
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
    if (kmtlThrottled(cb->graphicsPipeline)) return;
    if (!kmtlFlushGraphics(cb)) return;
    auto* buf = (KBuffer*)buffer;
    if (stride == 0) stride = sizeof(VkDrawIndirectCommand);
    for (uint32_t i = 0; i < drawCount; i++) {
        kmtlSetDrawId(cb, i);
        kmtlForEachView(cb, [&] {
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
    if (kmtlThrottled(cb->graphicsPipeline)) return;
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
    if (!kmtlFlushGraphics(cb) || !cb->indexBuffer) return;
    auto* buf = (KBuffer*)buffer;
    if (stride == 0) stride = sizeof(VkDrawIndexedIndirectCommand);
    for (uint32_t i = 0; i < drawCount; i++) {
        kmtlSetDrawId(cb, i);
        kmtlForEachView(cb, [&] {
            [cb->render drawIndexedPrimitives:cb->graphicsPipeline->primitive indexType:cb->indexType indexBuffer:cb->indexBuffer
                            indexBufferOffset:cb->indexOffset indirectBuffer:buf->mtl
                         indirectBufferOffset:buf->offset + offset + (VkDeviceSize)i * stride];
        });
    }
    kmtlProfNote(cb, cb->graphicsPipeline, false, drawCount);
    cb->draws += drawCount;
}

// After every dispatch, ray traces included: the profiler's note and the count, and with KSA_MTL_GPU_PROFILE_SPLIT=1
// the encoder ends, so the profiler times each dispatch alone
static void dispatched(KCommandBuffer* cb) {
    kmtlProfNote(cb, cb->computePipeline, true, 1);
    cb->dispatches++;
    if (kmtlConfig().gpuProfileSplit) cb->endEncoder();
}

VKFN(void) vkCmdDispatch(VkCommandBuffer commandBuffer, uint32_t x, uint32_t y, uint32_t z) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (kmtlThrottled(cb->computePipeline)) return;
    if (!x || !y || !z || !kmtlFlushCompute(cb)) return;
    [cb->compute dispatchThreadgroups:MTLSizeMake(x, y, z) threadsPerThreadgroup:cb->computePipeline->workgroup];
    dispatched(cb);
}

VKFN(void) vkCmdDispatchIndirect(VkCommandBuffer commandBuffer, VkBuffer buffer, VkDeviceSize offset) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (kmtlThrottled(cb->computePipeline)) return;
    if (!kmtlFlushCompute(cb)) return;
    auto* buf = (KBuffer*)buffer;
    [cb->compute dispatchThreadgroupsWithIndirectBuffer:buf->mtl indirectBufferOffset:buf->offset + offset
                                  threadsPerThreadgroup:cb->computePipeline->workgroup];
    dispatched(cb);
}

// Ray tracing pipelines are compute kernels of 8x8 threads; the launch size goes to kRtLaunchBufferIndex and the
// shader binding table is not read (see rtpipeline.mm)
VKFN(void) vkCmdTraceRaysKHR(VkCommandBuffer commandBuffer, const VkStridedDeviceAddressRegionKHR*, const VkStridedDeviceAddressRegionKHR*,
                             const VkStridedDeviceAddressRegionKHR*, const VkStridedDeviceAddressRegionKHR*, uint32_t w, uint32_t h, uint32_t d) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (kmtlThrottled(cb->computePipeline)) return;
    if (!w || !h || !d || !kmtlFlushCompute(cb)) return;
    uint32_t size[4] = {w, h, d, 0};
    [cb->compute setBytes:size length:sizeof(size) atIndex:kRtLaunchBufferIndex];
    MTLSize wg = cb->computePipeline->workgroup;
    [cb->compute dispatchThreadgroups:MTLSizeMake((w + wg.width - 1) / wg.width, (h + wg.height - 1) / wg.height, d)
                threadsPerThreadgroup:wg];
    dispatched(cb);
}

VKFN(void) vkCmdDispatchBase(VkCommandBuffer commandBuffer, uint32_t bx, uint32_t by, uint32_t bz, uint32_t x, uint32_t y, uint32_t z) {
    if (bx || by || bz) KUNIMPL();
    vkCmdDispatch(commandBuffer, x, y, z);
}

// ---------------------------------------------------------------------------------------------------------------
