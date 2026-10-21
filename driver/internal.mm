// Internal shaders: 32-bit buffer fills (Metal's fillBuffer only repeats a byte) and scaled image blits.
#include "kmtl.h"

static const char* kInternalMSL = R"(
#include <metal_stdlib>
using namespace metal;

kernel void kmtl_fill32(device uint* dst [[buffer(0)]], constant uint2& p [[buffer(1)]], uint i [[thread_position_in_grid]]) {
    if (i < p.y) dst[i] = p.x;
}

// Tessellated indexed indirect draws: turn a VkDrawIndexedIndirectCommand into the stage-in region, dispatch sizes,
// control-stage parameters and drawPatches arguments (instances folded into the patch count)
struct VkDII { uint indexCount, instanceCount, firstIndex; int vertexOffset; uint firstInstance; };
kernel void kmtl_tess_prep(const device VkDII& a [[buffer(0)]], device uint* o [[buffer(1)]], constant uint4& p [[buffer(2)]],
                           constant uint& maxIndices [[buffer(3)]], device atomic_uint* stats [[buffer(4)]]) {
    uint inCP = p.x, outCP = p.y, vw = p.z, wg = p.w;
    // Largest request seen (the driver grows the scratch cap from it), and what the draw path does not handle
    atomic_fetch_max_explicit(&stats[0], a.indexCount, memory_order_relaxed);
    atomic_fetch_max_explicit(&stats[1], a.firstIndex, memory_order_relaxed);
    atomic_fetch_max_explicit(&stats[2], a.instanceCount, memory_order_relaxed);
    if (a.indexCount > maxIndices) atomic_fetch_add_explicit(&stats[3], 1u, memory_order_relaxed);
    // The scratch buffers hold maxIndices vertices: larger draws are clamped (and reported by the driver)
    uint indexCount = min(a.indexCount, maxIndices / inCP * inCP);
    uint patches = (indexCount / inCP) * a.instanceCount;
    o[0] = uint(a.vertexOffset); o[1] = a.firstInstance; o[2] = 0;
    o[3] = indexCount; o[4] = a.instanceCount; o[5] = 1;
    o[6] = (indexCount + vw - 1) / vw; o[7] = a.instanceCount; o[8] = 1;
    o[9] = (patches * outCP + wg - 1) / wg; o[10] = 1; o[11] = 1;
    o[12] = inCP; o[13] = patches;
    o[14] = patches; o[15] = 1; o[16] = 0; o[17] = 0;
    o[18] = a.indexCount;  // unclamped, for the driver's report
}

struct BlitParams {
    float4 srcRect;   // u0, v0, u1, v1 (normalized)
    float lod;
    float layer;
    float depth;      // 3D: normalized w
    float pad;
};

struct BlitOut {
    float4 position [[position]];
    float2 uv;
};

vertex BlitOut kmtl_blit_vs(uint vid [[vertex_id]], constant BlitParams& p [[buffer(0)]]) {
    float2 t = float2((vid << 1) & 2, vid & 2);  // fullscreen triangle: (0,0) (2,0) (0,2)
    BlitOut o;
    o.position = float4(t.x * 2.0 - 1.0, 1.0 - t.y * 2.0, 0.0, 1.0);
    o.uv = mix(p.srcRect.xy, p.srcRect.zw, t);
    return o;
}

fragment float4 kmtl_blit_fs_2d(BlitOut in [[stage_in]], constant BlitParams& p [[buffer(0)]],
                                texture2d<float> src [[texture(0)]], sampler s [[sampler(0)]]) {
    return src.sample(s, in.uv, level(p.lod));
}

fragment float4 kmtl_blit_fs_2darray(BlitOut in [[stage_in]], constant BlitParams& p [[buffer(0)]],
                                     texture2d_array<float> src [[texture(0)]], sampler s [[sampler(0)]]) {
    return src.sample(s, in.uv, uint(p.layer), level(p.lod));
}

fragment float4 kmtl_blit_fs_3d(BlitOut in [[stage_in]], constant BlitParams& p [[buffer(0)]],
                                texture3d<float> src [[texture(0)]], sampler s [[sampler(0)]]) {
    return src.sample(s, float3(in.uv, p.depth), level(p.lod));
}
)";

struct KInternalPipelines {
    id<MTLLibrary> lib = nil;
    id<MTLComputePipelineState> fill32 = nil;
    id<MTLComputePipelineState> tessPrep = nil;
    // Written by kmtl_tess_prep: [0] largest index count requested, [1] largest firstIndex, [2] largest instance
    // count, [3] number of draws clamped to the cap
    id<MTLBuffer> tessStats = nil;
    std::atomic<uint32_t> tessCap{0};
    uint32_t firstIndexWarned = 0;
    id<MTLSamplerState> nearest = nil, linear = nil;
    std::mutex m;
    std::unordered_map<uint64_t, id<MTLRenderPipelineState>> blit;  // (pixel format, variant) -> pipeline
};

void kmtlInitInternal(KDevice* dev) {
    auto* ip = new KInternalPipelines();
    NSError* e = nil;
    ip->lib = [dev->mtl newLibraryWithSource:@(kInternalMSL) options:nil error:&e];
    if (!ip->lib) {
        KERR("internal shaders failed to compile: %s", e.description.UTF8String);
        dev->internal = ip;
        return;
    }
    ip->fill32 = [dev->mtl newComputePipelineStateWithFunction:[ip->lib newFunctionWithName:@"kmtl_fill32"] error:&e];
    ip->tessPrep = [dev->mtl newComputePipelineStateWithFunction:[ip->lib newFunctionWithName:@"kmtl_tess_prep"] error:&e];
    ip->tessStats = [dev->mtl newBufferWithLength:16 options:MTLResourceStorageModeShared | MTLResourceHazardTrackingModeUntracked];
    memset(ip->tessStats.contents, 0, 16);
    ip->tessCap = (uint32_t)std::max(3, kmtlEnvInt("KSA_MTL_TESS_MAX_INDICES", 1 << 19));
    MTLSamplerDescriptor* sd = [MTLSamplerDescriptor new];
    sd.sAddressMode = sd.tAddressMode = sd.rAddressMode = MTLSamplerAddressModeClampToEdge;
    ip->nearest = [dev->mtl newSamplerStateWithDescriptor:sd];
    sd.minFilter = sd.magFilter = MTLSamplerMinMagFilterLinear;
    ip->linear = [dev->mtl newSamplerStateWithDescriptor:sd];
    dev->internal = ip;
}

void kmtlInternalFillBuffer(KCommandBuffer* cb, id<MTLBuffer> buf, NSUInteger offset, NSUInteger size, uint32_t data) {
    KInternalPipelines* ip = cb->device->internal;
    if (!ip->fill32) return;
    id<MTLComputeCommandEncoder> enc = cb->computeEncoder();
    uint32_t count = (uint32_t)(size / 4);
    uint32_t p[2] = {data, count};
    [enc setComputePipelineState:ip->fill32];
    [enc setBuffer:buf offset:offset atIndex:0];
    [enc setBytes:p length:sizeof(p) atIndex:1];
    [enc dispatchThreads:MTLSizeMake(count, 1, 1) threadsPerThreadgroup:MTLSizeMake(std::min(count, 256u), 1, 1)];
    // The game's compute state must be re-applied on the next dispatch
    cb->computeSetsDirty = ~0u;
    cb->computePushDirty = true;
}

static id<MTLRenderPipelineState> blitPipeline(KDevice* dev, MTLPixelFormat dst, int variant) {
    KInternalPipelines* ip = dev->internal;
    uint64_t key = ((uint64_t)dst << 8) | (uint64_t)variant;
    std::lock_guard<std::mutex> lock(ip->m);
    auto it = ip->blit.find(key);
    if (it != ip->blit.end()) return it->second;
    static NSString* const fs[] = {@"kmtl_blit_fs_2d", @"kmtl_blit_fs_2darray", @"kmtl_blit_fs_3d"};
    MTLRenderPipelineDescriptor* d = [MTLRenderPipelineDescriptor new];
    d.vertexFunction = [ip->lib newFunctionWithName:@"kmtl_blit_vs"];
    d.fragmentFunction = [ip->lib newFunctionWithName:fs[variant]];
    d.colorAttachments[0].pixelFormat = dst;
    NSError* e = nil;
    id<MTLRenderPipelineState> ps = [dev->mtl newRenderPipelineStateWithDescriptor:d error:&e];
    if (!ps) KERR("blit pipeline for format %lu: %s", (unsigned long)dst, e.description.UTF8String);
    ip->blit[key] = ps;
    return ps;
}

void kmtlInternalBlit(KCommandBuffer* cb, KImage* src, KImage* dst, const VkImageBlit2& r, VkFilter filter) {
    KDevice* dev = cb->device;
    const KFormatInfo* dfi = kmtlFormat(dst->format);
    const KFormatInfo* sfi = kmtlFormat(src->format);
    if (!dfi || !dfi->renderable || dfi->depth || dfi->stencil || dfi->isInt || dfi->isUint || sfi->isInt || sfi->isUint) {
        static std::atomic<int> warned{0};
        if (warned++ < 5) KERR("scaled blit between formats %d -> %d unsupported", src->format, dst->format);
        return;
    }
    uint32_t sl = r.srcSubresource.mipLevel, dl = r.dstSubresource.mipLevel;
    bool src3D = src->info.imageType == VK_IMAGE_TYPE_3D, dst3D = dst->info.imageType == VK_IMAGE_TYPE_3D;
    // Sample through a view of the source level only: mip chains blit within one image, and no draw may read the
    // level it renders into
    int variant;
    MTLTextureType vt;
    NSRange slices;
    if (src3D) { variant = 2; vt = MTLTextureType3D; slices = NSMakeRange(0, 1); }
    else { variant = 1; vt = MTLTextureType2DArray; slices = NSMakeRange(0, src->info.arrayLayers); }
    id<MTLTexture> srcTex = [src->mtl newTextureViewWithPixelFormat:src->mtl.pixelFormat textureType:vt
                                                             levels:NSMakeRange(sl, 1) slices:slices];
    if (!srcTex) {
        KERR("blit: source view failed");
        return;
    }
    id<MTLRenderPipelineState> ps = blitPipeline(dev, dst->pixelFormat, variant);
    if (!ps) return;
    float sw = (float)std::max(1u, src->info.extent.width >> sl), sh = (float)std::max(1u, src->info.extent.height >> sl);
    float sd = (float)std::max(1u, src->info.extent.depth >> sl);
    uint32_t dw = std::max(1u, dst->info.extent.width >> dl), dh = std::max(1u, dst->info.extent.height >> dl);
    int dz0 = std::min(r.dstOffsets[0].z, r.dstOffsets[1].z), dz1 = std::max(r.dstOffsets[0].z, r.dstOffsets[1].z);
    // One pass per destination layer (2D) or depth plane (3D)
    uint32_t passes = dst3D ? (uint32_t)std::max(1, dz1 - dz0) : r.dstSubresource.layerCount;
    for (uint32_t i = 0; i < passes; i++) {
        MTLRenderPassDescriptor* d = [MTLRenderPassDescriptor renderPassDescriptor];
        d.colorAttachments[0].texture = dst->mtl;
        d.colorAttachments[0].level = dl;
        if (dst3D) d.colorAttachments[0].depthPlane = dz0 + i;
        else d.colorAttachments[0].slice = r.dstSubresource.baseArrayLayer + i;
        d.colorAttachments[0].loadAction = MTLLoadActionLoad;
        d.colorAttachments[0].storeAction = MTLStoreActionStore;
        cb->beginRender(d, dw, dh);
        cb->passes--;
        id<MTLRenderCommandEncoder> enc = cb->render;
        if (!enc) return;
        int x0 = std::min(r.dstOffsets[0].x, r.dstOffsets[1].x), x1 = std::max(r.dstOffsets[0].x, r.dstOffsets[1].x);
        int y0 = std::min(r.dstOffsets[0].y, r.dstOffsets[1].y), y1 = std::max(r.dstOffsets[0].y, r.dstOffsets[1].y);
        // The fullscreen triangle covers twice the viewport, so the viewport is the destination rectangle and the
        // uv rectangle is extrapolated: uv(t) = mix(a, b, t) with t in [0, 2]
        [enc setViewport:(MTLViewport){(double)x0, (double)y0, (double)(x1 - x0), (double)(y1 - y0), 0, 1}];
        [enc setRenderPipelineState:ps];
        struct { float rect[4]; float lod, layer, depth, pad; } p;
        float u0 = r.srcOffsets[0].x / sw, v0 = r.srcOffsets[0].y / sh;
        float u1 = r.srcOffsets[1].x / sw, v1 = r.srcOffsets[1].y / sh;
        // Mirror if the destination offsets are reversed
        if (r.dstOffsets[0].x > r.dstOffsets[1].x) std::swap(u0, u1);
        if (r.dstOffsets[0].y > r.dstOffsets[1].y) std::swap(v0, v1);
        p.rect[0] = u0; p.rect[1] = v0; p.rect[2] = u1; p.rect[3] = v1;
        p.lod = 0;
        p.layer = src3D ? 0 : (float)(r.srcSubresource.baseArrayLayer + (dst3D ? 0 : i));
        if (src3D) {
            // Map the destination plane centre into the source z range
            float sz0 = (float)r.srcOffsets[0].z, sz1 = (float)r.srcOffsets[1].z;
            float t = (i + 0.5f) / (float)passes;
            if (r.dstOffsets[0].z > r.dstOffsets[1].z) t = 1.0f - t;
            p.depth = (sz0 + t * (sz1 - sz0)) / sd;
        } else {
            p.depth = 0.5f;
        }
        p.pad = 0;
        [enc setVertexBytes:&p length:sizeof(p) atIndex:0];
        [enc setFragmentBytes:&p length:sizeof(p) atIndex:0];
        [enc setFragmentTexture:srcTex atIndex:0];
        [enc setFragmentSamplerState:filter == VK_FILTER_LINEAR ? dev->internal->linear : dev->internal->nearest atIndex:0];
        [enc drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
        cb->endEncoder();
    }
}

id<MTLComputePipelineState> kmtlTessPrepPipeline(KDevice* dev) { return dev->internal->tessPrep; }
id<MTLBuffer> kmtlTessStats(KDevice* dev) { return dev->internal->tessStats; }

// Per-draw index cap of indirect tessellated draws (sizes the scratch buffers). The real counts are GPU-written, so
// the prep kernel records the largest request and the cap grows to it: a draw over the cap loses its last patches
// (holes in the terrain) until the next command buffer records with the larger cap
uint32_t kmtlTessCap(KDevice* dev) {
    KInternalPipelines* ip = dev->internal;
    const volatile uint32_t* st = (const volatile uint32_t*)ip->tessStats.contents;
    uint32_t want = st[0], cap = ip->tessCap.load(std::memory_order_relaxed);
    static const bool grow = kmtlEnvInt("KSA_MTL_TESS_GROW", 1) != 0;
    if (want > cap && grow) {
        uint32_t grown = (uint32_t)std::min<uint64_t>(((uint64_t)want * 5 / 4 + 65535) & ~65535ull, 0xffffffffu);
        if (ip->tessCap.compare_exchange_strong(cap, grown))
            KLOG(1, "tessellation index cap %u -> %u (largest draw %u indices, firstIndex up to %u, instances up to %u, "
                 "%u clamped draws so far)", cap, grown, want, st[1], st[2], st[3]);
        return std::max(cap, grown);
    }
    if (st[1] && !ip->firstIndexWarned) {
        ip->firstIndexWarned = 1;
        KERR("indirect tessellated draws with firstIndex %u are drawn from index 0", st[1]);
    }
    return cap;
}
