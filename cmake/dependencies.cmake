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

# GLFW: RocketWerkz's fork, the commit Ahwoo.BRUTAL.Glfw 2026.10.4 is built from (the library of
# Ahwoo.BRUTAL.Glfw.Native 2026.10.5 has the same exports). Only its headers are used, to build the GLFW shim against
# the library from the package.
FetchContent_Declare(glfw_headers
    URL https://github.com/RocketWerkz/glfw/archive/d02a9def75dc5e03411d7f7fc5e0fbad506d2938.tar.gz # rw/3.5.1.2
    URL_HASH SHA512=6a9aaa8e9a9358fbea451e86c8a93b7d4b32bbf2e0be673eb2db7293d1167e2b5ff83369faa9b0d22a40bf2a6a3644da9400f35a3b85a4140319e2ad853a6dab
    SOURCE_SUBDIR none) # headers only

# RocketWerkz's official BRUTAL packages on nuget.org. Their runtimes/osx-arm64/native/ folders hold the native
# libraries KSA's BRUTAL assemblies load, and Monitor.Host's the crash-dump helper Brutal.Monitor.Subprocess. Since
# the packages KSA 2026.10.25 uses, the libraries are in packages of their own, Ahwoo.BRUTAL.<X>.Native, versioned with
# Ahwoo.BRUTAL.<X> (Monitor.Host still carries its program).
# KSA 2026.10.25 ships exactly these versions (the Linux libraries in its archive are the packages' linux-x64 ones);
# src/port/lib/convert-dotnet.sh compares them with the game's assemblies.
# Format: "<package id> <version> <SHA-512 of the .nupkg>".
set(BRUTAL_PACKAGES
    "Ahwoo.BRUTAL.Stb.Native         2026.10.3 a2f3c40cf7c8426be0e55c2a1dcb90c3278f5a986b39fd760722e15d3b479de017a93a7cb1c6c06b941a990a9a2ba27c87c749e199ac834d774323130b1a3b78"
    "Ahwoo.BRUTAL.Vulkan.Native      2026.10.3 8a8b27fcf6a13764b04f1cf06beee877994501d44c56e51832c8867224b7b587de4b7e3b5873e6835d903aa4979fbf93d6b8ec0ab1c73f712165a6e51b542e64"
    "Ahwoo.BRUTAL.Gli.Native         2026.10.2 d7a425c24e389d352a7e6b1b0a13f19d4fa144f4a5a89456e1b9839df4b8dfbfcb8f897661fc2b37a7ebd1c910a1e88b5fb77c4a81d441fa6b244c92bd83a0c2"
    "Ahwoo.BRUTAL.ImGui.Native       2026.10.3 ea26fc4eca8ee34847ea7cb9c769cf1edc1d78bbb18c5fcbaee2f4f62a41f000facfe602235d1901b5561b1842ab1a3156655041f4ea194b321ce8dd417bc243"
    "Ahwoo.BRUTAL.ImPlot.Native      2026.10.3 342b6691ffe37293cc56813faddad9fa38323d266e1ee0f02fed07fb8cc27d17737f8865e69b8a3d2512691b58b0213364dd25be0902f4ec59140e069ba4169b"
    "Ahwoo.BRUTAL.Ktx.Native         2026.10.2 6980a5593beea7af9617011af8ab9c18e47d50411a528afc0832c5dd7abfa613edf6ab05986705f5038110bd6dedc2c2b243c9abc2efa6d66ab11277540f7355"
    "Ahwoo.BRUTAL.ShaderC.Native     2026.10.3 3a4b5daaec038802953e07257e88bf4e82f6576324504bd32a8bf2ffff72287a7b2b5af23e5a14ca0a78bd6feed44f1fb0f90988237ee2232aa56c273a9f181e"
    "Ahwoo.BRUTAL.Glfw.Native        2026.10.5 3fcc070ee3171d4cdf83f044af97fd7705a950adcab649d905ec31c0d86a9b2d5b95e08c505a3d7684ced01065b469b42ecea54ef0c36315e8f4acee8c7413e5"
    "Ahwoo.BRUTAL.Vulkan.Vma.Native  2026.10.3 a2da58bb67fc4ef7fb1363cd2aa77924866993edc347a7910486d6859feb40ebd8312caa03f2c5f23bbc016763fae2db5a272211814fd4f1449769e3db73e388"
    "Ahwoo.BRUTAL.RakNet.Native      2026.10.3 b7a49b9b15523a26da2b5d97e02034ceba99e0448aae98aecc3879975e502dd80779e84c856057ce39ab6117b3632c7d4f43bc1de92714247bff08ebc3ff6cd2"
    "Ahwoo.BRUTAL.Monitor.Host       2026.10.3 c216bc90a52df7bd1c86809dcfe3f28ee820a6c90b17762fe8951b27b7360bb250200712c689e245086152aab7ae946bcb95fe7f0d68fb76f4090dd1f86d5966"
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

# Harmony, which KSA Metal Hooks (src/hooks/) use to change KSA's code at runtime: the official package on
# nuget.org, the version StarMap includes (its licence: harmony_license above). Its net10.0 build goes into the app.
set(HARMONY_VERSION 2.4.2)
FetchContent_Declare(lib_harmony
    URL https://api.nuget.org/v3-flatcontainer/lib.harmony/${HARMONY_VERSION}/lib.harmony.${HARMONY_VERSION}.nupkg
    URL_HASH SHA512=c96a96244fda8eb2d463626c6882162b1a5877c7e8255960537227b02a74f7fea834e89bb373310bf0879262d4a98485b03dee60c81f9281d14d4eafdbeb1eca
    DOWNLOAD_NO_EXTRACT TRUE) # CMakeLists.txt takes the .NET 10 assembly out of it

# The app icon: the kitten astronaut from KSA's title card, cropped by scripts/build-icon.sh. Not pinned: it is
# artwork, not code, and its owner may update it.
set(TITLE_CARD_URL https://ahwoo.com/ksa/title-card.jpg)
FetchContent_Declare(title_card
    URL ${TITLE_CARD_URL}
    DOWNLOAD_NO_EXTRACT TRUE)
