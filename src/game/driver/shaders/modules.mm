// Shader modules: the game's SPIR-V kept with its hash, which names the module's translations, dumps (KSA_MTL_DUMP_SPV)
// and overrides.
#include "kmtl.h"

VKFN(VkResult) vkCreateShaderModule(VkDevice, const VkShaderModuleCreateInfo* ci, const VkAllocationCallbacks*, VkShaderModule* out) {
    KTRACE_FIRST();
    auto* m = new KShaderModule();
    m->spirv.assign(ci->pCode, ci->pCode + ci->codeSize / 4);
    m->hash = kmtlHash(ci->pCode, ci->codeSize);
    if (const char* dir = kmtlConfig().dumpSpv) {
        char name[32];
        snprintf(name, sizeof(name), "%016llx.spv", (unsigned long long)m->hash);
        kmtlDumpFile(dir, name, ci->pCode, ci->codeSize, true);
    }
    *out = (VkShaderModule)m;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyShaderModule(VkDevice, VkShaderModule m, const VkAllocationCallbacks*) {
    KTRACE_FIRST();
    delete (KShaderModule*)m;
}
