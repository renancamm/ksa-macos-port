// SPIR-V rewrites behind fused tessellation; see tessspirv.h. No Metal or Vulkan dependencies, so it can be tested
// on its own.
//
// Fused evaluation: the vertex shader becomes an exported function ksa_vs whose inputs and outputs are imported
// Private variables (ksa_vin_<location>, ksa_vout_<location>, ksa_vout_pos for gl_Position) and whose descriptors
// are imports of the evaluation shader's (ksa_res_<set>_<binding>, ksa_push), so the linked module declares each
// resource once. The evaluation shader exports those variables, gets one per-control-point input array per vertex
// shader input, turns its own per-control-point inputs (and gl_in[].gl_Position) into Private arrays, and fills them
// at the top of main by calling ksa_vs once per control point. SPIRV-Tools links the two.
#include "tessspirv.h"
#include "spvmodule.h"
#include "spirv-tools/linker.hpp"
#include "spirv-tools/optimizer.hpp"

namespace {

using namespace kspv;

struct Entry {
    uint32_t model = 0, fn = 0;
};

static Entry entryPoint(const Module& m) {
    Entry e;
    for (auto& i : m.insts)
        if (i.op() == spv::OpEntryPoint) { e.model = i.w[1]; e.fn = i.w[2]; break; }
    return e;
}

static uint32_t pointee(const Module& m, uint32_t var) {
    const Inst* v = m.def(var);
    const Inst* p = v ? m.def(v->w[1]) : nullptr;
    return p ? p->w[3] : 0;
}

// A struct whose member 0 is gl_Position (gl_PerVertex)
static bool isPerVertex(const Module& m, uint32_t type) {
    for (auto& i : m.insts)
        if (i.op() == spv::OpMemberDecorate && i.w[1] == type && i.w[2] == 0 && i.w[3] == spv::DecorationBuiltIn &&
            i.w[4] == spv::BuiltInPosition)
            return true;
    return false;
}

// An interface block (a Block struct, or an array of them): it cannot become Private
static bool isBlock(const Module& m, uint32_t type) {
    const Inst* t = m.def(type);
    if (t && (t->op() == spv::OpTypeArray || t->op() == spv::OpTypeRuntimeArray)) t = m.def(t->w[2]);
    if (!t || t->op() != spv::OpTypeStruct) return false;
    for (auto& i : m.insts)
        if (i.op() == spv::OpDecorate && i.w[1] == t->w[1] && i.w[2] == spv::DecorationBlock) return true;
    return false;
}

static bool constantValue(const Module& m, uint32_t id, uint32_t* value) {
    const Inst* c = m.def(id);
    if (!c || c->op() != spv::OpConstant) return false;
    *value = c->w[3];
    return true;
}

static bool decorated(const Module& m, uint32_t id, spv::Decoration d) {
    bool found;
    m.decoration(id, d, &found);
    if (found) return true;
    for (auto& i : m.insts) if (i.op() == spv::OpDecorate && i.w[1] == id && i.w[2] == (uint32_t)d) return true;
    return false;
}

// Interface decorations a variable loses when it stops being an input or output
static void removeInterfaceDecorations(Module& m, uint32_t var) {
    for (spv::Decoration d : {spv::DecorationLocation, spv::DecorationComponent, spv::DecorationFlat, spv::DecorationNoPerspective,
                              spv::DecorationCentroid, spv::DecorationSample, spv::DecorationInvariant, spv::DecorationIndex})
        m.removeDecoration(var, d);
}

// Removes a global's definition, names and decorations
static void eraseGlobal(Module& m, uint32_t id) {
    m.insts.erase(std::remove_if(m.insts.begin(), m.insts.end(), [&](const Inst& i) {
        if ((i.op() == spv::OpName || i.op() == spv::OpDecorate) && i.w[1] == id) return true;
        return i.op() == spv::OpVariable && i.w[2] == id;
    }), m.insts.end());
}

// Replaces a pointer wherever instructions take one: loads, stores, access chain bases, copies, call arguments
static void replacePointer(Module& m, uint32_t from, uint32_t to) {
    for (auto& i : m.insts) {
        switch (i.op()) {
        case spv::OpLoad: if (i.w[3] == from) i.w[3] = to; break;
        case spv::OpStore: if (i.w[1] == from) i.w[1] = to; break;
        case spv::OpAccessChain: case spv::OpInBoundsAccessChain: case spv::OpPtrAccessChain:
            if (i.w[3] == from) i.w[3] = to;
            break;
        case spv::OpCopyMemory: for (int k = 1; k <= 2; k++) if (i.w[k] == from) i.w[k] = to; break;
        case spv::OpFunctionCall: for (size_t k = 4; k < i.w.size(); k++) if (i.w[k] == from) i.w[k] = to; break;
        default: break;
        }
    }
}

// Ids rooted at a variable (it and the access chains into it)
static std::set<uint32_t> rooted(const Module& m, uint32_t var) {
    std::set<uint32_t> ids = {var};
    for (auto& i : m.insts)
        if ((i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain) && ids.count(i.w[3])) ids.insert(i.w[2]);
    return ids;
}

// Whether any instruction in a function body still uses the id (as any operand)
static bool usedInFunctions(const Module& m, uint32_t id) {
    for (size_t k = m.firstFunction(); k < m.insts.size(); k++) {
        const Inst& i = m.insts[k];
        for (size_t w = 1; w < i.w.size(); w++) if (i.w[w] == id) return true;
    }
    return false;
}

// The entry point's interface: every global variable from SPIR-V 1.4 on, only inputs and outputs before
static void setInterface(Module& m) {
    bool all = (m.header[1] >> 16 & 0xFF) > 1 || (m.header[1] >> 8 & 0xFF) >= 4;
    std::vector<uint32_t> globals;
    size_t end = m.firstFunction();
    for (size_t k = 0; k < end; k++) {
        const Inst& i = m.insts[k];
        if (i.op() != spv::OpVariable || i.w[3] == spv::StorageClassFunction) continue;
        if (all || i.w[3] == spv::StorageClassInput || i.w[3] == spv::StorageClassOutput) globals.push_back(i.w[2]);
    }
    for (auto& i : m.insts) {
        if (i.op() != spv::OpEntryPoint) continue;
        std::string name = readLiteral(i.w, 3);
        std::vector<uint32_t> ops = {i.w[1], i.w[2]};
        auto lit = literal(name);
        ops.insert(ops.end(), lit.begin(), lit.end());
        ops.insert(ops.end(), globals.begin(), globals.end());
        i = makeV(spv::OpEntryPoint, ops);
        break;
    }
}

// Dead code out, then interface variables nothing reads any more, then what that left dead
static bool cleanup(std::vector<uint32_t>& words, std::string* err) {
    spvtools::Optimizer opt(SPV_ENV_UNIVERSAL_1_6);
    opt.RegisterPass(spvtools::CreateAggressiveDCEPass());
    opt.RegisterPass(spvtools::CreateRemoveUnusedInterfaceVariablesPass());
    opt.RegisterPass(spvtools::CreateAggressiveDCEPass());
    spvtools::OptimizerOptions oo;
    oo.set_run_validator(false);
    std::vector<uint32_t> cleaned;
    if (!opt.Run(words.data(), words.size(), &cleaned, oo)) { *err = "dead-code elimination failed"; return false; }
    words.swap(cleaned);
    return true;
}

// Deletes an output variable with its stores and access chains, making what was computed only for it dead. False
// when the shader also reads it back (the variable then stays).
static bool dropOutput(Module& m, uint32_t var) {
    std::set<uint32_t> ids = rooted(m, var);
    for (auto& i : m.insts)
        if ((i.op() == spv::OpLoad && ids.count(i.w[3])) || (i.op() == spv::OpCopyMemory && ids.count(i.w[2]))) return false;
    m.insts.erase(std::remove_if(m.insts.begin(), m.insts.end(), [&](const Inst& i) {
        if (i.op() == spv::OpStore && ids.count(i.w[1])) return true;
        return (i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain) && ids.count(i.w[2]);
    }), m.insts.end());
    eraseGlobal(m, var);
    return true;
}

// Set, binding and name: KSA declares several bindless arrays over one binding (2D, array, cube and 3D textures)
static std::string resourceName(const Module& m, uint32_t var, bool* ok) {
    bool fs, fb;
    uint32_t s = m.decoration(var, spv::DecorationDescriptorSet, &fs), b = m.decoration(var, spv::DecorationBinding, &fb);
    std::string name;
    for (auto& i : m.insts)
        if (i.op() == spv::OpName && i.w[1] == var) { name = readLiteral(i.w, 2); break; }
    *ok = fs && fb;
    return "ksa_res_" + std::to_string(s) + "_" + std::to_string(b) + "_" + name;
}

// A type's full structure, decorations that shape its layout included, comparable across modules
static std::string deepSig(const Module& m, uint32_t id) {
    const Inst* t = m.def(id);
    if (!t) return "?";
    std::string s = std::to_string((int)t->op()) + "(";
    switch (t->op()) {
    case spv::OpTypeArray: {
        uint32_t len = 0;
        constantValue(m, t->w[3], &len);
        s += deepSig(m, t->w[2]) + "," + std::to_string(len);
        break;
    }
    case spv::OpTypeRuntimeArray: case spv::OpTypeSampledImage: case spv::OpTypeVector: case spv::OpTypeMatrix:
        s += deepSig(m, t->w[2]);
        for (size_t k = 3; k < t->w.size(); k++) s += "," + std::to_string(t->w[k]);
        break;
    case spv::OpTypeImage:
        s += deepSig(m, t->w[2]);
        for (size_t k = 3; k < t->w.size(); k++) s += "," + std::to_string(t->w[k]);
        break;
    case spv::OpTypeStruct:
        for (size_t k = 2; k < t->w.size(); k++) s += deepSig(m, t->w[k]) + ";";
        break;
    case spv::OpTypePointer:
        s += std::to_string(t->w[2]) + "," + deepSig(m, t->w[3]);
        break;
    default:
        for (size_t k = 2; k < t->w.size(); k++) s += std::to_string(t->w[k]) + ",";
        break;
    }
    s += ")";
    for (auto& i : m.insts) {
        if (i.op() == spv::OpDecorate && i.w[1] == id && i.w[2] != spv::DecorationLinkageAttributes) {
            s += "d";
            for (size_t k = 2; k < i.w.size(); k++) s += std::to_string(i.w[k]) + ".";
        }
        if (i.op() == spv::OpMemberDecorate && i.w[1] == id) {
            s += "m";
            for (size_t k = 2; k < i.w.size(); k++) s += std::to_string(i.w[k]) + ".";
        }
    }
    return s;
}

}  // namespace

bool kmtlFuseTessEvaluation(const std::vector<uint32_t>& vertex, const std::vector<uint32_t>& evaluation, uint32_t controlPoints,
                            std::vector<uint32_t>* out, std::vector<uint32_t>* rawLocations, std::string* err) {
    Module v, e;
    if (!v.parse(vertex)) { *err = "vertex shader: " + v.err; return false; }
    if (!e.parse(evaluation)) { *err = "evaluation shader: " + e.err; return false; }
    Entry ve = entryPoint(v), ee = entryPoint(e);
    if (ve.model != spv::ExecutionModelVertex || ee.model != spv::ExecutionModelTessellationEvaluation) {
        *err = "unexpected entry points";
        return false;
    }

    // The evaluation shader's descriptors, which the vertex shader's become imports of
    std::map<std::string, uint32_t> eResources;
    uint32_t ePush = 0;
    for (uint32_t sc : {spv::StorageClassUniform, spv::StorageClassUniformConstant, spv::StorageClassStorageBuffer})
        for (uint32_t var : e.globalVariables((spv::StorageClass)sc)) {
            bool ok;
            std::string n = resourceName(e, var, &ok);
            if (ok) eResources[n] = var;
        }
    for (uint32_t var : e.globalVariables(spv::StorageClassPushConstant)) ePush = var;

    // --- Vertex shader -> function ksa_vs
    v.insts.erase(std::remove_if(v.insts.begin(), v.insts.end(), [&](const Inst& i) {
        return i.op() == spv::OpEntryPoint || ((i.op() == spv::OpExecutionMode || i.op() == spv::OpExecutionModeId) && i.w[1] == ve.fn);
    }), v.insts.end());
    v.addCapability(spv::CapabilityLinkage);
    v.link(ve.fn, "ksa_vs", true);
    for (auto& i : v.insts)
        if (i.op() == spv::OpName && i.w[1] == ve.fn) i = makeV(spv::OpName, [&] { std::vector<uint32_t> o = {ve.fn}; auto l = literal("ksa_vs"); o.insert(o.end(), l.begin(), l.end()); return o; }());
    struct IO { uint32_t loc; std::string sig; };
    std::vector<IO> vin, vout;
    for (uint32_t var : v.globalVariables(spv::StorageClassInput)) {
        bool found, loc;
        v.decoration(var, spv::DecorationBuiltIn, &found);
        if (found) { *err = "the vertex shader reads built-in inputs"; return false; }
        uint32_t l = v.decoration(var, spv::DecorationLocation, &loc);
        std::string s = v.sig(pointee(v, var));
        if (!loc || s == "?") { *err = "vertex shader input without a location or of an unsupported type"; return false; }
        removeInterfaceDecorations(v, var);
        v.makePrivate(var);
        v.link(var, "ksa_vin_" + std::to_string(l), false);
        vin.push_back({l, s});
    }
    uint32_t perVertex = 0;
    for (uint32_t var : v.globalVariables(spv::StorageClassOutput)) {
        if (isPerVertex(v, pointee(v, var))) { perVertex = var; continue; }
        bool loc;
        uint32_t l = v.decoration(var, spv::DecorationLocation, &loc);
        std::string s = v.sig(pointee(v, var));
        if (!loc || s == "?" || isBlock(v, pointee(v, var))) {
            *err = "vertex shader output without a location or of an unsupported type";
            return false;
        }
        removeInterfaceDecorations(v, var);
        v.makePrivate(var);
        v.link(var, "ksa_vout_" + std::to_string(l), false);
        vout.push_back({l, s});
    }
    if (!perVertex) { *err = "the vertex shader writes no gl_Position"; return false; }
    {
        // gl_Position (member 0 of gl_PerVertex) -> ksa_vout_pos
        const Inst* st = v.def(pointee(v, perVertex));
        uint32_t vec4 = st ? st->w[2] : 0;
        uint32_t pos = v.newId();
        v.addGlobal(make(spv::OpVariable, {v.pointer(spv::StorageClassPrivate, vec4), pos, (uint32_t)spv::StorageClassPrivate}));
        v.link(pos, "ksa_vout_pos", false);
        // A chain to gl_Position itself is replaced by the variable; one into its components (gl_Position.z) now
        // starts at the variable
        // (By result id: creating pointer types inserts instructions, so no references into insts across it)
        std::vector<uint32_t> chains, componentChains;
        for (auto& i : v.insts) {
            if ((i.op() != spv::OpAccessChain && i.op() != spv::OpInBoundsAccessChain) || i.w[3] != perVertex) continue;
            uint32_t member;
            if (i.w.size() < 5 || !constantValue(v, i.w[4], &member) || member != 0) {
                *err = "the vertex shader writes gl_PerVertex members other than gl_Position";
                return false;
            }
            (i.w.size() == 5 ? chains : componentChains).push_back(i.w[2]);
        }
        for (uint32_t id : componentChains) {
            uint32_t ptr = v.pointer(spv::StorageClassPrivate, v.def(v.def(id)->w[1])->w[3]);
            Inst& i = v.insts[v.defIndex(id)];
            std::vector<uint32_t> ops = {ptr, i.w[2], pos};
            ops.insert(ops.end(), i.w.begin() + 5, i.w.end());
            i = makeV(spv::OpAccessChain, ops);
        }
        for (uint32_t c : chains) replacePointer(v, c, pos);
        v.insts.erase(std::remove_if(v.insts.begin(), v.insts.end(), [&](const Inst& i) {
            return (i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain) && i.w[3] == perVertex;
        }), v.insts.end());
        if (usedInFunctions(v, perVertex)) { *err = "the vertex shader uses gl_PerVertex as a whole"; return false; }
        eraseGlobal(v, perVertex);
    }
    // Descriptors the evaluation shader declares too: imports of its variables when both declare the same type.
    // Otherwise the one that is not used goes (glslang sizes a bindless array a shader never indexes to 1).
    std::set<std::string> shared;
    std::vector<uint32_t> dropFromE;
    for (uint32_t sc : {spv::StorageClassUniform, spv::StorageClassUniformConstant, spv::StorageClassStorageBuffer})
        for (uint32_t var : v.globalVariables((spv::StorageClass)sc)) {
            bool ok;
            std::string n = resourceName(v, var, &ok);
            auto it = eResources.find(n);
            if (!ok || it == eResources.end()) continue;
            if (deepSig(v, pointee(v, var)) != deepSig(e, pointee(e, it->second))) {
                if (!usedInFunctions(e, it->second)) dropFromE.push_back(it->second);
                else if (!usedInFunctions(v, var)) eraseGlobal(v, var);
                else { *err = "both shaders use " + n + " with different types"; return false; }
                continue;
            }
            v.link(var, n, false);  // the linker wants the import's decorations equal to the export's
            shared.insert(n);
        }
    for (uint32_t var : dropFromE) eraseGlobal(e, var);
    bool sharedPush = false;
    for (uint32_t var : v.globalVariables(spv::StorageClassPushConstant)) {
        if (!ePush) continue;
        if (deepSig(v, pointee(v, var)) != deepSig(e, pointee(e, ePush))) {
            *err = "the shaders declare different push constants";
            return false;
        }
        v.link(var, "ksa_push", false);
        sharedPush = true;
    }

    // --- Evaluation shader
    e.addCapability(spv::CapabilityLinkage);
    for (uint32_t sc : {spv::StorageClassUniform, spv::StorageClassUniformConstant, spv::StorageClassStorageBuffer})
        for (uint32_t var : e.globalVariables((spv::StorageClass)sc)) {
            bool ok;
            std::string n = resourceName(e, var, &ok);
            if (ok && shared.count(n)) e.link(var, n, true);
        }
    if (sharedPush) e.link(ePush, "ksa_push", true);

    // Its per-control-point inputs become Private arrays, filled below; the array length comes from them
    std::map<uint32_t, std::pair<uint32_t, uint32_t>> oldInputs;  // location -> variable, element type
    uint32_t lengthId = 0, glIn = 0;
    for (uint32_t var : e.globalVariables(spv::StorageClassInput)) {
        bool builtin;
        e.decoration(var, spv::DecorationBuiltIn, &builtin);
        uint32_t t = pointee(e, var);
        const Inst* arr = e.def(t);
        if (arr && arr->op() == spv::OpTypeArray && isPerVertex(e, arr->w[2])) {
            glIn = var;
            lengthId = arr->w[3];
            continue;
        }
        if (builtin) continue;
        if (decorated(e, var, spv::DecorationPatch)) { *err = "the evaluation shader reads per-patch inputs"; return false; }
        bool loc;
        uint32_t l = e.decoration(var, spv::DecorationLocation, &loc);
        if (!loc || !arr || arr->op() != spv::OpTypeArray || isBlock(e, t)) { *err = "unexpected evaluation shader input"; return false; }
        lengthId = arr->w[3];
        oldInputs[l] = {var, arr->w[2]};
    }
    if (!lengthId) lengthId = e.constant(e.u32(), 32);
    for (auto& oi : oldInputs) {
        removeInterfaceDecorations(e, oi.second.first);
        e.makePrivate(oi.second.first);
    }
    // gl_in[i].gl_Position -> a Private array of positions
    uint32_t positions = 0, vec4 = 0;
    if (glIn) {
        const Inst* arr = e.def(pointee(e, glIn));
        const Inst* st = e.def(arr->w[2]);
        vec4 = st->w[2];
        positions = e.newId();
        uint32_t posArray = e.type(spv::OpTypeArray, {vec4, lengthId});
        e.addGlobal(make(spv::OpVariable, {e.pointer(spv::StorageClassPrivate, posArray), positions, (uint32_t)spv::StorageClassPrivate}));
        uint32_t ptrVec4 = e.pointer(spv::StorageClassPrivate, vec4);
        std::vector<uint32_t> chains;
        for (auto& i : e.insts) {
            if ((i.op() != spv::OpAccessChain && i.op() != spv::OpInBoundsAccessChain) || i.w[3] != glIn) continue;
            uint32_t member;
            if (i.w.size() < 6 || !constantValue(e, i.w[5], &member) || member != 0) {
                *err = "the evaluation shader reads gl_in members other than gl_Position";
                return false;
            }
            chains.push_back(i.w[2]);
        }
        // gl_in[i].gl_Position(.component) -> positions[i](.component)
        for (uint32_t id : chains) {
            const Inst* old = e.def(id);
            uint32_t ptr = old->w.size() == 6 ? ptrVec4 : e.pointer(spv::StorageClassPrivate, e.def(old->w[1])->w[3]);
            Inst& i = e.insts[e.defIndex(id)];
            std::vector<uint32_t> ops = {ptr, i.w[2], positions, i.w[4]};
            ops.insert(ops.end(), i.w.begin() + 6, i.w.end());
            i = makeV(spv::OpAccessChain, ops);
        }
        if (usedInFunctions(e, glIn)) { *err = "the evaluation shader uses gl_in as a whole"; return false; }
        eraseGlobal(e, glIn);
    }
    // The variables the two share, and one input array per vertex shader input
    auto exported = [&](const std::string& name, uint32_t type) {
        uint32_t id = e.newId();
        e.addGlobal(make(spv::OpVariable, {e.pointer(spv::StorageClassPrivate, type), id, (uint32_t)spv::StorageClassPrivate}));
        e.link(id, name, true);
        return id;
    };
    struct In { uint32_t raw, vin, type; };
    std::vector<In> ins;
    for (auto& io : vin) {
        uint32_t t = e.fromSig(io.sig);
        uint32_t arr = e.type(spv::OpTypeArray, {t, lengthId});
        uint32_t raw = e.newId();
        e.addGlobal(make(spv::OpVariable, {e.pointer(spv::StorageClassInput, arr), raw, (uint32_t)spv::StorageClassInput}));
        e.addDecoration(make(spv::OpDecorate, {raw, (uint32_t)spv::DecorationLocation, io.loc}));
        ins.push_back({raw, exported("ksa_vin_" + std::to_string(io.loc), t), t});
        rawLocations->push_back(io.loc);
    }
    struct Out { uint32_t vout, target, type; };
    std::vector<Out> outs;
    for (auto& io : vout) {
        uint32_t t = e.fromSig(io.sig);
        uint32_t var = exported("ksa_vout_" + std::to_string(io.loc), t);
        auto it = oldInputs.find(io.loc);
        if (it == oldInputs.end()) continue;  // an output the evaluation stage does not read
        if (e.sig(it->second.second) != io.sig) { *err = "vertex output and evaluation input types differ"; return false; }
        outs.push_back({var, it->second.first, it->second.second});
    }
    uint32_t posOut = exported("ksa_vout_pos", vec4 ? vec4 : e.fromSig("v4f32"));
    // ksa_vs, declared for the linker
    uint32_t voidT = e.voidType(), fnType = e.type(spv::OpTypeFunction, {voidT});
    uint32_t vsFn = e.newId();
    {
        size_t at = e.firstFunction();
        e.insts.insert(e.insts.begin() + at, make(spv::OpFunction, {voidT, vsFn, (uint32_t)spv::FunctionControlMaskNone, fnType}));
        e.insts.insert(e.insts.begin() + at + 1, make(spv::OpFunctionEnd, {}));
        e.link(vsFn, "ksa_vs", false);
    }
    // Every type and constant first (they go among the globals), then the prologue
    uint32_t intT = e.type(spv::OpTypeInt, {32, 1});
    std::vector<uint32_t> cps;
    for (uint32_t cp = 0; cp < controlPoints; cp++) cps.push_back(e.constant(intT, cp));
    std::vector<uint32_t> inPtr, privPtr;
    for (auto& in : ins) inPtr.push_back(e.pointer(spv::StorageClassInput, in.type));
    for (auto& o : outs) privPtr.push_back(e.pointer(spv::StorageClassPrivate, o.type));
    uint32_t privVec4 = vec4 ? e.pointer(spv::StorageClassPrivate, vec4) : 0;
    std::vector<Inst> prologue;
    for (uint32_t cp = 0; cp < controlPoints; cp++) {
        for (size_t k = 0; k < ins.size(); k++) {
            uint32_t a = e.newId(), l = e.newId();
            prologue.push_back(make(spv::OpAccessChain, {inPtr[k], a, ins[k].raw, cps[cp]}));
            prologue.push_back(make(spv::OpLoad, {ins[k].type, l, a}));
            prologue.push_back(make(spv::OpStore, {ins[k].vin, l}));
        }
        prologue.push_back(make(spv::OpFunctionCall, {voidT, e.newId(), vsFn}));
        for (size_t k = 0; k < outs.size(); k++) {
            uint32_t l = e.newId(), a = e.newId();
            prologue.push_back(make(spv::OpLoad, {outs[k].type, l, outs[k].vout}));
            prologue.push_back(make(spv::OpAccessChain, {privPtr[k], a, outs[k].target, cps[cp]}));
            prologue.push_back(make(spv::OpStore, {a, l}));
        }
        if (positions) {
            uint32_t l = e.newId(), a = e.newId();
            prologue.push_back(make(spv::OpLoad, {vec4, l, posOut}));
            prologue.push_back(make(spv::OpAccessChain, {privVec4, a, positions, cps[cp]}));
            prologue.push_back(make(spv::OpStore, {a, l}));
        }
    }
    // At the top of main: after its first label and the block's variables
    size_t at = 0;
    for (size_t k = 0; k < e.insts.size(); k++)
        if (e.insts[k].op() == spv::OpFunction && e.insts[k].w[2] == ee.fn) { at = k; break; }
    if (!at) { *err = "evaluation shader main not found"; return false; }
    while (at < e.insts.size() && e.insts[at].op() != spv::OpLabel) at++;
    at++;
    while (at < e.insts.size() && e.insts[at].op() == spv::OpVariable) at++;
    e.insts.insert(e.insts.begin() + at, prologue.begin(), prologue.end());

    // --- Link, then drop the linkage
    spvtools::Context ctx(SPV_ENV_UNIVERSAL_1_6);
    std::string log;
    ctx.SetMessageConsumer([&](spv_message_level_t, const char*, const spv_position_t&, const char* msg) { log += msg; log += "\n"; });
    std::vector<uint32_t> linked;
    if (spvtools::Link(ctx, {e.words(), v.words()}, &linked, spvtools::LinkerOptions()) != SPV_SUCCESS) {
        *err = "linking failed: " + log;
        return false;
    }
    Module l;
    if (!l.parse(linked)) { *err = l.err; return false; }
    l.removeCapability(spv::CapabilityLinkage);
    l.insts.erase(std::remove_if(l.insts.begin(), l.insts.end(), [](const Inst& i) {
        return i.op() == spv::OpDecorate && i.w[2] == spv::DecorationLinkageAttributes; }), l.insts.end());
    setInterface(l);
    *out = l.words();
    return cleanup(*out, err);
}

bool kmtlReduceTessControl(const std::vector<uint32_t>& control, std::vector<uint32_t>* out, std::vector<uint32_t>* inputs,
                           bool* position, std::string* err) {
    Module m;
    if (!m.parse(control)) { *err = m.err; return false; }
    if (entryPoint(m).model != spv::ExecutionModelTessellationControl) { *err = "not a control shader"; return false; }
    for (uint32_t var : m.globalVariables(spv::StorageClassOutput)) {
        bool builtin;
        uint32_t b = m.decoration(var, spv::DecorationBuiltIn, &builtin);
        if (builtin && (b == spv::BuiltInTessLevelOuter || b == spv::BuiltInTessLevelInner)) continue;
        if (decorated(m, var, spv::DecorationPatch)) { *err = "the control shader writes per-patch outputs"; return false; }
        if (!dropOutput(m, var)) { *err = "the control shader reads its outputs"; return false; }
    }
    setInterface(m);
    *out = m.words();
    if (!cleanup(*out, err)) return false;
    Module r;
    if (!r.parse(*out)) { *err = r.err; return false; }
    *position = false;
    for (uint32_t var : r.globalVariables(spv::StorageClassInput)) {
        const Inst* arr = r.def(pointee(r, var));
        if (arr && arr->op() == spv::OpTypeArray && isPerVertex(r, arr->w[2])) { *position = true; continue; }
        bool loc;
        uint32_t l = r.decoration(var, spv::DecorationLocation, &loc);
        if (loc) inputs->push_back(l);
    }
    return true;
}

bool kmtlReduceVertexOutputs(const std::vector<uint32_t>& vertex, const std::vector<uint32_t>& keep, std::vector<uint32_t>* out,
                             std::string* err) {
    Module m;
    if (!m.parse(vertex)) { *err = m.err; return false; }
    if (entryPoint(m).model != spv::ExecutionModelVertex) { *err = "not a vertex shader"; return false; }
    for (uint32_t var : m.globalVariables(spv::StorageClassOutput)) {
        if (isPerVertex(m, pointee(m, var))) continue;
        bool loc;
        uint32_t l = m.decoration(var, spv::DecorationLocation, &loc);
        if (loc && std::find(keep.begin(), keep.end(), l) != keep.end()) continue;
        if (!dropOutput(m, var)) { *err = "the vertex shader reads its outputs"; return false; }
    }
    setInterface(m);
    *out = m.words();
    return cleanup(*out, err);
}
