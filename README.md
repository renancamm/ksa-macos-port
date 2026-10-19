# Kitten Space Agency (KSA) on macOS Apple Silicon

> **Note:** This is a fork of [Luc8020/ksa-macos-port](https://github.com/Luc8020/ksa-macos-port). It adds the one-command installer and app bundle, MoltenVK's ray-query branch, and fixes for KSA's ground clutter shaders.

Scripts that turn the Linux release of **Kitten Space Agency** into a native Apple Silicon app, `Kitten Space Agency.app`. The game runs on the arm64 .NET 10 runtime and on Metal through MoltenVK, with no Rosetta 2 and no Wine. All graphics settings work, including ground clutter and its shadows, and FMOD audio works too.

Tested with KSA 2026.10.13.5602 on an Apple M5 with macOS 27. Other Apple Silicon Macs should work but haven't been tested, and a new KSA version can need script updates (see [Updating KSA](#updating-ksa)).

This repository contains no game files. You need your own copy of the Linux release of KSA.

## Requirements

- An Apple Silicon Mac.
- **Xcode** from the App Store, plus the Command Line Tools (`xcode-select --install`). Building MoltenVK needs the full Xcode.
- **[Homebrew](https://brew.sh)** and these packages:
  ```bash
  brew install --cask dotnet-sdk
  brew install cmake glfw vulkan-headers vulkan-loader shaderc spirv-tools glm
  ```
- **FMOD Engine 2.03 for macOS.** FMOD can't be redistributed, so download it yourself: sign in at [fmod.com/download](https://www.fmod.com/download) (free account), download **FMOD Engine → 2.03.xx → Mac**, and open the `.dmg` so it mounts at `/Volumes/FMOD Programmers API Mac`.
- **The Linux release of KSA**, for example `ksa_linux_v2026.10.13.5602.tar.gz`.

## Install

```bash
git clone https://github.com/renancamm/ksa-macos-port.git
cd ksa-macos-port
./install.sh ~/Downloads/ksa_linux_v2026.10.13.5602.tar.gz
```

Then open **Kitten Space Agency** from Finder, Launchpad or Spotlight (or run `open ~/Applications/"Kitten Space Agency.app"`).

`install.sh` does everything in one go:
1. Checks all requirements above before doing any work, and lists whatever is missing.
2. Builds the native libraries and MoltenVK into `dist/`. This happens only the first time and takes a while, mostly for MoltenVK. Later runs reuse `dist/`.
3. Extracts the archive.
4. Converts the game to macOS (`port_game.sh`).
5. Builds `~/Applications/Kitten Space Agency.app` (`make_app.sh`).

If the game has no settings file yet, `install.sh` creates one with anti-aliasing turned off (the game's default is MSAA x2). You can turn it back on in the game's display settings.

To put the app somewhere else, pass its path as a second argument: `./install.sh <archive> /Applications/KSA.app`. You can also pass an already extracted Linux folder instead of the archive; it is converted in place and moved into the app.

Saves, settings and game logs are in `~/Documents/My Games/Kitten Space Agency`. The app's own log is `~/Library/Logs/Kitten Space Agency.log`.

## Updating KSA

When a new version of KSA comes out, update this repository and run the installer on the new archive:
```bash
git pull
./install.sh ~/Downloads/ksa_linux_vNEW.tar.gz
```
- The new app replaces the old one, so the Dock icon and shortcuts keep working. The old app goes to the Trash, named with its version (for example `Kitten Space Agency 2026.10.13.5602 ….app`). If anything fails, the old app is left untouched. Delete the old app from the Trash once the new version runs.
- Your saves and settings are not touched.
- If the `git pull` changed the native code or `build_moltenvk.sh`, add `--rebuild` to rebuild `dist/` first: `./install.sh --rebuild <archive>`.
- Check the `[2/6]` shader step in the output. `[SHADER]` or `[OK]` means the [ground clutter fixes](#ground-clutter-shader-fixes) are in place. `[WARN]` means the update changed that shader: either RocketWerkz fixed the bug and the fix is no longer needed, or `patch_shaders.py` needs updating.
- If you installed the app at a custom path, pass that path again.

## Running the steps yourself

`install.sh` chains four scripts. Run them yourself to redo one step, or to keep a plain game folder instead of an app:
```bash
./build_all.sh                    # native helper libraries + bundled Homebrew libraries → dist/
./build_moltenvk.sh               # MoltenVK (ray-query branch) → dist/

mkdir -p ~/Games/KSA_Mac
tar -xf ~/Downloads/ksa_linux_v2026.10.13.5602.tar.gz -C ~/Games/KSA_Mac
./port_game.sh ~/Games/KSA_Mac    # convert the folder
./make_app.sh ~/Games/KSA_Mac     # optional: move it into ~/Applications/Kitten Space Agency.app
```
To play a plain folder without making an app, run `./start_mac.command` in it. That needs .NET 10 installed system-wide; the app brings its own.

## How it works

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
│                         Metal (Apple Silicon GPU)                  │
└────────────────────────────────────────────────────────────────────┘
```

### .NET runtime and `KSA.dll`

The Linux release is a self-contained `linux-x64` .NET 10 app, so it can't run on macOS as shipped:
- `KSA.runtimeconfig.json` (`includedFrameworks`) and `KSA.deps.json` (`runtimepack…linux-x64`) tell the .NET host to use the runtime in the game folder instead of the installed macOS one.
- The game folder holds 172 .NET runtime DLLs (`System.*`, `mscorlib`, `netstandard`, …) precompiled for Linux x64 (ReadyToRun). Left in place, .NET loads them and fails with `BadImageFormatException`. Of the 228 DLLs, 15 with the same prefixes are NuGet packages the game needs (`System.CommandLine`, `System.IO.Hashing` and 13 `Microsoft.Extensions.*`), so a blanket delete of `System.*`/`Microsoft.*` breaks the game.
- The 40 game and library DLLs (`Brutal.*`, `Planet.*`, `BepuPhysics`, …) are platform-neutral PE32 assemblies and load as they are. `KSA.dll` is a PE32+ assembly marked `AMD64` (`0x8664`), which .NET on Apple Silicon refuses. A PE32+ assembly there must be marked `ARM64` (`0xAA64`).

[`patch_anycpu.py`](patch_anycpu.py) fixes all of this:
1. Reads `KSA.deps.json` and moves only the runtime-pack DLLs to `backup_dlls/`, keeping the NuGet ones.
2. Renames `KSA.deps.json` to `KSA.deps.json.bak`.
3. Rewrites `KSA.runtimeconfig.json` to use the installed .NET 10 (`frameworks`, `rollForward: LatestMinor`).
4. Sets `KSA.dll`'s COFF `Machine` field (PE header + 4) to `0xAA64`, marks it IL-only and clears any ReadyToRun header. `KSA.dll` is IL-only already, so in practice only the machine type changes.

### Native libraries

KSA's `Brutal.*` assemblies call native libraries that ship only as Linux `.so` files. [`build_all.sh`](build_all.sh) builds arm64 versions into `dist/`, with `-DNDEBUG`: without it, `assert()`s inside VMA and GLI abort the game.

| Library | Source | Used by | What it is |
|---|---|---|---|
| `libstb.dylib` | `src/stb.c` | `Brutal.Stb` | stb_image / stb_image_write |
| `libVulkanEx.dylib` | `src/vulkanex.c` | `Brutal.Vulkan` | RocketWerkz's batch Vulkan create/destroy helpers |
| `libvma.dylib` | `src/vma.cpp` + [VulkanMemoryAllocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator) | `Brutal.Vulkan.Vma` | VMA plus RocketWerkz's dedicated-allocation exports |
| `libgli.dylib` | `src/gli_wrap.cpp` + [gli](https://github.com/g-truc/gli) | `Brutal.Gli` | C wrapper around OpenGL Image |
| `libimgui.dylib`, `libimplot.dylib` | `src/imgui/` + Dear ImGui `v1.92.2-docking`, ImPlot `v0.17` | `Brutal.ImGui`, `Brutal.ImPlot` | C wrappers, built with `IMGUI_USE_WCHAR32` |
| `libKtx.dylib`, `libktx_real.dylib` | `src/ktx/` + KTX-Software `v4.4.2` | `Brutal.Ktx` | See [KTX](#ktx) |
| `libGlfwShim.dylib` | `src/glfw_shim.c` | the launcher | See [Launcher](#launcher) |
| `libglfw.3`, `libvulkan.1`, `libshaderc_shared.1`, `libSPIRV-Tools-shared` | Homebrew | `Brutal.Glfw`, Vulkan, `Brutal.ShaderC` | Copied from Homebrew, renamed to `@loader_path` and re-signed |

Every library gets an `@loader_path/…` install name, so the game folder needs no Homebrew at run time.

### KTX

`Brutal.Ktx` loads `Ktx` (`libKtx.dylib`) and calls functions with a `1` suffix (`ktxTexture_CreateFromNamedFile1`, …). [`src/ktx/build_ktx.sh`](src/ktx/build_ktx.sh) builds KTX-Software with `KTX_FEATURE_VK_UPLOAD=ON`, plus a small wrapper that provides those names. Two macOS details:
- APFS is case-insensitive, so `libktx.dylib` and `libKtx.dylib` would be the same file. The real library is therefore named `libktx_real.dylib`, and the wrapper links against it.
- The wrapper turns `\` into `/` in texture paths, and cuts paths off after `.ktx2` when the game passes a buffer that isn't null-terminated.

### FMOD

The Linux release ships FMOD 2.03 `.so` files. `port_game.sh` copies the macOS `libfmod`, `libfmodL`, `libfmodstudio` and `libfmodstudioL` dylibs (universal binaries) from the mounted FMOD `.dmg`, and adds `@loader_path` to the studio libraries' rpath so they find `libfmod.dylib` next to them.

### MoltenVK with ray queries

KSA places some ground clutter (on Earth, the grass) by snapping it to the planet surface with Vulkan ray queries. It uses them only when the driver offers `VK_KHR_ray_query`, `VK_KHR_acceleration_structure`, `VK_KHR_buffer_device_address` and `VK_KHR_deferred_host_operations`. Release MoltenVK has no ray queries, so KSA falls back to a software BVH walk in the shader (`LookupBVHDirection`). On Apple Silicon that drops the frame rate to 0 as soon as grass is in view, and can hang the GPU until macOS resets it.

[`build_moltenvk.sh`](build_moltenvk.sh) therefore builds **dttdrv's `macgaming/ray-query-pr` branch** of MoltenVK ([MoltenVK PR #2771](https://github.com/KhronosGroup/MoltenVK/pull/2771), with [SPIRV-Cross PR #2663](https://github.com/KhronosGroup/SPIRV-Cross/pull/2663)), pinned to commit `176926b`. Both PRs were still open as of October 2026. The script builds that commit **unmodified** and refuses any other or modified checkout. Its ray tracing is experimental and off by default; the launcher turns it on with `MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1`. No other `MVK_CONFIG_*` settings are used, because MoltenVK's defaults ran best.

The branch also covers KSA's descriptor sets. Its planet, cloud and atmosphere shaders use up to 10 (`set = 0..9`). SPIRV-Cross versions that allow only 8 Metal argument buffers fail to compile them (`cannot reserve 'buffer' resource location at index 1`). The branch's SPIRV-Cross allows 16.

> In October 2026 MoltenVK was deprecated on Apple Silicon, with Khronos recommending [KosmicKrisp](https://gitlab.freedesktop.org/mesa/mesa) (Mesa's Vulkan-on-Metal driver) instead. This port still uses MoltenVK.

### Ground clutter shader fixes

KSA compiles its GLSL shaders from `Content/` at startup. [`patch_shaders.py`](patch_shaders.py), run by `port_game.sh`, fixes two bugs in `Content/Core/Shaders/Planet/GroundClutter/`, in three files:
1. **`PrefixSum.comp` uses the wrong uniform layout.** Its hand-written ecotype block leaves out the 16-byte `TextureHandles textureHandles` member that the real `EcotypeData` layout has before `numObjects`. The shader then reads a texture index as the object count, and reads and writes past its buffers. On Metal this corrupts clutter (popping, wrong shapes) and can hang the GPU, especially with clutter shadows on. The fix adds the missing member.
2. **`Solid.vert` and `SolidDepth.vert` disagree on depth.** Clutter is drawn twice: a depth pre-pass, then a colour pass with depth compare `EQUAL`. That needs bit-identical positions, which Vulkan only guarantees for `invariant` outputs. Under Metal's fast math the two differ slightly, which shows up as black hatched stripes or see-through patches on trees and rocks. The fix declares `invariant gl_Position;` in both shaders.

The script is idempotent, keeps the files' CRLF line endings, and prints `[WARN]` instead of failing when a game update has changed the code around a fix. Both bugs were found with Metal shader validation (`MTL_SHADER_VALIDATION=1`, plus `MVK_CONFIG_SHADER_DUMP_DIR=<dir>` to dump the generated Metal source; move the game's `PipelineCache.bin` aside first so every shader is converted again).

### Launcher

[`start_mac.command`](start_mac.command) starts the game with `dotnet KSA.dll` after setting:
- `VK_DRIVER_FILES` and `VK_ICD_FILENAMES` to the game folder's [`MoltenVK_icd.json`](MoltenVK_icd.json), whose relative `./libMoltenVK.dylib` path makes the Vulkan loader use the game's MoltenVK, not another one on the system (for example Homebrew's).
- `DYLD_LIBRARY_PATH` to the game folder. dyld then loads `libMoltenVK.dylib` by file name from this folder even if an ICD manifest names another path, so to compare MoltenVK builds, swap the dylib in the game folder.
- `DYLD_INSERT_LIBRARIES` to `libGlfwShim.dylib`. Microsoft's `dotnet` allows this (`com.apple.security.cs.allow-dyld-environment-variables`). The shim changes two GLFW calls without modifying GLFW: it asks macOS for the scaled HiDPI display modes, as Wine does, so the resolution list offers modes like 1280x800 and 1512x982 and doesn't start at 1920x1200; and it skips raw mouse motion calls when unsupported, which stops a `PlatformError` after every right-drag.
- `MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1` (see [MoltenVK with ray queries](#moltenvk-with-ray-queries)).

Changing a dylib with `install_name_tool` invalidates its code signature, and macOS then kills the process (`Killed: 9`). The scripts therefore ad-hoc sign every dylib they touch (`codesign -f -s -`), and `port_game.sh` clears quarantine attributes in the game folder.

### The app bundle

[`make_app.sh`](make_app.sh) turns a ported folder into `Kitten Space Agency.app`:
- It **moves** the game folder into `Contents/Resources` (it doesn't copy it).
- It bundles a private copy of the installed .NET 10 runtime (`dotnet`, `host/fxr`, `shared/Microsoft.NETCore.App`, about 85 MB) in `Contents/MacOS`, so the app needs no system-wide .NET.
- The bundle executable, [`app/launcher.sh`](app/launcher.sh), puts that `dotnet` first on `PATH`, sends output to `~/Library/Logs/Kitten Space Agency.log`, and hands off to `start_mac.command`, so all launch settings live in one place.
- The app version is the game version read from `KSA.dll`.
- The icon is `app/AppIcon.icns` if you add one (git-ignored), otherwise the kitten astronaut from KSA's [title card](https://ahwoo.com/ksa/title-card.jpg), downloaded and cropped at build time. The title card is © RocketWerkz and is not part of this repository or its license. Without a network connection the app has no icon.
- If an app built by `make_app.sh` already exists at the target path, the new app is built next to it and then replaces it, and the old app goes to the Trash.
- The bundle is deliberately **not** re-signed, because re-signing would replace Microsoft's hardened-runtime signature on `dotnet`. A locally built app has no quarantine attribute, so it doesn't need a bundle signature.

To undo, move the game folder back out: `mv ~/Applications/"Kitten Space Agency.app"/Contents/Resources ~/Games/KSA_Mac`.

## Repository layout

```text
install.sh            One command: checks requirements, builds, ports, builds or updates the app
build_all.sh          Builds the native helper libraries and bundles Homebrew libraries into dist/
build_moltenvk.sh     Builds libMoltenVK.dylib from the pinned ray-query branch
port_game.sh          Converts an extracted Linux game folder (runs the two patchers, installs dylibs, FMOD, launcher)
patch_anycpu.py       .NET fixes: moves Linux runtime DLLs, rewrites configs, patches KSA.dll to ARM64
patch_shaders.py      Fixes two bugs in KSA's ground clutter shaders
make_app.sh           Moves a ported game folder into a self-contained .app
start_mac.command     Game launcher (Vulkan driver, GLFW shim, ray tracing setting)
MoltenVK_icd.json     Vulkan driver manifest pointing to ./libMoltenVK.dylib
pecheck.py            Diagnostic: prints the PE machine type and CLR flags of a .dll
app/launcher.sh       The .app's executable; hands off to start_mac.command
src/                  Sources of the native helper libraries (stb, VulkanEx, VMA, GLI, GLFW shim, KTX, ImGui/ImPlot)
```

## Credits

- [Luc8020/ksa-macos-port](https://github.com/Luc8020/ksa-macos-port), the project this fork is based on.
- [dttdrv's MoltenVK ray-query branch](https://github.com/dttdrv/MoltenVK/tree/macgaming/ray-query-pr), and the authors of MoltenVK and SPIRV-Cross.
- [Dear ImGui](https://github.com/ocornut/imgui), [ImPlot](https://github.com/epezent/implot), [KTX-Software](https://github.com/KhronosGroup/KTX-Software), [Vulkan Memory Allocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator), [GLI](https://github.com/g-truc/gli), [stb](https://github.com/nothings/stb) and [GLFW](https://www.glfw.org).

## License and disclaimer

The scripts and sources in this repository are under the [MIT License](LICENSE).

**Kitten Space Agency** is developed and owned by **RocketWerkz**. This is an unofficial community project. It contains no game code, no game binaries, no FMOD binaries and no game assets. You need your own copy of KSA to use it.
