// Name table behind vkGetInstanceProcAddr / vkGetDeviceProcAddr, made from Vulkan's registry (entries.inc,
// ../entries/Generate.cs). Unknown names return NULL so gaps fail loudly.
#include "kmtl.h"

struct KEntry {
    const char* name;
    PFN_vkVoidFunction fn;
    int level;  // 0 global, 1 instance, 2 device
};

#define G(n) {#n, (PFN_vkVoidFunction)n, 0}
#define I(n) {#n, (PFN_vkVoidFunction)n, 1}
#define D(n) {#n, (PFN_vkVoidFunction)n, 2}
#define IA(alias, n) {#alias, (PFN_vkVoidFunction)n, 1}
#define DA(alias, n) {#alias, (PFN_vkVoidFunction)n, 2}

VKFN(PFN_vkVoidFunction) vkGetInstanceProcAddr(VkInstance instance, const char* name);
VKFN(PFN_vkVoidFunction) vkGetDeviceProcAddr(VkDevice device, const char* name);

static const KEntry kEntries[] = {
#include "entries.inc"
};

static const KEntry* findEntry(const char* name) {
    static std::unordered_map<std::string, const KEntry*> map = [] {
        std::unordered_map<std::string, const KEntry*> m;
        for (auto& e : kEntries) m[e.name] = &e;
        return m;
    }();
    auto it = map.find(name);
    if (it == map.end()) {
        KLOG(2, "proc addr miss: %s", name);
        return nullptr;
    }
    return it->second;
}

static PFN_vkVoidFunction lookup(const char* name, int maxLevel) {
    const KEntry* e = findEntry(name);
    return e && e->level <= maxLevel ? e->fn : nullptr;
}

VKFN(PFN_vkVoidFunction) vkGetInstanceProcAddr(VkInstance instance, const char* name) {
    if (!name) return nullptr;
    // Without an instance only global functions resolve (plus vkGetInstanceProcAddr itself)
    return lookup(name, instance ? 2 : 0);
}

VKFN(PFN_vkVoidFunction) vkGetDeviceProcAddr(VkDevice, const char* name) {
    if (!name) return nullptr;
    // Device-level queries return only device-level functions
    const KEntry* e = findEntry(name);
    return e && e->level == 2 ? e->fn : nullptr;
}
