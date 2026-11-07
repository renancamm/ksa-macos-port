// Transfers (copies, blits, fills, clears, updates), queries and debug labels.
#include "kmtl.h"

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
    KUploadRing::Place at = cb->ring.alloc(cb->device, size);
    memcpy(at.ptr, data, size);
    [cb->blitEncoder() copyFromBuffer:at.buffer sourceOffset:at.offset toBuffer:d->mtl destinationOffset:d->offset + offset size:size];
}

// Image clears are load-clear render passes, one per level and layer
static void clearImage(KCommandBuffer* cb, KImage* img, const VkClearColorValue* color, const VkClearDepthStencilValue* ds,
                       const VkImageSubresourceRange& r) {
    const KFormatInfo* fi = kmtlFormat(img->format);
    if (!fi || !fi->renderable || !(img->mtl.usage & MTLTextureUsageRenderTarget)) {
        KERR_LIMIT(5, "clear of non-renderable image (format %d) skipped", img->format);
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
                d.colorAttachments[0].clearColor = kmtlClearColor(img->format, *color);
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
// Queries and debug labels

// Timestamps (the game's GPU profiler; zones.mm says how they are answered)

void KCommandBuffer::writeTimestamp(KQueryPool* pool, uint32_t query) {
    uint32_t mode = kmtlZoneMode();
    // Labels: answered at once, no encoder and no wait
    if (mode == kZonesLabels) {
        std::lock_guard<std::mutex> lock(pool->m);
        if (query < pool->count) {
            pool->values[query] = 0;
            pool->available[query] = 1;
        }
        pool->cv.notify_all();
        return;
    }
    if (render) {
        // Split: the pass ends here and goes on after the timestamp, which waits for its first half and is waited for by
        // its second (beginRender's roles)
        if (mode == kZonesSplit && deferredStores && passDesc) {
            suspendRender();
            stampTimestamp(pool, query);
            resumeRender();
            return;
        }
        tsDeferred.push_back({pool, query});
        return;
    }
    stampTimestamp(pool, query);
}

// A tiny blit encoder after everything recorded so far, whose end is the timestamp
void KCommandBuffer::stampTimestamp(KQueryPool* pool, uint32_t query) {
    if (!tsSamples) {
        tsSamples = kmtlAcquireTimestamps(device, "vkCmdWriteTimestamp");
        if (!tsSamples) {
            tsWrites.push_back({pool, query, UINT32_MAX});
            return;
        }
        tsUsed = 0;
    }
    if (tsUsed + 2 > kTimestampSamples) {
        tsWrites.push_back({pool, query, UINT32_MAX});
        return;
    }
    endEncoder();
    // It waits for everything before it, MSAA resolves too: its wait clears what later encoders would wait for
    // (waitForResolves), so they would no longer wait for those
    waitForAllResolves();
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
        kmtlRecycleTimestamps(samples);
    }];
}

void KCommandBuffer::releaseTimestamps() {
    kmtlRecycleTimestamps(tsSamples);
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
