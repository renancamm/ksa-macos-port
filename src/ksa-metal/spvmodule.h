// A SPIR-V module as a list of instructions, with the few edits the driver's SPIR-V rewrites need (rtspirv.cpp,
// tessspirv.cpp), and the SPIRV-Tools steps both end with: linking and dead-code elimination. Header-only; no Metal
// or Vulkan dependencies.
#pragma once
#define SPV_ENABLE_UTILITY_CODE
#include "spirv.hpp"
#include "spirv-tools/linker.hpp"
#include "spirv-tools/optimizer.hpp"
#include <algorithm>
#include <cstring>
#include <map>
#include <set>
#include <string>
#include <vector>

namespace kspv {

using spv::Op;

struct Inst {
    std::vector<uint32_t> w;
    Op op() const { return (Op)(w[0] & 0xFFFF); }
};

inline Inst make(Op op, std::initializer_list<uint32_t> operands) {
    Inst i;
    i.w.push_back(0);
    i.w.insert(i.w.end(), operands.begin(), operands.end());
    i.w[0] = (uint32_t)op | ((uint32_t)i.w.size() << 16);
    return i;
}

inline Inst makeV(Op op, const std::vector<uint32_t>& operands) {
    Inst i;
    i.w.push_back(0);
    i.w.insert(i.w.end(), operands.begin(), operands.end());
    i.w[0] = (uint32_t)op | ((uint32_t)i.w.size() << 16);
    return i;
}

inline std::vector<uint32_t> literal(const std::string& s) {
    std::vector<uint32_t> out((s.size() + 4) / 4, 0);
    memcpy(out.data(), s.data(), s.size());
    return out;
}

inline std::string readLiteral(const std::vector<uint32_t>& w, size_t at) {
    std::string s;
    for (size_t i = at; i < w.size(); i++) {
        for (int b = 0; b < 4; b++) {
            char c = (char)((w[i] >> (8 * b)) & 0xFF);
            if (!c) return s;
            s += c;
        }
    }
    return s;
}

inline bool isTypeOp(Op op) {
    return (op >= spv::OpTypeVoid && op <= spv::OpTypeForwardPointer) || op == spv::OpTypeRayQueryKHR ||
           op == spv::OpTypeAccelerationStructureKHR;
}

inline bool isAnnotation(Op op) {
    return op == spv::OpDecorate || op == spv::OpMemberDecorate || op == spv::OpDecorationGroup || op == spv::OpGroupDecorate ||
           op == spv::OpGroupMemberDecorate || op == spv::OpDecorateId || op == spv::OpDecorateString ||
           op == spv::OpMemberDecorateString;
}

struct Module {
    std::vector<uint32_t> header;
    std::vector<Inst> insts;
    std::string err;

    bool parse(const std::vector<uint32_t>& words) {
        if (words.size() < 5 || words[0] != spv::MagicNumber) { err = "not SPIR-V"; return false; }
        header.assign(words.begin(), words.begin() + 5);
        for (size_t i = 5; i < words.size();) {
            uint32_t n = words[i] >> 16;
            if (!n || i + n > words.size()) { err = "malformed instruction"; return false; }
            Inst in;
            in.w.assign(words.begin() + i, words.begin() + i + n);
            insts.push_back(std::move(in));
            i += n;
        }
        return true;
    }
    std::vector<uint32_t> words() const {
        std::vector<uint32_t> out = header;
        for (auto& i : insts) out.insert(out.end(), i.w.begin(), i.w.end());
        return out;
    }
    uint32_t newId() { return header[3]++; }

    size_t firstFunction() const {
        for (size_t i = 0; i < insts.size(); i++) if (insts[i].op() == spv::OpFunction) return i;
        return insts.size();
    }
    size_t annotationEnd() const {
        size_t last = SIZE_MAX, firstType = SIZE_MAX;
        for (size_t i = 0; i < insts.size(); i++) {
            if (isAnnotation(insts[i].op())) last = i;
            if (firstType == SIZE_MAX && isTypeOp(insts[i].op())) firstType = i;
        }
        return last != SIZE_MAX ? last + 1 : firstType;
    }
    void addCapability(spv::Capability c) {
        for (auto& i : insts) if (i.op() == spv::OpCapability && i.w[1] == (uint32_t)c) return;
        insts.insert(insts.begin(), make(spv::OpCapability, {(uint32_t)c}));
    }
    void removeCapability(spv::Capability c) {
        insts.erase(std::remove_if(insts.begin(), insts.end(), [&](const Inst& i) {
            return i.op() == spv::OpCapability && i.w[1] == (uint32_t)c; }), insts.end());
    }
    void removeExtension(const std::string& name) {
        insts.erase(std::remove_if(insts.begin(), insts.end(), [&](const Inst& i) {
            return i.op() == spv::OpExtension && readLiteral(i.w, 1) == name; }), insts.end());
    }
    void addExtension(const std::string& name) {
        for (auto& i : insts) if (i.op() == spv::OpExtension && readLiteral(i.w, 1) == name) return;
        size_t at = 0;
        while (at < insts.size() && insts[at].op() == spv::OpCapability) at++;
        insts.insert(insts.begin() + at, makeV(spv::OpExtension, literal(name)));
    }
    void addDecoration(const Inst& d) { insts.insert(insts.begin() + annotationEnd(), d); }
    void addGlobal(const Inst& g) { insts.insert(insts.begin() + firstFunction(), g); }
    void link(uint32_t id, const std::string& name, bool exported) {
        std::vector<uint32_t> ops = {id, (uint32_t)spv::DecorationLinkageAttributes};
        auto lit = literal(name);
        ops.insert(ops.end(), lit.begin(), lit.end());
        ops.push_back(exported ? spv::LinkageTypeExport : spv::LinkageTypeImport);
        addDecoration(makeV(spv::OpDecorate, ops));
    }
    const Inst* def(uint32_t id) const {
        for (auto& i : insts) {
            bool hasResult, hasType;
            spv::HasResultAndType(i.op(), &hasResult, &hasType);
            if (hasResult && i.w.size() > (hasType ? 2u : 1u) && i.w[hasType ? 2 : 1] == id) return &i;
        }
        return nullptr;
    }
    size_t defIndex(uint32_t id) const {
        for (size_t k = 0; k < insts.size(); k++) {
            bool hasResult, hasType;
            spv::HasResultAndType(insts[k].op(), &hasResult, &hasType);
            if (hasResult && insts[k].w.size() > (hasType ? 2u : 1u) && insts[k].w[hasType ? 2 : 1] == id) return k;
        }
        return SIZE_MAX;
    }
    // Inserts a declaration right after the definitions of the ids it uses (types must precede their uses)
    void insertAfterDefs(const Inst& in, const std::vector<uint32_t>& ids) {
        size_t at = annotationEnd();
        for (uint32_t id : ids) {
            size_t k = defIndex(id);
            if (k != SIZE_MAX && k + 1 > at) at = k + 1;
        }
        insts.insert(insts.begin() + at, in);
    }
    // Type with these operands (after the result id), created if missing
    uint32_t type(Op op, const std::vector<uint32_t>& ops) {
        for (auto& i : insts) {
            if (i.op() != op || i.w.size() != ops.size() + 2) continue;
            if (std::equal(ops.begin(), ops.end(), i.w.begin() + 2)) return i.w[1];
        }
        uint32_t id = newId();
        std::vector<uint32_t> all = {id};
        all.insert(all.end(), ops.begin(), ops.end());
        std::vector<uint32_t> deps;
        if (op == spv::OpTypePointer) deps = {ops[1]};
        else if (op == spv::OpTypeVector || op == spv::OpTypeMatrix) deps = {ops[0]};
        else if (op == spv::OpTypeFunction || op == spv::OpTypeStruct || op == spv::OpTypeArray) deps = ops;
        else if (op == spv::OpTypeRuntimeArray) deps = {ops[0]};
        insertAfterDefs(makeV(op, all), deps);
        return id;
    }
    uint32_t constant(uint32_t typeId, uint32_t value) {
        for (auto& i : insts)
            if (i.op() == spv::OpConstant && i.w.size() == 4 && i.w[1] == typeId && i.w[3] == value) return i.w[2];
        uint32_t id = newId();
        insertAfterDefs(make(spv::OpConstant, {typeId, id, value}), {typeId});
        return id;
    }
    uint32_t u32() { return type(spv::OpTypeInt, {32, 0}); }
    uint32_t boolType() { return type(spv::OpTypeBool, {}); }
    uint32_t voidType() { return type(spv::OpTypeVoid, {}); }
    uint32_t pointer(spv::StorageClass sc, uint32_t pointee) { return type(spv::OpTypePointer, {(uint32_t)sc, pointee}); }
    uint32_t decoration(uint32_t id, spv::Decoration d, bool* found) const {
        for (auto& i : insts)
            if (i.op() == spv::OpDecorate && i.w[1] == id && i.w[2] == (uint32_t)d && i.w.size() > 3) { *found = true; return i.w[3]; }
        *found = false;
        return 0;
    }
    // Whether id has the decoration, with or without operands (decoration() only finds those with one, unlike Patch)
    bool decorated(uint32_t id, spv::Decoration d) const {
        for (auto& i : insts)
            if (i.op() == spv::OpDecorate && i.w[1] == id && i.w[2] == (uint32_t)d) return true;
        return false;
    }
    void removeDecoration(uint32_t id, spv::Decoration d) {
        insts.erase(std::remove_if(insts.begin(), insts.end(), [&](const Inst& i) {
            return i.op() == spv::OpDecorate && i.w[1] == id && i.w[2] == (uint32_t)d; }), insts.end());
    }
    // Structural type signature (scalars, vectors, matrices) and its inverse, to recreate a type in another module
    std::string sig(uint32_t typeId) const {
        const Inst* t = def(typeId);
        if (!t) return "?";
        switch (t->op()) {
        case spv::OpTypeBool: return "b";
        case spv::OpTypeInt: return "i" + std::to_string(t->w[2]) + (t->w[3] ? "s" : "u");
        case spv::OpTypeFloat: return "f" + std::to_string(t->w[2]);
        case spv::OpTypeVector: return "v" + std::to_string(t->w[3]) + sig(t->w[2]);
        case spv::OpTypeMatrix: return "m" + std::to_string(t->w[3]) + sig(t->w[2]);
        default: return "?";
        }
    }
    uint32_t fromSig(const std::string& s) {
        switch (s[0]) {
        case 'b': return boolType();
        case 'i': return type(spv::OpTypeInt, {(uint32_t)atoi(s.c_str() + 1), s.back() == 's' ? 1u : 0u});
        case 'f': return type(spv::OpTypeFloat, {(uint32_t)atoi(s.c_str() + 1)});
        case 'v': return type(spv::OpTypeVector, {fromSig(s.substr(2)), (uint32_t)(s[1] - '0')});
        case 'm': return type(spv::OpTypeMatrix, {fromSig(s.substr(2)), (uint32_t)(s[1] - '0')});
        default: return 0;
        }
    }
    // Moves a global variable to Private storage: new pointer type, and every access chain rooted at it follows
    void makePrivate(uint32_t var) {
        // Collect first: creating pointer types inserts instructions
        std::set<uint32_t> moved = {var};
        std::map<uint32_t, uint32_t> pointeeOf;  // result id -> pointee type
        for (auto& i : insts) {
            bool isVar = i.op() == spv::OpVariable && i.w[2] == var;
            bool isChain = (i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain) && moved.count(i.w[3]);
            if (!isVar && !isChain) continue;
            const Inst* pt = def(i.w[1]);
            pointeeOf[i.w[2]] = pt ? pt->w[3] : 0;
            moved.insert(i.w[2]);
        }
        std::map<uint32_t, uint32_t> newType;
        for (auto& e : pointeeOf) newType[e.first] = pointer(spv::StorageClassPrivate, e.second);
        for (auto& i : insts) {
            bool isVar = i.op() == spv::OpVariable, isChain = i.op() == spv::OpAccessChain || i.op() == spv::OpInBoundsAccessChain;
            if ((!isVar && !isChain) || !newType.count(i.w[2])) continue;
            i.w[1] = newType[i.w[2]];
            if (isVar) i.w[3] = spv::StorageClassPrivate;
        }
    }
    std::vector<uint32_t> globalVariables(spv::StorageClass sc) const {
        std::vector<uint32_t> out;
        size_t end = firstFunction();
        for (size_t k = 0; k < end; k++)
            if (insts[k].op() == spv::OpVariable && insts[k].w[3] == (uint32_t)sc) out.push_back(insts[k].w[2]);
        return out;
    }
};

// The entry point's interface: every global variable from SPIR-V 1.4 on, only inputs and outputs before
inline void setInterface(Module& m) {
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

// Links modules into one and drops the linkage (its capability and LinkageAttributes decorations); false with the
// linker's messages in *err
inline bool linkModules(const std::vector<std::vector<uint32_t>>& inputs, Module* out, std::string* err) {
    spvtools::Context ctx(SPV_ENV_UNIVERSAL_1_6);
    std::string log;
    ctx.SetMessageConsumer([&](spv_message_level_t, const char*, const spv_position_t&, const char* msg) { log += msg; log += "\n"; });
    std::vector<uint32_t> linked;
    if (spvtools::Link(ctx, inputs, &linked, spvtools::LinkerOptions()) != SPV_SUCCESS) { *err = "linking failed: " + log; return false; }
    if (!out->parse(linked)) { *err = out->err; return false; }
    out->removeCapability(spv::CapabilityLinkage);
    out->insts.erase(std::remove_if(out->insts.begin(), out->insts.end(), [](const Inst& i) {
        return i.op() == spv::OpDecorate && i.w[2] == spv::DecorationLinkageAttributes; }), out->insts.end());
    return true;
}

// Aggressive dead-code elimination; with interfaceVariables, then the interface variables nothing reads any more, and
// what that left dead
inline bool deadCode(std::vector<uint32_t>& words, bool interfaceVariables, std::string* err) {
    spvtools::Optimizer opt(SPV_ENV_UNIVERSAL_1_6);
    opt.RegisterPass(spvtools::CreateAggressiveDCEPass());
    if (interfaceVariables) {
        opt.RegisterPass(spvtools::CreateRemoveUnusedInterfaceVariablesPass());
        opt.RegisterPass(spvtools::CreateAggressiveDCEPass());
    }
    spvtools::OptimizerOptions oo;
    oo.set_run_validator(false);
    std::vector<uint32_t> cleaned;
    if (!opt.Run(words.data(), words.size(), &cleaned, oo)) { *err = "dead-code elimination failed"; return false; }
    words.swap(cleaned);
    return true;
}

}  // namespace kspv
