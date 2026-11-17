// Acceleration structure builds, recorded into the command buffer in an encoder of their own (the acceleration
// structures themselves, their descriptors from a build's inputs and their device addresses: resources/accel.mm).
#include "kmtl.h"

VKFN(void) vkCmdBuildAccelerationStructuresKHR(VkCommandBuffer commandBuffer, uint32_t n,
                                               const VkAccelerationStructureBuildGeometryInfoKHR* infos,
                                               const VkAccelerationStructureBuildRangeInfoKHR* const* ranges) {
    KTRACE_FIRST();
    auto* cb = (KCommandBuffer*)commandBuffer;
    KDevice* dev = cb->device;
    cb->endEncoder();
    id<MTLAccelerationStructureCommandEncoder> enc = [cb->mtl accelerationStructureCommandEncoder];
    cb->waitForRoles(enc, KCommandBuffer::kClassAccel);
    for (uint32_t i = 0; i < n; i++) {
        auto* dst = (KAccel*)infos[i].dstAccelerationStructure;
        // An update (refit in place, src == dst) is done as a full build: KSA's per-frame TLAS has few instances
        if (infos[i].mode != VK_BUILD_ACCELERATION_STRUCTURE_MODE_BUILD_KHR) KLOG_LIMIT(1, 1, "acceleration structure update: rebuilt");
        id<MTLBuffer> instances = nil;
        MTLAccelerationStructureDescriptor* d = kmtlAccelDescriptor(&infos[i], nullptr, true, ranges[i], dev->mtl, &instances);
        if (!d || !dst) continue;
        MTLAccelerationStructureSizes s = [dev->mtl accelerationStructureSizesWithDescriptor:d];
        if (s.accelerationStructureSize > dst->mtl.size) KERR("acceleration structure too small (%lu > %lu)",
                                                              (unsigned long)s.accelerationStructureSize, (unsigned long)dst->mtl.size);
        id<MTLBuffer> scratch = [dev->mtl newBufferWithLength:std::max<NSUInteger>(s.buildScratchBufferSize, 256)
                                                      options:MTLResourceStorageModePrivate];
        if (!scratch) {
            KERR("vkCmdBuildAccelerationStructuresKHR: scratch buffer of %lu bytes failed", (unsigned long)s.buildScratchBufferSize);
            continue;
        }
        [enc buildAccelerationStructure:dst->mtl descriptor:d scratchBuffer:scratch scratchBufferOffset:0];
        if (instances) [enc useResource:instances usage:MTLResourceUsageRead];
    }
    [enc updateFence:cb->finishFence(cb->encRoles)];
    [enc endEncoding];
    cb->encRoles = 0;
}
