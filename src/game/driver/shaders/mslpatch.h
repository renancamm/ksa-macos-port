// Text rewrites of translated MSL, and what they read in it (mslpatch.cpp): pure functions of the text, which the
// pipeline files and the driver's tests include. Each gives the text back unchanged where what it looks for isn't
// there.
#pragma once
#include <string>
#include <vector>

// The stage with its uniform blocks' structs read in place: copies of a constant block's struct become references to
// it, and the helpers that take one take a constant reference (KSA_MTL_IN_PLACE_SHADER_PARAMETERS)
std::string kmtlMslUniformRefs(const std::string& msl);
// Whether a fragment shader's every store is to the selection mask, so it gets a state with early fragment tests
bool kmtlMslStoresOnlySelection(const std::string& msl);
// The fragment stage with early fragment tests ("" without a fragment function)
std::string kmtlMslWithEarlyTests(std::string msl);
// A shader override's MSL with the specialization values of the translation it replaces
std::string kmtlMslKeepSpecialization(std::string msl, const std::string& translated);
// A ray tracing kernel with its traces as intersector calls (KSA_MTL_IVA_RAY_SEARCH), or unchanged when a trace doesn't
// have the expected shape
std::string kmtlMslRtIntersectors(const std::string& msl);
// The field types and names of a struct in translated MSL (none when it has no such struct)
std::vector<std::string> kmtlMslStructFields(const std::string& msl, const std::string& name, std::vector<std::string>* names);
// The translation's own edits (translate.mm): multiview's layer as the view index; arrays of acceleration structures
// passed as device pointers
std::string kmtlMslViewLayer(std::string msl);
std::string kmtlMslAccelArrays(const std::string& msl);
