# StarMap mod loader

An optional extra that adds [StarMap](https://github.com/StarMapLoader/StarMap), a community mod loader for KSA, to
the app made by `port.sh`. With StarMap in the app, the game loads StarMap mods: mods with their own code, which
KSA alone does not run.

StarMap is a separate project, still early. Mods made for it may not work on a Mac, and a new KSA version can break
StarMap until it is updated.

## Install

From the project's folder, after `./port.sh`:

```bash
./extras/starmap/port-starmap.sh
```

Open the game as usual: it now starts through StarMap. To go back to the game without mods:

```bash
./extras/starmap/port-starmap.sh --remove
```

`./update.sh` keeps StarMap in the app. A new KSA version makes a new app without it: run `port-starmap.sh` again
after updating the game.

## Mods

Put each mod in its own folder in `~/Documents/My Games/Kitten Space Agency/mods/`. At the next start the game
asks whether to enable the new mod. When you enable it, StarMap asks to restart the game: choose **Continue**, then
quit and open the game again. **Restart** closes the game but cannot open it again on a Mac.

StarMap's output goes to `console.log` in `~/Documents/My Games/Kitten Space Agency/logs`, with the game's.

## How it works

StarMap is published as a Windows build, but only two of its files are Windows-specific:

- `StarMap.exe` starts StarMap on Windows. The app doesn't need it: it runs `StarMap.dll` with its own .NET runtime.
- `StarMap.dll` is marked as built for x64. It contains only .NET code, so `port-starmap.sh` changes the mark to
  ARM64, as `port.sh` does for `KSA.dll` (`scripts/mark-arm64.sh`).

The other files are platform-neutral .NET libraries and are used as they are. `port-starmap.sh` downloads the
pinned release, checks its SHA-256, and puts it in `Kitten Space Agency.app/Contents/Resources/StarMap/`. When that
folder exists, the app's launcher starts the game through StarMap and writes StarMap's config
(`StarMapConfig.json`) with the game's path at every start, so the app can be moved.

## Options and files

| Option | |
|---|---|
| `--app <path>` | the app to change (default: `~/Applications/Kitten Space Agency.app`) |
| `--remove` | take StarMap out of the app |

| File | |
|---|---|
| `deps/starmap/StarMap-<version>.zip` | the downloaded release |
| `Kitten Space Agency.app/Contents/Resources/StarMap/` | StarMap in the app |

StarMap is MIT-licensed, by the StarMap authors, and includes [Harmony](https://github.com/pardeike/Harmony) (MIT).
