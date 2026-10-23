# Kitten Space Agency on Apple Silicon Macs

An experimental community project that turns the Linux version of **Kitten Space Agency** (KSA) into a Mac app for
Apple Silicon. It replaces the game's Linux libraries with macOS ones, including **KSA Metal**, a small Vulkan driver
written for KSA on top of Apple's Metal.

This project is unofficial and not affiliated with RocketWerkz. It contains no game files: you need your own copy of
KSA's Linux version.

**Status:** the game runs with all of its graphics settings available, including terrain tessellation, ground clutter
and ray tracing. Expect rough edges.

Bug reports are welcome as GitHub issues; attach the files in `~/Documents/My Games/Kitten Space Agency/logs`.
Benchmark reports from your Mac are welcome too ([Extras](#extras)).

1. [Quick start](#quick-start): install and update.
2. [How it works](#how-it-works): what the port changes, step by step.
3. [KSA Metal](#ksa-metal): the Vulkan driver.
4. [Extras](#extras): the StarMap mod loader and the benchmark.
5. [Credits and license](#credits-and-license).

## Quick start

Making the app takes one command and about a minute. You need:

- A Mac with Apple Silicon (M1 or later) and **macOS 26** or later.
- The **Linux version of KSA** from [ksa.ahwoo.com](https://ksa.ahwoo.com), version 2026.10.19 or later. It is a
  file like `ksa_linux_v2026.10.19.5638.tar.gz`; leave it in your Downloads folder.

### Install

1. Download the port: the `ksa-macos-port-v….zip` of the
   [latest release](https://github.com/renancamm/ksa-macos-port/releases/latest), then unzip it.
2. In Terminal, go to the unzipped `ksa-macos-port` folder and run `port.sh`:
   ```bash
   cd ~/Downloads/ksa-macos-port
   ./port.sh
   ```
3. Open **Kitten Space Agency** from `~/Applications`.

### Update

To update the game, download its new Linux version into your Downloads folder and run `./port.sh` again. It makes
the app anew from the newer KSA and moves the old one to the Trash. Saves, settings and StarMap stay.

To update the port, run `./port.sh --update`. It replaces this folder with the latest release, then runs as
`./port.sh` does.

### Options and files

Without an archive, `port.sh` takes the newest KSA in Downloads when it is newer than the app's; otherwise it puts the
folder's files into the app you have.

| Option | |
|---|---|
| `<ksa_linux_v….tar.gz>` | make the app from this archive, whatever its version (an extracted folder works too, and is converted in place) |
| `--update` | first update this folder to the latest release of the port |
| `--app <path>` | the app to make or update (default: `~/Applications/Kitten Space Agency.app`) |

Where things are:

| What | Where |
|---|---|
| The app | `~/Applications/Kitten Space Agency.app` |
| Saves, settings, crash dumps and logs | `~/Documents/My Games/Kitten Space Agency` |
| Everything the last run printed, including KSA Metal's messages and startup errors | `.../Kitten Space Agency/logs/console.log` |
| KSA Metal's shader cache | `~/Library/Caches/ksa-metal` |

To uninstall, delete the app, the port folder, the shader cache and `~/Library/Caches/ksa-macos-port`. The Documents
folder holds your saves; delete it too if you don't want them.

## How it works

KSA is a .NET 10 program built on [BRUTAL](https://github.com/ahwootech/BRUTAL), RocketWerkz's framework, and it
renders with Vulkan. Its Linux release holds the game's .NET assemblies and content, plus Linux builds of everything
native: the .NET runtime, BRUTAL's native libraries and FMOD. The .NET code runs on any platform, so the port keeps
the game's files and puts macOS libraries under them:

```text
 KSA's Linux release                              The Mac app
 ┌─────────────────────────────────┐              ┌─────────────────────────────────────┐
 │ The game                        │     kept     │ The game                            │
 │ KSA.dll, the .NET assemblies,   ├─────────────►│ the same files                      │
 │ Content/ with the shaders       │              │ + ARM64 mark: two bytes in KSA.dll  │
 │                                 │              │ + shader fixes: two bugs            │
 └─────────────────────────────────┘              └─────────────────────────────────────┘
 ┌─────────────────────────────────┐              ┌─────────────────────────────────────┐
 │ .NET runtime                    │   swapped    │ .NET runtime                        │
 │ for Linux x64                   ├─────────────►│ Microsoft's build for macOS         │
 └─────────────────────────────────┘              └─────────────────────────────────────┘
 ┌─────────────────────────────────┐              ┌─────────────────────────────────────┐
 │ Native libraries                │   swapped    │ Native libraries                    │
 │ BRUTAL's and FMOD, for Linux    ├─────────────►│ the same versions, built for macOS  │
 │                                 │              │ by RocketWerkz and FMOD             │
 │                                 │              │ + GLFW shim: fixes a few calls      │
 └─────────────────────────────────┘              └─────────────────────────────────────┘
 ┌─────────────────────────────────┐              ┌─────────────────────────────────────┐
 │ Vulkan driver                   │ written here │ KSA Metal                           │
 │ comes with the system           ├─────────────►│ a Vulkan driver made for KSA,       │
 │                                 │              │ built on Apple's Metal              │
 └─────────────────────────────────┘              └─────────────────────────────────────┘

 + a fix made by the port
```

So the app is made of three kinds of pieces:

- **Unchanged:** Microsoft's .NET runtime, RocketWerkz's macOS builds of BRUTAL's libraries and FMOD's macOS library
  are used as their makers publish them, byte for byte.
- **Patched:** the game's own files lose the Linux .NET runtime and the two files that point the .NET host at it
  ([.NET](#net)), then get small changes: two bytes in `KSA.dll`'s header, and bug fixes in its shader sources
  ([Shader fixes](#shader-fixes)).
- **Built by this project:** the GLFW shim ([`src/glfw-shim/`](src/glfw-shim/)), KSA Metal
  ([`src/ksa-metal/`](src/ksa-metal/)), the launcher ([`src/launcher/launcher.sh`](src/launcher/launcher.sh)) and the
  app bundle around everything ([Building the port](#building-the-port)).

### The steps

The port folder holds the app without the game, `payload/app/Contents/`: the launcher, the .NET runtime, the native
libraries, the icon and the licences. `port.sh` converts the game with the scripts in the folder's `lib/` and puts it
into a copy of that app; in this repository they are in [`src/port/lib/`](src/port/lib/).

| Step | Script | What it does |
|---|---|---|
| Convert | `convert-cleanup.sh` | removes the Linux binaries and the Windows installer files |
| | `convert-dotnet.sh` | makes the .NET files run on the macOS runtime (with `mark-arm64.sh`) |
| | `convert-shaders.sh` | fixes bugs in the game's shaders |
| App | `make-app.sh` | builds the app around the converted game, or puts the port's files into one |

To update an app, `port.sh` runs the same steps on it again, without the extraction.

### BRUTAL's native libraries

RocketWerkz publishes BRUTAL as NuGet packages (`Ahwoo.BRUTAL.*`). The packages that wrap native libraries carry
RocketWerkz's builds for Windows, Linux and macOS on Apple Silicon. The port takes the macOS builds of the nine it
needs, unmodified:

| Package | Library | Used for |
|---|---|---|
| Ahwoo.BRUTAL.Stb | `libstb.dylib` | loading and writing images |
| Ahwoo.BRUTAL.Vulkan | `libVulkanEx.dylib` | batched Vulkan helpers |
| Ahwoo.BRUTAL.Vulkan.Vma | `libvma.dylib` | GPU memory (Vulkan Memory Allocator) |
| Ahwoo.BRUTAL.Glfw | `libglfw.3.dylib` | the window and input (RocketWerkz's fork of GLFW 3.5.1) |
| Ahwoo.BRUTAL.Gli | `libgli.dylib` | texture formats |
| Ahwoo.BRUTAL.ImGui | `libimgui.dylib` | the user interface |
| Ahwoo.BRUTAL.ImPlot | `libimplot.dylib` | the profiler's plots |
| Ahwoo.BRUTAL.Ktx | `libKtx.dylib` | KTX2 textures |
| Ahwoo.BRUTAL.ShaderC | `libshaderc_shared.dylib`, `libSPIRV-Tools-shared.dylib` | compiling the game's GLSL shaders at startup |

These are the same versions KSA ships for Linux: the Linux libraries in the game's archive are identical to the
Linux builds in these packages. `convert-dotnet.sh` compares each package version with the matching game assembly
(`Ahwoo.BRUTAL.Glfw` with `Brutal.Glfw.dll`). It stops when the game is older than the packages, because older
bindings don't match the newer libraries, and warns when the game is newer.

GLFW has to be RocketWerkz's fork: `Brutal.Glfw` calls functions that upstream GLFW doesn't have.

### Building the port

`build.sh` makes the port folder from this repository. It needs:

- the Command Line Tools: `xcode-select --install`
- cmake and ninja: `brew install cmake ninja`
- the .NET 10 SDK: `brew install --cask dotnet-sdk`

```bash
git clone https://github.com/renancamm/ksa-macos-port.git
cd ksa-macos-port
./build.sh
```

That makes `dist/ksa-macos-port/`, a port folder like a release's; `./build.sh --install` also puts its files into the
installed app. The native libraries are a CMake project ([`CMakeLists.txt`](CMakeLists.txt)). The benchmark mod is
built against the installed app's KSA, or another one with `--ksa <archive>`, and the port folder is made for that
KSA version. Everything the build downloads is declared in [`cmake/dependencies.cmake`](cmake/dependencies.cmake).

Releases are made with `release.sh <ksa_linux_vX.tar.gz>`. It builds the port folder from a clean copy of the
committed sources, zips it, and tests the zip by making an app with it and running the benchmark on that app.
`release.sh --publish` then tags the commit and uploads the zip as a draft GitHub release.

### .NET

The Linux release is a self-contained `linux-x64` .NET program. `convert-dotnet.sh` turns it into one that runs on
the shared macOS runtime the app bundles:

1. It removes the Linux .NET runtime libraries (`System.*.dll`, `mscorlib.dll`, ...): exactly the DLLs whose names
   are also in Microsoft's macOS runtime. The game's own DLLs and NuGet packages stay.
2. It removes `KSA.deps.json`, which points the .NET host at the `linux-x64` runtime.
3. It replaces `KSA.runtimeconfig.json` with one for the shared .NET 10 runtime.
4. It changes `KSA.dll`'s machine type from AMD64 (`0x8664`) to ARM64 (`0xAA64`). .NET on Apple Silicon refuses
   64-bit assemblies marked for another CPU. `KSA.dll` contains only IL code, so this mark is the only thing in the
   way. The other game DLLs are platform-neutral and load as they are.

The runtime itself (about 85 MB) is Microsoft's. Its `dotnet` host is signed by Microsoft with the entitlements the
launcher needs: DYLD environment variables, unsigned libraries and JIT.

### Shader fixes

KSA compiles its GLSL shaders from `Content/` at startup, so fixes can go straight into the shader sources.
`convert-shaders.sh` applies these, to files under `Content/Core/Shaders/`; its header explains each one:

| Shader | Bug | Fix |
|---|---|---|
| `Planet/GroundClutter/PrefixSum.comp` | its ecotype uniform block leaves out the 16-byte `textureHandles` member of `EcotypeData`, so the shader reads a texture index as the object count and runs past its buffers: clutter pops and changes shape, and the GPU can hang | adds the member |
| `Planet/GroundClutter/Solid.vert`, `SolidDepth.vert` | the depth pre-pass and the colour pass (depth `EQUAL`) need bit-identical positions, which Vulkan only guarantees for `invariant` outputs; with Metal's fast math they differ: black stripes and see-through patches on trees and rocks | declares `invariant gl_Position` |

When a KSA update changes the code around a fix, the script prints `[WARN]` and skips it.

### GLFW shim

The launcher loads `libGlfwShim.dylib` with `DYLD_INSERT_LIBRARIES`. With dyld interposing it replaces a few calls
without modifying GLFW; [`glfw_shim.c`](src/glfw-shim/glfw_shim.c) explains each one:

| Call | Problem | Fix |
|---|---|---|
| `CGDisplayCopyAllDisplayModes` | GLFW asks macOS for display modes without the scaled HiDPI ones, so the resolution list on a MacBook display lacks modes like 1280x800 and 1512x982 | asks for them too |
| `glfwSetWindowMonitor` | in Fullscreen the game always asks for the desktop's size, so the Resolution setting does nothing there | uses the Resolution from `settings.toml`, and sizes the window again when it leaves Fullscreen |
| `glfwSetWindowMonitor`, `glfwPollEvents` | GLFW's macOS backend doesn't report the window's new position after a display mode change, so dropdowns opened in the wrong spot | reports it |
| `glfwSetInputMode` | the game turns raw mouse motion off without checking whether it is supported, and GLFW logs an error after every right-drag | skips those calls |

### The app bundle

`make-app.sh` moves the converted game folder into the app as `Contents/Resources` and adds the port's part from
`payload/app/Contents/`:

| In the app | What |
|---|---|
| `Contents/MacOS/KSA` | the app's executable, [`src/launcher/launcher.sh`](src/launcher/launcher.sh): starts `dotnet KSA.dll` (through StarMap when [StarMap](#mods) is in the app) with `DYLD_LIBRARY_PATH` set to the game folder, so every library comes from the app and never from elsewhere on the Mac, and with the GLFW shim loaded; its output goes to `console.log` in the game's logs folder, or to the terminal when started from one |
| `Contents/MacOS/dotnet`, `host/`, `shared/` | the .NET runtime |
| `Contents/Resources/*.dylib` | the native libraries, next to the game that loads them |
| `Contents/Resources/Licenses/` | every component's licence and notices ([THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)) |
| `Contents/Resources/AppIcon.icns`, `Contents/Info.plist` | the icon; the game's version, the port's version and commit, and `NSHighResolutionCapable` off, so the game renders one pixel per point and its Resolution setting is the number of pixels it renders |

The bundle isn't signed as a whole, because that would replace Microsoft's signature on `dotnet`. `make-app.sh`
removes the quarantine attribute that a browser download puts on the files, so macOS opens the app as one made on
this Mac. When `port.sh` replaces an app, it builds the new one next to it, keeps its window layout (`imgui.ini`) and
StarMap, and moves the old one to the Trash only at the end; if anything fails, the old app is left as it was.

### Repository layout

```text
build.sh, release.sh      make the port folder; make a release of it
CMakeLists.txt, cmake/    the native build, and every external source the build uses (dependencies.cmake)
CMakePresets.json         how CMake is run: Ninja, Release, deps/build/cmake/
scripts/                  the build's other steps, and lib.sh
global.json               the .NET SDK version that builds the benchmark
src/port/                 the port folder's commands and lib/, its steps
src/launcher/             the app's executable
src/glfw-shim/            the GLFW shim
src/ksa-metal/            KSA Metal, with its own README
src/benchmark/            the benchmark: mod, report program, tour and vehicles, with its own README
third_party/fmod/         FMOD's macOS library, with its licence and notice (not MIT)
.github/                  the issue templates
deps/, dist/              made by the build: downloads and build trees; the port folder and release zips
```

## KSA Metal

KSA draws everything through Vulkan, a cross-platform graphics API. macOS doesn't have Vulkan; its GPU API is Metal.
KSA Metal sits between the two: it implements the Vulkan functions KSA calls, and does the work with Metal.

On other platforms, a Vulkan program loads `libvulkan`, the *loader*, which finds and loads the actual driver for
the GPU. KSA Metal takes the place of both: the app ships it as `libvulkan.1.dylib`, so BRUTAL, `libVulkanEx` and
GLFW find their Vulkan functions directly in it. Nothing in the game changes.

KSA Metal implements only the part of Vulkan that KSA uses: about 180 functions and two dozen extensions, one GPU.
That keeps it small (about 9,000 lines of Objective-C++ in [`src/ksa-metal/`](src/ksa-metal/)) and lets each Vulkan
concept map onto Metal in the most direct way that works for this game. It is not a general Vulkan driver, and
other Vulkan programs won't run on it. It needs macOS 26 for the Metal features it uses.

### How it works, in short

Vulkan and Metal are both explicit, low-level GPU APIs with similar ideas, which is what makes a thin translation
possible. In short:

- **Memory:** each block of Vulkan device memory is a Metal heap. Buffers are offsets into it, images are textures
  placed in it.
- **Shaders:** SPIR-V is translated to Metal's shading language with SPIRV-Cross when a pipeline is created, on
  worker threads, and the translations are cached on disk.
- **Descriptors:** each descriptor set is written directly as a Metal argument buffer.
- **Commands:** Metal commands are encoded while the game records, so submitting is cheap.
- **Synchronization:** barriers become Metal fences between groups of commands, sorted by stage, so independent
  work overlaps on the GPU.
- **Tessellation:** the vertex and control stages run as compute kernels, and Metal's tessellator draws the patches
  with the evaluation shader, which runs the vertex shader itself where it can.
- **Ray tracing:** ray queries map onto Metal's. Each ray tracing pipeline is rewritten into one compute kernel.
- **Presentation:** the swapchain's images are copied into the window's drawable.

The detail is in [`src/ksa-metal/README.md`](src/ksa-metal/README.md): its switches, how to build and test a change,
how each Vulkan concept maps onto Metal, and what each source file holds.

## Extras

Two optional tools come in the port folder. Each works on the app `port.sh` made.

### Mods

[StarMap](https://github.com/StarMapLoader/StarMap) is a community mod loader that lets KSA load mods with code. To
add it to the app:

```bash
./starmap.sh
```

The game then starts through StarMap. Put each mod in its own folder in
`~/Documents/My Games/Kitten Space Agency/mods/`; at the next start the game asks whether to enable it. StarMap's
output goes to `console.log` with the game's. `./starmap.sh --remove` takes it out again; `port.sh` keeps it.

StarMap is a separate project: mods made for it may not work on a Mac, and a new KSA version can break it until it
is updated. Its release is a Windows build, but only two of its files are Windows-specific: the port leaves out
`StarMap.exe` (the app runs `StarMap.dll` with its own .NET runtime) and marks `StarMap.dll` as ARM64, as it does for
`KSA.dll`.

### Benchmark

The port's benchmark is a mod that plays a fixed tour of the game in one session (Earth from orbit, flights over its
ground and sea, other worlds, a kitten in the grass, a rocket launch, a 2,000-part rocket blown up on its pad),
measures each part and writes a report to compare with another run, and a zip you can share as an issue if you like:

```bash
./benchmark.sh
```

[`src/benchmark/README.md`](src/benchmark/README.md) explains the tour, its presets and options, how to compare runs,
and the tools it brings for work on the port (repeats, GPU profiles, Instruments traces, Xcode frame captures).

## Credits and license

### Credits

- [Luc8020/ksa-macos-port](https://github.com/Luc8020/ksa-macos-port), the project this one started from.
- RocketWerkz, for Kitten Space Agency and for publishing [BRUTAL](https://github.com/ahwootech/BRUTAL) with macOS
  builds of its libraries.
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross) and [SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools),
  built into KSA Metal, and the projects inside BRUTAL's libraries, among them [GLFW](https://www.glfw.org) and
  [Vulkan Memory Allocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator).
- Audio: FMOD Engine by Firelight Technologies Pty Ltd.

[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) lists every component in the app with its licence.

### License

The scripts and sources here are under the [MIT License](LICENSE), except [`third_party/fmod/`](third_party/fmod/),
which holds FMOD's macOS library under the FMOD End User Licence Agreement
([`NOTICE.txt`](third_party/fmod/NOTICE.txt), [`LICENSE.TXT`](third_party/fmod/LICENSE.TXT)). FMOD is a trademark
of Firelight Technologies Pty Ltd; this project is not affiliated with or endorsed by Firelight Technologies.

Kitten Space Agency is developed and owned by RocketWerkz. This project is not affiliated with or endorsed by
RocketWerkz.
