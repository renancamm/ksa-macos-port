# The porter

`KsaPorter.dll` is the port's program: it does what `port.sh` and `starmap.sh` do. The commands are short bash
scripts ([`../port-folder/`](../port-folder/)) that check the Mac and the folder, then start the porter on the port
folder's own .NET runtime (`payload/app/Contents/MacOS/dotnet`, Microsoft's), so a player needs nothing installed. In
the port folder it is `lib/porter/`, with its shader edits.

| File | What it holds |
|---|---|
| [`Program.cs`](Program.cs) | the commands, `port.sh`'s steps and its options, `starmap.sh` |
| [`Conversion.cs`](Conversion.cs) | a game folder converted for macOS: the Linux binaries out, the .NET files for the app's runtime, the BRUTAL version check, the ARM64 mark |
| [`ShaderEdits.cs`](ShaderEdits.cs) | the shader edits: the `.edits` files read and applied |
| [`MilkyWay.cs`](MilkyWay.cs) | the one edit made by code: the Milky Way bake's |
| [`App.cs`](App.cs) | the app made around the converted game, or the port's files put into it again; StarMap in the app |
| [`Update.cs`](Update.cs) | `port.sh --update`: the newest release downloaded and checked |
| [`shader-edits/`](shader-edits/) | the edits to the game's shaders, as data, with their own README |
| [`tests/`](tests/) | the tests that need no game |

The port folder, its `VERSION`, KSA's versions, the apps this port made and the messages are in the library the
porter shares with the benchmark ([`../shared/PortFolder.cs`](../shared/PortFolder.cs)).

The port's names (the app's name and bundle id, the oldest macOS, where releases are) are in
[`../port-folder/lib/common.sh`](../port-folder/lib/common.sh), which the commands, CMake and the porter all read. File
operations that macOS's tools do best stay theirs: `ditto` keeps Microsoft's signatures and the links in a copy, `tar`
extracts the game, `plutil` reads and checks `Info.plist`, `xattr` removes the quarantine.

## Commands

| Command | Run by |
|---|---|
| `port [options] [archive \| folder]` | `port.sh` |
| `starmap [--app <path>] [--remove]` | `starmap.sh` |
| `convert-dotnet <game> <runtime>` | the build, for KSA's assemblies the hooks and the benchmark are built against |
| `mark-arm64 <file.dll...>` | the build, for StarMap's `StarMap.dll` |
| `edit-shaders <game>` | by hand: the shader edits alone, on a game folder |

It exits with 0 when done, 1 on a problem (the message says which), 2 on a wrong option, and 75 after
`port --update` has put a newer release of the folder in `.update/`: `port.sh` then replaces the folder's contents
with it (the porter runs from the folder, so it doesn't) and runs the new `port.sh` without `--update`.
`KSA_PORT_RELEASE_API` points the update at another release's JSON, for a test.

## Test

`build.sh` runs the tests ([`tests/Tests.cs`](tests/Tests.cs)) after building it: the shader edits' format and rules
on synthetic shaders (made, made again, the earlier form, `done-if`, `needs`, the checks, CRLF and other bytes kept),
the port's own `.edits` files, the ARM64 mark, the cleanup and the versions.

For a change to the edits or the conversion, port the game's archive into a test app and look at what changed:

```bash
dist/ksa-macos-port/port.sh --app /tmp/ksa-test/KSA.app ~/Downloads/ksa_linux_v2026.10.25.5707.tar.gz
KsaPorter edit-shaders <game folder>    # dotnet deps/build/assemblies/bin/KsaPorter/KsaPorter.dll
```

A second run on the same folder must print only `[OK]` lines and change nothing.
