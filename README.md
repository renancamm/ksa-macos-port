# Kitten Space Agency on Apple Silicon Macs

An experimental community project that turns the Linux version of **Kitten Space Agency** (KSA) into a Mac app for
Apple Silicon. It replaces the game's Linux libraries with macOS ones, including **KSA Metal**, a small Vulkan driver
written for KSA on top of Apple's Metal.

This project is unofficial and not affiliated with RocketWerkz. It contains no game files: you need your own copy of
KSA's Linux version.

**Status:** the game runs with all of its graphics settings available, including terrain tessellation, ground clutter
and ray tracing. Expect rough edges. Known issues:

- Anti-aliasing and V-Sync currently cost a lot of performance. Turn them off if the game runs slowly.

Bug reports are welcome as GitHub issues; attach the files in `~/Documents/My Games/Kitten Space Agency/logs`.
Benchmark reports from your Mac are welcome too ([Extras](#extras)).

1. [Quick start](#quick-start): install and update.
2. [How it works](#how-it-works): what the port changes, step by step.
3. [KSA Metal](#ksa-metal): the Vulkan driver and its switches.
4. [Extras](#extras): the StarMap mod loader and the benchmark.
5. [Credits and license](#credits-and-license).

## Quick start

Making the app takes one command and about a minute. You need:

- A Mac with Apple Silicon (M1 or later) and **macOS 26** or later.
- The **Linux version of KSA** from [ksa.ahwoo.com](https://ksa.ahwoo.com), version 2026.10.19 or later. It is a
  file like `ksa_linux_v2026.10.19.5638.tar.gz`; leave it in your Downloads folder.

### Install

1. Download this project: on its GitHub page, **Code** > **Download ZIP**, then unzip it. (With git:
   `git clone https://github.com/renancamm/ksa-macos-port.git`.)
2. In Terminal, go to the project's folder (`ksa-macos-port-main` for the ZIP) and run `port.sh`:
   ```bash
   cd ~/Downloads/ksa-macos-port-main
   ./port.sh
   ```
3. Open **Kitten Space Agency** from `~/Applications`.

`port.sh` needs an internet connection ([what it downloads](#downloads)). The first launch of the game takes longer
than later ones, while Metal compiles the game's shaders.

### Update

When KSA tells you about a new version, download its Linux version into your Downloads folder and run this from
the project's folder:

```bash
./update.sh
```

It makes the app again from the newer KSA and moves the old app to the Trash. Saves and settings are not touched.

To get this project's latest fixes, download the ZIP again and run `./update.sh` from the new folder (in a git
clone, `./update.sh --pull` updates the folder first). When there is no newer KSA, it applies the folder's version
of the port to the app you have. `update.sh` doesn't look online for new KSA versions; the game does that.

### Options and files

`port.sh` always makes the app anew; `update.sh` updates the one you have. Both take these options:

| Option | `port.sh` | `update.sh` |
|---|---|---|
| `<ksa_linux_v….tar.gz>` | convert this archive instead of the newest one in Downloads (an extracted folder works too, and is converted in place) | convert this archive, whatever its version |
| `--app <path>` | make the app at this path instead of `~/Applications/Kitten Space Agency.app` | update the app at this path |
| `--from-source` | build this project's own libraries on your Mac instead of downloading them ([details](#building-the-libraries-yourself)) | the same |
| `--pull` | | run `git pull` first (a git clone, with the Command Line Tools) |

Where things are:

| What | Where |
|---|---|
| The app | `~/Applications/Kitten Space Agency.app` |
| Saves, settings, crash dumps and logs | `~/Documents/My Games/Kitten Space Agency` |
| Everything the last run printed, including KSA Metal's messages and startup errors | `.../Kitten Space Agency/logs/console.log` |
| KSA Metal's shader cache | `~/Library/Caches/ksa-metal` |

To uninstall, delete the app, the shader cache and this project's folder. The Documents folder holds your saves;
delete it too if you don't want them.

## How it works

KSA is a .NET 10 program built on [BRUTAL](https://github.com/ahwootech/BRUTAL), RocketWerkz's framework, and it
renders with Vulkan. Its Linux release holds the game's .NET assemblies and content, plus Linux builds of everything
native: the .NET runtime, BRUTAL's native libraries and FMOD. The .NET code runs on any platform, so the port keeps
the game's files and puts macOS libraries under them:

```
KSA.dll, the other game assemblies, Content/      from your KSA archive         Linux runtime removed, three small fixes
  |-- .NET runtime                                Microsoft's macOS build       unchanged
  |-- BRUTAL native libraries (ImGui, KTX, VMA...) RocketWerkz's macOS builds    unchanged
  |-- GLFW (window, input)                        RocketWerkz's macOS build     unchanged, plus a shim built by this project
  |-- FMOD (audio)                                FMOD's macOS library          unchanged
  `-- Vulkan                                      KSA Metal                     built by this project
        `-- Metal                                 macOS
```

So the app is made of three kinds of pieces:

- **Unchanged:** Microsoft's .NET runtime, RocketWerkz's macOS builds of BRUTAL's libraries and FMOD's macOS library
  are used as their makers publish them, byte for byte.
- **Patched:** the game's own files lose the Linux .NET runtime and the two files that point the .NET host at it
  ([.NET](#net)), then get three small changes: two bytes in `KSA.dll`'s header, and two bug fixes in its shader
  sources.
- **Built by this project:** the GLFW shim ([`src/glfw-shim/`](src/glfw-shim/)) and KSA Metal
  ([`src/ksa-metal/`](src/ksa-metal/)), compiled into two libraries that `port.sh` usually downloads ready-built
  ([details](#building-the-libraries-yourself)). The launcher ([`app/launcher.sh`](app/launcher.sh)) and the app
  bundle around everything are this project's too.

### The steps

`port.sh` runs these scripts from `scripts/`, in this order. Each one does one thing and can be read on its own. They
share [`scripts/lib.sh`](scripts/lib.sh), and every external source is pinned to a version and checksum in
[`scripts/pins.sh`](scripts/pins.sh).

| Step | Script | What it does |
|---|---|---|
| Native libraries | `fetch-brutal-natives.sh` | RocketWerkz's macOS builds of BRUTAL's libraries, from nuget.org into `dist/` |
| | `fetch-prebuilt.sh` | the GLFW shim and KSA Metal, ready-built, from a GitHub release that matches the sources; otherwise: |
| | `build-glfw-shim.sh` | `src/glfw-shim/` -> `dist/libGlfwShim.dylib` |
| | `build-spirv-cross.sh`, `build-spirv-tools.sh` | static libraries for KSA Metal |
| | `build-ksa-metal.sh` | `src/ksa-metal/` -> `dist/libvulkan.1.dylib` |
| | `check-natives.sh` | checks `dist/`: arm64, macOS 26, signatures, dependencies, licences |
| .NET | `fetch-dotnet.sh` | Microsoft's .NET runtime for macOS, SHA-512 checked |
| Convert | `convert-cleanup.sh` | removes the Linux binaries and the Windows installer files |
| | `convert-dotnet.sh` | makes the .NET files run on the macOS runtime (with `mark-arm64.sh`) |
| | `convert-shaders.sh` | fixes two bugs in the ground clutter shaders |
| App | `make-app.sh` | builds the app around the converted game, or refreshes one |

`update.sh` runs either `port.sh` with a newer archive, or the same steps without the extraction on the installed
app. Every conversion step can safely run again on a converted game.

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

GLFW has to be RocketWerkz's fork: it adds trackpad callbacks that `Brutal.Glfw` sets on every window, so upstream
GLFW stops the game at startup.

### Building the libraries yourself

The GLFW shim and KSA Metal are the two pieces of the app compiled from this repository. Compiling them needs the
Command Line Tools and cmake, which most Macs don't have, so `port.sh` and `update.sh` download them ready-built
and need nothing beyond macOS. To build them on your Mac instead, install the Command Line Tools
(`xcode-select --install`) and cmake (`brew install cmake`), then run:

```bash
./update.sh --from-source
```

This builds SPIRV-Cross and SPIRV-Tools (static libraries for KSA Metal, built once), the shim and KSA Metal,
checks the result and puts it into the installed app. Compiled objects are kept in `deps/`, so after a change only
the changed files are compiled again.

The ready-built libraries come from the [Prebuilt natives](.github/workflows/prebuilt.yml) workflow, which runs
`package-prebuilt.sh` on GitHub's macOS runners whenever their sources change and publishes the result as a GitHub
release. The release is
named after a *stamp*: a SHA-256 over every file the libraries are built from. `fetch-prebuilt.sh` computes the
stamp of your local files and downloads the release with that name, so you only ever get libraries built from
exactly the sources you have. When there is no such release (a branch, local changes), the libraries are built on
your Mac, which then needs the tools above. The download is checked against its SHA-256 and carries a build
provenance attestation (`gh attestation verify <file> --repo renancamm/ksa-macos-port`).

Everything compiled from this repository targets macOS 26 explicitly (`MACOS_MIN` in `lib.sh`) and is ad-hoc signed
after linking, because macOS kills a process that loads a library with an invalid signature. `dist/` holds the
libraries for the app and belongs to the build: when the stamp changes, it is emptied and filled again.

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

The runtime itself (`fetch-dotnet.sh`, about 85 MB) is Microsoft's, checked against the SHA-512 Microsoft
publishes. Its `dotnet` host is signed by Microsoft with the entitlements the launcher needs: DYLD environment
variables, unsigned libraries and JIT.

### Shader fixes

KSA compiles its GLSL shaders from `Content/` at startup, so fixes can go straight into the shader sources.
`convert-shaders.sh` fixes two bugs in the ground clutter shaders (`Content/Core/Shaders/Planet/GroundClutter/`):

1. **`PrefixSum.comp` declares the wrong uniform layout.** Its ecotype block leaves out the 16-byte
   `TextureHandles textureHandles` member that comes before `numObjects` in the real `EcotypeData` layout. The shader
   then reads a texture index as the object count and accesses memory past its buffers: clutter pops and changes
   shape, and the GPU can hang. The fix adds the missing member.
2. **`Solid.vert` and `SolidDepth.vert` can compute different depths.** Clutter is drawn twice: a depth pre-pass,
   then a colour pass that only draws where the depth is `EQUAL`. That needs bit-identical positions, which Vulkan
   only guarantees for `invariant` outputs. With Metal's fast math the two differ slightly, which shows as black
   stripes and see-through patches on trees and rocks. The fix declares `invariant gl_Position;` in both shaders.

The script keeps the files' CRLF line endings, skips a file it already fixed, and prints `[WARN]` instead of failing
when a KSA update changed the code around a fix.

### GLFW shim

The launcher loads `libGlfwShim.dylib` with `DYLD_INSERT_LIBRARIES`. With dyld interposing it replaces a few calls
without modifying GLFW ([`src/glfw-shim/`](src/glfw-shim/) explains each one):

- **Resolution list:** it asks macOS for the scaled HiDPI display modes too, so the list offers modes like 1280x800
  and 1512x982 on a MacBook display.
- **Fullscreen resolution:** in Fullscreen the game always asks for the desktop's size. The shim uses the
  Resolution from `settings.toml` instead, and sizes the window again when it leaves Fullscreen.
- **Window position:** GLFW's macOS backend doesn't report the window's new position after a display mode change,
  so dropdowns opened in the wrong spot. The shim reports it.
- **Raw mouse motion:** the game turns raw mouse motion off without checking whether it is supported, and GLFW logs
  an error after every right-drag. The shim skips those calls.

### The app

`make-app.sh` moves the converted game folder into the app as `Contents/Resources` and adds:

- `dist/*.dylib` and `libfmod.dylib` next to the game, and the .NET runtime in `Contents/MacOS`.
- `Contents/MacOS/KSA`, the app's executable: [`app/launcher.sh`](app/launcher.sh). It sets `DYLD_LIBRARY_PATH` to
  the game folder, so every library comes from the app and never from elsewhere on the Mac, loads the GLFW shim,
  and starts `dotnet KSA.dll` (through StarMap when the [StarMap extra](#extras) is in the app). Its output goes to
  `console.log` in the game's logs folder, or to the terminal when started from one.
- `Contents/Resources/Licenses/`: every component's licence and notices ([THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)).
- An icon: `app/AppIcon.icns` if you add one, otherwise the kitten astronaut from KSA's title card, downloaded and
  cropped at build time (RocketWerkz's artwork is never part of this repository).
- `Info.plist`, with the game's version, the port's commit and natives stamp, and `NSHighResolutionCapable` off. The
  game then renders one pixel per point, so its Resolution setting is the number of pixels it renders.

The bundle isn't signed as a whole, because that would replace Microsoft's signature on `dotnet`. An app made on
the Mac it runs on has no quarantine attribute, so macOS opens it as it is. When `port.sh` replaces an app, it builds
the new one next to it, keeps its icon and window layout (`imgui.ini`), and moves the old one to the Trash only at
the end; if anything fails, the old app is left as it was.

### Downloads

The scripts contact only these hosts, and only when a file isn't downloaded already:

| Host | What | When |
|---|---|---|
| github.com | this project's prebuilt libraries; pinned sources (GLFW headers, SPIRV-Cross, SPIRV-Tools, SPIRV-Headers, Vulkan-Headers); `git pull` | looking for prebuilt libraries; building from source; `update.sh --pull` |
| api.nuget.org | RocketWerkz's BRUTAL packages | first run, and when their pins change |
| builds.dotnet.microsoft.com | the .NET runtime | first run, and when its pin changes |
| ahwoo.com | KSA's title card, for the app icon | the first app you make |

Every download is checked: against the checksum or commit pinned in `scripts/pins.sh`, or, for the prebuilt
libraries, against the SHA-256 published with them and the stamp of your sources. The [extras](#extras) also
download StarMap's release from github.com and Microsoft's .NET SDK from builds.dotnet.microsoft.com, each pinned
and checked in its own script.

### Repository layout

```text
port.sh, update.sh        the two commands
scripts/                  the steps, lib.sh and pins.sh; package-prebuilt.sh for the workflow
app/launcher.sh           the app's executable
src/glfw-shim/            the GLFW shim
src/ksa-metal/            KSA Metal, with its own README
extras/                   optional tools, each with its own README: the StarMap mod loader and the benchmark
third_party/fmod/         FMOD's macOS library, with its licence and notice (not MIT)
.github/                  the prebuilt libraries workflow and the issue templates
dist/, deps/              made by the build: the libraries for the app; downloads, sources and build trees
```

## KSA Metal

KSA draws everything through Vulkan, a cross-platform graphics API. macOS doesn't have Vulkan; its GPU API is Metal.
KSA Metal sits between the two: it implements the Vulkan functions KSA calls, and does the work with Metal.

On other platforms, a Vulkan program loads `libvulkan`, the *loader*, which finds and loads the actual driver for
the GPU. KSA Metal takes the place of both: the app ships it as `libvulkan.1.dylib`, so BRUTAL, `libVulkanEx` and
GLFW find their Vulkan functions directly in it. Nothing in the game changes.

KSA Metal implements only the part of Vulkan that KSA uses: about 180 functions and two dozen extensions, one GPU.
That keeps it small (about 8,000 lines of Objective-C++ in [`src/ksa-metal/`](src/ksa-metal/)) and lets each Vulkan
concept map onto Metal in the most direct way that works for this game. It is not a general Vulkan driver, and
other Vulkan programs won't run on it. It needs macOS 26 for the Metal features it uses.

[`src/ksa-metal/README.md`](src/ksa-metal/README.md) has the rest: how it maps Vulkan onto Metal in detail, its
source files, and how to build and test a change.

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
  with the evaluation shader.
- **Ray tracing:** ray queries map onto Metal's. Each ray tracing pipeline is rewritten into one compute kernel.
- **Presentation:** the swapchain's images are copied into the window's drawable.

### Configuration

KSA Metal reads a few environment variables. To use one, start the app's executable from Terminal, where its output
stays visible:

```bash
KSA_MTL_TESSELLATION=0 ~/Applications/"Kitten Space Agency.app"/Contents/MacOS/KSA
```

Each of these turns one feature off or changes a default, to work around a problem or to find out what causes it:

| Variable | Effect |
|---|---|
| `KSA_MTL_TESSELLATION=0` | hide tessellation from the game; it draws terrain and ocean without it |
| `KSA_MTL_RAYQUERY=0` | hide ray queries and ray tracing; the game places clutter without them, and IVA ray tracing is unavailable |
| `KSA_MTL_RTPIPELINE=0` | hide only ray tracing pipelines (IVA ray tracing) |
| `KSA_MTL_ASYNC_PIPELINES=0` | compile pipelines on the game's thread, when the game creates them |
| `KSA_MTL_CACHE=0` | don't use the shader cache on disk |
| `KSA_MTL_SYNC=strict` | run all GPU work strictly in order; slower, but rules out synchronization bugs |
| `KSA_MTL_SAFE_MATH=1` | compile shaders without fast math; rules out precision problems |
| `KSA_MTL_BUDGET_MB=<n>` | the GPU memory budget reported to the game, which sizes its texture streaming from it (default: what Metal recommends for this Mac) |

`MTL_HUD_ENABLED=1` shows Apple's performance overlay. The variables for debugging KSA Metal itself (logs,
screenshots, shader dumps, profiles) are listed in [`src/ksa-metal/README.md`](src/ksa-metal/README.md#test).

## Extras

Optional tools in [`extras/`](extras/), kept apart from the port itself: `port.sh` and `update.sh` never need them.
Each works on the app `port.sh` made.

- **[StarMap mod loader](extras/starmap/README.md):** adds [StarMap](https://github.com/StarMapLoader/StarMap), a
  community mod loader, to the app, so the game loads mods with their own code:
  ```bash
  ./extras/starmap/port-starmap.sh
  ```
- **[Benchmark](extras/benchmark/README.md):** plays a fixed set of scenes (a launch, a 2,000-part rocket, a flight
  over Earth and other worlds, a dip into Jupiter's clouds, an explosion, the sea, a cockpit with ray tracing, a
  kitten's fur), measures each one and writes a report to compare with another run or to share as an issue:
  ```bash
  ./extras/benchmark/benchmark.sh
  ```

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
