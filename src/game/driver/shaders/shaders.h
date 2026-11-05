// KSA Metal's shaders (shaders/): what a translated stage is, and the shader cache. The translation itself is
// translate.h's, which only its callers include. Included by kmtl.h.
#pragma once

// Result of translating one pipeline stage (SPIR-V -> MSL plus reflection); cached on disk by shadercache.mm
struct KTranslated {
    std::string msl;
    std::string entry;
    bool usesDrawId = false;
    MTLSize workgroup = MTLSizeMake(1, 1, 1);
    // Tessellation execution modes (from whichever of the two stages declares them)
    uint32_t outputVertices = 0;
    bool quads = false, cw = false;
    MTLTessellationPartitionMode partition = MTLTessellationPartitionModeInteger;
    bool hasPartition = false, hasWinding = false, hasDomain = false;
};

// Shader cache (shadercache.mm): translations on disk keyed by everything that shapes them, and a background
// compile of the previous run's shaders at device creation
bool kmtlCacheLoad(uint64_t key, KTranslated* out);
void kmtlCacheStore(uint64_t key, const KTranslated& tr);
// A translation as the cache stores it (header, entry point, flags, MSL), and read back
std::string kmtlTranslatedText(const KTranslated& tr);
bool kmtlTranslatedParse(const std::string& text, KTranslated* out);
void kmtlCacheNoteUsed(uint64_t key);
void kmtlCacheFlushRecent();
void kmtlShaderPrewarm(KDevice* dev);
