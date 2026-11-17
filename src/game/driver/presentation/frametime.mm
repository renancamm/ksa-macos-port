// Players' frame timing: the presents counted, the GPU's busy time from the command buffers' completion handlers, and
// what the display showed from the drawables' presented handlers (swapchain.mm). Pacing, dynamic resolution's levels
// and Steady Frame Rate's steps work from them, and so do the instruments (diagnostics/instruments.mm: the frame log,
// the benchmark's numbers).
#include "kmtl.h"

namespace {
struct FrameTimeState {
    // GPU busy time: the union of the command buffers' GPU intervals, in completion order, summed until the instruments
    // take it at the next present (so it lags the CPU interval by the frames in flight), and since the last present
    // completed (one frame's GPU time); under m
    std::mutex m;
    double gpuBusy = 0, gpuLastEnd = 0, gpuBusyFrame = 0;
    // Presents made, read without a lock (the throttle, stall logs); the drawables the display showed, interpolated ones
    // too; when it last showed a game frame (presentedTime) and the time between the last two
    std::atomic<uint64_t> presents{0}, displayed{0};
    std::atomic<double> lastPresented{0}, displayMs{0};
};
FrameTimeState& g_ft = *new FrameTimeState;
}  // namespace

uint64_t kmtlCountPresent() { return ++g_ft.presents; }
uint64_t kmtlPresentCount() { return g_ft.presents; }

void kmtlNoteGpuTime(id<MTLCommandBuffer> cb) {
    double s = cb.GPUStartTime, e = cb.GPUEndTime;
    if (e <= s) return;
    std::lock_guard<std::mutex> lock(g_ft.m);
    double busy = e - std::max(s, std::min(e, g_ft.gpuLastEnd));
    g_ft.gpuBusy += busy;
    g_ft.gpuBusyFrame += busy;
    g_ft.gpuLastEnd = std::max(g_ft.gpuLastEnd, e);
}

double kmtlTakeFrameGpuTime() {
    std::lock_guard<std::mutex> lock(g_ft.m);
    return std::exchange(g_ft.gpuBusyFrame, 0.0);
}

double kmtlTakePresentGpuTime() {
    std::lock_guard<std::mutex> lock(g_ft.m);
    return std::exchange(g_ft.gpuBusy, 0.0);
}

void kmtlNoteShown(double t, bool timed) {
    g_ft.displayed++;
    if (!timed) return;
    double last = g_ft.lastPresented.exchange(t);
    if (last > 0) g_ft.displayMs = (t - last) * 1e3;
}

uint64_t kmtlShownCount() { return g_ft.displayed.load(); }
double kmtlDisplayMs() { return g_ft.displayMs.load(); }
