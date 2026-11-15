// Dynamic resolution: the main view drawn at a level of its size, frame by frame, without the game rebuilding its
// targets.
//
// The hooks pass the main view's size after each rebuild (ksaMtlDynamicResolution). An image bound after it that is
// that size or a fraction of it in both directions (the same shape within a pixel's rounding: the scene, its depth, the
// reduced images of the bloom, the ambient occlusion, the clouds) is dynamic: besides its texture at its own size it
// gets one per level, placed in the same memory, and draws, copies and views use the level's. Levels are steps of
// 1/kLevelSteps of the size, down to kMinLevel. What the game draws with the image then sees the smaller size, as it
// would after a rebuild: the shaders' image sizes and normalised coordinates, the viewports and scissors (scaled by
// the pass's attachments, passes.mm and state.mm), the copies (transfers.mm). What the game itself works out from
// its sizes (the cameras' screen size, dispatches, push constants) the hooks give the level's size.
//
// A change of level (ksaMtlDynamicResolutionFrame, at the start of the game's frame) carries what each image holds over
// to its texture at the new level (scaled: the game's histories), in the queue's order after the frames before, and
// moves the images and their views to them. The descriptor sets keep the textures they were written with: a set that
// holds a dynamic image's texture is bound through a copy whose textures are the frame's level's (kmtlDynamicTranslate,
// from any level's texture to the image's or view's now), so that frames still on the GPU read theirs unchanged and
// nothing waits for them.
//
// Each frame's level: with KSA_MTL_DYNAMIC_TARGET_MS, the highest whose GPU time is expected to stay under that target,
// from the last frames' GPU times (kmtlDynamicNoteFrame, as each present completes): a frame's time is taken as a part
// that doesn't follow the pixel count (kFixedShare) and a part that does. It goes down as soon as the frames run over
// the target, up only well under it, and changes at most every kChangeFrames frames, as a change costs a frame.
// KSA_MTL_DYNAMIC_LEVEL=<n> (16-63 of 64) is the lowest it goes; without a target, the level from the start (an
// experiment); KSA_MTL_DYNAMIC_CYCLE=<frames>: that level and the full size in turn, that many frames each (a test of
// the changes).
#include "kmtl.h"
#include <cmath>

namespace {
struct DynamicState {
    std::mutex m;
    uint32_t width = 0, height = 0;  // the main view's size (0: none)
    uint32_t level = 0;              // the level now (0: full size)
    uint32_t minLevel = 0;           // the lowest level the hooks will ask for (0: none below full size)
    std::vector<KImage*> images;     // the dynamic images
    // Every level's texture of a dynamic image or view, by resource ID: what a descriptor's texture is now
    struct Owner { KImage* image; KImageView* view; };
    std::unordered_map<uint64_t, Owner> owners;
    KDevice* device = nullptr;
    uint64_t frames = 0;             // frames begun (ksaMtlDynamicResolutionFrame)
    uint64_t changes = 0;
    uint64_t changedAt = 0;          // the frame of the last change
    bool prepared = false;           // the levels' textures made (prepareLevels) since the last image
    // The last frames' GPU times and levels, as their presents completed: a lock of their own, as completion handlers
    // add them while a change of level holds m and waits for the GPU
    std::mutex samplesMutex;
    struct Sample { uint32_t level; double ms; };
    Sample samples[16];
    uint32_t sampleCount = 0;
    // The level change's resampling: a pipeline per pixel format
    id<MTLLibrary> library = nil;
    std::unordered_map<NSUInteger, id<MTLRenderPipelineState>> resample;
};
DynamicState& g_dyn = *new DynamicState;
}

static constexpr uint32_t kLevelSteps = 64, kMinLevel = 16;

static uint32_t startLevel() {
    uint32_t n = (uint32_t)kmtlConfig().dynamicLevel;
    return n >= kMinLevel && n < kLevelSteps ? n : 0u;
}

static constexpr double kFixedShare = 0.5;
static constexpr uint32_t kChangeFrames = 30, kLevelStep = 4;

static double target() { return kmtlConfig().dynamicTargetMs; }

static uint32_t scaled(uint32_t size, uint32_t level) {
    if (level == 0) return size;
    return std::max(1u, (uint32_t)lround((double)size * level / kLevelSteps));
}

// Whether a size is the main view's divided by a whole number, within a pixel (rounded or padded either way)
static bool divides(uint32_t size, uint32_t full) {
    for (uint32_t k = 1; k <= 16; k++)
        if (std::abs((double)size - (double)full / k) <= 1.0) return true;
    return false;
}

// Whether an image is the main view's size or a fraction of it: a 2D image or array of up to 16 (the ambient
// occlusion's four deinterleaved quarters), no larger, at least 16 pixels each way,
// the same shape within a pixel's rounding each way, or each side the main view's divided by a whole number (the
// exhausts' half by a quarter)
static uint32_t mipsFor(uint32_t w, uint32_t h);

static bool fits(const VkImageCreateInfo& ci, uint32_t W, uint32_t H) {
    if (!W || !H || ci.imageType != VK_IMAGE_TYPE_2D || ci.arrayLayers > 16 || ci.tiling != VK_IMAGE_TILING_OPTIMAL) return false;
    if (ci.flags & VK_IMAGE_CREATE_CUBE_COMPATIBLE_BIT) return false;
    // Drawn into (an attachment or a storage image): not textures the game uploads, which happen to have such a size
    if (!(ci.usage & (VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_IMAGE_USAGE_STORAGE_BIT)))
        return false;
    uint64_t w = ci.extent.width, h = ci.extent.height;
    if (w > W || h > H || w < 16 || h < 16) return false;
    // Not a mip chain down to a pixel: the lowest level's texture would have fewer mip levels than the game's views
    if (ci.mipLevels > mipsFor(std::max(1u, (uint32_t)(w * kMinLevel / kLevelSteps)), std::max(1u, (uint32_t)(h * kMinLevel / kLevelSteps))))
        return false;
    int64_t skew = (int64_t)(w * H) - (int64_t)(h * W);
    return (uint64_t)std::llabs(skew) <= W + H || (divides((uint32_t)w, W) && divides((uint32_t)h, H));
}

static uint32_t mipsFor(uint32_t w, uint32_t h) {
    uint32_t n = 1;
    for (uint32_t m = std::max(w, h); m > 1; m >>= 1) n++;
    return n;
}

// The image's texture at a level, placed where its full-size one is; the full-size one where it doesn't fit
static id<MTLTexture> levelTexture(KImage* img, uint32_t level) {
    bool memoryless = img->desc.storageMode == MTLStorageModeMemoryless;
    if (level == 0 || (!img->heap && !memoryless)) return img->full;
    if (img->levels.size() <= level) img->levels.resize(kLevelSteps);
    if (img->levels[level]) return img->levels[level];
    MTLTextureDescriptor* d = [img->desc copy];
    d.width = scaled(img->info.extent.width, level);
    d.height = scaled(img->info.extent.height, level);
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

static id<MTLTexture> viewOf(KImageView* v, id<MTLTexture> tex);

// A view's texture at a level, made once (under g_dyn.m)
static id<MTLTexture> viewAt(KImageView* v, uint32_t level, id<MTLTexture> tex) {
    if (v->levels.size() <= level) v->levels.resize(kLevelSteps);
    if (!v->levels[level]) {
        v->levels[level] = viewOf(v, tex);
        if (v->levels[level] && !v->whole) g_dyn.owners[v->levels[level].gpuResourceID._impl] = {v->image, v};
    }
    return v->levels[level];
}

// A view's texture over the image's texture at a level, made as the view was
static id<MTLTexture> viewOf(KImageView* v, id<MTLTexture> tex) {
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

id<MTLTexture> kmtlViewTexture(KImageView* v, id<MTLTexture> tex) {
    return viewOf(v, tex);
}

// images.mm, at bind: the full-size texture is placed; a dynamic image takes the level's
void kmtlDynamicBind(KImage* img) {
    img->full = img->mtl;
    std::lock_guard<std::mutex> lock(g_dyn.m);
    if (!g_dyn.device) g_dyn.device = img->device;
    g_dyn.prepared = false;
    if (!fits(img->info, g_dyn.width, g_dyn.height)) return;
    img->dynamic = true;
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
    return v && v->image && v->image->dynamic;
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
    v->level = img->level;
    if (v->levels.size() <= v->level) v->levels.resize(kLevelSteps);
    v->levels[v->level] = v->mtl;
    if (!v->whole) g_dyn.owners[v->mtl.gpuResourceID._impl] = {img, v};
    img->views.push_back(v);
}

void kmtlDynamicRemoveView(KImageView* v) {
    KImage* img = v->image;
    if (!img || !img->dynamic) return;
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

static id<MTLRenderPipelineState> resamplePipeline(KDevice* dev, const KImage* img) {
    MTLPixelFormat pf = img->mtl.pixelFormat;
    auto it = g_dyn.resample.find(pf);
    if (it != g_dyn.resample.end()) return it->second;
    NSError* err = nil;
    if (!g_dyn.library) {
        g_dyn.library = [dev->mtl newLibraryWithSource:[NSString stringWithUTF8String:kResampleSource] options:nil error:&err];
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
// textures, their views and the resampling pipelines, so that a change makes none (the first took 0.36 s)
static void prepareLevels(const std::vector<uint32_t>& levels) {
    for (KImage* img : g_dyn.images) {
        if (carried(img)) resamplePipeline(img->device, img);
        for (uint32_t level : levels) {
            id<MTLTexture> t = levelTexture(img, level);
            for (KImageView* v : img->views) viewAt(v, level, t);
        }
    }
}

// The levels a target picks from: every kLevelStep down to the lowest, and the lowest (0: the full size)
static std::vector<uint32_t> levelGrid() {
    std::vector<uint32_t> grid = {0};
    uint32_t lowest = g_dyn.minLevel ? g_dyn.minLevel : kLevelSteps;
    for (uint32_t l = kLevelSteps - kLevelStep; l >= lowest + kLevelStep; l -= kLevelStep) grid.push_back(l);
    if (lowest < kLevelSteps) grid.push_back(lowest);
    return grid;
}

// The change of level, between frames (under g_dyn.m)
static void changeLevel(uint32_t level) {
    KDevice* dev = g_dyn.device;
    if (!dev) { g_dyn.level = level; return; }
    auto t0 = std::chrono::steady_clock::now();
    struct Carry { id<MTLTexture> copy, to; id<MTLRenderPipelineState> ps; };
    std::vector<Carry> carries;
    id<MTLCommandBuffer> cb = [dev->queue->mtl commandBuffer];
    cb.label = @"dynamic resolution: level change";
    id<MTLBlitCommandEncoder> blit = nil;
    KQueue* q = dev->queue;
    for (KImage* img : g_dyn.images) {
        if (img->level == level) continue;
        id<MTLTexture> from = img->mtl, to = levelTexture(img, level);
        if (from != to && carried(img)) {
            // Through a copy: the two levels' textures share the image's memory
            id<MTLRenderPipelineState> ps = resamplePipeline(dev, img);
            MTLTextureDescriptor* d = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:from.pixelFormat width:from.width
                                                                                       height:from.height mipmapped:NO];
            d.usage = MTLTextureUsageShaderRead;
            d.storageMode = MTLStorageModePrivate;
            id<MTLTexture> copy = ps ? [dev->mtl newTextureWithDescriptor:d] : nil;
            if (copy) {
                if (!blit) {
                    blit = [cb blitCommandEncoder];
                    [blit waitForFence:q->chain];
                }
                [blit copyFromTexture:from sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
                           sourceSize:MTLSizeMake(from.width, from.height, 1) toTexture:copy destinationSlice:0
                     destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
                carries.push_back({copy, to, ps});
            }
        }
        img->mtl = to;
        img->level = level;
        for (KImageView* v : img->views) {
            id<MTLTexture> nv = viewAt(v, level, to);
            if (!nv) continue;
            v->mtl = nv;
            v->level = level;
        }
    }
    // In the queue's order (its chain fence, which the game's next encoders wait for), one after another
    if (blit) {
        id<MTLFence> step = [dev->mtl newFence];
        [blit updateFence:step];
        [blit endEncoding];
        for (size_t i = 0; i < carries.size(); i++) {
            const Carry& c = carries[i];
            MTLRenderPassDescriptor* rp = [MTLRenderPassDescriptor renderPassDescriptor];
            rp.colorAttachments[0].texture = c.to;
            rp.colorAttachments[0].loadAction = MTLLoadActionDontCare;
            rp.colorAttachments[0].storeAction = MTLStoreActionStore;
            id<MTLRenderCommandEncoder> e = [cb renderCommandEncoderWithDescriptor:rp];
            [e waitForFence:step beforeStages:MTLRenderStageVertex];
            [e setRenderPipelineState:c.ps];
            [e setFragmentTexture:c.copy atIndex:0];
            [e drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
            [e updateFence:step afterStages:MTLRenderStageFragment];
            if (i + 1 == carries.size()) [e updateFence:q->chain afterStages:MTLRenderStageFragment];
            [e endEncoding];
        }
        std::lock_guard<std::mutex> submit(q->submitMutex);
        [cb commit];
    }
    g_dyn.level = level;
    g_dyn.changes++;
    KLOG(g_dyn.changes <= 20 ? 0 : 1, "dynamic resolution: level %u (%ux%u): %zu images carried, %.2f ms", level,
         scaled(g_dyn.width, level), scaled(g_dyn.height, level), carries.size(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
}

uint32_t kmtlDynamicLevelNow() {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    return g_dyn.level;
}

void kmtlDynamicNoteFrame(uint32_t level, double gpuMs) {
    std::lock_guard<std::mutex> lock(g_dyn.samplesMutex);
    if (gpuMs <= 0) return;
    g_dyn.samples[g_dyn.sampleCount++ % 16] = {level, gpuMs};
}

// The level for the next frame, by the target (under g_dyn.m)
static uint32_t chooseLevel() {
    uint32_t now = g_dyn.level ? g_dyn.level : kLevelSteps;
    if (g_dyn.frames < g_dyn.changedAt + kChangeFrames) return g_dyn.level;
    // The median of the last frames drawn at this level
    double ms[16];
    uint32_t n = 0;
    std::lock_guard<std::mutex> lock(g_dyn.samplesMutex);
    for (uint32_t i = 0; i < std::min(g_dyn.sampleCount, 16u); i++)
        if ((g_dyn.samples[i].level ? g_dyn.samples[i].level : kLevelSteps) == now) ms[n++] = g_dyn.samples[i].ms;
    if (n < 5) return g_dyn.level;
    std::nth_element(ms, ms + n / 2, ms + n);
    double gpu = ms[n / 2], t = target();
    auto expected = [&](uint32_t level) {
        double r = (double)level / now;
        return gpu * (kFixedShare + (1 - kFixedShare) * r * r);
    };
    // The grid from the full size down, as fractions of 64
    std::vector<uint32_t> grid = levelGrid();
    for (uint32_t& l : grid) if (!l) l = kLevelSteps;
    uint32_t want = now;
    if (gpu > t) {
        // Down: the highest level below this one expected under the target, or the lowest
        want = grid.back();
        for (uint32_t l : grid)
            if (l < now && expected(l) <= t * 0.95) { want = l; break; }
    } else if (gpu < t * 0.85) {
        // Up one step at a time (the model's fixed share is a guess), when it is expected to stay under the target
        for (auto it = grid.rbegin(); it != grid.rend(); ++it)
            if (*it > now) {
                if (expected(*it) <= t * 0.9) want = *it;
                break;
            }
    }
    return want >= kLevelSteps ? 0 : want;
}

float kmtlDynamicMinContent() {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    return g_dyn.width && g_dyn.minLevel ? (float)g_dyn.minLevel / kLevelSteps : 1.f;
}

extern "C" {
// The hooks, after each rebuild of the main view's targets (and before they are made): its size, and the lowest level
// they will ask for (of 64; MetalFX is made for it); 0 x 0 turns dynamic resolution off for images bound after it
__attribute__((visibility("default"))) void ksaMtlDynamicResolution(uint32_t width, uint32_t height, uint32_t minLevel) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    // Only with an experiment's level (dynamic_level): without, nothing is registered and nothing changes
    if (!startLevel()) width = height = 0;
    g_dyn.width = width;
    g_dyn.height = height;
    // With a target, from the full size down to the lowest level; without, the experiment's level from the start
    uint32_t level = width && height && target() <= 0 ? startLevel() : 0;
    uint32_t lowest = target() > 0 ? startLevel() : level;
    // Not below what MetalFX's temporal scaler reaches from its input (3x on an M5): its output is made for the lowest
    float most = kmtlUpscaleMaxScale();
    uint32_t reach = most > 1 ? (uint32_t)std::ceil(kLevelSteps / most) : kMinLevel;
    lowest = lowest ? std::max(lowest, reach) : 0;
    if (level) level = std::max(level, reach);
    g_dyn.minLevel = !width || !height ? 0 : std::clamp(std::min(minLevel ? minLevel : kLevelSteps, lowest ? lowest : kLevelSteps), kMinLevel, kLevelSteps);
    if (g_dyn.minLevel == kLevelSteps) g_dyn.minLevel = 0;
    // Images still there from before (a rebuild that kept them, or this one's own before it makes its targets) take
    // the level now, or the hooks' sizes and the textures part
    if (level != g_dyn.level) changeLevel(level);
    {
        std::lock_guard<std::mutex> samples(g_dyn.samplesMutex);
        g_dyn.sampleCount = 0;
    }
    g_dyn.changedAt = g_dyn.frames;
    KLOG(0, "dynamic resolution: main view %ux%u, level %u of %u, lowest %u", width, height, g_dyn.level, kLevelSteps, g_dyn.minLevel);
}

// The main view's size at the level now: what the game's own sizes for it become
__attribute__((visibility("default"))) void ksaMtlDynamicResolutionSize(uint32_t* width, uint32_t* height, uint32_t* level) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    *width = scaled(g_dyn.width, g_dyn.level);
    *height = scaled(g_dyn.height, g_dyn.level);
    *level = g_dyn.level;
}

// An image's size as its texture has it now (a dynamic image's at the frame's level)
__attribute__((visibility("default"))) void ksaMtlImageSize(VkImage image, uint32_t* width, uint32_t* height) {
    auto* img = (KImage*)image;
    *width = img && img->mtl ? (uint32_t)img->mtl.width : 0;
    *height = img && img->mtl ? (uint32_t)img->mtl.height : 0;
}

// The hooks, at the start of the game's frame, before it records any of it: this frame's level, changed here, and the
// main view's size at it
__attribute__((visibility("default"))) void ksaMtlDynamicResolutionFrame(uint32_t* width, uint32_t* height, uint32_t* level) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    g_dyn.frames++;
    uint32_t want = g_dyn.level;
    uint32_t cycle = (uint32_t)kmtlConfig().dynamicCycle;
    if (g_dyn.width && cycle && startLevel())
        want = (g_dyn.frames / cycle) % 2 ? 0 : startLevel();
    else if (g_dyn.width && target() > 0)
        want = chooseLevel();
    if (g_dyn.width && !g_dyn.prepared && !g_dyn.images.empty()) {
        auto t0 = std::chrono::steady_clock::now();
        std::vector<uint32_t> levels = target() > 0 ? levelGrid() : std::vector<uint32_t>{0, startLevel()};
        prepareLevels(levels);
        g_dyn.prepared = true;
        KLOG(0, "dynamic resolution: %zu images at %zu levels made in %.1f ms", g_dyn.images.size(), levels.size(),
             std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    }
    if (want != g_dyn.level) {
        changeLevel(want);
        g_dyn.changedAt = g_dyn.frames;
    }
    *width = scaled(g_dyn.width, g_dyn.level);
    *height = scaled(g_dyn.height, g_dyn.level);
    *level = g_dyn.level;
}
}
