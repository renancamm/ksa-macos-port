// Acceleration structures for ray queries (KSA: grass placement in GroundClutter/Generate.comp) and the cockpit's ray
// tracing pipelines (rtpipeline.mm).
//
// KSA builds a few bottom-level structures from meshes (float3 positions, uint32 indices) and top-level structures
// whose instances it writes from the CPU into host-visible memory. Device addresses in the build inputs are mapped
// back to (MTLBuffer, offset) through the address table of device memory; instances are converted on the CPU at
// record time into Metal indirect instance descriptors, which name bottom-level structures by gpuResourceID.
#include "kmtl.h"
#include <map>

struct KAccel {
    id<MTLAccelerationStructure> mtl = nil;
    uint64_t address = 0;  // what vkGetAccelerationStructureDeviceAddressKHR hands out
};

// ---------------------------------------------------------------------------------------------------------------
// Device address -> memory

static std::mutex g_addrMutex;
static std::map<uint64_t, KDeviceMemory*> g_addrMap;  // whole-buffer gpuAddress -> memory
static std::unordered_map<uint64_t, KAccel*> g_accels;  // handed-out address -> structure

void kmtlRegisterMemory(KDeviceMemory* m) {
    std::lock_guard<std::mutex> lock(g_addrMutex);
    g_addrMap[m->whole.gpuAddress] = m;
}

void kmtlUnregisterMemory(KDeviceMemory* m) {
    std::lock_guard<std::mutex> lock(g_addrMutex);
    g_addrMap.erase(m->whole.gpuAddress);
}

static KDeviceMemory* memoryAt(uint64_t addr, NSUInteger* offset) {
    std::lock_guard<std::mutex> lock(g_addrMutex);
    auto it = g_addrMap.upper_bound(addr);
    if (it == g_addrMap.begin()) return nullptr;
    --it;
    KDeviceMemory* m = it->second;
    if (addr >= it->first + m->size) return nullptr;
    *offset = (NSUInteger)(addr - it->first);
    return m;
}

// ---------------------------------------------------------------------------------------------------------------
// Descriptors

static MTLAccelerationStructureDescriptor* describe(const VkAccelerationStructureBuildGeometryInfoKHR* info,
                                                    const uint32_t* primitiveCounts, bool resolveBuffers,
                                                    const VkAccelerationStructureBuildRangeInfoKHR* ranges,
                                                    id<MTLDevice> dev, id<MTLBuffer>* outInstances) {
    if (info->type == VK_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL_KHR) {
        MTLInstanceAccelerationStructureDescriptor* d = [MTLInstanceAccelerationStructureDescriptor descriptor];
        d.instanceDescriptorType = MTLAccelerationStructureInstanceDescriptorTypeIndirect;
        uint32_t count = primitiveCounts ? primitiveCounts[0] : ranges[0].primitiveCount;
        d.instanceCount = count;
        if (resolveBuffers) {
            const VkAccelerationStructureGeometryKHR* g = info->pGeometries ? &info->pGeometries[0] : info->ppGeometries[0];
            NSUInteger off = 0;
            KDeviceMemory* m = memoryAt(g->geometry.instances.data.deviceAddress + ranges[0].primitiveOffset, &off);
            if (!m || !m->mapped || g->geometry.instances.arrayOfPointers) {
                KERR("TLAS instances must be an array in host-visible memory (KSA writes them from the CPU)");
                return nil;
            }
            auto* src = (const VkAccelerationStructureInstanceKHR*)((uint8_t*)m->mapped + off);
            id<MTLBuffer> buf = [dev newBufferWithLength:std::max<NSUInteger>(1, count) * sizeof(MTLIndirectAccelerationStructureInstanceDescriptor)
                                                 options:MTLResourceStorageModeShared];
            auto* dst = (MTLIndirectAccelerationStructureInstanceDescriptor*)buf.contents;
            for (uint32_t i = 0; i < count; i++) {
                const auto& s = src[i];
                auto& o = dst[i];
                memset(&o, 0, sizeof(o));
                // Vulkan: row-major 3x4; Metal: 4 columns of 3
                for (int c = 0; c < 4; c++)
                    for (int r = 0; r < 3; r++) o.transformationMatrix.columns[c].elements[r] = s.transform.matrix[r][c];
                o.options = MTLAccelerationStructureInstanceOptionNone;
                if (s.flags & VK_GEOMETRY_INSTANCE_TRIANGLE_FACING_CULL_DISABLE_BIT_KHR)
                    o.options |= MTLAccelerationStructureInstanceOptionDisableTriangleCulling;
                if (s.flags & VK_GEOMETRY_INSTANCE_TRIANGLE_FLIP_FACING_BIT_KHR)
                    o.options |= MTLAccelerationStructureInstanceOptionTriangleFrontFacingWindingCounterClockwise;
                if (s.flags & VK_GEOMETRY_INSTANCE_FORCE_OPAQUE_BIT_KHR) o.options |= MTLAccelerationStructureInstanceOptionOpaque;
                if (s.flags & VK_GEOMETRY_INSTANCE_FORCE_NO_OPAQUE_BIT_KHR) o.options |= MTLAccelerationStructureInstanceOptionNonOpaque;
                o.mask = s.mask;
                o.intersectionFunctionTableOffset = s.instanceShaderBindingTableRecordOffset;
                o.userID = s.instanceCustomIndex;
                KAccel* blas = nullptr;
                {
                    std::lock_guard<std::mutex> lock(g_addrMutex);
                    auto it = g_accels.find(s.accelerationStructureReference);
                    if (it != g_accels.end()) blas = it->second;
                }
                if (blas) o.accelerationStructureID = blas->mtl.gpuResourceID;
                else KERR("TLAS instance %u references unknown structure 0x%llx", i, (unsigned long long)s.accelerationStructureReference);
            }
            d.instanceDescriptorBuffer = buf;
            d.instanceDescriptorBufferOffset = 0;
            d.instanceDescriptorStride = sizeof(MTLIndirectAccelerationStructureInstanceDescriptor);
            *outInstances = buf;
        }
        return d;
    }
    MTLPrimitiveAccelerationStructureDescriptor* d = [MTLPrimitiveAccelerationStructureDescriptor descriptor];
    NSMutableArray* geoms = [NSMutableArray array];
    for (uint32_t i = 0; i < info->geometryCount; i++) {
        const VkAccelerationStructureGeometryKHR* g = info->pGeometries ? &info->pGeometries[i] : info->ppGeometries[i];
        if (g->geometryType != VK_GEOMETRY_TYPE_TRIANGLES_KHR) {
            KERR("acceleration structure geometry type %d not supported", g->geometryType);
            return nil;
        }
        const auto& t = g->geometry.triangles;
        MTLAccelerationStructureTriangleGeometryDescriptor* gd = [MTLAccelerationStructureTriangleGeometryDescriptor descriptor];
        uint32_t count = primitiveCounts ? primitiveCounts[i] : ranges[i].primitiveCount;
        gd.triangleCount = count;
        gd.vertexStride = t.vertexStride;
        gd.vertexFormat = t.vertexFormat == VK_FORMAT_R32G32B32A32_SFLOAT ? MTLAttributeFormatFloat4 : MTLAttributeFormatFloat3;
        gd.opaque = (g->flags & VK_GEOMETRY_OPAQUE_BIT_KHR) != 0;
        if (t.indexType != VK_INDEX_TYPE_NONE_KHR) gd.indexType = t.indexType == VK_INDEX_TYPE_UINT16 ? MTLIndexTypeUInt16 : MTLIndexTypeUInt32;
        if (resolveBuffers) {
            const auto& r = ranges[i];
            NSUInteger off = 0;
            KDeviceMemory* vm = memoryAt(t.vertexData.deviceAddress, &off);
            if (!vm) { KERR("BLAS vertex address not found"); return nil; }
            gd.vertexBuffer = vm->whole;
            gd.vertexBufferOffset = off + (NSUInteger)r.firstVertex * t.vertexStride;
            if (t.indexType != VK_INDEX_TYPE_NONE_KHR) {
                KDeviceMemory* im = memoryAt(t.indexData.deviceAddress, &off);
                if (!im) { KERR("BLAS index address not found"); return nil; }
                gd.indexBuffer = im->whole;
                gd.indexBufferOffset = off + r.primitiveOffset;
            } else {
                gd.vertexBufferOffset += r.primitiveOffset;
            }
            // KSA's transforms are identity; a non-identity transform would need a column-major copy
        }
        [geoms addObject:gd];
    }
    d.geometryDescriptors = geoms;
    return d;
}

VKFN(void) vkGetAccelerationStructureBuildSizesKHR(VkDevice device, VkAccelerationStructureBuildTypeKHR,
                                                   const VkAccelerationStructureBuildGeometryInfoKHR* info,
                                                   const uint32_t* maxPrimitiveCounts, VkAccelerationStructureBuildSizesInfoKHR* sizes) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    id<MTLBuffer> unused = nil;
    MTLAccelerationStructureDescriptor* d = describe(info, maxPrimitiveCounts, false, nullptr, dev->mtl, &unused);
    if (!d) { sizes->accelerationStructureSize = sizes->buildScratchSize = sizes->updateScratchSize = 0; return; }
    MTLAccelerationStructureSizes s = [dev->mtl accelerationStructureSizesWithDescriptor:d];
    sizes->accelerationStructureSize = s.accelerationStructureSize;
    sizes->buildScratchSize = std::max<NSUInteger>(s.buildScratchBufferSize, 256);
    sizes->updateScratchSize = std::max<NSUInteger>(s.refitScratchBufferSize, 256);
}

VKFN(VkResult) vkCreateAccelerationStructureKHR(VkDevice device, const VkAccelerationStructureCreateInfoKHR* ci,
                                                const VkAllocationCallbacks*, VkAccelerationStructureKHR* out) {
    KTRACE_FIRST();
    auto* dev = (KDevice*)device;
    // A standalone structure (KSA's are small); the Vulkan buffer it sits in stays unused
    auto* a = new KAccel();
    a->mtl = [dev->mtl newAccelerationStructureWithSize:std::max<VkDeviceSize>(ci->size, 256)];
    if (!a->mtl) {
        delete a;
        return VK_ERROR_OUT_OF_DEVICE_MEMORY;
    }
    dev->addResident(a->mtl);
    // The structure's address is its buffer's device address at the offset, as on other drivers: KSA's IVA ray
    // tracing writes that buffer address (not vkGetAccelerationStructureDeviceAddressKHR's) into TLAS instances
    auto* buf = (KBuffer*)ci->buffer;
    a->address = buf && buf->mtl ? buf->gpuAddress() + ci->offset : a->mtl.gpuResourceID._impl;
    {
        std::lock_guard<std::mutex> lock(g_addrMutex);
        g_accels[a->address] = a;
    }
    *out = (VkAccelerationStructureKHR)a;
    return VK_SUCCESS;
}

VKFN(void) vkDestroyAccelerationStructureKHR(VkDevice device, VkAccelerationStructureKHR as, const VkAllocationCallbacks*) {
    auto* a = (KAccel*)as;
    if (!a) return;
    {
        std::lock_guard<std::mutex> lock(g_addrMutex);
        // A newer structure may already sit at the same buffer address
        auto it = g_accels.find(a->address);
        if (it != g_accels.end() && it->second == a) g_accels.erase(it);
    }
    ((KDevice*)device)->removeResident(a->mtl);
    delete a;
}

VKFN(VkDeviceAddress) vkGetAccelerationStructureDeviceAddressKHR(VkDevice, const VkAccelerationStructureDeviceAddressInfoKHR* info) {
    KTRACE_FIRST();
    return ((KAccel*)info->accelerationStructure)->address;
}

uint64_t kmtlAccelId(VkAccelerationStructureKHR as) {
    auto* a = (KAccel*)as;
    return a && a->mtl ? a->mtl.gpuResourceID._impl : 0;
}

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
        if (infos[i].mode != VK_BUILD_ACCELERATION_STRUCTURE_MODE_BUILD_KHR) {
            static std::atomic<bool> logged{false};
            if (!logged.exchange(true)) KLOG(1, "acceleration structure update: rebuilt");
        }
        id<MTLBuffer> instances = nil;
        MTLAccelerationStructureDescriptor* d = describe(&infos[i], nullptr, true, ranges[i], dev->mtl, &instances);
        if (!d || !dst) continue;
        MTLAccelerationStructureSizes s = [dev->mtl accelerationStructureSizesWithDescriptor:d];
        if (s.accelerationStructureSize > dst->mtl.size) KERR("acceleration structure too small (%lu > %lu)",
                                                              (unsigned long)s.accelerationStructureSize, (unsigned long)dst->mtl.size);
        id<MTLBuffer> scratch = [dev->mtl newBufferWithLength:std::max<NSUInteger>(s.buildScratchBufferSize, 256)
                                                      options:MTLResourceStorageModePrivate];
        [enc buildAccelerationStructure:dst->mtl descriptor:d scratchBuffer:scratch scratchBufferOffset:0];
        if (instances) [enc useResource:instances usage:MTLResourceUsageRead];
    }
    [enc updateFence:cb->finishFence(cb->encRoles)];
    [enc endEncoding];
    cb->encRoles = 0;
}
