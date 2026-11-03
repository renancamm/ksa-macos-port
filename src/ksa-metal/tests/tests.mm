// KSA Metal's tests, which run without the game (README.md in this folder):
//
//   ksa-metal-tests smoke
//       The driver on its own: an instance, a device, buffers in host-visible memory, a command buffer that fills one
//       and copies it into the other, a submit and a fence, and the copy read back.
//   ksa-metal-tests corpus [--update] [--out <dir>] <corpus>
//       Every translation of a shader corpus (KSA_MTL_SHADER_CORPUS) again: its key from the recorded inputs (the
//       record holds everything that shapes it), its MSL against the recorded one, and Metal's compile of it. A changed
//       translation goes to <dir> as <key>.recorded.txt and <key>.now.txt (the entry point and flags, then the MSL);
//       --update records it instead.
//
// Exits with 1 when a test fails.
#include "shaders/translate.h"
#include <mach-o/dyld.h>
#include <sys/stat.h>
#include <unistd.h>
#include <thread>

static int fail(const char* what) {
    fprintf(stderr, "FAILED: %s\n", what);
    return 1;
}

// A device on the Mac's GPU, without a window
static bool makeDevice(VkInstance* instance, VkPhysicalDevice* physical, VkDevice* device) {
    VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};
    app.apiVersion = VK_API_VERSION_1_3;
    VkInstanceCreateInfo ici{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};
    ici.pApplicationInfo = &app;
    if (vkCreateInstance(&ici, nullptr, instance) != VK_SUCCESS) return false;
    uint32_t n = 1;
    if (vkEnumeratePhysicalDevices(*instance, &n, physical) < 0 || n == 0) return false;
    float priority = 1;
    VkDeviceQueueCreateInfo qci{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};
    qci.queueCount = 1;
    qci.pQueuePriorities = &priority;
    VkDeviceCreateInfo dci{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};
    dci.queueCreateInfoCount = 1;
    dci.pQueueCreateInfos = &qci;
    return vkCreateDevice(*physical, &dci, nullptr, device) == VK_SUCCESS;
}

// A buffer of <size> bytes in host-visible memory, mapped
static bool hostBuffer(VkPhysicalDevice physical, VkDevice device, VkDeviceSize size, VkBuffer* buffer, VkDeviceMemory* memory,
                       uint32_t** mapped) {
    VkBufferCreateInfo bci{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    bci.size = size;
    bci.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    if (vkCreateBuffer(device, &bci, nullptr, buffer) != VK_SUCCESS) return false;
    VkMemoryRequirements req;
    vkGetBufferMemoryRequirements(device, *buffer, &req);
    VkPhysicalDeviceMemoryProperties props;
    vkGetPhysicalDeviceMemoryProperties(physical, &props);
    const VkMemoryPropertyFlags want = VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT;
    uint32_t type = UINT32_MAX;
    for (uint32_t i = 0; i < props.memoryTypeCount && type == UINT32_MAX; i++)
        if ((req.memoryTypeBits & (1u << i)) && (props.memoryTypes[i].propertyFlags & want) == want) type = i;
    if (type == UINT32_MAX) return false;
    VkMemoryAllocateInfo mai{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    mai.allocationSize = req.size;
    mai.memoryTypeIndex = type;
    return vkAllocateMemory(device, &mai, nullptr, memory) == VK_SUCCESS &&
           vkBindBufferMemory(device, *buffer, *memory, 0) == VK_SUCCESS &&
           vkMapMemory(device, *memory, 0, size, 0, (void**)mapped) == VK_SUCCESS;
}

static int smoke() {
    VkInstance instance;
    VkPhysicalDevice physical;
    VkDevice device;
    if (!makeDevice(&instance, &physical, &device)) return fail("no instance or device");
    const VkDeviceSize size = 1 << 20;
    VkBuffer a, b;
    VkDeviceMemory am, bm;
    uint32_t *ap, *bp;
    if (!hostBuffer(physical, device, size, &a, &am, &ap) || !hostBuffer(physical, device, size, &b, &bm, &bp))
        return fail("no host-visible buffers");
    memset(bp, 0, size);

    VkQueue queue;
    vkGetDeviceQueue(device, 0, 0, &queue);
    VkCommandPoolCreateInfo pci{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};
    VkCommandPool pool;
    if (vkCreateCommandPool(device, &pci, nullptr, &pool) != VK_SUCCESS) return fail("no command pool");
    VkCommandBufferAllocateInfo cai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};
    cai.commandPool = pool;
    cai.commandBufferCount = 1;
    VkCommandBuffer cb;
    if (vkAllocateCommandBuffers(device, &cai, &cb) != VK_SUCCESS) return fail("no command buffer");
    VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};
    vkBeginCommandBuffer(cb, &begin);
    vkCmdFillBuffer(cb, a, 0, size, 0x4b53414du);
    VkMemoryBarrier barrier{VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    barrier.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    barrier.dstAccessMask = VK_ACCESS_TRANSFER_READ_BIT;
    vkCmdPipelineBarrier(cb, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 1, &barrier, 0, nullptr, 0, nullptr);
    VkBufferCopy region{0, 0, size / 2};
    vkCmdCopyBuffer(cb, a, b, 1, &region);
    if (vkEndCommandBuffer(cb) != VK_SUCCESS) return fail("the command buffer did not end");
    VkFenceCreateInfo fci{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};
    VkFence fence;
    vkCreateFence(device, &fci, nullptr, &fence);
    VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};
    submit.commandBufferCount = 1;
    submit.pCommandBuffers = &cb;
    if (vkQueueSubmit(queue, 1, &submit, fence) != VK_SUCCESS) return fail("the submit failed");
    if (vkWaitForFences(device, 1, &fence, VK_TRUE, 10ull * 1000 * 1000 * 1000) != VK_SUCCESS) return fail("the fence never signalled");
    for (VkDeviceSize i = 0; i < size / 4; i++) {
        uint32_t want = i < size / 8 ? 0x4b53414du : 0;
        if (bp[i] != want) {
            fprintf(stderr, "word %llu is %08x, not %08x\n", (unsigned long long)i, bp[i], want);
            return fail("the copy reads back wrong");
        }
    }
    vkDestroyFence(device, fence, nullptr);
    vkDestroyCommandPool(device, pool, nullptr);
    vkDestroyBuffer(device, a, nullptr);
    vkDestroyBuffer(device, b, nullptr);
    vkFreeMemory(device, am, nullptr);
    vkFreeMemory(device, bm, nullptr);
    vkDestroyDevice(device, nullptr);
    vkDestroyInstance(instance, nullptr);
    printf("smoke: a fill and a copy on the GPU read back right\n");
    return 0;
}

static void writeFile(const std::string& path, const std::string& data) {
    if (FILE* f = fopen(path.c_str(), "wb")) {
        fwrite(data.data(), 1, data.size(), f);
        fclose(f);
    }
}

static int corpus(const std::string& dir, const std::string& outDir, bool update) {
    std::vector<uint64_t> keys = kmtlCorpusKeys(dir);
    if (keys.empty()) return fail(("no shader corpus in " + dir).c_str());
    VkInstance instance;
    VkPhysicalDevice physical;
    VkDevice device;
    if (!makeDevice(&instance, &physical, &device)) return fail("no instance or device");
    auto* dev = (KDevice*)device;
    std::string presets = kmtlConfig().spec ? kmtlConfig().spec : "";

    std::mutex m;
    std::vector<std::string> failed, changed;
    std::atomic<size_t> next{0}, skipped{0};
    auto t0 = std::chrono::steady_clock::now();
    auto work = [&] {
        for (size_t i; (i = next++) < keys.size();) {
            @autoreleasepool {
                char name[17];
                snprintf(name, sizeof(name), "%016llx", (unsigned long long)keys[i]);
                auto e = std::make_unique<KCorpusEntry>();
                std::string err;
                auto failure = [&](const std::string& what) {
                    std::lock_guard<std::mutex> lock(m);
                    failed.push_back(std::string(name) + ": " + what);
                };
                if (!kmtlCorpusLoad(dir, keys[i], e.get(), &err)) {
                    failure(err);
                    continue;
                }
                // Recorded with other settings: its translation here would differ
                if (e->constantArgumentBuffers != kmtlConfig().constantArgumentBuffers ||
                    e->unlitShadowSkip != kmtlConfig().unlitShadowSkip || e->specPresets != presets) {
                    skipped++;
                    continue;
                }
                KPipelineLayout* layout = e->hasLayout ? &e->layout : nullptr;
                const VkSpecializationInfo* spec = e->hasSpec ? &e->spec : nullptr;
                if (kmtlTranslationKey(&e->module, e->entry.c_str(), e->model, layout, spec, e->options) != e->key) {
                    failure("its key differs: the record leaves out an input, or the translator changed its version "
                            "(record the corpus again)");
                    continue;
                }
                KTranslated now;
                if (!kmtlTranslate(&e->module, e->entry.c_str(), e->model, layout, spec, e->options, &now, &err)) {
                    failure(err);
                    continue;
                }
                std::string was = kmtlTranslatedText(e->recorded), is = kmtlTranslatedText(now);
                if (was != is) {
                    if (update) {
                        writeFile(dir + "/" + name + ".out", is);
                    } else {
                        mkdir(outDir.c_str(), 0755);
                        writeFile(outDir + "/" + name + ".recorded.txt", was);
                        writeFile(outDir + "/" + name + ".now.txt", is);
                    }
                    std::lock_guard<std::mutex> lock(m);
                    changed.push_back(name);
                }
                id<MTLLibrary> lib = kmtlCompileStageMSL(dev, now.msl, &err);
                if (!lib) {
                    failure("Metal's compile failed: " + err.substr(0, 400));
                } else if (![lib newFunctionWithName:[NSString stringWithUTF8String:now.entry.c_str()]]) {
                    failure("no function " + now.entry);
                }
            }
        }
    };
    std::vector<std::thread> threads;
    for (unsigned t = 0; t < std::max(1u, std::thread::hardware_concurrency()); t++) threads.emplace_back(work);
    for (auto& t : threads) t.join();
    double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();

    std::sort(failed.begin(), failed.end());
    for (auto& f : failed) fprintf(stderr, "  %s\n", f.c_str());
    printf("corpus: %zu translations in %.1f s: %zu changed, %zu failed, %zu recorded with other settings (skipped)\n",
           keys.size(), seconds, changed.size(), failed.size(), skipped.load());
    if (!changed.empty())
        printf("  %s\n", update ? "the changed ones are recorded now" : ("the changed ones, before and now: " + outDir).c_str());
    vkDestroyDevice(device, nullptr);
    vkDestroyInstance(instance, nullptr);
    if (skipped.load() == keys.size()) return fail("every translation was recorded with other settings: nothing compared");
    return failed.empty() && (changed.empty() || update) ? 0 : 1;
}

int main(int argc, char** argv) {
    // Neither the shader cache on disk nor its prewarm: the tests translate and compile everything themselves. The
    // driver reads its settings before main, so the program starts again with the cache off.
    const char* cache = getenv("KSA_MTL_SHADER_CACHE");
    if (!cache || strcmp(cache, "0") != 0) {
        char path[PATH_MAX];
        uint32_t size = sizeof(path);
        setenv("KSA_MTL_SHADER_CACHE", "0", 1);
        if (_NSGetExecutablePath(path, &size) == 0) execv(path, argv);
        return fail("could not start again with the shader cache off");
    }
    @autoreleasepool {
        std::string command = argc > 1 ? argv[1] : "";
        if (command == "smoke" && argc == 2) return smoke();
        if (command == "corpus") {
            bool update = false;
            const char* tmp = getenv("TMPDIR");
            std::string out = std::string(tmp ? tmp : "/tmp") + "/ksa-metal-corpus", dir;
            for (int i = 2; i < argc; i++) {
                std::string a = argv[i];
                if (a == "--update") update = true;
                else if (a == "--out" && i + 1 < argc) out = argv[++i];
                else dir = a;
            }
            if (!dir.empty()) return corpus(dir, out, update);
        }
        fprintf(stderr, "usage: ksa-metal-tests smoke | corpus [--update] [--out <dir>] <corpus>\n");
        return 2;
    }
}
