<h1 align="center">Kitten Space Agency on Apple Silicon Macs</h1>

<p align="center">
  <a href="https://github.com/renancamm/ksa-macos-port/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/renancamm/ksa-macos-port?label=release"></a>
  <a href="#quick-start"><img alt="macOS 26+ on Apple Silicon" src="https://img.shields.io/badge/macOS_26+-Apple_Silicon-000000?logo=apple"></a>
  <a href="https://github.com/renancamm/ksa-macos-port/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/renancamm/ksa-macos-port/total"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/renancamm/ksa-macos-port"></a>
</p>

<p align="center">
  <img alt="Kitten Space Agency on a Mac: the game's rocket in orbit over Earth, with Apple's Metal Performance HUD" src=".github/images/game.png" width="880">
</p>
<p align="center"><sub>MacBook Pro with Apple M5, KSA 2026.11.10.5801, the Quality preset with MetalFX Temporal and Steady Frame Rate in a 1280x800 window</sub></p> <!-- the caption: scripts/readme-screenshots.sh writes it with the pictures -->

An experimental community project that turns the Linux version of **Kitten Space Agency** (KSA) into a native Mac
app for Apple Silicon. It draws the game with **KSA Metal**: a Vulkan driver written for KSA on top of Apple's Metal,
and a few things around it.

This project is unofficial and not affiliated with RocketWerkz. It contains no game files: you need your own copy of
KSA's Linux version.

**Status:** the game runs with its graphics settings, including terrain tessellation, ground clutter and ray
tracing. Expect rough edges.

Bug reports are welcome as GitHub issues; attach the files in `~/Documents/My Games/Kitten Space Agency/logs`.
Benchmark reports from your Mac are welcome too ([The benchmark](#the-benchmark)).

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
the app anew from the newer KSA and moves the old one to the Trash. Saves and settings stay.

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
 │                                 │              │ + shader fixes: a few bugs          │
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

- **Unchanged:** Microsoft's .NET runtime, RocketWerkz's macOS builds of BRUTAL's libraries and FMOD's macOS library,
  as their makers publish them, byte for byte.
- **Patched:** the game's own files. They run on the macOS .NET runtime instead of the Linux one they come with, and
  get two small changes: two bytes in `KSA.dll`'s header that mark it for Apple Silicon, and bug fixes in its shaders.
- **Built by this project:** the GLFW and KTX shims, KSA Metal, the launcher, and the app around everything.

### Making the app

`port.sh` makes the app on your Mac. The port folder holds the app without the game, and the port's program extracts
the game's archive, converts it and puts it into a copy of that app. An update runs the same steps on the app you
have. [`src/player/port-cli/`](src/player/port-cli/) has the steps, the conversion and what goes where in the app.

### The fixes

A few bugs in the game's shaders hang the GPU or draw wrongly on a Mac. KSA compiles its shaders from source at
startup, so the port's program fixes them in the sources; when a KSA update changes the code around a fix, it prints
`[WARN]` and skips it. [`src/game/changes/`](src/game/changes/) lists them, with the fixes KSA Metal Hooks make in the
game's code.

The GLFW shim fixes a few of GLFW's calls on macOS that the game's window and display settings need, without
modifying GLFW ([`src/game/shims/glfw/`](src/game/shims/glfw/)).

### The launcher

The app's executable is the launcher. It starts the game with the port's shims loaded and every library taken from
the app, never from elsewhere on the Mac, and it runs .NET in its own process, so macOS treats the game as the app,
Game Mode included. It starts `KSA.dll`, or a [mod loader](#mods) given to it that loads the game
([`src/game/launcher/`](src/game/launcher/)).

## KSA Metal

KSA Metal is how the port draws the game, in three parts: the **driver**, a Vulkan driver written for KSA on top of
Apple's Metal; the **hooks**, patches to the game's rendering code that make drawing cheaper; and the **benchmark**,
which measures how the game runs on a Mac. Beside them, the **KTX shim** makes the game's textures take less memory.
The **Mac Port** tab in the game's settings sets them all. The game runs with the driver alone; the rest can be turned
off, and the defaults keep the game's image as it is.

### The driver

KSA draws everything through Vulkan, a cross-platform graphics API. macOS doesn't have Vulkan; its GPU API is Metal.
The driver sits between the two: it implements the Vulkan functions KSA calls, and does the work with Metal. The app
ships it as `libvulkan.1.dylib`, in the place of the Vulkan loader and a driver, so nothing in the game changes.

It implements only the part of Vulkan that KSA uses, about 180 functions for one GPU, which keeps it small (about
11,000 lines of Objective-C++) and specific: other Vulkan programs won't run on it. Vulkan and Metal are both
explicit, low-level GPU APIs with similar ideas, so each Vulkan concept maps onto Metal in a direct way;
[`src/game/driver/`](src/game/driver/) has that mapping, the driver's switches, and how to build and test a change.

### The Mac Port tab

The port's own settings are in the game's settings (Esc), in the **Mac Port** tab: graphics presets, Apple's MetalFX
upscaling, faster settings with small trade-offs, memory savings fitted to your Mac, and the benchmark. It works like
the game's other tabs, and each row's tooltip says what it changes and what it costs. The settings are kept in
`port-settings.toml`, next to the game's `settings.toml`, with a comment on each; the file can be edited instead of
the tab, and holds three sections for testing and debugging that the tab doesn't show
([`src/shared/settings/`](src/shared/settings/)).

<p align="center">
  <img alt="The Mac Port tab in the game's settings" src=".github/images/mac-port-tab.png" width="880">
</p>

### The hooks

Some of the port's changes need the game's C# code. KSA Metal Hooks make them in memory when the game starts, with
[Harmony](https://github.com/pardeike/Harmony), so `KSA.dll` stays as it is on disk. Their patches are the tab's rows
that reach into the game, and the hooks draw the tab itself. When a KSA update has moved the code a hook changes, the
hook is left out, the tab shows its row as unavailable, and the game runs as it would without it
([`src/game/hooks/`](src/game/hooks/)).

### The KTX shim

The game keeps every texture's pixels in memory for the whole session. The KTX shim maps them from files instead once
a texture is loaded, so macOS can page them out when memory runs short and read them back later. It is the tab's
**Textures Kept on Disk** row ([`src/game/shims/ktx/`](src/game/shims/ktx/)).

### The benchmark

The benchmark measures how the game runs on your Mac with three tests, each starting in a new game's world: the
Mixed Test (rockets, smoke, an explosion, a kitten and a cockpit), the Planetary Tour (a flight from Earth to Jupiter)
and the Warp Test (how far time warp gets in a fixed time). It writes a report you can compare with another run or
share. Run it from the Mac Port tab (**RUN** on a test's row), or from the port folder:

```bash
./benchmark.sh
```

[`src/game/benchmark/`](src/game/benchmark/) explains the tests, how to compare runs, and the tools it brings
for work on the port.

## Mods

[Borea](https://github.com/KSAModding/Borea), the community's mod manager, installs the
[StarMap](https://github.com/StarMapLoader/StarMap) mod loader and mods, keeps them in instances, and starts the game
with them. Version 0.6.16 or newer finds the app in `/Applications` and `~/Applications`; for an app elsewhere, set
Borea's game folder to the app's `Contents/Resources`.

Borea starts the game through the app, so the Mac Port tab works with mods too. Each instance has its own saves,
settings, port settings and `logs` folder.

Borea and StarMap are separate projects: mods made for them may not work on a Mac, and a new KSA version can break
them until they are updated.

## Building

`build.sh` makes the port folder from this repository. It needs:

- the Command Line Tools: `xcode-select --install`
- cmake and ninja: `brew install cmake ninja`
- the .NET 10 SDK: `brew install --cask dotnet-sdk`

```bash
git clone https://github.com/renancamm/ksa-macos-port.git
cd ksa-macos-port
./build.sh              # makes dist/ksa-macos-port/, a port folder like a release's
./build.sh --install    # also puts its files into the installed app
```

[CONTRIBUTING.md](CONTRIBUTING.md) has how the build works, what it checks and how a release is made. The sources,
each linked folder with its README:

| Folder | What it holds |
|---|---|
| [`src/game/driver/`](src/game/driver/) | KSA Metal's driver and its tests |
| [`src/game/hooks/`](src/game/hooks/) | KSA Metal Hooks, their loader and the Mac Port tab |
| [`src/game/changes/`](src/game/changes/) | the changes to the game: fixes, optimizations, features |
| [`src/game/benchmark/`](src/game/benchmark/) | the benchmark's director, its tests and vehicles |
| [`src/game/launcher/`](src/game/launcher/) | the app's executable |
| [`src/game/shims/glfw/`](src/game/shims/glfw/), [`shims/ktx/`](src/game/shims/ktx/) | the GLFW shim, the KTX shim |
| [`src/player/port-cli/`](src/player/port-cli/) | the port's program, what `port.sh` runs |
| [`src/player/port-folder/`](src/player/port-folder/) | the port folder's commands and README |
| [`src/player/benchmark-cli/`](src/player/benchmark-cli/) | the program `benchmark.sh` runs |
| [`src/shared/settings/`](src/shared/settings/) | the port settings, in the library the programs share |
| `src/build-tools/` | the generators the build runs |

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
