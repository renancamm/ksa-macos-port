// KSA Metal's command buffers (commands/): pools, the command buffer's recording state, and the internal shaders
// they use. Included by kmtl.h.
#pragma once

// ---------------------------------------------------------------------------------------------------------------
// Command buffers
struct KCommandPool {
    KDevice* device = nullptr;
    KQueue* queue = nullptr;  // of the pool's queue family
    std::vector<struct KCommandBuffer*> buffers;
};

struct KBoundSet {
    id<MTLBuffer> buffer = nil;
    NSUInteger offset = 0;
    VkShaderStageFlags stages = 0;  // the layout's: a graphics set is bound to the functions that read it
    // For a pass dump: its slots as the GPU reads them (dynamic offsets added) and their layout, valid while recording
    const uint8_t* slots = nullptr;
    KSetLayout* layout = nullptr;
};

struct KUploadRing {
    // A chunk's properties are read once when it is made: placing data in it then sends the buffer no message (the
    // game's threads have no autorelease pool, and a message that autoreleases the buffer would leak it)
    struct Chunk { id<MTLBuffer> buffer = nil; uint8_t* contents = nullptr; uint64_t gpuAddress = 0; size_t length = 0; };
    std::vector<Chunk> chunks;
    size_t chunk = 0;
    size_t offset = 0;
    static constexpr size_t kChunkSize = 1 << 20;
    struct Place { id<MTLBuffer> buffer; uint64_t gpuAddress; NSUInteger offset; void* ptr; };
    Place alloc(KDevice* dev, size_t size);  // `size` bytes aligned to 256: their buffer, its GPU address, offset, CPU pointer
    void reset() { chunk = 0; offset = 0; }
};

struct KCommandBuffer : KDispatchable {
    KDevice* device = nullptr;
    KQueue* queue = nullptr;  // the queue of its pool's family: encoders order against that queue's chain fence
    id<MTLCommandBuffer> mtl = nil;
    id<MTLRenderCommandEncoder> render = nil;
    id<MTLComputeCommandEncoder> compute = nil;
    id<MTLBlitCommandEncoder> blit = nil;
    KUploadRing ring;
    bool submittedOnce = false;

    // Deferred store actions: with tessellation on, render passes start with MTLStoreActionUnknown so a pass can be
    // suspended for the compute stages (store everything) and resumed (load everything); the real store actions are
    // applied when the pass really ends
    MTLRenderPassDescriptor* passDesc = nil;
    MTLStoreAction colorStores[8] = {}, depthStore = MTLStoreActionStore, stencilStore = MTLStoreActionStore;
    bool deferredStores = false, suspending = false;

    // Encoder ordering (encoders.mm): every encoder has stage classes (roles); each class keeps the fence of its last
    // encoder, and a Vulkan barrier makes later encoders of its destination classes wait on the last fences of its
    // source classes. Command buffers start after and end before the queue's chain fence.
    enum : uint32_t { kClassCompute = 1, kClassTransfer = 2, kClassAccel = 4, kClassVertex = 8, kClassFragment = 16, kClassAll = 31 };
    id<MTLFence> lastFence[5] = {};
    uint32_t needMask[5] = {};
    uint32_t encRoles = 0;
    std::vector<id<MTLFence>> encWaited;  // fences the open encoder waited on
    std::vector<id<MTLFence>> fencePool;
    size_t fenceNext = 0;
    // MSAA resolves: a render pass's fences can signal before its resolve store actions have written the resolve
    // textures, so a pass that resolves also signals this event, and the next compute or blit encoder that depends on
    // fragment work (and the end of the command buffer) waits for it
    id<MTLEvent> resolveEvent = nil;
    uint64_t resolveSignaled = 0, resolveWaited = 0;
    bool passResolves = false;
    // vkCmdUpscaleKSA (upscale.mm): full barriers around MetalFX
    id<MTLEvent> fxEvent = nil;
    uint64_t fxValue = 0;
    uint32_t fxSnapshots = 0;  // the stages vkCmdUpscaleSnapshotKSA copied in this recording (a bit each)
    void waitForResolves(uint32_t roles);  // an encoder of these roles that depends on fragment work
    void waitForAllResolves();             // whatever comes next (the end of recording, MetalFX)
    void beginCommands();
    void joinCommands();
    id<MTLFence> newFence();
    template <typename E> void waitForRoles(E enc, uint32_t roles);
    id<MTLFence> finishFence(uint32_t roles);  // a new fence that becomes the last fence of these classes
    void barrierClasses(uint32_t src, uint32_t dst);

    // Render pass state
    uint32_t rtWidth = 0, rtHeight = 0;
    bool inRenderPass = false;
    KRenderPass* renderPass = nullptr;
    KFramebuffer* framebuffer = nullptr;
    uint32_t subpass = 0;

    // Bound state
    KPipeline* graphicsPipeline = nullptr;
    KPipeline* computePipeline = nullptr;
    KBoundSet graphicsSets[kMaxSets], computeSets[kMaxSets];
    uint32_t graphicsSetsDirty = 0, computeSetsDirty = 0;
    uint8_t push[kMaxPushConstants];
    uint32_t pushSize = 0;
    bool graphicsPushDirty = true, computePushDirty = true;
    struct VB { id<MTLBuffer> buf = nil; NSUInteger off = 0; VkDeviceSize size = 0; };
    VB vertexBuffers[16];
    uint32_t vertexBuffersDirty = 0;
    id<MTLBuffer> indexBuffer = nil;
    NSUInteger indexOffset = 0;
    VkDeviceSize indexBytes = 0;  // bytes of the bound VkBuffer from the binding offset
    MTLIndexType indexType = MTLIndexTypeUInt16;
    std::vector<VkViewport> viewports;
    std::vector<VkRect2D> scissors;
    bool viewportDirty = false, scissorDirty = false;
    float depthBias[3] = {0, 0, 0};
    bool depthBiasDirty = false;
    float blendConstants[4] = {0, 0, 0, 0};  // dynamic state, applied when the pipeline declares it dynamic
    uint32_t stencilRef[2] = {0, 0};         // front, back
    bool blendConstantsDirty = false, stencilRefDirty = false;
    bool pipelineDirty = false;
    KPipeline* encodedPipeline = nullptr;

    // Timestamp queries: one counter sample per write (end of a tiny ordered encoder); writes inside a render pass
    // are taken when the pass ends
    struct TimestampWrite { KQueryPool* pool; uint32_t query; uint32_t sample; };
    id<MTLCounterSampleBuffer> tsSamples = nil;
    uint32_t tsUsed = 0;
    std::vector<TimestampWrite> tsWrites;
    std::vector<std::pair<KQueryPool*, uint32_t>> tsDeferred;
    void writeTimestamp(KQueryPool* pool, uint32_t query);
    void submitTimestamps(id<MTLCommandBuffer> c);
    void releaseTimestamps();

    // Counters for the frame log
    uint32_t draws = 0, dispatches = 0, passes = 0;

    // GPU profiler (profile.mm): set while this recording is profiled, null otherwise, so the hooks cost one test
    KProfState* prof = nullptr;

    // KSA_MTL_LABELS=1: the open encoder's name, from the pipelines it ran (the game sets no debug labels in its
    // release builds); Instruments, the Metal HUD and Xcode's frame capture show it
    std::string encLabel;
    uint32_t encPipelines = 0;
    uint64_t encLastPipeline = 0;
    void notePipelineLabel(KPipeline* p);
    // Pass dump (passdump.mm), while a frame is dumped: the open pass's descriptor and its first load actions, its name (the
    // first encoder's, as it may suspend and resume), its draws so far, whether a suspension resolves its images, whether the
    // recording is being dropped
    MTLRenderPassDescriptor* dumpDesc = nil;
    MTLLoadAction dumpLoads[8] = {}, dumpDepthLoad = MTLLoadActionDontCare;
    std::string dumpName;
    uint32_t dumpDraws = 0;
    bool dumpResolves = false, dumpDrawsSkipped = false, dropping = false;
    template <typename E> void nameEncoder(E enc);

    void endEncoder();
    id<MTLComputeCommandEncoder> computeEncoder(uint32_t roles = kClassCompute);
    id<MTLBlitCommandEncoder> blitEncoder();
    void beginRender(MTLRenderPassDescriptor* rp, uint32_t w, uint32_t h, bool resuming = false,
                     uint32_t roles = kClassVertex | kClassFragment);
    void suspendRender();
    void resumeRender();
    void endRender();
    void resetState();
};

void kmtlZeroImage(KImage* img);  // transfers.mm: a new color image cleared to zero at bind time

// Internal shaders (fills, image blits, the prep kernel of indirect tessellated draws)
void kmtlInitInternal(KDevice* dev);
void kmtlInternalFillBuffer(KCommandBuffer* cb, id<MTLBuffer> buf, NSUInteger offset, NSUInteger size, uint32_t data);
void kmtlInternalBlit(KCommandBuffer* cb, KImage* src, KImage* dst, const VkImageBlit2& region, VkFilter filter);
id<MTLComputePipelineState> kmtlTessPrepPipeline(KDevice* dev);
id<MTLBuffer> kmtlTessStats(KDevice* dev);
uint32_t kmtlTessCap(KDevice* dev);

// What only the command files share (encoders.mm, state.mm, draws.mm, passes.mm, transfers.mm)
static const MTLRenderStages kAllRenderStages = MTLRenderStageVertex | MTLRenderStageFragment;

// KSA_MTL_STRICT_SYNC=1: every encoder is of every class and every barrier covers every class, so encoders run in
// submission order
inline bool strictSync() { return kmtlConfig().strictSync; }

// Multiview: one draw per view in the mask, with spvViewMask = {view, 1}
template <typename F> void kmtlForEachView(KCommandBuffer* cb, F&& draw) {
    uint32_t mask = cb->graphicsPipeline->viewMask;
    if (!mask) { draw(); return; }
    while (mask) {
        uint32_t v = __builtin_ctz(mask);
        mask &= mask - 1;
        uint32_t vm[2] = {v, 1};
        [cb->render setVertexBytes:vm length:sizeof(vm) atIndex:kViewMaskBufferIndex];
        [cb->render setFragmentBytes:vm length:sizeof(vm) atIndex:kViewMaskBufferIndex];
        draw();
    }
}

uint32_t kmtlStageClasses(VkPipelineStageFlags2 m, bool src);  // encoders.mm
bool kmtlFlushGraphics(KCommandBuffer* cb);  // state.mm
void kmtlSetDrawId(KCommandBuffer* cb, uint32_t id);  // state.mm
bool kmtlFlushCompute(KCommandBuffer* cb);  // state.mm
MTLLoadAction kmtlLoadAction(VkAttachmentLoadOp op);  // passes.mm
MTLStoreAction kmtlStoreAction(VkAttachmentStoreOp op, bool resolve);  // passes.mm
MTLClearColor kmtlClearColor(VkFormat f, const VkClearColorValue& c);  // passes.mm
