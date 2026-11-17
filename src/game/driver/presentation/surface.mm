// Surfaces over the GLFW window's CAMetalLayer, and what the game asks of them: presentation support, their extent and
// capabilities, their formats (HDR10 on a display with EDR) and present modes. The swapchains are swapchain.mm's.
#include "kmtl.h"
#include <iterator>

VKFN(VkResult) vkCreateMetalSurfaceEXT(VkInstance, const VkMetalSurfaceCreateInfoEXT* ci, const VkAllocationCallbacks*, VkSurfaceKHR* out) {
    KTRACE_FIRST();
    auto* s = new KSurface();
    s->layer = (CAMetalLayer*)ci->pLayer;
    KLOG(1, "surface on CAMetalLayer %p", ci->pLayer);
    *out = (VkSurfaceKHR)s;
    return VK_SUCCESS;
}

VKFN(void) vkDestroySurfaceKHR(VkInstance, VkSurfaceKHR surface, const VkAllocationCallbacks*) {
    if (!surface) return;
    kmtlSetHdr((KSurface*)surface, false);
    delete (KSurface*)surface;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceSupportKHR(VkPhysicalDevice, uint32_t family, VkSurfaceKHR, VkBool32* supported) {
    *supported = family == 0;  // presentation is on the graphics queue
    return VK_SUCCESS;
}

VkExtent2D kmtlLayerExtent(CAMetalLayer* layer) {
    CGSize size = layer.bounds.size;  // property reads are safe off the main thread
    CGFloat scale = layer.contentsScale;
    return {(uint32_t)(size.width * scale + 0.5), (uint32_t)(size.height * scale + 0.5)};
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceCapabilitiesKHR(VkPhysicalDevice, VkSurfaceKHR surface, VkSurfaceCapabilitiesKHR* c) {
    KTRACE_FIRST();
    auto* s = (KSurface*)surface;
    VkExtent2D e = kmtlLayerExtent(s->layer);
    if (e.width == 0) e.width = 1;
    if (e.height == 0) e.height = 1;
    c->minImageCount = 2;
    c->maxImageCount = 3;
    c->currentExtent = e;
    c->minImageExtent = {1, 1};
    c->maxImageExtent = {16384, 16384};
    c->maxImageArrayLayers = 1;
    c->supportedTransforms = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c->currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    c->supportedCompositeAlpha = VK_COMPOSITE_ALPHA_OPAQUE_BIT_KHR | VK_COMPOSITE_ALPHA_PRE_MULTIPLIED_BIT_KHR;
    c->supportedUsageFlags = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
        VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_STORAGE_BIT;
    KLOG(1, "surface extent %ux%u", e.width, e.height);
    return VK_SUCCESS;
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceCapabilities2KHR(VkPhysicalDevice pd, const VkPhysicalDeviceSurfaceInfo2KHR* info, VkSurfaceCapabilities2KHR* c) {
    return vkGetPhysicalDeviceSurfaceCapabilitiesKHR(pd, info->surface, &c->surfaceCapabilities);
}

// HDR10 (PQ in Rec.2020) last, and only on a display with EDR: hdr.mm shows it with EDR, mapping the game's HDR UI
// Brightness to the display's SDR white. Not scRGB: its 1.0 is 80 nits to the game and SDR white to macOS
static const VkSurfaceFormatKHR kSurfaceFormats[] = {
    {VK_FORMAT_B8G8R8A8_UNORM, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
    {VK_FORMAT_B8G8R8A8_SRGB, VK_COLOR_SPACE_SRGB_NONLINEAR_KHR},
    {VK_FORMAT_A2B10G10R10_UNORM_PACK32, VK_COLOR_SPACE_HDR10_ST2084_EXT},
};

static uint32_t surfaceFormatCount(VkSurfaceKHR surface) {
    bool hdr = kmtlConfig().hdrOutput && surface && kmtlEdrAvailable(((KSurface*)surface)->layer);
    return uint32_t(std::size(kSurfaceFormats)) - (hdr ? 0 : 1);
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceFormatsKHR(VkPhysicalDevice, VkSurfaceKHR surface, uint32_t* count, VkSurfaceFormatKHR* out) {
    KTRACE_FIRST();
    return kmtlEnumerate(surfaceFormatCount(surface), count, out, [](uint32_t i, VkSurfaceFormatKHR& o) { o = kSurfaceFormats[i]; });
}

VKFN(VkResult) vkGetPhysicalDeviceSurfaceFormats2KHR(VkPhysicalDevice, const VkPhysicalDeviceSurfaceInfo2KHR* info, uint32_t* count, VkSurfaceFormat2KHR* out) {
    return kmtlEnumerate(surfaceFormatCount(info->surface), count, out,
                         [](uint32_t i, VkSurfaceFormat2KHR& o) { o.surfaceFormat = kSurfaceFormats[i]; });
}

VKFN(VkResult) vkGetPhysicalDeviceSurfacePresentModesKHR(VkPhysicalDevice, VkSurfaceKHR, uint32_t* count, VkPresentModeKHR* out) {
    KTRACE_FIRST();
    static const VkPresentModeKHR modes[] = {VK_PRESENT_MODE_FIFO_KHR, VK_PRESENT_MODE_IMMEDIATE_KHR, VK_PRESENT_MODE_MAILBOX_KHR};
    return kmtlEnumerate(3, count, out, [](uint32_t i, VkPresentModeKHR& o) { o = modes[i]; });
}
