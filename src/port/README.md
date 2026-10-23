# Kitten Space Agency for Apple Silicon Macs

This folder turns the Linux version of Kitten Space Agency into a Mac app. It holds everything except the game,
which you download yourself from [ksa.ahwoo.com](https://ksa.ahwoo.com) (a file like
`ksa_linux_v2026.10.19.5638.tar.gz`, left in your Downloads folder). It needs an Apple Silicon Mac with macOS 26.

In Terminal, from this folder:

| Command | What it does |
|---|---|
| `./port.sh` | makes `~/Applications/Kitten Space Agency.app` from the newest KSA archive in Downloads, or updates the app you have; `--update` first updates this folder to the newest release of the port |
| `./starmap.sh` | adds the StarMap mod loader to the app (`--remove` takes it out) |
| `./benchmark.sh` | measures how the game runs on this Mac and writes a report |

Each command explains its options with `--help`. Saves and settings stay in
`~/Documents/My Games/Kitten Space Agency`; bug reports are welcome on GitHub with the files in its `logs` folder.

`VERSION` names this version of the port and the KSA version it was made for. The licences of everything in the
app are in `THIRD_PARTY_NOTICES.md` and in the app at `Contents/Resources/Licenses/`.

More: https://github.com/renancamm/ksa-macos-port
