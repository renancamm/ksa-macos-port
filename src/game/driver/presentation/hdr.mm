// HDR output: the surface's layer set up for HDR10 with EDR and given back its SDR state, the metadata that maps the
// game's UI brightness to the display's SDR white, and the game's brightness settings (vkSetHdrMetadataEXT,
// ksaMtlHdrNits).
#include "kmtl.h"
#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>

// HDR output. The game offers HDR in its Display settings when the surface has an HDR format, which surface.mm gives
// on a display with EDR (extended dynamic range: brighter than SDR white, as Apple's XDR displays and HDR monitors in
// HDR mode): HDR10, the game's absolute nits in PQ. The layer shows it with EDR, and its metadata maps the game's UI
// brightness (Display > HDR UI Brightness) to the display's SDR white: its UI then sits at the brightness of other
// windows, and its scene and highlights reach up to HDR Peak Brightness, tone-mapped by macOS to what the display has
// to spare. The game sends the peak with vkSetHdrMetadataEXT and keeps UI brightness to itself, so the hooks pass both
// in (ksaMtlHdrNits) whenever the game applies its Display settings.
namespace {
struct HdrState {
    std::mutex m;  // all of it
    float uiBrightness = 200, peak = 1000, min = 0;  // the game's defaults, until it or the hooks say
    CAMetalLayer* layer = nil;  // the HDR layer, which the metadata goes to: KSA has one HDR window
};
HdrState& g_hdr = *new HdrState;
}  // namespace

bool kmtlEdrAvailable(CAMetalLayer* layer) {
    // The display of the window the layer is in (AppKit calls: the game makes surfaces and swapchains on its main
    // thread); else any display
    for (NSWindow* window in NSApp.windows) {
        if (window.contentView.layer == layer && window.screen)
            return window.screen.maximumPotentialExtendedDynamicRangeColorComponentValue > 1;
    }
    for (NSScreen* screen in NSScreen.screens) {
        if (screen.maximumPotentialExtendedDynamicRangeColorComponentValue > 1) return true;
    }
    return false;
}

static void applyHdrMetadata() {  // g_hdr.m held
    if (!g_hdr.layer) return;
    g_hdr.layer.EDRMetadata = [CAEDRMetadata HDR10MetadataWithMinLuminance:g_hdr.min maxLuminance:g_hdr.peak
                                                       opticalOutputScale:g_hdr.uiBrightness];
}

void kmtlSetHdr(KSurface* surface, bool hdr) {
    CAMetalLayer* layer = surface->layer;
    std::lock_guard<std::mutex> lock(g_hdr.m);
    if (hdr && !surface->hdr) surface->sdrColorspace = CGColorSpaceRetain(layer.colorspace);
    if (hdr) {
        CGColorSpaceRef pq = CGColorSpaceCreateWithName(kCGColorSpaceITUR_2100_PQ);
        layer.colorspace = pq;
        CGColorSpaceRelease(pq);
        layer.wantsExtendedDynamicRangeContent = YES;
        g_hdr.layer = layer;
        applyHdrMetadata();
    } else if (surface->hdr) {
        layer.colorspace = surface->sdrColorspace;
        CGColorSpaceRelease(surface->sdrColorspace);
        surface->sdrColorspace = nullptr;
        layer.wantsExtendedDynamicRangeContent = NO;
        layer.EDRMetadata = nil;
        if (g_hdr.layer == layer) g_hdr.layer = nil;
    }
    surface->hdr = hdr;
}

float kmtlHdrUiBrightness() {
    std::lock_guard<std::mutex> lock(g_hdr.m);
    return g_hdr.uiBrightness;
}

KMTL_EXPORT void ksaMtlHdrNits(float uiBrightness, float peak) {
    KTRACE_FIRST();
    if (uiBrightness <= 0 || peak <= 0) return;
    std::lock_guard<std::mutex> lock(g_hdr.m);
    if (g_hdr.uiBrightness == uiBrightness && g_hdr.peak == peak) return;
    g_hdr.uiBrightness = uiBrightness;
    g_hdr.peak = peak;
    applyHdrMetadata();
    KLOG(1, "HDR UI brightness %.0f nits, peak %.0f nits", uiBrightness, peak);
}

VKFN(void) vkSetHdrMetadataEXT(VkDevice, uint32_t count, const VkSwapchainKHR*, const VkHdrMetadataEXT* metadata) {
    KTRACE_FIRST();
    if (!count) return;
    std::lock_guard<std::mutex> lock(g_hdr.m);
    if (metadata[0].maxLuminance > 0) g_hdr.peak = metadata[0].maxLuminance;
    g_hdr.min = std::max(metadata[0].minLuminance, 0.0f);
    applyHdrMetadata();
}
