// A history for the cockpit's ray-traced lighting under MetalFX Temporal, behind the private entry point
// vkCmdRayHistoryKSA (exports.h; the hooks' Upscale.cs). The game traces its lighting images (direct light, indirect
// light, reflections) from the prepass every frame anew, at a fraction of the render size, and smooths them only in
// space; with a seed per jittered frame and the jitter moving where each ray starts, they change from frame to frame
// and MetalFX can't settle on them. Here each image is blended with its own last frames (up to a number of frames per image),
// reprojected through the motion MetalFX gets, kept only where the surface is the same (its normal and its distance in
// the last frame) and held within the spread of this frame's pixels around it, so that what moved or changed doesn't
// trail. The result goes back into the game's image.
#import <Metal/Metal.h>
#include "kmtl.h"

static const char* kHistorySource = R"(
#include <metal_stdlib>
using namespace metal;
struct Params { float4x4 egoFromClip, scene, object, nearby; float4 sphere, body; float2 renderSize, imageSize;
                float2 lastJitter; float maxFrames; uint history; float clip; float pad[3]; };
kernel void accumulate(texture2d<half, access::read> image [[texture(0)]],
                       depth2d<float, access::sample> depth [[texture(1)]],
                       texture2d<half, access::read> normals [[texture(2)]],
                       texture2d<half, access::read> lastColor [[texture(3)]],
                       texture2d<half, access::read> lastGeometry [[texture(4)]],
                       texture2d<half, access::write> outColor [[texture(5)]],
                       texture2d<half, access::write> outGeometry [[texture(6)]],
                       constant Params& p [[buffer(0)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= uint(p.imageSize.x) || gid.y >= uint(p.imageSize.y)) return;
    float2 scale = p.renderSize / p.imageSize;
    // The ray generation's point: the prepass's depth at this pixel's centre (createRayFromPrePass); its normal from the
    // prepass pixel the game's own mapping picks (as it reads it)
    float2 uv = (float2(gid) + 0.5) / p.imageSize;
    constexpr sampler bilinear(filter::linear, address::repeat, coord::normalized);  // the game's prepass sampler
    float d = depth.sample(bilinear, uv);
    int2 at = clamp(int2((float2(gid) + 0.5) * scale - 0.5), int2(0), int2(p.renderSize) - 1);
    float3 n = float3(normals.read(uint2(at)).xyz);
    half4 now = image.read(gid);
    if (all(n == 0) || d == 0) {
        outColor.write(half4(now.rgb, 0), gid);
        outGeometry.write(half4(0), gid);
        return;
    }
    float4 ego = p.egoFromClip * float4(uv * 2.0 - 1.0, d, 1.0);
    float3 pos = ego.xyz / ego.w;
    float viewDepth = 1.0 / ego.w;  // this frame's clip w
    bool onObject = p.sphere.w > 0 && distance(pos, p.sphere.xyz) < p.sphere.w;
    bool onBody = p.body.w > 0 && distance(pos, p.body.xyz) < p.body.w;
    float4 prev = (onObject ? p.object : onBody ? p.scene : p.nearby) * float4(pos, 1.0);
    float3 result = float3(now.rgb);
    float frames = 1;
    if (p.history != 0 && prev.w > 0 && isfinite(prev.w)) {
        // Where the point was in the last frame's image: its plain clip position, moved by the last frame's jitter
        float2 lastPix = ((prev.xy / prev.w * 0.5 + 0.5) * p.renderSize + p.lastJitter) / scale - 0.5;
        int2 base = int2(floor(lastPix));
        float2 f = lastPix - float2(base);
        float3 sum = 0;
        float weight = 0, len = 0;
        for (int y = 0; y <= 1; y++)
            for (int x = 0; x <= 1; x++) {
                int2 tap = base + int2(x, y);
                if (any(tap < 0) || any(tap >= int2(p.imageSize))) continue;
                float4 g = float4(lastGeometry.read(uint2(tap)));
                // The same surface: its normal within ~25 degrees, its distance within 4 %
                if (g.w <= 0 || dot(g.xyz, n) < 0.9 || abs(g.w - prev.w) > 0.04 * prev.w) continue;
                float w = (x ? f.x : 1 - f.x) * (y ? f.y : 1 - f.y);
                half4 c = lastColor.read(uint2(tap));
                sum += float3(c.rgb) * w;
                len += float(c.a) * w;
                weight += w;
            }
        if (weight > 0.05) {
            float3 last = sum / weight;
            if (p.clip > 0) {
                // Within this frame's 3x3 around it, mean +- clip x spread: what no longer is there doesn't trail
                float3 m1 = 0, m2 = 0;
                for (int y = -1; y <= 1; y++)
                    for (int x = -1; x <= 1; x++) {
                        float3 c = float3(image.read(uint2(clamp(int2(gid) + int2(x, y), int2(0), int2(p.imageSize) - 1))).rgb);
                        m1 += c; m2 += c * c;
                    }
                m1 /= 9; m2 /= 9;
                float3 sigma = sqrt(max(m2 - m1 * m1, 0.0));
                last = clamp(last, m1 - p.clip * sigma, m1 + p.clip * sigma);
            }
            frames = min(len / weight + 1, p.maxFrames);
            result = mix(last, result, 1.0 / frames);
        }
    }
    if (!all(isfinite(result))) result = float3(now.rgb);
    outColor.write(half4(half3(result), half(frames)), gid);
    outGeometry.write(half4(half3(n), half(viewDepth)), gid);
}
// The blended image back into the game's (its alpha as the ray generation wrote it), once every pixel has read its
// neighbours
kernel void writeBack(texture2d<half, access::read_write> image [[texture(0)]], texture2d<half, access::read> blended [[texture(1)]],
                      uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= image.get_width() || gid.y >= image.get_height()) return;
    image.write(half4(blended.read(gid).rgb, image.read(gid).a), gid);
}
)";

// The accumulate kernel's parameters, as kHistorySource's Params
struct HistoryParams {
    float egoFromClip[16], scene[16], object[16], nearby[16], sphere[4], body[4];
    float renderSize[2], imageSize[2], lastJitter[2];
    float maxFrames;
    uint32_t history;
    float clip, pad[3];
};
static_assert(sizeof(HistoryParams) == 336, "HistoryParams must match kHistorySource's Params");

namespace {
struct Slot {
    id<MTLTexture> color[2] = {nil, nil}, geometry[2] = {nil, nil};
    uint32_t w = 0, h = 0;
    int current = 0;     // which of the two holds the last frame's
    bool valid = false;  // it holds the frame just before
};
struct HistoryState {
    std::mutex m;
    id<MTLComputePipelineState> accumulate = nil, writeBack = nil;
    bool failed = false;
    Slot slots[4];
    float lastJitter[2] = {0, 0};
    uint32_t lastFrame = 0;
};
HistoryState& g_rh = *new HistoryState;

id<MTLTexture> newTexture(id<MTLDevice> d, MTLPixelFormat f, uint32_t w, uint32_t h) {
    return kmtlPrivateTexture(d, f, w, h, MTLTextureUsageShaderRead | MTLTextureUsageShaderWrite);
}

}  // namespace

VKFN(void) vkCmdRayHistoryKSA(VkCommandBuffer commandBuffer, const VkRayHistoryInfoKSA* info) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    auto* depth = (KImage*)info->depth;
    auto* normals = (KImage*)info->normals;
    if (!depth || !normals || cb->inRenderPass) return;
    // The normals are read at the depth's pixels
    if (normals->mtl.width != depth->mtl.width || normals->mtl.height != depth->mtl.height) {
        KERR_LIMIT(10, "ray history: off this frame, normals %lux%lu against depth %lux%lu", (unsigned long)normals->mtl.width,
                   (unsigned long)normals->mtl.height, (unsigned long)depth->mtl.width, (unsigned long)depth->mtl.height);
        return;
    }
    std::lock_guard<std::mutex> lock(g_rh.m);
    id<MTLDevice> d = cb->device->mtl;
    if (g_rh.failed) return;
    if (!g_rh.accumulate) {
        std::string err;
        auto kernels = kmtlComputePipelines(d, kHistorySource, {"accumulate", "writeBack"}, &err);
        g_rh.accumulate = kernels[0];
        g_rh.writeBack = kernels[1];
        if (!g_rh.accumulate || !g_rh.writeBack) {
            KERR("ray history: kernel: %s", err.c_str());
            g_rh.failed = true;
            return;
        }
        KLOG(0, "ray history: on");
    }
    bool reset = (info->flags & kRayHistoryReset) != 0 || info->frame != g_rh.lastFrame + 1;
    g_rh.lastFrame = info->frame;
    // In the driver's own ordering, as a compute pass after a barrier from everything before (the ray tracing, the
    // game's smoothing, the prepass); the game's barrier after it orders its readers
    cb->barrierClasses(KCommandBuffer::kClassAll, KCommandBuffer::kClassCompute);
    id<MTLComputeCommandEncoder> e = cb->computeEncoder(KCommandBuffer::kClassCompute);
    [e setComputePipelineState:g_rh.accumulate];
    for (int i = 0; i < 4; i++) {
        auto* image = (KImage*)info->images[i];
        Slot& s = g_rh.slots[i];
        if (!image || info->maxFrames[i] < 1) {
            s.valid = false;
            continue;
        }
        uint32_t w = (uint32_t)image->mtl.width, h = (uint32_t)image->mtl.height;
        if (s.w != w || s.h != h || !s.color[0]) {
            for (int k = 0; k < 2; k++) {
                s.color[k] = newTexture(d, MTLPixelFormatRGBA16Float, w, h);
                s.geometry[k] = newTexture(d, MTLPixelFormatRGBA16Float, w, h);
            }
            s.w = w; s.h = h;
            s.valid = false;
        }
        if (!s.color[0] || !s.color[1] || !s.geometry[0] || !s.geometry[1]) {
            KERR_LIMIT(10, "ray history: no %ux%u history for image %d", w, h, i);
            s.color[0] = nil;  // made again next frame
            s.valid = false;
            continue;
        }
        int last = s.current, next = 1 - s.current;
        HistoryParams p;
        memcpy(p.egoFromClip, info->egoFromClip, sizeof(p.egoFromClip));
        memcpy(p.scene, info->scene, sizeof(p.scene));
        memcpy(p.object, info->object, sizeof(p.object));
        memcpy(p.nearby, info->nearby, sizeof(p.nearby));
        memcpy(p.sphere, info->objectSphere, sizeof(p.sphere));
        memcpy(p.body, info->bodySphere, sizeof(p.body));
        p.renderSize[0] = (float)depth->mtl.width; p.renderSize[1] = (float)depth->mtl.height;
        p.imageSize[0] = (float)w; p.imageSize[1] = (float)h;
        p.lastJitter[0] = g_rh.lastJitter[0]; p.lastJitter[1] = g_rh.lastJitter[1];
        p.maxFrames = info->maxFrames[i];
        p.clip = info->clip[i];
        p.pad[0] = p.pad[1] = p.pad[2] = 0;
        p.history = s.valid && !reset ? 1 : 0;
        [e setTexture:image->mtl atIndex:0];
        [e setTexture:depth->mtl atIndex:1];
        [e setTexture:normals->mtl atIndex:2];
        [e setTexture:s.color[last] atIndex:3];
        [e setTexture:s.geometry[last] atIndex:4];
        [e setTexture:s.color[next] atIndex:5];
        [e setTexture:s.geometry[next] atIndex:6];
        [e setBytes:&p length:sizeof(p) atIndex:0];
        [e dispatchThreads:MTLSizeMake(w, h, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
        [e memoryBarrierWithScope:MTLBarrierScopeTextures];
        [e setComputePipelineState:g_rh.writeBack];
        [e setTexture:image->mtl atIndex:0];
        [e setTexture:s.color[next] atIndex:1];
        [e dispatchThreads:MTLSizeMake(w, h, 1) threadsPerThreadgroup:MTLSizeMake(16, 16, 1)];
        [e memoryBarrierWithScope:MTLBarrierScopeTextures];
        [e setComputePipelineState:g_rh.accumulate];
        s.current = next;
        s.valid = true;
    }
    // The game's compute state must be re-applied on its next dispatch
    cb->computeSetsDirty = ~0u;
    cb->computePushDirty = true;
    g_rh.lastJitter[0] = info->jitterX; g_rh.lastJitter[1] = info->jitterY;
}
