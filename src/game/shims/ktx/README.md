# The KTX shim

`libKtxShim.dylib` keeps the pixels of KSA's textures in memory that macOS can page out without swapping. KSA keeps a
copy of every texture's pixels for the whole session (about 3.5 GB with the solar system), in memory the libraries
that load them allocate themselves: libktx (KTX2), gli (DDS) and stb (PNG). macOS can only compress or swap that
memory when it runs short. The launcher loads the shim with `DYLD_INSERT_LIBRARIES`; it interposes the libraries'
load and free calls, and once one has loaded a texture of 64 KB or more, it swaps the texture's pixels for the same
bytes mapped from a file:

- the game's own file, when its bytes are the texture's: an uncompressed KTX2, a DDS;
- otherwise a copy written once to `textures/` in KSA Metal's cache folder, named after the file's path, size and
  date (a zstd-compressed KTX2 is decompressed there once, a PNG decoded once; later starts map the copy without
  decoding the PNG). Copies unused for 60 days are deleted when the game starts.

Mapped pages belong to the file: macOS drops them when memory runs short and reads them back when the game touches
them again. Textures that need transcoding (Basis) stay as libktx made them. A DDS's pixels sit in gli's texture
object, whose layout the shim reads from BRUTAL's build of gli; it checks the layout on every texture and leaves one
that doesn't match as gli made it. [`ktx_shim.c`](ktx_shim.c) has the details; the libraries, RocketWerkz's builds
from BRUTAL's native packages, stay unmodified.

Its port settings ([`../../../shared/settings/`](../../../shared/settings/); each one's variable is `KSA_MTL_` and its
key in capitals):

| Setting | Effect |
|---|---|
| `texture_file_mapping` | on (Memory Use turns it on with 16 GB of memory or less): the shim maps textures' pixels from their files; off, the default: it leaves every texture as its library made it |
| `cache_dir` | a folder: where the copies go, as KSA Metal's cache (default: `~/Library/Caches/ksa-metal`) |
