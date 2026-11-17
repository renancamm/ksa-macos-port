// The game's GPU profiler as KSA Metal serves it: how its timestamps are answered (taken by commands/timestamps.mm), and
// the encoders named after its zones.
//
//   ksaMtlZoneMode(mode)    how the game's timestamps are answered from the next one on:
//                           0 as they come: each one ends the encoder and waits for everything before it, through an
//                             event (an encoder with counter samples doesn't keep the fences' order); one inside a
//                             render pass is taken when the pass ends, so a zone inside a pass reads about 0
//                           1 labels: answered at once with 0, no encoder and no wait; the game's profiler then only
//                             names the encoders (with labels)
//                           2 split: as 0, but one inside a render pass splits the pass at its next draw (suspended
//                             and resumed), so zones inside it read their own time; needs gpu_zones at launch, which
//                             also gives the attachments kept in tile memory memory of their own (false without it)
//   ksaMtlZoneBegin(commandBuffer, name), ksaMtlZoneEnd(commandBuffer)
//                           a zone of the game's GPU profiler begins or ends in this recording; with labels, an encoder
//                           is named after the zone its first draw or dispatch ran in
#include "kmtl.h"

static std::atomic<uint32_t> g_zoneMode{kZonesAsTheyCome};

uint32_t kmtlZoneMode() { return g_zoneMode.load(std::memory_order_relaxed); }

KMTL_EXPORT bool ksaMtlZoneMode(uint32_t mode) {
    if (mode > kZonesSplit || (mode == kZonesSplit && !kmtlConfig().gpuZones)) return false;
    g_zoneMode = mode;
    return true;
}

KMTL_EXPORT void ksaMtlZoneBegin(VkCommandBuffer commandBuffer, const char* name) {
    if (commandBuffer && name) ((KCommandBuffer*)commandBuffer)->zones.push_back(name);
}

KMTL_EXPORT void ksaMtlZoneEnd(VkCommandBuffer commandBuffer) {
    auto* cb = (KCommandBuffer*)commandBuffer;
    if (cb && !cb->zones.empty()) cb->zones.pop_back();
}

// A draw or dispatch with this pipeline in the current encoder: its name, and a pass dump's image after the draw
void kmtlNoteDraw(KCommandBuffer* cb, KPipeline* p, bool dispatch) {
    if (!kmtlConfig().labels && !kmtlPassDumpRecording()) return;  // the common case: two predictable branches per draw
    cb->notePipelineLabel(p);  // a pass dump names its passes
    if (!dispatch && cb->render && cb->deferredStores && cb->dumpDesc) kmtlPassDumpAfterDraw(cb, p);
}
