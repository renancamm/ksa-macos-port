// MetalFX upscaling for the hooks' render scale (src/game/hooks/), behind private entry points the patched game calls:
// vkCmdUpscaleKSA between its scene and its final composite, vkCmdUpscaleSnapshotKSA before its translucent effects
// and on both sides of its bloom.
// KSA Metal makes the motion vectors itself from the depth buffer and the matrices the game passes: the scene's
// motion through the nearby body's frame up to its highest ground and with the followed vessel's centre above it, the
// sky's by the camera's turn alone, and inside a bounding sphere the motion of the vessel the camera follows, through
// its own frame. Clouds, smoke and flames write no depth: where they are, the game's cloud upscaler's own motion for them
// and the followed vessel's are candidates too, and per pixel the one that best explains the last frame wins. What the
// translucent effects changed before the bloom, and what is drawn over the scene after it
// (orbit lines, the selected part), becomes MetalFX's reactive mask, so that plumes moving with the camera don't smear;
// the bloom itself is left out, or the glow beside a bright edge would keep MetalFX from smoothing the edge. Past the
// temporal upscaler's largest scale (3x on an M5), it upscales as far as it can and MetalFX's spatial upscaler does the
// rest. With dynamic resolution, the temporal upscaler takes each level's content to the whole output; the spatial one
// can't, and a level gets a spatial upscaler of its own size. MetalFX gets the game's exposure, which its tonemapping multiplies the image by, instead of estimating one: in
// dark views its estimate drew faint stars as short streaks and lost a third of them. The same file has the present's
// MetalFX pass: frame interpolation (KSA_MTL_FRAME_INTERPOLATION), for which the game marks where its UI starts
// (vkCmdMarkUiKSA).
#import <MetalFX/MetalFX.h>
#include <cmath>
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
    float prevClipFromEgoSky[16];     // the same for the sky (depth 0)
    float prevClipFromEgoNearby[16];  // the same for what is outside the body's sphere: moving with the followed object
    float objectSphere[4];      // its bounding sphere in ego (radius 0: none)
    float bodySphere[4];        // the nearby body's highest ground or sea and 1 km, in ego (radius 0: no body)
    float fovY, nearPlane, farPlane, deltaTime;  // for frame interpolation
    uint32_t flags;          // kUpscaleReset, kUpscaleSpatial
    float exposure;          // what the game's tonemapping multiplies the image by (Tonemap Exposure; 0: 1)
    // The game's clouds and smoke trails, as its cloud upscaler drew them this frame at the render size (none: 0): their
    // colour with the light that passes them in alpha, and their own motion (uv now minus uv before, the lowest half
    // where it has none)
    VkImage cloudColor, cloudMotion;
    // Dynamic resolution (dynres.mm): the size drawn this frame, within the input size the scaler is made for (0: that)
    uint32_t contentWidth, contentHeight;
};
}
enum : uint32_t { kUpscaleReset = 1, kUpscaleSpatial = 2 };
// How strongly a colour change between two snapshots marks a pixel reactive
static const float kReactiveGain = 2.f;
// Where the game's clouds and smoke hide at least this share of what is behind them, the pixel may move with them
static const float kCloudSeen = 0.05f;

static const char* kMotionSource = R"(
#include <metal_stdlib>
using namespace metal;
struct Params { float4x4 egoFromClip, scene, object, sky, nearby; float4 sphere, body; float2 size; float2 jitter; float reactiveGain; uint snapshots;
                uint clouds; float cloudSeen; float2 lastJitter; uint history; float pad; };
// How far from the followed vessel, in its bounding spheres, its own motion is a candidate for flames and smoke
constant float kNearVessel = 6.0;
// The clouds' motion where they have none of their own: their upscaler writes the lowest half
constant float kNoOwnMotion = -60000.0;
// A candidate motion past this many pixels a frame (or NaN) is none; this close to the one it would replace, the same
constant float kMostMotion = 4096.0, kSameMotion = 0.25;
// Without the last frame, the clouds' own motion where they hide more than this share of what is behind them
constant float kCloudCovers = 0.5;
// The motion texture's range (half floats), which the motion is clamped to
constant float kMotionRange = 16384.0;
static float luma(float3 c) { return max(dot(c, float3(0.2126, 0.7152, 0.0722)), 0.0); }
// How far the 8 pixels around one are from where a motion takes them in the last frame's image: the lower, the better
// that motion explains what changed. The pixel itself is left out, so its own noise doesn't pick
static float misfit(texture2d<half, access::read> color, texture2d<half, access::sample> last, int2 at, float2 lastAt, float2 size) {
    constexpr sampler s(filter::linear, address::clamp_to_edge, coord::pixel);
    float sum = 0;
    for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++) {
            if (x == 0 && y == 0) continue;
            float3 now = float3(color.read(uint2(clamp(at + int2(x, y), int2(0), int2(size) - 1))).rgb);
            float3 was = float3(last.sample(s, lastAt + float2(x, y)).rgb);
            sum += min(length(now - was) / (luma(now) + luma(was) + 0.05), 1.0);
        }
    return sum;
}
// How much of a pixel changed from before to now, as the reactive mask's weight
static float changed(half4 now, half4 before, float gain) {
    float3 c = float3(now.rgb), s = float3(before.rgb);
    float change = length(c - s) / (max(dot(c, float3(0.2126, 0.7152, 0.0722)), 0.0) + 0.05);
    return saturate((change - 0.05) * gain);
}
kernel void motion(depth2d<float, access::read> depth [[texture(0)]], texture2d<half, access::write> mv [[texture(1)]],
                   texture2d<half, access::read> color [[texture(2)]], texture2d<half, access::read> beforeEffects [[texture(3)]],
                   texture2d<half, access::write> reactive [[texture(4)]], texture2d<half, access::read> beforeBloom [[texture(5)]],
                   texture2d<half, access::read> afterBloom [[texture(6)]],
                   texture2d<half, access::read> cloudColor [[texture(7)]], texture2d<half, access::read> cloudMotion [[texture(8)]],
                   texture2d<half, access::sample> last [[texture(9)]],
                   constant Params& p [[buffer(0)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= uint(p.size.x) || gid.y >= uint(p.size.y)) return;
    float d = depth.read(gid);
    float2 pix = float2(gid) + 0.5;
    float4 ego = p.egoFromClip * float4(pix / p.size * 2.0 - 1.0, d, 1.0);
    float3 at = ego.xyz / ego.w;
    bool onObject = p.sphere.w > 0 && abs(ego.w) > 1e-20 && distance(at, p.sphere.xyz) < p.sphere.w;
    bool onBody = p.body.w > 0 && distance(at, p.body.xyz) < p.body.w;
    float4 prev = (d == 0 ? p.sky : onObject ? p.object : onBody ? p.scene : p.nearby) * ego;
    float2 prevPix = (prev.xy / prev.w * 0.5 + 0.5) * p.size;
    float2 v = prevPix - (pix - p.jitter);
    if (!all(isfinite(v))) v = 0;
    float effects = 0, r = 0;
    if (p.snapshots == 7) {
        effects = changed(beforeBloom.read(gid), beforeEffects.read(gid), p.reactiveGain);
        r = max(effects, changed(color.read(gid), afterBloom.read(gid), p.reactiveGain));
    } else if ((p.snapshots & 1) != 0) {
        effects = r = changed(color.read(gid), beforeEffects.read(gid), p.reactiveGain);
    }
    // Clouds, smoke and flames write no depth, so the motion from the depth is that of what is behind them. Where they
    // are, other motions are candidates, and the one that best takes the pixels around to their colour in the last
    // frame's image wins: the clouds' and smoke's own (from this frame's jittered projection to the last frame's plain
    // one), which wins in a descent through the clouds, and near the vessel the camera follows, that vessel's at its own
    // distance along the pixel's ray, for the flames under it (the smoke march draws a rocket's flames, the exhausts
    // other engines'). Without the last frame, the clouds' own where they hide most of what is behind them
    float2 lastAt = pix - p.jitter + p.lastJitter;
    float fit = -1;  // the misfit of v, once needed
    auto consider = [&](float2 c) {
        // Out of range (NaN fails the test too), or the same motion (far clouds against the sky): no need to look
        if (!all(abs(c) < kMostMotion) || distance(c, v) < kSameMotion)
            return;
        if (fit < 0)
            fit = misfit(color, last, int2(gid), lastAt + v, p.size);
        float f = misfit(color, last, int2(gid), lastAt + c, p.size);
        if (f < fit) { fit = f; v = c; }
    };
    bool volume = false;
    if (p.clouds != 0) {
        float seen = 1.0 - float(cloudColor.read(gid).a);
        float2 m = float2(cloudMotion.read(gid).xy);
        volume = seen >= p.cloudSeen;
        if (volume && all(m > kNoOwnMotion)) {
            float2 own = -m * p.size + p.jitter;
            if (p.history != 0)
                consider(own);
            else if (seen > kCloudCovers && all(abs(own) < kMostMotion))
                v = own;
        }
    }
    // Not from inside the vessel's sphere (the cockpit): the point on the ray would be the camera itself
    if (p.history != 0 && p.sphere.w > 0 && length(p.sphere.xyz) > p.sphere.w && (volume || effects > 0.05)) {
        float4 nearPoint = p.egoFromClip * float4(pix / p.size * 2.0 - 1.0, 1.0, 1.0);
        float3 onRay = normalize(nearPoint.xyz / nearPoint.w) * length(p.sphere.xyz);
        if (distance(onRay, p.sphere.xyz) < p.sphere.w * kNearVessel) {
            float4 was = p.object * float4(onRay, 1.0);
            if (was.w > 1e-3)
                consider((was.xy / was.w * 0.5 + 0.5) * p.size - (pix - p.jitter));
        }
    }
    mv.write(half4(half2(clamp(v, -kMotionRange, kMotionRange)), 0, 0), gid);
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
// Where MetalFX can't scale this size: the image stretched, filtered, so that the composite never shows a stale one
kernel void stretch(texture2d<half, access::sample> color [[texture(0)]], texture2d<half, access::write> out [[texture(1)]],
                    constant float2& content [[buffer(0)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= out.get_width() || gid.y >= out.get_height()) return;
    constexpr sampler s(filter::linear, address::clamp_to_edge, coord::pixel);
    float2 at = (float2(gid) + 0.5) / float2(out.get_width(), out.get_height()) * content;
    out.write(color.sample(s, min(at, content - 0.5)), gid);
}
)";

// The motion kernel's parameters, as kMotionSource's Params
struct MotionParams {
    float egoFromClip[16], scene[16], object[16], sky[16], nearby[16], sphere[4], body[4];
    float size[2], jitter[2];
    float reactiveGain;
    uint32_t snapshots, clouds;
    float cloudSeen, lastJitter[2];
    uint32_t history;
    float pad;
};
static_assert(sizeof(MotionParams) == 400, "MotionParams must match kMotionSource's Params");

namespace {
struct UpscaleState {
    std::mutex m;
    id<MTLComputePipelineState> motion = nil;
    id<MTLComputePipelineState> debugView = nil;
    id<MTLComputePipelineState> stretch = nil;
    id<MTLFXTemporalScaler> temporal = nil;
    id<MTLFXSpatialScaler> spatial = nil;
    // Dynamic resolution's levels through the spatial scaler, one made for each level's size (width << 32 | height), as
    // MetalFX's spatial scaler doesn't take a smaller content to the whole output (the temporal one does): it scales by
    // its input and output sizes, and with less content it fills only that share of the output, the rest smeared from
    // the content's edge. Made at a level's first frame, kept until the sizes change (nil: MetalFX made none)
    std::unordered_map<uint64_t, id<MTLFXSpatialScaler>> spatialAt;
    uint32_t inW = 0, inH = 0, outW = 0, outH = 0;
    // The game's image and the output's formats, which the scalers are made for (KSA 2026.11.10 draws its main view in
    // RG11B10Float; a scaler reads a texture of another format as noise)
    MTLPixelFormat colorFormat = MTLPixelFormatInvalid, outFormat = MTLPixelFormatInvalid;
    bool spatialMode = false;
    float minContent = 1;  // dynamic resolution: the least share of the input the content may be
    bool scalerFailed = false;  // MetalFX made no scaler for these sizes and mode: not tried again until they change
    // Past the temporal upscaler's largest scale: its output, which the spatial upscaler scales the rest of the way
    id<MTLFXSpatialScaler> rest = nil;
    id<MTLTexture> restTex = nil;
    id<MTLTexture> motionTex = nil;
    // The last frame's image as MetalFX got it, and its jitter: which motion explains the clouds, smoke and flames best
    id<MTLTexture> lastColor = nil;
    float lastJitter[2] = {0, 0};
    bool haveLast = false;
    uint32_t lastW = 0, lastH = 0;  // the render size it was taken at
    id<MTLTexture> reactiveTex = nil;
    // The game's exposure as MetalFX reads it, 1x1; a new one when the value changes (never written while in use), the
    // one before kept until the next change for frames still in flight
    id<MTLTexture> exposureTex = nil, previousExposureTex = nil;
    float exposure = 0;
    id<MTLTexture> snapshots[3] = {nil, nil, nil};  // vkCmdUpscaleSnapshotKSA's stages: before the effects, before the bloom, after it
    // KSA_MTL_FRAME_INTERPOLATION=1: the last upscale's inputs, for a frame interpolated at present
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
    bool failed = false;  // the kernels didn't compile
    // The input and output MetalFX made no interpolator for (0: none)
    uint64_t interpolatorFailed = 0;
    MTLPixelFormat interpolatorFailedFormat = MTLPixelFormatInvalid;
};
UpscaleState& g_up = *new UpscaleState;  // never destroyed: releasing a scaler at exit aborts in MetalFX's MPSGraph teardown
}

id<MTLTexture> kmtlPrivateTexture(id<MTLDevice> d, MTLPixelFormat format, NSUInteger width, NSUInteger height,
                                  MTLTextureUsage usage) {
    MTLTextureDescriptor* t = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:format width:width height:height
                                                                             mipmapped:NO];
    t.usage = usage;
    t.storageMode = MTLStorageModePrivate;
    return [d newTextureWithDescriptor:t];
}

std::vector<id<MTLComputePipelineState>> kmtlComputePipelines(id<MTLDevice> d, const char* source,
                                                              std::initializer_list<const char*> names, std::string* error) {
    std::vector<id<MTLComputePipelineState>> pipelines;
    NSError* err = nil;
    id<MTLLibrary> lib = [d newLibraryWithSource:[NSString stringWithUTF8String:source] options:nil error:&err];
    if (!lib && error) *error = err.localizedDescription.UTF8String ?: "no library";
    for (const char* name : names) {
        id<MTLFunction> f = lib ? [lib newFunctionWithName:[NSString stringWithUTF8String:name]] : nil;
        NSError* perr = nil;
        id<MTLComputePipelineState> ps = f ? [d newComputePipelineStateWithFunction:f error:&perr] : nil;
        if (!ps && lib && error && error->empty())
            *error = perr && perr.localizedDescription.UTF8String ? perr.localizedDescription.UTF8String
                                                                  : std::string("no function ") + name;
        pipelines.push_back(ps);
    }
    return pipelines;
}

static bool prepare(KDevice* dev, uint32_t inW, uint32_t inH, uint32_t outW, uint32_t outH, bool spatial,
                    MTLPixelFormat colorFormat, MTLPixelFormat outFormat, float minContent) {
    if (g_up.failed) return false;
    id<MTLDevice> d = dev->mtl;
    if (!g_up.motion) {
        std::string err;
        auto kernels = kmtlComputePipelines(d, kMotionSource, {"motion", "debugView", "stretch"}, &err);
        g_up.motion = kernels[0];
        g_up.debugView = kernels[1];
        g_up.stretch = kernels[2];
        if (!g_up.motion) {
            KERR("upscale: motion kernel: %s", err.c_str());
            g_up.failed = true;
            return false;
        }
    }
    if (g_up.inW == inW && g_up.inH == inH && g_up.outW == outW && g_up.outH == outH && g_up.spatialMode == spatial &&
        g_up.colorFormat == colorFormat && g_up.outFormat == outFormat && g_up.minContent == minContent)
        return !g_up.scalerFailed;
    g_up.inW = inW; g_up.inH = inH; g_up.outW = outW; g_up.outH = outH;
    g_up.minContent = minContent;
    g_up.colorFormat = colorFormat; g_up.outFormat = outFormat;
    g_up.spatialMode = spatial;
    g_up.scalerFailed = false;
    g_up.temporal = nil;
    g_up.spatial = nil;
    g_up.spatialAt.clear();
    g_up.rest = nil;
    g_up.restTex = nil;
    if (spatial) {
        MTLFXSpatialScalerDescriptor* sd = [MTLFXSpatialScalerDescriptor new];
        sd.colorTextureFormat = colorFormat;
        sd.outputTextureFormat = outFormat;
        sd.inputWidth = inW; sd.inputHeight = inH; sd.outputWidth = outW; sd.outputHeight = outH;
        sd.colorProcessingMode = MTLFXSpatialScalerColorProcessingModeHDR;
        g_up.spatial = [sd newSpatialScalerWithDevice:d];
    } else {
        MTLFXTemporalScalerDescriptor* td = [MTLFXTemporalScalerDescriptor new];
        float most = [MTLFXTemporalScalerDescriptor supportedInputContentMaxScaleForDevice:d];
        // With dynamic resolution, the content can shrink to minContent of the input: the temporal scaler's output is
        // as far as it scales from there (the lowest level's size rounded as dynres.mm rounds it), and the spatial one
        // takes it the rest of the way
        uint32_t midW = std::min(outW, std::max(inW, kmtlUpscaleReach((uint32_t)std::round(inW * minContent), most)));
        uint32_t midH = std::min(outH, std::max(inH, kmtlUpscaleReach((uint32_t)std::round(inH * minContent), most)));
        bool rest = midW != outW || midH != outH;
        td.colorTextureFormat = colorFormat;
        td.depthTextureFormat = MTLPixelFormatDepth32Float;
        td.motionTextureFormat = MTLPixelFormatRG16Float;
        // Past its largest scale, into an RGBA16Float image the spatial scaler takes the rest of the way
        td.outputTextureFormat = rest ? MTLPixelFormatRGBA16Float : outFormat;
        td.inputWidth = inW; td.inputHeight = inH; td.outputWidth = midW; td.outputHeight = midH;
        if (minContent < 1) {
            td.inputContentPropertiesEnabled = YES;
            td.inputContentMinScale = std::max(1.f, (float)midW / inW);
            // From the lowest level's size rounded down: a level's size may round below inW * minContent
            float least = std::max((float)midW / std::max(1.f, std::floor(inW * minContent)),
                                   (float)midH / std::max(1.f, std::floor(inH * minContent)));
            td.inputContentMaxScale = std::max(td.inputContentMinScale, std::min(most, least));
        }
        // The game's own exposure, passed each frame (exposureTexture): with MetalFX's estimate, in dark views such as
        // stars against space, faint stars came out as short streaks and a third of them dropped out
        td.autoExposureEnabled = NO;
        td.requiresSynchronousInitialization = YES;
        td.reactiveMaskTextureEnabled = YES;
        td.reactiveMaskTextureFormat = MTLPixelFormatR8Unorm;
        g_up.temporal = [td newTemporalScalerWithDevice:d];
        MTLTextureUsage readWrite = MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite;
        g_up.motionTex = kmtlPrivateTexture(d, MTLPixelFormatRG16Float, inW, inH, readWrite);
        g_up.reactiveTex = kmtlPrivateTexture(d, MTLPixelFormatR8Unorm, inW, inH, readWrite);
        if (g_up.temporal && rest) {
            MTLFXSpatialScalerDescriptor* sd = [MTLFXSpatialScalerDescriptor new];
            sd.colorTextureFormat = MTLPixelFormatRGBA16Float;
            sd.outputTextureFormat = outFormat;
            sd.inputWidth = midW; sd.inputHeight = midH; sd.outputWidth = outW; sd.outputHeight = outH;
            sd.colorProcessingMode = MTLFXSpatialScalerColorProcessingModeHDR;
            g_up.rest = [sd newSpatialScalerWithDevice:d];
            g_up.restTex = kmtlPrivateTexture(d, MTLPixelFormatRGBA16Float, midW, midH,
                                              g_up.temporal.outputTextureUsage | MTLTextureUsageShaderRead);
            if (!g_up.rest || !g_up.restTex) g_up.temporal = nil;
        }
        if (g_up.temporal)
            KLOG(0, "upscale: MetalFX temporal %ux%u -> %ux%u%s", inW, inH, midW, midH,
                 g_up.rest ? [NSString stringWithFormat:@", spatial -> %ux%u", outW, outH].UTF8String : "");
    }
    if (!(spatial ? (id)g_up.spatial : (id)g_up.temporal)) {
        KERR("upscale: MetalFX %s scaler %ux%u -> %ux%u not created, the image is stretched instead", spatial ? "spatial" : "temporal",
             inW, inH, outW, outH);
        g_up.scalerFailed = true;
        return false;
    }
    return true;
}

// The spatial scaler for a level's content (g_up.m held): the whole input's, or one made for that size
static id<MTLFXSpatialScaler> spatialFor(id<MTLDevice> d, uint32_t cw, uint32_t ch) {
    if (cw == g_up.inW && ch == g_up.inH) return g_up.spatial;
    uint64_t key = (uint64_t)cw << 32 | ch;
    auto it = g_up.spatialAt.find(key);
    if (it != g_up.spatialAt.end()) return it->second;
    auto t0 = std::chrono::steady_clock::now();
    MTLFXSpatialScalerDescriptor* sd = [MTLFXSpatialScalerDescriptor new];
    sd.colorTextureFormat = g_up.colorFormat;
    sd.outputTextureFormat = g_up.outFormat;
    sd.inputWidth = cw; sd.inputHeight = ch; sd.outputWidth = g_up.outW; sd.outputHeight = g_up.outH;
    sd.colorProcessingMode = MTLFXSpatialScalerColorProcessingModeHDR;
    id<MTLFXSpatialScaler> scaler = [sd newSpatialScalerWithDevice:d];
    g_up.spatialAt[key] = scaler;
    if (scaler)
        KLOG(1, "upscale: MetalFX spatial %ux%u -> %ux%u for a level, made in %.1f ms", cw, ch, g_up.outW, g_up.outH,
             std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    else
        KERR("upscale: MetalFX spatial scaler %ux%u -> %ux%u not created, that level is stretched instead", cw, ch,
             g_up.outW, g_up.outH);
    return scaler;
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
    // The encoders around it are MetalFX's own: a timestamp after them waits through its own event (stampTimestamp)
    cb->stampedLast = false;
}

// This frame's content in the input size the scalers are made for (dynamic resolution: the level's size), within the
// textures it is read from (the motion kernel reads the depth at the colour's pixels)
static void contentSize(const VkUpscaleInfoKSA* info, const KImage* color, const KImage* depth, bool spatial, uint32_t* cw,
                        uint32_t* ch) {
    *cw = info->contentWidth ? std::min(info->contentWidth, info->inputWidth) : info->inputWidth;
    *ch = info->contentHeight ? std::min(info->contentHeight, info->inputHeight) : info->inputHeight;
    *cw = std::min<uint32_t>(*cw, (uint32_t)color->mtl.width);
    *ch = std::min<uint32_t>(*ch, (uint32_t)color->mtl.height);
    if (!spatial) {
        *cw = std::min<uint32_t>(*cw, (uint32_t)depth->mtl.width);
        *ch = std::min<uint32_t>(*ch, (uint32_t)depth->mtl.height);
    }
}

// Where MetalFX can't scale this frame: the content stretched to the output, filtered (g_up.m held)
static void encodeStretch(KCommandBuffer* cb, id<MTLTexture> color, id<MTLTexture> out, uint32_t cw, uint32_t ch) {
    float content[2] = {(float)cw, (float)ch};
    id<MTLComputeCommandEncoder> e = [cb->mtl computeCommandEncoder];
    e.label = @"upscale stretch";
    [e setComputePipelineState:g_up.stretch];
    [e setTexture:color atIndex:0];
    [e setTexture:out atIndex:1];
    [e setBytes:content length:sizeof(content) atIndex:0];
    [e dispatchThreads:MTLSizeMake(out.width, out.height, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
    [e endEncoding];
}

static void encodeSpatial(KCommandBuffer* cb, id<MTLFXSpatialScaler> scaler, id<MTLTexture> color, id<MTLTexture> out,
                          uint32_t cw, uint32_t ch) {
    scaler.colorTexture = color;
    scaler.outputTexture = out;
    scaler.inputContentWidth = cw;
    scaler.inputContentHeight = ch;
    [scaler encodeToCommandBuffer:cb->mtl];
}

// MetalFX Temporal's motion vectors and reactive mask (g_up.m held): the motion kernel over the content, with the
// snapshots taken this frame, the clouds' own colour and motion where they are this frame's at this size, and the last
// frame's image where it is the frame just before at this size. Its parameters, for the debug view; clouds: whether the
// clouds' were used
static MotionParams encodeMotion(KCommandBuffer* cb, const VkUpscaleInfoKSA* info, KImage* color, KImage* depth,
                                 uint32_t cw, uint32_t ch, bool haveLast, bool* clouds) {
    MotionParams p;
    memcpy(p.egoFromClip, info->egoFromClip, sizeof(p.egoFromClip));
    memcpy(p.scene, info->prevClipFromEgo, sizeof(p.scene));
    memcpy(p.object, info->prevClipFromEgoObject, sizeof(p.object));
    memcpy(p.sky, info->prevClipFromEgoSky, sizeof(p.sky));
    memcpy(p.nearby, info->prevClipFromEgoNearby, sizeof(p.nearby));
    memcpy(p.body, info->bodySphere, sizeof(p.body));
    memcpy(p.sphere, info->objectSphere, sizeof(p.sphere));
    p.size[0] = (float)cw; p.size[1] = (float)ch;
    p.jitter[0] = info->jitterX; p.jitter[1] = info->jitterY;
    id<MTLComputeCommandEncoder> e = [cb->mtl computeCommandEncoder];
    e.label = @"upscale motion vectors";
    [e setComputePipelineState:g_up.motion];
    [e setTexture:depth->mtl atIndex:0];
    [e setTexture:g_up.motionTex atIndex:1];
    [e setTexture:color->mtl atIndex:2];
    uint32_t snaps = 0;
    for (uint32_t i = 0; i < 3; i++) {
        id<MTLTexture> s = g_up.snapshots[i];
        bool taken = (cb->fxSnapshots & 1u << i) && s && s.width == color->mtl.width && s.height == color->mtl.height;
        if (taken) snaps |= 1u << i;
        [e setTexture:taken ? s : color->mtl atIndex:i == 0 ? 3 : 4 + i];
    }
    [e setTexture:g_up.reactiveTex atIndex:4];
    auto* cloudColor = (KImage*)info->cloudColor;
    auto* cloudMotion = (KImage*)info->cloudMotion;
    *clouds = cloudColor && cloudMotion && cloudColor->mtl.width == color->mtl.width &&
              cloudColor->mtl.height == color->mtl.height && cloudMotion->mtl.width == color->mtl.width &&
              cloudMotion->mtl.height == color->mtl.height;
    [e setTexture:*clouds ? cloudColor->mtl : color->mtl atIndex:7];
    [e setTexture:*clouds ? cloudMotion->mtl : color->mtl atIndex:8];
    if (!g_up.lastColor || g_up.lastColor.width != color->mtl.width || g_up.lastColor.height != color->mtl.height ||
        g_up.lastColor.pixelFormat != color->mtl.pixelFormat) {
        g_up.lastColor = kmtlPrivateTexture(cb->device->mtl, color->mtl.pixelFormat, color->mtl.width, color->mtl.height,
                                            MTLTextureUsageShaderRead);
        haveLast = false;
    }
    bool history = haveLast && g_up.lastW == cw && g_up.lastH == ch && !(info->flags & kUpscaleReset);
    [e setTexture:history ? g_up.lastColor : color->mtl atIndex:9];
    p.reactiveGain = kReactiveGain;
    p.snapshots = snaps;
    p.clouds = *clouds ? 1 : 0;
    p.cloudSeen = kCloudSeen;
    p.lastJitter[0] = g_up.lastJitter[0]; p.lastJitter[1] = g_up.lastJitter[1];
    p.history = history ? 1 : 0;
    p.pad = 0;
    [e setBytes:&p length:sizeof(p) atIndex:0];
    [e dispatchThreads:MTLSizeMake(cw, ch, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
    [e endEncoding];
    return p;
}

// This frame's image for the next frame's motion candidates (g_up.m held), only when one can use it: clouds or smoke
// drawn, or a vessel followed
static void keepLastImage(KCommandBuffer* cb, const VkUpscaleInfoKSA* info, id<MTLTexture> color, bool clouds,
                          uint32_t cw, uint32_t ch) {
    g_up.haveLast = g_up.lastColor && (clouds || info->objectSphere[3] > 0);
    if (!g_up.haveLast) return;
    id<MTLBlitCommandEncoder> b = [cb->mtl blitCommandEncoder];
    b.label = @"upscale last image";
    [b copyFromTexture:color toTexture:g_up.lastColor];
    [b endEncoding];
    fullBarrier(cb);
    g_up.lastJitter[0] = info->jitterX; g_up.lastJitter[1] = info->jitterY;
    g_up.lastW = cw; g_up.lastH = ch;
}

// KSA_MTL_FX_DEBUG=1: the motion vectors and the object mask in the output instead of the image
static void encodeDebugView(KCommandBuffer* cb, id<MTLTexture> depth, id<MTLTexture> out, const MotionParams& p) {
    id<MTLComputeCommandEncoder> e = [cb->mtl computeCommandEncoder];
    [e setComputePipelineState:g_up.debugView];
    [e setTexture:depth atIndex:0];
    [e setTexture:g_up.motionTex atIndex:1];
    [e setTexture:out atIndex:2];
    [e setBytes:&p length:sizeof(p) atIndex:0];
    [e dispatchThreads:MTLSizeMake(out.width, out.height, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
    [e endEncoding];
}

// The game's exposure as MetalFX reads it (g_up.m held): a 1x1 texture, a new one when the value changes, as one may
// still be read by frames in flight
static id<MTLTexture> exposureTexture(id<MTLDevice> d, float value) {
    float exposure = value > 0 && std::isfinite(value) ? value : 1.f;
    if (!g_up.exposureTex || exposure != g_up.exposure) {
        MTLTextureDescriptor* ed = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:MTLPixelFormatR16Float
                                                                                      width:1 height:1 mipmapped:NO];
        ed.usage = MTLTextureUsageShaderRead;
        ed.storageMode = MTLStorageModeShared;
        id<MTLTexture> t = [d newTextureWithDescriptor:ed];
        __fp16 v = (__fp16)exposure;
        [t replaceRegion:MTLRegionMake2D(0, 0, 1, 1) mipmapLevel:0 withBytes:&v bytesPerRow:sizeof(v)];
        g_up.previousExposureTex = g_up.exposureTex;
        g_up.exposureTex = t;
        g_up.exposure = exposure;
    }
    return g_up.exposureTex;
}

// MetalFX Temporal, and past its largest scale the spatial scaler the rest of the way (g_up.m held); the frame's inputs
// kept for frame interpolation at present
static void encodeTemporal(KCommandBuffer* cb, const VkUpscaleInfoKSA* info, id<MTLTexture> color, id<MTLTexture> depth,
                           id<MTLTexture> out, uint32_t cw, uint32_t ch) {
    g_up.temporal.colorTexture = color;
    g_up.temporal.depthTexture = depth;
    g_up.temporal.motionTexture = g_up.motionTex;
    g_up.temporal.outputTexture = g_up.rest ? g_up.restTex : out;
    g_up.temporal.inputContentWidth = cw;
    g_up.temporal.inputContentHeight = ch;
    g_up.temporal.jitterOffsetX = info->jitterX;
    g_up.temporal.jitterOffsetY = info->jitterY;
    g_up.temporal.motionVectorScaleX = 1.f;
    g_up.temporal.motionVectorScaleY = 1.f;
    g_up.temporal.depthReversed = YES;
    g_up.temporal.reset = (info->flags & kUpscaleReset) != 0;
    g_up.temporal.reactiveMaskTexture = g_up.reactiveTex;
    g_up.temporal.exposureTexture = exposureTexture(cb->device->mtl, info->exposure);
    [g_up.temporal encodeToCommandBuffer:cb->mtl];
    if (g_up.rest) {
        fullBarrier(cb);
        g_up.rest.colorTexture = g_up.restTex;
        g_up.rest.outputTexture = out;
        g_up.rest.inputContentWidth = g_up.restTex.width;
        g_up.rest.inputContentHeight = g_up.restTex.height;
        [g_up.rest encodeToCommandBuffer:cb->mtl];
    }
    g_up.frameDepth = depth;
    g_up.frameJitter[0] = info->jitterX; g_up.frameJitter[1] = info->jitterY;
    g_up.fovY = info->fovY; g_up.nearPlane = info->nearPlane; g_up.farPlane = info->farPlane; g_up.deltaTime = info->deltaTime;
    g_up.frameReset = (info->flags & kUpscaleReset) != 0;
    g_up.frameSerial++;
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
    // Read before g_up.m is taken: the controller's lock, g_fr.m, is above it (kmtl.h, "Threads and locks")
    float minContent = kmtlDynamicMinContent();
    std::lock_guard<std::mutex> lock(g_up.m);
    // The last image counts only from the frame just before, through MetalFX Temporal: a stretched or spatial frame
    // in between leaves it older
    bool haveLast = g_up.haveLast;
    g_up.haveLast = false;
    uint32_t cw, ch;
    contentSize(info, color, depth, spatial, &cw, &ch);
    bool ready = prepare(cb->device, info->inputWidth, info->inputHeight, outW, outH, spatial, color->mtl.pixelFormat,
                         out->mtl.pixelFormat, minContent);
    id<MTLFXSpatialScaler> spatialScaler = ready && spatial ? spatialFor(cb->device->mtl, cw, ch) : nil;
    // A level's scaler takes a texture of its size only (a level whose texture wasn't placed keeps the full one): else
    // the stretch, which reads the content as it is
    if (spatial && (!spatialScaler || (spatialScaler != g_up.spatial && (color->mtl.width != cw || color->mtl.height != ch))))
        ready = false;
    if (!ready && !g_up.stretch) return;
    cb->endEncoder();
    cb->waitForAllResolves();
    fullBarrier(cb);
    if (!ready) {
        encodeStretch(cb, color->mtl, out->mtl, cw, ch);
    } else if (spatial) {
        encodeSpatial(cb, spatialScaler, color->mtl, out->mtl, cw, ch);
    } else {
        bool clouds = false;
        MotionParams p = encodeMotion(cb, info, color, depth, cw, ch, haveLast, &clouds);
        fullBarrier(cb);
        keepLastImage(cb, info, color->mtl, clouds, cw, ch);
        if (kmtlConfig().fxDebug && g_up.debugView) {
            encodeDebugView(cb, depth->mtl, out->mtl, p);
            fullBarrier(cb);
            return;
        }
        encodeTemporal(cb, info, color->mtl, depth->mtl, out->mtl, cw, ch);
    }
    fullBarrier(cb);
}

// stage 0: before the translucent effects, 1: before the bloom, 2: after it
VKFN(void) vkCmdUpscaleSnapshotKSA(VkCommandBuffer commandBuffer, VkImage image, uint32_t stage) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* color = (KImage*)image;
    if (!color || cb->inRenderPass || stage > 2) return;
    std::lock_guard<std::mutex> lock(g_up.m);
    id<MTLTexture> src = color->mtl;
    __strong id<MTLTexture>& snapshot = g_up.snapshots[stage];
    if (!snapshot || snapshot.width != src.width || snapshot.height != src.height || snapshot.pixelFormat != src.pixelFormat) {
        snapshot = kmtlPrivateTexture(cb->device->mtl, src.pixelFormat, src.width, src.height, MTLTextureUsageShaderRead);
    }
    cb->endEncoder();
    cb->waitForAllResolves();
    fullBarrier(cb);
    id<MTLBlitCommandEncoder> b = [cb->mtl blitCommandEncoder];
    b.label = @"upscale snapshot";
    [b copyFromTexture:src toTexture:snapshot];
    [b endEncoding];
    fullBarrier(cb);
    cb->fxSnapshots |= 1u << stage;
}

// How far the temporal scaler scales on this Mac (dynamic resolution's lowest level), once
float kmtlUpscaleMaxScale() {
    static float most = [] {
        @autoreleasepool {
            id<MTLDevice> d = MTLCreateSystemDefaultDevice();
            return d ? [MTLFXTemporalScalerDescriptor supportedInputContentMaxScaleForDevice:d] : 0.0f;
        }
    }();
    return most;
}

// The temporal scaler's output from a content size, as far as it scales (whole pixels): prepare()'s, and the lowest
// level framerate.mm picks from the display
uint32_t kmtlUpscaleReach(uint32_t content, float most) {
    return (uint32_t)(content * most);
}

// How far the temporal scaler scales on this Mac, past which the spatial one does the rest: for the Mac Port tab
VKFN(float) vkGetUpscaleMaxScaleKSA(void) {
    return kmtlUpscaleMaxScale();
}

// For frame interpolation: the game calls it inside its final pass, between the composited scene and the UI. The pass
// is suspended (its attachments stored), the colour attachment copied, and the pass resumed.
VKFN(void) vkCmdMarkUiKSA(VkCommandBuffer commandBuffer) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (!kmtlConfig().frameInterpolation || !cb->inRenderPass || !cb->passDesc) return;
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

// KSA_MTL_FRAME_INTERPOLATION=1: a frame between the last presented one and this one, from the scenes without the UI
// (vkCmdMarkUiKSA) and the motion and depth of this frame's upscale; MetalFX takes this frame's UI from the presented
// image, which has it composited, and draws it on top. Returns the texture to present first, or nil.
id<MTLTexture> kmtlPresentInterpolate(KDevice* dev, id<MTLCommandBuffer> cb, id<MTLTexture> color) {
    std::lock_guard<std::mutex> lock(g_up.m);
    if (!g_up.temporal || !g_up.frameDepth || g_up.frameSerial == g_up.presentedSerial) return nil;
    g_up.presentedSerial = g_up.frameSerial;
    // Not at a dynamic resolution's lower level: the interpolator takes no smaller content
    if (g_up.frameDepth.width != g_up.inW || g_up.frameDepth.height != g_up.inH) {
        g_up.prevSceneValid = false;
        return nil;
    }
    // Without this frame's scene (no UI mark: a loading screen), no frame in between, and no history for the next
    if (!g_up.scene || g_up.sceneSerial != g_up.frameSerial || g_up.scene.width != color.width ||
        g_up.scene.height != color.height || g_up.scene.pixelFormat != color.pixelFormat) {
        g_up.prevSceneValid = false;
        return nil;
    }
    id<MTLDevice> d = dev->mtl;
    if (!g_up.interpolator || g_up.interpolator.outputWidth != color.width || g_up.interpolator.outputHeight != color.height ||
        g_up.interpolator.inputWidth != g_up.inW || g_up.interpolator.colorTextureFormat != color.pixelFormat) {
        uint64_t sizes = (uint64_t)g_up.inW << 40 | (uint64_t)color.width << 20 | color.height;
        if (g_up.interpolatorFailed == sizes && g_up.interpolatorFailedFormat == color.pixelFormat) return nil;
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
            g_up.interpolatorFailed = sizes;
            g_up.interpolatorFailedFormat = color.pixelFormat;
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
