// The shader corpus (KSA_MTL_SHADER_CORPUS=<dir>): every stage translation's inputs and its result, recorded while
// the game runs, so that the driver's tests (tests/) translate and compile the same stages again without the game.
//
//   <dir>/<module hash>.spv        a shader module, as the game created it
//   <dir>/<translation key>.in     the rest of the inputs: entry point, stage, options, set layouts, specialization,
//                                  and the settings that shape a translation
//   <dir>/<translation key>.out    the result, as the shader cache stores it
//
// The corpus holds the game's shaders: it stays on the Mac that recorded it.
#include "translate.h"
#include <dirent.h>
#include <sys/stat.h>

static const char* kFormat = "KSAMTL-CORPUS 1";

static std::string hex64(uint64_t v) {
    char s[17];
    snprintf(s, sizeof(s), "%016llx", (unsigned long long)v);
    return s;
}

// Writes <path> through a temporary file, so that a reader never sees half of it
static void writeAtomically(const std::string& path, const std::string& data) {
    std::string tmp = path + ".tmp" + std::to_string((unsigned long)pthread_mach_thread_np(pthread_self()));
    FILE* f = fopen(tmp.c_str(), "wb");
    if (!f) return;
    fwrite(data.data(), 1, data.size(), f);
    if (fclose(f) == 0) rename(tmp.c_str(), path.c_str());
    else unlink(tmp.c_str());
}

void kmtlCorpusRecord(uint64_t key, KShaderModule* mod, const char* entry, spv::ExecutionModel model, KPipelineLayout* layout,
                      const VkSpecializationInfo* spec, const KStageOptions& so, const KTranslated& out) {
    std::string dir = kmtlConfig().shaderCorpus;
    std::string in = dir + "/" + hex64(key) + ".in";
    struct stat st;
    if (stat(in.c_str(), &st) == 0) return;
    for (size_t at = dir.find('/', 1); at != std::string::npos; at = dir.find('/', at + 1)) mkdir(dir.substr(0, at).c_str(), 0755);
    mkdir(dir.c_str(), 0755);
    std::string spv = dir + "/" + hex64(mod->hash) + ".spv";
    if (stat(spv.c_str(), &st) != 0)
        writeAtomically(spv, std::string((const char*)mod->spirv.data(), mod->spirv.size() * 4));

    std::string t = std::string(kFormat) + "\n";
    char line[256];
    t += "module " + hex64(mod->hash) + "\n";
    t += std::string("entry ") + entry + "\n";
    snprintf(line, sizeof(line), "model %d\noptions %u %d %u %d %d %u %d %d %d\n", (int)model, so.fragMask, (int)so.sampleRate,
             so.viewMask, (int)so.tess, (int)so.indexType, so.tessControlPoints, (int)so.tessQuads, (int)so.rtLaunch,
             (int)so.tessFusedInput);
    t += line;
    const char* presets = kmtlConfig().spec;
    snprintf(line, sizeof(line), "config %d %d ", (int)kmtlConfig().shaderConstantMemory, (int)kmtlConfig().skipShadowsOnUnlitSides);
    t += line + std::string(presets ? presets : "-") + "\n";
    if (!kmtlConfig().sharedShadowSamples) t += "shared-shadow-samples 0\n";  // a line of its own: older records lack it
    if (layout) {
        t += "sets " + std::to_string(layout->sets.size()) + "\n";
        for (KSetLayout* sl : layout->sets) {
            if (!sl) {
                t += "set -\n";
                continue;
            }
            snprintf(line, sizeof(line), "set %u %u %zu\n", sl->slotCount, sl->sizeSlot, sl->bindings.size());
            t += line;
            for (const KBinding& b : sl->bindings) {
                snprintf(line, sizeof(line), "binding %u %d %u %u %u\n", b.binding, (int)b.type, b.count, b.id, b.samplerId);
                t += line;
            }
        }
    }
    if (spec) {
        snprintf(line, sizeof(line), "spec %u %zu\n", spec->mapEntryCount, spec->dataSize);
        t += line;
        for (uint32_t i = 0; i < spec->mapEntryCount; i++) {
            const VkSpecializationMapEntry& me = spec->pMapEntries[i];
            snprintf(line, sizeof(line), "constant %u %u %zu\n", me.constantID, me.offset, me.size);
            t += line;
        }
        t += "data ";
        for (size_t i = 0; i < spec->dataSize; i++) {
            snprintf(line, sizeof(line), "%02x", ((const uint8_t*)spec->pData)[i]);
            t += line;
        }
        t += "\n";
    }
    writeAtomically(dir + "/" + hex64(key) + ".out", kmtlTranslatedText(out));
    writeAtomically(in, t);  // last: an entry with its .in is complete
}

static bool readText(const std::string& path, std::string* out) {
    FILE* f = fopen(path.c_str(), "rb");
    if (!f) return false;
    out->clear();
    char buf[65536];
    for (size_t n; (n = fread(buf, 1, sizeof(buf), f)) > 0;) out->append(buf, n);
    fclose(f);
    return true;
}

std::vector<uint64_t> kmtlCorpusKeys(const std::string& dir) {
    std::vector<uint64_t> keys;
    DIR* d = opendir(dir.c_str());
    if (!d) return keys;
    while (dirent* e = readdir(d)) {
        std::string name = e->d_name;
        if (name.size() == 19 && name.compare(16, 3, ".in") == 0)
            keys.push_back(strtoull(name.substr(0, 16).c_str(), nullptr, 16));
    }
    closedir(d);
    std::sort(keys.begin(), keys.end());
    return keys;
}

bool kmtlCorpusLoad(const std::string& dir, uint64_t key, KCorpusEntry* out, std::string* err) {
    std::string text, data;
    std::string base = dir + "/" + hex64(key);
    if (!readText(base + ".in", &text) || text.compare(0, strlen(kFormat), kFormat) != 0) {
        *err = "no inputs, or of another format";
        return false;
    }
    if (!readText(base + ".out", &data) || !kmtlTranslatedParse(data, &out->recorded)) {
        *err = "no result";
        return false;
    }
    out->key = key;
    KSetLayout* set = nullptr;
    size_t bindings = 0;  // the bindings the last set line announced
    size_t pos = text.find('\n') + 1;
    while (pos < text.size()) {
        size_t nl = text.find('\n', pos);
        if (nl == std::string::npos) nl = text.size();
        std::string line = text.substr(pos, nl - pos);
        pos = nl + 1;
        size_t sp = line.find(' ');
        std::string what = line.substr(0, sp), rest = sp == std::string::npos ? "" : line.substr(sp + 1);
        const char* r = rest.c_str();
        bool ok = true;
        if (what == "module") {
            out->module.hash = strtoull(r, nullptr, 16);
            std::string spv;
            ok = readText(dir + "/" + rest + ".spv", &spv) && spv.size() % 4 == 0;
            out->module.spirv.resize(spv.size() / 4);
            if (ok) memcpy(out->module.spirv.data(), spv.data(), spv.size());
        } else if (what == "entry") {
            out->entry = rest;
        } else if (what == "model") {
            out->model = (spv::ExecutionModel)atoi(r);
        } else if (what == "options") {
            KStageOptions& so = out->options;
            int sampleRate = 0, tess = 0, indexType = 0, quads = 0, rtLaunch = 0, fused = 0;
            ok = sscanf(r, "%u %d %u %d %d %u %d %d %d", &so.fragMask, &sampleRate, &so.viewMask, &tess, &indexType,
                        &so.tessControlPoints, &quads, &rtLaunch, &fused) == 9;
            so.sampleRate = sampleRate;
            so.tess = (KTessRole)tess;
            so.indexType = (KIndexType)indexType;
            so.tessQuads = quads;
            so.rtLaunch = rtLaunch;
            so.tessFusedInput = fused;
        } else if (what == "config") {
            int constantAB = 1, unlitSkip = 1, n = 0;
            ok = sscanf(r, "%d %d %n", &constantAB, &unlitSkip, &n) == 2 && n > 0;
            out->constantArgumentBuffers = constantAB;
            out->unlitShadowSkip = unlitSkip;
            out->specPresets = ok && rest.substr(n) != "-" ? rest.substr(n) : "";
        } else if (what == "shared-shadow-samples") {
            out->sharedShadowSamples = atoi(r) != 0;
        } else if (what == "sets") {
            out->hasLayout = true;
        } else if (what == "set") {
            set = nullptr;
            if (rest != "-") {
                set = new KSetLayout();
                ok = sscanf(r, "%u %u %zu", &set->slotCount, &set->sizeSlot, &bindings) == 3;
            }
            out->layout.sets.push_back(set);
        } else if (what == "binding") {
            KBinding b;
            int type = 0;
            ok = set && sscanf(r, "%u %d %u %u %u", &b.binding, &type, &b.count, &b.id, &b.samplerId) == 5;
            b.type = (VkDescriptorType)type;
            if (ok) set->bindings.push_back(b);
        } else if (what == "spec") {
            out->hasSpec = true;
        } else if (what == "constant") {
            VkSpecializationMapEntry me{};
            ok = sscanf(r, "%u %u %zu", &me.constantID, &me.offset, &me.size) == 3;
            out->specEntries.push_back(me);
        } else if (what == "data") {
            for (size_t i = 0; ok && i + 1 < rest.size(); i += 2) {
                unsigned v;
                ok = sscanf(r + i, "%2x", &v) == 1;
                out->specData.push_back((uint8_t)v);
            }
        }
        if (!ok || (set && set->bindings.size() > bindings)) {
            *err = "unreadable line: " + line;
            return false;
        }
    }
    for (KSetLayout* sl : out->layout.sets)
        if (sl && sl == set && sl->bindings.size() != bindings) {
            *err = "a set's bindings are missing";
            return false;
        }
    if (out->module.spirv.empty() || out->entry.empty()) {
        *err = "no module or entry point";
        return false;
    }
    out->spec.mapEntryCount = (uint32_t)out->specEntries.size();
    out->spec.pMapEntries = out->specEntries.data();
    out->spec.dataSize = out->specData.size();
    out->spec.pData = out->specData.data();
    return true;
}
