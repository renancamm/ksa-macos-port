// KSA Metal's pipelines (pipelines/): shader modules, render passes, framebuffers and the pipelines, and what the
// other layers ask of them. Included by kmtl.h; stages.h has what only the pipeline files share.
#pragma once

struct KShaderModule {
    std::vector<uint32_t> spirv;
    uint64_t hash = 0;
};

struct KRenderPass {
    std::vector<VkAttachmentDescription2> attachments;
    struct Subpass {
        std::vector<int> colors;    // attachment index or -1
        std::vector<int> resolves;  // attachment index or -1
        int depth = -1;
        int depthResolve = -1;
        uint32_t viewMask = 0;
    };
    std::vector<Subpass> subpasses;
};

struct KFramebuffer {
    std::vector<KImageView*> attachments;
    uint32_t width = 0, height = 0, layers = 1;
};

// The compute stages of a tessellation pipeline: the vertex shader as a kernel per index type (none, 16-bit, 32-bit)
// and the control shader as a multi-patch kernel, with the byte strides of the records they write (per vertex, per
// control point, per patch)
struct KTessStages {
    id<MTLComputePipelineState> vertex[3] = {nil, nil, nil};
    id<MTLComputePipelineState> control = nil;
    NSUInteger vertexStride = 0, controlStride = 0, patchStride = 0;
};

struct KPipeline {
    // Graphics
    id<MTLRenderPipelineState> render = nil;
    id<MTLDepthStencilState> depthStencil = nil;
    MTLPrimitiveType primitive = MTLPrimitiveTypeTriangle;
    bool triangleFan = false;  // emulated with a generated index buffer
    // Tessellation: the plain stages, and, when the control stage is a passthrough, the fused ones (32-bit indexed
    // draws; KSA_MTL_FUSED_TESSELLATION=0 turns them off): their evaluation stage runs the vertex shader on
    // attributes Metal fetches per control point, so their kernels only write what the tessellation factors need
    bool tess = false;
    KTessStages tessPlain, tessFused;
    id<MTLRenderPipelineState> renderFused = nil;
    id<MTLRenderPipelineState> renderEarly = nil;  // its fragment function tests depth first (kmtlEarlyFragmentTests)
    uint32_t tessInCP = 3, tessOutCP = 3;
    bool tessQuads = false;
    bool tessPassthrough = false;  // control stage writes only tessellation factors; evaluation reads the vertex outputs
    MTLCullMode cull = MTLCullModeNone;
    MTLWinding winding = MTLWindingCounterClockwise;
    MTLTriangleFillMode fill = MTLTriangleFillModeFill;
    MTLDepthClipMode depthClip = MTLDepthClipModeClip;
    bool depthBiasEnable = false;
    float depthBias[3] = {0, 0, 0};  // constant, slope, clamp
    bool dynamicViewport = false, dynamicScissor = false, dynamicDepthBias = false, dynamicStencilRef = false,
         dynamicBlendConstants = false;
    std::vector<VkViewport> viewports;
    std::vector<VkRect2D> scissors;
    uint32_t stencilRef[2] = {0, 0};
    float blendConstants[4] = {0, 0, 0, 0};
    bool vertexUsesDrawId = false;
    bool fragmentUsesDrawId = false;
    uint32_t viewMask = 0;
    // Compute (ray tracing pipelines run as compute kernels, rtpipeline.mm)
    id<MTLComputePipelineState> compute = nil;
    MTLSize workgroup = MTLSizeMake(1, 1, 1);
    bool computeUsesDrawId = false;
    // Asynchronous creation: render / compute are set by a worker; binding the pipeline waits for it, and once it is
    // done every later bind sees that without the lock
    struct Pending {
        std::mutex m;
        std::condition_variable cv;
        std::atomic<bool> done{false};  // stored with release after the state, under the lock
    };
    std::shared_ptr<Pending> pending;
    void wait() {
        if (!pending || pending->done.load(std::memory_order_acquire)) return;
        std::unique_lock<std::mutex> lock(pending->m);
        pending->cv.wait(lock, [&] { return pending->done.load(std::memory_order_acquire); });
    }
    // GPU profiler name: shader module hashes
    uint64_t profId = 0;
    std::string profName;
    int throttle = -1;  // KSA_MTL_THROTTLE: run every N-th present (0 never, -1 always)
    bool overlap = false;  // KSA_MTL_OVERLAP: a compute pipeline that doesn't wait for earlier work
};
// KSA_MTL_THROTTLE (setup/config.mm): a pipeline whose name holds one of its module hashes runs every N-th present
int kmtlThrottleFor(const std::string& profName);
// KSA_MTL_OVERLAP (setup/config.mm): whether a pipeline's name holds one of its module hashes
bool kmtlOverlapFor(const std::string& profName);
// KSA_MTL_THREADGROUP_HINT for a compute module (config.mm): at least this many threads per threadgroup, 0 = none
uint32_t kmtlThreadgroupHintFor(uint64_t hash);
inline bool kmtlThrottled(const KPipeline* p) {
    if (!p || p->throttle < 0) return false;
    return p->throttle == 0 || kmtlPresentCount() % (uint64_t)p->throttle != 0;
}

// stages.mm
id<MTLLibrary> kmtlCompileStageMSL(KDevice* dev, const std::string& msl, std::string* err);
// stages.mm: threads per shader-compile pool (the pipeline workers, and the prewarm's own): all cores but two; the
// two pools can overlap while the game loads
unsigned kmtlWorkerCount();

void kmtlPipelineCounts(uint32_t* shaders, uint32_t* pipelines);  // stages.mm: MSL compiled (not from the cache), states made
void kmtlReportPipelineTimes();  // stages.mm: the pipelines' creation cost, with the first in-game frame

// Ray tracing pipelines (rtpipeline.mm): the size of a shader group handle, which the device properties report, and
// the compute pipeline a ray tracing pipeline runs as (pipelines.mm)
uint32_t kmtlRtGroupHandleSize();
KPipeline* kmtlCreateComputeFromStage(KDevice* dev, KPipelineLayout* layout, const VkPipelineShaderStageCreateInfo* stage,
                                      bool rtLaunch);
// A ray tracing kernel's MSL with its traces as intersector calls (rtpipeline.mm), or unchanged when a trace doesn't
// have the expected shape
std::string kmtlRtIntersectors(const std::string& msl);

// The end every vkCreate*Pipelines call shares (pipelines.mm): failed pipelines, KSA_MTL_STRICT_PIPELINES
VkResult kmtlFinishPipelines(uint32_t n, VkPipeline* out);
