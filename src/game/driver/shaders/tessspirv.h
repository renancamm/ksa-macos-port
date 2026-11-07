// SPIR-V rewrites behind passthrough tessellation (tessspirv.cpp; design in commands/draws.mm, "Tessellated draws").
#pragma once
#include <cstdint>
#include <string>
#include <vector>

// A control shader that only copies its control points: every per-control-point output (the located arrays and
// gl_Position) is stored once, from the same input of the same type at gl_InvocationID, and the inputs are exactly
// those outputs. *out is the shader without the copies; its outputs stay declared, so the translated kernel keeps
// its output record and the driver can check that record against the vertex kernel's before it points the
// evaluation stage at the vertex kernel's buffer. False, with the reason in *why, when the shader computes
// something per control point (not an error: the stage then runs as written).
bool kmtlStripPassthroughControl(const std::vector<uint32_t>& control, std::vector<uint32_t>* out, std::string* why);

