// SPIR-V rewrites behind passthrough and fused tessellation (tessspirv.cpp; design in command.mm, "Tessellated draws").
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

// The evaluation shader with the vertex shader linked in: for each of its control points it reads the vertex
// shader's inputs (the vertex attributes, fetched per control point), runs the vertex shader, and takes its outputs
// as that control point's inputs. *rawLocations = the vertex shader's input locations, now the evaluation stage's.
bool kmtlFuseTessEvaluation(const std::vector<uint32_t>& vertex, const std::vector<uint32_t>& evaluation,
                            uint32_t controlPoints, std::vector<uint32_t>* out, std::vector<uint32_t>* rawLocations,
                            std::string* err);

// A control shader without its per-control-point outputs (for a passthrough stage whose outputs the evaluation stage
// computes itself): only the inputs its tessellation levels read remain. *inputs = their locations; *position = it
// reads gl_in[].gl_Position.
bool kmtlReduceTessControl(const std::vector<uint32_t>& control, std::vector<uint32_t>* out, std::vector<uint32_t>* inputs,
                           bool* position, std::string* err);

// The vertex shader writing only the outputs at these locations, and gl_Position.
bool kmtlReduceVertexOutputs(const std::vector<uint32_t>& vertex, const std::vector<uint32_t>& keep,
                             std::vector<uint32_t>* out, std::string* err);
