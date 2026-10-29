# Kitten Space Agency on Apple Silicon Macs

An experimental community project that turns the Linux version of **Kitten Space Agency** (KSA) into a Mac app for
Apple Silicon. It replaces the game's Linux libraries with macOS ones and draws the game with **KSA Metal**: a Vulkan
driver written for KSA on top of Apple's Metal, and a few things around it.

This project is unofficial and not affiliated with RocketWerkz. It contains no game files: you need your own copy of
KSA's Linux version.

**Status:** the game runs with its graphics settings, including terrain tessellation, ground clutter and ray
tracing. Expect rough edges.

Bug reports are welcome as GitHub issues; attach the files in `~/Documents/My Games/Kitten Space Agency/logs`.
Benchmark reports from your Mac are welcome too ([The benchmark](#the-benchmark)).

1. [Quick start](#quick-start): install and update.
2. [How it works](#how-it-works): what the port changes, step by step.
3. [KSA Metal](#ksa-metal): the Vulkan driver that draws the game, the Mac Port tab, the hooks and the benchmark.
4. [Extras](#extras): the StarMap mod loader.
5. [Credits and license](#credits-and-license).

## Quick start

Making the app takes one command and about a minute. You need:

- A Mac with Apple Silicon (M1 or later) and **macOS 26** or later.
- The **Linux version of KSA** from [ksa.ahwoo.com](https://ksa.ahwoo.com), version 2026.10.25 or later. It is a
  file like `ksa_linux_v2026.10.25.5707.tar.gz`; leave it in your Downloads folder.

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

### Where things are

| What | Where |
|---|---|
| The app | `~/Applications/Kitten Space Agency.app` |
| Saves, settings, crash dumps and logs | `~/Documents/My Games/Kitten Space Agency` |
| Everything the last run printed, including KSA Metal's messages and startup errors | `.../Kitten Space Agency/logs/console.log` |
| KSA Metal's shader cache, and the textures the [KTX shim](#the-ktx-shim) decompressed (about 2 GB) | `~/Library/Caches/ksa-metal` |

To uninstall, delete the app, the port folder, the shader cache and `~/Library/Caches/ksa-macos-port` (the
benchmark's fresh games). The Documents folder holds your saves; delete it too if you don't want them.

### Options

Without an archive, `port.sh` takes the newest KSA in Downloads when it is newer than the app's; otherwise it puts the
folder's files into the app you have.

| Option | |
|---|---|
| `<ksa_linux_v….tar.gz>` | make the app from this archive, whatever its version (an extracted folder works too, and is converted in place) |
| `--update` | first update this folder to the latest release of the port |
| `--app <path>` | the app to make or update (default: `~/Applications/Kitten Space Agency.app`) |

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
 │                                 │              │ + shader fixes: three bugs          │
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
- **Built by this project:** the GLFW shim ([`src/glfw-shim/`](src/glfw-shim/)), KSA Metal's driver
  ([`src/ksa-metal/`](src/ksa-metal/)), the launcher ([`src/launcher/`](src/launcher/)) and the
  app bundle around everything ([Building the port](#building-the-port)).

### The steps

The port folder holds the app without the game, `payload/app/Contents/`: the launcher, the .NET runtime, the native
libraries, the icon and the licences. `port.sh` extracts the game's archive, converts the game with the scripts in the
folder's `lib/` and puts it into a copy of that app; in this repository they are in [`src/port/lib/`](src/port/lib/).

| Step | Script | What it does |
|---|---|---|
| Convert | `convert-cleanup.sh` | removes the Linux binaries and the Windows installer files |
| | `convert-dotnet.sh` | makes the .NET files run on the macOS runtime (with `mark-arm64.sh`) |
| | `convert-shaders.sh` | fixes and edits the game's shaders |
| App | `make-app.sh` | builds the app around the converted game, or puts the port's files into one |

To update an app, `port.sh` runs the same steps on it again, without the extraction.

### BRUTAL's native libraries

RocketWerkz publishes BRUTAL as NuGet packages (`Ahwoo.BRUTAL.*`). The native libraries are in packages of their
own (`Ahwoo.BRUTAL.<name>.Native`; Monitor.Host carries its program itself), with RocketWerkz's builds for Windows,
Linux and macOS on Apple Silicon. The port takes the macOS builds of the eleven it needs, unmodified:

| Package | Library | Used for |
|---|---|---|
| Ahwoo.BRUTAL.Stb.Native | `libstb.dylib` | loading and writing images |
| Ahwoo.BRUTAL.Vulkan.Native | `libVulkanEx.dylib` | batched Vulkan helpers |
| Ahwoo.BRUTAL.Vulkan.Vma.Native | `libvma.dylib` | GPU memory (Vulkan Memory Allocator) |
| Ahwoo.BRUTAL.Glfw.Native | `libglfw.3.dylib` | the window and input (GLFW 3.5.1 fork) |
| Ahwoo.BRUTAL.Gli.Native | `libgli.dylib` | texture formats |
| Ahwoo.BRUTAL.ImGui.Native | `libimgui.dylib` | the user interface |
| Ahwoo.BRUTAL.ImPlot.Native | `libimplot.dylib` | the profiler's plots |
| Ahwoo.BRUTAL.Ktx.Native | `libKtx.dylib` | KTX2 textures |
| Ahwoo.BRUTAL.ShaderC.Native | `libshaderc_shared.dylib`, `libSPIRV-Tools-shared.dylib` | compiling GLSL shaders at startup |
| Ahwoo.BRUTAL.RakNet.Native | `libRakNetDLL.dylib` | networking (RakNet) |
| Ahwoo.BRUTAL.Monitor.Host | `Brutal.Monitor.Subprocess`, a program | crash dumps |

These are the same versions KSA ships for Linux: the Linux libraries in the game's archive are identical to the
Linux builds in these packages. `convert-dotnet.sh` compares each package version with the matching game assembly
(`Ahwoo.BRUTAL.Glfw.Native` with `Brutal.Glfw.dll`). It stops when the game is older than the packages, because older
bindings don't match the newer libraries, and warns when the game is newer.

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
installed app. The native libraries are a CMake project ([`CMakeLists.txt`](CMakeLists.txt)). The benchmark and the hooks are
built against the installed app's KSA, or another one with `--ksa <archive>`, and the port folder is made for that
KSA version. Everything the build downloads is declared in [`cmake/dependencies.cmake`](cmake/dependencies.cmake).

Warnings are errors in the native libraries, the hooks and the benchmark, and the build runs the hooks' and the
benchmark's tests. The scripts run on macOS's own bash 3.2 and are kept clean of ShellCheck's findings
([`.shellcheckrc`](.shellcheckrc)).

Releases are made with `release.sh <ksa_linux_vX.tar.gz>`. It builds the port folder from a clean copy of the
committed sources, zips it, and tests the zip by making an app with it and running the benchmark on that app with a
fresh install's settings. `release.sh --publish` then tags the commit and uploads the zip as a draft GitHub release.

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
`convert-shaders.sh` applies these, to files under `Content/Core/Shaders/`; its header explains each one. It also
edits shaders for the hooks and the driver, which their READMEs explain.

| Shader | Bug | Fix |
|---|---|---|
| `Planet/GroundClutter/PrefixSum.comp` | its ecotype uniform block leaves out the 16-byte `textureHandles` member of `EcotypeData`, so the shader reads a texture index as the object count and runs past its buffers: clutter pops and changes shape, and the GPU can hang | adds the member |
| `Planet/GroundClutter/Solid.vert`, `SolidDepth.vert` | the depth pre-pass and the colour pass (depth `EQUAL`) need bit-identical positions, which Vulkan only guarantees for `invariant` outputs; with Metal's fast math they differ: black stripes and see-through patches on trees and rocks | declares `invariant gl_Position` |
| `Lighting/LightFineClusterCompact.comp` | a tile whose depth range comes out inverted makes a loop run about 4 billion times: the GPU hangs | skips such a tile |

When a KSA update changes the code around a fix, the script prints `[WARN]` and skips it.

### GLFW shim

The launcher loads `libGlfwShim.dylib` with `DYLD_INSERT_LIBRARIES`. With dyld interposing it replaces a few calls
without modifying GLFW; [`glfw_shim.c`](src/glfw-shim/glfw_shim.c) explains each one:

| Call | Problem | Fix |
|---|---|---|
| `CGDisplayCopyAllDisplayModes` | GLFW asks macOS for display modes without the scaled HiDPI ones, so the resolution list on a MacBook display lacks sizes like 1352x878 and 1147x745 | asks for them, and keeps those of the desktop's kind and shape |
| `glfwSetWindowMonitor` | in Fullscreen the game always asks for the desktop's size, so the Resolution setting does nothing there | uses the Resolution from `settings.toml`, and sizes the window again when it leaves Fullscreen |
| `glfwSetWindowMonitor`, `glfwPollEvents` | GLFW's macOS backend doesn't report the window's new position after a display mode change, so dropdowns open in the wrong spot | reports it |
| `glfwSetInputMode` | the game turns raw mouse motion off without checking whether it is supported, and GLFW logs an error after every right-drag | skips those calls |

### The app bundle

`make-app.sh` moves the converted game folder into the app as `Contents/Resources` and adds the port's part from
`payload/app/Contents/`:

| In the app | What |
|---|---|
| `Contents/MacOS/KSA` | the launcher, [`launcher.c`](src/launcher/launcher.c) |
| `Contents/MacOS/dotnet`, `host/`, `shared/` | the .NET runtime |
| `Contents/Resources/*.dylib` | the native libraries, next to the game that loads them |
| `Contents/Resources/Hooks/` | [KSA Metal Hooks](#the-hooks), with Harmony |
| `Contents/Resources/Benchmark/` | the [benchmark](#the-benchmark), with its tour and vehicles |
| `Contents/Resources/port-settings.txt`, `port-settings.toml` | the [port settings](#the-mac-port-tab)' table and the file's template |
| `Contents/Resources/Licenses/` | every component's licence and notices ([THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)) |
| `Contents/Resources/AppIcon.icns`, `Contents/Info.plist` | the icon; the game's and the port's versions |

The launcher starts `dotnet KSA.dll`, through StarMap when [StarMap](#mods) is in the app, with the GLFW shim loaded and
`DYLD_LIBRARY_PATH` set to the game folder, so every library comes from the app and never from elsewhere on the Mac. The
launcher is the app's own executable and hands its process over to `dotnet`, so macOS treats the game as the app, Game
Mode included. `Info.plist` turns `NSHighResolutionCapable` on, so the game draws its interface at the display's full
resolution.

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
global.json               the .NET SDK version that builds the benchmark and the hooks
src/port/                 the port folder's commands and lib/, its steps
src/launcher/             the app's executable
src/glfw-shim/            the GLFW shim
src/ktx-shim/             the KTX shim
src/hooks/                KSA Metal Hooks and their loader, with their own README
src/ksa-metal/            the driver, with its own README
src/benchmark/            the benchmark: director, report program, tour and vehicles, with its own README
third_party/fmod/         FMOD's macOS library, with its licence and notice (not MIT)
.github/                  the issue templates
deps/, dist/              made by the build: downloads and build trees; the port folder and release zips
```

## KSA Metal

KSA Metal is how the port draws the game, in three parts: the **driver**, a Vulkan driver written for KSA on top of
Apple's Metal; the **hooks**, patches to the game's rendering code that make drawing cheaper; and the **benchmark**,
which measures how the game runs on a Mac. Beside them, the **KTX shim** makes the game's textures take less memory.
The **Mac Port** tab in the game's settings sets them all. The game runs with the driver alone; the rest can be turned
off, and the defaults keep the game's image as it is.

### The driver

KSA draws everything through Vulkan, a cross-platform graphics API. macOS doesn't have Vulkan; its GPU API is Metal.
The driver sits between the two: it implements the Vulkan functions KSA calls, and does the work with Metal.

The app ships it as `libvulkan.1.dylib`, in the place of the Vulkan loader and a driver, so nothing in the game
changes. It implements only the part of Vulkan that KSA uses, about 180 functions for one GPU, which keeps it small
(about 9,000 lines of Objective-C++) and specific: other Vulkan programs won't run on it. It needs macOS 26 for the
Metal features it uses.

Vulkan and Metal are both explicit, low-level GPU APIs with similar ideas, so each Vulkan concept maps onto Metal
in a direct way. [`src/ksa-metal/README.md`](src/ksa-metal/README.md) has that mapping, the driver's switches, how
to build and test a change, and what each source file holds.

### The Mac Port tab

The port's own settings are in the game's settings (Esc), in the **Mac Port** tab. It works like the game's other
tabs; each row's tooltip says what it changes and what it costs.

| Section | What's there |
|---|---|
| Presets | Performance, Balanced and Quality: the game's graphics settings and the port's, chosen together for this Mac; Game Defaults sets them back |
| Image | Apple's MetalFX and what needs it, off by default: upscaling, render scale, MetalFX's anti-aliasing in place of the game's |
| Interface | the flight HUD fitted to the display, or the game's own layout |
| Extra Performance | a visible trade for speed, with the game's own as the default: cloud shadow updates, plume shadow quality, simpler PCSS shadows, a baked Milky Way |
| Optimizations | the same image, faster, on by default |
| GPU Features | what the Mac's GPU and display offer the game, with the game's own settings still deciding: tessellation, ray queries, ray tracing, HDR output |
| Measure | Apple's Metal Performance HUD over the game, and the [benchmark](#the-benchmark) |

The settings are kept in `port-settings.toml`, next to the game's `settings.toml` in
`~/Documents/My Games/Kitten Space Agency`, with a comment on each. The file can be edited instead of the tab, and
its `[developer]` section holds switches for testing and debugging that the tab doesn't show.

### The hooks

Some of the port's changes need the game's C# code. KSA Metal Hooks make them in memory when the game starts, with
[Harmony](https://github.com/pardeike/Harmony), so `KSA.dll` stays as it is on disk. Their patches are the tab's
rows that reach into the game; the hooks also draw the tab itself, start the benchmark from it, and make the game's
Restart button start the app again. When a KSA update has moved the code a hook changes, the hook is left out, the
tab shows its row as unavailable, and the game runs as it would without it. [`src/hooks/`](src/hooks/) says what
each one changes and how.

### The KTX shim

The game keeps every texture's pixels in memory for the whole session. The KTX shim maps them from files instead once
a texture is loaded, so macOS can page them out when memory runs short and read them back later. It is the tab's
**Texture File Mapping** row; [`ktx_shim.c`](src/ktx-shim/ktx_shim.c) has the details.

### The benchmark

The benchmark plays a fixed tour of the game in one session, from Earth's orbit to other worlds, a kitten in the
grass, a rocket launch and a 2,000-part rocket blown up on its pad. It measures each part and writes a report to
compare with another run, and a zip you can share as an issue if you like. It is in the app: run it from the Mac
Port tab (**RUN BENCHMARK**), or from the port folder:

```bash
./benchmark.sh
```

[`src/benchmark/README.md`](src/benchmark/README.md) explains the tour, its presets and options, how to compare
runs, and the tools it brings for work on the port.

## Extras

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
is updated.

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
