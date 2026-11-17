// The Frame Rate section's controller: Steady Frame Rate's rate, stepped 60 -> 40 -> 30 and back by Lower Rate Under
// Heavy Load (stepRate), and with Dynamic Resolution the level of its size the main view is drawn at, frame by frame
// (chooseLevel). The cadence that holds the rate is pacing.mm's; the mechanism that draws at a level, dynres.mm's.
//
// The hooks pass the main window's swapchain and the main view's size after each rebuild (ksaMtlFrameRateView) and call
// in at the start of each of the game's frames, before it records any of it (ksaMtlFrameRateBeginFrame): the rate's
// step, then the level, changed there, and the main view's size at it, which the game's own sizes take. Steady Frame
// Rate alone registers the swapchain only, for the rate's steps.
//
// The level: each frame the highest whose GPU time is expected to stay under the target, from the last frames' GPU
// times (kmtlFrameRateNoteFrame, as each present completes), each at the full clock (gpuclock.mm: with the frames paced,
// macOS slows the GPU down to each frame's deadline, and the time measured tells the deadline). The target: 90 % of the
// cadence Steady Frame Rate shows frames at, and when even the lowest level runs over it, the next slower rate. The
// lowest level: with MetalFX Temporal, the lowest from which MetalFX scales the main view up to the display no more than
// its temporal scaler reaches (3x on an M5), so the temporal scaler alone reaches the display at every level and no
// spatial step softens the full level; with Spatial, the hooks' least size (half the window, or 3/4 of the render size
// where that is lower); the experiment dynamic_level instead (without a target, that level all the time).
//
// Levels here are in parts of the main view's size (kDynamicParts: the full size); dynres.mm, the samples and the hooks
// count the full size as 0 (parts and levelOf convert).
#include "kmtl.h"
#include <cmath>

// The target's model: a share of a frame's GPU time that doesn't follow the pixel count
static constexpr double kFixedShare = 0.5;
// A step down: when the median of the last frames at a level runs over the target, to the highest level expected under
// this share of it
static constexpr uint32_t kDownFrames = 3;
static constexpr double kDownShare = 0.95;
// A step up: after this many frames at a level (doubled after one that had to come back down, to kMostWaits times),
// when the median of at least kUpSamples frames is under kUpWhenUnder of the target, to the highest level expected
// under kUpShare of it (a change costs ~1 ms of the CPU and no frame: down at once, up only when it has stayed well
// under). It counts as held after kUpHeldFrames
static constexpr uint32_t kUpFrames = 30, kUpSamples = 8, kMostWaits = 8, kUpHeldFrames = 2 * kUpFrames;
static constexpr double kUpWhenUnder = 0.85, kUpShare = 0.9;
// The target's grid of levels, in parts
static constexpr uint32_t kGridStep = 4;
// A failed level's ceiling: frames at first and at most (about 4 and 16 s at 60 FPS)
static constexpr uint32_t kCeilingFrames = 240, kCeilingMostFrames = 960;
// Steady Frame Rate's steps: frames at a slower rate before a faster one is tried (doubled after one that had to come
// back down, to kMostWaits times), held after kRateHeldWaits of its wait
static constexpr uint32_t kRateUpFrames = 120, kRateHeldWaits = 4;
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
// The last frames' GPU times kept
static constexpr uint32_t kSamples = 16;

namespace {
struct FrameRateState {
    std::mutex m;
    uint32_t width = 0, height = 0;          // the main view's size with levels (0: none)
    // The main window's swapchain (none: no rate, no target); the presents' handlers read it without m
    std::atomic<VkSwapchainKHR> main{VK_NULL_HANDLE};
    uint32_t lowest = kDynamicParts;         // the lowest level the frames take (kDynamicParts: the full size, no levels)
    std::vector<uint32_t> grid;              // the levels a target picks from, highest first: the full size, every
                                             // kGridStep down to the lowest, and the lowest
    bool steady = false;                     // Steady Frame Rate held (with Vsync) at the last registration: its rate steps
    uint64_t frames = 0;                     // frames begun (ksaMtlFrameRateBeginFrame)
    // The level's steps: the frame of the last change, whether it was a step up, the frames before a step up
    uint64_t changedAt = 0;
    bool steppedUp = false;
    uint32_t upWait = kUpFrames;
    // A level a step up to which had to come back down: not stepped up to again before ceilingUntil (a still view near
    // the target would otherwise climb to it and drop every second or so), ceilingWait frames, doubled each time
    uint32_t ceiling = 0, ceilingWait = 0;
    uint64_t ceilingUntil = 0;
    // Steady Frame Rate's steps, as the levels': the frame of the last change, whether it was to a faster rate (and not
    // held yet), and the frames at a slower rate before a faster one is tried
    uint64_t rateChangedAt = 0;
    bool rateSteppedUp = false;
    uint32_t rateUpWait = kRateUpFrames;
    uint64_t rateChanges = 0;
    // The last faster step's expected time at the lowest level, and how far over it the frames there ran when a faster
    // step didn't hold (the model's fixed share is a guess: in the lower atmosphere nearly all of a frame's GPU time
    // stays at any level): the expectations after it are scaled by that, fading, until a faster step holds
    double rateExpected = 0, rateBias = 1;
    // The last frames' GPU times and levels, as their presents completed: a lock of their own, for completion handlers
    std::mutex samplesMutex;
    struct Sample { uint32_t level; double ms, measured; };  // level as dynres.mm has it; ms: at the GPU's full clock
    Sample samples[kSamples];
    uint32_t sampleCount = 0;
};
FrameRateState& g_fr = *new FrameRateState;
}

// A level as dynres.mm has it (0: the full size) in parts, and back
static uint32_t parts(uint32_t level) { return level ? level : kDynamicParts; }
static uint32_t levelOf(uint32_t parts) { return parts >= kDynamicParts ? 0 : parts; }

// The setting's level (kDynamicMinLevel to kDynamicParts - 1; anything else: off)
static uint32_t settingLevel() {
    uint32_t n = (uint32_t)kmtlConfig().dynamicLevel;
    return n >= kDynamicMinLevel && n < kDynamicParts ? n : 0u;
}

// The target, in GPU milliseconds a frame (0: none, the level stays): Steady Frame Rate's
static double target() {
    return kmtlSteadyFps() && g_fr.main ? kCadenceShare * kmtlFrameMs(g_fr.main) : 0;
}

void kmtlFrameRateNoteFrame(uint32_t level, double gpuMs, VkSwapchainKHR swapchain) {
    // The main window's frames only: a pop-out window's present would add a small sample at the same level
    if (!swapchain || swapchain != g_fr.main.load(std::memory_order_relaxed)) return;
    if (gpuMs <= 0) return;
    std::lock_guard<std::mutex> lock(g_fr.samplesMutex);
    g_fr.samples[g_fr.sampleCount++ % kSamples] = {level, gpuMs * kmtlGpuClockShare(), gpuMs};
}

// The GPU times of the last frames drawn at this level (in parts), at the full clock, the newest first (completed
// presents lag the frames begun by about two); as measured, with measured
static uint32_t samplesAt(uint32_t level, double* ms, double* measured = nullptr) {
    std::lock_guard<std::mutex> lock(g_fr.samplesMutex);
    uint32_t n = 0, count = std::min(g_fr.sampleCount, kSamples);
    for (uint32_t i = 0; i < count; i++) {
        const auto& sample = g_fr.samples[(g_fr.sampleCount - 1 - i) % kSamples];
        if (parts(sample.level) != level) continue;
        if (measured) measured[n] = sample.measured;
        ms[n++] = sample.ms;
    }
    return n;
}

// The GPU time expected at another level, from the time at this one: a fixed share and a share by the pixels
static double expectedAt(double gpu, uint32_t level, uint32_t now) {
    double r = (double)level / now;
    return gpu * (kFixedShare + (1 - kFixedShare) * r * r);
}

// Steady Frame Rate's rate, stepped with the level (under g_fr.m; with Lower Rate Under Heavy Load):
// a slower step (60 -> 40 -> 30) when the frames at the lowest level, settled there, run over the target even at the
// GPU's full clock (they start to miss their slot), so that they keep an even rate; the faster one when the lowest
// level is expected well under that rate's target, after rateUpWait frames at this one (after one that didn't hold:
// twice as long, and expecting as much more as it ran over). Without the GPU's clock the times can't tell (they measure
// the deadline: an M5 at 30 FPS runs at 340-800 MHz instead of 1620, every frame about three quarters of its slot,
// however light): the faster one is then tried after rateUpWait frames whatever they say. Before the level's choice,
// which then follows the rate's target
static void stepRate(double t) {
    if (!kmtlConfig().lowerRateUnderHeavyLoad) return;
    // Without levels (Dynamic Resolution off, MetalFX off, or no room below the render size: Spatial at half the
    // window), the full size is the lowest
    uint32_t now = parts(kmtlDynamicLevelNow());
    double ms[kSamples], measured[kSamples];
    uint32_t n = samplesAt(now, ms, measured);
    if (n == 0) return;
    VkSwapchainKHR main = g_fr.main;
    if (now == g_fr.lowest && n >= kRateDownFrames && kmtlMedian(ms, kRateDownFrames) > t &&
        g_fr.frames >= g_fr.changedAt + kRateSettleFrames && g_fr.frames >= g_fr.rateChangedAt + kRateHoldFrames) {
        if (kmtlSteadyStep(main, 1, false) <= 0) return;
        // A faster rate that didn't hold: the next waits longer, and expects as much more as this one ran over
        if (g_fr.rateSteppedUp) {
            g_fr.rateUpWait = std::min(g_fr.rateUpWait * 2, kRateUpFrames * kMostWaits);
            if (g_fr.rateExpected > 0)
                g_fr.rateBias = std::min(kRateBiasMost, std::max(g_fr.rateBias, kmtlMedian(ms, kRateDownFrames) / g_fr.rateExpected));
        }
        double frame = kmtlSteadyStep(main, 1, true);
        g_fr.rateChangedAt = g_fr.frames;
        g_fr.ceiling = 0;  // another target: the levels as they come
        g_fr.rateSteppedUp = false;
        KLOG(++g_fr.rateChanges <= 20 ? 0 : 1,
             "steady frame rate: slower, %.1f ms a frame (the lowest level at %.1f ms at the full clock, %.1f ms at %.0f MHz)",
             frame, kmtlMedian(ms, kRateDownFrames), kmtlMedian(measured, kRateDownFrames), kmtlGpuClockMhz());
        return;
    }
    // A faster rate that held: the wait back to the shortest, the expectations as the model has them
    if (g_fr.rateSteppedUp && g_fr.frames > g_fr.rateChangedAt + kRateHeldWaits * g_fr.rateUpWait) {
        g_fr.rateUpWait = kRateUpFrames;
        g_fr.rateBias = 1;
        g_fr.rateSteppedUp = false;
    }
    // At the slower rate, what a try that didn't hold taught fades (the scene it ran in passes)
    if (!g_fr.rateSteppedUp) g_fr.rateBias = 1 + (g_fr.rateBias - 1) * kRateBiasKeep;
    double faster = kmtlSteadyStep(main, -1, false);
    if (faster <= 0 || g_fr.frames < g_fr.rateChangedAt + g_fr.rateUpWait) return;
    // The level follows (chooseLevel: down at once if the frames run over), and if even the lowest can't hold the faster
    // rate, the slower one comes back (above)
    double expected = 0;
    if (kmtlGpuClockKnown()) {
        if (n < kUpSamples) return;
        expected = expectedAt(kmtlMedian(ms, n), g_fr.lowest, now);
        if (expected * g_fr.rateBias > kRateUpShare * kCadenceShare * faster) return;
    }
    g_fr.rateExpected = expected;
    kmtlSteadyStep(main, -1, true);
    g_fr.rateChangedAt = g_fr.frames;
    g_fr.ceiling = 0;  // another target: the levels as they come
    g_fr.rateSteppedUp = true;
    if (expected > 0)
        KLOG(++g_fr.rateChanges <= 20 ? 0 : 1,
             "steady frame rate: faster, %.1f ms a frame (the lowest level expected at %.1f ms at the full clock, x%.2f; now %.1f ms at %.0f MHz)",
             faster, expected, g_fr.rateBias, kmtlMedian(measured, n), kmtlGpuClockMhz());
    else
        KLOG(++g_fr.rateChanges <= 20 ? 0 : 1, "steady frame rate: faster, %.1f ms a frame (tried after %u frames, no GPU clock)",
             faster, g_fr.rateUpWait);
}

// The level for the next frame (in parts), by the target t (under g_fr.m): down as soon as the last kDownFrames frames
// drawn at this level ran over it (their median: one hitch alone doesn't), to the highest level expected under it; up,
// to the highest expected well under it, when the frames at this level have stayed well under it for kUpFrames frames,
// longer after a step up that had to come back down, whose level is then a ceiling for a while (the model's fixed share
// is a guess)
static uint32_t chooseLevel(double t) {
    uint32_t now = parts(kmtlDynamicLevelNow());
    double ms[kSamples];
    uint32_t n = samplesAt(now, ms);
    if (n == 0) return now;
    const std::vector<uint32_t>& grid = g_fr.grid;
    if (now > grid.back() && n >= kDownFrames && kmtlMedian(ms, kDownFrames) > t) {
        // Down: the highest level below this one expected under the target, or the lowest. A step up that didn't hold
        // (as long as it takes to count as held, below) was a mistake of the model's: the next waits longer (else a
        // still view near the target climbs and drops again every second or so, its sharpness pumping)
        if (g_fr.steppedUp && g_fr.frames <= g_fr.changedAt + kUpHeldFrames) {
            g_fr.upWait = std::min(g_fr.upWait * 2, kUpFrames * kMostWaits);
            // ... and this level is a ceiling for a while, longer each time it fails again (a repeat: the same level
            // failing again within one more wait of its last ceiling's end)
            bool again = g_fr.ceiling == now && g_fr.frames < g_fr.ceilingUntil + g_fr.ceilingWait;
            g_fr.ceilingWait = again ? std::min(g_fr.ceilingWait * 2, kCeilingMostFrames) : kCeilingFrames;
            g_fr.ceiling = now;
            g_fr.ceilingUntil = g_fr.frames + g_fr.ceilingWait;
        }
        double gpu = kmtlMedian(ms, kDownFrames);
        for (uint32_t l : grid)
            if (l < now && expectedAt(gpu, l, now) <= t * kDownShare) return l;
        return grid.back();
    }
    // A step up that held: the wait back to the shortest; at the ceiling or above, the ceiling gone
    if (g_fr.steppedUp && g_fr.frames > g_fr.changedAt + kUpHeldFrames) {
        g_fr.upWait = kUpFrames;
        if (g_fr.ceiling && now >= g_fr.ceiling) g_fr.ceiling = 0;
    }
    if (g_fr.frames < g_fr.changedAt + g_fr.upWait || n < kUpSamples) return now;
    double gpu = kmtlMedian(ms, n);
    if (gpu >= t * kUpWhenUnder) return now;
    // Up to the highest level expected to stay under the target, below a ceiling: in one change, which a still view
    // shows once instead of at every step. Upwards the model errs on the safe side where more of a frame stays at any
    // level than its fixed share (the lower atmosphere: nearly all); where less, the step comes back down and its level
    // becomes a ceiling
    uint32_t best = now;
    for (auto it = grid.rbegin(); it != grid.rend(); ++it) {
        if (*it <= now) continue;
        if (g_fr.ceiling && *it >= g_fr.ceiling && g_fr.frames < g_fr.ceilingUntil) break;
        if (expectedAt(gpu, *it, now) > t * kUpShare) break;
        best = *it;
    }
    return best;
}

// The least share of the main view's size a level may have (1: no levels), for MetalFX's scalers
float kmtlFrameRateMinContent() {
    std::lock_guard<std::mutex> lock(g_fr.m);
    return g_fr.width ? (float)g_fr.lowest / kDynamicParts : 1.f;
}

// The lowest level for a main view of this size shown at this one (kDynamicParts: none, no room below the render size).
// The hooks' least size (MetalFX Spatial: half the window or 3/4 of the render size, RenderScale.cs) when given, else the
// display's: the lowest level whose size the temporal scaler takes to the display by itself. Not below what the temporal
// scaler reaches from its input (most: 3x on an M5), whose output is made for the lowest, one step over it as a level's
// size is rounded; dynamic_level instead, if set
static uint32_t lowestLevel(uint32_t width, uint32_t height, uint32_t outW, uint32_t outH, uint32_t leastW,
                            uint32_t leastH) {
    float most = kmtlUpscaleMaxScale();
    uint32_t reach = most > 0 ? (uint32_t)std::ceil(kDynamicParts / most) + 1 : kDynamicMinLevel;
    uint32_t lowest = settingLevel();
    if (!lowest && leastW && leastH) {
        lowest = kDynamicParts;
        for (uint32_t l = kDynamicMinLevel; l < kDynamicParts; l++)
            if (kmtlDynamicScaled(width, l) >= leastW && kmtlDynamicScaled(height, l) >= leastH) { lowest = l; break; }
    } else if (!lowest) {
        // As upscale.mm's prepare() works out the temporal scaler's output: from the level's size, rounded
        lowest = kDynamicParts;
        for (uint32_t l = kDynamicMinLevel; l < kDynamicParts && most > 0; l++)
            if (kmtlUpscaleReach(kmtlDynamicScaled(width, l), most) >= outW &&
                kmtlUpscaleReach(kmtlDynamicScaled(height, l), most) >= outH) { lowest = l; break; }
    }
    return std::min(std::max(lowest, reach), kDynamicParts);
}

// The hooks, after each rebuild of the main view's targets (and before they are made): the main window's swapchain
// (none: no rate and no levels), the main view's size (0 x 0: none), the display's, and the least size a level may
// have (0 x 0: the display's rule). Levels only with Dynamic Resolution (and Steady Frame Rate's frame time, with
// Vsync) or the experiment's level; Steady Frame Rate alone registers the swapchain, for its rate's steps (the main
// view's size is then none); without either, nothing changes
KMTL_EXPORT void ksaMtlFrameRateView(VkSwapchainKHR swapchain, uint32_t width, uint32_t height, uint32_t outW,
                                     uint32_t outH, uint32_t leastW, uint32_t leastH) {
    KTRACE_FIRST();  // with a pool: a level change's command buffer and encoders, from a .NET thread
    std::lock_guard<std::mutex> lock(g_fr.m);
    g_fr.main = swapchain;
    // Steady Frame Rate's, with the main window's swapchain to take its frame time from (with Vsync): its rate's
    // steps, and with Dynamic Resolution the levels; or the experiment's level. Vsync turned on later waits for the next
    // rebuild, levels and rate steps alike
    bool steady = kmtlSteadyFps() && kmtlFrameMs(swapchain) > 0;
    g_fr.steady = steady;
    if (!settingLevel() && !(steady && kmtlConfig().dynamicResolution)) width = height = 0;
    uint32_t viewW = width, viewH = height;  // for the log: the main view's size even where no level fits below it
    g_fr.lowest = width && height ? lowestLevel(width, height, outW, outH, leastW, leastH) : kDynamicParts;
    if (g_fr.lowest >= kDynamicParts) width = height = 0;
    g_fr.width = width;
    g_fr.height = height;
    g_fr.grid = {kDynamicParts};
    for (uint32_t l = kDynamicParts - kGridStep; l >= g_fr.lowest + kGridStep; l -= kGridStep) g_fr.grid.push_back(l);
    if (g_fr.lowest < kDynamicParts) g_fr.grid.push_back(g_fr.lowest);
    // The GPU's clock, for the times of frames paced by Steady Frame Rate (gpuclock.mm), when the levels or the rate's
    // steps use them
    if (steady && (width || kmtlConfig().lowerRateUnderHeavyLoad)) kmtlGpuClockStart();
    // With a target from the full size, without one at the lowest level. Images still there from before (a rebuild that
    // kept them) take it now, or the hooks' sizes and the textures would part. The levels a target picks from made
    // ahead; without one, the lowest
    double t = target();
    std::vector<uint32_t> levels;
    if (t > 0)
        for (uint32_t l : g_fr.grid) levels.push_back(levelOf(l));
    else
        levels = {0, levelOf(g_fr.lowest)};
    kmtlDynamicSetView(width, height, levels);
    kmtlDynamicSetLevel(t > 0 ? 0 : levelOf(g_fr.lowest));
    {
        std::lock_guard<std::mutex> samples(g_fr.samplesMutex);
        g_fr.sampleCount = 0;
    }
    g_fr.changedAt = g_fr.rateChangedAt = g_fr.frames;
    // A new start: no step up of before to fail (the first frames after a rebuild often run over), no ceiling
    g_fr.steppedUp = false;
    g_fr.upWait = kUpFrames;
    g_fr.ceiling = 0;
    g_fr.rateSteppedUp = false;
    g_fr.rateUpWait = kRateUpFrames;
    g_fr.rateExpected = 0;
    g_fr.rateBias = 1;
    KLOG(0, "dynamic resolution: main view %ux%u to %ux%u, level %u of %u, lowest %u (%s), target %.1f ms", viewW, viewH,
         outW, outH, kmtlDynamicLevelNow(), kDynamicParts, levelOf(g_fr.lowest),
         settingLevel() ? "dynamic_level" : leastW ? "Spatial's least size" : "the display's", t);
}

// The main view's size at the level now: what the game's own sizes for it become
KMTL_EXPORT void ksaMtlFrameRateSize(uint32_t* width, uint32_t* height, uint32_t* level) {
    KTRACE_FIRST_PLAIN();
    std::lock_guard<std::mutex> lock(g_fr.m);
    *level = kmtlDynamicLevelNow();
    *width = kmtlDynamicScaled(g_fr.width, *level);
    *height = kmtlDynamicScaled(g_fr.height, *level);
}

// The hooks, at the start of the game's frame, before it records any of it: the rate's step, this frame's level,
// changed here, and the main view's size at it
KMTL_EXPORT void ksaMtlFrameRateBeginFrame(uint32_t* width, uint32_t* height, uint32_t* level) {
    KTRACE_FIRST();  // as above
    std::lock_guard<std::mutex> lock(g_fr.m);
    g_fr.frames++;
    // The rate first, whose target the level then follows (also without levels). Without a target (no main swapchain
    // registered, as after a screenshot's rebuild, Steady Frame Rate off or no cadence): the experiment's level, or the
    // full size
    if (g_fr.steady && target() > 0) stepRate(target());
    double t = target();
    uint32_t before = parts(kmtlDynamicLevelNow());
    uint32_t want = !g_fr.width ? before : t > 0 ? chooseLevel(t) : settingLevel() ? g_fr.lowest : kDynamicParts;
    kmtlDynamicPrepare();
    if (want != before) {
        if (g_logLevel >= 1) {
            double ms[kSamples], measured[kSamples];
            uint32_t n = samplesAt(before, ms, measured);
            KLOG(1, "dynamic resolution: frame %llu, %u -> %u after %llu frames (target %.1f ms, wait %u): newest %.1f, %.1f, %.1f ms (%.1f, %.1f, %.1f measured)",
                 (unsigned long long)g_fr.frames, before, want, (unsigned long long)(g_fr.frames - g_fr.changedAt), t,
                 g_fr.upWait, n > 0 ? ms[0] : 0.0, n > 1 ? ms[1] : 0.0, n > 2 ? ms[2] : 0.0, n > 0 ? measured[0] : 0.0,
                 n > 1 ? measured[1] : 0.0, n > 2 ? measured[2] : 0.0);
        }
        g_fr.steppedUp = want > before;
        kmtlDynamicSetLevel(levelOf(want));
        g_fr.changedAt = g_fr.frames;
    }
    *level = kmtlDynamicLevelNow();
    *width = kmtlDynamicScaled(g_fr.width, *level);
    *height = kmtlDynamicScaled(g_fr.height, *level);
}
