// Ray tracing pipeline -> compute module rewrite (rtspirv.cpp)
#pragma once
#include <cstdint>
#include <string>
#include <vector>

// Links a ray generation shader with its closest-hit shader (optional) and miss shaders (by miss index) into one
// GLCompute module (8x8 workgroups) that traces with ray queries. Launch size: uniform at set 15, binding 0.
bool kmtlLinkRayTracing(const std::vector<uint32_t>& raygen, const std::vector<uint32_t>* closestHit,
                        const std::vector<std::vector<uint32_t>>& misses, std::vector<uint32_t>* out, std::string* err);
