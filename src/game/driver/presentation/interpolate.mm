// MetalFX's frame interpolation at present (KSA_MTL_FRAME_INTERPOLATION): a frame between the last presented one and
// this one, from the two scenes without the UI, which the game marks inside its final pass (vkCmdMarkUiKSA), and the
// depth and motion of this frame's upscale, which upscale.mm hands over (kmtlInterpolateTakeFrame).
#import <MetalFX/MetalFX.h>
#include "kmtl.h"

namespace {
struct FrameInterpolationState {
    std::mutex m;
    id<MTLFXFrameInterpolator> interpolator = nil;
    id<MTLTexture> interpolated = nil;
    // The last temporal upscale's inputs (kmtlInterpolateTakeFrame): its depth and motion, the scaler's input size, and
    // the frame's jitter and camera
    id<MTLTexture> frameDepth = nil, motionTex = nil;
    uint32_t inW = 0, inH = 0;
    float frameJitter[2] = {0, 0}, fovY = 1, nearPlane = 0.1f, farPlane = 1e11f, deltaTime = 0.016f;
    bool frameReset = true;
    uint64_t frameSerial = 0, presentedSerial = 0;
    // vkCmdMarkUiKSA: the scene without the UI, this frame's and the last one's
    id<MTLTexture> scene = nil, prevScene = nil;
    uint64_t sceneSerial = 0;
    bool prevSceneValid = false;  // prevScene holds the frame before (no gap, no new interpolator)
    // The barriers around the interpolator in the present's command buffer, and its KSA_MTL_FX_SHOTS count
    id<MTLEvent> presentEvent = nil;
    uint64_t presentEventValue = 0, fxShotCount = 0;
    // The input and output MetalFX made no interpolator for (0: none)
    uint64_t interpolatorFailed = 0;
    MTLPixelFormat interpolatorFailedFormat = MTLPixelFormatInvalid;
};
FrameInterpolationState& g_fi = *new FrameInterpolationState;  // never destroyed, as upscale.mm's g_up
}  // namespace

// The upscale's handover (g_up.m held): this temporal frame's depth and motion, the scaler's input size, its jitter and
// camera
void kmtlInterpolateTakeFrame(id<MTLTexture> depth, id<MTLTexture> motion, uint32_t inW, uint32_t inH,
                              const VkUpscaleInfoKSA* info) {
    std::lock_guard<std::mutex> lock(g_fi.m);
    g_fi.frameDepth = depth;
    g_fi.motionTex = motion;
    g_fi.inW = inW;
    g_fi.inH = inH;
    g_fi.frameJitter[0] = info->jitterX; g_fi.frameJitter[1] = info->jitterY;
    g_fi.fovY = info->fovY; g_fi.nearPlane = info->nearPlane; g_fi.farPlane = info->farPlane; g_fi.deltaTime = info->deltaTime;
    g_fi.frameReset = (info->flags & kUpscaleReset) != 0;
    g_fi.frameSerial++;
}

// For frame interpolation: the game calls it inside its final pass, between the composited scene and the UI. The pass
// is suspended (its attachments stored), the colour attachment copied, and the pass resumed.
VKFN(void) vkCmdMarkUiKSA(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!kmtlConfig().frameInterpolation || !cb->pass.active || !cb->pass.desc) return;
    id<MTLTexture> src = cb->pass.desc.colorAttachments[0].texture;
    if (!src || src.sampleCount > 1 || src.storageMode == MTLStorageModeMemoryless) return;
    std::lock_guard<std::mutex> lock(g_fi.m);
    if (!kmtlTextureIs(g_fi.scene, src.width, src.height, src.pixelFormat)) {
        MTLTextureUsage usage = MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite | MTLTextureUsageRenderTarget;
        g_fi.scene = kmtlPrivateTexture(cb->device->mtl, src.pixelFormat, src.width, src.height, usage);
        g_fi.prevScene = kmtlPrivateTexture(cb->device->mtl, src.pixelFormat, src.width, src.height, usage);
        g_fi.sceneSerial = 0;
    }
    cb->suspendRender();
    kmtlFxBarrier(cb);
    id<MTLBlitCommandEncoder> b = [cb->mtl blitCommandEncoder];
    b.label = @"scene before the UI";
    [b copyFromTexture:src toTexture:g_fi.scene];
    [b endEncoding];
    kmtlFxBarrier(cb);
    cb->resumeRender();
    g_fi.sceneSerial = g_fi.frameSerial;
}

// KSA_MTL_FRAME_INTERPOLATION=1: a frame between the last presented one and this one, from the scenes without the UI
// (vkCmdMarkUiKSA) and the motion and depth of this frame's upscale; MetalFX takes this frame's UI from the presented
// image, which has it composited, and draws it on top. Returns the texture to present first, or nil.
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color) {
    std::lock_guard<std::mutex> lock(g_fi.m);
    if (!g_fi.frameDepth || g_fi.frameSerial == g_fi.presentedSerial) return nil;
    g_fi.presentedSerial = g_fi.frameSerial;
    // Not at a dynamic resolution's lower level: the interpolator takes no smaller content
    if (g_fi.frameDepth.width != g_fi.inW || g_fi.frameDepth.height != g_fi.inH) {
        g_fi.prevSceneValid = false;
        return nil;
    }
    // Without this frame's scene (no UI mark: a loading screen), no frame in between, and no history for the next
    if (g_fi.sceneSerial != g_fi.frameSerial || !kmtlTextureIs(g_fi.scene, color.width, color.height, color.pixelFormat)) {
        g_fi.prevSceneValid = false;
        return nil;
    }
    id<MTLDevice> d = dev->mtl;
    if (!g_fi.interpolator || g_fi.interpolator.outputWidth != color.width || g_fi.interpolator.outputHeight != color.height ||
        g_fi.interpolator.inputWidth != g_fi.inW || g_fi.interpolator.colorTextureFormat != color.pixelFormat) {
        uint64_t sizes = (uint64_t)g_fi.inW << 40 | (uint64_t)color.width << 20 | color.height;
        if (g_fi.interpolatorFailed == sizes && g_fi.interpolatorFailedFormat == color.pixelFormat) return nil;
        MTLFXFrameInterpolatorDescriptor* fd = [MTLFXFrameInterpolatorDescriptor new];
        fd.colorTextureFormat = color.pixelFormat;
        fd.outputTextureFormat = color.pixelFormat;
        fd.depthTextureFormat = MTLPixelFormatDepth32Float;
        fd.motionTextureFormat = MTLPixelFormatRG16Float;
        fd.uiTextureFormat = color.pixelFormat;
        fd.inputWidth = g_fi.inW; fd.inputHeight = g_fi.inH;
        fd.outputWidth = color.width; fd.outputHeight = color.height;
        g_fi.interpolator = [fd newFrameInterpolatorWithDevice:d];
        g_fi.interpolated = kmtlPrivateTexture(d, color.pixelFormat, color.width, color.height,
                                               MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite | MTLTextureUsageRenderTarget);
        if (!g_fi.interpolator) {
            KERR("frame interpolation: interpolator %ux%u -> %lux%lu not created", g_fi.inW, g_fi.inH, (unsigned long)color.width,
                 (unsigned long)color.height);
            g_fi.interpolatorFailed = sizes;
            g_fi.interpolatorFailedFormat = color.pixelFormat;
            return nil;
        }
        KLOG(0, "frame interpolation: %ux%u motion -> %lux%lu frames", g_fi.inW, g_fi.inH, (unsigned long)color.width,
             (unsigned long)color.height);
        g_fi.prevSceneValid = false;
    }
    if (!g_fi.presentEvent) g_fi.presentEvent = [d newEvent];
    id<MTLTexture> result = nil;
    kmtlEventBarrier(cb, g_fi.presentEvent, &g_fi.presentEventValue);
    if (!g_fi.frameReset && g_fi.prevSceneValid) {
        id<MTLFXFrameInterpolator> fi = g_fi.interpolator;
        fi.colorTexture = g_fi.scene;
        fi.prevColorTexture = g_fi.prevScene;
        fi.uiTexture = color;
        fi.uiTextureComposited = YES;
        fi.depthTexture = g_fi.frameDepth;
        fi.motionTexture = g_fi.motionTex;
        fi.motionVectorScaleX = 1.f;
        fi.motionVectorScaleY = 1.f;
        fi.deltaTime = g_fi.deltaTime;
        fi.nearPlane = g_fi.nearPlane;
        fi.farPlane = g_fi.farPlane;
        fi.fieldOfView = g_fi.fovY * 57.29578f;
        fi.aspectRatio = (float)color.width / (float)color.height;
        fi.jitterOffsetX = g_fi.frameJitter[0];
        fi.jitterOffsetY = g_fi.frameJitter[1];
        fi.shouldResetHistory = NO;
        fi.depthReversed = YES;
        fi.outputTexture = g_fi.interpolated;
        [fi encodeToCommandBuffer:cb];
        kmtlEventBarrier(cb, g_fi.presentEvent, &g_fi.presentEventValue);
        result = g_fi.interpolated;
        // KSA_MTL_FX_SHOTS=<dir>: every 300th interpolated frame, with the scenes around it and this frame as shown
        const char* shotDir = kmtlConfig().fxShots;
        if (shotDir && ++g_fi.fxShotCount % 300 == 0) {
            id<MTLTexture> texs[4] = {g_fi.prevScene, g_fi.interpolated, g_fi.scene, color};
            const char* names[4] = {"a_prev_scene", "b_interpolated", "c_scene", "d_shown"};
            for (int i = 0; i < 4; i++)
                kmtlSavePNG(cb, texs[i], nil, std::string(shotDir) + "/fi_" + std::to_string(g_fi.fxShotCount) + "_" + names[i] + ".png");
        }
    }
    g_fi.prevSceneValid = true;
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b copyFromTexture:g_fi.scene toTexture:g_fi.prevScene];
    [b endEncoding];
    kmtlEventBarrier(cb, g_fi.presentEvent, &g_fi.presentEventValue);
    return result;
}
