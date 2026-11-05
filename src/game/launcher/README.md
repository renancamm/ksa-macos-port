# The launcher

`Contents/MacOS/KSA`, the app's executable ([`launcher.c`](launcher.c)). It sets up the game's environment, the shims,
the hooks' loader and the library path, and hands its process over to .NET's `dotnet` running `KSA.dll`, so macOS
treats the game as the app. The comment at the top of `launcher.c` lists what it sets.

macOS's allocator reads its variables when the process starts, before the hooks' loader applies the port settings, so
the launcher reads its one setting from `port-settings.toml` itself (the game's documents folder, or
`STARMAP_INSTANCE_PATH`); its variable `KSA_MTL_RELEASE_FREED_MEMORY`, when set, wins over the file:

| Setting | Effect |
|---|---|
| `release_freed_memory` | on: `MallocLargeCache=0`, so blocks of 128 KB or more the game frees go back to macOS at once instead of staying in the allocator's cache, dirty (about 0.8 GB with the solar system); with no file yet, a fresh install's first start, on with 16 GB of memory or less, as Auto Memory Saving is about to write it. The hooks' `FreedMemory` does the game's half ([`../changes/`](../changes/)) |
