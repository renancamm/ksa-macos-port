# Every external source the build uses, pinned to an exact version and checked against a checksum or commit.
# Sourced by scripts/lib.sh. Updating a dependency means changing its lines here and nowhere else.

# RocketWerkz's official BRUTAL packages on nuget.org. Their runtimes/osx-arm64/native/ folders hold the native
# libraries KSA's BRUTAL assemblies load. KSA 2026.10.19 ships exactly these versions (the Linux libraries in its
# archive are the packages' linux-x64 ones); src/port/lib/convert-dotnet.sh compares them with the game's assemblies.
# Format: "<package id> <version> <SHA-512 of the .nupkg>".
BRUTAL_PACKAGES=(
    "Ahwoo.BRUTAL.Stb         2026.10.2 d8f1c0f544e7a00f75163defbcb6cb26e0dcb7fc6e1b42c0bd665b30f5f69b1bb914a683e0cc9de3b3f50018d993677da3cfb423cb2507f5b307099b2695f60f"
    "Ahwoo.BRUTAL.Vulkan      2026.10.2 728905f6e529cba8eb76c27d96a50e9fa9ad73589a4d9bfbffb7114a8d957a6d4aefb4284c954e6d9cdcab6573b923f0baf067f7488be5978f487ae8fea621eb"
    "Ahwoo.BRUTAL.Gli         2026.10.1 7e0e8c8d60e20d00d64b03dac61b27104018df2c9a226f844a83a0eb6254facecb01b86a86223af9bcbd010d7e6b02f45afd41af2e9d47e488a2623815ef7a88"
    "Ahwoo.BRUTAL.ImGui       2026.10.2 d97bd63979bc15236038710357b100303eafaad078506c5985eb359a993814a8eddfa9d78a3f2652d4dfe17062b1f7a797a65424400b4d31b2ae9a49caf33ad0"
    "Ahwoo.BRUTAL.ImPlot      2026.10.2 751ec7ec8d3aa2044bb472860803d6a7606032faec1b50d1bc5796d86ae2852ddd002d9b53e37690cbe2c7ebd32c441ad1c95c871f7744a2d5efb8a43ec5c9cc"
    "Ahwoo.BRUTAL.Ktx         2026.10.1 822eaa4a67ed44c7cafd4a4e5cf39f6d4cd9f7491eb467a9baab8838b8a9d0ffcc932ab136d3219e591e1e93620e0279b70ed3d05fd66db7f5d434164bbd3ba4"
    "Ahwoo.BRUTAL.ShaderC     2026.10.2 47dd0dd150a8cdc67a5f119221ea6fef38afbb6b62c7e2cd6657e66ecb24cad603f24f0d390f617bf69a178dfb7135fba13a53baeff989d1dde3339f6bce796a"
    "Ahwoo.BRUTAL.Glfw        2026.10.4 927964af93896c17efb40e9ae914b994e7cbdc8f31a00c77c782fc6d55543ca1ccf54baaf73e0d8230961d57d6c264828cc37a68a851b6cfa904a0bbe1d0aca9"
    "Ahwoo.BRUTAL.Vulkan.Vma  2026.10.2 164713a8eedadbdf83a192cea9ccb598a87ab750b5b6b293e83a6cabef9b1547d77b6f303395fac2aba482cd0401d0adfdcc067420539f71be7db61898748888"
)
NUGET_URL="https://api.nuget.org/v3-flatcontainer"

# Sources we build from, as "<url> <commit>". The tag each commit belongs to is in the comment.
# GLFW: RocketWerkz's fork, the commit Ahwoo.BRUTAL.Glfw 2026.10.4 is built from. Only its headers are used, to
# build the GLFW shim against the library from the package.
GLFW_SOURCE="https://github.com/RocketWerkz/glfw.git d02a9def75dc5e03411d7f7fc5e0fbad506d2938"             # rw/3.5.1.2
VULKAN_HEADERS_SOURCE="https://github.com/KhronosGroup/Vulkan-Headers.git 6802bb4733b63ed5efd3adb308a6c885ef180ea1" # vulkan-sdk-1.4.363.0
SPIRV_CROSS_SOURCE="https://github.com/KhronosGroup/SPIRV-Cross.git 300681bbcb8365a8d1eacdacdbdd0249bbf1d6b1"     # vulkan-sdk-1.4.363.0 + 25
SPIRV_HEADERS_SOURCE="https://github.com/KhronosGroup/SPIRV-Headers.git 496543121ce6419f23d6fa5d7194ba66c36212d2" # vulkan-sdk-1.4.363.0
SPIRV_TOOLS_SOURCE="https://github.com/KhronosGroup/SPIRV-Tools.git ef96ed763b43b59b33b31b362f09a02b729fa1c9"     # vulkan-sdk-1.4.363.0

# The .NET runtime bundled into the app: Microsoft's osx-arm64 build, SHA-512 from Microsoft's release metadata
# (https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
DOTNET_VERSION="10.0.13"
DOTNET_URL="https://builds.dotnet.microsoft.com/dotnet/Runtime/${DOTNET_VERSION}/dotnet-runtime-${DOTNET_VERSION}-osx-arm64.tar.gz"
DOTNET_SHA512="339c5b031b5fd7d64edd27314a1d0f8a1320b8c61f9ac305c1d4cdb61c94bc438482867ceea09d38fc7194da2a05e6a86b164992a5ec793ea3d65ccce0ea6bac"

# Microsoft's .NET SDK, to build the benchmark mod and its report program (the feature band of the runtime above),
# SHA-512 from the same release metadata.
DOTNET_SDK_VERSION="10.0.113"
DOTNET_SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${DOTNET_SDK_VERSION}/dotnet-sdk-${DOTNET_SDK_VERSION}-osx-arm64.tar.gz"
DOTNET_SDK_SHA512="1fe7e83b47037e175611504f0b238a18d95589e1413a22ea44de43d8a5bffc283b9aadbd5b2c4de3cef243ac5d0167f2c75d22e5e719ecea4b9e08707ca1050a"

# StarMap, the mod loader in payload/starmap/: its release (the Windows build, platform-neutral .NET) and the
# licences of StarMap and of Harmony, which it includes, at the matching tags.
STARMAP_VERSION="0.4.8"
STARMAP_URL="https://github.com/StarMapLoader/StarMap/releases/download/${STARMAP_VERSION}/StarMap-${STARMAP_VERSION}.zip"
STARMAP_SHA256="5324240f15d6841835158940057359ef011149bbc30b4d50f7b27480df7b2d5c"
STARMAP_LICENSE_URL="https://raw.githubusercontent.com/StarMapLoader/StarMap/${STARMAP_VERSION}/LICENSE.txt"
HARMONY_LICENSE_URL="https://raw.githubusercontent.com/pardeike/Harmony/v2.4.2.0/LICENSE"

# The app icon: the kitten astronaut from KSA's title card, cropped by scripts/build-icon.sh.
TITLE_CARD_URL="https://ahwoo.com/ksa/title-card.jpg"
