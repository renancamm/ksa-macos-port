// Swapchain over the GLFW window's CAMetalLayer, and the present: frame interpolation's frame first, the image into a
// drawable, the handlers in their order. Pacing is pacing.mm's, HDR hdr.mm's; what is measured at each present is in
// instruments.mm.
#include "kmtl.h"
#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#include <algorithm>

// The window was resized since the swapchain was made: report VK_SUBOPTIMAL_KHR like other drivers, which makes the
// game check the surface extent and rebuild (a resize it did not schedule itself, e.g. a display mode change)
static bool outdated(KSwapchain* sc) {
    VkExtent2D e = kmtlLayerExtent(sc->surface->layer);
    return e.width && e.height && (e.width != sc->extent.width || e.height != sc->extent.height);
}

VKFN(VkResult) vkCreateSwapchainKHR(VkDevice device, const VkSwapchainCreateInfoKHR* ci, const VkAllocationCallbacks*, VkSwapchainKHR* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    auto* sc = new KSwapchain();
    sc->surface = (KSurface*)ci->surface;
    sc->extent = ci->imageExtent;
    uint32_t count = std::max(2u, std::min(3u, ci->minImageCount));
    for (uint32_t i = 0; i < count; i++) {
        KImage* img = kmtlCreateSwapchainImage(dev, ci->imageFormat, ci->imageExtent.width, ci->imageExtent.height);
        if (!img) {
            KERR("vkCreateSwapchainKHR: image %u of %ux%u format %d failed", i, ci->imageExtent.width,
                 ci->imageExtent.height, ci->imageFormat);
            for (auto* made : sc->images) kmtlDestroyImage(made);
            delete sc;
            return VK_ERROR_OUT_OF_DEVICE_MEMORY;
        }
        img->mtl.label = [NSString stringWithFormat:@"swapchain %u", i];
        sc->images.push_back(img);
    }
    // The layer changes once the images exist: a failed create leaves the old swapchain's layer as it was. CAMetalLayer
    // drawable properties may be set from any thread; hopping to the main thread could deadlock
    CAMetalLayer* layer = sc->surface->layer;
    layer.device = dev->mtl;
    layer.pixelFormat = kmtlPixelFormat(ci->imageFormat);
    layer.framebufferOnly = NO;
    layer.drawableSize = CGSizeMake(ci->imageExtent.width, ci->imageExtent.height);
    sc->vsync = ci->presentMode == VK_PRESENT_MODE_FIFO_KHR || ci->presentMode == VK_PRESENT_MODE_FIFO_RELAXED_KHR;
    kmtlPaceSwapchain(sc);
    layer.displaySyncEnabled = sc->vsync;
    // The game's extra windows (detached gauges, pop-outs) ask for a see-through one and clear it to alpha 0
    layer.opaque = ci->compositeAlpha == VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR;
    kmtlSetHdr(sc->surface, ci->imageColorSpace == VK_COLOR_SPACE_HDR10_ST2084_EXT);
    dev->commitResidency();
    KLOG(1, "swapchain %ux%u format %d colour space %d present mode %d, %u images, %lu drawables, cadence %.2f ms",
         ci->imageExtent.width, ci->imageExtent.height, ci->imageFormat, ci->imageColorSpace, ci->presentMode, count,
         (unsigned long)layer.maximumDrawableCount, sc->cadenceMs.load());
    kmtlListSwapchain(sc);
    *out = (VkSwapchainKHR)sc;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySwapchainKHR(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks*) {
    auto* sc = (KSwapchain*)swapchain;
    if (!sc) return;
    vkDeviceWaitIdle(device);
    kmtlUnlistSwapchain(sc);
    kmtlOnSwapchainDestroyed(sc);
    for (auto* img : sc->images) kmtlDestroyImage(img);
    delete sc;
}

VKFN(VkResult) vkGetSwapchainImagesKHR(VkDevice, VkSwapchainKHR swapchain, uint32_t* count, VkImage* out) {
    KTRACE_FIRST();
    auto* sc = (KSwapchain*)swapchain;
    return kmtlEnumerate((uint32_t)sc->images.size(), count, out, [sc](uint32_t i, VkImage& o) { o = (VkImage)sc->images[i]; });
}

VKFN(VkResult) vkAcquireNextImageKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t, VkSemaphore, VkFence fence, uint32_t* index) {
    KTRACE_FIRST();
    KStallTimer stall("vkAcquireNextImageKHR");
    auto* sc = (KSwapchain*)swapchain;
    *index = sc->next;
    sc->next = (sc->next + 1) % sc->images.size();
    if (fence) {
        // Images are reused in queue order, so they are available as soon as acquired
        auto* f = (KFence*)fence;
        std::lock_guard<std::mutex> lock(f->m);
        f->signaled = true;
        f->cv.notify_all();
    }
    return outdated(sc) ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
}

VKFN(VkResult) vkAcquireNextImage2KHR(VkDevice device, const VkAcquireNextImageInfoKHR* info, uint32_t* index) {
    return vkAcquireNextImageKHR(device, info->swapchain, info->timeout, info->semaphore, info->fence, index);
}

// The image into a drawable (of the image's size, but for a resize in flight: then what both have)
static void present(id<MTLBlitCommandEncoder> b, id<MTLTexture> image, id<MTLTexture> drawable) {
    NSUInteger w = std::min(drawable.width, image.width), h = std::min(drawable.height, image.height);
    [b copyFromTexture:image sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(w, h, 1)
             toTexture:drawable destinationSlice:0 destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
}

VKFN(VkResult) vkQueuePresentKHR(VkQueue queue, const VkPresentInfoKHR* pi) {
    KTRACE_FIRST();
    KStallTimer stall("vkQueuePresentKHR");
    auto* q = (KQueue*)queue;
    std::lock_guard<std::mutex> lock(q->submitMutex);
    auto* ids = (const VkPresentIdKHR*)findNext(pi->pNext, VK_STRUCTURE_TYPE_PRESENT_ID_KHR);
    VkResult result = VK_SUCCESS;
    for (uint32_t i = 0; i < pi->swapchainCount; i++) {
        @autoreleasepool {
            auto* sc = (KSwapchain*)pi->pSwapchains[i];
            VkResult r = outdated(sc) ? VK_SUBOPTIMAL_KHR : VK_SUCCESS;
            KImage* img = sc->images[pi->pImageIndices[i]];
            id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
            cb.label = @"present";
            id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
            [b waitForFence:q->chain];
            // KSA_MTL_FRAME_INTERPOLATION: a frame between the last one and this one goes to the display first
            id<CAMetalDrawable> first = nil;
            if (kmtlConfig().frameInterpolation) {
                [b endEncoding];
                id<MTLTexture> mid = kmtlPresentInterpolate(q->device, cb, img->mtl);
                first = mid ? [sc->surface->layer nextDrawable] : nil;
                if (first) {
                    id<MTLBlitCommandEncoder> copy = [cb blitCommandEncoder];
                    present(copy, mid, first.texture);
                    [copy endEncoding];
                    [first addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, false); }];
                    kmtlPresentPaced(sc, cb, first, true);
                }
                b = [cb blitCommandEncoder];
                [b waitForFence:q->chain];
            }
            id<CAMetalDrawable> drawable;
            {
                KStallTimer stall("nextDrawable");
                drawable = [sc->surface->layer nextDrawable];
            }
            if (drawable) present(b, img->mtl, drawable.texture);
            [b updateFence:q->chain];
            [b endEncoding];
            [cb addCompletedHandler:^(id<MTLCommandBuffer> c) { kmtlNoteGpuTime(c); }];
            uint64_t shotsBefore = kmtlShotsTaken();
            uint64_t counted = kmtlCountPresent();  // this present's number, as the present record has it
            kmtlOnPresent(q->device, sc, img, cb, counted);
            // A screenshot's copy is in this frame's GPU time: not the game's load, left out of dynamic resolution's choice
            bool shot = kmtlShotsTaken() != shotsBefore;
            // For vkWaitForPresentKHR: when this present was made, ended on the GPU, and was shown
            kmtlPaceAtPresent(sc, cb, drawable, ids && ids->pPresentIds && i < ids->swapchainCount ? ids->pPresentIds[i] : 0,
                              counted, shot);
            if (drawable) {
                // When the display showed it (0 for an image it never showed)
                [drawable addPresentedHandler:^(id<MTLDrawable> d) { if (d.presentedTime > 0) kmtlNoteShown(d.presentedTime, true); }];
                if (kmtlConfig().latency || kmtlDrainOn()) kmtlLatencyAtPresent(drawable, cb);
                kmtlPresentPaced(sc, cb, drawable, first != nil);
            }
            [cb commit];
            if (pi->pResults) pi->pResults[i] = r;
            if (r != VK_SUCCESS) result = r;
        }
    }
    return result;
}
