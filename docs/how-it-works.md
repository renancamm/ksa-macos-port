# How it works

KSA is a .NET 10 program built on RocketWerkz's BRUTAL framework, rendering with Vulkan. Its Linux release is the
game's .NET assemblies and content plus Linux builds of everything native: the .NET runtime, BRUTAL's native
libraries (GLFW among them), FMOD. The port keeps the game's own files and replaces everything native with macOS builds:

```
KSA.dll and the other game assemblies (.NET IL)        unchanged, except KSA.dll's machine type
  |-- .NET runtime                                     Microsoft's macOS arm64 runtime
  |-- BRUTAL native libraries (ImGui, KTX, VMA, ...)   RocketWerkz's macOS builds, from nuget.org
  |-- GLFW (window, input)                             RocketWerkz's macOS build, from nuget.org, plus the GLFW shim
  |-- FMOD (audio)                                     FMOD's macOS library, in third_party/fmod
  `-- Vulkan                                           the KSA Metal driver, built here (driver/)
        `-- Metal
```

## The steps

`port.sh` runs these scripts in order. Each does one thing and can be read on its own; they share
[`scripts/lib.sh`](../scripts/lib.sh), and every external source is pinned in [`scripts/pins.sh`](../scripts/pins.sh).

| Step | Script | What it does |
|---|---|---|
| Native libraries | `fetch-brutal-natives.sh` | RocketWerkz's macOS builds of BRUTAL's libraries from nuget.org -> `dist/` |
| | `fetch-prebuilt.sh` | this project's own libraries from a GitHub release, if one matches the sources; otherwise: |
| | `build-glfw-shim.sh` | `src/glfw_shim.c` -> `dist/libGlfwShim.dylib` |
| | `build-spirv-cross.sh`, `build-spirv-tools.sh` | static libraries for the driver |
| | `build-driver.sh` | `driver/` -> `dist/libvulkan.1.dylib` |
| | `check-natives.sh` | audits `dist/`: arm64, macOS 26, signatures, dependencies, licences |
| .NET | `fetch-dotnet.sh` | Microsoft's .NET runtime, SHA-512 checked |
| Convert | `convert-cleanup.sh` | removes the Linux binaries and Windows installer files |
| | `convert-dotnet.sh` | makes the .NET files run on the macOS runtime |
| | `convert-shaders.sh` | fixes two bugs in the ground clutter shaders |
| App | `make-app.sh` | builds the app around the converted game, or refreshes one |

`update.sh` runs `git pull`, then either `port.sh` on a newer archive, or the same steps minus the extraction on the
installed app: every conversion step can run again on a converted game.

## Native libraries

### BRUTAL's libraries from nuget.org

RocketWerkz publishes BRUTAL as NuGet packages (`Ahwoo.BRUTAL.*`), and the packages that wrap native libraries
carry RocketWerkz's builds for Windows, Linux and macOS on Apple Silicon (`runtimes/osx-arm64/native/`). The port
takes the macOS builds of the nine it needs, unmodified:

| Package | Library | Used by |
|---|---|---|
| Ahwoo.BRUTAL.Stb | `libstb.dylib` | image loading and writing |
| Ahwoo.BRUTAL.Vulkan | `libVulkanEx.dylib` | batch Vulkan helpers |
| Ahwoo.BRUTAL.Vulkan.Vma | `libvma.dylib` | GPU memory (Vulkan Memory Allocator) |
| Ahwoo.BRUTAL.Glfw | `libglfw.3.dylib` | the window and input (RocketWerkz's fork of GLFW 3.5.1) |
| Ahwoo.BRUTAL.Gli | `libgli.dylib` | texture formats |
| Ahwoo.BRUTAL.ImGui | `libimgui.dylib` | the user interface |
| Ahwoo.BRUTAL.ImPlot | `libimplot.dylib` | the profiler's plots |
| Ahwoo.BRUTAL.Ktx | `libKtx.dylib` | KTX2 textures |
| Ahwoo.BRUTAL.ShaderC | `libshaderc_shared.dylib`, `libSPIRV-Tools-shared.dylib` | compiling the game's GLSL shaders at startup |

These are the versions KSA itself ships: since KSA 2026.10.19, every Linux library in the game's archive is
byte-identical to the Linux build in the matching package, so the macOS builds come from the same release as the
game's BRUTAL assemblies. `convert-dotnet.sh` compares each package with its assembly (`Ahwoo.BRUTAL.Glfw` with
`Brutal.Glfw.dll`): it stops when the game is older than the packages (KSA 2026.10.13 ships BRUTAL 2026.9, whose
VMA binding passes no Vulkan functions to the 2026.10 library, and the game crashes in `vmaCreateAllocator`), and
warns when the game is newer.

GLFW must be RocketWerkz's: their fork adds trackpad zoom, rotate and scroll detail callbacks, and KSA's
`Brutal.Glfw` sets them on every window, so upstream GLFW stops the game at startup (`EntryPointNotFoundException:
glfwSetScrollDetailCallback`). The package ships the library three times under its alias names, as identical
copies; `fetch-brutal-natives.sh` keeps `libglfw.3.dylib`, its install name, and links the other two to it, so that
the game and the GLFW shim load the same GLFW.

### Built here

The GLFW shim and the Metal driver are built from pinned sources with the Command Line Tools and cmake. The shim
links RocketWerkz's GLFW, with the headers of the fork commit the package is built from
([RocketWerkz/glfw](https://github.com/RocketWerkz/glfw), tag `rw/3.5.1.2`). Everything is compiled for macOS 26
explicitly (`MACOS_MIN` in `lib.sh`): without that, a library requires the macOS version of the Mac that built it.
The driver needs macOS 26 itself. Each step ad-hoc signs what it links, because macOS kills a process that loads a
library with an invalid signature.

### Prebuilt

So that players don't need the Command Line Tools, the [Prebuilt natives](../.github/workflows/prebuilt.yml)
workflow builds the libraries above on GitHub's macOS runners and publishes them as a release, named after a
stamp: the SHA-256 over every file they are built from (`natives_stamp` in `lib.sh`). `fetch-prebuilt.sh` computes
the stamp of the local files and downloads the release with that name, so it only ever installs libraries built
from exactly these sources; when there is none (a branch, local changes), the libraries are built locally.
`--from-source` always builds them. The release asset is checked against its SHA-256 and carries a build provenance
attestation (`gh attestation verify <file> --repo renancamm/ksa-macos-port`).

`dist/` belongs to the build: when the stamp changes it is emptied and filled again, so it never holds stale files.

## .NET

The Linux release is a self-contained `linux-x64` program. `convert-dotnet.sh` first compares the game's BRUTAL
version with the port's libraries (above), then:

1. Removes the 172 Linux .NET runtime libraries (`System.*.dll`, `mscorlib.dll`, ...): exactly the DLLs whose names
   are also in Microsoft's macOS runtime. Left in place, .NET loads them and fails with `BadImageFormatException`.
   The game's own DLLs and its NuGet packages (`System.CommandLine`, `System.IO.Hashing`,
   `Microsoft.Extensions.*`) aren't in the runtime and stay.
2. Removes `KSA.deps.json`, which points the .NET host at the `linux-x64` runtime pack.
3. Replaces `KSA.runtimeconfig.json` with one for the shared .NET 10 runtime (`rollForward: LatestMinor`).
4. Changes `KSA.dll`'s machine type from AMD64 (`0x8664`) to ARM64 (`0xAA64`), two bytes at the PE header offset
   plus 4. .NET on Apple Silicon refuses 64-bit (PE32+) assemblies marked for another CPU. `KSA.dll` holds only IL
   code, so the mark is the only thing in the way. The other game DLLs are platform-neutral PE32 and load as they
   are.

The app bundles Microsoft's .NET runtime for macOS (`dotnet`, `host/fxr`, `shared/Microsoft.NETCore.App`, about
85 MB), downloaded by `fetch-dotnet.sh` and checked against the SHA-512 Microsoft publishes. Its `dotnet` host is
signed by Microsoft with the entitlements the launcher needs: DYLD environment variables, unsigned libraries, JIT.

## Ground clutter shader fixes

KSA compiles its GLSL shaders from `Content/` at startup. `convert-shaders.sh` fixes two bugs in
`Content/Core/Shaders/Planet/GroundClutter/`, in three files:

1. **`PrefixSum.comp` uses the wrong uniform layout.** Its hand-written ecotype block leaves out the 16-byte
   `TextureHandles textureHandles` member that the real `EcotypeData` layout has before `numObjects`. The shader then
   reads a texture index as the object count, and reads and writes past its buffers: clutter pops and changes shape,
   and the GPU can hang, especially with clutter shadows on. The fix adds the missing member.
2. **`Solid.vert` and `SolidDepth.vert` disagree on depth.** Clutter is drawn twice: a depth pre-pass, then a colour
   pass with depth compare `EQUAL`. That needs bit-identical positions, which Vulkan only guarantees for `invariant`
   outputs. Under Metal's fast math the two differ slightly: black hatched stripes and see-through patches on trees
   and rocks. The fix declares `invariant gl_Position;` in both shaders.

Both are bugs in the game, not in a driver. The script keeps the files' CRLF line endings, does nothing to a file it
already fixed, and prints `[WARN]` instead of failing when a KSA update changed the code around a fix. Both bugs were
found with Metal shader validation (`MTL_SHADER_VALIDATION=1`, and the driver's `KSA_MTL_DUMP_MSL=<dir>` to see the
generated Metal source).

## GLFW shim

The launcher loads `libGlfwShim.dylib` with `DYLD_INSERT_LIBRARIES` (Microsoft's `dotnet` allows it). Through dyld
interposing it changes a few calls without modifying GLFW ([`src/glfw_shim.c`](../src/glfw_shim.c)). RocketWerkz's
fork already lists the desktop's own mode and keeps it when Fullscreen asks for the desktop's size; the shim adds
what the fork and the game leave out:

- It asks macOS for the scaled HiDPI display modes, as Wine does, so the Resolution list offers modes like 1280x800
  and 1512x982, instead of only the desktop's mode and the modes from 1920x1200 up.
- In Fullscreen it switches the display to the Resolution from `settings.toml`, which the game itself ignores there
  (it always uses the desktop's size, on Windows too), and sizes the window again when it leaves that mode.
- It reports the window's real position after display mode changes, which GLFW's macOS backend leaves out. Without
  it, dropdowns opened in the wrong spot, often on another monitor.
- It skips raw mouse motion calls when they are unsupported. KSA checks for support before it turns raw motion on,
  but not before it turns it off, and GLFW reports a `PlatformError` after every right-drag.

Each of these was checked against the fork, with and without the shim: without it, the Resolution list starts at
1512x982 on a MacBook display, Fullscreen stays at the desktop's size whatever the Resolution, the game keeps the
window position it got at startup through every mode change, and the log shows the raw motion error.

## Metal driver

KSA talks to the GPU through Vulkan; macOS has Metal. The driver in [`driver/`](../driver/) implements the part of
Vulkan KSA uses directly on Metal, including tessellation, ray queries, ray tracing pipelines and MSAA. It is the
app's `libvulkan.1.dylib`, so it takes the place of the Vulkan loader and a Vulkan driver at once.
[driver/README.md](../driver/README.md) describes its design and its debugging switches (`KSA_MTL_*`).

## The app

`make-app.sh` moves the converted game folder into the app as `Contents/Resources` (a move, not a copy, on the same
volume) and adds:

- `dist/*.dylib` and `libfmod.dylib` next to the game.
- The .NET runtime in `Contents/MacOS`.
- `Contents/MacOS/KSA`: [`app/launcher.sh`](../app/launcher.sh). It sets `DYLD_LIBRARY_PATH` to the game folder, so
  every library comes from the app (the driver included) and never from elsewhere on the Mac, loads the GLFW shim,
  and starts `dotnet KSA.dll`. Its output goes to `~/Library/Logs/Kitten Space Agency.log`, or to the terminal when
  started from one.
- `Contents/Resources/Licenses/`: every component's licence and notices ([THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)).
- The icon: `app/AppIcon.icns` if you add one, otherwise the kitten astronaut from KSA's title card, downloaded and
  cropped at build time and kept on later updates. The title card is RocketWerkz's artwork and never part of this
  repository.
- `Info.plist`: the game's version (read from `KSA.dll`), `LSMinimumSystemVersion` 26.0, the port's commit and
  natives stamp (`KSAPortVersion`, `KSAPortNatives`), and `NSHighResolutionCapable` off: the game renders one pixel
  per point, so its Resolution setting is the number of pixels it renders, as on Windows, instead of a 2x Retina
  framebuffer.

The bundle is not signed as a whole, because that would replace Microsoft's signature on `dotnet`. An app made on
the Mac it runs on has no quarantine attribute, so macOS opens it without one. When an app made by this port is
already at the target path, the new one is built next to it, takes its icon and the game's window layout
(`imgui.ini`), and replaces it; the old app goes to the Trash. If anything fails, the old app is left as it was.

## Network access

The scripts contact only these, and only when the file isn't downloaded already:

| Host | What | When |
|---|---|---|
| github.com | pinned sources (GLFW headers from RocketWerkz's fork, SPIRV-Cross, SPIRV-Tools, SPIRV-Headers, Vulkan-Headers); this project's prebuilt libraries; `git pull` | building from source; prebuilt lookup; `update.sh` |
| api.nuget.org | RocketWerkz's BRUTAL packages | first run, and when their pins change |
| builds.dotnet.microsoft.com | the .NET runtime | first run, and when its pin changes |
| ahwoo.com | KSA's title card, for the app icon | the first app you make |

Nothing checks for new KSA versions; the game does that itself. Every download except the prebuilt libraries is
checked against a checksum or commit in `scripts/pins.sh`; the prebuilt libraries are checked against the SHA-256
published with them and must carry the stamp of the local sources.

The player's path (prebuilt libraries) uses only tools that come with macOS: bash, curl, unzip, tar, shasum, perl,
cmp, od, dd, plutil, sips, iconutil, ditto, codesign and xattr. `git`, `clang` and `python3` need the Command Line Tools
and are only used to build from source and for `git pull`.

## Repository layout

```text
port.sh, update.sh        the two commands
scripts/                  the steps above, lib.sh and pins.sh
app/launcher.sh           the app's executable
src/glfw_shim.c           the GLFW shim
driver/                   the KSA Metal driver
third_party/fmod/         FMOD's macOS library, with its licence and notice (not MIT)
.github/workflows/        the prebuilt libraries workflow
```
