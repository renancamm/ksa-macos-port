// MetalFX upscaling for the hooks' render scale (src/hooks/), behind private entry points the patched
// game calls: vkCmdUpscaleKSA between its scene and its final composite, vkCmdUpscaleSnapshotKSA before its
// translucent effects. KSA Metal makes the motion vectors itself from the depth buffer and the matrices the game
// passes: the scene's motion through the nearby body's frame, and inside a bounding sphere the motion of the vessel
// the camera follows, through its own frame. What the translucent effects and the bloom changed since the snapshot
// becomes MetalFX's reactive mask, so that plumes moving with the camera don't smear. Past the temporal upscaler's
// largest scale (3x on an M5), it upscales as far as it can and MetalFX's spatial upscaler does the rest. The same
// file has the present's MetalFX pass: frame interpolation (KSA_MTL_FX_INTERPOLATE), for which the game marks where
// its UI starts (vkCmdMarkUiKSA).
#import <MetalFX/MetalFX.h>
#include "kmtl.h"

extern "C" {
struct VkUpscaleInfoKSA {
    VkImage color;           // render size, linear HDR
    VkImage depth;           // render size, D32, reversed (0 = far)
    VkImage output;          // output size, color format, storage usage
    uint32_t inputWidth, inputHeight;
    float jitterX, jitterY;  // pixels the projection moved the image by (y down)
    // Row-vector, row-major matrices. Ego = camera-centred position, ecliptic axes.
    float egoFromClip[16];      // (ndc.xy, depth, 1) * M = homogeneous ego position (inverse jittered view-projection)
    float prevClipFromEgo[16];  // ego * M = the previous frame's unjittered clip position, for the scene
    float prevClipFromEgoObject[16];  // the same for the followed object
    float objectSphere[4];      // its bounding sphere in ego (radius 0: none)
    float fovY, nearPlane, farPlane, deltaTime;  // for frame interpolation
    uint32_t flags;          // kUpscaleReset, kUpscaleSpatial
    uint32_t pad;
};
}
enum : uint32_t { kUpscaleReset = 1, kUpscaleSpatial = 2 };
// How strongly a colour change since the snapshot marks a pixel reactive
static const float kReactiveGain = 2.f;

static const char* kMotionSource = R"(
#include <metal_stdlib>
using namespace metal;
struct Params { float4x4 egoFromClip, scene, object; float4 sphere; float2 size; float2 jitter; float reactiveGain; uint hasSnapshot; };
kernel void motion(depth2d<float, access::read> depth [[texture(0)]], texture2d<half, access::write> mv [[texture(1)]],
                   texture2d<half, access::read> color [[texture(2)]], texture2d<half, access::read> snapshot [[texture(3)]],
                   texture2d<half, access::write> reactive [[texture(4)]],
                   constant Params& p [[buffer(0)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= uint(p.size.x) || gid.y >= uint(p.size.y)) return;
    float d = depth.read(gid);
    float2 pix = float2(gid) + 0.5;
    float4 ego = p.egoFromClip * float4(pix / p.size * 2.0 - 1.0, d, 1.0);
    bool onObject = p.sphere.w > 0 && abs(ego.w) > 1e-20 && distance(ego.xyz / ego.w, p.sphere.xyz) < p.sphere.w;
    float4 prev = (onObject ? p.object : p.scene) * ego;
    float2 prevPix = (prev.xy / prev.w * 0.5 + 0.5) * p.size;
    float2 v = prevPix - (pix - p.jitter);
    if (!all(isfinite(v))) v = 0;
    mv.write(half4(half2(clamp(v, -16384.0, 16384.0)), 0, 0), gid);
    float r = 0;
    if (p.hasSnapshot != 0) {
        float3 c = float3(color.read(gid).rgb), s = float3(snapshot.read(gid).rgb);
        float change = length(c - s) / (max(dot(c, float3(0.2126, 0.7152, 0.0722)), 0.0) + 0.05);
        r = saturate((change - 0.05) * p.reactiveGain);
    }
    reactive.write(half4(r), gid);
}
// KSA_MTL_FX_DEBUG=1: the output shows the motion vectors (red |x|, green |y|, 32 px = full) and the object mask (blue)
kernel void debugView(depth2d<float, access::read> depth [[texture(0)]], texture2d<half, access::read> mv [[texture(1)]],
                      texture2d<half, access::write> out [[texture(2)]], constant Params& p [[buffer(0)]],
                      uint2 gid [[thread_position_in_grid]]) {
    uint2 src = uint2(float2(gid) * p.size / float2(out.get_width(), out.get_height()));
    float2 v = float2(mv.read(src).xy);
    float d = depth.read(src);
    float4 ego = p.egoFromClip * float4((float2(src) + 0.5) / p.size * 2.0 - 1.0, d, 1.0);
    bool onObject = p.sphere.w > 0 && abs(ego.w) > 1e-20 && distance(ego.xyz / ego.w, p.sphere.xyz) < p.sphere.w;
    out.write(half4(half(saturate(abs(v.x) / 32)), half(saturate(abs(v.y) / 32)), onObject ? 0.6h : 0.0h, 1), gid);
}
)";

namespace {
struct UpscaleState {
    std::mutex m;
    id<MTLComputePipelineState> motion = nil;
    id<MTLComputePipelineState> debugView = nil;
    id<MTLFXTemporalScaler> temporal = nil;
    id<MTLFXSpatialScaler> spatial = nil;
    uint32_t inW = 0, inH = 0, outW = 0, outH = 0;
    // Past the temporal upscaler's largest scale: its output, which the spatial upscaler scales the rest of the way
    id<MTLFXSpatialScaler> rest = nil;
    id<MTLTexture> restTex = nil;
    id<MTLTexture> motionTex = nil;
    id<MTLTexture> reactiveTex = nil;
    id<MTLTexture> snapshotTex = nil;
    // KSA_MTL_FX_INTERPOLATE=1: the last upscale's inputs, for a frame interpolated at present
    id<MTLFXFrameInterpolator> interpolator = nil;
    id<MTLTexture> frameDepth = nil, interpolated = nil;
    // vkCmdMarkUiKSA: the scene without the UI, this frame's and the last one's
    id<MTLTexture> scene = nil, prevScene = nil;
    uint64_t sceneSerial = 0;
    bool prevSceneValid = false;  // prevScene holds the frame before (no gap, no new interpolator)
    float frameJitter[2] = {0, 0}, fovY = 1, nearPlane = 0.1f, farPlane = 1e11f, deltaTime = 0.016f;
    uint64_t frameSerial = 0, presentedSerial = 0;
    // The barriers around the interpolator in the present's command buffer, and its KSA_MTL_FX_SHOTS count
    id<MTLEvent> presentEvent = nil;
    uint64_t presentEventValue = 0, fxShotCount = 0;
    bool frameReset = true;
    bool failed = false;
};
UpscaleState& g_up = *new UpscaleState;  // never destroyed: releasing a scaler at exit aborts in MetalFX's MPSGraph teardown
}

static bool prepare(KDevice* dev, uint32_t inW, uint32_t inH, uint32_t outW, uint32_t outH, bool spatial) {
    if (g_up.failed) return false;
    id<MTLDevice> d = dev->mtl;
    if (!g_up.motion) {
        NSError* err = nil;
        id<MTLLibrary> lib = [d newLibraryWithSource:[NSString stringWithUTF8String:kMotionSource] options:nil error:&err];
        id<MTLFunction> f = [lib newFunctionWithName:@"motion"];
        g_up.motion = f ? [d newComputePipelineStateWithFunction:f error:&err] : nil;
        id<MTLFunction> dv = [lib newFunctionWithName:@"debugView"];
        g_up.debugView = dv ? [d newComputePipelineStateWithFunction:dv error:&err] : nil;
        if (!g_up.motion) {
            KERR("upscale: motion kernel: %s", err.localizedDescription.UTF8String);
            g_up.failed = true;
            return false;
        }
    }
    if (g_up.inW == inW && g_up.inH == inH && g_up.outW == outW && g_up.outH == outH && (spatial ? g_up.spatial != nil : g_up.temporal != nil))
        return true;
    g_up.inW = inW; g_up.inH = inH; g_up.outW = outW; g_up.outH = outH;
    g_up.temporal = nil;
    g_up.spatial = nil;
    g_up.rest = nil;
    g_up.restTex = nil;
    if (spatial) {
        MTLFXSpatialScalerDescriptor* sd = [MTLFXSpatialScalerDescriptor new];
        sd.colorTextureFormat = MTLPixelFormatRGBA16Float;
        sd.outputTextureFormat = MTLPixelFormatRGBA16Float;
        sd.inputWidth = inW; sd.inputHeight = inH; sd.outputWidth = outW; sd.outputHeight = outH;
        sd.colorProcessingMode = MTLFXSpatialScalerColorProcessingModeHDR;
        g_up.spatial = [sd newSpatialScalerWithDevice:d];
    } else {
        MTLFXTemporalScalerDescriptor* td = [MTLFXTemporalScalerDescriptor new];
        td.colorTextureFormat = MTLPixelFormatRGBA16Float;
        td.depthTextureFormat = MTLPixelFormatDepth32Float;
        td.motionTextureFormat = MTLPixelFormatRG16Float;
        td.outputTextureFormat = MTLPixelFormatRGBA16Float;
        float most = [MTLFXTemporalScalerDescriptor supportedInputContentMaxScaleForDevice:d];
        uint32_t midW = std::min(outW, (uint32_t)(inW * most)), midH = std::min(outH, (uint32_t)(inH * most));
        td.inputWidth = inW; td.inputHeight = inH; td.outputWidth = midW; td.outputHeight = midH;
        td.autoExposureEnabled = YES;
        td.requiresSynchronousInitialization = YES;
        td.reactiveMaskTextureEnabled = YES;
        td.reactiveMaskTextureFormat = MTLPixelFormatR8Unorm;
        g_up.temporal = [td newTemporalScalerWithDevice:d];
        MTLTextureDescriptor* md = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRG16Float
                                                                                      width:inW height:inH mipmapped:NO];
        md.usage = MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite;
        md.storageMode = MTLStorageModePrivate;
        g_up.motionTex = [d newTextureWithDescriptor:md];
        md.pixelFormat = MTLPixelFormatR8Unorm;
        g_up.reactiveTex = [d newTextureWithDescriptor:md];
        if (g_up.temporal && (midW != outW || midH != outH)) {
            MTLFXSpatialScalerDescriptor* sd = [MTLFXSpatialScalerDescriptor new];
            sd.colorTextureFormat = MTLPixelFormatRGBA16Float;
            sd.outputTextureFormat = MTLPixelFormatRGBA16Float;
            sd.inputWidth = midW; sd.inputHeight = midH; sd.outputWidth = outW; sd.outputHeight = outH;
            sd.colorProcessingMode = MTLFXSpatialScalerColorProcessingModeHDR;
            g_up.rest = [sd newSpatialScalerWithDevice:d];
            MTLTextureDescriptor* rd = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA16Float
                                                                                          width:midW height:midH mipmapped:NO];
            rd.usage = g_up.temporal.outputTextureUsage | MTLTextureUsageShaderRead;
            rd.storageMode = MTLStorageModePrivate;
            g_up.restTex = [d newTextureWithDescriptor:rd];
            if (!g_up.rest || !g_up.restTex) g_up.temporal = nil;
        }
        if (g_up.temporal)
            KLOG(0, "upscale: MetalFX temporal %ux%u -> %ux%u%s", inW, inH, midW, midH,
                 g_up.rest ? [NSString stringWithFormat:@", spatial -> %ux%u", outW, outH].UTF8String : "");
    }
    if (!(spatial ? (id)g_up.spatial : (id)g_up.temporal)) {
        KERR("upscale: MetalFX %s scaler %ux%u -> %ux%u not created", spatial ? "spatial" : "temporal", inW, inH, outW, outH);
        g_up.failed = true;
        return false;
    }
    return true;
}

// Everything encoded before it finishes before anything encoded after it starts: a signal and a wait on an event
static void eventBarrier(id<MTLCommandBuffer> cb, id<MTLEvent> event, uint64_t* value) {
    [cb encodeSignalEvent:event value:++*value];
    [cb encodeWaitForEvent:event value:*value];
}

// Everything before the upscaler finishes before it starts, and it finishes before anything after it: the game's
// images are untracked, and MetalFX's own encoders can't wait on KSA Metal's fences
static void fullBarrier(KCommandBuffer* cb) {
    if (!cb->fxEvent) cb->fxEvent = [cb->device->mtl newEvent];
    eventBarrier(cb->mtl, cb->fxEvent, &cb->fxValue);
}

VKFN(void) vkCmdUpscaleKSA(VkCommandBuffer commandBuffer, const VkUpscaleInfoKSA* info) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* color = (KImage*)info->color;
    auto* depth = (KImage*)info->depth;
    auto* out = (KImage*)info->output;
    if (!color || !out || cb->inRenderPass) return;
    bool spatial = (info->flags & kUpscaleSpatial) || !depth;
    uint32_t outW = (uint32_t)out->mtl.width, outH = (uint32_t)out->mtl.height;
    std::lock_guard<std::mutex> lock(g_up.m);
    if (!prepare(cb->device, info->inputWidth, info->inputHeight, outW, outH, spatial)) return;
    cb->endEncoder();
    cb->waitForAllResolves();
    fullBarrier(cb);
    if (spatial) {
        g_up.spatial.colorTexture = color->mtl;
        g_up.spatial.outputTexture = out->mtl;
        g_up.spatial.inputContentWidth = info->inputWidth;
        g_up.spatial.inputContentHeight = info->inputHeight;
        [g_up.spatial encodeToCommandBuffer:cb->mtl];
    } else {
        struct { float egoFromClip[16], scene[16], object[16], sphere[4]; float size[2]; float jitter[2]; float reactiveGain; uint32_t hasSnapshot; float pad[2]; } p;
        memcpy(p.egoFromClip, info->egoFromClip, sizeof(p.egoFromClip));
        memcpy(p.scene, info->prevClipFromEgo, sizeof(p.scene));
        memcpy(p.object, info->prevClipFromEgoObject, sizeof(p.object));
        memcpy(p.sphere, info->objectSphere, sizeof(p.sphere));
        p.size[0] = (float)info->inputWidth; p.size[1] = (float)info->inputHeight;
        p.jitter[0] = info->jitterX; p.jitter[1] = info->jitterY;
        id<MTLComputeCommandEncoder> e = [cb->mtl computeCommandEncoder];
        e.label = @"upscale motion vectors";
        [e setComputePipelineState:g_up.motion];
        [e setTexture:depth->mtl atIndex:0];
        [e setTexture:g_up.motionTex atIndex:1];
        [e setTexture:color->mtl atIndex:2];
        bool snap = cb->fxSnapshot && g_up.snapshotTex && g_up.snapshotTex.width == color->mtl.width &&
                    g_up.snapshotTex.height == color->mtl.height;
        [e setTexture:snap ? g_up.snapshotTex : color->mtl atIndex:3];
        [e setTexture:g_up.reactiveTex atIndex:4];
        p.reactiveGain = kReactiveGain;
        p.hasSnapshot = snap ? 1 : 0;
        [e setBytes:&p length:sizeof(p) atIndex:0];
        [e dispatchThreads:MTLSizeMake(info->inputWidth, info->inputHeight, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
        [e endEncoding];
        fullBarrier(cb);
        if (kmtlConfig().fxDebug && g_up.debugView) {
            id<MTLComputeCommandEncoder> e2 = [cb->mtl computeCommandEncoder];
            [e2 setComputePipelineState:g_up.debugView];
            [e2 setTexture:depth->mtl atIndex:0];
            [e2 setTexture:g_up.motionTex atIndex:1];
            [e2 setTexture:out->mtl atIndex:2];
            [e2 setBytes:&p length:sizeof(p) atIndex:0];
            [e2 dispatchThreads:MTLSizeMake(outW, outH, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
            [e2 endEncoding];
            fullBarrier(cb);
            return;
        }
        g_up.temporal.colorTexture = color->mtl;
        g_up.temporal.depthTexture = depth->mtl;
        g_up.temporal.motionTexture = g_up.motionTex;
        g_up.temporal.outputTexture = g_up.rest ? g_up.restTex : out->mtl;
        g_up.temporal.inputContentWidth = info->inputWidth;
        g_up.temporal.inputContentHeight = info->inputHeight;
        g_up.temporal.jitterOffsetX = info->jitterX;
        g_up.temporal.jitterOffsetY = info->jitterY;
        g_up.temporal.motionVectorScaleX = 1.f;
        g_up.temporal.motionVectorScaleY = 1.f;
        g_up.temporal.depthReversed = YES;
        g_up.temporal.reset = (info->flags & kUpscaleReset) != 0;
        g_up.temporal.reactiveMaskTexture = g_up.reactiveTex;
        [g_up.temporal encodeToCommandBuffer:cb->mtl];
        if (g_up.rest) {
            fullBarrier(cb);
            g_up.rest.colorTexture = g_up.restTex;
            g_up.rest.outputTexture = out->mtl;
            g_up.rest.inputContentWidth = g_up.restTex.width;
            g_up.rest.inputContentHeight = g_up.restTex.height;
            [g_up.rest encodeToCommandBuffer:cb->mtl];
        }
        g_up.frameDepth = depth->mtl;
        g_up.frameJitter[0] = info->jitterX; g_up.frameJitter[1] = info->jitterY;
        g_up.fovY = info->fovY; g_up.nearPlane = info->nearPlane; g_up.farPlane = info->farPlane; g_up.deltaTime = info->deltaTime;
        g_up.frameReset = (info->flags & kUpscaleReset) != 0;
        g_up.frameSerial++;
    }
    fullBarrier(cb);
}

VKFN(void) vkCmdUpscaleSnapshotKSA(VkCommandBuffer commandBuffer, VkImage image) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* color = (KImage*)image;
    if (!color || cb->inRenderPass) return;
    std::lock_guard<std::mutex> lock(g_up.m);
    id<MTLTexture> src = color->mtl;
    if (!g_up.snapshotTex || g_up.snapshotTex.width != src.width || g_up.snapshotTex.height != src.height) {
        MTLTextureDescriptor* d = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:src.pixelFormat width:src.width
                                                                                   height:src.height mipmapped:NO];
        d.usage = MTLTextureUsageShaderRead;
        d.storageMode = MTLStorageModePrivate;
        g_up.snapshotTex = [cb->device->mtl newTextureWithDescriptor:d];
    }
    cb->endEncoder();
    cb->waitForAllResolves();
    fullBarrier(cb);
    id<MTLBlitCommandEncoder> b = [cb->mtl blitCommandEncoder];
    b.label = @"upscale snapshot";
    [b copyFromTexture:src toTexture:g_up.snapshotTex];
    [b endEncoding];
    fullBarrier(cb);
    cb->fxSnapshot = true;
}

// How far the temporal scaler scales on this Mac, past which the spatial one does the rest: for the Mac Port tab
VKFN(float) vkGetUpscaleMaxScaleKSA(void) {
    @autoreleasepool {
        id<MTLDevice> d = MTLCreateSystemDefaultDevice();
        return d ? [MTLFXTemporalScalerDescriptor supportedInputContentMaxScaleForDevice:d] : 0.0f;
    }
}

// For frame interpolation: the game calls it inside its final pass, between the composited scene and the UI. The pass
// is suspended (its attachments stored), the colour attachment copied, and the pass resumed.
VKFN(void) vkCmdMarkUiKSA(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!kmtlConfig().fxInterpolate || !cb->inRenderPass || !cb->passDesc) return;
    id<MTLTexture> src = cb->passDesc.colorAttachments[0].texture;
    if (!src || src.sampleCount > 1 || src.storageMode == MTLStorageModeMemoryless) return;
    std::lock_guard<std::mutex> lock(g_up.m);
    if (!g_up.scene || g_up.scene.width != src.width || g_up.scene.height != src.height || g_up.scene.pixelFormat != src.pixelFormat) {
        MTLTextureDescriptor* d = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:src.pixelFormat width:src.width
                                                                                   height:src.height mipmapped:NO];
        d.usage = MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite | MTLTextureUsageRenderTarget;
        d.storageMode = MTLStorageModePrivate;
        g_up.scene = [cb->device->mtl newTextureWithDescriptor:d];
        g_up.prevScene = [cb->device->mtl newTextureWithDescriptor:d];
        g_up.sceneSerial = 0;
    }
    cb->suspendRender();
    fullBarrier(cb);
    id<MTLBlitCommandEncoder> b = [cb->mtl blitCommandEncoder];
    b.label = @"scene before the UI";
    [b copyFromTexture:src toTexture:g_up.scene];
    [b endEncoding];
    fullBarrier(cb);
    cb->resumeRender();
    g_up.sceneSerial = g_up.frameSerial;
}

// KSA_MTL_FX_INTERPOLATE=1: a frame between the last presented one and this one, from the scenes without the UI
// (vkCmdMarkUiKSA) and the motion and depth of this frame's upscale; MetalFX takes this frame's UI from the presented
// image, which has it composited, and draws it on top. Returns the texture to present first, or nil.
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color) {
    std::lock_guard<std::mutex> lock(g_up.m);
    if (g_up.failed || !g_up.temporal || !g_up.frameDepth || g_up.frameSerial == g_up.presentedSerial) return nil;
    g_up.presentedSerial = g_up.frameSerial;
    // Without this frame's scene (no UI mark: a loading screen), no frame in between, and no history for the next
    if (!g_up.scene || g_up.sceneSerial != g_up.frameSerial || g_up.scene.width != color.width ||
        g_up.scene.height != color.height || g_up.scene.pixelFormat != color.pixelFormat) {
        g_up.prevSceneValid = false;
        return nil;
    }
    id<MTLDevice> d = dev->mtl;
    if (!g_up.interpolator || g_up.interpolator.outputWidth != color.width || g_up.interpolator.outputHeight != color.height ||
        g_up.interpolator.inputWidth != g_up.inW || g_up.interpolator.colorTextureFormat != color.pixelFormat) {
        MTLFXFrameInterpolatorDescriptor* fd = [MTLFXFrameInterpolatorDescriptor new];
        fd.colorTextureFormat = color.pixelFormat;
        fd.outputTextureFormat = color.pixelFormat;
        fd.depthTextureFormat = MTLPixelFormatDepth32Float;
        fd.motionTextureFormat = MTLPixelFormatRG16Float;
        fd.uiTextureFormat = color.pixelFormat;
        fd.inputWidth = g_up.inW; fd.inputHeight = g_up.inH;
        fd.outputWidth = color.width; fd.outputHeight = color.height;
        g_up.interpolator = [fd newFrameInterpolatorWithDevice:d];
        MTLTextureDescriptor* td = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:color.pixelFormat width:color.width
                                                                                    height:color.height mipmapped:NO];
        td.storageMode = MTLStorageModePrivate;
        td.usage = MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite | MTLTextureUsageRenderTarget;
        g_up.interpolated = [d newTextureWithDescriptor:td];
        if (!g_up.interpolator) {
            KERR("frame interpolation: interpolator %ux%u -> %lux%lu not created", g_up.inW, g_up.inH, (unsigned long)color.width,
                 (unsigned long)color.height);
            g_up.failed = true;
            return nil;
        }
        KLOG(0, "frame interpolation: %ux%u motion -> %lux%lu frames", g_up.inW, g_up.inH, (unsigned long)color.width,
             (unsigned long)color.height);
        g_up.prevSceneValid = false;
    }
    if (!g_up.presentEvent) g_up.presentEvent = [d newEvent];
    id<MTLTexture> result = nil;
    eventBarrier(cb, g_up.presentEvent, &g_up.presentEventValue);
    if (!g_up.frameReset && g_up.prevSceneValid) {
        id<MTLFXFrameInterpolator> fi = g_up.interpolator;
        fi.colorTexture = g_up.scene;
        fi.prevColorTexture = g_up.prevScene;
        fi.uiTexture = color;
        fi.uiTextureComposited = YES;
        fi.depthTexture = g_up.frameDepth;
        fi.motionTexture = g_up.motionTex;
        fi.motionVectorScaleX = 1.f;
        fi.motionVectorScaleY = 1.f;
        fi.deltaTime = g_up.deltaTime;
        fi.nearPlane = g_up.nearPlane;
        fi.farPlane = g_up.farPlane;
        fi.fieldOfView = g_up.fovY * 57.29578f;
        fi.aspectRatio = (float)color.width / (float)color.height;
        fi.jitterOffsetX = g_up.frameJitter[0];
        fi.jitterOffsetY = g_up.frameJitter[1];
        fi.shouldResetHistory = NO;
        fi.depthReversed = YES;
        fi.outputTexture = g_up.interpolated;
        [fi encodeToCommandBuffer:cb];
        eventBarrier(cb, g_up.presentEvent, &g_up.presentEventValue);
        result = g_up.interpolated;
        // KSA_MTL_FX_SHOTS=<dir>: every 300th interpolated frame, with the scenes around it and this frame as shown
        const char* shotDir = kmtlConfig().fxShots;
        if (shotDir && ++g_up.fxShotCount % 300 == 0) {
            id<MTLTexture> texs[4] = {g_up.prevScene, g_up.interpolated, g_up.scene, color};
            const char* names[4] = {"a_prev_scene", "b_interpolated", "c_scene", "d_shown"};
            for (int i = 0; i < 4; i++)
                kmtlSavePNG(cb, texs[i], nil, std::string(shotDir) + "/fi_" + std::to_string(g_up.fxShotCount) + "_" + names[i] + ".png");
        }
    }
    g_up.prevSceneValid = true;
    id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
    [b copyFromTexture:g_up.scene toTexture:g_up.prevScene];
    [b endEncoding];
    eventBarrier(cb, g_up.presentEvent, &g_up.presentEventValue);
    return result;
}
