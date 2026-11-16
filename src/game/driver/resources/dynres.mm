// Dynamic resolution: the main view drawn at a level of its size, frame by frame, without the game rebuilding its
// targets. Steady Frame Rate turns it on (KSA_MTL_STEADY_FRAME_RATE, a MetalFX setting); the experiments dynamic_level
// and dynamic_target_ms set its lowest level and its target instead.
//
// The hooks pass the main view's size after each rebuild (ksaMtlDynamicResolution). An image bound after it that is
// drawn into and is that size or a fraction of it (fits) is dynamic: besides its texture at its own size it gets one
// per level (n/kLevelSteps of its size), placed in the same memory, and draws, copies and views use the level's. What
// the game draws with the image then sees the smaller size, as after a rebuild: the shaders' image sizes and normalised
// coordinates, the viewports and scissors (scaled by the pass's attachments, passes.mm and state.mm), the copies
// (transfers.mm). What the game works out from its sizes itself (the cameras' screen size, push constants) the hooks
// give the level's size (ksaMtlDynamicResolutionFrame).
//
// A change of level, at the start of the game's frame, carries what each image holds over to its texture at the new
// level (scaled: the game's histories), in the queue's order after the frames before, and moves the images and their
// views to them. The descriptor sets keep the textures they were written with: a set that holds a dynamic image's
// texture is bound through a copy whose textures are the frame's level's (kmtlDynamicTranslate, from any level's
// texture to the image's or view's now), so frames still on the GPU read theirs unchanged and nothing waits for them.
//
// The level: each frame the highest whose GPU time is expected to stay under the target (chooseLevel), from the last
// frames' GPU times (kmtlDynamicNoteFrame, as each present completes), each at the full clock (gpuclock.mm: with the
// frames paced, macOS slows the GPU down to each frame's deadline, and the time measured tells the deadline). The target: 90 % of the cadence Steady Frame Rate
// shows frames at (dynamic_target_ms instead), and when even the lowest level runs over it, the next slower rate
// (stepRate). The lowest level: with MetalFX Temporal, the lowest from which MetalFX scales the main view up to the
// display no more than its temporal scaler reaches (3x on an M5), so the temporal scaler alone reaches the display at
// every level and no spatial step softens the full level; with Spatial, half the window (the hooks' least size)
// (dynamic_level instead; without a target, that level all the time).
#include "kmtl.h"
#include <cmath>
#include <map>
#include <tuple>

namespace {
struct DynamicState {
    std::mutex m;
    uint32_t width = 0, height = 0;  // the main view's size (0: none)
    std::atomic<uint32_t> outW{0}, outH{0};  // the display's: what MetalFX scales it up to (the main swapchain's size)
    std::atomic<uint32_t> level{0};  // the level now (0: full size); read at present without the lock
    uint32_t lowest = 0;             // the lowest level the frames take (0: none, dynamic resolution off)
    bool steady = false;             // Steady Frame Rate held (with Vsync) at the last registration: its rate steps
    std::vector<KImage*> images;     // the dynamic images
    // Every level's texture of a dynamic image or view, by resource ID: what a descriptor's texture is now
    struct Owner { KImage* image; KImageView* view; };
    std::unordered_map<uint64_t, Owner> owners;
    KDevice* device = nullptr;
    uint64_t frames = 0;             // frames begun (ksaMtlDynamicResolutionFrame)
    uint64_t changes = 0;
    uint64_t changedAt = 0;          // the frame of the last change
    bool steppedUp = false;          // the last change was a step up
    uint32_t upWait = 30;            // frames before a step up: doubled after one that had to come back down
    // A level a step up to which had to come back down: not stepped up to again before ceilingUntil (a still view near
    // the target would otherwise climb to it and drop every second or so), ceilingWait frames, doubled each time
    uint32_t ceiling = 0, ceilingWait = 0;
    uint64_t ceilingUntil = 0;
    // Steady Frame Rate's steps, as the levels': the frame of the last change, whether it was to a faster rate, and
    // the frames at a slower rate before a faster one is tried
    uint64_t rateChangedAt = 0;
    bool rateSteppedUp = false;      // ... and not held yet (rateUpWait * 4 frames)
    uint32_t rateUpWait = 120;
    uint64_t rateChanges = 0;
    // The last faster step's expected time at the lowest level, and how far over it the frames there ran when a faster
    // step didn't hold (the model's fixed share is a guess: in the lower atmosphere nearly all of a frame's GPU time
    // stays at any level): the expectations after it are scaled by that, fading, until a faster step holds
    double rateExpected = 0, rateBias = 1;
    bool prepared = false;           // the levels' textures made (prepareLevels) since the last image
    // The last frames' GPU times and levels, as their presents completed: a lock of their own, for completion handlers
    std::mutex samplesMutex;
    struct Sample { uint32_t level; double ms, measured; };  // ms: at the GPU's full clock
    Sample samples[16];
    uint32_t sampleCount = 0;
    // The level change's resampling: a pipeline per pixel format
    id<MTLLibrary> library = nil;
    std::unordered_map<NSUInteger, id<MTLRenderPipelineState>> resample;
};
DynamicState& g_dyn = *new DynamicState;
}

static constexpr uint32_t kLevelSteps = 64, kMinLevel = 16;
// The target's model: a share of a frame's GPU time that doesn't follow the pixel count; how many frames at a level
// decide a step down (the median of the last ones) and how long it holds before a step up (a change costs ~1 ms of the
// CPU and no frame: down at once, up only when it has stayed well under the target); the target's grid of levels
static constexpr double kFixedShare = 0.5;
static constexpr uint32_t kDownFrames = 3, kUpFrames = 30, kLevelStep = 4;
// A failed level's ceiling: frames at first and at most (about 4 and 16 s at 60 FPS)
static constexpr uint32_t kCeilingFrames = 240, kCeilingMostFrames = 960;
// Steady Frame Rate's steps: frames at a slower rate before a faster one is tried (doubled after one that had to come
// back down, to 8 times)
static constexpr uint32_t kRateUpFrames = 120;
// ... and before a slower one: the frames since the last change of level (the change's own work and the frames still in
// flight from before it read high), since the last change of rate, and how many frames at the lowest level decide it
static constexpr uint32_t kRateSettleFrames = 8, kRateHoldFrames = 30, kRateDownFrames = 5;
// Steady Frame Rate's target: this share of its cadence, leaving room for the frames that cost more than the last ones
static constexpr double kCadenceShare = 0.9;
// ... and a faster rate's: taken when the lowest level is expected under this share of its target (the times at the full
// clock leave out what doesn't follow the clock, memory's part)
static constexpr double kRateUpShare = 0.85;
// ... scaled after a try that didn't hold by how far it ran over its expectation, to this at most, fading by this a frame
// at the slower rate (half of the excess in about 20 s at 40 FPS)
static constexpr double kRateBiasMost = 2.5, kRateBiasKeep = 0.99914;

// The setting's level (16 to 63; anything else: off)
static uint32_t settingLevel() {
    uint32_t n = (uint32_t)kmtlConfig().dynamicLevel;
    return n >= kMinLevel && n < kLevelSteps ? n : 0u;
}

// The target, in GPU milliseconds a frame (0: none, the level stays): dynamic_target_ms, else Steady Frame Rate's
static double target() {
    if (kmtlConfig().dynamicTargetMs > 0) return kmtlConfig().dynamicTargetMs;
    return kmtlSteadyFps() && g_dyn.outW ? kCadenceShare * kmtlFrameMs(g_dyn.outW, g_dyn.outH) : 0;
}

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

static uint32_t mipsFor(uint32_t w, uint32_t h) {
    uint32_t n = 1;
    for (uint32_t m = std::max(w, h); m > 1; m >>= 1) n++;
    return n;
}

// Whether an image is the main view's size or a fraction of it: a 2D image or array of up to 16 (the ambient
// occlusion's four deinterleaved quarters), no larger, at least 16 pixels each way, the same shape within a pixel's
// rounding each way, or each side the main view's divided by a whole number (the exhausts' half by a quarter)
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
    uint32_t lowest = g_dyn.lowest ? g_dyn.lowest : kLevelSteps;
    for (uint32_t l = kLevelSteps - kLevelStep; l >= lowest + kLevelStep; l -= kLevelStep) grid.push_back(l);
    if (lowest < kLevelSteps) grid.push_back(lowest);
    return grid;
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
            id<MTLTexture> nv = viewAt(v, level, to);
            if (!nv) continue;
            v->mtl = nv;
            v->level = level;
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
         scaled(g_dyn.width, level), scaled(g_dyn.height, level), carries.size(),
         std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
}

// Without the lock: the present calls it holding the queue's submit lock, which a change of level takes inside it
uint32_t kmtlDynamicLevelNow() {
    return g_dyn.level.load(std::memory_order_relaxed);
}

void kmtlDynamicNoteFrame(uint32_t level, double gpuMs, uint32_t width, uint32_t height) {
    // The main window's frames only: a pop-out window's present would add a small sample at the same level
    if (width != g_dyn.outW.load(std::memory_order_relaxed) || height != g_dyn.outH.load(std::memory_order_relaxed)) return;
    std::lock_guard<std::mutex> lock(g_dyn.samplesMutex);
    if (gpuMs <= 0) return;
    g_dyn.samples[g_dyn.sampleCount++ % 16] = {level, gpuMs * kmtlGpuClockShare(), gpuMs};
}

// The GPU times of the last frames drawn at this level (of kLevelSteps), at the full clock, the newest first (completed
// presents lag the frames begun by about two); as measured, with measured
static uint32_t samplesAt(uint32_t level, double* ms, double* measured = nullptr) {
    std::lock_guard<std::mutex> lock(g_dyn.samplesMutex);
    uint32_t n = 0, count = std::min(g_dyn.sampleCount, 16u);
    for (uint32_t i = 0; i < count; i++) {
        const auto& sample = g_dyn.samples[(g_dyn.sampleCount - 1 - i) % 16];
        if ((sample.level ? sample.level : kLevelSteps) != level) continue;
        if (measured) measured[n] = sample.measured;
        ms[n++] = sample.ms;
    }
    return n;
}

static double medianOf(const double* ms, uint32_t k) {
    double m[16];
    std::copy(ms, ms + k, m);
    std::nth_element(m, m + k / 2, m + k);
    return m[k / 2];
}

// The GPU time expected at another level, from the time at this one: a fixed share and a share by the pixels
static double expectedAt(double gpu, uint32_t level, uint32_t now) {
    double r = (double)level / now;
    return gpu * (kFixedShare + (1 - kFixedShare) * r * r);
}

// Steady Frame Rate's rate, stepped with the level (under g_dyn.m; not with dynamic_target_ms, whose target is fixed):
// a slower step (60 -> 40 -> 30) when the frames at the lowest level, settled there, run over the target even at the
// GPU's full clock (they start to miss their slot), so that they keep an even rate; the faster one when the lowest
// level is expected well under that rate's target, after rateUpWait frames at this one (after one that didn't hold:
// twice as long, and expecting as much more as it ran over). Without the GPU's clock the times can't tell (they measure the deadline: an M5 at 30 FPS runs at 340-800
// MHz instead of 1620, every frame about three quarters of its slot, however light): the faster one is then tried after
// rateUpWait frames whatever they say. Before the level's choice, which then follows the rate's target
static void stepRate() {
    if (kmtlConfig().dynamicTargetMs > 0 || !kmtlSteadyFps()) return;
    // Without levels (MetalFX with no room below the render size: Spatial at half the window), the full size is the lowest
    uint32_t now = g_dyn.level ? g_dyn.level.load() : kLevelSteps, lowest = g_dyn.lowest ? g_dyn.lowest : kLevelSteps;
    double ms[16], measured[16];
    uint32_t n = samplesAt(now, ms, measured);
    if (n == 0) return;
    uint32_t outW = g_dyn.outW, outH = g_dyn.outH;
    if (now == lowest && n >= kRateDownFrames && medianOf(ms, kRateDownFrames) > target() &&
        g_dyn.frames >= g_dyn.changedAt + kRateSettleFrames && g_dyn.frames >= g_dyn.rateChangedAt + kRateHoldFrames) {
        if (kmtlSteadyStep(outW, outH, 1, false) <= 0) return;
        // A faster rate that didn't hold: the next waits longer, and expects as much more as this one ran over
        if (g_dyn.rateSteppedUp) {
            g_dyn.rateUpWait = std::min(g_dyn.rateUpWait * 2, kRateUpFrames * 8);
            if (g_dyn.rateExpected > 0)
                g_dyn.rateBias = std::min(kRateBiasMost, std::max(g_dyn.rateBias, medianOf(ms, kRateDownFrames) / g_dyn.rateExpected));
        }
        double frame = kmtlSteadyStep(outW, outH, 1, true);
        g_dyn.rateChangedAt = g_dyn.frames;
        g_dyn.ceiling = 0;  // another target: the levels as they come
        g_dyn.rateSteppedUp = false;
        KLOG(++g_dyn.rateChanges <= 20 ? 0 : 1,
             "dynamic resolution: slower, %.1f ms a frame (the lowest level at %.1f ms at the full clock, %.1f ms at %.0f MHz)",
             frame, medianOf(ms, kRateDownFrames), medianOf(measured, kRateDownFrames), kmtlGpuClockMhz());
        return;
    }
    // A faster rate that held: the wait back to the shortest, the expectations as the model has them
    if (g_dyn.rateSteppedUp && g_dyn.frames > g_dyn.rateChangedAt + 4 * g_dyn.rateUpWait) {
        g_dyn.rateUpWait = kRateUpFrames;
        g_dyn.rateBias = 1;
        g_dyn.rateSteppedUp = false;
    }
    // At the slower rate, what a try that didn't hold taught fades (the scene it ran in passes)
    if (!g_dyn.rateSteppedUp) g_dyn.rateBias = 1 + (g_dyn.rateBias - 1) * kRateBiasKeep;
    double faster = kmtlSteadyStep(outW, outH, -1, false);
    if (faster <= 0 || g_dyn.frames < g_dyn.rateChangedAt + g_dyn.rateUpWait) return;
    // The level follows (chooseLevel: down at once if the frames run over), and if even the lowest can't hold the faster
    // rate, the slower one comes back (above)
    double expected = 0;
    if (kmtlGpuClockKnown()) {
        if (n < 8) return;
        expected = expectedAt(medianOf(ms, n), lowest, now);
        if (expected * g_dyn.rateBias > kRateUpShare * kCadenceShare * faster) return;
    }
    g_dyn.rateExpected = expected;
    kmtlSteadyStep(outW, outH, -1, true);
    g_dyn.rateChangedAt = g_dyn.frames;
    g_dyn.ceiling = 0;  // another target: the levels as they come
    g_dyn.rateSteppedUp = true;
    if (expected > 0)
        KLOG(++g_dyn.rateChanges <= 20 ? 0 : 1,
             "dynamic resolution: faster, %.1f ms a frame (the lowest level expected at %.1f ms at the full clock, x%.2f; now %.1f ms at %.0f MHz)",
             faster, expected, g_dyn.rateBias, medianOf(measured, n), kmtlGpuClockMhz());
    else
        KLOG(++g_dyn.rateChanges <= 20 ? 0 : 1, "dynamic resolution: faster, %.1f ms a frame (tried after %u frames, no GPU clock)",
             faster, g_dyn.rateUpWait);
    return;
}

// The level for the next frame, by the target (under g_dyn.m): down as soon as the last kDownFrames frames drawn at
// this level ran over it (their median: one hitch alone doesn't), to the highest level expected under it; up, to the
// highest expected well under it, when the frames at this level have stayed well under it for kUpFrames frames, longer
// after a step up that had to come back down, whose level is then a ceiling for a while (the model's fixed share is a
// guess)
static uint32_t chooseLevel() {
    uint32_t now = g_dyn.level ? g_dyn.level.load() : kLevelSteps;
    double ms[16];
    uint32_t n = samplesAt(now, ms);
    if (n == 0) return g_dyn.level;
    double t = target();
    auto median = [&](uint32_t k) { return medianOf(ms, k); };
    std::vector<uint32_t> grid = levelGrid();
    for (uint32_t& l : grid) if (!l) l = kLevelSteps;
    if (now > grid.back()) {
        if (n >= kDownFrames && median(kDownFrames) > t) {
            // Down: the highest level below this one expected under the target, or the lowest. A step up that didn't
            // hold (as long as it takes to count as held, below) was a mistake of the model's: the next waits longer
            // (else a still view near the target climbs and drops again every second or so, its sharpness pumping)
            if (g_dyn.steppedUp && g_dyn.frames <= g_dyn.changedAt + 2 * kUpFrames) {
                g_dyn.upWait = std::min(g_dyn.upWait * 2, kUpFrames * 8);
                // ... and this level is a ceiling for a while, longer each time it fails again
                // (a repeat: the same level failing again within one more wait of its last ceiling's end)
                bool again = g_dyn.ceiling == now && g_dyn.frames < g_dyn.ceilingUntil + g_dyn.ceilingWait;
                g_dyn.ceilingWait = again ? std::min(g_dyn.ceilingWait * 2, kCeilingMostFrames) : kCeilingFrames;
                g_dyn.ceiling = now;
                g_dyn.ceilingUntil = g_dyn.frames + g_dyn.ceilingWait;
            }
            double gpu = median(kDownFrames);
            uint32_t want = grid.back();
            for (uint32_t l : grid) {
                if (l < now && expectedAt(gpu, l, now) <= t * 0.95) { want = l; break; }
            }
            return want >= kLevelSteps ? 0 : want;
        }
    }
    // A step up that held: the wait back to the shortest; at the ceiling or above, the ceiling gone
    if (g_dyn.steppedUp && g_dyn.frames > g_dyn.changedAt + 2 * kUpFrames) {
        g_dyn.upWait = kUpFrames;
        if (g_dyn.ceiling && now >= g_dyn.ceiling) g_dyn.ceiling = 0;
    }
    if (g_dyn.frames < g_dyn.changedAt + g_dyn.upWait || n < 8) return g_dyn.level;
    double gpu = median(n);
    if (gpu >= t * 0.85) return g_dyn.level;
    // Up to the highest level expected to stay under the target, below a ceiling: in one change, which a still view
    // shows once instead of at every step. Upwards the model errs on the safe side where more of a frame stays at any
    // level than its fixed share (the lower atmosphere: nearly all); where less, the step comes back down and its level
    // becomes a ceiling
    uint32_t best = 0;
    for (auto it = grid.rbegin(); it != grid.rend(); ++it) {
        if (*it <= now) continue;
        if (g_dyn.ceiling && *it >= g_dyn.ceiling && g_dyn.frames < g_dyn.ceilingUntil) break;
        if (expectedAt(gpu, *it, now) > t * 0.9) break;
        best = *it;
    }
    if (best) return best >= kLevelSteps ? 0 : best;
    return g_dyn.level;
}

float kmtlDynamicMinContent() {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    return g_dyn.width && g_dyn.lowest ? (float)g_dyn.lowest / kLevelSteps : 1.f;
}

extern "C" {
// The hooks, after each rebuild of the main view's targets (and before they are made): its size (0 x 0: none), the
// display's, and the least size a level may have (0 x 0: the display's rule). Only with Steady Frame Rate (its frame
// time, with Vsync) or the experiment's level: without, nothing is registered and nothing changes
__attribute__((visibility("default"))) void ksaMtlDynamicResolution(uint32_t width, uint32_t height, uint32_t outW,
                                                                    uint32_t outH, uint32_t leastW, uint32_t leastH) {
    std::lock_guard<std::mutex> lock(g_dyn.m);
    g_dyn.outW = outW;
    g_dyn.outH = outH;
    // Steady Frame Rate's, with a swapchain of the display's size to take its frame time from (with Vsync); or the
    // experiment's level. Vsync turned on later waits for the next rebuild, levels and rate steps alike
    bool steady = kmtlSteadyFps() && kmtlFrameMs(outW, outH) > 0;
    g_dyn.steady = steady;
    if (!settingLevel() && !steady) width = height = 0;
    g_dyn.width = width;
    g_dyn.height = height;
    // Not below what MetalFX's temporal scaler reaches from its input (3x on an M5): its output is made for the lowest,
    // one step over it, as a level's size is rounded
    float most = kmtlUpscaleMaxScale();
    uint32_t reach = most > 0 ? (uint32_t)std::ceil(kLevelSteps / most) + 1 : kMinLevel;
    uint32_t lowest = settingLevel();
    if (!lowest && width && height && leastW && leastH) {
        // The hooks' least size (MetalFX Spatial: half the window, the Performance card's size before)
        lowest = kLevelSteps;
        for (uint32_t l = kMinLevel; l < kLevelSteps; l++)
            if (scaled(width, l) >= leastW && scaled(height, l) >= leastH) { lowest = l; break; }
    } else if (!lowest && width && height) {
        // The display's: the lowest level whose size the temporal scaler takes to the display by itself (none: no room
        // below the render size, the frame rate is held by the cadence alone)
        // As upscale.mm's prepare() works out the temporal scaler's output: from the level's size, rounded
        auto reaches = [&](uint32_t size, uint32_t out, uint32_t l) { return (uint32_t)(scaled(size, l) * most) >= out; };
        lowest = kLevelSteps;
        for (uint32_t l = kMinLevel; l < kLevelSteps && most > 0; l++)
            if (reaches(width, outW, l) && reaches(height, outH, l)) { lowest = l; break; }
    }
    g_dyn.lowest = width && height ? std::max(lowest, reach) : 0;
    if (g_dyn.lowest >= kLevelSteps) g_dyn.lowest = g_dyn.width = g_dyn.height = 0;
    // The GPU's clock, for the times of frames paced by Steady Frame Rate (gpuclock.mm)
    if (steady) kmtlGpuClockStart();
    // With a target from the full size, without one at the lowest level. Images still there from before (a rebuild
    // that kept them) take it now, or the hooks' sizes and the textures would part
    uint32_t level = target() > 0 ? 0 : g_dyn.lowest;
    if (level != g_dyn.level) changeLevel(level);
    {
        std::lock_guard<std::mutex> samples(g_dyn.samplesMutex);
        g_dyn.sampleCount = 0;
    }
    g_dyn.changedAt = g_dyn.rateChangedAt = g_dyn.frames;
    // A new start: no step up of before to fail (the first frames after a rebuild often run over), no ceiling
    g_dyn.steppedUp = false;
    g_dyn.upWait = kUpFrames;
    g_dyn.ceiling = 0;
    g_dyn.rateSteppedUp = false;
    g_dyn.rateUpWait = kRateUpFrames;
    g_dyn.rateExpected = 0;
    g_dyn.rateBias = 1;
    KLOG(0, "dynamic resolution: main view %ux%u to %ux%u, level %u of %u, lowest %u (%s), target %.1f ms", width, height,
         outW, outH, g_dyn.level.load(), kLevelSteps, g_dyn.lowest, settingLevel() ? "dynamic_level" : leastW ? "half the window" : "the display's",
         target());
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
    // The rate first, whose target the level then follows (also without levels: stepRate). Without a target (no
    // swapchain of the display's size: the window's changed before the rebuild): the experiment's level, or the full size
    if (g_dyn.steady && target() > 0) stepRate();
    uint32_t want = !g_dyn.width ? g_dyn.level.load() : target() > 0 ? chooseLevel() : settingLevel() ? g_dyn.lowest : 0;
    if (g_dyn.width && !g_dyn.prepared && !g_dyn.images.empty()) {
        auto t0 = std::chrono::steady_clock::now();
        std::vector<uint32_t> levels = target() > 0 ? levelGrid() : std::vector<uint32_t>{0, g_dyn.lowest};
        prepareLevels(levels);
        g_dyn.prepared = true;
        KLOG(0, "dynamic resolution: %zu images at %zu levels made in %.1f ms", g_dyn.images.size(), levels.size(),
             std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count());
    }
    if (want != g_dyn.level) {
        uint32_t before = g_dyn.level ? g_dyn.level.load() : kLevelSteps;
        if (g_logLevel >= 1) {
            double ms[16], measured[16];
            uint32_t n = samplesAt(before, ms, measured);
            KLOG(1, "dynamic resolution: frame %llu, %u -> %u after %llu frames (target %.1f ms, wait %u): newest %.1f, %.1f, %.1f ms (%.1f, %.1f, %.1f measured)",
                 (unsigned long long)g_dyn.frames, before, want ? want : kLevelSteps,
                 (unsigned long long)(g_dyn.frames - g_dyn.changedAt), target(), g_dyn.upWait, n > 0 ? ms[0] : 0.0,
                 n > 1 ? ms[1] : 0.0, n > 2 ? ms[2] : 0.0, n > 0 ? measured[0] : 0.0, n > 1 ? measured[1] : 0.0,
                 n > 2 ? measured[2] : 0.0);
        }
        g_dyn.steppedUp = (want ? want : kLevelSteps) > before;
        changeLevel(want);
        g_dyn.changedAt = g_dyn.frames;
    }
    *width = scaled(g_dyn.width, g_dyn.level);
    *height = scaled(g_dyn.height, g_dyn.level);
    *level = g_dyn.level;
}
}
