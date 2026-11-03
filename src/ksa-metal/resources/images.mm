// Images, image views and samplers.
#include "kmtl.h"

// ---------------------------------------------------------------------------------------------------------------
// Images

static MTLTextureDescriptor* textureDescriptor(const VkImageCreateInfo* ci, MTLPixelFormat pf, const KFormatInfo* fi) {
    MTLTextureDescriptor* d = [MTLTextureDescriptor new];
    d.pixelFormat = pf;
    d.width = ci->extent.width;
    d.height = ci->extent.height;
    d.depth = ci->extent.depth;
    d.mipmapLevelCount = ci->mipLevels;
    d.sampleCount = ci->samples;
    uint32_t layers = ci->arrayLayers;
    bool cube = (ci->flags & VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT) && layers % 6 == 0 && ci->samples == 1;
    switch (ci->imageType) {
    case VK_IMAGE_TYPE_1D:
        d.textureType = layers > 1 ? MTLTextureType1DArray : MTLTextureType1D;
        d.arrayLength = layers;
        break;
    case VK_IMAGE_TYPE_3D:
        d.textureType = MTLTextureType3D;
        d.arrayLength = 1;
        break;
    default:
        if (ci->samples > 1) {
            d.textureType = layers > 1 ? MTLTextureType2DMultisampleArray : MTLTextureType2DMultisample;
            d.arrayLength = layers;
        } else if (cube) {
            d.textureType = layers > 6 ? MTLTextureTypeCubeArray : MTLTextureTypeCube;
            d.arrayLength = layers / 6;
        } else {
            d.textureType = layers > 1 ? MTLTextureType2DArray : MTLTextureType2D;
            d.arrayLength = layers;
        }
        break;
    }
    MTLTextureUsage u = MTLTextureUsageShaderRead;
    VkImageUsageFlags vu = ci->usage;
    if (vu & VK_IMAGE_USAGE_STORAGE_BIT) u |= MTLTextureUsageShaderWrite;
    if (vu & (VK_IMAGE_USAGE_STORAGE_BIT)) {
        if (pf == MTLPixelFormatR32Uint || pf == MTLPixelFormatR32Sint || pf == MTLPixelFormatRG32Uint) u |= MTLTextureUsageShaderAtomic;
    }
    if (fi->renderable && (vu & (VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
                                 VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT |
                                 VK_IMAGE_USAGE_STORAGE_BIT)))
        u |= MTLTextureUsageRenderTarget;
    if ((ci->flags & VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT) || fi->depth || fi->stencil) u |= MTLTextureUsagePixelFormatView;
    d.usage = u;
    d.storageMode = MTLStorageModePrivate;
    d.hazardTrackingMode = MTLHazardTrackingModeUntracked;
    // Textures read at scattered positions without lossless compression, where a compressed texture fetches a whole
    // block for each read: the ones the game only uploads and samples (the terrain's height maps, lookup maps; block
    // formats have no lossless compression anyway), 3D images (cloud and smoke noise, the clouds' shadow volume), and
    // square power-of-two storage images of 64 to 256 (the ocean's FFT waves at each ocean simulation quality, the Milky
    // Way's noise), which the game makes on the GPU and repeats across the world. Screen-sized targets, read in step
    // with their pixels, keep it (without it: +1 ms), and so does depth (+3-9 ms). Same contents; on an M5 the terrain
    // draws about 2-4 % faster, over clouds and the sea up to 10 %, and scattered reads of a 128^3 R8 volume run 2.6x
    // faster.
    if (kmtlConfig().uncompressedTextures) {
        uint32_t w = ci->extent.width;
        bool uploaded = (vu & VK_IMAGE_USAGE_TRANSFER_DST_BIT) && (vu & VK_IMAGE_USAGE_SAMPLED_BIT) &&
                        !(vu & (VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT));
        bool tile = ci->imageType == VK_IMAGE_TYPE_2D && (vu & VK_IMAGE_USAGE_STORAGE_BIT) && w == ci->extent.height &&
                    w >= 64 && w <= 256 && !(w & (w - 1));
        if (uploaded || tile || ci->imageType == VK_IMAGE_TYPE_3D) d.allowGPUOptimizedContents = NO;
    }
    return d;
}

VKFN(VkResult) vkCreateImage(VkDevice device, const VkImageCreateInfo* ci, const VkAllocationCallbacks*, VkImage* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    const KFormatInfo* fi = kmtlFormat(ci->format);
    if (!fi) {
        KERR("vkCreateImage: unsupported format %d", ci->format);
        return VK_ERROR_FORMAT_NOT_SUPPORTED;
    }
    auto* img = new KImage();
    img->device = dev;
    img->info = *ci;
    img->info.pNext = nullptr;
    img->info.pQueueFamilyIndices = nullptr;
    img->format = ci->format;
    img->pixelFormat = fi->mtl;
    img->desc = textureDescriptor(ci, fi->mtl, fi);
    if (ci->tiling == VK_IMAGE_TILING_LINEAR) img->desc.storageMode = MTLStorageModeShared;
    img->sizeAlign = [dev->mtl heapTextureSizeAndAlignWithDescriptor:img->desc];
    KLOG(2, "image %p %ux%ux%u fmt %d mips %u layers %u samples %u usage 0x%x -> %lu bytes", img, ci->extent.width,
         ci->extent.height, ci->extent.depth, ci->format, ci->mipLevels, ci->arrayLayers, ci->samples, ci->usage,
         (unsigned long)img->sizeAlign.size);
    kmtlPassDumpTrack(img);
    *out = (VkImage)img;
    return VK_SUCCESS;
}

void kmtlDestroyImage(KImage* img) {
    if (!img) return;
    kmtlPassDumpForget(img);
    if (img->ownsTexture && img->mtl) img->device->removeResident(img->mtl);
    delete img;
}

VKFN(void) vkDestroyImage(VkDevice, VkImage image, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    kmtlDestroyImage((KImage*)image);
}

// Transient attachments live only in tile memory: Metal memoryless textures, outside the heaps; the memory the game
// binds them to is a token allocation
static bool memoryless(const VkImageCreateInfo& ci) {
    const VkImageUsageFlags attachmentOnly = VK_IMAGE_USAGE_TRANSIENT_ATTACHMENT_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
                                             VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_INPUT_ATTACHMENT_BIT;
    return (ci.usage & VK_IMAGE_USAGE_TRANSIENT_ATTACHMENT_BIT) && !(ci.usage & ~attachmentOnly) && ci.tiling == VK_IMAGE_TILING_OPTIMAL;
}

static void imageRequirements(KImage* img, VkMemoryRequirements* r) {
    if (memoryless(img->info)) {
        r->size = 4096;
        r->alignment = 4096;
        r->memoryTypeBits = 0x1;
        return;
    }
    r->size = img->sizeAlign.size;
    r->alignment = std::max<VkDeviceSize>(img->sizeAlign.align, 256);
    r->memoryTypeBits = img->info.tiling == VK_IMAGE_TILING_LINEAR ? 0x6 : 0x1;
}

VKFN(void) vkGetImageMemoryRequirements(VkDevice, VkImage image, VkMemoryRequirements* r) {
    KTRACE_FIRST();
    imageRequirements((KImage*)image, r);
}

VKFN(void) vkGetImageMemoryRequirements2(VkDevice, const VkImageMemoryRequirementsInfo2* info, VkMemoryRequirements2* r) {
    KTRACE_FIRST();
    auto* img = (KImage*)info->image;
    imageRequirements(img, &r->memoryRequirements);
    kmtlFillDedicated(r->pNext, img->sizeAlign.size >= (64u << 20));
}

VKFN(void) vkGetDeviceImageMemoryRequirements(VkDevice device, const VkDeviceImageMemoryRequirements* info, VkMemoryRequirements2* r) {
    KTRACE_FIRST();
    VkImage tmp = VK_NULL_HANDLE;
    if (vkCreateImage(device, info->pCreateInfo, nullptr, &tmp) != VK_SUCCESS) {
        r->memoryRequirements = {};
        return;
    }
    imageRequirements((KImage*)tmp, &r->memoryRequirements);
    kmtlFillDedicated(r->pNext, false);
    kmtlDestroyImage((KImage*)tmp);
}

static VkResult bindImage(VkImage image, VkDeviceMemory memory, VkDeviceSize offset) {
    auto* img = (KImage*)image;
    auto* m = (KDeviceMemory*)memory;
    if (memoryless(img->info)) {
        img->desc.storageMode = MTLStorageModeMemoryless;
        img->desc.usage = MTLTextureUsageRenderTarget;
        img->mtl = [img->device->mtl newTextureWithDescriptor:img->desc];
        if (img->mtl) {
            KLOG(1, "memoryless attachment %ux%u format %d samples %u", img->info.extent.width, img->info.extent.height,
                 img->format, img->info.samples);
            return VK_SUCCESS;
        }
        KERR("vkBindImageMemory: memoryless attachment %ux%u format %d failed", img->info.extent.width,
             img->info.extent.height, img->format);
        img->desc = nil;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    img->desc.storageMode = m->heap.storageMode;
    img->desc.cpuCacheMode = m->heap.cpuCacheMode;
    img->mtl = [m->heap newTextureWithDescriptor:img->desc offset:offset];
    if (!img->mtl) {
        KERR("vkBindImageMemory: texture placement failed (offset %llu, heap %lu)", (unsigned long long)offset,
             (unsigned long)m->heap.size);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    const KFormatInfo* fi = kmtlFormat(img->format);
    if (fi && !fi->depth && !fi->stencil && (img->mtl.usage & MTLTextureUsageRenderTarget) &&
        (img->info.usage & (VK_IMAGE_USAGE_STORAGE_BIT | VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT)))
        kmtlZeroImage(img);
    return VK_SUCCESS;
}

VKFN(VkResult) vkBindImageMemory(VkDevice, VkImage image, VkDeviceMemory memory, VkDeviceSize offset) {
    KTRACE_FIRST();
    return bindImage(image, memory, offset);
}

VKFN(VkResult) vkBindImageMemory2(VkDevice, uint32_t n, const VkBindImageMemoryInfo* infos) {
    KTRACE_FIRST();
    for (uint32_t i = 0; i < n; i++) {
        VkResult r = bindImage(infos[i].image, infos[i].memory, infos[i].memoryOffset);
        if (r != VK_SUCCESS) return r;
    }
    return VK_SUCCESS;
}

VKFN(void) vkGetImageSubresourceLayout(VkDevice, VkImage image, const VkImageSubresource* sub, VkSubresourceLayout* l) {
    auto* img = (KImage*)image;
    const KFormatInfo* fi = kmtlFormat(img->format);
    uint32_t w = std::max(1u, img->info.extent.width >> sub->mipLevel);
    uint32_t h = std::max(1u, img->info.extent.height >> sub->mipLevel);
    l->offset = 0;
    l->rowPitch = (w + fi->blockW - 1) / fi->blockW * fi->blockBytes;
    l->depthPitch = l->rowPitch * ((h + fi->blockH - 1) / fi->blockH);
    l->arrayPitch = l->depthPitch;
    l->size = l->depthPitch;
}

KImage* kmtlCreateSwapchainImage(KDevice* dev, VkFormat format, uint32_t w, uint32_t h) {
    VkImageCreateInfo ci{VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = format;
    ci.extent = {w, h, 1};
    ci.mipLevels = 1;
    ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT |
        VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    VkImage handle;
    if (vkCreateImage((VkDevice)dev, &ci, nullptr, &handle) != VK_SUCCESS) return nullptr;
    auto* img = (KImage*)handle;
    img->mtl = [dev->mtl newTextureWithDescriptor:img->desc];
    if (!img->mtl) {
        kmtlDestroyImage(img);
        return nullptr;
    }
    img->ownsTexture = true;
    dev->addResident(img->mtl);
    return img;
}

// ---------------------------------------------------------------------------------------------------------------
// Image views

static MTLTextureSwizzle swz(VkComponentSwizzle s, MTLTextureSwizzle identity) {
    switch (s) {
    case VK_COMPONENT_SWIZZLE_ZERO: return MTLTextureSwizzleZero;
    case VK_COMPONENT_SWIZZLE_ONE: return MTLTextureSwizzleOne;
    case VK_COMPONENT_SWIZZLE_R: return MTLTextureSwizzleRed;
    case VK_COMPONENT_SWIZZLE_G: return MTLTextureSwizzleGreen;
    case VK_COMPONENT_SWIZZLE_B: return MTLTextureSwizzleBlue;
    case VK_COMPONENT_SWIZZLE_A: return MTLTextureSwizzleAlpha;
    default: return identity;
    }
}

VKFN(VkResult) vkCreateImageView(VkDevice device, const VkImageViewCreateInfo* ci, const VkAllocationCallbacks*, VkImageView* out) {
    KTRACE_FIRST();
    auto* img = (KImage*)ci->image;
    if (!img->mtl) {
        KERR("vkCreateImageView: image %p has no memory bound", img);
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    auto* v = new KImageView();
    v->image = img;
    v->format = ci->format;
    const VkImageSubresourceRange& r = ci->subresourceRange;
    v->baseLevel = r.baseMipLevel;
    v->levelCount = r.levelCount == VK_REMAINING_MIP_LEVELS ? img->info.mipLevels - r.baseMipLevel : r.levelCount;
    v->baseLayer = r.baseArrayLayer;
    v->layerCount = r.layerCount == VK_REMAINING_ARRAY_LAYERS ? img->info.arrayLayers - r.baseArrayLayer : r.layerCount;

    MTLPixelFormat pf = kmtlPixelFormat(ci->format);
    const KFormatInfo* fi = kmtlFormat(ci->format);
    // Aspect-only views of combined depth/stencil formats
    if (fi && fi->depth && fi->stencil) {
        if (r.aspectMask == VK_IMAGE_ASPECT_STENCIL_BIT) pf = MTLPixelFormatX32_Stencil8;
    }
    MTLTextureType tt = img->mtl.textureType;
    switch (ci->viewType) {
    case VK_IMAGE_VIEW_TYPE_1D: tt = MTLTextureType1D; break;
    case VK_IMAGE_VIEW_TYPE_1D_ARRAY: tt = MTLTextureType1DArray; break;
    case VK_IMAGE_VIEW_TYPE_2D: tt = img->info.samples > 1 ? MTLTextureType2DMultisample : MTLTextureType2D; break;
    case VK_IMAGE_VIEW_TYPE_2D_ARRAY: tt = img->info.samples > 1 ? MTLTextureType2DMultisampleArray : MTLTextureType2DArray; break;
    case VK_IMAGE_VIEW_TYPE_CUBE: tt = MTLTextureTypeCube; break;
    case VK_IMAGE_VIEW_TYPE_CUBE_ARRAY: tt = MTLTextureTypeCubeArray; break;
    case VK_IMAGE_VIEW_TYPE_3D: tt = MTLTextureType3D; break;
    default: break;
    }
    MTLTextureSwizzleChannels sw = MTLTextureSwizzleChannelsMake(
        swz(ci->components.r, MTLTextureSwizzleRed), swz(ci->components.g, MTLTextureSwizzleGreen),
        swz(ci->components.b, MTLTextureSwizzleBlue), swz(ci->components.a, MTLTextureSwizzleAlpha));
    bool identitySwizzle = sw.red == MTLTextureSwizzleRed && sw.green == MTLTextureSwizzleGreen &&
        sw.blue == MTLTextureSwizzleBlue && sw.alpha == MTLTextureSwizzleAlpha;
    bool whole = pf == img->mtl.pixelFormat && tt == img->mtl.textureType && v->baseLevel == 0 &&
        v->levelCount == img->mtl.mipmapLevelCount && v->baseLayer == 0 && v->layerCount == img->info.arrayLayers &&
        identitySwizzle;
    if (whole) {
        v->mtl = img->mtl;
    } else {
        NSRange levels = NSMakeRange(v->baseLevel, v->levelCount);
        NSRange slices = NSMakeRange(v->baseLayer, tt == MTLTextureType3D ? 1 : v->layerCount);
        if (identitySwizzle)
            v->mtl = [img->mtl newTextureViewWithPixelFormat:pf textureType:tt levels:levels slices:slices];
        else
            v->mtl = [img->mtl newTextureViewWithPixelFormat:pf textureType:tt levels:levels slices:slices swizzle:sw];
        if (!v->mtl) {
            KERR("vkCreateImageView: view failed (fmt %d type %d levels %u+%u layers %u+%u)", ci->format, ci->viewType,
                 v->baseLevel, v->levelCount, v->baseLayer, v->layerCount);
            delete v;
            return VK_ERROR_OUT_OF_DEVICE_MEMORY;
        }
    }
    *out = (VkImageView)v;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyImageView(VkDevice, VkImageView view, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KImageView*)view;
}

// ---------------------------------------------------------------------------------------------------------------
// Samplers

static MTLSamplerAddressMode addressMode(VkSamplerAddressMode m) {
    switch (m) {
    case VK_SAMPLER_ADDRESS_MODE_REPEAT: return MTLSamplerAddressModeRepeat;
    case VK_SAMPLER_ADDRESS_MODE_MIRRORED_REPEAT: return MTLSamplerAddressModeMirrorRepeat;
    case VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE: return MTLSamplerAddressModeClampToEdge;
    case VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_BORDER: return MTLSamplerAddressModeClampToBorderColor;
    case VK_SAMPLER_ADDRESS_MODE_MIRROR_CLAMP_TO_EDGE: return MTLSamplerAddressModeMirrorClampToEdge;
    default: return MTLSamplerAddressModeRepeat;
    }
}

MTLCompareFunction kmtlCompare(VkCompareOp op) {
    switch (op) {
    case VK_COMPARE_OP_NEVER: return MTLCompareFunctionNever;
    case VK_COMPARE_OP_LESS: return MTLCompareFunctionLess;
    case VK_COMPARE_OP_EQUAL: return MTLCompareFunctionEqual;
    case VK_COMPARE_OP_LESS_OR_EQUAL: return MTLCompareFunctionLessEqual;
    case VK_COMPARE_OP_GREATER: return MTLCompareFunctionGreater;
    case VK_COMPARE_OP_NOT_EQUAL: return MTLCompareFunctionNotEqual;
    case VK_COMPARE_OP_GREATER_OR_EQUAL: return MTLCompareFunctionGreaterEqual;
    default: return MTLCompareFunctionAlways;
    }
}

VKFN(VkResult) vkCreateSampler(VkDevice device, const VkSamplerCreateInfo* ci, const VkAllocationCallbacks*, VkSampler* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    MTLSamplerDescriptor* d = [MTLSamplerDescriptor new];
    d.minFilter = ci->minFilter == VK_FILTER_LINEAR ? MTLSamplerMinMagFilterLinear : MTLSamplerMinMagFilterNearest;
    d.magFilter = ci->magFilter == VK_FILTER_LINEAR ? MTLSamplerMinMagFilterLinear : MTLSamplerMinMagFilterNearest;
    d.mipFilter = ci->mipmapMode == VK_SAMPLER_MIPMAP_MODE_LINEAR ? MTLSamplerMipFilterLinear : MTLSamplerMipFilterNearest;
    d.sAddressMode = addressMode(ci->addressModeU);
    d.tAddressMode = addressMode(ci->addressModeV);
    d.rAddressMode = addressMode(ci->addressModeW);
    d.maxAnisotropy = ci->anisotropyEnable ? std::max(1, std::min(16, (int)ci->maxAnisotropy)) : 1;
    d.lodMinClamp = ci->minLod;
    d.lodMaxClamp = ci->maxLod;
    d.normalizedCoordinates = !ci->unnormalizedCoordinates;
    if (ci->compareEnable) d.compareFunction = kmtlCompare(ci->compareOp);
    switch (ci->borderColor) {
    case VK_BORDER_COLOR_FLOAT_OPAQUE_BLACK: case VK_BORDER_COLOR_INT_OPAQUE_BLACK:
        d.borderColor = MTLSamplerBorderColorOpaqueBlack; break;
    case VK_BORDER_COLOR_FLOAT_OPAQUE_WHITE: case VK_BORDER_COLOR_INT_OPAQUE_WHITE:
        d.borderColor = MTLSamplerBorderColorOpaqueWhite; break;
    default: d.borderColor = MTLSamplerBorderColorTransparentBlack; break;
    }
    // S4.6 precision, [-16, 15.999] (macOS 26+)
    if (ci->mipLodBias != 0.0f) d.lodBias = std::max(-16.0f, std::min(15.99f, ci->mipLodBias));
    if (findNext(ci->pNext, VK_STRUCTURE_TYPE_SAMPLER_REDUCTION_MODE_CREATE_INFO))
        KLOG(1, "sampler reduction mode ignored");
    d.supportArgumentBuffers = YES;
    auto* s = new KSampler();
    s->mtl = [dev->mtl newSamplerStateWithDescriptor:d];
    if (!s->mtl) {
        KERR("vkCreateSampler: sampler state failed");
        delete s;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    *out = (VkSampler)s;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySampler(VkDevice, VkSampler sampler, const VkAllocationCallbacks*) {
    delete (KSampler*)sampler;
}
