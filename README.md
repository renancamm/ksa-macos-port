# Kitten Space Agency on Apple Silicon Macs

Turns the Linux version of **Kitten Space Agency** (KSA) into a Mac app, `Kitten Space Agency.app`, that runs
natively on Apple Silicon: no Wine, no Rosetta. Every graphics setting works, and so does the sound.

This is an unofficial community project. It contains no game files: you need your own copy of KSA's Linux version.

## What you need

- A Mac with Apple Silicon (M1 or later) and **macOS 26** or later.
- The **Linux version of KSA** from [ksa.ahwoo.com](https://ksa.ahwoo.com), a file like `ksa_linux_v2026.10.13.5602.tar.gz`.
  Leave it in your Downloads folder.

## Make the app

1. Get this project. With git: `git clone https://github.com/renancamm/ksa-macos-port.git`. Or download it from
   GitHub (**Code**, then **Download ZIP**) and unzip it.
2. Open Terminal, go to the project's folder and run `./port.sh`:
   ```bash
   cd ksa-macos-port
   ./port.sh
   ```
3. Open **Kitten Space Agency** from Launchpad, Spotlight or `~/Applications`.

`port.sh` takes the newest `ksa_linux_v*.tar.gz` from your Downloads folder. To use another file, pass its path:
`./port.sh ~/Desktop/ksa_linux_v2026.10.13.5602.tar.gz`. It takes a minute and needs an internet connection the
first time (see [what it downloads](docs/how-it-works.md#network-access)).

The first start of the game takes longer than later ones, while macOS compiles the game's shaders.

## Update

When a new KSA version comes out, KSA tells you in the game. Download its Linux version into your Downloads folder,
then run:

```bash
./update.sh
```

`update.sh` first updates this project (with `git pull`; if you downloaded the ZIP, download it again instead). Then:

- If your Downloads folder has a newer KSA than the app, it makes the app again from it. The old app goes to the
  Trash, so you can delete it once the new one runs.
- Otherwise it applies this project's latest fixes to the app you have.

Your saves and settings are never touched. `update.sh` doesn't look online for new KSA versions.

## Your files

| What | Where |
|---|---|
| The app | `~/Applications/Kitten Space Agency.app` (`--app <path>` puts it elsewhere) |
| Saves, settings, game logs, crash dumps | `~/Documents/My Games/Kitten Space Agency` |
| The app's own log | `~/Library/Logs/Kitten Space Agency.log` |
| Compiled shaders | `~/Library/Caches/ksamtl` |

To uninstall, delete the app, the cache folder and this project's folder. Delete the Documents folder too if you
don't want to keep your saves.

## If something goes wrong

- **"no prebuilt libraries match these sources"**: a few of the app's libraries are built by this project, and for
  this version none are published to download. The message says what to install to build them on your Mac (the
  Command Line Tools and cmake); then run the command again. `--from-source` always builds them on your Mac.
- **The game doesn't start**: look at `~/Library/Logs/Kitten Space Agency.log`.
- **`[WARN]` next to a shader fix** while converting a new KSA version: the game changed the code that fix
  applies to. RocketWerkz may have fixed the bug, or this project needs an update. The game still runs; please open
  an issue.
- **"this KSA uses BRUTAL ..., newer than the port's"**: the game moved to a newer version of its framework. If it
  fails to start, this project needs an update; please open an issue.

## How it works

The Linux version of KSA is a .NET program with Linux libraries next to it. `port.sh` swaps in macOS versions of
those libraries: RocketWerkz's own macOS builds of its framework's libraries, Microsoft's .NET runtime for macOS,
FMOD for macOS, and a graphics driver written for KSA on Apple's Metal. Then it fixes two bugs in the game's shaders
and puts it all in an app. [docs/how-it-works.md](docs/how-it-works.md) explains each step;
[driver/README.md](driver/README.md) explains the driver.

## Credits

- [Luc8020/ksa-macos-port](https://github.com/Luc8020/ksa-macos-port), the project this one started from.
- RocketWerkz, for Kitten Space Agency and for publishing [BRUTAL](https://github.com/ahwootech/BRUTAL), its
  framework, with macOS builds of its libraries.
- [SPIRV-Cross](https://github.com/KhronosGroup/SPIRV-Cross), [SPIRV-Tools](https://github.com/KhronosGroup/SPIRV-Tools),
  [GLFW](https://www.glfw.org) and [Vulkan Memory Allocator](https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator),
  built into the app, and the many projects inside BRUTAL's libraries.
- Audio: FMOD Engine by Firelight Technologies Pty Ltd.

[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) lists every component in the app with its licence.

## License

The scripts and sources here are under the [MIT License](LICENSE), except [`third_party/fmod/`](third_party/fmod/),
which holds FMOD's macOS library under the FMOD End User Licence Agreement
([`NOTICE.txt`](third_party/fmod/NOTICE.txt), [`LICENSE.TXT`](third_party/fmod/LICENSE.TXT)). FMOD is a trademark
of Firelight Technologies Pty Ltd; this project is not affiliated with or endorsed by Firelight Technologies.

Kitten Space Agency is developed and owned by RocketWerkz. This project is not affiliated with or endorsed by
RocketWerkz.
