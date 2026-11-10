# The launcher

`Contents/MacOS/KSA`, the app's executable ([`launcher.c`](launcher.c)). It sets up the game's environment, the shims,
the hooks' loader and the library path, starts itself again so that they take effect, and runs `KSA.dll` in its own
process with .NET's host library, as .NET's app host does: the game's process is `KSA`, the app's executable, and macOS
treats the game as the app. The comment at the top of `launcher.c` lists what it sets.

## Starting a mod loader

`KSA <program.dll> [arguments]` starts that program instead of `KSA.dll`, in its own folder, with the same environment
and the app's .NET runtime: it stands in for `dotnet <program.dll> [arguments]`. A mod loader that loads the game
itself (StarMap) runs this way with the port's shims, hooks and port settings. `Contents/Resources/KSA` is a link to
the launcher, so the game folder holds an executable named `KSA`, as the Linux release's does. A mod manager (Borea)
starts the game with `<game folder>/KSA`, and a loader that runs through `dotnet` with
`<game folder>/KSA <loader>.dll -InstancePath <instance>`; the link and this start are a fixed interface.

`KSA_PORT_PROGRAM` names the program the launcher runs (its full path), so the hooks' loader runs only in it, and when
it is a mod loader, the game's Restart starts it again with its arguments. `STARMAP_INSTANCE_PATH` moves the game's documents
folder, port settings included. Without a program, the game starts through the app's StarMap when it has one
(`starmap.sh`; `KSA_STARMAP_DIR` names another StarMap folder).

macOS's allocator reads its variables when the process starts, before the hooks' loader applies the port settings, so
the launcher reads its one setting from `port-settings.toml` itself (the game's documents folder, or
`STARMAP_INSTANCE_PATH`); its variable `KSA_MTL_RELEASE_FREED_MEMORY`, when set, wins over the file:

| Setting | Effect |
|---|---|
| `release_freed_memory` | on: `MallocLargeCache=0`, so blocks of 128 KB or more the game frees go back to macOS at once instead of staying in the allocator's cache, dirty (about 0.8 GB with the solar system); with no file yet, a fresh install's first start, on with 16 GB of memory or less, as Auto Memory Saving is about to write it. The hooks' `FreedMemory` does the game's half ([`../changes/`](../changes/)) |
