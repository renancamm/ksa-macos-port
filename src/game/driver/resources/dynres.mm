// Dynamic resolution's mechanism: the main view drawn at a level of its size, frame by frame, without the game
// rebuilding its targets. Which level each frame takes, and when Dynamic Resolution draws levels at all: the Frame Rate
// section's controller (presentation/framerate.mm), which registers the main view's size here (kmtlDynamicSetView) and
// sets the level between frames (kmtlDynamicSetLevel).
//
// An image bound after a registration that is drawn into and is the main view's size or a fraction of it (fits) is
// dynamic: besides its texture at its own size it gets one per level, placed in the same memory, and draws, copies and
// views use the level's. A level's size is the main view's n/kDynamicParts, and a fraction's the same fraction of it,
// rounded as the game rounds it (sizeAt). What the game draws with the image then sees the smaller size,
// as after a rebuild: the shaders' image sizes and normalised coordinates, the viewports and scissors (scaled by the
// pass's attachments, passes.mm and state.mm), the copies (transfers.mm). What the game works out from its sizes itself
// (the cameras' screen size, push constants) the hooks give the level's size (framerate.mm's ksaMtlFrameRate*).
//
// A change of level, at the start of the game's frame, carries what each image holds over to its texture at the new
// level (scaled: the game's histories), in the queue's order after the frames before, and moves the images and their
// views to them. The descriptor sets keep the textures they were written with: a set that holds a dynamic image's
// texture is bound through a copy whose textures are the frame's level's (kmtlDynamicTranslate, from any level's
// texture to the image's or view's now), so frames still on the GPU read theirs unchanged and nothing waits for them.
// The game records a frame's commands after the change, at its start (the hooks' Program.Render prefix): a command
// buffer recorded across it would mix two levels' textures and sizes.
#include "kmtl.h"
#include <cmath>
#include <map>
#include <tuple>

namespace {
struct DynamicState {
    std::mutex m;
    uint32_t width = 0, height = 0;  // the main view's size the images are matched against (0: none)
    std::atomic<uint32_t> level{0};  // the level now (0: full size); read at present without the lock
    std::vector<uint32_t> levels;    // the levels the frames take (0: full size), made ahead
    bool prepared = false;           // the levels' textures made (prepareLevels) since the last dynamic image or view
    std::vector<KImage*> images;     // the dynamic images
    // Every level's texture of a dynamic image or view, by resource ID: what a descriptor's texture is now
    struct Owner { KImage* image; KImageView* view; };
    std::unordered_map<uint64_t, Owner> owners;
    KDevice* device = nullptr;
    uint64_t changes = 0;
    // The level change's resampling: a pipeline per pixel format
    id<MTLLibrary> library = nil;
    std::unordered_map<NSUInteger, id<MTLRenderPipelineState>> resample;
};
DynamicState& g_dyn = *new DynamicState;
}

// The images that can be dynamic: 2D, as many layers as this at most (the ambient occlusion's four deinterleaved
// quarters), at least this many pixels each way, or each side the main view's divided by a whole number up to this
static constexpr uint32_t kMostLayers = 16, kLeastSize = 16, kMostDivisor = 16;

uint32_t kmtlDynamicScaled(uint32_t size, uint32_t level) {
    if (level == 0) return size;
    return std::max(1u, (uint32_t)lround((double)size * level / kDynamicParts));
}

// How a size follows the main view's: its size divided by a whole number k, rounded up or down. The game derives its
// fractions from the main view's size ((h + 1) / 2 for the ambient occlusion's, w / 4 for the exhausts'), and at a
// level from the level's size (the hooks give it that): an image's texture at a level is the same fraction of the main
// view's size at the level, rounded the same way, so that the two agree (the ambient occlusion's own half sizes a row
// apart from its textures drew a dark band across the ground at some levels). A size both ways (an exact fraction) is
// taken as rounded up, as the clouds' and the ambient occlusion's are; the hooks give the exhausts and the particles
// the textures' sizes. k = 0: no such fraction (within a pixel of one: scaled itself)
using KFraction = KImage::KFraction;
static KFraction fractionOf(uint32_t size, uint32_t main) {
    for (uint32_t k = 1; k <= kMostDivisor && main; k++) {
        if (size == (main + k - 1) / k) return {(uint8_t)k, true};
        if (size == main / k) return {(uint8_t)k, false};
    }
    return {};
}

// A size at a level: as a fraction of the main view's there, or scaled itself
static uint32_t sizeAt(uint32_t size, KFraction f, uint32_t main, uint32_t level) {
    if (level == 0) return size;
    if (!f.k) return kmtlDynamicScaled(size, level);
    uint32_t m = kmtlDynamicScaled(main, level);
    return std::max(1u, f.up ? (m + f.k - 1) / f.k : m / f.k);
}

// Whether a size is the main view's divided by a whole number, within a pixel (rounded or padded either way)
static bool divides(uint32_t size, uint32_t full) {
    for (uint32_t k = 1; k <= kMostDivisor; k++)
        if (std::abs((double)size - (double)full / k) <= 1.0) return true;
    return false;
}

static uint32_t mipsFor(uint32_t w, uint32_t h) {
    uint32_t n = 1;
    for (uint32_t m = std::max(w, h); m > 1; m >>= 1) n++;
    return n;
}

// Whether an image is the main view's size or a fraction of it: a 2D image or array (kMostLayers), no larger, at least
// kLeastSize each way, the same shape within a pixel's rounding each way, or each side the main view's divided by a
// whole number (the exhausts' half by a quarter)
static bool fits(const VkImageCreateInfo& ci, uint32_t W, uint32_t H) {
    if (!W || !H || ci.imageType != VK_IMAGE_TYPE_2D || ci.arrayLayers > kMostLayers || ci.tiling != VK_IMAGE_TILING_OPTIMAL) return false;
    if (ci.flags & VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT) return false;
    // Drawn into (an attachment or a storage image): not textures the game uploads, which happen to have such a size
    if (!(ci.usage & (VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_STORAGE_BIT)))
        return false;
    uint64_t w = ci.extent.width, h = ci.extent.height;
    if (w > W || h > H || w < kLeastSize || h < kLeastSize) return false;
    // Not a mip chain down to a pixel: the lowest level's texture would have fewer mip levels than the game's views
    if (ci.mipLevels > mipsFor(std::max(1u, (uint32_t)(w * kDynamicMinLevel / kDynamicParts)), std::max(1u, (uint32_t)(h * kDynamicMinLevel / kDynamicParts))))
        return false;
    int64_t skew = (int64_t)(w * H) - (int64_t)(h * W);
    return (uint64_t)std::llabs(skew) <= W + H || (divides((uint32_t)w, W) && divides((uint32_t)h, H));
}

// The image's texture at a level, placed where its full-size one is; the full-size one where it doesn't fit
static id<MTLTexture> levelTexture(KImage* img, uint32_t level) {
    bool memoryless = img->desc.storageMode == MTLStorageModeMemoryless;
    if (level == 0 || (!img->heap && !memoryless)) return img->full;
    if (img->levels.size() <= level) img->levels.resize(kDynamicParts);
    if (img->levels[level]) return img->levels[level];
    MTLTextureDescriptor* d = [img->desc copy];
    d.width = sizeAt(img->info.extent.width, img->fractionW, img->mainW, level);
    d.height = sizeAt(img->info.extent.height, img->fractionH, img->mainH, level);
    d.mipmapLevelCount = std::min<NSUInteger>(d.mipmapLevelCount, mipsFor((uint32_t)d.width, (uint32_t)d.height));
    id<MTLTexture> t = nil;
    if (memoryless) {
        t = [img->device->mtl newTextureWithDescriptor:d];
    } else {
        MTLSizeAndAlign sa = [img->device->mtl heapTextureSizeAndAlignWithDescriptor:d];
        if (sa.size <= img->sizeAlign.size && img->heapOffset % std::max<NSUInteger>(sa.align, 1) == 0)
            t = [img->heap newTextureWithDescriptor:d offset:img->heapOffset];
    }
    if (!t) {
        KERR_LIMIT(10, "dynamic resolution: %ux%u (format %d) has no texture at level %u", img->info.extent.width,
                   img->info.extent.height, img->format, level);
        t = img->full;
    }
    img->levels[level] = t;
    g_dyn.owners[t.gpuResourceID._impl] = {img, nullptr};
    return t;
}

// A view's texture over the image's texture at a level, made as the view was (images.mm: at its own level)
id<MTLTexture> kmtlViewTexture(KImageView* v, id<MTLTexture> tex) {
    if (v->whole) return tex;
    uint32_t mips = (uint32_t)tex.mipmapLevelCount;
    uint32_t base = std::min(v->baseLevel, mips - 1);
    uint32_t count = std::max(1u, std::min(v->levelCount, mips - base));
    NSRange levels = NSMakeRange(base, count);
    NSRange slices = NSMakeRange(v->baseLayer, v->viewType == MTLTextureType3D ? 1 : v->layerCount);
    return v->identitySwizzle
        ? [tex newTextureViewWithPixelFormat:v->viewFormat textureType:v->viewType levels:levels slices:slices]
        : [tex newTextureViewWithPixelFormat:v->viewFormat textureType:v->viewType levels:levels slices:slices swizzle:v->swizzle];
}

// A view's texture at a level, made once (under g_dyn.m)
static id<MTLTexture> viewAt(KImageView* v, uint32_t level, id<MTLTexture> tex) {
    if (v->levels.size() <= level) v->levels.resize(kDynamicParts);
    if (!v->levels[level]) {
        v->levels[level] = kmtlViewTexture(v, tex);
        if (v->levels[level] && !v->whole) g_dyn.owners[v->levels[level].gpuResourceID._impl] = {v->image, v};
    }
    return v->levels[level];
}

// images.mm, at bind: the full-size texture is placed; a dynamic image takes the level's
void kmtlDynamicBind(KImage* img) {
    img->full = img->mtl;
    std::lock_guard<std::mutex> lock(g_dyn.m);
    if (!g_dyn.device) g_dyn.device = img->device;
    if (!fits(img->info, g_dyn.width, g_dyn.height)) return;
    img->dynamic = true;
    img->mainW = g_dyn.width;
    img->mainH = g_dyn.height;
    img->fractionW = fractionOf(img->info.extent.width, g_dyn.width);
    img->fractionH = fractionOf(img->info.extent.height, g_dyn.height);
    g_dyn.prepared = false;
    g_dyn.images.push_back(img);
    g_dyn.owners[img->full.gpuResourceID._impl] = {img, nullptr};
    img->level = g_dyn.level;
    img->mtl = levelTexture(img, img->level);
    KLOG(1, "dynamic resolution: image %ux%u format %d mips %u, level %u: %lux%lu", img->info.extent.width,
         img->info.extent.height, img->format, img->info.mipLevels, img->level, (unsigned long)img->mtl.width,
         (unsigned long)img->mtl.height);
}

void kmtlDynamicForget(KImage* img) {
    if (!img->dynamic) return;
    std::lock_guard<std::mutex> lock(g_dyn.m);
    auto it = std::find(g_dyn.images.begin(), g_dyn.images.end(), img);
    if (it != g_dyn.images.end()) g_dyn.images.erase(it);
    if (img->full) g_dyn.owners.erase(img->full.gpuResourceID._impl);
    for (id<MTLTexture> t : img->levels)
        if (t) g_dyn.owners.erase(t.gpuResourceID._impl);
    // Views destroyed after their image (Vulkan allows it): no longer tracked, their image gone
    for (KImageView* v : img->views) {
        if (!v->whole)
            for (id<MTLTexture> t : v->levels)
                if (t) g_dyn.owners.erase(t.gpuResourceID._impl);
        v->image = nullptr;
    }
    img->views.clear();
}

bool kmtlDynamicView(VkImageView view) {
    auto* v = (KImageView*)view;
    return v && v->dynamic && v->image;
}

// A bound set's copy: each texture of a dynamic image or view, written at whatever level, at the frame's
void kmtlDynamicTranslate(const KSetLayout* l, uint64_t* slots) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    for (const auto& b : l->bindings) {
        switch (b.type) {
        case VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER:
        case VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE:
        case VK_DESCRIPTOR_TYPE_STORAGE_IMAGE:
        case VK_DESCRIPTOR_TYPE_INPUT_ATTACHMENT:
            break;
        default:
            continue;
        }
        for (uint32_t i = 0; i < b.count; i++) {
            auto it = g_dyn.owners.find(slots[b.id + i]);
            if (it == g_dyn.owners.end()) continue;
            id<MTLTexture> now = it->second.view ? it->second.view->mtl : it->second.image->mtl;
            if (now) slots[b.id + i] = now.gpuResourceID._impl;
        }
    }
}

// images.mm, a new view of a dynamic image: it follows the image's level
void kmtlDynamicAddView(KImageView* v) {
    KImage* img = v->image;
    if (!img->dynamic) return;
    std::lock_guard<std::mutex> lock(g_dyn.m);
    v->dynamic = true;
    if (v->levels.size() <= img->level) v->levels.resize(kDynamicParts);
    v->levels[img->level] = v->mtl;
    if (!v->whole) g_dyn.owners[v->mtl.gpuResourceID._impl] = {img, v};
    img->views.push_back(v);
    g_dyn.prepared = false;  // its levels' views
}

void kmtlDynamicRemoveView(KImageView* v) {
    // Its image destroyed before it (kmtlDynamicForget): its textures already forgotten
    if (!v->dynamic || !v->image) return;
    KImage* img = v->image;
    std::lock_guard<std::mutex> lock(g_dyn.m);
    auto it = std::find(img->views.begin(), img->views.end(), v);
    if (it != img->views.end()) img->views.erase(it);
    if (!v->whole)
        for (id<MTLTexture> t : v->levels)
            if (t) g_dyn.owners.erase(t.gpuResourceID._impl);
}

// The share of an image's own size its texture now has, each way (1 for others)
void kmtlDynamicScale(const KImage* img, float* sx, float* sy) {
    if (!img || !img->dynamic || img->level == 0) { *sx = *sy = 1; return; }
    *sx = (float)img->mtl.width / (float)img->info.extent.width;
    *sy = (float)img->mtl.height / (float)img->info.extent.height;
}

// What the game gives at an image's own size, at its level (scaled by sx, sy), each with its own rounding. A viewport:
// exact. A rectangle (scissors, copies): its start rounded down and its end up, so that it covers what it covered,
// within limitW x limitH, as [x0, x1) x [y0, y1) (empty when x1 <= x0 or y1 <= y0). A point (a blit's corners): to the
// nearest pixel
MTLViewport kmtlDynamicViewport(const VkViewport& vp, double sx, double sy) {
    return {vp.x * sx, vp.y * sy, vp.width * sx, vp.height * sy, vp.minDepth, vp.maxDepth};
}

void kmtlDynamicRect(double sx, double sy, int64_t x, int64_t y, int64_t w, int64_t h, int64_t limitW, int64_t limitH,
                     int64_t* x0, int64_t* y0, int64_t* x1, int64_t* y1) {
    *x0 = std::max<int64_t>(0, (int64_t)std::floor(x * sx));
    *y0 = std::max<int64_t>(0, (int64_t)std::floor(y * sy));
    *x1 = std::min<int64_t>(limitW, (int64_t)std::ceil(((double)x + w) * sx));
    *y1 = std::min<int64_t>(limitH, (int64_t)std::ceil(((double)y + h) * sy));
}

int32_t kmtlDynamicPoint(int32_t v, double s) { return (int32_t)lround(v * s); }

static const char* kResampleSource = R"(
#include <metal_stdlib>
using namespace metal;
struct V { float4 pos [[position]]; float2 uv; };
vertex V fullscreen(uint id [[vertex_id]]) {
    float2 p = float2((id << 1) & 2, id & 2);
    V v;
    v.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
    v.uv = float2(p.x, 1.0 - p.y);
    return v;
}
fragment float4 resampleFloat(V in [[stage_in]], texture2d<float> src [[texture(0)]]) {
    constexpr sampler s(filter::linear, address::clamp_to_edge);
    return src.sample(s, in.uv);
}
template <typename T> static vec<T, 4> nearest(float2 uv, texture2d<T> src) {
    uint2 size = uint2(src.get_width(), src.get_height());
    return src.read(min(uint2(uv * float2(size)), size - 1));
}
fragment uint4 resampleUint(V in [[stage_in]], texture2d<uint> src [[texture(0)]]) { return nearest(in.uv, src); }
fragment int4 resampleInt(V in [[stage_in]], texture2d<int> src [[texture(0)]]) { return nearest(in.uv, src); }
)";

// What an image holds can be carried to another level: a colour image of one sample and one layer in device memory
// (the ambient occlusion's arrays are made again every frame)
static bool carried(const KImage* img) {
    const KFormatInfo* fi = kmtlFormat(img->format);
    return fi && !fi->depth && !fi->stencil && fi->renderable && img->info.samples == 1 && img->info.arrayLayers == 1 && img->heap &&
           (img->mtl.usage & MTLTextureUsageRenderTarget);
}

// Whether another dynamic image is placed in any of this one's memory (the game reuses memory between images it uses at
// different times): what either holds is the frame's own, and carrying both would write one over the other
static bool sharesMemory(const KImage* img) {
    for (const KImage* other : g_dyn.images)
        if (other != img && other->heap == img->heap && other->heapOffset < img->heapOffset + img->sizeAlign.size &&
            img->heapOffset < other->heapOffset + other->sizeAlign.size)
            return true;
    return false;
}

static id<MTLRenderPipelineState> resamplePipeline(KDevice* dev, const KImage* img) {
    MTLPixelFormat pf = img->mtl.pixelFormat;
    auto it = g_dyn.resample.find(pf);
    if (it != g_dyn.resample.end()) return it->second;
    NSError* err = nil;
    if (!g_dyn.library) {
        g_dyn.library = kmtlLibrary(dev->mtl, kResampleSource, nil, &err);
        if (!g_dyn.library) KERR("dynamic resolution: resample shaders: %s", err.localizedDescription.UTF8String);
    }
    id<MTLRenderPipelineState> ps = nil;
    if (g_dyn.library) {
        const KFormatInfo* fi = kmtlFormat(img->format);
        MTLRenderPipelineDescriptor* d = [MTLRenderPipelineDescriptor new];
        d.vertexFunction = [g_dyn.library newFunctionWithName:@"fullscreen"];
        d.fragmentFunction = [g_dyn.library newFunctionWithName:fi->isUint ? @"resampleUint" : fi->isInt ? @"resampleInt" : @"resampleFloat"];
        d.colorAttachments[0].pixelFormat = pf;
        ps = [dev->mtl newRenderPipelineStateWithDescriptor:d error:&err];
        if (!ps) KERR("dynamic resolution: resample pipeline for format %lu: %s", (unsigned long)pf, err.localizedDescription.UTF8String);
    }
    g_dyn.resample[pf] = ps;
    return ps;
}

// Every level the frames may take, made ahead (on the first frame after a rebuild, when the images exist): the
// textures, their views and the resampling pipelines, so that a change makes none (the first took 0.36 s; under
// g_dyn.m)
static void prepareLevels(const std::vector<uint32_t>& levels) {
    for (KImage* img : g_dyn.images) {
        if (carried(img)) resamplePipeline(img->device, img);
        for (uint32_t level : levels) {
            id<MTLTexture> t = levelTexture(img, level);
            for (KImageView* v : img->views) viewAt(v, level, t);
        }
    }
}

// The change of level, between frames (under g_dyn.m)
static void changeLevel(uint32_t level) {
    KDevice* dev = g_dyn.device;
    if (!dev) { g_dyn.level = level; return; }
    auto t0 = std::chrono::steady_clock::now();
    struct Carry { id<MTLTexture> from, to; id<MTLRenderPipelineState> ps; };
    std::vector<Carry> carries;
    KQueue* q = dev->queue;
    for (KImage* img : g_dyn.images) {
        if (img->level == level) continue;
        id<MTLTexture> from = img->mtl, to = levelTexture(img, level);
        if (from != to && carried(img) && !sharesMemory(img))
            if (id<MTLRenderPipelineState> ps = resamplePipeline(dev, img)) carries.push_back({from, to, ps});
        img->mtl = to;
        img->level = level;
        for (KImageView* v : img->views) {
            if (id<MTLTexture> nv = viewAt(v, level, to))
                v->mtl = nv;
            else
                KERR_LIMIT(10, "dynamic resolution: a view of %ux%u (format %d) has no texture at level %u: it stays at the last",
                           img->info.extent.width, img->info.extent.height, img->format, level);
        }
    }
    // Each image through a copy (its two levels' textures share its memory): one copy for each format and size,
    // used by one image after another, so every step waits for the one before. In the queue's order: after its chain
    // fence, which the game's next encoders wait for in turn
    if (!carries.empty()) {
        id<MTLCommandBuffer> cb = [q->mtl commandBuffer];
        cb.label = @"dynamic resolution: level change";
        std::map<std::tuple<NSUInteger, NSUInteger, NSUInteger>, id<MTLTexture>> copies;
        id<MTLFence> step = q->chain;
        for (size_t i = 0; i < carries.size(); i++) {
            const Carry& c = carries[i];
            __strong id<MTLTexture>& copy = copies[{c.from.pixelFormat, c.from.width, c.from.height}];
            if (!copy) {
                MTLTextureDescriptor* d = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:c.from.pixelFormat
                    width:c.from.width height:c.from.height mipmapped:NO];
                d.usage = MTLTextureUsageShaderRead;
                d.storageMode = MTLStorageModePrivate;
                d.hazardTrackingMode = MTLHazardTrackingModeUntracked;  // the fences order it
                copy = [dev->mtl newTextureWithDescriptor:d];
            }
            if (!copy) continue;
            id<MTLFence> next = [dev->mtl newFence];
            id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
            [b waitForFence:step];
            [b copyFromTexture:c.from sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
                    sourceSize:MTLSizeMake(c.from.width, c.from.height, 1) toTexture:copy destinationSlice:0
              destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
            [b updateFence:next];
            [b endEncoding];
            MTLRenderPassDescriptor* rp = [MTLRenderPassDescriptor renderPassDescriptor];
            rp.colorAttachments[0].texture = c.to;
            rp.colorAttachments[0].loadAction = MTLLoadActionDontCare;
            rp.colorAttachments[0].storeAction = MTLStoreActionStore;
            id<MTLRenderCommandEncoder> e = [cb renderCommandEncoderWithDescriptor:rp];
            [e waitForFence:next beforeStages:MTLRenderStageVertex];
            [e setRenderPipelineState:c.ps];
            [e setFragmentTexture:copy atIndex:0];
            [e drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
            step = [dev->mtl newFence];
            [e updateFence:step afterStages:MTLRenderStageFragment];
            [e endEncoding];
        }
        // The chain fence after the last step
        id<MTLBlitCommandEncoder> b = [cb blitCommandEncoder];
        [b waitForFence:step];
        [b updateFence:q->chain];
        [b endEncoding];
        std::lock_guard<std::mutex> submit(q->submitMutex);
        [cb commit];
    }
    g_dyn.level = level;
    g_dyn.changes++;
    KLOG(g_dyn.changes <= 20 ? 0 : 1, "dynamic resolution: level %u (%ux%u): %zu images to carry, %.2f ms", level,
         kmtlDynamicScaled(g_dyn.width, level), kmtlDynamicScaled(g_dyn.height, level), carries.size(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
}

// Without the lock: the present calls it holding the queue's submit lock, which a change of level takes inside it
uint32_t kmtlDynamicLevelNow() {
    return g_dyn.level.load(std::memory_order_relaxed);
}

// The controller, at each registration: the main view's size the images bound from now on are matched against (0 x 0:
// none), and the levels the frames take (0: the full size), made ahead at the next frame (kmtlDynamicPrepare)
void kmtlDynamicSetView(uint32_t width, uint32_t height, const std::vector<uint32_t>& levels) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    g_dyn.width = width;
    g_dyn.height = height;
    g_dyn.levels = levels;
    g_dyn.prepared = false;
}

// The controller, at the start of a frame: the levels' textures for images bound since, so that a change makes none
void kmtlDynamicPrepare() {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    if (!g_dyn.width || g_dyn.prepared || g_dyn.images.empty()) return;
    auto t0 = std::chrono::steady_clock::now();
    prepareLevels(g_dyn.levels);
    g_dyn.prepared = true;
    KLOG(0, "dynamic resolution: %zu images at %zu levels made in %.1f ms", g_dyn.images.size(), g_dyn.levels.size(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
}

// The controller, between frames: every dynamic image and view at this level (0: the full size), what they hold carried
void kmtlDynamicSetLevel(uint32_t level) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    if (level != g_dyn.level) changeLevel(level);
}

// An image's size as its texture has it now (a dynamic image's at the frame's level)
KMTL_EXPORT void ksaMtlDynamicImageSize(VkImage image, uint32_t* width, uint32_t* height) {
    KTRACE_FIRST_PLAIN();
    auto* img = (KImage*)image;
    *width = img && img->mtl ? (uint32_t)img->mtl.width : 0;
    *height = img && img->mtl ? (uint32_t)img->mtl.height : 0;
}

// The hooks: a size of the main view's or a fraction of it, at the level now, as its image's texture has it (sizeAt)
KMTL_EXPORT void ksaMtlDynamicSizeAt(uint32_t* width, uint32_t* height) {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_dyn.m);
    *width = sizeAt(*width, fractionOf(*width, g_dyn.width), g_dyn.width, g_dyn.level);
    *height = sizeAt(*height, fractionOf(*height, g_dyn.height), g_dyn.height, g_dyn.level);
}
