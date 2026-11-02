// Pass dump: one frame's images, to read without Xcode where a picture went wrong (ksaMtlPassDump). Every render pass's
// colour attachments after each of its draws (the first kDrawsAll, then every kDrawsStride-th), as it ends, and at the
// frame's end the images the game has: a PNG of each (tone mapped, NaN magenta, infinity cyan), numbered in the order they
// were recorded, and a line of passes.txt with its numbers and where it lies in memory, since images placed in a heap can
// share it. With each draw's images, the buffers bound for it that this pipeline in this pass hasn't shown: the first
// kBufferBytes of each, raw, in a .bin file, its line counting what read as floats isn't finite, a hint only, as integers
// read as NaN. Bound isn't read: Vulkan's bindings outlast a pipeline that doesn't use them. Lines starting -- come in the order of the encoders; an image's line
// comes when it is written, and names its pass. What can't be read gets a line saying why. An encoder is named as
// KSA_MTL_LABELS names it, in this frame without the switch.
//
// A limit: each render pass with images is followed by a full wait, so passes that overlap in other frames don't overlap in
// this one (compute encoders are not held). A fault that comes from a missing barrier may not appear: a clean dump doesn't
// rule one out.
//
// The watch (KSA_MTL_WATCH=<dir>): every kWatchEvery presents, a copy of the presented image and a look at a sample of it,
// off the game's thread. An image that is broken without doubt, by what no clean view has shown, is kept and gets a pass dump
// of the next frame; watch.txt has when each fault began and ended. Dark or partly wrong images need a clean image of the
// same view to compare with: the benchmark's --catch does that.
#include "kmtl.h"
#import <CoreGraphics/CoreGraphics.h>
#import <ImageIO/ImageIO.h>
#import <UniformTypeIdentifiers/UniformTypeIdentifiers.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <map>
#include <iterator>
#include <memory>
#include <mutex>
#include <set>
#include <tuple>
#include <string>
#include <utility>
#include <vector>

namespace {

enum State { Idle, Armed, Recording, Writing };
constexpr uint64_t kBudgetBytes = 3ull << 30;  // copies waiting to be written; past it, images are left out and said so
constexpr NSUInteger kMinSize = 16, kPngWidth = 768;
constexpr int kLimitSeconds = 90, kOtherPresents = 8;
constexpr uint32_t kDrawsAll = 64, kDrawsStride = 16;
constexpr NSUInteger kBufferBytes = 64 << 10;

struct Item {
    id<MTLBuffer> buffer;
    NSUInteger width, height, rowBytes;
    MTLPixelFormat format;
    std::string dir, file, line;
};

std::atomic<int> g_state{Idle};
std::atomic<uint64_t> g_used{0};
std::atomic<int> g_pending{0};
std::atomic<uint32_t> g_sequence{0};
std::atomic<uint32_t> g_gpuErrors{0};
std::mutex g_mutex;  // the images and the fields below
std::set<KImage*> g_images;
std::map<uint64_t, id<MTLBuffer>> g_memory;  // by GPU address
std::set<std::tuple<KPipeline*, uint64_t, uint32_t>> g_buffersSeen;  // in this pass
std::string g_dir;
KSwapchain* g_swapchain = nullptr;
int g_otherPresents = 0;
std::chrono::steady_clock::time_point g_started;

dispatch_queue_t writer() {
    static dispatch_queue_t q = dispatch_queue_create("ksa-metal.passdump", DISPATCH_QUEUE_SERIAL);
    return q;
}

// Bytes a texel and how many channels, for the formats it can read; 0 for the others
struct Layout { uint32_t bytes, channels; bool isFloat; };
Layout layoutOf(MTLPixelFormat f) {
    switch (f) {
    case MTLPixelFormatRGBA32Float: return {16, 4, true};
    case MTLPixelFormatRG32Float: return {8, 2, true};
    case MTLPixelFormatR32Float: case MTLPixelFormatDepth32Float: return {4, 1, true};
    case MTLPixelFormatRGBA16Float: return {8, 4, true};
    case MTLPixelFormatRG16Float: return {4, 2, true};
    case MTLPixelFormatR16Float: return {2, 1, true};
    case MTLPixelFormatRG11B10Float: case MTLPixelFormatRGB9E5Float: return {4, 3, true};
    case MTLPixelFormatRGBA8Unorm: case MTLPixelFormatRGBA8Unorm_sRGB:
    case MTLPixelFormatBGRA8Unorm: case MTLPixelFormatBGRA8Unorm_sRGB:
    case MTLPixelFormatRGB10A2Unorm: case MTLPixelFormatBGR10A2Unorm: return {4, 4, false};
    case MTLPixelFormatRGBA16Unorm: return {8, 4, false};
    case MTLPixelFormatRG16Unorm: return {4, 2, false};
    case MTLPixelFormatR16Unorm: case MTLPixelFormatDepth16Unorm: return {2, 1, false};
    case MTLPixelFormatRG8Unorm: return {2, 2, false};
    case MTLPixelFormatR8Unorm: return {1, 1, false};
    default: return {0, 0, false};
    }
}

float smallFloat(uint32_t bits, uint32_t mantissaBits) {
    uint32_t e = bits >> mantissaBits, m = bits & ((1u << mantissaBits) - 1);
    if (e == 31) return m ? NAN : INFINITY;
    if (e == 0) return std::ldexp((float)m, -14 - (int)mantissaBits);
    return std::ldexp(1.0f + (float)m / (float)(1u << mantissaBits), (int)e - 15);
}

void readTexel(MTLPixelFormat f, const uint8_t* p, float* v) {
    const auto* u16 = (const uint16_t*)p;
    uint32_t u = 0;
    switch (f) {
    case MTLPixelFormatRGBA32Float: memcpy(v, p, 16); break;
    case MTLPixelFormatRG32Float: memcpy(v, p, 8); break;
    case MTLPixelFormatR32Float: case MTLPixelFormatDepth32Float: memcpy(v, p, 4); break;
    case MTLPixelFormatRGBA16Float: for (int i = 0; i < 4; i++) v[i] = ((const __fp16*)p)[i]; break;
    case MTLPixelFormatRG16Float: for (int i = 0; i < 2; i++) v[i] = ((const __fp16*)p)[i]; break;
    case MTLPixelFormatR16Float: v[0] = *(const __fp16*)p; break;
    case MTLPixelFormatRG11B10Float:
        memcpy(&u, p, 4);
        v[0] = smallFloat(u & 0x7FF, 6);
        v[1] = smallFloat((u >> 11) & 0x7FF, 6);
        v[2] = smallFloat(u >> 22, 5);
        break;
    case MTLPixelFormatRGB9E5Float: {
        memcpy(&u, p, 4);
        float scale = std::ldexp(1.0f, (int)(u >> 27) - 24);
        v[0] = (u & 0x1FF) * scale;
        v[1] = ((u >> 9) & 0x1FF) * scale;
        v[2] = ((u >> 18) & 0x1FF) * scale;
        break;
    }
    case MTLPixelFormatRGBA8Unorm: case MTLPixelFormatRGBA8Unorm_sRGB:
        for (int i = 0; i < 4; i++) v[i] = p[i] / 255.0f;
        break;
    case MTLPixelFormatBGRA8Unorm: case MTLPixelFormatBGRA8Unorm_sRGB:
        v[0] = p[2] / 255.0f; v[1] = p[1] / 255.0f; v[2] = p[0] / 255.0f; v[3] = p[3] / 255.0f;
        break;
    case MTLPixelFormatRGB10A2Unorm: case MTLPixelFormatBGR10A2Unorm: {
        memcpy(&u, p, 4);
        float a = (u & 0x3FF) / 1023.0f, b = ((u >> 10) & 0x3FF) / 1023.0f, c = ((u >> 20) & 0x3FF) / 1023.0f;
        bool bgr = f == MTLPixelFormatBGR10A2Unorm;
        v[0] = bgr ? c : a; v[1] = b; v[2] = bgr ? a : c; v[3] = (u >> 30) / 3.0f;
        break;
    }
    case MTLPixelFormatRGBA16Unorm: for (int i = 0; i < 4; i++) v[i] = u16[i] / 65535.0f; break;
    case MTLPixelFormatRG16Unorm: for (int i = 0; i < 2; i++) v[i] = u16[i] / 65535.0f; break;
    case MTLPixelFormatR16Unorm: case MTLPixelFormatDepth16Unorm: v[0] = u16[0] / 65535.0f; break;
    case MTLPixelFormatRG8Unorm: v[0] = p[0] / 255.0f; v[1] = p[1] / 255.0f; break;
    case MTLPixelFormatR8Unorm: v[0] = p[0] / 255.0f; break;
    default: break;
    }
}

void appendLine(const std::string& dir, const std::string& line) {
    if (FILE* f = fopen((dir + "/passes.txt").c_str(), "a")) {
        fprintf(f, "%s\n", line.c_str());
        fclose(f);
    }
}

// A buffer's bytes, and how many read as floats aren't finite
void writeBuffer(const Item& item) {
    const auto* base = (const uint8_t*)item.buffer.contents;
    if (FILE* f = fopen((item.dir + "/" + item.file).c_str(), "wb")) {
        fwrite(base, 1, item.rowBytes, f);
        fclose(f);
    }
    uint64_t bad = 0;
    std::string where;
    for (NSUInteger i = 0; i + 4 <= item.rowBytes; i += 4) {
        float v;
        memcpy(&v, base + i, 4);
        if (std::isfinite(v)) continue;
        if (bad++ < 8) where += " " + std::to_string(i);
    }
    appendLine(item.dir, item.line + " nonfinite " + std::to_string(bad) + (bad ? " at" + where : ""));
}

// The numbers of all its pixels, and a PNG of some of them; runs on the writer's queue, one image at a time
void writeItem(const Item& item) {
    if (item.format == MTLPixelFormatInvalid) {
        writeBuffer(item);
        return;
    }
    Layout layout = layoutOf(item.format);
    const auto* base = (const uint8_t*)item.buffer.contents;
    NSUInteger step = std::max<NSUInteger>(1, item.width / kPngWidth);
    NSUInteger w = item.width / step, h = std::max<NSUInteger>(1, item.height / step);
    std::vector<uint8_t> png(w * h * 4);
    double sum[4] = {};
    uint64_t count[4] = {};
    float low[4], high[4];
    std::fill_n(low, 4, INFINITY);
    std::fill_n(high, 4, -INFINITY);
    uint64_t nan = 0, inf = 0;
    for (NSUInteger y = 0; y < item.height; y++) {
        const uint8_t* row = base + y * item.rowBytes;
        for (NSUInteger x = 0; x < item.width; x++) {
            float v[4] = {};
            readTexel(item.format, row + x * layout.bytes, v);
            bool isNan = false, isInf = false;
            for (uint32_t c = 0; c < layout.channels; c++) {
                if (std::isnan(v[c])) isNan = true;
                else if (std::isinf(v[c])) isInf = true;
                else {
                    sum[c] += v[c];
                    count[c]++;
                    low[c] = std::min(low[c], v[c]);
                    high[c] = std::max(high[c], v[c]);
                }
            }
            if (isNan) nan++;
            else if (isInf) inf++;
            if (x % step || y % step || x / step >= w || y / step >= h) continue;
            uint8_t* out = &png[(y / step * w + x / step) * 4];
            out[3] = 255;
            if (isNan || isInf) {
                out[0] = isNan ? 255 : 0; out[1] = isNan ? 0 : 255; out[2] = 255;
                continue;
            }
            uint32_t shown = std::min<uint32_t>(layout.channels, 3);
            for (uint32_t c = 0; c < 3; c++) {
                float value = std::max(0.0f, v[shown == 1 ? 0 : (c < shown ? c : 0)]);
                if (shown == 2 && c == 2) value = 0;
                if (layout.isFloat) value = std::pow(value / (1 + value), 1 / 2.2f);
                out[c] = (uint8_t)(std::min(value, 1.0f) * 255 + 0.5f);
            }
        }
    }
    CGColorSpaceRef cs = CGColorSpaceCreateWithName(kCGColorSpaceSRGB);
    CGContextRef ctx = CGBitmapContextCreate(png.data(), w, h, 8, w * 4, cs, kCGImageAlphaNoneSkipLast);
    CGImageRef image = CGBitmapContextCreateImage(ctx);
    std::string path = item.dir + "/" + item.file;
    CFURLRef url = CFURLCreateFromFileSystemRepresentation(nullptr, (const UInt8*)path.c_str(), path.size(), false);
    if (CGImageDestinationRef dest = CGImageDestinationCreateWithURL(url, (__bridge CFStringRef)UTTypePNG.identifier, 1, nullptr)) {
        CGImageDestinationAddImage(dest, image, nullptr);
        CGImageDestinationFinalize(dest);
        CFRelease(dest);
    }
    CFRelease(url);
    CGImageRelease(image);
    CGContextRelease(ctx);
    CGColorSpaceRelease(cs);
    char numbers[400];
    int n = snprintf(numbers, sizeof numbers, " nan %llu inf %llu", (unsigned long long)nan, (unsigned long long)inf);
    for (uint32_t c = 0; c < layout.channels && n < (int)sizeof numbers - 60; c++)
        n += count[c] ? snprintf(numbers + n, sizeof numbers - n, " c%u %.4g..%.4g mean %.4g", c, low[c], high[c], sum[c] / count[c])
                      : snprintf(numbers + n, sizeof numbers - n, " c%u no finite value", c);
    appendLine(item.dir, item.line + numbers);
}

// A copy of one level and slice or depth plane into a new buffer, in this encoder; when it can't be made, a line says why
void copyTexture(id<MTLBlitCommandEncoder> blit, id<MTLTexture> t, NSUInteger level, NSUInteger slice, NSUInteger plane,
                 const std::string& name, const std::string& dir, std::vector<Item>& items) {
    NSUInteger width = std::max<NSUInteger>(1, t.width >> level), height = std::max<NSUInteger>(1, t.height >> level);
    std::string what = name + " " + std::to_string(width) + "x" + std::to_string(height) + " format " + std::to_string((int)t.pixelFormat) +
                       " level " + std::to_string(level) + " slice " + std::to_string(slice) + " plane " + std::to_string(plane) +
                       " of " + std::to_string(t.width) + "x" + std::to_string(t.height);
    Layout layout = layoutOf(t.pixelFormat);
    const char* why = !layout.bytes ? "a format this can't read"
        : t.sampleCount > 1 ? "multisampled, with no resolve image"
        : t.storageMode == MTLStorageModeMemoryless ? "memoryless"
        : t.textureType != MTLTextureType2D && t.textureType != MTLTextureType2DArray && t.textureType != MTLTextureTypeCube &&
                  t.textureType != MTLTextureType3D ? "a texture type this can't read"
        : nullptr;
    if (why) {
        appendLine(dir, what + ": left out, " + why);
        return;
    }
    NSUInteger rowBytes = width * layout.bytes, size = rowBytes * height;
    if (g_used.fetch_add(size) + size > kBudgetBytes) {
        g_used -= size;
        appendLine(dir, what + ": left out, over the budget");
        return;
    }
    id<MTLBuffer> buffer = [t.device newBufferWithLength:size options:MTLResourceStorageModeShared];
    if (!buffer) {
        g_used -= size;
        appendLine(dir, what + ": left out, no memory");
        return;
    }
    [blit copyFromTexture:t sourceSlice:slice sourceLevel:level sourceOrigin:MTLOriginMake(0, 0, plane)
               sourceSize:MTLSizeMake(width, height, 1) toBuffer:buffer destinationOffset:0
   destinationBytesPerRow:rowBytes destinationBytesPerImage:size];
    char where[160] = "";
    if (t.heap) snprintf(where, sizeof where, " heap %p offset %llu size %llu", (__bridge void*)t.heap, (unsigned long long)t.heapOffset, (unsigned long long)t.allocatedSize);
    char file[64];
    snprintf(file, sizeof file, "%04u.png", g_sequence++);
    char line[160];
    snprintf(line, sizeof line, " texture %p usage %lu depth %lu layers %lu", (__bridge void*)t, (unsigned long)t.usage, (unsigned long)t.depth, (unsigned long)t.arrayLength);
    items.push_back({buffer, width, height, rowBytes, t.pixelFormat, dir, file, std::string(file) + " " + what + line + where});
}

// One command buffer's copies. It ends with its last owner: the handler's block when the buffer ran, the buffer itself when
// it is dropped without being submitted, so a dropped recording can't hold the dump back
struct Batch {
    std::vector<Item> items;
    ~Batch() {
        for (const Item& item : items) g_used -= item.rowBytes * item.height;
        if (--g_pending == 0) {
            int writing = Writing;
            if (g_state.compare_exchange_strong(writing, Idle)) KLOG(0, "pass dump written");
        }
    }
};

void writeWhenDone(id<MTLCommandBuffer> cb, std::vector<Item> items) {
    g_pending++;
    auto batch = std::make_shared<Batch>();
    batch->items = std::move(items);
    [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
        // After a GPU error the copies never ran: their buffers hold nothing that was on the GPU
        if (done.status == MTLCommandBufferStatusError) kmtlNoteGpuError();
        std::string failed = done.status == MTLCommandBufferStatusError ? std::string(": left out, its command buffer failed: ") + done.error.localizedDescription.UTF8String : "";
        dispatch_async(writer(), ^{
            @autoreleasepool {
                for (const Item& item : batch->items) {
                    if (failed.empty()) writeItem(item);
                    else appendLine(item.dir, item.line + failed);
                }
            }
        });
    }];
}

const char* loadName(MTLLoadAction a) {
    return a == MTLLoadActionLoad ? "load" : a == MTLLoadActionClear ? "clear" : "dontcare";
}

const char* storeName(MTLStoreAction a) {
    switch (a) {
    case MTLStoreActionDontCare: return "dontcare";
    case MTLStoreActionStore: return "store";
    case MTLStoreActionMultisampleResolve: return "resolve";
    case MTLStoreActionStoreAndMultisampleResolve: return "store+resolve";
    default: return "unknown";
    }
}

// A render pass's images, in a blit that nothing overlaps. Between its draws everything is stored, whatever the pass ends with
void copyPassImages(KCommandBuffer* cb, MTLRenderPassDescriptor* pass, const std::string& name, const std::string& dir, bool passEnd) {
    struct Source { id<MTLTexture> texture; NSUInteger level, slice, plane; };
    std::vector<Source> sources;
    for (int i = 0; i < 8; i++) {
        MTLRenderPassColorAttachmentDescriptor* a = pass.colorAttachments[i];
        if (!a.texture) continue;
        MTLStoreAction store = cb->deferredStores ? cb->colorStores[i] : a.storeAction;
        if (a.resolveTexture) sources.push_back({a.resolveTexture, a.resolveLevel, a.resolveSlice, a.resolveDepthPlane});
        else if (passEnd && store == MTLStoreActionDontCare) appendLine(dir, "[" + name + "] colour" + std::to_string(i) + ": left out, the pass does not store it");
        else sources.push_back({a.texture, a.level, a.slice, a.depthPlane});
    }
    if (sources.empty()) return;
    id<MTLEvent> event = [cb->device->mtl newEvent];
    [cb->mtl encodeSignalEvent:event value:1];
    [cb->mtl encodeWaitForEvent:event value:1];
    id<MTLBlitCommandEncoder> blit = [cb->mtl blitCommandEncoder];
    blit.label = @"pass dump";
    std::vector<Item> items;
    for (const Source& s : sources) copyTexture(blit, s.texture, s.level, s.slice, s.plane, "[" + name + "]", dir, items);
    [blit endEncoding];
    [cb->mtl encodeSignalEvent:event value:2];
    [cb->mtl encodeWaitForEvent:event value:2];
    writeWhenDone(cb->mtl, std::move(items));
}

// The buffers bound for a draw, as its sets say, in a blit of their own
void copyDrawBuffers(KCommandBuffer* cb, KPipeline* pipeline, const std::string& name, const std::string& dir) {
    struct Source { id<MTLBuffer> buffer; NSUInteger offset, size; std::string line; };
    std::vector<Source> sources;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (uint32_t s = 0; s < kMaxSets; s++) {
            const KBoundSet& bound = cb->graphicsSets[s];
            if (!bound.buffer || !bound.layout || !bound.slots) continue;
            const auto* slots = (const uint64_t*)bound.slots;
            const auto* sizes = (const uint32_t*)(bound.slots + bound.layout->slotCount * 8);
            for (const KBinding& b : bound.layout->bindings) {
                if (b.type != VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER && b.type != VK_DESCRIPTOR_TYPE_STORAGE_BUFFER &&
                    b.type != VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC && b.type != VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC) continue;
                for (uint32_t e = 0; e < b.count; e++) {
                    uint64_t address = slots[b.id + e];
                    uint32_t size = sizes[b.id + e];
                    if (!address || !size || !g_buffersSeen.insert({pipeline, address, size}).second) continue;
                    std::string what = "[" + name + "] set " + std::to_string(s) + " binding " + std::to_string(b.binding) +
                                       " element " + std::to_string(e) + " size " + std::to_string(size);
                    auto memory = g_memory.upper_bound(address);
                    if (memory == g_memory.begin() || address - std::prev(memory)->first >= std::prev(memory)->second.length) {
                        appendLine(dir, "buffer " + what + ": left out, in no memory");
                        continue;
                    }
                    --memory;
                    NSUInteger offset = address - memory->first;
                    NSUInteger bytes = std::min<NSUInteger>({size, kBufferBytes, memory->second.length - offset});
                    sources.push_back({memory->second, offset, bytes, what});
                }
            }
        }
    }
    if (sources.empty()) return;
    id<MTLEvent> event = [cb->device->mtl newEvent];
    id<MTLBlitCommandEncoder> blit = [cb->mtl blitCommandEncoder];
    blit.label = @"pass dump";
    std::vector<Item> items;
    for (const Source& s : sources) {
        if (g_used.fetch_add(s.size) + s.size > kBudgetBytes) {
            g_used -= s.size;
            appendLine(dir, "buffer " + s.line + ": left out, over the budget");
            continue;
        }
        id<MTLBuffer> copy = [cb->device->mtl newBufferWithLength:s.size options:MTLResourceStorageModeShared];
        if (!copy) {
            g_used -= s.size;
            appendLine(dir, "buffer " + s.line + ": left out, no memory");
            continue;
        }
        [blit copyFromBuffer:s.buffer sourceOffset:s.offset toBuffer:copy destinationOffset:0 size:s.size];
        char file[64];
        snprintf(file, sizeof file, "%04u.bin", g_sequence++);
        items.push_back({copy, s.size, 1, s.size, MTLPixelFormatInvalid, dir, file, std::string(file) + " buffer " + s.line});
    }
    [blit endEncoding];
    [cb->mtl encodeSignalEvent:event value:1];
    [cb->mtl encodeWaitForEvent:event value:1];
    writeWhenDone(cb->mtl, std::move(items));
}

// Past the limit, a dump that didn't end (no present, a recording that never ran) gives way to the next; copies still to
// come go on to their own folder
void giveUpIfLate() {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_state == Idle || std::chrono::steady_clock::now() - g_started < std::chrono::seconds(kLimitSeconds)) return;
    KERR("pass dump: not finished after %d s, given up", kLimitSeconds);
    appendLine(g_dir, "-- given up: this dump did not finish");
    g_state = Idle;
}

}  // namespace

void kmtlPassDumpTrack(KImage* image) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_images.insert(image);
}

void kmtlPassDumpForget(KImage* image) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_images.erase(image);
}

void kmtlPassDumpTrackMemory(id<MTLBuffer> whole) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_memory[whole.gpuAddress] = whole;
}

void kmtlPassDumpForgetMemory(id<MTLBuffer> whole) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_memory.erase(whole.gpuAddress);
}

void kmtlPassDumpSwapchainGone(KSwapchain* swapchain) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_swapchain == swapchain) g_swapchain = nullptr;
}

bool kmtlPassDumpRecording() { return g_state == Recording; }

// After a render pass, or a compute encoder (a line only). A new event each time: with a shared one, a value signalled by
// a command buffer submitted earlier would satisfy this one's wait, and the copy would overlap the pass. The signal comes
// once everything before it has finished, and nothing after the wait starts until then.
void kmtlPassDumpEncoder(KCommandBuffer* cb, const std::string& name, MTLRenderPassDescriptor* pass) {
    giveUpIfLate();
    std::string dir;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        dir = g_dir;
    }
    // The original load actions (a resumed pass's descriptor says load) and the store actions the pass ends with
    std::string actions;
    for (int i = 0; pass && i < 8; i++) {
        if (!pass.colorAttachments[i].texture) continue;
        MTLStoreAction store = cb->deferredStores ? cb->colorStores[i] : pass.colorAttachments[i].storeAction;
        actions += " colour" + std::to_string(i) + " load " + loadName(cb->dumpLoads[i]) + " store " + storeName(store) +
                   " samples " + std::to_string((int)pass.colorAttachments[i].texture.sampleCount);
    }
    if (pass && pass.depthAttachment.texture) actions += std::string(" depth load ") + loadName(cb->dumpDepthLoad);
    appendLine(dir, "-- " + name + actions);
    if (pass) copyPassImages(cb, pass, name, dir, true);
    {
        // A pass's buffers show anew in the next: a pass in between may have written them
        std::lock_guard<std::mutex> lock(g_mutex);
        g_buffersSeen.clear();
    }
}

// Splits the pass after a draw and copies what the draws so far left. A multisampled image can't be copied: each split resolves
// it, which the pass's own resolve overwrites at its end.
void kmtlPassDumpAfterDraw(KCommandBuffer* cb, KPipeline* pipeline) {
    uint32_t draw = cb->dumpDraws++;
    MTLRenderPassDescriptor* pass = cb->dumpDesc;
    bool colour = false, tileOnly = false;
    for (int i = 0; i < 8; i++) {
        id<MTLTexture> t = pass.colorAttachments[i].texture;
        colour |= t != nil;
        tileOnly |= t.storageMode == MTLStorageModeMemoryless;
    }
    tileOnly |= pass.depthAttachment.texture.storageMode == MTLStorageModeMemoryless ||
                pass.stencilAttachment.texture.storageMode == MTLStorageModeMemoryless;
    if (!colour || (draw >= kDrawsAll && draw % kDrawsStride)) return;
    // A split would lose a memoryless image, and draws must not use up what the passes after them need
    if (tileOnly || g_used > kBudgetBytes / 2) {
        if (!std::exchange(cb->dumpDrawsSkipped, true)) {
            std::lock_guard<std::mutex> lock(g_mutex);
            appendLine(g_dir, "[" + cb->dumpName + "] no images or buffers after its draws: " + (tileOnly ? "it has a memoryless image" : "half of the budget is used"));
        }
        return;
    }
    cb->dumpResolves = true;
    cb->suspendRender();
    cb->dumpResolves = false;
    std::string dir;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        dir = g_dir;
    }
    copyPassImages(cb, pass, cb->dumpName + " after draw " + std::to_string(draw) + " " + (pipeline ? pipeline->profName : ""), dir, false);
    copyDrawBuffers(cb, pipeline, cb->dumpName + " after draw " + std::to_string(draw), dir);
    cb->resumeRender();
}

// At a present: the frame after the one the request came in is the one dumped, and its present ends it with all the images.
// A pop-out window's presents don't end it, unless the first window to present stops presenting.
void kmtlPassDumpAtPresent(KQueue* queue, KSwapchain* swapchain, id<MTLCommandBuffer> cb) {
    int state = g_state;
    if (state != Armed && state != Recording) return;
    std::vector<id<MTLTexture>> textures;
    std::string dir;
    size_t tiny = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (state == Armed) {
            g_swapchain = swapchain;
            g_otherPresents = 0;
            g_state = Recording;
            return;
        }
        if (g_swapchain && g_swapchain != swapchain && ++g_otherPresents < kOtherPresents) return;
        dir = g_dir;
        for (KImage* image : g_images) {
            if (!image->mtl) continue;
            if (image->mtl.width >= kMinSize && image->mtl.height >= kMinSize) textures.push_back(image->mtl);
            else tiny++;
        }
    }
    std::sort(textures.begin(), textures.end(), [](id<MTLTexture> a, id<MTLTexture> b) { return a.width * a.height > b.width * b.height; });
    appendLine(dir, "-- the frame's end: the images, " + std::to_string(tiny) + " under " + std::to_string(kMinSize) + " pixels left out");
    id<MTLBlitCommandEncoder> blit = [cb blitCommandEncoder];
    blit.label = @"pass dump";
    [blit waitForFence:queue->chain];
    std::vector<Item> items;
    for (id<MTLTexture> t : textures) copyTexture(blit, t, 0, 0, t.textureType == MTLTextureType3D ? t.depth / 2 : 0, "image", dir, items);
    // The next frame's encoders wait for this one: without it they could write these images while they are copied
    [blit updateFence:queue->chain];
    [blit endEncoding];
    writeWhenDone(cb, std::move(items));  // before the state: the last batch to finish, which this one is, needs both
    g_state = Writing;
}

KMTL_EXPORT bool ksaMtlPassDump(const char* dir) {
    giveUpIfLate();
    // The GPU refuses this process's work after a hang: a dump would hold nothing
    if (g_gpuErrors) return false;
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_state != Idle) return false;
    g_dir = dir;
    g_buffersSeen.clear();
    g_sequence = 0;
    g_started = std::chrono::steady_clock::now();
    [[NSFileManager defaultManager] createDirectoryAtPath:@(dir) withIntermediateDirectories:YES attributes:nil error:nil];
    g_state = Armed;
    return true;
}

KMTL_EXPORT bool ksaMtlPassDumping() {
    giveUpIfLate();
    return g_state != Idle;
}

KMTL_EXPORT uint32_t ksaMtlGpuErrors() { return g_gpuErrors; }

// The share of a sample of the image with what clean views don't show, in percent. Measured on 4400 screenshots of 44 views:
// white at most 14 % on a clean view and from 50 % on a broken one, the others only on broken ones
namespace {

constexpr uint64_t kWatchEvery = 30;
constexpr NSUInteger kWatchStride = 8, kWatchMinWidth = 640;
constexpr double kWatchBlack = 99.9, kWatchColour = 0.1;
constexpr int kWatchDumps = 3, kWatchDumpSeconds = 10, kWatchBlackLooks = 4;

struct WatchSample { double white, black, green, primary; };

WatchSample sampleImage(const uint8_t* bgra, NSUInteger width, NSUInteger height) {
    uint64_t n = 0, white = 0, black = 0, green = 0, primary = 0;
    for (NSUInteger y = 0; y < height; y += kWatchStride) {
        const uint8_t* row = bgra + y * width * 4;
        for (NSUInteger x = 0; x < width; x += kWatchStride) {
            int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
            n++;
            white += r >= 250 && g >= 250 && b >= 250;
            black += r <= 1 && g <= 1 && b <= 1;
            green += g > 60 && r * 6 < g && b * 6 < g;
            bool full[3] = {r >= 240, g >= 240, b >= 240}, none[3] = {r <= 16, g <= 16, b <= 16};
            primary += (full[0] && full[1] && none[2]) || (full[0] && none[1] && none[2]) ||
                       (none[0] && full[1] && none[2]) || (none[0] && none[1] && full[2]);
        }
    }
    double scale = 100.0 / std::max<uint64_t>(n, 1);
    return {white * scale, black * scale, green * scale, primary * scale};
}

void watchLine(const std::string& text) {
    if (FILE* f = fopen((std::string(kmtlConfig().watch) + "/watch.txt").c_str(), "a")) {
        time_t now = time(nullptr);
        char when[32];
        strftime(when, sizeof when, "%H:%M:%S", localtime(&now));
        fprintf(f, "%s %s\n", when, text.c_str());
        fclose(f);
    }
}

id<MTLBuffer> g_watchBuffer;
std::atomic<bool> g_watchBusy{false};
uint64_t g_watchPresents = 0;
// On the writer's queue only
bool g_watchBroken = false, g_watchSeenClean = false;
int g_watchBlackLooks = 0;
int g_watchDumped = 0;
std::chrono::steady_clock::time_point g_watchLastDump, g_watchBrokenSince;

void watchLook(id<MTLBuffer> buffer, NSUInteger width, NSUInteger height, uint64_t present) {
    WatchSample s = sampleImage((const uint8_t*)buffer.contents, width, height);
    // A fade or a loading screen is black too: black counts when it stays, and for one dump
    g_watchBlackLooks = s.black >= kWatchBlack ? g_watchBlackLooks + 1 : 0;
    bool colours = s.white >= kmtlConfig().watchWhite || s.green >= kWatchColour || s.primary >= kWatchColour;
    bool broken = colours || g_watchBlackLooks >= kWatchBlackLooks;
    auto now = std::chrono::steady_clock::now();
    if (FILE* f = fopen((std::string(kmtlConfig().watch) + "/watch.csv").c_str(), "a")) {
        fseek(f, 0, SEEK_END);
        if (ftell(f) == 0) fprintf(f, "present,seconds,white,black,green,primary\n");
        fprintf(f, "%llu,%.2f,%.2f,%.2f,%.2f,%.2f\n", (unsigned long long)present, std::chrono::duration<double>(now.time_since_epoch()).count(), s.white, s.black, s.green, s.primary);
        fclose(f);
    }
    // Loading and fades can be black: only an image that follows a clean one counts (a limit of 0 is a test, always broken)
    g_watchSeenClean |= !broken;
    if (!g_watchSeenClean && kmtlConfig().watchWhite > 0) return;
    if (broken != g_watchBroken) {
        char line[160];
        if (broken) {
            g_watchBrokenSince = now;
            snprintf(line, sizeof line, "present %llu broken: white %.2f %% black %.2f %% green %.2f %% primary %.2f %%", (unsigned long long)present, s.white, s.black, s.green, s.primary);
        } else {
            snprintf(line, sizeof line, "present %llu clear after %.1f s", (unsigned long long)present, std::chrono::duration<double>(now - g_watchBrokenSince).count());
        }
        watchLine(line);
        g_watchBroken = broken;
    }
    if (!broken || g_watchDumped >= (colours ? kWatchDumps : 1) || (g_watchDumped && now - g_watchLastDump < std::chrono::seconds(kWatchDumpSeconds)))
        return;
    std::string name = "watch_" + std::to_string(g_watchDumped + 1);
    std::string dir = std::string(kmtlConfig().watch) + "/" + name;
    if (!ksaMtlPassDump(dir.c_str())) {
        watchLine("present " + std::to_string(present) + " no pass dump: another is running or the GPU has failed");
        return;
    }
    g_watchDumped++;
    g_watchLastDump = now;
    kmtlWritePNG(buffer, width, height, true, std::string(kmtlConfig().watch) + "/" + name + ".png");
    watchLine("present " + std::to_string(present) + " " + name + ": the image, and the next frame's passes");
}

}  // namespace

void kmtlWatchAtPresent(KQueue* queue, KImage* image, id<MTLCommandBuffer> cb) {
    if (!kmtlConfig().watch || !image->mtl || image->mtl.width < kWatchMinWidth || ++g_watchPresents % kWatchEvery) return;
    id<MTLTexture> t = image->mtl;
    if (t.pixelFormat != MTLPixelFormatBGRA8Unorm && t.pixelFormat != MTLPixelFormatBGRA8Unorm_sRGB) {
        KERR_ONCE("watch: this image's format can't be read: the watch does nothing");
        return;
    }
    NSUInteger size = t.width * t.height * 4;
    if (g_watchBusy.exchange(true)) return;
    static std::once_flag folder;
    std::call_once(folder, [] {
        [[NSFileManager defaultManager] createDirectoryAtPath:@(kmtlConfig().watch) withIntermediateDirectories:YES attributes:nil error:nil];
    });
    if (g_watchBuffer.length != size) g_watchBuffer = [t.device newBufferWithLength:size options:MTLResourceStorageModeShared];
    id<MTLBuffer> buffer = g_watchBuffer;
    if (!buffer) {
        g_watchBusy = false;
        return;
    }
    id<MTLBlitCommandEncoder> blit = [cb blitCommandEncoder];
    blit.label = @"watch";
    [blit waitForFence:queue->chain];
    [blit copyFromTexture:t sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0) sourceSize:MTLSizeMake(t.width, t.height, 1)
                 toBuffer:buffer destinationOffset:0 destinationBytesPerRow:t.width * 4 destinationBytesPerImage:size];
    [blit endEncoding];
    NSUInteger width = t.width, height = t.height;
    uint64_t present = g_watchPresents;
    [cb addCompletedHandler:^(id<MTLCommandBuffer> done) {
        if (done.status == MTLCommandBufferStatusError) {
            kmtlNoteGpuError();
            g_watchBusy = false;
            return;
        }
        dispatch_async(writer(), ^{
            @autoreleasepool {
                watchLook(buffer, width, height, present);
            }
            g_watchBusy = false;
        });
    }];
}

void kmtlNoteGpuError() {
    if (g_gpuErrors++ == 0 && kmtlConfig().watch) watchLine("the GPU failed a command buffer: nothing after this can be dumped");
}
