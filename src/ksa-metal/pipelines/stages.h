// What only the pipeline files share: stages.mm makes a stage ready, pipelines.mm and rtpipeline.mm build pipelines
// from stages.
#pragma once
#include "kmtl.h"
#include "shaders/translate.h"
#include <functional>

// A stage translated (or from the disk cache) and overridden; *outName is its dump and override name. Needs the create
// info, so it runs on the calling thread
bool kmtlPrepareStage(const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model, KPipelineLayout* layout,
                      const KStageOptions& so, KTranslated* tr, std::string* outName);
// A translated stage's function: rtLaunch, a ray tracing kernel; earlyTests, a fragment function with early fragment
// tests (kmtlStoresFromFragment)
id<MTLFunction> kmtlCompileStage(KDevice* dev, const KTranslated& t, const std::string& nameStr, bool rtLaunch = false,
                                 bool earlyTests = false);
// Both: a stage ready and compiled
id<MTLFunction> kmtlMakeFunction(KDevice* dev, const VkPipelineShaderStageCreateInfo* st, spv::ExecutionModel model,
                                 KPipelineLayout* layout, const KStageOptions& so, KTranslated* tr);
// Whether a fragment shader's every store is to the selection mask, so it gets a state with early fragment tests
bool kmtlStoresFromFragment(const std::string& msl);
// A pipeline's compile, on the pipeline workers
void kmtlRunPipelineJob(std::function<void()> job);
// A pipeline state made, since <started>: its cost goes to kmtlReportPipelineTimes
void kmtlNotePipelineState(std::chrono::steady_clock::time_point started);
