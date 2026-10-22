// SPIR-V rewrites behind fused tessellation (tessspirv.cpp; design in command.mm, "Tessellated draws").
#pragma once
#include <cstdint>
#include <string>
#include <vector>

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
