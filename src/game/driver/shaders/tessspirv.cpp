// SPIR-V rewrites behind passthrough tessellation; see tessspirv.h. No Metal or Vulkan dependencies, so it can be
// tested on its own.
#include "tessspirv.h"
#include "spvmodule.h"

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

static bool constantValue(const Module& m, uint32_t id, uint32_t* value) {
    const Inst* c = m.def(id);
    if (!c || c->op() != spv::OpConstant) return false;
    *value = c->w[3];
    return true;
}

// Ids rooted at a variable (it and the access chains into it)
static std::set<uint32_t> rooted(const Module& m, uint32_t var) {
    std::set<uint32_t> ids = {var};
    for (auto& i : m.insts)
        if ((i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain) && ids.count(i.w[3])) ids.insert(i.w[2]);
    return ids;
}

// A type's full structure, decorations that shape its layout included
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

bool kmtlStripPassthroughControl(const std::vector<uint32_t>& control, std::vector<uint32_t>* out, std::string* why) {
    Module m;
    if (!m.parse(control)) { *why = m.err; return false; }
    if (entryPoint(m).model != spv::ExecutionModelTessellationControl) { *why = "not a control shader"; return false; }
    // The inputs: gl_in, the located per-vertex arrays, and gl_InvocationID
    uint32_t invocation = 0, glIn = 0;
    std::map<uint32_t, uint32_t> inputs;  // location -> variable
    for (uint32_t var : m.globalVariables(spv::StorageClassInput)) {
        bool builtin, loc;
        uint32_t b = m.decoration(var, spv::DecorationBuiltIn, &builtin);
        if (builtin && b == spv::BuiltInInvocationId) { invocation = var; continue; }
        const Inst* arr = m.def(pointee(m, var));
        if (arr && arr->op() == spv::OpTypeArray && isPerVertex(m, arr->w[2])) { glIn = var; continue; }
        uint32_t l = m.decoration(var, spv::DecorationLocation, &loc);
        if (loc) inputs[l] = var;
    }
    if (!invocation) { *why = "no gl_InvocationID"; return false; }
    auto isInvocation = [&](uint32_t id) {
        const Inst* d = m.def(id);
        return d && d->op() == spv::OpLoad && d->w[3] == invocation;
    };
    auto element = [&](uint32_t var) {  // element type of a per-vertex array variable
        const Inst* arr = m.def(pointee(m, var));
        return arr && arr->op() == spv::OpTypeArray ? arr->w[2] : 0u;
    };
    // A chain out[idx] (member < 0) or out[idx].member, with idx = gl_InvocationID
    auto isCopyChain = [&](const Inst* c, uint32_t base, int member) {
        if (!c || (c->op() != spv::OpAccessChain && c->op() != spv::OpInBoundsAccessChain) || c->w[3] != base) return false;
        if (c->w.size() != (member < 0 ? 5u : 6u) || !isInvocation(c->w[4])) return false;
        uint32_t idx;
        return member < 0 || (constantValue(m, c->w[5], &idx) && idx == (uint32_t)member);
    };

    std::set<size_t> copies;  // the stores to remove
    std::set<uint32_t> passed;
    bool position = false;
    for (uint32_t var : m.globalVariables(spv::StorageClassOutput)) {
        bool builtin, loc;
        uint32_t b = m.decoration(var, spv::DecorationBuiltIn, &builtin);
        if (builtin && (b == spv::BuiltInTessLevelOuter || b == spv::BuiltInTessLevelInner)) continue;
        if (m.decorated(var, spv::DecorationPatch)) { *why = "it writes per-patch outputs"; return false; }
        const Inst* arr = m.def(pointee(m, var));
        bool block = arr && arr->op() == spv::OpTypeArray && isPerVertex(m, arr->w[2]);
        uint32_t l = m.decoration(var, spv::DecorationLocation, &loc);
        if (!block && (!loc || !arr || arr->op() != spv::OpTypeArray)) { *why = "an output is neither gl_out nor a located array"; return false; }
        uint32_t in = block ? glIn : inputs.count(l) ? inputs[l] : 0;
        if (!in) { *why = "an output has no input at its location"; return false; }
        if (block) {
            // gl_Position only, of the same type
            const Inst* os = m.def(arr->w[2]);
            const Inst* is = m.def(m.def(pointee(m, in))->w[2]);
            if (!os || !is || os->w.size() < 3 || is->w.size() < 3 || deepSig(m, os->w[2]) != deepSig(m, is->w[2])) {
                *why = "gl_in and gl_out differ";
                return false;
            }
        } else if (deepSig(m, element(var)) != deepSig(m, element(in))) {
            *why = "the input and output at location " + std::to_string(l) + " differ in type";
            return false;
        }
        std::set<uint32_t> ids = rooted(m, var);
        size_t stores = 0;
        for (size_t k = 0; k < m.insts.size(); k++) {
            const Inst& i = m.insts[k];
            if ((i.op() == spv::OpLoad && ids.count(i.w[3])) || (i.op() == spv::OpCopyMemory && ids.count(i.w[2]))) {
                *why = "it reads its outputs";
                return false;
            }
            if (i.op() == spv::OpCopyMemory && ids.count(i.w[1])) { *why = "it copies into an output as a whole"; return false; }
            if (i.op() != spv::OpStore || !ids.count(i.w[1])) continue;
            int member = block ? 0 : -1;
            const Inst* val = m.def(i.w[2]);
            const Inst* src = val && val->op() == spv::OpLoad ? m.def(val->w[3]) : nullptr;
            if (!isCopyChain(m.def(i.w[1]), var, member) || !isCopyChain(src, in, member)) {
                *why = block ? "gl_out is not a copy of gl_in at gl_InvocationID"
                             : "location " + std::to_string(l) + " is not a copy of its input at gl_InvocationID";
                return false;
            }
            copies.insert(k);
            stores++;
        }
        if (stores != 1) { *why = "an output is stored " + std::to_string(stores) + " times"; return false; }
        if (block) position = true;
        else passed.insert(l);
    }
    if (passed.empty()) { *why = "no per-control-point outputs"; return false; }
    // The in and out records hold the same members
    std::set<uint32_t> inLocations;
    for (auto& e : inputs) inLocations.insert(e.first);
    if (inLocations != passed || (glIn != 0) != position) { *why = "the inputs are not the outputs"; return false; }
    // Only the stores go: the chains and loads they fed are dead, and the outputs stay in the interface
    std::vector<Inst> kept;
    kept.reserve(m.insts.size());
    for (size_t k = 0; k < m.insts.size(); k++)
        if (!copies.count(k)) kept.push_back(std::move(m.insts[k]));
    m.insts.swap(kept);
    *out = m.words();
    return true;
}
