// SPIR-V -> MSL translation of one shader stage (translate.mm): the part of pipeline creation that needs no device.
// pipelines/stages.mm translates every stage with it, and the driver's tests (tests/) run it on recorded inputs.
#pragma once
#include "kmtl.h"
#include "spirv_msl.hpp"

using KIndexType = SPIRV_CROSS_NAMESPACE::CompilerMSL::Options::IndexType;

// Per-stage translation options
enum class KTessRole { None, VertexKernel, ControlKernel, Evaluation };

struct KStageOptions {
    uint32_t fragMask = 0xffffffff;
    bool sampleRate = false;
    uint32_t viewMask = 0;
    KTessRole tess = KTessRole::None;
    KIndexType indexType = KIndexType::None;
    // Tessellation modes merged from both stages (each declares only part of them)
    uint32_t tessControlPoints = 0;
    bool tessQuads = false;
    bool rtLaunch = false;  // ray tracing kernel: launch size uniform at set 15 -> kRtLaunchBufferIndex
    bool tessFusedInput = false;  // fused evaluation stage: control points as stage-in attributes, not a raw buffer
};

// Everything that shapes a stage's translation, hashed: translations with the same key are the same
uint64_t kmtlTranslationKey(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KPipelineLayout* layout,
                            const VkSpecializationInfo* spec, const KStageOptions& so);
// The stage's MSL, its entry point's name and what pipeline creation needs to know about it; false with *err when
// SPIRV-Cross fails
bool kmtlTranslate(KShaderModule* mod, const char* entryName, spv::ExecutionModel model, KPipelineLayout* layout,
                   const VkSpecializationInfo* spec, const KStageOptions& so, KTranslated* out, std::string* err);
// The tessellation execution modes of one stage module
void kmtlTessModes(KShaderModule* mod, const char* entry, spv::ExecutionModel model, KTranslated* out);
// Translations made so far and the time they took, in microseconds (kmtlReportPipelineTimes)
void kmtlTranslateStats(int* count, int64_t* us);

// The shader corpus (corpus.mm, KSA_MTL_CORPUS=<dir>): each translation's inputs and result, recorded once per key
// while the game runs, for the driver's tests
void kmtlCorpusRecord(uint64_t key, KShaderModule* mod, const char* entry, spv::ExecutionModel model, KPipelineLayout* layout,
                      const VkSpecializationInfo* spec, const KStageOptions& so, const KTranslated& out);

// One recorded translation, read back: the inputs kmtlTranslationKey and kmtlTranslate take, and the result
struct KCorpusEntry {
    uint64_t key = 0;
    KShaderModule module;
    std::string entry;
    spv::ExecutionModel model = spv::ExecutionModelMax;
    KPipelineLayout layout;  // its set layouts are the entry's own
    bool hasLayout = false;
    std::vector<VkSpecializationMapEntry> specEntries;
    std::vector<uint8_t> specData;
    VkSpecializationInfo spec{};
    bool hasSpec = false;
    KStageOptions options;
    // The settings that shape a translation, as they were when it was recorded (KSA_MTL_AB_CONSTANT,
    // KSA_MTL_UNLIT_SHADOW_SKIP, KSA_MTL_SPEC)
    bool constantArgumentBuffers = true, unlitShadowSkip = true;
    std::string specPresets;
    KTranslated recorded;
};
// The keys of the translations recorded in <dir>, and one of them read back
std::vector<uint64_t> kmtlCorpusKeys(const std::string& dir);
bool kmtlCorpusLoad(const std::string& dir, uint64_t key, KCorpusEntry* out, std::string* err);
