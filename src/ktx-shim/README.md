# The KTX shim

`libKtxShim.dylib` keeps the pixels of KSA's textures in memory that macOS can page out without swapping. KSA keeps a
copy of every texture's pixels for the whole session (about 3.5 GB with the solar system), in memory libktx allocates
itself, which macOS can only compress or swap when memory runs short. The launcher loads the shim with
`DYLD_INSERT_LIBRARIES`; it interposes libktx's create and destroy calls, and once libktx has loaded a texture of
1 MB or more, it swaps the texture's pixels for the same bytes mapped from a file:

- the game's own file, when the texture is stored uncompressed and its bytes are the file's;
- otherwise a copy written once to `textures/` in KSA Metal's cache folder, named after the file's path, size and
  date (a zstd-compressed texture is decompressed there once). Copies unused for 60 days are deleted when the game
  starts.

Mapped pages belong to the file: macOS drops them when memory runs short and reads them back when the game touches
them again. Textures that need transcoding (Basis) stay as libktx made them. [`ktx_shim.c`](ktx_shim.c) has the
details; libktx itself, RocketWerkz's build from the `Ahwoo.BRUTAL.Ktx.Native` package, stays unmodified.

| Variable | Setting | Effect |
|---|---|---|
| `KSA_KTX_SHIM=0` | `texture_file_mapping` | the shim leaves every texture as libktx made it |
| `KSA_MTL_CACHE_DIR=<dir>` | `cache_dir` | where the copies go, as KSA Metal's cache (default: `~/Library/Caches/ksa-metal`) |
