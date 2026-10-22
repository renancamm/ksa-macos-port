// SPIR-V rewrite behind ray tracing pipelines: ray generation + closest-hit + miss shaders -> one compute module
// with ray queries. See rtpipeline.mm for the design; this file has no Metal or Vulkan dependencies so it can be
// tested on its own.
#include "rtspirv.h"
#include "spvmodule.h"
#include "spirv-tools/linker.hpp"
#include "spirv-tools/optimizer.hpp"
#include <algorithm>
#include <cstring>
#include <map>
#include <set>
#include <string>

namespace {

using namespace kspv;


static bool isRayTracingBuiltin(uint32_t b) {
    switch (b) {
    case spv::BuiltInLaunchIdKHR: case spv::BuiltInLaunchSizeKHR: case spv::BuiltInWorldRayOriginKHR:
    case spv::BuiltInWorldRayDirectionKHR: case spv::BuiltInObjectRayOriginKHR: case spv::BuiltInObjectRayDirectionKHR:
    case spv::BuiltInRayTminKHR: case spv::BuiltInRayTmaxKHR: case spv::BuiltInInstanceCustomIndexKHR:
    case spv::BuiltInObjectToWorldKHR: case spv::BuiltInWorldToObjectKHR: case spv::BuiltInHitTNV: case spv::BuiltInHitKindKHR:
    case spv::BuiltInIncomingRayFlagsKHR: case spv::BuiltInRayGeometryIndexKHR: case spv::BuiltInPrimitiveId:
    case spv::BuiltInInstanceId: case spv::BuiltInCullMaskKHR:
        return true;
    default:
        return false;
    }
}

struct Callee {
    std::string fn;            // exported function name
    int payloadLocation = -1;  // 1 when it has an incoming payload (linked by name to the traces' payload variable)
    std::map<uint32_t, std::string> builtins;  // built-in -> type signature
    bool hitAttributes = false;
    std::vector<uint32_t> words;
};

// Closest-hit or miss shader -> library module exporting `fn`
static bool transformCallee(const std::vector<uint32_t>& spirv, const std::string& fn, const std::string& payloadName, Callee* out, std::string* err) {
    Module m;
    if (!m.parse(spirv)) { *err = m.err; return false; }
    uint32_t entry = 0;
    for (auto& i : m.insts) if (i.op() == spv::OpEntryPoint) { entry = i.w[2]; break; }
    if (!entry) { *err = "no entry point"; return false; }
    m.insts.erase(std::remove_if(m.insts.begin(), m.insts.end(), [&](const Inst& i) {
        return i.op() == spv::OpEntryPoint || ((i.op() == spv::OpExecutionMode || i.op() == spv::OpExecutionModeId) && i.w[1] == entry);
    }), m.insts.end());
    m.removeCapability(spv::CapabilityRayTracingKHR);
    m.removeExtension("SPV_KHR_ray_tracing");
    m.addCapability(spv::CapabilityLinkage);
    m.link(entry, fn, true);
    out->fn = fn;
    for (uint32_t v : m.globalVariables(spv::StorageClassIncomingRayPayloadKHR)) {
        if (payloadName.empty()) { *err = "shader has a payload but no trace passes one"; return false; }
        m.removeDecoration(v, spv::DecorationLocation);
        m.makePrivate(v);
        m.link(v, payloadName, false);
        out->payloadLocation = 1;
    }
    for (uint32_t v : m.globalVariables(spv::StorageClassHitAttributeKHR)) {
        m.makePrivate(v);
        m.link(v, "ksa_hitattr", false);
        out->hitAttributes = true;
    }
    for (uint32_t v : m.globalVariables(spv::StorageClassInput)) {
        bool found;
        uint32_t b = m.decoration(v, spv::DecorationBuiltIn, &found);
        if (!found || !isRayTracingBuiltin(b)) continue;
        const Inst* var = m.def(v);
        const Inst* ptr = var ? m.def(var->w[1]) : nullptr;
        if (!ptr) { *err = "built-in without a pointer type"; return false; }
        out->builtins[b] = m.sig(ptr->w[3]);
        m.removeDecoration(v, spv::DecorationBuiltIn);
        m.makePrivate(v);
        m.link(v, "ksa_bi_" + std::to_string(b), false);
    }
    for (auto& i : m.insts) {
        if (i.op() == spv::OpTraceRayKHR || i.op() == spv::OpExecuteCallableKHR) {
            *err = "closest-hit or miss shader traces rays or calls callables (unsupported)";
            return false;
        }
        if (i.op() == spv::OpIgnoreIntersectionKHR || i.op() == spv::OpTerminateRayKHR) {
            *err = "any-hit instructions in a closest-hit or miss shader";
            return false;
        }
    }
    out->words = m.words();
    return true;
}

// Ray query getter for a built-in, or 0 if it has no ray query equivalent; `intersection` = takes the committed
// intersection operand
static Op builtinGetter(uint32_t b, bool* intersection) {
    *intersection = true;
    switch (b) {
    case spv::BuiltInInstanceCustomIndexKHR: return spv::OpRayQueryGetIntersectionInstanceCustomIndexKHR;
    case spv::BuiltInInstanceId: return spv::OpRayQueryGetIntersectionInstanceIdKHR;
    case spv::BuiltInPrimitiveId: return spv::OpRayQueryGetIntersectionPrimitiveIndexKHR;
    case spv::BuiltInRayTmaxKHR: case spv::BuiltInHitTNV: return spv::OpRayQueryGetIntersectionTKHR;
    case spv::BuiltInObjectRayOriginKHR: return spv::OpRayQueryGetIntersectionObjectRayOriginKHR;
    case spv::BuiltInObjectRayDirectionKHR: return spv::OpRayQueryGetIntersectionObjectRayDirectionKHR;
    case spv::BuiltInObjectToWorldKHR: return spv::OpRayQueryGetIntersectionObjectToWorldKHR;
    case spv::BuiltInWorldToObjectKHR: return spv::OpRayQueryGetIntersectionWorldToObjectKHR;
    case spv::BuiltInRayGeometryIndexKHR: return spv::OpRayQueryGetIntersectionGeometryIndexKHR;
    default: break;
    }
    *intersection = false;
    switch (b) {
    case spv::BuiltInRayTminKHR: return spv::OpRayQueryGetRayTMinKHR;
    case spv::BuiltInWorldRayOriginKHR: return spv::OpRayQueryGetWorldRayOriginKHR;
    case spv::BuiltInWorldRayDirectionKHR: return spv::OpRayQueryGetWorldRayDirectionKHR;
    case spv::BuiltInIncomingRayFlagsKHR: return spv::OpRayQueryGetRayFlagsKHR;
    default: return spv::OpNop;
    }
}

struct RaygenPlan {
    const Callee* chit = nullptr;
    std::vector<const Callee*> misses;  // by miss index
    uint32_t chitPayload = 0;           // payload variable of the traces that run the closest-hit shader
    std::vector<uint32_t> missPayload;  // payload variable of the traces that use each miss index
};

// Which payload variable the traces pass to the closest-hit shader and to each miss index (each must be one)
static bool scanTraces(const std::vector<uint32_t>& spirv, size_t missCount, RaygenPlan* plan, std::string* err) {
    Module m;
    if (!m.parse(spirv)) { *err = m.err; return false; }
    plan->missPayload.assign(missCount, 0);
    for (auto& in : m.insts) {
        if (in.op() != spv::OpTraceRayKHR) continue;
        const Inst* flags = m.def(in.w[2]);
        const Inst* miss = m.def(in.w[6]);
        if (!miss || miss->op() != spv::OpConstant || miss->w[3] >= missCount) { *err = "trace with a non-constant or unknown miss index"; return false; }
        uint32_t payload = in.w[11];
        uint32_t& mp = plan->missPayload[miss->w[3]];
        if (mp && mp != payload) { *err = "miss shader used with two payloads"; return false; }
        mp = payload;
        bool skipHit = flags && flags->op() == spv::OpConstant && (flags->w[3] & spv::RayFlagsSkipClosestHitShaderKHRMask);
        if (!skipHit) {
            if (plan->chitPayload && plan->chitPayload != payload) { *err = "closest-hit shader used with two payloads"; return false; }
            plan->chitPayload = payload;
        }
    }
    return true;
}

static bool transformRaygen(const std::vector<uint32_t>& spirv, const RaygenPlan& plan, std::vector<uint32_t>* out, std::string* err) {
    Module m;
    if (!m.parse(spirv)) { *err = m.err; return false; }
    Inst* ep = nullptr;
    for (auto& i : m.insts) if (i.op() == spv::OpEntryPoint) { ep = &i; break; }
    if (!ep || ep->w[1] != spv::ExecutionModelRayGenerationKHR) { *err = "not a ray generation shader"; return false; }
    uint32_t mainFn = ep->w[2];
    ep->w[1] = spv::ExecutionModelGLCompute;
    size_t epIndex = ep - m.insts.data();
    m.insts.insert(m.insts.begin() + epIndex + 1, make(spv::OpExecutionMode, {mainFn, (uint32_t)spv::ExecutionModeLocalSize, 8, 8, 1}));
    m.removeCapability(spv::CapabilityRayTracingKHR);
    m.addCapability(spv::CapabilityRayQueryKHR);
    m.addCapability(spv::CapabilityLinkage);
    m.removeExtension("SPV_KHR_ray_tracing");
    m.addExtension("SPV_KHR_ray_query");

    uint32_t u32 = m.u32(), v3u = m.type(spv::OpTypeVector, {u32, 3}), boolT = m.boolType(), voidT = m.voidType();
    uint32_t c0 = m.constant(u32, 0), c1 = m.constant(u32, 1);

    // Launch id / size
    uint32_t gidVar = 0, sizeVar = 0;
    for (uint32_t v : m.globalVariables(spv::StorageClassInput)) {
        bool found;
        uint32_t b = m.decoration(v, spv::DecorationBuiltIn, &found);
        if (!found) continue;
        if (b == spv::BuiltInLaunchIdKHR) {
            for (auto& i : m.insts)
                if (i.op() == spv::OpDecorate && i.w[1] == v && i.w[2] == spv::DecorationBuiltIn) i.w[3] = spv::BuiltInGlobalInvocationId;
            gidVar = v;
        } else if (b == spv::BuiltInLaunchSizeKHR) {
            m.removeDecoration(v, spv::DecorationBuiltIn);
            m.makePrivate(v);
            sizeVar = v;
        } else if (isRayTracingBuiltin(b)) {
            *err = "ray generation shader uses built-in " + std::to_string(b);
            return false;
        }
    }
    if (!gidVar) {
        gidVar = m.newId();
        m.addGlobal(make(spv::OpVariable, {m.pointer(spv::StorageClassInput, v3u), gidVar, (uint32_t)spv::StorageClassInput}));
        m.addDecoration(make(spv::OpDecorate, {gidVar, (uint32_t)spv::DecorationBuiltIn, (uint32_t)spv::BuiltInGlobalInvocationId}));
    }
    // Launch size uniform: set 15, binding 0, struct { uvec3 size; }
    uint32_t lsStruct = m.newId();
    m.insertAfterDefs(make(spv::OpTypeStruct, {lsStruct, v3u}), {v3u});
    m.addDecoration(make(spv::OpDecorate, {lsStruct, (uint32_t)spv::DecorationBlock}));
    m.addDecoration(make(spv::OpMemberDecorate, {lsStruct, 0, (uint32_t)spv::DecorationOffset, 0}));
    uint32_t lsVar = m.newId();
    m.addGlobal(make(spv::OpVariable, {m.pointer(spv::StorageClassUniform, lsStruct), lsVar, (uint32_t)spv::StorageClassUniform}));
    m.addDecoration(make(spv::OpDecorate, {lsVar, (uint32_t)spv::DecorationDescriptorSet, 15}));
    m.addDecoration(make(spv::OpDecorate, {lsVar, (uint32_t)spv::DecorationBinding, 0}));

    // Payloads
    for (uint32_t v : m.globalVariables(spv::StorageClassRayPayloadKHR)) {
        m.removeDecoration(v, spv::DecorationLocation);
        m.makePrivate(v);
        m.link(v, "ksa_payload_" + std::to_string(v), true);
    }
    // Built-ins and hit attributes the callees read
    std::map<uint32_t, std::pair<uint32_t, uint32_t>> builtinVars;  // built-in -> (variable, type)
    bool hitAttributes = false;
    std::vector<const Callee*> callees = plan.misses;
    if (plan.chit) callees.push_back(plan.chit);
    for (const Callee* c : callees) {
        if (!c) continue;
        hitAttributes |= c->hitAttributes;
        for (auto& b : c->builtins) {
            if (builtinVars.count(b.first)) continue;
            bool inter;
            if (builtinGetter(b.first, &inter) == spv::OpNop && b.first != spv::BuiltInHitKindKHR) {
                *err = "built-in " + std::to_string(b.first) + " has no ray query equivalent";
                return false;
            }
            uint32_t t = m.fromSig(b.second), v = m.newId();
            m.addGlobal(make(spv::OpVariable, {m.pointer(spv::StorageClassPrivate, t), v, (uint32_t)spv::StorageClassPrivate}));
            m.link(v, "ksa_bi_" + std::to_string(b.first), true);
            builtinVars[b.first] = {v, t};
        }
    }
    uint32_t f32 = m.type(spv::OpTypeFloat, {32}), v2f = m.type(spv::OpTypeVector, {f32, 2});
    uint32_t hitAttrVar = 0;
    if (hitAttributes) {
        hitAttrVar = m.newId();
        m.addGlobal(make(spv::OpVariable, {m.pointer(spv::StorageClassPrivate, v2f), hitAttrVar, (uint32_t)spv::StorageClassPrivate}));
        m.link(hitAttrVar, "ksa_hitattr", true);
    }
    uint32_t rqType = m.type(spv::OpTypeRayQueryKHR, {});
    uint32_t rq = m.newId();
    m.addGlobal(make(spv::OpVariable, {m.pointer(spv::StorageClassPrivate, rqType), rq, (uint32_t)spv::StorageClassPrivate}));
    // Imported functions
    uint32_t fnType = m.type(spv::OpTypeFunction, {voidT});
    std::map<std::string, uint32_t> imported;
    for (const Callee* c : callees) {
        if (!c || imported.count(c->fn)) continue;
        uint32_t id = m.newId();
        size_t at = m.firstFunction();
        m.insts.insert(m.insts.begin() + at, make(spv::OpFunction, {voidT, id, (uint32_t)spv::FunctionControlMaskNone, fnType}));
        m.insts.insert(m.insts.begin() + at + 1, make(spv::OpFunctionEnd, {}));
        m.link(id, c->fn, false);
        imported[c->fn] = id;
    }

    auto constValue = [&](uint32_t id, bool* isConst) -> uint32_t {
        const Inst* d = m.def(id);
        *isConst = d && d->op() == spv::OpConstant;
        return *isConst ? d->w[3] : 0;
    };

    // Rewrite the functions: trace sites, and the launch-bounds prologue of main
    std::vector<Inst> out_;
    std::map<uint32_t, uint32_t> lastSegment;  // block label -> label of its last split segment
    // Types and constants created below go into m.insts before its functions; the functions are rewritten from a copy
    size_t fnStart = m.firstFunction();
    const std::vector<Inst> funcs(m.insts.begin() + fnStart, m.insts.end());
    uint32_t curFn = 0, curLabel = 0;
    bool needPrologue = false;
    for (size_t k = 0; k < funcs.size(); k++) {
        const Inst& in = funcs[k];
        if (in.op() == spv::OpFunction) { curFn = in.w[2]; needPrologue = curFn == mainFn; }
        if (in.op() == spv::OpLabel) curLabel = in.w[1];
        bool isVarLike = in.op() == spv::OpVariable || in.op() == spv::OpLine || in.op() == spv::OpNoLine;
        if (needPrologue && curLabel && !isVarLike && in.op() != spv::OpLabel) {
            needPrologue = false;
            // if (gid.x >= size.x || gid.y >= size.y) return;
            uint32_t ptr = m.newId(), size = m.newId(), gid = m.newId(), gx = m.newId(), gy = m.newId(), sx = m.newId(), sy = m.newId();
            uint32_t cx = m.newId(), cy = m.newId(), oob = m.newId(), lret = m.newId(), lbody = m.newId();
            uint32_t pU = m.pointer(spv::StorageClassUniform, v3u);
            out_.push_back(make(spv::OpAccessChain, {pU, ptr, lsVar, m.constant(m.type(spv::OpTypeInt, {32, 1}), 0)}));
            out_.push_back(make(spv::OpLoad, {v3u, size, ptr}));
            if (sizeVar) out_.push_back(make(spv::OpStore, {sizeVar, size}));
            out_.push_back(make(spv::OpLoad, {v3u, gid, gidVar}));
            out_.push_back(make(spv::OpCompositeExtract, {u32, gx, gid, 0}));
            out_.push_back(make(spv::OpCompositeExtract, {u32, gy, gid, 1}));
            out_.push_back(make(spv::OpCompositeExtract, {u32, sx, size, 0}));
            out_.push_back(make(spv::OpCompositeExtract, {u32, sy, size, 1}));
            out_.push_back(make(spv::OpUGreaterThanEqual, {boolT, cx, gx, sx}));
            out_.push_back(make(spv::OpUGreaterThanEqual, {boolT, cy, gy, sy}));
            out_.push_back(make(spv::OpLogicalOr, {boolT, oob, cx, cy}));
            out_.push_back(make(spv::OpSelectionMerge, {lbody, (uint32_t)spv::SelectionControlMaskNone}));
            out_.push_back(make(spv::OpBranchConditional, {oob, lret, lbody}));
            out_.push_back(make(spv::OpLabel, {lret}));
            out_.push_back(make(spv::OpReturn, {}));
            out_.push_back(make(spv::OpLabel, {lbody}));
            lastSegment[curLabel] = lbody;
            curLabel = lbody;
        }
        if (in.op() != spv::OpTraceRayKHR) {
            out_.push_back(in);
            continue;
        }
        // The rest of this block must not hold a loop merge (the header would move)
        for (size_t j = k + 1; j < funcs.size() && funcs[j].op() != spv::OpLabel; j++)
            if (funcs[j].op() == spv::OpLoopMerge) { *err = "trace inside a loop header block"; return false; }
        const auto& w = in.w;
        uint32_t as = w[1], flags = w[2], cull = w[3], missIdx = w[6], origin = w[7], tmin = w[8], dir = w[9], tmax = w[10], payload = w[11];
        bool flagsConst, missConst;
        uint32_t flagsValue = constValue(flags, &flagsConst), missIndex = constValue(missIdx, &missConst);
        if (!missConst || missIndex >= plan.misses.size() || !plan.misses[missIndex]) {
            *err = "trace with a non-constant or unknown miss index";
            return false;
        }
        bool skipHit = flagsConst && (flagsValue & spv::RayFlagsSkipClosestHitShaderKHRMask);
        bool callHit = !skipHit && plan.chit;
        if (callHit && plan.chitPayload != payload) { *err = "closest-hit called with different payloads"; return false; }
        if (plan.misses[missIndex]->payloadLocation >= 0 && plan.missPayload[missIndex] != payload) {
            *err = "miss shader called with different payloads";
            return false;
        }
        out_.push_back(make(spv::OpRayQueryInitializeKHR, {rq, as, flags, cull, origin, tmin, dir, tmax}));
        uint32_t proceed = m.newId(), type = m.newId(), hit = m.newId(), lh = m.newId(), lmiss = m.newId(), lm = m.newId();
        out_.push_back(make(spv::OpRayQueryProceedKHR, {boolT, proceed, rq}));
        out_.push_back(make(spv::OpRayQueryGetIntersectionTypeKHR, {u32, type, rq, c1}));
        out_.push_back(make(spv::OpINotEqual, {boolT, hit, type, c0}));
        out_.push_back(make(spv::OpSelectionMerge, {lm, (uint32_t)spv::SelectionControlMaskNone}));
        out_.push_back(make(spv::OpBranchConditional, {hit, lh, lmiss}));
        auto setBuiltins = [&](bool intersectionValid) {
            for (auto& b : builtinVars) {
                bool inter;
                Op g = builtinGetter(b.first, &inter);
                if (inter && !intersectionValid) continue;
                uint32_t v = m.newId();
                if (b.first == spv::BuiltInHitKindKHR) {
                    if (!intersectionValid) continue;
                    uint32_t ff = m.newId();
                    out_.push_back(make(spv::OpRayQueryGetIntersectionFrontFaceKHR, {boolT, ff, rq, c1}));
                    out_.push_back(make(spv::OpSelect, {b.second.second, v, ff, m.constant(b.second.second, 0xFE), m.constant(b.second.second, 0xFF)}));
                } else if (inter) {
                    out_.push_back(make(g, {b.second.second, v, rq, c1}));
                } else {
                    out_.push_back(make(g, {b.second.second, v, rq}));
                }
                out_.push_back(make(spv::OpStore, {b.second.first, v}));
            }
        };
        out_.push_back(make(spv::OpLabel, {lh}));
        if (callHit) {
            setBuiltins(true);
            if (hitAttrVar) {
                uint32_t bc = m.newId();
                out_.push_back(make(spv::OpRayQueryGetIntersectionBarycentricsKHR, {v2f, bc, rq, c1}));
                out_.push_back(make(spv::OpStore, {hitAttrVar, bc}));
            }
            out_.push_back(make(spv::OpFunctionCall, {voidT, m.newId(), imported[plan.chit->fn]}));
        }
        out_.push_back(make(spv::OpBranch, {lm}));
        out_.push_back(make(spv::OpLabel, {lmiss}));
        setBuiltins(false);
        out_.push_back(make(spv::OpFunctionCall, {voidT, m.newId(), imported[plan.misses[missIndex]->fn]}));
        out_.push_back(make(spv::OpBranch, {lm}));
        out_.push_back(make(spv::OpLabel, {lm}));
        // The original block's remaining instructions (and terminator) now live in lm
        uint32_t orig = curLabel;
        for (auto& e : lastSegment) if (e.second == curLabel) orig = e.first;
        lastSegment[orig] = lm;
        if (orig != curLabel) lastSegment[curLabel] = lm;
        curLabel = lm;
    }
    // Phis name the block their value comes from: point them at the last segment of a split block
    for (auto& in : out_) {
        if (in.op() != spv::OpPhi) continue;
        for (size_t p = 4; p < in.w.size(); p += 2) {
            auto it = lastSegment.find(in.w[p]);
            if (it != lastSegment.end()) in.w[p] = it->second;
        }
    }
    m.insts.resize(m.firstFunction());
    m.insts.insert(m.insts.end(), out_.begin(), out_.end());
    *out = m.words();
    return true;
}

// After linking: drop linkage, and list every global variable in the entry point's interface (SPIR-V 1.4+)
static bool finishLinked(std::vector<uint32_t>& words, std::string* err) {
    Module m;
    if (!m.parse(words)) { *err = m.err; return false; }
    m.removeCapability(spv::CapabilityLinkage);
    m.insts.erase(std::remove_if(m.insts.begin(), m.insts.end(), [](const Inst& i) {
        return i.op() == spv::OpDecorate && i.w[2] == spv::DecorationLinkageAttributes; }), m.insts.end());
    std::vector<uint32_t> globals;
    size_t end = m.firstFunction();
    for (size_t k = 0; k < end; k++)
        if (m.insts[k].op() == spv::OpVariable && m.insts[k].w[3] != spv::StorageClassFunction) globals.push_back(m.insts[k].w[2]);
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
    words = m.words();
    // Dead-code elimination drops what the rewrite left unused, notably pointer types in ray tracing storage
    // classes (they would need the ray tracing capability)
    spvtools::Optimizer opt(SPV_ENV_UNIVERSAL_1_6);
    opt.RegisterPass(spvtools::CreateAggressiveDCEPass());
    spvtools::OptimizerOptions oo;
    oo.set_run_validator(false);
    std::vector<uint32_t> cleaned;
    if (!opt.Run(words.data(), words.size(), &cleaned, oo)) { *err = "dead-code elimination failed"; return false; }
    words.swap(cleaned);
    return true;
}

}  // namespace

bool kmtlLinkRayTracing(const std::vector<uint32_t>& raygen, const std::vector<uint32_t>* closestHit,
                        const std::vector<std::vector<uint32_t>>& misses, std::vector<uint32_t>* out, std::string* err) {
    Callee hit;
    std::vector<Callee> missCallees(misses.size());
    RaygenPlan plan;
    if (!scanTraces(raygen, misses.size(), &plan, err)) { *err = "ray generation shader: " + *err; return false; }
    auto payloadName = [](uint32_t v) { return v ? "ksa_payload_" + std::to_string(v) : std::string(); };
    if (closestHit && plan.chitPayload) {
        if (!transformCallee(*closestHit, "ksa_chit", payloadName(plan.chitPayload), &hit, err)) { *err = "closest-hit shader: " + *err; return false; }
        plan.chit = &hit;
    }
    for (size_t i = 0; i < misses.size(); i++) {
        if (!transformCallee(misses[i], "ksa_miss_" + std::to_string(i), payloadName(plan.missPayload[i]), &missCallees[i], err)) {
            *err = "miss shader " + std::to_string(i) + ": " + *err;
            return false;
        }
        plan.misses.push_back(&missCallees[i]);
    }
    std::vector<uint32_t> rg;
    if (!transformRaygen(raygen, plan, &rg, err)) { *err = "ray generation shader: " + *err; return false; }
    std::vector<std::vector<uint32_t>> inputs = {rg};
    if (plan.chit) inputs.push_back(hit.words);
    for (auto& mc : missCallees) inputs.push_back(mc.words);
    if (const char* dir = getenv("KSA_MTL_RT_DUMP")) {
        for (size_t i = 0; i < inputs.size(); i++) {
            std::string p = std::string(dir) + "/rt_in_" + std::to_string(i) + ".spv";
            if (FILE* f = fopen(p.c_str(), "wb")) { fwrite(inputs[i].data(), 4, inputs[i].size(), f); fclose(f); }
        }
    }
    spvtools::Context ctx(SPV_ENV_UNIVERSAL_1_6);
    std::string log;
    ctx.SetMessageConsumer([&](spv_message_level_t, const char*, const spv_position_t&, const char* msg) { log += msg; log += "\n"; });
    spvtools::LinkerOptions lo;
    if (spvtools::Link(ctx, inputs, out, lo) != SPV_SUCCESS) { *err = "linking failed: " + log; return false; }
    return finishLinked(*out, err);
}
