// Present pacing: Steady Frame Rate's cadence and steps on the display (the swapchains' list, for the Frame Rate
// section), the present wait that ends when the GPU is about to need the next frame, late frame start, and the
// latency log. swapchain.mm calls in when a swapchain is made and at each present.
#include "kmtl.h"
#import <AppKit/AppKit.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <mutex>
#include <thread>
#include <vector>

// KSA_MTL_LATENCY=<csv>, and the benchmark's drain while it is open (shown records, diagnostics/drain.mm): per present,
// when its frame began (the game's wait for an earlier present returned, or else the previous present), when it was
// presented, finished on the GPU and was shown (seconds, presentedTime's clock), how long the frame waited for an earlier
// present, and whether pacing ended that wait before the display
namespace {
struct PacingState {
    // The live swapchains, for the main view's frame time and steps (kmtlFrameMs, kmtlSteadyStep). Their cadence
    // fields are written under m; the presents read them without it (atomics)
    std::mutex m;
    std::vector<KSwapchain*> swapchains;
    // The latency log's, under a lock of their own
    std::mutex latencyMutex;
    FILE* latencyCsv = nullptr;
    double frameBegan = 0, frameWaited = 0;
    bool framePaced = false;
};
PacingState& g_pace = *new PacingState;
}  // namespace

static void latencyBegin(double waited, bool paced) {
    std::lock_guard<std::mutex> lock(g_pace.latencyMutex);
    g_pace.frameBegan = CACurrentMediaTime();
    g_pace.frameWaited += waited;
    g_pace.framePaced = paced;
}

void kmtlLatencyAtPresent(id<CAMetalDrawable> drawable, id<MTLCommandBuffer> cb) {
    static uint64_t presents = 0;
    // The handler is added after the mutex is released: Core Animation runs presented handlers while it holds the
    // drawable's lock, which addPresentedHandler takes too, and the handler takes this mutex (a deadlock otherwise:
    // the game hung in vkQueuePresentKHR)
    double now, began, waited;
    uint64_t n, present = kmtlPresentCount();  // vkQueuePresentKHR counted this present already
    bool paced, drain = kmtlDrainOn();
    FILE* csv;
    {
        std::lock_guard<std::mutex> lock(g_pace.latencyMutex);
        if (!g_pace.latencyCsv && kmtlConfig().latency) {
            g_pace.latencyCsv = fopen(kmtlConfig().latency, "w");
            if (g_pace.latencyCsv) fprintf(g_pace.latencyCsv, "present,began,presented,gpu_end,shown,waited_ms,paced\n");
        }
        if (!g_pace.latencyCsv && !drain) return;
        now = CACurrentMediaTime();
        began = g_pace.frameBegan ? g_pace.frameBegan : now;
        waited = g_pace.frameWaited * 1e3;
        n = ++presents;
        paced = g_pace.framePaced;
        g_pace.frameBegan = now;
        g_pace.frameWaited = 0;
        g_pace.framePaced = false;
        csv = g_pace.latencyCsv;
    }
    [drawable addPresentedHandler:^(id<MTLDrawable> d) {
        if (drain) kmtlDrainShown(present, began, now, cb.GPUEndTime, d.presentedTime, waited, paced);
        if (!csv) return;
        std::lock_guard<std::mutex> lock(g_pace.latencyMutex);
        fprintf(csv, "%llu,%.6f,%.6f,%.6f,%.6f,%.3f,%d\n", (unsigned long long)n, began, now, cb.GPUEndTime,
                d.presentedTime, waited, paced ? 1 : 0);
        if (n % 60 == 0) fflush(csv);
    }];
}

// The window a layer is in (AppKit calls, on the game's main thread)
static NSWindow* layerWindow(CAMetalLayer* layer) {
    for (NSWindow* window in NSApp.windows) {
        if (window.contentView.layer == layer) return window;
    }
    return nil;
}

// The highest refresh rate of the display the layer's window is on (a ProMotion display: 120), else the main display's
static double displayRefresh(CAMetalLayer* layer) {
    @autoreleasepool {
        NSScreen* screen = layerWindow(layer).screen ?: NSScreen.mainScreen;
        NSInteger hz = screen.maximumFramesPerSecond;
        return hz > 0 ? (double)hz : 60.0;
    }
}

// Steady Frame Rate's rate (KSA_MTL_STEADY_FRAME_RATE: 30, 40 or 60; 0: off)
int kmtlSteadyFps() {
    static const int fps = [] {  // read once, as the settings are
        const char* v = kmtlConfig().steadyFrameRate;
        int f = v ? atoi(v) : 0;
        return f == 30 || f == 40 || f == 60 ? f : 0;
    }();
    return fps;
}

// Steady Frame Rate's steps: the frame time at its rate, then at 40 and 30 where slower. With Vsync, as whole refreshes
// of the display, never faster (60 fps at 60 or 120 Hz, 40 at 120 Hz and 30 at 60 Hz, 30 at either), and an even count
// with frame interpolation, which shows two images a frame, each for half of it. Only with Vsync (applyCadence)
static std::vector<double> steadySteps(double hz) {
    std::vector<double> steps;
    int fps = kmtlSteadyFps();
    for (int f : {60, 40, 30}) {
        if (f > fps) continue;
        double refreshes = std::max(1.0, std::ceil(hz / f - 1e-3));
        if (kmtlConfig().frameInterpolation && std::fmod(refreshes, 2.0) != 0) refreshes += 1;
        double ms = refreshes * 1000.0 / hz;
        if (steps.empty() || ms > steps.back() + 0.01) steps.push_back(ms);
    }
    return steps;
}

// The cadence that holds a frame time: with Vsync and more than one refresh (one is what Vsync does by itself; more
// than one and a half, as a step's frame time is whole refreshes), else none
static double pacedCadence(const KSwapchain* sc, double frameMs) {
    return sc->vsync && frameMs > 1.5 * 1000.0 / sc->refreshHz ? frameMs : 0;
}

// A step's frame time and cadence (g_pace.m held, or the swapchain not listed yet). At one refresh the frame
// time is dynamic resolution's target only
static void takeStep(KSwapchain* sc, size_t step) {
    sc->steadyStep = step;
    sc->frameMs = sc->steadySteps[step];
    sc->cadenceMs = pacedCadence(sc, sc->frameMs);
}

// A swapchain's cadence and drawables. KSA_MTL_STEADY_FRAME_RATE: its rate's step (steadySteps), again whenever the
// window moves to another display or a display changes (the rate's own step then); KSA_MTL_FRAME_CADENCE sets the
// interval itself. 2 drawables hold the display queue to one frame: at 25 ms a frame or more (40 fps or less), one
// frame less latency with the same cadence; at 60 fps the game blocks on a drawable and misses slots, so 3
// (KSA_MTL_DRAWABLE_COUNT=2 or 3 decides instead). Frame interpolation takes two drawables in one present, so it
// needs 3. A slower step taken later keeps the drawables of the rate's own
static void applyCadence(KSwapchain* sc) {
    CAMetalLayer* layer = sc->surface->layer;
    // Steady Frame Rate only with Vsync (without it nothing is paced: no frame time, no dynamic resolution); frame_cadence,
    // an experiment, instead of it
    bool steady = kmtlSteadyFps() && kmtlConfig().frameCadence <= 0 && sc->vsync;
    double hz = steady ? displayRefresh(layer) : 0;
    double own;
    {
        std::lock_guard<std::mutex> lock(g_pace.m);
        if (steady) {
            // A notification that changed nothing keeps the step taken: the display reports one each time the
            // frames' pacing changes (a slower step on a ProMotion display: three, each of which put it back)
            std::vector<double> steps = steadySteps(hz);
            bool same = steps == sc->steadySteps && hz == sc->refreshHz;
            sc->refreshHz = hz;
            sc->steadySteps = steps;
            takeStep(sc, same ? sc->steadyStep : 0);
        } else {
            sc->steadySteps.clear();
            sc->cadenceMs = sc->frameMs = kmtlConfig().frameCadence > 0 ? kmtlConfig().frameCadence : 0;
        }
        // The rate's own step's cadence, not the step taken now: a slower step's two drawables would stay after the
        // faster one comes back, and at 60 FPS the game then waits for a drawable and shows every other frame
        own = sc->steadySteps.empty() ? sc->cadenceMs.load() : pacedCadence(sc, sc->steadySteps[0]);
    }
    int forced = kmtlConfig().drawableCount;
    bool two = forced == 2 || (forced != 3 && own >= 25);
    layer.maximumDrawableCount = two && !kmtlConfig().frameInterpolation ? 2 : 3;
}

// The swapchain behind a handle while it is alive (g_pace.m held): the hooks' handle may outlive it
static KSwapchain* listed(VkSwapchainKHR swapchain) {
    auto* sc = (KSwapchain*)swapchain;
    if (!sc) return nullptr;
    return std::find(g_pace.swapchains.begin(), g_pace.swapchains.end(), sc) != g_pace.swapchains.end() ? sc : nullptr;
}

// The frame time a swapchain holds its frames to (0: none, or no cadence)
double kmtlFrameMs(VkSwapchainKHR swapchain) {
    std::lock_guard<std::mutex> lock(g_pace.m);
    KSwapchain* sc = listed(swapchain);
    return sc ? sc->frameMs.load() : 0;
}

// Steady Frame Rate's next step of a swapchain (dir 1: slower, -1: faster): its frame time (0: none that way); with
// take, taken
double kmtlSteadyStep(VkSwapchainKHR swapchain, int dir, bool take) {
    std::lock_guard<std::mutex> lock(g_pace.m);
    KSwapchain* sc = listed(swapchain);
    if (!sc || sc->steadySteps.empty()) return 0;
    long step = (long)sc->steadyStep + dir;
    if (step < 0 || step >= (long)sc->steadySteps.size()) return 0;
    if (take) takeStep(sc, (size_t)step);
    return sc->steadySteps[step];
}

// A new swapchain's cadence and drawables, and with Steady Frame Rate again at every change of its window's display
void kmtlPaceSwapchain(KSwapchain* sc) {
    CAMetalLayer* layer = sc->surface->layer;
    applyCadence(sc);
    if (kmtlSteadyFps() && kmtlConfig().frameCadence <= 0 && sc->vsync) {
        // The game makes a new swapchain only when the window's size changes. Both on the main thread, as the presents
        NSNotificationCenter* center = NSNotificationCenter.defaultCenter;
        auto again = ^(NSNotification*) {
            applyCadence(sc);
            KLOG(1, "swapchain: cadence %.2f ms, frame %.2f ms, %lu drawables", sc->cadenceMs.load(), sc->frameMs.load(),
                 (unsigned long)layer.maximumDrawableCount);
        };
        sc->observers = @[
            [center addObserverForName:NSWindowDidChangeScreenNotification object:layerWindow(layer)
                                 queue:NSOperationQueue.mainQueue usingBlock:again],
            [center addObserverForName:NSApplicationDidChangeScreenParametersNotification object:nil
                                 queue:NSOperationQueue.mainQueue usingBlock:again],
        ];
    }
}

// The live swapchains' list: a swapchain made, and one about to be destroyed (its display observers removed first)
void kmtlListSwapchain(KSwapchain* sc) {
    std::lock_guard<std::mutex> lock(g_pace.m);
    g_pace.swapchains.push_back(sc);
}

void kmtlUnlistSwapchain(KSwapchain* sc) {
    for (id observer in sc->observers) [NSNotificationCenter.defaultCenter removeObserver:observer];
    std::lock_guard<std::mutex> lock(g_pace.m);
    g_pace.swapchains.erase(std::remove(g_pace.swapchains.begin(), g_pace.swapchains.end(), sc), g_pace.swapchains.end());
}

// Present pacing. With Vsync the game waits for the present before last before it reads input. If a present counted
// only once the display showed it, that wait would also cover the window server's delay (about 21 ms in a window, 10
// in fullscreen) and leave the GPU idle whenever a frame takes less than that delay plus the CPU's part. So a present
// also counts once its GPU work has ended, the present before it was shown, and the GPU is about to need the next
// frame: the previous frame's predicted GPU end, less the CPU's part and a 2 ms margin. Frames then start when the GPU
// can take them instead of queueing up. KSA_MTL_VSYNC_PACING=0: the display only (Vulkan's meaning)

static KPresentState::Frame* findFrame(KPresentState& st, uint64_t present) {
    if (st.frames.empty() || present < st.frames.front().present || present > st.frames.back().present) return nullptr;
    return &st.frames[present - st.frames.front().present];
}

// When pacing lets a wait for this id end (CACurrentMediaTime seconds): 0 now, -1 not yet known
static double pacedEnd(KPresentState& st, uint64_t id) {
    if (st.frames.empty() || id < st.frames.front().id) return 0;
    KPresentState::Frame* f = nullptr;
    for (auto& fr : st.frames) if (fr.id == id) { f = &fr; break; }
    if (!f || !f->gpuDone) return -1;
    KPresentState::Frame* prev = findFrame(st, f->present - 1);
    if (prev && !prev->shown) return -1;
    KPresentState::Frame* next = findFrame(st, f->present + 1);
    double lead = kmtlMedian(st.gpuMs, std::min(st.gpuSamples, 8u)) - kmtlMedian(st.cpuMs, std::min(st.cpuSamples, 8u)) - 2.0;
    return lead > 0 ? std::max(f->gpuEnd, next ? next->made : 0.0) + lead * 1e-3 : 0;
}

VKFN(VkResult) vkWaitForPresentKHR(VkDevice, VkSwapchainKHR swapchain, uint64_t presentId, uint64_t timeout) {
    KTRACE_FIRST();
    std::shared_ptr<KPresentState> state = ((KSwapchain*)swapchain)->presentState;
    bool pacing = kmtlConfig().vsyncPacing;
    double start = CACurrentMediaTime();
    std::unique_lock<std::mutex> lock(state->m);
    auto deadline = std::chrono::steady_clock::now() +
        std::chrono::nanoseconds(timeout > (uint64_t)INT64_MAX / 2 ? (uint64_t)INT64_MAX / 2 : timeout);
    bool done, paced = false;
    while (!(done = state->presented >= presentId)) {
        double at = pacing ? pacedEnd(*state, presentId) : -1, now = CACurrentMediaTime();
        if (at >= 0 && at <= now) { done = paced = true; break; }
        auto until = at > 0 ? std::min(deadline, std::chrono::steady_clock::now() + std::chrono::duration_cast<
                                       std::chrono::steady_clock::duration>(std::chrono::duration<double>(at - now)))
                            : deadline;
        if (state->cv.wait_until(lock, until) == std::cv_status::timeout && std::chrono::steady_clock::now() >= deadline) {
            done = state->presented >= presentId;
            break;
        }
    }
    // late_frame_start's delay, within the caller's timeout
    double left = std::chrono::duration<double>(deadline - std::chrono::steady_clock::now()).count();
    double delay = done ? std::min(state->startDelay, std::max(left, 0.0)) : 0;
    lock.unlock();
    if (delay > 0) std::this_thread::sleep_for(std::chrono::duration<double>(delay));
    double end = CACurrentMediaTime();
    // The drain only: under V-Sync the game waits here every frame by design, which the stalls log would drown in
    if (kmtlDrainOn()) kmtlDrainWait("vkWaitForPresentKHR", start, (end - start) * 1000.0);
    lock.lock();
    state->began = end;
    lock.unlock();
    if (kmtlConfig().latency || kmtlDrainOn()) latencyBegin(end - start, paced);
    return done ? VK_SUCCESS : VK_TIMEOUT;
}

// late_frame_start: with a cadence the game's wait above ends a frame or two before the display slot its frame will
// get, and the input it reads then is that old when shown. Each game frame shown on its slot (within 2 ms of the
// cadence after the one before) stretches the wait by late_frame_start's milliseconds, up to three quarters of the
// cadence; one shown later, or not at all, halves it: about one frame in (half the wait / the step) misses its slot.
// The wait settles just short of the frames missing their slots, whatever the CPU, GPU and window server take. The
// cadence must be a whole number of the display's refresh intervals (steady_frame_rate's is), or every frame counts
// as late
static void noteShownForStart(KPresentState& st, uint64_t present, double shownAt, double cadenceMs, double stepMs) {
    if (shownAt <= 0) {
        st.startDelay *= 0.5;
        st.lastShownPresent = 0;  // the next frame shown only starts the count again
        return;
    }
    if (st.lastShownPresent && st.lastShownPresent + 1 == present) {
        double interval = (shownAt - st.lastShown) * 1e3;
        st.startDelay = interval <= cadenceMs + 2.0 ? std::min(st.startDelay + stepMs * 1e-3, cadenceMs * 0.75e-3)
                                                    : st.startDelay * 0.5;
    } else if (st.lastShownPresent) {
        st.startDelay *= 0.5;
    }
    st.startDelay = std::min(st.startDelay, cadenceMs * 0.75e-3);  // the cadence can shrink (another display)
    st.lastShown = shownAt;
    st.lastShownPresent = present;
}

// At a present, for vkWaitForPresentKHR: when it was made, and the handlers that note when it ended on the GPU (its
// frame's GPU time, for pacing and the Frame Rate section) and when it was shown. Its present id (0: none), its number
// as the present record has it, and whether a screenshot's copy is in its GPU time
void kmtlPaceAtPresent(KSwapchain* sc, id<MTLCommandBuffer> cb, id<CAMetalDrawable> drawable, uint64_t presentId,
                       uint64_t counted, bool shot) {
    std::shared_ptr<KPresentState> state = sc->presentState;
    uint64_t n;
    {
        std::lock_guard<std::mutex> lock(state->m);
        double now = CACurrentMediaTime();
        n = ++state->presents;
        // The CPU's part of a frame: from the game's wait to the present, when it waited in this frame
        if (state->began > 0 && (state->frames.empty() || state->began > state->frames.back().made))
            state->cpuMs[state->cpuSamples++ % 8] = (now - state->began) * 1e3;
        if (state->frames.size() == 16) state->frames.erase(state->frames.begin());
        state->frames.push_back({n, presentId, now, 0, false, false});
    }
    bool shownAtEnd = !drawable;
    uint32_t level = kmtlDynamicLevelNow();  // dynamic resolution's level of this frame
    VkSwapchainKHR handle = (VkSwapchainKHR)sc;
    [cb addCompletedHandler:^(id<MTLCommandBuffer> c) {
        std::lock_guard<std::mutex> lock(state->m);
        // The frame's own GPU time: its work up to this command buffer, which completes in order after it
        // (kmtlNoteGpuTime, the handler before, counted this buffer)
        double ms = kmtlTakeFrameGpuTime() * 1e3;
        state->gpuMs[state->gpuSamples++ % 8] = ms;
        if (!shot) kmtlFrameRateNoteFrame(level, ms, handle);
        if (kmtlDrainOn()) kmtlDrainFrameGpu(counted, CACurrentMediaTime(), ms);
        if (KPresentState::Frame* f = findFrame(*state, n)) {
            f->gpuDone = true;
            f->gpuEnd = c.GPUEndTime;
            f->shown |= shownAtEnd;
        }
        if (shownAtEnd) state->presented = std::max(state->presented, presentId);
        state->cv.notify_all();
    }];
    bool late = kmtlConfig().lateFrameStart > 0 && !kmtlConfig().frameInterpolation;
    double cadence = late && sc->vsync ? sc->frameMs.load() : 0;  // also at one refresh, which isn't paced
    double step = kmtlConfig().lateFrameStart;
    if (drawable) [drawable addPresentedHandler:^(id<MTLDrawable> d) {
        std::lock_guard<std::mutex> lock(state->m);
        if (cadence > 0) noteShownForStart(*state, n, d.presentedTime, cadence, step);
        if (KPresentState::Frame* f = findFrame(*state, n)) f->shown = true;
        state->presented = std::max(state->presented, presentId);
        state->cv.notify_all();
    }];
}

// The swapchain's cadence (steady_frame_rate, frame_cadence): each drawable is shown at least that long after the one
// before (with frame interpolation two are shown per game frame, so half as long), so the frame rate holds one step of
// the display's refresh instead of moving between them
void kmtlPresentPaced(KSwapchain* sc, id<MTLCommandBuffer> cb, id<CAMetalDrawable> d, bool interpolated) {
    double ms = sc->cadenceMs;
    if (ms > 0) [cb presentDrawable:d afterMinimumDuration:(interpolated ? ms / 2 : ms) / 1000.0];
    else [cb presentDrawable:d];
}
