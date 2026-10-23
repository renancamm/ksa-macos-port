# Every external source the build uses, pinned to an exact version and checked against its checksum. Included by
# CMakeLists.txt; updating a dependency means changing its lines here and nowhere else. Downloads go to
# deps/build/cmake/_deps/ and are reused while their URL and checksum stay the same.

# Sources we build from: GitHub's archive of one commit (the tag it belongs to is in the comment).
# Vulkan-Headers: the Vulkan API KSA Metal implements.
FetchContent_Declare(vulkan_headers
    URL https://github.com/KhronosGroup/Vulkan-Headers/archive/6802bb4733b63ed5efd3adb308a6c885ef180ea1.tar.gz # vulkan-sdk-1.4.363.0
    URL_HASH SHA512=212aa777b4802336db7e5f47c26f0653456d02b3dd90e678aa6ed5890d89f32aee78bedc45df3bf0a0f5b029709e492f578d186c33e0dc6d84f2b6a655bfeb82
    EXCLUDE_FROM_ALL)

# SPIRV-Cross: translates KSA's SPIR-V shaders to Metal Shading Language (static, only the parts KSA Metal uses).
FetchContent_Declare(spirv_cross
    URL https://github.com/KhronosGroup/SPIRV-Cross/archive/300681bbcb8365a8d1eacdacdbdd0249bbf1d6b1.tar.gz # vulkan-sdk-1.4.363.0 + 25
    URL_HASH SHA512=14d05c69a103eb9440c7110043df9f457fc1d78c420e1109b9f4cd989faf88c075678afef6442286f629f283ec97ca0de665519257235d71cb7ecb7fa2839f64
    EXCLUDE_FROM_ALL)
set(SPIRV_CROSS_CLI OFF)
set(SPIRV_CROSS_ENABLE_TESTS OFF)
set(SPIRV_CROSS_ENABLE_HLSL OFF)
set(SPIRV_CROSS_ENABLE_CPP OFF)
set(SPIRV_CROSS_ENABLE_REFLECT OFF)
set(SPIRV_CROSS_ENABLE_C_API OFF)
set(SPIRV_CROSS_ENABLE_UTIL OFF)

# SPIRV-Tools with SPIRV-Headers: links KSA's ray tracing shaders into one compute kernel (static).
FetchContent_Declare(spirv_headers
    URL https://github.com/KhronosGroup/SPIRV-Headers/archive/496543121ce6419f23d6fa5d7194ba66c36212d2.tar.gz # vulkan-sdk-1.4.363.0
    URL_HASH SHA512=97271ca47dd397af9a87ee050d88ab81566214e36a4c4f42f95aa61969bf6393dc9a94e5143a07b0b4c1414ccf61ec03884b5ce27ee80e216371733d70dc52bd
    SOURCE_SUBDIR none) # SPIRV-Tools includes it itself (SPIRV-Headers_SOURCE_DIR)
FetchContent_Declare(spirv_tools
    URL https://github.com/KhronosGroup/SPIRV-Tools/archive/ef96ed763b43b59b33b31b362f09a02b729fa1c9.tar.gz # vulkan-sdk-1.4.363.0
    URL_HASH SHA512=7e55c480044590ab48601c48bbab039e19a04cb392c4c7e739750d9f33b8a8eee360896b2ebc6b8e7cff59abecd3e8626c4388da46ddcdb99f89fe32a5dc5c8b
    EXCLUDE_FROM_ALL)
set(SPIRV_SKIP_TESTS ON)
set(SPIRV_SKIP_EXECUTABLES ON)
set(SPIRV_WERROR OFF)

# GLFW: RocketWerkz's fork, the commit Ahwoo.BRUTAL.Glfw 2026.10.4 is built from. Only its headers are used, to
# build the GLFW shim against the library from the package.
FetchContent_Declare(glfw_headers
    URL https://github.com/RocketWerkz/glfw/archive/d02a9def75dc5e03411d7f7fc5e0fbad506d2938.tar.gz # rw/3.5.1.2
    URL_HASH SHA512=6a9aaa8e9a9358fbea451e86c8a93b7d4b32bbf2e0be673eb2db7293d1167e2b5ff83369faa9b0d22a40bf2a6a3644da9400f35a3b85a4140319e2ad853a6dab
    SOURCE_SUBDIR none) # headers only

# RocketWerkz's official BRUTAL packages on nuget.org. Their runtimes/osx-arm64/native/ folders hold the native
# libraries KSA's BRUTAL assemblies load, and Monitor.Host's the crash-dump helper Brutal.Monitor.Subprocess. KSA 2026.10.19 ships exactly these versions (the Linux libraries in its
# archive are the packages' linux-x64 ones); src/port/lib/convert-dotnet.sh compares them with the game's assemblies.
# Format: "<package id> <version> <SHA-512 of the .nupkg>".
set(BRUTAL_PACKAGES
    "Ahwoo.BRUTAL.Stb         2026.10.2 d8f1c0f544e7a00f75163defbcb6cb26e0dcb7fc6e1b42c0bd665b30f5f69b1bb914a683e0cc9de3b3f50018d993677da3cfb423cb2507f5b307099b2695f60f"
    "Ahwoo.BRUTAL.Vulkan      2026.10.2 728905f6e529cba8eb76c27d96a50e9fa9ad73589a4d9bfbffb7114a8d957a6d4aefb4284c954e6d9cdcab6573b923f0baf067f7488be5978f487ae8fea621eb"
    "Ahwoo.BRUTAL.Gli         2026.10.1 7e0e8c8d60e20d00d64b03dac61b27104018df2c9a226f844a83a0eb6254facecb01b86a86223af9bcbd010d7e6b02f45afd41af2e9d47e488a2623815ef7a88"
    "Ahwoo.BRUTAL.ImGui       2026.10.2 d97bd63979bc15236038710357b100303eafaad078506c5985eb359a993814a8eddfa9d78a3f2652d4dfe17062b1f7a797a65424400b4d31b2ae9a49caf33ad0"
    "Ahwoo.BRUTAL.ImPlot      2026.10.2 751ec7ec8d3aa2044bb472860803d6a7606032faec1b50d1bc5796d86ae2852ddd002d9b53e37690cbe2c7ebd32c441ad1c95c871f7744a2d5efb8a43ec5c9cc"
    "Ahwoo.BRUTAL.Ktx         2026.10.1 822eaa4a67ed44c7cafd4a4e5cf39f6d4cd9f7491eb467a9baab8838b8a9d0ffcc932ab136d3219e591e1e93620e0279b70ed3d05fd66db7f5d434164bbd3ba4"
    "Ahwoo.BRUTAL.ShaderC     2026.10.2 47dd0dd150a8cdc67a5f119221ea6fef38afbb6b62c7e2cd6657e66ecb24cad603f24f0d390f617bf69a178dfb7135fba13a53baeff989d1dde3339f6bce796a"
    "Ahwoo.BRUTAL.Glfw        2026.10.4 927964af93896c17efb40e9ae914b994e7cbdc8f31a00c77c782fc6d55543ca1ccf54baaf73e0d8230961d57d6c264828cc37a68a851b6cfa904a0bbe1d0aca9"
    "Ahwoo.BRUTAL.Vulkan.Vma  2026.10.2 164713a8eedadbdf83a192cea9ccb598a87ab750b5b6b293e83a6cabef9b1547d77b6f303395fac2aba482cd0401d0adfdcc067420539f71be7db61898748888"
    "Ahwoo.BRUTAL.RakNet      2026.10.2 aa36db96a4ea169457e69dbb536a83a94eb507b9e857a9a4c4d68507215e278c3d57dbbd0f90a528b3f2518670ce975e0c9f77d7d88f151fafa2eb64e0dda496"
    "Ahwoo.BRUTAL.Monitor.Host 2026.10.2 b6f14a123257c5b28d364279bb8c3024622be0ff3b3c0ae880ce5dbc05e907118f1c8106a969bfedbc884b7ef598ff60a06815b8a6ade1d3772bf8240875f3be"
)
foreach(entry IN LISTS BRUTAL_PACKAGES)
    string(REGEX MATCHALL "[^ ]+" fields "${entry}")
    list(GET fields 0 id)
    list(GET fields 1 version)
    list(GET fields 2 sha512)
    string(TOLOWER "${id}" lower)
    string(REPLACE "." "_" name "${lower}")
    FetchContent_Declare(${name}
        URL https://api.nuget.org/v3-flatcontainer/${lower}/${version}/${lower}.${version}.nupkg
        URL_HASH SHA512=${sha512}
        DOWNLOAD_NO_EXTRACT TRUE) # CMakeLists.txt takes the macOS libraries out of it
endforeach()

# The .NET runtime bundled into the app: Microsoft's osx-arm64 build, SHA-512 from Microsoft's release metadata
# (https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
set(DOTNET_VERSION 10.0.13)
FetchContent_Declare(dotnet_runtime
    URL https://builds.dotnet.microsoft.com/dotnet/Runtime/${DOTNET_VERSION}/dotnet-runtime-${DOTNET_VERSION}-osx-arm64.tar.gz
    URL_HASH SHA512=339c5b031b5fd7d64edd27314a1d0f8a1320b8c61f9ac305c1d4cdb61c94bc438482867ceea09d38fc7194da2a05e6a86b164992a5ec793ea3d65ccce0ea6bac)

# StarMap, the mod loader in payload/starmap/: its release (the Windows build, platform-neutral .NET) and the
# licences of StarMap and of Harmony, which it includes, at the matching tags.
set(STARMAP_VERSION 0.4.8)
FetchContent_Declare(starmap
    URL https://github.com/StarMapLoader/StarMap/releases/download/${STARMAP_VERSION}/StarMap-${STARMAP_VERSION}.zip
    URL_HASH SHA256=5324240f15d6841835158940057359ef011149bbc30b4d50f7b27480df7b2d5c)
FetchContent_Declare(starmap_license
    URL https://raw.githubusercontent.com/StarMapLoader/StarMap/${STARMAP_VERSION}/LICENSE.txt
    URL_HASH SHA256=cd27b291fba83ca3016cd50b6ce9032413f3a3cc4fa87a035bbb826feb50e313
    DOWNLOAD_NO_EXTRACT TRUE)
FetchContent_Declare(harmony_license
    URL https://raw.githubusercontent.com/pardeike/Harmony/v2.4.2.0/LICENSE
    URL_HASH SHA256=a5883d0c45eb7e176d3d1631220e7f96e958e37064773d40245ae6db560bbd6d
    DOWNLOAD_NO_EXTRACT TRUE)

# The app icon: the kitten astronaut from KSA's title card, cropped by scripts/build-icon.sh. Not pinned: it is
# artwork, not code, and its owner may update it.
set(TITLE_CARD_URL https://ahwoo.com/ksa/title-card.jpg)
FetchContent_Declare(title_card
    URL ${TITLE_CARD_URL}
    DOWNLOAD_NO_EXTRACT TRUE)
