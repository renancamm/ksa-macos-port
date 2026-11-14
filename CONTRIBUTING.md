# Building, testing and releasing the port

The README's [Building](README.md#building) has the tools to install, the commands and a map of the source. This file
has how the build works, what it checks and how a release is made. Each piece's README has how to work on it.

## The build

CMake builds everything ([`CMakeLists.txt`](CMakeLists.txt)): the native libraries, and the port's program, the hooks,
the benchmark, the shared library and the two generators, .NET projects of one solution
([`ksa-macos-port.slnx`](ksa-macos-port.slnx)), which an editor can open after a first build. `build.sh` runs its four
steps: configure, build, install (the port folder, [`cmake/port-folder.cmake`](cmake/port-folder.cmake)) and test.
Everything goes to `deps/build/`, and a second build only compiles what changed.

The hooks and the benchmark are built against the installed app's KSA, or another one with `--ksa <archive>`, and the
port folder is made for that KSA version. Everything the build downloads is declared, with its checksum, in
[`cmake/dependencies.cmake`](cmake/dependencies.cmake).

| Option | |
|---|---|
| `--ksa <archive \| folder \| app>` | the KSA to build against (default: the installed app, otherwise the newest archive in Downloads) |
| `--install` | then put the port folder's files into the installed app |
| `--app <path>` | the app for `--install` (default: `~/Applications/Kitten Space Agency.app`) |

Three files are made by the build, in its build tree, and are not in the repository: `port-settings.h` and
`Settings.Generated.cs`, the settings the native code and the hooks read, from the settings table
([`src/shared/settings/`](src/shared/settings/)), and `entries.inc`, the driver's entry table, from Vulkan's registry
and the driver's sources ([`src/build-tools/entries-generator/`](src/build-tools/entries-generator/)). A build makes
them again when what they come from changed.

## Checks and tests

Warnings are errors in the native libraries and the .NET projects, and the build runs the tests of the driver, the
port's program, the port settings and the benchmark, none of which need the game. The scripts run on macOS's own
bash 3.2 and are kept clean of ShellCheck's findings ([`.shellcheckrc`](.shellcheckrc));
[`.editorconfig`](.editorconfig) says how the sources are written.

A change to how the game runs or looks is tested in the game with the benchmark's `check`: the Mixed Test, compared
with a baseline, screenshots included ([`src/game/benchmark/`](src/game/benchmark/)).

## Releases

`release.sh <ksa_linux_vX.tar.gz>` makes a release. It builds the port folder from a clean copy of the committed
sources, zips it, and tests the zip by making an app with it and running the benchmark on that app with a fresh
install's settings (`--no-game-test` skips the run). The version is the build number of the KSA it is made for and
the port's release for it: `v5707.2` is the second for KSA 2026.10.25.5707. `release.sh --publish` then tags the
commit and uploads the zip as a draft GitHub release.

## The README's pictures

`scripts/readme-screenshots.sh` makes the pictures in [`.github/images/`](.github/images/) and the caption under the
first: the benchmark plays its `readme` scenario in the installed app at a fresh install's settings. The game's view is
a picture of its window with Apple's Metal Performance HUD, taken once the frame rate has settled; the Mac Port tab is
KSA Metal's own screenshot, without the HUD. Run it from Terminal after `./build.sh --install`; Terminal needs the
Screen Recording permission (System Settings > Privacy & Security).

## The repository's top level

```text
build.sh, release.sh        make the port folder; make a release of it
scripts/                    the icon, the port folder's check, the README's pictures, and lib.sh, which they share
CMakeLists.txt, cmake/      the build, the port folder's layout, and every external source the build uses
CMakePresets.json           how CMake is run: Ninja, Release, deps/build/cmake/
ksa-macos-port.slnx         the .NET projects
global.json                 the .NET SDK version that builds them
src/                        the sources (the README's Building has their map)
third_party/fmod/           FMOD's macOS library, with its licence and notice (not MIT)
.github/                    the issue templates
deps/, dist/                made by the build: downloads and build trees; the port folder and release zips
```
