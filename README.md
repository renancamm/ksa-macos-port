# Kitten Space Agency (KSA) — Native macOS Apple Silicon Port & Toolkit

[![Platform](https://img.shields.io/badge/Platform-macOS%20Apple%20Silicon%20(arm64)-black.svg?logo=apple)](https://apple.com)
[![Runtime](https://img.shields.io/badge/.NET-10.0%20(arm64)-purple.svg?logo=dotnet)](https://dotnet.microsoft.com)
[![Graphics](https://img.shields.io/badge/Graphics-Vulkan%201.4%20%E2%86%92%20Metal%203%20(MoltenVK)-orange.svg?logo=vulkan)](https://github.com/KhronosGroup/MoltenVK)
[![License](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)


Complete toolkit, native C/C++ wrappers, shader fixes, and step-by-step documentation to convert and run the Linux build of **Kitten Space Agency (KSA)** (`v2026.10.13.5602+`) **100% natively on macOS Apple Silicon (M1 / M2 / M3 / M4)** — with **all graphics features enabled** (Atmosphere, Volumetric Clouds, Cloud Shadows, Terrain Shadows, Godrays, Ground Clutter, Vessel Cascade Shadows, and FMOD Audio) and zero x86 emulation (no Rosetta 2, no Wine/CrossOver).

---

## Table of Contents
1. [Quick Start for Players (Pre-Ported Game Folder / ZIP)](#1-quick-start-for-players-pre-ported-game-folder--zip)
2. [Automated Porting Guide (From a Clean Linux Build)](#2-automated-porting-guide-from-a-clean-linux-build)
3. [Complete Manual Step-by-Step Tutorial (Deep Dive)](#3-complete-manual-step-by-step-tutorial-deep-dive)
   - [Step 1: Converting the .NET 10.0 Runtime & Patching PE32+ Assemblies](#step-1-converting-the-net-100-runtime--patching-pe32-assemblies)
   - [Step 2: Building Native C/C++ Helper Libraries (`stb`, `VulkanEx`, `vma`, `gli`)](#step-2-building-native-cc-helper-libraries-stb-vulkanex-vma-gli)
   - [Step 3: Building Dear ImGui (`v1.92.2-docking`) & ImPlot (`v0.17`)](#step-3-building-dear-imgui-v1922-docking--implot-v017)
   - [Step 4: Building KTX-Software (`v4.4.2`) & Solving the APFS Case-Insensitivity Trap](#step-4-building-ktx-software-v442--solving-the-apfs-case-insensitivity-trap)
   - [Step 5: Adding Native FMOD Engine Audio Libraries (`2.03.xx`)](#step-5-adding-native-fmod-engine-audio-libraries-203xx)
   - [Step 6: MoltenVK with Ray Queries & Ground Clutter Shader Fixes](#step-6-moltenvk-with-ray-queries--ground-clutter-shader-fixes)
   - [Step 7: Making the Game Folder Self-Contained (`@loader_path` & `codesign`)](#step-7-making-the-game-folder-self-contained-loader_path--codesign)
4. [Repository Structure](#4-repository-structure)
5. [Porting Future Game Updates](#5-porting-future-game-updates)
6. [Troubleshooting & FAQ](#6-troubleshooting--faq)
7. [Disclaimer](#7-disclaimer)

---

## 1. Quick Start for Players (Pre-Ported Game Folder / ZIP)

If someone shared an already-converted macOS game folder (`.zip` / `.tar.gz`) with you, all native `.dylib` libraries are already bundled inside the game folder. You only need to do **3 things**:

### Step 1: Install .NET 10.0 Runtime (Apple Silicon `arm64`)
KSA targets **.NET 10.0 (`net10.0`)**. Install the official macOS Arm64 `.NET 10` runtime or SDK:
- **Via Homebrew (recommended):**
  ```bash
  brew install --cask dotnet-sdk
  ```
- **Or via Microsoft's Official Installer:**  
  Download the **.NET 10.0 Arm64 `.pkg` installer** from [dotnet.microsoft.com/download/dotnet/10.0](https://dotnet.microsoft.com/download/dotnet/10.0).

Verify that `.NET 10.0` is visible:
```bash
dotnet --list-runtimes
# Must list: Microsoft.NETCore.App 10.0.x [/usr/local/share/dotnet/shared/Microsoft.NETCore.App]
```

### Step 2: Remove the macOS Gatekeeper Quarantine Attribute
When you download a `.zip` from a browser, Discord, or AirDrop, macOS attaches the `com.apple.quarantine` attribute to every file inside and blocks unsigned `.dylib` libraries from loading. Clear it once in Terminal:
```bash
cd /path/to/ksa_linux_v2026.10.13.5602
xattr -cr .
```

### Step 3: Launch the Game
Double-click **`start_mac.command`** in Finder, or run it from Terminal:
```bash
./start_mac.command
```

---

## 2. Automated Porting Guide (From a Clean Linux Build)

If you have the original, unmodified Linux archive of KSA (e.g., `ksa_linux_v2026.10.13.5602.tar`) and want to convert it into a standalone macOS Apple Silicon version automatically using the scripts in this repository:

### Step 1: Install Build Prerequisites
Ensure Xcode Command Line Tools (`xcode-select --install`), **Xcode.app** (needed once to compile MoltenVK's `ExternalDependencies.xcodeproj`), and [Homebrew](https://brew.sh) are installed, then run:
```bash
brew install --cask dotnet-sdk
brew install cmake glfw vulkan-headers vulkan-loader shaderc spirv-tools glm
```

### Step 2: Download FMOD Engine for macOS (`v2.03.xx`)
FMOD is proprietary redistributable middleware and cannot be compiled from source:
1. Go to [fmod.com/download](https://www.fmod.com/download) (free account required).
2. Download **FMOD Engine → `2.03.xx` (e.g., `2.03.08` or `2.03.15`) → macOS** (`fmodstudioapi203xxmac-installer.dmg`).
3. Double-click the downloaded `.dmg` so it mounts at `/Volumes/FMOD Programmers API Mac` (our `port_game.sh` script detects this volume automatically).

### Step 3: Clone This Repository & Build Native Libraries
```bash
git clone https://github.com/Luc8020/ksa-macos-port.git
cd ksa-macos-port

# 1. Compile all native helper libraries (libstb, libVulkanEx, libvma, libgli, libimgui, libimplot, libKtx, libGlfwShim)
#    and bundle Homebrew runtime dylibs into ./dist
./build_all.sh

# 2. Fetch MoltenVK's ray-query branch (pinned commit, no patches) and compile libMoltenVK.dylib into ./dist
./build_moltenvk.sh
```

### Step 4: Convert Your Extracted Linux KSA Folder
```bash
# Extract the original Linux archive if you haven't already:
mkdir -p ~/Games/KSA_Mac
tar -xf ~/Downloads/ksa_linux_v2026.10.13.5602.tar -C ~/Games/KSA_Mac

# Run the automated converter:
./port_game.sh ~/Games/KSA_Mac
```

### Step 5: Play!
```bash
cd ~/Games/KSA_Mac
./start_mac.command
```

### Step 6 (Optional): Build a Double-Clickable `.app`
Turn the ported folder into `Kitten Space Agency.app`, which you can open from Finder, Launchpad, or the Dock without a Terminal window or a system-wide .NET install:
```bash
./make_app.sh ~/Games/KSA_Mac                       # → ~/Applications/Kitten Space Agency.app
./make_app.sh ~/Games/KSA_Mac /Applications/KSA.app # optional: custom output path
```
What it does:
- **Moves** the game folder into `Contents/Resources`. It does not copy it.
- Bundles a private copy of the installed .NET 10 runtime (`dotnet`, `host/fxr`, `shared/Microsoft.NETCore.App`, about 85 MB) into `Contents/MacOS`.
- Installs [`app/launcher.sh`](app/launcher.sh) as the bundle executable. The launcher puts the bundled `dotnet` first on `PATH` and hands off to `start_mac.command`, so all MoltenVK settings live in one place.
- Sets the app icon: a local `app/AppIcon.icns` if you put one there, else the kitten astronaut from KSA's [title card](https://ahwoo.com/ksa/title-card.jpg), downloaded and cropped at build time. The title card is © RocketWerkz and is not part of this repository or its MIT license. Without a network connection the app is built without an icon.

Notes:
- The launcher's output goes to `~/Library/Logs/Kitten Space Agency.log`. Game saves, settings and logs stay in `~/Documents/My Games/Kitten Space Agency`.
- The bundle is deliberately **not** re-signed. Re-signing would replace Microsoft's hardened-runtime signature on `dotnet`. A locally built app carries no quarantine attribute, so it doesn't need a bundle signature.
- To undo, move the folder back out: `mv ~/Applications/"Kitten Space Agency.app"/Contents/Resources ~/Games/KSA_Mac`

---

## 3. Complete Manual Step-by-Step Tutorial (Deep Dive)

This section explains **every single technical barrier** between the Linux x64 release of KSA and macOS Apple Silicon, and how to perform each conversion step manually.

```
┌────────────────────────────────────────────────────────────────────┐
│                   KSA Game Engine (C# / .NET 10.0)                 │
│     KSA.dll patched: PE32+ Machine 0x8664 (AMD64) → 0xAA64 (ARM64) │
└───────────────┬────────────────────────────────────┬───────────────┘
                │ (P/Invoke / LibraryImport)         │ (.NET 10 JIT)
                ▼                                    ▼
┌───────────────────────────────────┐  ┌─────────────────────────────┐
│ Native ARM64 Helper Dylibs        │  │ macOS ARM64 CoreCLR Runtime │
│ • libstb.dylib / libgli.dylib     │  │ (172 Linux x64 R2R runtime  │
│ • libVulkanEx.dylib / libvma.dylib│  │  DLLs removed from folder)  │
│ • libimgui.dylib / libimplot.dylib│  └─────────────────────────────┘
│ • libKtx.dylib → libktx_real.dylib│
│ • libfmod.dylib / libfmodstudio   │
└───────────────┬───────────────────┘
                │ (Vulkan 1.4 API via libvulkan.1.dylib + MoltenVK_icd.json)
                ▼
┌────────────────────────────────────────────────────────────────────┐
│     libMoltenVK.dylib (ray-query branch, unmodified, pinned)       │
│     • VK_KHR_ray_query + acceleration structures                   │
│     • SPIRV-Cross with 16 argument buffers (Descriptor Sets 0..9)  │
└───────────────┬────────────────────────────────────────────────────┘
                │ (Metal Shading Language / Argument Buffers)
                ▼
┌────────────────────────────────────────────────────────────────────┐
│                    Apple Metal 3 (Apple Silicon GPU)               │
└────────────────────────────────────────────────────────────────────┘
```

---

### Step 1: Converting the .NET 10.0 Runtime & Patching PE32+ Assemblies

#### Why `dotnet KSA.dll` Fails Out of the Box
The official Linux archive (`ksa_linux_v2026.10.13.5602.tar`) is published as a **self-contained `linux-x64` `.NET 10.0` deployment**. This causes four immediate problems on macOS ARM64:

1. **`KSA.runtimeconfig.json` uses `"includedFrameworks"`:** This tells the `.NET` host to look for a self-contained runtime inside the current folder instead of the system-wide macOS `.NET 10.0` installation.
2. **`KSA.deps.json` requires `runtimepack.Microsoft.NETCore.App.Runtime.linux-x64/10.0.10`:** When the macOS `.NET` host reads `KSA.deps.json`, it aborts if the `linux-x64` runtimepack is missing or mismatched.
3. **172 Bundled Linux x64 ReadyToRun (R2R) Framework DLLs:** The root folder contains 172 `.NETCore.App` DLLs (`System.*.dll`, `Microsoft.CSharp.dll`, `Microsoft.VisualBasic*.dll`, `Microsoft.Win32.*.dll`, `mscorlib.dll`, `netstandard.dll`, `WindowsBase.dll`) compiled with Linux x86_64 ReadyToRun native code (`machine=0x8664` / `0xFD1D`). If left in the game directory, macOS `.NET` loads them instead of its own ARM64 framework assemblies and crashes with `System.BadImageFormatException`.
   - **⚠️ Critical Trap:** You must **NOT** blindly delete `System.*.dll` and `Microsoft.*.dll`! Of the 228 `.dll` files in the root directory, **15 files matching those prefixes are actually pure-IL NuGet dependencies of the game** that are *not* part of `Microsoft.NETCore.App`:
     - `System.CommandLine.dll`
     - `System.IO.Hashing.dll`
     - 13 `Microsoft.Extensions.*.dll` assemblies (`Configuration`, `DependencyInjection`, `Logging`, `ObjectPool`, `Options`, `Primitives`, etc.)
     If you delete those 15 files, the game will fail with `FileNotFoundException` unless you restore them from `backup_dlls/` or NuGet!
4. **`KSA.dll` Has `IMAGE_FILE_MACHINE_AMD64` (`0x8664`) in its PE32+ COFF Header:**
   - All 55 library/engine DLLs (`Brutal.*.dll`, `Planet.*.dll`, `BepuPhysics.dll`, etc.) are 32-bit PE32 (`magic=0x10B`) AnyCPU assemblies (`machine=0x014C`).
   - However, `KSA.dll` is a 64-bit **PE32+ (`magic=0x20B`)** assembly with COFF `Machine = 0x8664` (`AMD64`).
   - **⚠️ Critical Trap:** On macOS ARM64 `.NET 10`, patching a 64-bit `PE32+` (`0x20B`) assembly to `0x014C` (`I386`) still fails with `BadImageFormatException`! A `PE32+` (`0x20B`) assembly on Apple Silicon **must have `Machine = 0xAA64` (`IMAGE_FILE_MACHINE_ARM64`)** at `pe_offset + 4` (with `COMIMAGE_FLAGS_ILONLY = 1` and its ReadyToRun `ManagedNativeHeader` zeroed out).

#### How `patch_anycpu.py` Fixes All Four Issues Automatically
Run [`patch_anycpu.py`](patch_anycpu.py) against your game folder:
```bash
python3 patch_anycpu.py /path/to/ksa_linux_v2026.10.13.5602
```
What it does under the hood:
1. Parses `KSA.deps.json` (`runtimepack.Microsoft.NETCore.App.Runtime.linux-x64`) and moves **only** the 172 Linux `.NET` runtime DLLs into `backup_dlls/`, preserving `System.CommandLine.dll`, `System.IO.Hashing.dll`, and all 13 `Microsoft.Extensions.*.dll` files.
2. Renames `KSA.deps.json` $\rightarrow$ `KSA.deps.json.bak`.
3. Replaces `KSA.runtimeconfig.json` with a framework-dependent configuration:
   ```json
   {
     "runtimeOptions": {
       "tfm": "net10.0",
       "frameworks": [
         {
           "name": "Microsoft.NETCore.App",
           "version": "10.0.0"
         }
       ],
       "rollForward": "LatestMinor",
       "configProperties": {
         "System.Reflection.Metadata.MetadataUpdater.IsSupported": false,
         "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false
       }
     }
   }
   ```
4. Rewrites `KSA.dll`'s COFF `Machine` field (`offset 0x84..0x85`) from `0x8664` (`AMD64`) to `0xAA64` (`ARM64`), sets `COR20_FLAGS = 0x1` (`ILONLY`), and zeroes the ReadyToRun directory.

---

### Step 2: Building Native C/C++ Helper Libraries (`stb`, `VulkanEx`, `vma`, `gli`)

KSA's `Brutal.*` C# interop assemblies (`Brutal.Stb.dll`, `Brutal.Vulkan.dll`, `Brutal.Vulkan.Vma.dll`, `Brutal.Gli.dll`) P/Invoke into custom native helper libraries. On macOS ARM64, compile them with `-DNDEBUG` (critical: without `-DNDEBUG`, internal C++ `assert()` checks inside VMA and GLI abort the process during gameplay):

#### 2.1 `libstb.dylib` (`src/stb.c`)
Compiles `stb_image.h` and `stb_image_write.h` into a shared library:
```bash
clang -O2 -DNDEBUG -shared -fPIC -arch arm64 \
  -Wl,-install_name,@loader_path/libstb.dylib \
  -o dist/libstb.dylib src/stb.c
```

#### 2.2 `libVulkanEx.dylib` (`src/vulkanex.c`)
Implements RocketWerkz's batch Vulkan handle creation/destruction helpers (`vkCreateSAMPLER`, `vkDestroySAMPLER`, `vkDestroyIMAGE`, `vkDestroyBUFFER`, etc.):
```bash
clang -O2 -DNDEBUG -shared -fPIC -arch arm64 \
  -I/opt/homebrew/include -L/opt/homebrew/lib -lvulkan \
  -Wl,-install_name,@loader_path/libVulkanEx.dylib \
  -o dist/libVulkanEx.dylib src/vulkanex.c
```

#### 2.3 `libvma.dylib` (`src/vma.cpp`)
Compiles `VulkanMemoryAllocator` (`VMA_VULKAN_VERSION 1004000`) along with RocketWerkz's custom convenience exports (`vmaCreateDedicatedBuffer`, `vmaCreateDedicatedImage`, `vmaAllocateDedicatedMemory`, `vmaGetPhysicalDeviceProperties`, `vmaGetMemoryProperties`):
```bash
git clone --depth 1 https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator.git deps/vma
clang++ -O2 -DNDEBUG -std=c++17 -shared -fPIC -arch arm64 -w \
  -I deps/vma/include -I /opt/homebrew/include -L/opt/homebrew/lib -lvulkan \
  -Wl,-install_name,@loader_path/libvma.dylib \
  -o dist/libvma.dylib src/vma.cpp
```

#### 2.4 `libgli.dylib` (`src/gli_wrap.cpp`)
Wraps `g-truc/gli` (`createEmpty`, `create`, `load`, `loadFromMemory`, `imagedata`, `rowpitch`, `blocksize`, `iscompressed`, etc.) for `Brutal.Gli.dll`:
```bash
git clone --depth 1 https://github.com/g-truc/gli.git deps/gli
clang++ -O2 -DNDEBUG -std=c++17 -shared -fPIC -arch arm64 -DGLM_ENABLE_EXPERIMENTAL -w \
  -I deps/gli -I /opt/homebrew/include \
  -Wl,-install_name,@loader_path/libgli.dylib \
  -o dist/libgli.dylib src/gli_wrap.cpp
```

#### 2.5 `libGlfwShim.dylib` (`src/glfw_shim.c`)
Fixes two differences from the Windows build under Wine without modifying the bundled Homebrew GLFW. `start_mac.command` loads it with `DYLD_INSERT_LIBRARIES`, which the bundled `dotnet` allows (`com.apple.security.cs.allow-dyld-environment-variables`), and it replaces two calls through dyld interposing:
- **Lower resolutions:** GLFW asks macOS for the display modes without `kCGDisplayShowDuplicateLowResolutionModes`, so the scaled HiDPI modes are missing and the in-game resolution list on a MacBook display starts at 1920x1200. The shim adds that option to GLFW's calls, as Wine's mac driver does, so the list also offers 960x600, 1280x800, 1512x982 and so on.
- **Raw mouse motion error:** KSA turns raw mouse motion off without checking `glfwRawMouseMotionSupported()`, so GLFW's Cocoa backend logs a `PlatformError` after every right-drag. The shim skips raw motion calls when it is unsupported.

It must be built after GLFW is bundled into `dist/`, since it links `@loader_path/libglfw.3.dylib`:
```bash
clang -O2 -DNDEBUG -shared -fPIC -arch arm64 \
  -I/opt/homebrew/include -Ldist -lglfw \
  -framework CoreGraphics -framework CoreFoundation \
  -Wl,-install_name,@loader_path/libGlfwShim.dylib \
  -o dist/libGlfwShim.dylib src/glfw_shim.c
```

---

### Step 3: Building Dear ImGui (`v1.92.2-docking`) & ImPlot (`v0.17`)

`Brutal.ImGui.dll` and `Brutal.ImPlot.dll` bind to a custom C wrapper around **Dear ImGui `v1.92.2-docking`** and **ImPlot `v0.17`** compiled with 32-bit wide characters (`-DIMGUI_USE_WCHAR32`) and `-DNDEBUG`:

```bash
cd src/imgui
bash build_mac.sh
cp libimgui.dylib libimplot.dylib ../../dist/
```
This clones `ocornut/imgui` (`v1.92.2-docking`) and `epezent/implot` (`v0.17`), compiles them with `imgui_wrap.cpp`, `imgui_extra.cpp`, and `implot_wrap.cpp`, and exports all **1,050 ImGui functions** and **301 ImPlot functions** expected by KSA.

---

### Step 4: Building KTX-Software (`v4.4.2`) & Solving the APFS Case-Insensitivity Trap

`Brutal.Ktx.dll` P/Invokes `[LibraryImport("Ktx")]` (which resolves to `libKtx.dylib` on macOS) and calls functions suffixed with `1` (e.g., `ktxTexture_CreateFromNamedFile1`, `ktxTexture2_TranscodeBasis1`, `ktx_FreeWriteToMemoryBuffer1`).

Two macOS-specific pitfalls occur here:
1. **The APFS Case-Insensitivity Collision:**  
   macOS's default APFS file system is case-insensitive (`libktx.dylib` and `libKtx.dylib` refer to the exact same file!). If you put upstream `libktx.dylib` and the wrapper `libKtx.dylib` in the same folder, one overwrites the other.
   - **Solution:** Name the upstream Khronos library **`libktx_real.dylib`** (`install_name_tool -id @loader_path/libktx_real.dylib`) and link the wrapper **`libKtx.dylib`** against `-lktx_real`.
2. **Path Backslash & Non-Null-Terminated Span Sanitization (`src/ktx/ktx_wrap.c`):**  
   When KSA loads planetary `.ktx2` textures via `ktxTexture_CreateFromNamedFile1`, C# occasionally passes Windows-style backslashes (`\`) or a `ReadOnlySpan<byte>` buffer that is not null-terminated immediately after `.ktx2`. Our wrapper in [`src/ktx/ktx_wrap.c`](src/ktx/ktx_wrap.c) normalizes `\` $\rightarrow$ `/` and cleanly null-terminates after `.ktx2`.

Build both `libktx_real.dylib` and `libKtx.dylib` via:
```bash
bash src/ktx/build_ktx.sh dist deps/ktx
```

---

### Step 5: Adding Native FMOD Engine Audio Libraries (`2.03.xx`)

The Linux release ships `libfmod.so.14.8` and `libfmodstudio.so.14.8` (FMOD Studio API `2.03`).
1. Download **FMOD Engine `2.03.xx` for macOS** (`fmodstudioapi203xxmac-installer.dmg`) from [fmod.com/download](https://www.fmod.com/download).
2. Mount the `.dmg` and copy the 4 universal (`arm64` + `x86_64`) dylibs into the game directory:
   ```bash
   API="/Volumes/FMOD Programmers API Mac/FMOD Programmers API/api"
   cp "$API/core/lib/libfmod.dylib" "$API/core/lib/libfmodL.dylib" /path/to/game/
   cp "$API/studio/lib/libfmodstudio.dylib" "$API/studio/lib/libfmodstudioL.dylib" /path/to/game/
   ```
3. Add `@loader_path` to `libfmodstudio.dylib`'s RPATH so it finds `libfmod.dylib` in the game directory:
   ```bash
   install_name_tool -add_rpath @loader_path /path/to/game/libfmodstudio.dylib
   install_name_tool -add_rpath @loader_path /path/to/game/libfmodstudioL.dylib
   ```

---

### Step 6: MoltenVK with Ray Queries & Ground Clutter Shader Fixes

This is the most important part for running KSA with **all graphics settings enabled**, including ground clutter and clutter shadows. It has two halves: the right MoltenVK build, and three one-line fixes to KSA's own ground clutter shaders.

#### 6.1 Why MoltenVK's ray-query branch
KSA places some ground clutter (on Earth: the grass, the one non-collideable ecotype) by snapping it to the planet mesh with **Vulkan ray queries** (`InteriorRayQuery` in `Shaders/Planet/GroundClutter/Common/RayQueries.glsl`). KSA only uses them when the driver offers all of `VK_KHR_ray_query`, `VK_KHR_acceleration_structure`, `VK_KHR_buffer_device_address` and `VK_KHR_deferred_host_operations`. Upstream MoltenVK has no ray queries, so KSA falls back to a software BVH walk in the same shader (`LookupBVHDirection`). On Apple Silicon that fallback drops the frame rate to 0 as soon as grass is in view, and can keep the GPU busy long enough for macOS to reset it.

[`build_moltenvk.sh`](build_moltenvk.sh) therefore builds **dttdrv's `macgaming/ray-query-pr` branch** of MoltenVK ([MoltenVK PR #2771](https://github.com/KhronosGroup/MoltenVK/pull/2771), with the compiler side from [SPIRV-Cross PR #2663](https://github.com/KhronosGroup/SPIRV-Cross/pull/2663)), pinned to commit `176926b` and **unmodified**. Its ray tracing is experimental and off by default; `start_mac.command` turns it on with `MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1`. Both PRs are still open upstream, so the pin keeps the build reproducible.

The same branch also covers KSA's descriptor sets. KSA's planet, cloud and atmosphere shaders bind up to **10 descriptor sets** (`set = 0..9`, for example `ATMOSPHERE_DATA_SET = 8` and `PLANET_TEXTURE_SET = 9`). Older SPIRV-Cross versions allowed only 8 Metal argument buffers (`kMaxArgumentBuffers = 8`), so sets 8 and 9 fell back to discrete `[[buffer(N)]]` bindings, collided with other sets, and failed to compile:
```text
program_source:2636:632: error: cannot reserve 'buffer' resource location at index 1
```
SPIRV-Cross raised the limit to 16 in September 2026, and the branch's SPIRV-Cross includes that, so no patch is needed.

**No MoltenVK patches and no other `MVK_CONFIG_*` settings.** Earlier versions of this port patched MoltenVK (disabling `MTLResidencySet`, guarding null pipelines) and forced several `MVK_CONFIG_*` settings (single-queue semaphores, live resource checks, resume after device loss, fast math). With this branch and the shader fixes below, testing (Apple M5, macOS 27) needed none of them, and MoltenVK's defaults gave the best performance.

> **Note:** In October 2026 Khronos deprecated MoltenVK on Apple Silicon in favor of [KosmicKrisp](https://gitlab.freedesktop.org/mesa/mesa) (Mesa's Vulkan-on-Metal driver). MoltenVK remains community-maintained.

#### 6.2 Game shader bugs fixed by `patch_shaders.py`
KSA compiles its GLSL shaders from `Content/` at startup, so [`patch_shaders.py`](patch_shaders.py) (run by `port_game.sh`) edits the shader sources in `Content/Core/Shaders/Planet/GroundClutter/`. It is idempotent, keeps the files' CRLF line endings, and prints a warning instead of failing if a game update has changed the code around a fix.

1. **`PrefixSum.comp` reads the wrong uniform layout.** It declares the ecotype uniform block by hand and leaves out the 16-byte `TextureHandles textureHandles` member that the real `EcotypeData` layout (`Common/GroundClutterCommon.glsl`, filled from `ClutterCubeCellGrid.EcotypeData` on the CPU) has before `numObjects`. The shader therefore reads a bindless texture index as the object count, then reads past its LOD count buffer and **writes past its LOD offset buffer**. The game doesn't enable `robustBufferAccess`, so this is undefined behavior everywhere; on Windows the overflow apparently stays inside larger memory blocks, but on Metal it corrupts clutter data (popping, wrong shapes) and can hang the GPU, especially with clutter shadows on, where the shadow variant adds a per-cascade offset. **Fix:** add the missing member so the layouts match.
2. **The clutter depth pre-pass and colour pass disagree on depth.** Clutter is drawn twice: `SolidDepth.vert` writes depth, then `Solid.vert` shades with depth compare `EQUAL`. That only works if both vertex shaders produce bit-identical positions, which Vulkan only guarantees for `invariant` outputs. Under Metal's fast math the two shaders compute slightly different depths, which shows up as **black hatched stripes** (per triangle, flickering even with the camera still) or **see-through patches** on trees and rocks. **Fix:** declare `invariant gl_Position;` in both shaders; MoltenVK then compiles them with `preserveInvariance`.

Both bugs were found with Metal's shader validation, which reports out-of-bounds buffer accesses with the line in the generated Metal source (`MTL_SHADER_VALIDATION=1`, plus `MVK_CONFIG_SHADER_DUMP_DIR=/some/dir` to dump that source; move the game's `PipelineCache.bin` aside first so every shader is converted again).

#### 6.3 How to Build `libMoltenVK.dylib`
Run [`./build_moltenvk.sh`](build_moltenvk.sh) (it refuses to build a checkout that is at another commit or modified), or execute these steps manually:
```bash
git init deps/MoltenVK && cd deps/MoltenVK
git remote add origin https://github.com/dttdrv/MoltenVK.git
git fetch --depth 1 origin 176926b4eb7b6bd70f46a59f277b4417dd235f67
git checkout FETCH_HEAD

# Fetch and build the dependencies (SPIRV-Cross comes from dttdrv's fork, pinned by the branch)
./fetchDependencies --macos

# Build MoltenVK for macOS
make macos

# Copy the dylib into dist/ (or your game folder) and re-sign it
cp Package/Release/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib ../../dist/
install_name_tool -id @loader_path/libMoltenVK.dylib ../../dist/libMoltenVK.dylib
codesign -f -s - ../../dist/libMoltenVK.dylib
```

---

### Step 7: Making the Game Folder Self-Contained (`@loader_path` & `codesign`)

To ensure the game runs on any Apple Silicon Mac without requiring Homebrew or global environment variables:
1. **`MoltenVK_icd.json`** uses a relative path (`"./libMoltenVK.dylib"`) so the Vulkan Loader (`libvulkan.1.dylib`) always loads the ray-query `libMoltenVK.dylib` from the game directory rather than another MoltenVK installed on the system (for example Homebrew's).
2. **`start_mac.command`** sets:
   - `DYLD_LIBRARY_PATH` to the game folder (`$DIR`). Note that dyld then picks `libMoltenVK.dylib` by file name from this folder even if an ICD manifest names a different absolute path, so swap the dylib in the game folder when comparing MoltenVK builds.
   - `VK_ICD_FILENAMES="$DIR/MoltenVK_icd.json"` and `VK_DRIVER_FILES="$DIR/MoltenVK_icd.json"`
   - `DYLD_INSERT_LIBRARIES` to `libGlfwShim.dylib` (see Step 2.5)
   - `MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1`, which turns on the branch's ray queries (see Step 6). No other `MVK_CONFIG_*` settings are needed.
3. **Ad-hoc Code Signing (`codesign`):**  
   Whenever `install_name_tool` modifies a Mach-O `.dylib` on Apple Silicon, its code signature is invalidated, causing macOS `amfid` to kill the process with `SIGKILL (Killed: 9)`. Always re-sign modified `.dylib` files:
   ```bash
   for f in /path/to/game/*.dylib; do
     [ -L "$f" ] || codesign -f -s - "$f"
   done
   ```

---

## 4. Repository Structure

```text
ksa-macos-port/
├── README.md                  # Complete English documentation & step-by-step tutorial
├── LICENSE                    # MIT License
├── build_all.sh               # Builds all native C/C++ dylibs & bundles Homebrew runtime libs
├── build_moltenvk.sh          # Builds libMoltenVK.dylib from the pinned ray-query branch (no patches)
├── port_game.sh               # All-in-one converter for a fresh Linux KSA game directory
├── patch_anycpu.py            # Cleans Linux x64 runtime DLLs, updates configs, patches KSA.dll to ARM64
├── patch_shaders.py           # Fixes two bugs in KSA's ground clutter shaders (see Step 6.2)
├── pecheck.py                 # Diagnostic utility to inspect PE/CLR headers of .dll files
├── start_mac.command          # Double-clickable macOS game launcher script
├── make_app.sh                # Moves a ported game folder into a self-contained .app (bundles .NET 10)
├── app/
│   ├── launcher.sh            # .app bundle executable (Contents/MacOS/KSA) → start_mac.command
│   └── AppIcon.icns           # Optional local app icon override (git-ignored, not shipped)
├── MoltenVK_icd.json          # Vulkan ICD manifest pointing to ./libMoltenVK.dylib
└── src/
    ├── stb.c                  # STB Image / Image Write compilation unit
    ├── stb_image.h            # stb_image v2.30 header
    ├── stb_image_write.h      # stb_image_write v1.16 header
    ├── vulkanex.c             # RocketWerkz Vulkan batch creation/destruction extensions
    ├── vma.cpp                # Vulkan Memory Allocator (Vulkan 1.4) + custom dedicated helpers
    ├── gli_wrap.cpp           # C ABI wrapper around OpenGL Image (GLI)
    ├── glfw_shim.c            # libGlfwShim.dylib: low-res display modes + raw mouse motion fix
    ├── ktx/
    │   ├── build_ktx.sh       # Builds libktx_real.dylib (v4.4.2) and wrapper libKtx.dylib
    │   └── ktx_wrap.c         # RocketWerkz "1"-suffixed KTX wrapper + path sanitizer
    └── imgui/
        ├── build_mac.sh       # Clones & builds Dear ImGui (v1.92.2-docking) & ImPlot (v0.17)
        ├── imgui_wrap.cpp     # C ABI wrapper for Brutal.ImGui.dll (1,050 exports)
        ├── imgui_extra.cpp    # Custom ImGui extensions required by KSA
        └── implot_wrap.cpp    # C ABI wrapper for Brutal.ImPlot.dll (301 exports)
```

---

## 5. Porting Future Game Updates

When RocketWerkz releases a newer Linux version of KSA:
1. Extract the new Linux `.tar` archive into a folder.
2. Run:
   ```bash
   ./port_game.sh /path/to/new_ksa_linux_folder
   ```
   That's it! `port_game.sh` will automatically strip the bundled Linux x64 `.NET` runtime DLLs, update `KSA.runtimeconfig.json`, patch the new `KSA.dll` binary to `ARM64` (`0xAA64`), apply the ground clutter shader fixes, copy all native `.dylib` libraries, and sign the bundle.
3. Check the `[2/6]` shader step in its output. `[SHADER]` or `[OK]` lines mean the fixes are in place. A `[WARN]` line means the update changed that shader: either RocketWerkz fixed the bug (then the fix is no longer needed), or `patch_shaders.py` needs updating for the new code.

---

## 6. Troubleshooting & FAQ

### Q: `You must install or update .NET to run this application. Framework: 'Microsoft.NETCore.App', version '10.0.0'`
**A:** You have `.NET 8` or `.NET 9` installed, or you installed the `x64` version instead of `arm64`. KSA requires **.NET 10.0 (`arm64`)**. Run:
```bash
brew install --cask dotnet-sdk
```

### Q: `System.BadImageFormatException: Could not load file or assembly '.../KSA.dll'`
**A:** Either:
1. `KSA.dll` still has `Machine = 0x8664` (`AMD64`) or `0x014C` (`I386`) in its PE32+ header instead of `0xAA64` (`ARM64`), OR
2. Bundled Linux x64 `System.*.dll` files are still sitting in the game folder.  
Run `python3 patch_anycpu.py /path/to/game` to fix both automatically.

### Q: `System.IO.FileNotFoundException: Could not load file or assembly 'Microsoft.Extensions.DependencyInjection.Abstractions...'` (or `System.CommandLine` / `System.IO.Hashing`)
**A:** You manually deleted all `System.*.dll` and `Microsoft.*.dll` files, which accidentally removed the 15 NuGet assemblies required by KSA. Run `python3 patch_anycpu.py /path/to/game` — it automatically restores those 15 NuGet DLLs from `backup_dlls/`.

### Q: Shader error `cannot reserve 'buffer' resource location at index 1` (or `index 4`)
**A:** The Vulkan Loader is loading an older `libMoltenVK.dylib` (e.g., from `/opt/homebrew/lib/libMoltenVK.dylib`) whose SPIRV-Cross only allows 8 descriptor sets, instead of the one built by `build_moltenvk.sh`. Always launch the game via `./start_mac.command` so `VK_DRIVER_FILES` and `VK_ICD_FILENAMES` point to `./MoltenVK_icd.json`.

### Q: The frame rate drops to 0 when grass is in view, or the screen freezes and the game reports `VK_ERROR_DEVICE_LOST`
**A:** KSA is not getting ray queries and fell back to its software BVH for ground clutter (see Step 6.1). Check that the game folder's `libMoltenVK.dylib` was built by `build_moltenvk.sh` and that you launched via `./start_mac.command`, which sets `MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1`. The game log's device creation should list `VK_KHR_ray_query`.

### Q: Black hatched stripes or see-through patches on trees and rocks, or clutter that pops and changes shape
**A:** The ground clutter shader fixes are missing (see Step 6.2). Run `python3 patch_shaders.py /path/to/game` (or `./port_game.sh` again) and restart the game.

### Q: `GLFW Error: PlatformError Raw mouse motion is not supported on this system`
**A:** Harmless log message from GLFW on macOS: KSA turns raw mouse motion off after a right-drag without checking whether it is supported, and mouse look works normally. `libGlfwShim.dylib` suppresses it; if you still see it, check that `libGlfwShim.dylib` is in the game folder and that you launched the game via `./start_mac.command` (or the `.app`).

### Q: macOS terminates the process immediately with `Killed: 9` (`SIGKILL`)
**A:** Apple Silicon enforces mandatory code signing on all native ARM64 Mach-O binaries. Clear quarantine attributes and re-sign all `.dylib` files:
```bash
xattr -cr /path/to/game
for f in /path/to/game/*.dylib; do [ -L "$f" ] || codesign -f -s - "$f"; done
```

---

## 7. Disclaimer

**Kitten Space Agency (KSA)** is developed and owned by **RocketWerkz**. This repository is an unofficial, independent community compatibility toolkit and contains **no proprietary game code, no original `.dll` binaries, no FMOD binaries, and no copyrighted game assets**. You must own or download an official copy of Kitten Space Agency to use this toolkit.
