// Vulkan format -> Metal pixel and vertex formats.
#include "kmtl.h"

#define C(vk, mtl, bytes, ren, i, u) {VK_FORMAT_##vk, MTLPixelFormat##mtl, bytes, 1, 1, false, false, ren, i, u}
#define BC(vk, mtl, bytes) {VK_FORMAT_##vk, MTLPixelFormat##mtl, bytes, 4, 4, false, false, false, false, false}
#define DS(vk, mtl, bytes, d, s) {VK_FORMAT_##vk, MTLPixelFormat##mtl, bytes, 1, 1, d, s, true, false, false}

static const KFormatInfo kFormats[] = {
    C(R8_UNORM, R8Unorm, 1, true, false, false),
    C(R8_SNORM, R8Snorm, 1, true, false, false),
    C(R8_UINT, R8Uint, 1, true, false, true),
    C(R8_SINT, R8Sint, 1, true, true, false),
    C(R8_SRGB, R8Unorm_sRGB, 1, true, false, false),
    C(R8G8_UNORM, RG8Unorm, 2, true, false, false),
    C(R8G8_SNORM, RG8Snorm, 2, true, false, false),
    C(R8G8_UINT, RG8Uint, 2, true, false, true),
    C(R8G8_SINT, RG8Sint, 2, true, true, false),
    C(R8G8_SRGB, RG8Unorm_sRGB, 2, true, false, false),
    C(R8G8B8A8_UNORM, RGBA8Unorm, 4, true, false, false),
    C(R8G8B8A8_SNORM, RGBA8Snorm, 4, true, false, false),
    C(R8G8B8A8_UINT, RGBA8Uint, 4, true, false, true),
    C(R8G8B8A8_SINT, RGBA8Sint, 4, true, true, false),
    C(R8G8B8A8_SRGB, RGBA8Unorm_sRGB, 4, true, false, false),
    C(B8G8R8A8_UNORM, BGRA8Unorm, 4, true, false, false),
    C(B8G8R8A8_SRGB, BGRA8Unorm_sRGB, 4, true, false, false),
    C(A8B8G8R8_UNORM_PACK32, RGBA8Unorm, 4, true, false, false),
    C(A8B8G8R8_SRGB_PACK32, RGBA8Unorm_sRGB, 4, true, false, false),
    C(A2B10G10R10_UNORM_PACK32, RGB10A2Unorm, 4, true, false, false),
    C(A2B10G10R10_UINT_PACK32, RGB10A2Uint, 4, true, false, true),
    C(A2R10G10B10_UNORM_PACK32, BGR10A2Unorm, 4, true, false, false),
    C(B10G11R11_UFLOAT_PACK32, RG11B10Float, 4, true, false, false),
    C(E5B9G9R9_UFLOAT_PACK32, RGB9E5Float, 4, true, false, false),
    C(R5G6B5_UNORM_PACK16, B5G6R5Unorm, 2, true, false, false),
    C(R16_UNORM, R16Unorm, 2, true, false, false),
    C(R16_SNORM, R16Snorm, 2, true, false, false),
    C(R16_UINT, R16Uint, 2, true, false, true),
    C(R16_SINT, R16Sint, 2, true, true, false),
    C(R16_SFLOAT, R16Float, 2, true, false, false),
    C(R16G16_UNORM, RG16Unorm, 4, true, false, false),
    C(R16G16_SNORM, RG16Snorm, 4, true, false, false),
    C(R16G16_UINT, RG16Uint, 4, true, false, true),
    C(R16G16_SINT, RG16Sint, 4, true, true, false),
    C(R16G16_SFLOAT, RG16Float, 4, true, false, false),
    C(R16G16B16A16_UNORM, RGBA16Unorm, 8, true, false, false),
    C(R16G16B16A16_SNORM, RGBA16Snorm, 8, true, false, false),
    C(R16G16B16A16_UINT, RGBA16Uint, 8, true, false, true),
    C(R16G16B16A16_SINT, RGBA16Sint, 8, true, true, false),
    C(R16G16B16A16_SFLOAT, RGBA16Float, 8, true, false, false),
    C(R32_UINT, R32Uint, 4, true, false, true),
    C(R32_SINT, R32Sint, 4, true, true, false),
    C(R32_SFLOAT, R32Float, 4, true, false, false),
    C(R32G32_UINT, RG32Uint, 8, true, false, true),
    C(R32G32_SINT, RG32Sint, 8, true, true, false),
    C(R32G32_SFLOAT, RG32Float, 8, true, false, false),
    C(R32G32B32A32_UINT, RGBA32Uint, 16, true, false, true),
    C(R32G32B32A32_SINT, RGBA32Sint, 16, true, true, false),
    C(R32G32B32A32_SFLOAT, RGBA32Float, 16, true, false, false),
    DS(D16_UNORM, Depth16Unorm, 2, true, false),
    DS(D32_SFLOAT, Depth32Float, 4, true, false),
    DS(X8_D24_UNORM_PACK32, Depth32Float, 4, true, false),
    DS(S8_UINT, Stencil8, 1, false, true),
    // Apple GPUs have no D24S8 or D16S8: promote to D32S8 (copies of these formats are not supported)
    DS(D16_UNORM_S8_UINT, Depth32Float_Stencil8, 5, true, true),
    DS(D24_UNORM_S8_UINT, Depth32Float_Stencil8, 5, true, true),
    DS(D32_SFLOAT_S8_UINT, Depth32Float_Stencil8, 5, true, true),
    BC(BC1_RGB_UNORM_BLOCK, BC1_RGBA, 8),
    BC(BC1_RGB_SRGB_BLOCK, BC1_RGBA_sRGB, 8),
    BC(BC1_RGBA_UNORM_BLOCK, BC1_RGBA, 8),
    BC(BC1_RGBA_SRGB_BLOCK, BC1_RGBA_sRGB, 8),
    BC(BC2_UNORM_BLOCK, BC2_RGBA, 16),
    BC(BC2_SRGB_BLOCK, BC2_RGBA_sRGB, 16),
    BC(BC3_UNORM_BLOCK, BC3_RGBA, 16),
    BC(BC3_SRGB_BLOCK, BC3_RGBA_sRGB, 16),
    BC(BC4_UNORM_BLOCK, BC4_RUnorm, 8),
    BC(BC4_SNORM_BLOCK, BC4_RSnorm, 8),
    BC(BC5_UNORM_BLOCK, BC5_RGUnorm, 16),
    BC(BC5_SNORM_BLOCK, BC5_RGSnorm, 16),
    BC(BC6H_UFLOAT_BLOCK, BC6H_RGBUfloat, 16),
    BC(BC6H_SFLOAT_BLOCK, BC6H_RGBFloat, 16),
    BC(BC7_UNORM_BLOCK, BC7_RGBAUnorm, 16),
    BC(BC7_SRGB_BLOCK, BC7_RGBAUnorm_sRGB, 16),
};

const KFormatInfo* kmtlFormat(VkFormat f) {
    static std::unordered_map<int, const KFormatInfo*> map = [] {
        std::unordered_map<int, const KFormatInfo*> m;
        for (auto& x : kFormats) m[x.vk] = &x;
        return m;
    }();
    auto it = map.find(f);
    return it == map.end() ? nullptr : it->second;
}

MTLPixelFormat kmtlPixelFormat(VkFormat f) {
    const KFormatInfo* i = kmtlFormat(f);
    if (!i) {
        if (f != VK_FORMAT_UNDEFINED) KERR("unsupported format %d", f);
        return MTLPixelFormatInvalid;
    }
    return i->mtl;
}

MTLVertexFormat kmtlVertexFormat(VkFormat f) {
    switch (f) {
    case VK_FORMAT_R8_UNORM: return MTLVertexFormatUCharNormalized;
    case VK_FORMAT_R8_SNORM: return MTLVertexFormatCharNormalized;
    case VK_FORMAT_R8_UINT: return MTLVertexFormatUChar;
    case VK_FORMAT_R8_SINT: return MTLVertexFormatChar;
    case VK_FORMAT_R8G8_UNORM: return MTLVertexFormatUChar2Normalized;
    case VK_FORMAT_R8G8_SNORM: return MTLVertexFormatChar2Normalized;
    case VK_FORMAT_R8G8_UINT: return MTLVertexFormatUChar2;
    case VK_FORMAT_R8G8_SINT: return MTLVertexFormatChar2;
    case VK_FORMAT_R8G8B8_UNORM: return MTLVertexFormatUChar3Normalized;
    case VK_FORMAT_R8G8B8_SNORM: return MTLVertexFormatChar3Normalized;
    case VK_FORMAT_R8G8B8_UINT: return MTLVertexFormatUChar3;
    case VK_FORMAT_R8G8B8_SINT: return MTLVertexFormatChar3;
    case VK_FORMAT_R8G8B8A8_UNORM: return MTLVertexFormatUChar4Normalized;
    case VK_FORMAT_R8G8B8A8_SNORM: return MTLVertexFormatChar4Normalized;
    case VK_FORMAT_R8G8B8A8_UINT: return MTLVertexFormatUChar4;
    case VK_FORMAT_R8G8B8A8_SINT: return MTLVertexFormatChar4;
    case VK_FORMAT_B8G8R8A8_UNORM: return MTLVertexFormatUChar4Normalized_BGRA;
    case VK_FORMAT_A2B10G10R10_UNORM_PACK32: return MTLVertexFormatUInt1010102Normalized;
    case VK_FORMAT_A2B10G10R10_SNORM_PACK32: return MTLVertexFormatInt1010102Normalized;
    case VK_FORMAT_R16_UNORM: return MTLVertexFormatUShortNormalized;
    case VK_FORMAT_R16_SNORM: return MTLVertexFormatShortNormalized;
    case VK_FORMAT_R16_UINT: return MTLVertexFormatUShort;
    case VK_FORMAT_R16_SINT: return MTLVertexFormatShort;
    case VK_FORMAT_R16_SFLOAT: return MTLVertexFormatHalf;
    case VK_FORMAT_R16G16_UNORM: return MTLVertexFormatUShort2Normalized;
    case VK_FORMAT_R16G16_SNORM: return MTLVertexFormatShort2Normalized;
    case VK_FORMAT_R16G16_UINT: return MTLVertexFormatUShort2;
    case VK_FORMAT_R16G16_SINT: return MTLVertexFormatShort2;
    case VK_FORMAT_R16G16_SFLOAT: return MTLVertexFormatHalf2;
    case VK_FORMAT_R16G16B16_UNORM: return MTLVertexFormatUShort3Normalized;
    case VK_FORMAT_R16G16B16_SNORM: return MTLVertexFormatShort3Normalized;
    case VK_FORMAT_R16G16B16_UINT: return MTLVertexFormatUShort3;
    case VK_FORMAT_R16G16B16_SINT: return MTLVertexFormatShort3;
    case VK_FORMAT_R16G16B16_SFLOAT: return MTLVertexFormatHalf3;
    case VK_FORMAT_R16G16B16A16_UNORM: return MTLVertexFormatUShort4Normalized;
    case VK_FORMAT_R16G16B16A16_SNORM: return MTLVertexFormatShort4Normalized;
    case VK_FORMAT_R16G16B16A16_UINT: return MTLVertexFormatUShort4;
    case VK_FORMAT_R16G16B16A16_SINT: return MTLVertexFormatShort4;
    case VK_FORMAT_R16G16B16A16_SFLOAT: return MTLVertexFormatHalf4;
    case VK_FORMAT_R32_UINT: return MTLVertexFormatUInt;
    case VK_FORMAT_R32_SINT: return MTLVertexFormatInt;
    case VK_FORMAT_R32_SFLOAT: return MTLVertexFormatFloat;
    case VK_FORMAT_R32G32_UINT: return MTLVertexFormatUInt2;
    case VK_FORMAT_R32G32_SINT: return MTLVertexFormatInt2;
    case VK_FORMAT_R32G32_SFLOAT: return MTLVertexFormatFloat2;
    case VK_FORMAT_R32G32B32_UINT: return MTLVertexFormatUInt3;
    case VK_FORMAT_R32G32B32_SINT: return MTLVertexFormatInt3;
    case VK_FORMAT_R32G32B32_SFLOAT: return MTLVertexFormatFloat3;
    case VK_FORMAT_R32G32B32A32_UINT: return MTLVertexFormatUInt4;
    case VK_FORMAT_R32G32B32A32_SINT: return MTLVertexFormatInt4;
    case VK_FORMAT_R32G32B32A32_SFLOAT: return MTLVertexFormatFloat4;
    case VK_FORMAT_B10G11R11_UFLOAT_PACK32: return MTLVertexFormatFloatRG11B10;
    case VK_FORMAT_E5B9G9R9_UFLOAT_PACK32: return MTLVertexFormatFloatRGB9E5;
    default: return MTLVertexFormatInvalid;
    }
}
