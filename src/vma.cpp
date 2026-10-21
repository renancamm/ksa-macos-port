// libvma.dylib: Vulkan Memory Allocator for Brutal.Vulkan.Vma, built by scripts/build-vma.sh.
// RocketWerkz's own build (Ahwoo.BRUTAL.Vulkan.Vma 2026.10) loads Vulkan functions only through pointers the
// caller passes, which the BRUTAL 2026.9 binding in KSA 2026.10.13 doesn't pass, so vmaCreateAllocator crashes.
// This build links the Vulkan functions (VMA_STATIC_VULKAN_FUNCTIONS, the default) from libvulkan.1.dylib.
#define VMA_IMPLEMENTATION
#define VMA_VULKAN_VERSION 1004000
#include <vk_mem_alloc.h>
