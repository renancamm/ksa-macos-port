# Every external source the port uses, pinned to an exact version and checked against a checksum or commit.
# Sourced by scripts/lib.sh. Updating a dependency means changing its lines here and nowhere else.

# RocketWerkz's official BRUTAL packages on nuget.org. Their runtimes/osx-arm64/native/ folders hold the native
# libraries KSA's BRUTAL assemblies load, except GLFW and VMA (see scripts/fetch-brutal-natives.sh).
# Format: "<package id> <version> <SHA-512 of the .nupkg>".
BRUTAL_PACKAGES=(
    "Ahwoo.BRUTAL.Stb         2026.10.2 d8f1c0f544e7a00f75163defbcb6cb26e0dcb7fc6e1b42c0bd665b30f5f69b1bb914a683e0cc9de3b3f50018d993677da3cfb423cb2507f5b307099b2695f60f"
    "Ahwoo.BRUTAL.Vulkan      2026.10.2 728905f6e529cba8eb76c27d96a50e9fa9ad73589a4d9bfbffb7114a8d957a6d4aefb4284c954e6d9cdcab6573b923f0baf067f7488be5978f487ae8fea621eb"
    "Ahwoo.BRUTAL.Gli         2026.10.1 7e0e8c8d60e20d00d64b03dac61b27104018df2c9a226f844a83a0eb6254facecb01b86a86223af9bcbd010d7e6b02f45afd41af2e9d47e488a2623815ef7a88"
    "Ahwoo.BRUTAL.ImGui       2026.10.2 d97bd63979bc15236038710357b100303eafaad078506c5985eb359a993814a8eddfa9d78a3f2652d4dfe17062b1f7a797a65424400b4d31b2ae9a49caf33ad0"
    "Ahwoo.BRUTAL.ImPlot      2026.10.2 751ec7ec8d3aa2044bb472860803d6a7606032faec1b50d1bc5796d86ae2852ddd002d9b53e37690cbe2c7ebd32c441ad1c95c871f7744a2d5efb8a43ec5c9cc"
    "Ahwoo.BRUTAL.Ktx         2026.10.1 822eaa4a67ed44c7cafd4a4e5cf39f6d4cd9f7491eb467a9baab8838b8a9d0ffcc932ab136d3219e591e1e93620e0279b70ed3d05fd66db7f5d434164bbd3ba4"
    "Ahwoo.BRUTAL.ShaderC     2026.10.2 47dd0dd150a8cdc67a5f119221ea6fef38afbb6b62c7e2cd6657e66ecb24cad603f24f0d390f617bf69a178dfb7135fba13a53baeff989d1dde3339f6bce796a"
)
NUGET_URL="https://api.nuget.org/v3-flatcontainer"

# Sources we build from, as "<url> <commit>". The tag each commit belongs to is in the comment.
GLFW_SOURCE="https://github.com/glfw/glfw.git d9d6f0f1f967807ffade6598ea9a631ebaf37a56"                    # 3.5.1
VULKAN_HEADERS_SOURCE="https://github.com/KhronosGroup/Vulkan-Headers.git 6802bb4733b63ed5efd3adb308a6c885ef180ea1" # vulkan-sdk-1.4.363.0
SPIRV_CROSS_SOURCE="https://github.com/KhronosGroup/SPIRV-Cross.git 300681bbcb8365a8d1eacdacdbdd0249bbf1d6b1"     # vulkan-sdk-1.4.363.0 + 25
SPIRV_HEADERS_SOURCE="https://github.com/KhronosGroup/SPIRV-Headers.git 496543121ce6419f23d6fa5d7194ba66c36212d2" # vulkan-sdk-1.4.363.0
SPIRV_TOOLS_SOURCE="https://github.com/KhronosGroup/SPIRV-Tools.git ef96ed763b43b59b33b31b362f09a02b729fa1c9"     # vulkan-sdk-1.4.363.0
VMA_SOURCE="https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator.git bfb2ca99e1e7e9684dc06546b552ee3eff4b10aa" # master, 2026-10-12

# The .NET runtime bundled into the app: Microsoft's osx-arm64 build, SHA-512 from Microsoft's release metadata
# (https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
DOTNET_VERSION="10.0.13"
DOTNET_URL="https://builds.dotnet.microsoft.com/dotnet/Runtime/${DOTNET_VERSION}/dotnet-runtime-${DOTNET_VERSION}-osx-arm64.tar.gz"
DOTNET_SHA512="339c5b031b5fd7d64edd27314a1d0f8a1320b8c61f9ac305c1d4cdb61c94bc438482867ceea09d38fc7194da2a05e6a86b164992a5ec793ea3d65ccce0ea6bac"

# Where the prebuilt natives (the libraries this repository builds itself) are published.
PREBUILT_REPO="renancamm/ksa-macos-port"

# The app icon: the kitten astronaut from KSA's title card, cropped at build time. The card is RocketWerkz's
# artwork and is never stored in this repository.
TITLE_CARD_URL="https://ahwoo.com/ksa/title-card.jpg"
