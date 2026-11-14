# The port's program

`KsaPort.Cli.dll` is the port's program: it does what `port.sh` does. The commands are short bash
scripts ([`../port-folder/`](../port-folder/)) that check the Mac and the folder, then start it on the port folder's
own .NET runtime (`payload/app/Contents/MacOS/dotnet`, Microsoft's), so a player needs nothing installed. In the port
folder it is `lib/port-cli/`, with its shader edits.

| File | What it holds |
|---|---|
| [`Program.cs`](Program.cs) | the commands, `port.sh`'s steps and its options |
| [`Conversion.cs`](Conversion.cs) | a game folder converted for macOS: the Linux binaries out, the .NET files for the app's runtime, the BRUTAL check (the game's Linux libraries against the packages'), the ARM64 mark |
| [`ShaderEdits.cs`](ShaderEdits.cs) | the shader edits: the `.edits` files read and applied |
| [`App.cs`](App.cs) | the app made around the converted game, or the port's files put into it again |
| [`Update.cs`](Update.cs) | `port.sh --update`: the newest release downloaded and checked |
| [`tests/`](tests/) | the tests that need no game |

The port folder, its `VERSION`, KSA's versions, the apps this port made and the messages are in the library it
shares with the benchmark ([`../../shared/PortFolder.cs`](../../shared/PortFolder.cs)). The shader edits it
makes are data, `.edits` files with the changes to the game they belong to
([`../../game/changes/`](../../game/changes/), whose README describes the format); the one edit made by code is there
too ([`BakedMilkyWay.cs`](../../game/changes/features/port/BakedMilkyWay.cs)), and the port's program compiles it.

The port's names (the app's name and bundle id, the oldest macOS, where releases are) are in
[`../port-folder/lib/port.env`](../port-folder/lib/port.env), which the commands, CMake and the port's program all
read. File operations that macOS's tools do best stay theirs: `ditto` keeps Microsoft's signatures and the links in a
copy, `tar` extracts the game, `plutil` reads and checks `Info.plist`, `xattr` removes the quarantine.

## The steps

The port folder holds the app without the game, `payload/app/Contents/`: the launcher, the .NET runtime, the native
libraries, the icon and the licences. `port.sh` extracts the game's archive, then runs two steps:

| Step | What it does |
|---|---|
| Convert | removes the Linux binaries and the Windows installer files, makes the .NET files run on the macOS runtime, and fixes and edits the game's shaders, as the `.edits` files of `src/game/changes/` say |
| App | builds the app around the converted game, or puts the port's files into one |

To update an app, it runs the same steps on it again, without the extraction.

## .NET

The Linux release is a self-contained `linux-x64` .NET program. The conversion turns it into one that runs on the
shared macOS runtime the app bundles:

1. It removes the Linux .NET runtime libraries (`System.*.dll`, `mscorlib.dll`, ...): exactly the DLLs whose names
   are also in Microsoft's macOS runtime. The game's own DLLs and NuGet packages stay.
2. It removes `KSA.deps.json`, which points the .NET host at the `linux-x64` runtime.
3. It replaces `KSA.runtimeconfig.json` with one for the shared .NET 10 runtime.
4. It changes `KSA.dll`'s machine type from AMD64 (`0x8664`) to ARM64 (`0xAA64`). .NET on Apple Silicon refuses
   64-bit assemblies marked for another CPU. `KSA.dll` contains only IL code, so this mark is the only thing in the
   way. The other game DLLs are platform-neutral and load as they are.

The runtime itself (about 85 MB) is Microsoft's. Its `dotnet` host is signed by Microsoft with the entitlements the
launcher needs: DYLD environment variables, unsigned libraries and JIT.

## BRUTAL's native libraries

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
Linux builds in these packages. The conversion compares each package version with the matching game assembly
(`Ahwoo.BRUTAL.Glfw.Native` with `Brutal.Glfw.dll`). It stops when the game is older than the packages, because older
bindings don't match the newer libraries, and warns when the game is newer.

## The app

The app step moves the converted game folder into the app as `Contents/Resources` and adds the port's part from
`payload/app/Contents/`:

| In the app | What |
|---|---|
| `Contents/MacOS/KSA` | the launcher ([`../../game/launcher/`](../../game/launcher/)) |
| `Contents/Resources/KSA` | a link to the launcher, the game folder's executable |
| `Contents/MacOS/dotnet`, `host/`, `shared/` | the .NET runtime |
| `Contents/Resources/*.dylib` | the native libraries, next to the game that loads them |
| `Contents/Resources/Hooks/` | KSA Metal Hooks, with Harmony |
| `Contents/Resources/Benchmark/` | the benchmark, with its tests and vehicles |
| `Contents/Resources/port-settings.txt`, `port-settings.toml` | the port settings' table and the file's template |
| `Contents/Resources/VERSION` | the port's version, for the tab's title |
| `Contents/Resources/Licenses/` | every component's licence and notices |
| `Contents/Resources/AppIcon.icns`, `Contents/Info.plist` | the icon; the game's and the port's versions |

`Info.plist` turns `NSHighResolutionCapable` on, so the game draws its interface at the display's full resolution.
The bundle isn't signed as a whole, because that would replace Microsoft's signature on `dotnet`. The app step
removes the quarantine attribute that a browser download puts on the files, so macOS opens the app as one made on
this Mac. When `port.sh` replaces an app, it builds the new one next to it, keeps its window layout (`imgui.ini`), and
moves the old one to the Trash only at the end; if anything fails, the old app is left as it was.

## Commands

| Command | Run by |
|---|---|
| `port [options] [archive \| folder]` | `port.sh` |
| `convert-dotnet <game> <runtime>` | the build, for KSA's assemblies the hooks and the benchmark are built against |
| `edit-shaders <game>` | by hand: the shader edits alone, on a game folder |

It exits with 0 when done, 1 on a problem (the message says which), 2 on a wrong option, and 75 after
`port --update` has put a newer release of the folder in `.update/`: `port.sh` then replaces the folder's contents
with it (the port's program runs from the folder, so it doesn't) and runs the new `port.sh` without `--update`.
`KSA_PORT_RELEASE_API` points the update at another release's JSON, for a test.

## Test

`build.sh` runs the tests ([`tests/Tests.cs`](tests/Tests.cs)) after building it: the shader edits' format and rules
on synthetic shaders (made, made again, the earlier form, `done-if`, `needs`, the checks, CRLF and other bytes kept),
the port's own `.edits` files, the ARM64 mark, the cleanup and the versions.

For a change to the edits or the conversion, port the game's archive into a test app and look at what changed:

```bash
dist/ksa-macos-port/port.sh --app /tmp/ksa-test/KSA.app ~/Downloads/ksa_linux_v2026.10.25.5707.tar.gz
KsaPort.Cli edit-shaders <game folder>    # dotnet deps/build/assemblies/bin/KsaPort.Cli/KsaPort.Cli.dll
```

A second run on the same folder must print only `[OK]` lines and change nothing.
