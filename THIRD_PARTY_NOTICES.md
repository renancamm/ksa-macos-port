# Third-party notices

The app that `port.sh` makes contains the game you supply plus the components below. Each keeps its own licence.
The full licence texts and notices are in the app at `Contents/Resources/Licenses/`, in the folder named in the
last column; versions and sources are pinned in [`scripts/pins.sh`](scripts/pins.sh).

**Kitten Space Agency** itself is developed and owned by RocketWerkz. This repository contains no game code,
binaries or assets, and none of the licences here apply to the game.

## In the app

| Component | In the app | Source | Licence | `Licenses/` |
|---|---|---|---|---|
| This port: launcher, GLFW shim, KSA Metal driver | `Contents/MacOS/KSA`, `libGlfwShim.dylib`, `libvulkan.1.dylib` | this repository | MIT | `ksa-macos-port/` |
| BRUTAL native libraries, built by RocketWerkz | `libstb`, `libVulkanEx`, `libgli`, `libimgui`, `libimplot`, `libKtx`, `libshaderc_shared`, `libSPIRV-Tools-shared` (`.dylib`) | the `Ahwoo.BRUTAL.*` packages on [nuget.org](https://www.nuget.org/profiles/Ahwoo), unmodified | BRUTAL License v1.0 (© Ahwoo Limited), and the licences of the components compiled into them (below) | `brutal/` |
| GLFW | `libglfw.3.dylib` | [glfw/glfw](https://github.com/glfw/glfw), unmodified | Zlib | `glfw/` |
| Vulkan Memory Allocator | `libvma.dylib` | [GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator), unmodified, with `src/vma.cpp` | MIT | `vma/` |
| SPIRV-Cross | compiled into `libvulkan.1.dylib` | [KhronosGroup/SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) | Apache-2.0 (some files MIT or CC-BY-4.0, see its `LICENSES/`) | `spirv-cross/` |
| SPIRV-Tools | compiled into `libvulkan.1.dylib` | [KhronosGroup/SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools) | Apache-2.0 | `spirv-tools/` |
| SPIRV-Headers | compiled into `libvulkan.1.dylib` | [KhronosGroup/SPIRV-Headers](https://github.com/KhronosGroup/SPIRV-Headers) | MIT (Khronos), CC-BY-4.0 | `spirv-headers/` |
| Vulkan-Headers | compiled into `libvulkan.1.dylib` | [KhronosGroup/Vulkan-Headers](https://github.com/KhronosGroup/Vulkan-Headers) | Apache-2.0 OR MIT | `vulkan-headers/` |
| .NET runtime | `Contents/MacOS/dotnet`, `host/`, `shared/` | Microsoft, [dotnet.microsoft.com](https://dotnet.microsoft.com), unmodified | MIT, with the notices in `ThirdPartyNotices.txt` | `dotnet/` |
| FMOD Engine (core library) | `libfmod.dylib` | Firelight Technologies, from this repository's [`third_party/fmod/`](third_party/fmod/) | FMOD End User Licence Agreement (proprietary, not MIT) | `fmod/` |
| App icon | `AppIcon.icns` | made at build time from KSA's [title card](https://ahwoo.com/ksa/title-card.jpg) | © RocketWerkz; not covered by any licence here | - |

The BRUTAL libraries contain these components, each listed with its licence text in the package notices in
`Licenses/brutal/` (`BRUTAL-*-THIRD-PARTY-NOTICES.txt`):

| Library | Components |
|---|---|
| `libstb` | stb_image, stb_image_write (MIT or Unlicense) |
| `libVulkanEx` | BRUTAL code only |
| `libgli` | GLI and GLM (MIT) |
| `libimgui` | Dear ImGui, with stb_rect_pack, stb_textedit and stb_truetype (MIT; stb: MIT or Unlicense) |
| `libimplot` | ImPlot (MIT) |
| `libKtx` | KTX-Software, Basis Universal, miniz, astc-encoder, dfdutils (Apache-2.0); Zstandard (BSD-3-Clause) |
| `libshaderc_shared` | shaderc (Apache-2.0); glslang (BSD-3-Clause, BSD-2-Clause, Apache-2.0, MIT and others, see the notice); SPIRV-Tools (Apache-2.0); SPIRV-Headers (MIT-style, CC-BY-4.0) |
| `libSPIRV-Tools-shared` | SPIRV-Tools (Apache-2.0); SPIRV-Headers |

BRUTAL's notice, as its licence asks: *This product is built with BRUTAL, a game development framework by Ahwoo
Limited, used under the BRUTAL License v1.0. www.ahwoo.com/brutal*. It is in `Licenses/brutal/NOTICE.txt`.

## In this repository

Everything is under the [MIT License](LICENSE) except [`third_party/fmod/`](third_party/fmod/), which holds
FMOD's `libfmod.dylib` under the FMOD End User Licence Agreement ([`LICENSE.TXT`](third_party/fmod/LICENSE.TXT),
[`NOTICE.txt`](third_party/fmod/NOTICE.txt)). The other components are downloaded at build time, not stored here.

## Names

Kitten Space Agency is RocketWerkz's game and BRUTAL is Ahwoo Limited's framework. FMOD is a trademark of Firelight
Technologies Pty Ltd. This is an unofficial community project, not affiliated with or endorsed by any of them.
