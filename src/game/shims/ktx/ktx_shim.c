/*
 * libKtxShim.dylib - keeps the pixels of KSA's textures in memory that macOS can page out without swapping.
 *
 * KSA keeps a copy of every texture's pixels in memory for the whole session (TextureAsset: the terrain's height
 * queries, the ocean's wind, the rings' colours, the texture streamer's uploads), about 3.5 GB with the solar system.
 * The libraries that load them, libktx (KTX2), gli (DDS) and stb (PNG, JPEG), read them into memory of their own
 * (malloc), which macOS can only compress or write to swap when memory runs short. The launcher loads this shim with
 * DYLD_INSERT_LIBRARIES; it replaces the libraries' load and free calls (through dyld interposing, the libraries stay
 * unmodified) and, once one has loaded a texture of 64 KB or more, swaps its pixels for the same bytes mapped from a
 * file:
 *
 * - the game's own file, when its bytes are the texture's (compared): an uncompressed KTX2, a DDS;
 * - otherwise a copy written once to textures/ in KSA Metal's cache folder (~/Library/Caches/ksa-metal, or
 *   KSA_MTL_CACHE_DIR), named after the file's path, size and date (a zstd-compressed KTX2 is decompressed there
 *   once, a PNG decoded there once: later starts map the copy without decoding the file). A copy is dated when used;
 *   copies unused for 60 days (from an older version of the game) are deleted when the game starts.
 *
 * Mapped pages are the file's: macOS drops them when memory runs short and reads them back when the game touches them
 * again, instead of swapping them. The mapping is private and writable, so a write changes only this process's copy
 * of the page. Textures that need transcoding (Basis) stay as libktx made them. KSA_MTL_TEXTURE_FILE_MAPPING=0 turns
 * it off.
 */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#include "port-settings.h"

#define DYLD_INTERPOSE(replacement, replacee) \
    __attribute__((used)) static struct { const void* replacement; const void* replacee; } \
    interpose_##replacee __attribute__((section("__DATA,__interpose"))) = \
    { (const void*)(unsigned long)&replacement, (const void*)(unsigned long)&replacee };

// libktx 4.4's entry points as BRUTAL binds them (libKtx.dylib exports these "1" names)
typedef struct ktxTexture ktxTexture;
extern int ktxTexture_CreateFromNamedFile1(const char* filename, uint32_t flags, ktxTexture** texture);
extern void ktxTexture_Destroy1(ktxTexture* texture);
extern uint8_t* ktxTexture_GetData1(ktxTexture* texture);
extern size_t ktxTexture_GetDataSize1(ktxTexture* texture);
extern bool ktxTexture_NeedsTranscoding1(ktxTexture* texture);

// BRUTAL's gli wrapper (libgli.dylib): a gli::texture made by load, freed by release, its pixels' start and size
typedef struct gliTexture gliTexture;
extern gliTexture* load(const char* filename);
extern void release(gliTexture* texture);
extern uint8_t* data(gliTexture* texture);
extern int64_t size(gliTexture* texture);

// stb_image as BRUTAL binds it (libstb.dylib)
extern unsigned char* stbi_load(const char* filename, int* x, int* y, int* channels, int desired);
extern uint16_t* stbi_load_16(const char* filename, int* x, int* y, int* channels, int desired);
extern float* stbi_loadf(const char* filename, int* x, int* y, int* channels, int desired);
extern void stbi_image_free(void* pixels);

#define MIN_BYTES (64u << 10)

enum owner { KTX, GLI, STB };

// A texture's pixels mapped from a file: key is what the library frees it by (the ktxTexture, gli's storage, stb's
// pixels)
struct mapping {
    enum owner owner;
    void* key;
    uint8_t* data;  // what the library's pointer to the pixels holds now
    size_t size;
    void* base;     // the mapping
    size_t length;
};

static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static struct mapping* g_maps;
static size_t g_count, g_capacity;
static uint64_t g_fileBytes, g_cacheBytes, g_logged;
static int g_enabled = -1;

static bool enabled(void) {
    if (g_enabled < 0) {
        g_enabled = portSettingsRead().textureFileMapping;
    }
    return g_enabled;
}

// A private, writable mapping of length bytes of fd from offset; the pointer to offset (base and mapped length out)
static uint8_t* mapFile(int fd, off_t offset, size_t length, void** base, size_t* mapped) {
    off_t page = offset & ~(off_t)(getpagesize() - 1);
    *mapped = length + (size_t)(offset - page);
    *base = mmap(NULL, *mapped, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, page);
    if (*base == MAP_FAILED) return NULL;
    return (uint8_t*)*base + (offset - page);
}

// The bytes of the game's file from offset, when they are exactly these
static uint8_t* mapFileIfSame(int fd, off_t offset, const uint8_t* data, size_t size, void** base, size_t* mapped) {
    uint8_t* p = mapFile(fd, offset, size, base, mapped);
    if (p && !memcmp(p, data, size)) return p;
    if (p) munmap(*base, *mapped);
    return NULL;
}

// The game's file, when it is an uncompressed KTX2 whose data region holds exactly these bytes
static uint8_t* mapKtxFile(const char* path, const uint8_t* data, size_t size, void** base, size_t* mapped) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return NULL;
    uint8_t* result = NULL;
    uint8_t header[80];
    static const uint8_t ktx2[12] = {0xAB, 'K', 'T', 'X', ' ', '2', '0', 0xBB, '\r', '\n', 0x1A, '\n'};
    if (pread(fd, header, sizeof header, 0) == (ssize_t)sizeof header && !memcmp(header, ktx2, 12)) {
        uint32_t levels, scheme;
        memcpy(&levels, header + 40, 4);
        memcpy(&scheme, header + 44, 4);
        if (levels == 0) levels = 1;
        uint64_t start = UINT64_MAX;
        for (uint32_t i = 0; scheme == 0 && i < levels && i < 32; i++) {
            uint64_t offset;
            if (pread(fd, &offset, 8, 80 + 24 * i) != 8) { start = UINT64_MAX; break; }
            if (offset < start) start = offset;
        }
        struct stat st;
        if (scheme == 0 && start != UINT64_MAX && fstat(fd, &st) == 0 && start + size <= (uint64_t)st.st_size)
            result = mapFileIfSame(fd, (off_t)start, data, size, base, mapped);
    }
    close(fd);
    return result;
}

// The game's file, when its last bytes are exactly these: a DDS is its header and then the pixels as gli keeps them
static uint8_t* mapFileTail(const char* path, const uint8_t* data, size_t size, void** base, size_t* mapped) {
    int fd = open(path, O_RDONLY);
    if (fd < 0) return NULL;
    uint8_t* result = NULL;
    struct stat st;
    if (fstat(fd, &st) == 0 && (uint64_t)st.st_size >= size)
        result = mapFileIfSame(fd, st.st_size - (off_t)size, data, size, base, mapped);
    close(fd);
    return result;
}

// Delete the cache's copies that no game start used in 60 days
static void cleanCache(const char* dir) {
    DIR* d = opendir(dir);
    if (!d) return;
    time_t old = time(NULL) - 60 * 24 * 3600;
    struct dirent* e;
    char path[1400];
    while ((e = readdir(d))) {
        if (e->d_name[0] == '.') continue;
        snprintf(path, sizeof path, "%s/%s", dir, e->d_name);
        struct stat st;
        if (stat(path, &st) == 0 && S_ISREG(st.st_mode) && st.st_mtime < old) unlink(path);
    }
    closedir(d);
}

// KSA Metal's cache folder, like the driver picks it (shadercache.mm), and the copies' folder in it: found at the first
// copy, when the copies unused for 60 days are deleted too
static char g_parent[1024], g_cacheDir[1024];
static pthread_once_t g_cacheOnce = PTHREAD_ONCE_INIT;
static void findCache(void) {
    const char* custom = portSettingsRead().cacheDir;
    const char* home = getenv("HOME");
    if (custom && *custom) snprintf(g_parent, sizeof g_parent, "%s", custom);
    else if (home) snprintf(g_parent, sizeof g_parent, "%s/Library/Caches/ksa-metal", home);
    else return;
    snprintf(g_cacheDir, sizeof g_cacheDir, "%s/textures", g_parent);
    cleanCache(g_cacheDir);
}

// The cache's copy of a file's texture: named after the file's path, size and date and what tells its copies apart
// (a KTX2's data size, stb's output)
static bool cachePath(const char* path, uint64_t kind, char* file, size_t capacity) {
    struct stat st;
    if (stat(path, &st) != 0) return false;
    uint64_t h = 1469598103934665603ull;  // FNV-1a
    for (const char* c = path; *c; c++) h = (h ^ (uint8_t)*c) * 1099511628211ull;
    h = (h ^ (uint64_t)st.st_size) * 1099511628211ull;
    h = (h ^ (uint64_t)st.st_mtimespec.tv_sec) * 1099511628211ull;
    h = (h ^ kind) * 1099511628211ull;
    pthread_once(&g_cacheOnce, findCache);
    if (!g_cacheDir[0]) return false;
    snprintf(file, capacity, "%s/%016llx", g_cacheDir, (unsigned long long)h);
    return true;
}

// Writes the copy (a header, then the pixels) under a temporary name, then renames it into place; open for reading
static int writeCacheFile(const char* file, const void* header, size_t headerSize, const uint8_t* data, size_t size) {
    mkdir(g_parent, 0755);
    mkdir(g_cacheDir, 0755);
    char temp[1200];
    snprintf(temp, sizeof temp, "%s.%d.%p", file, getpid(), (const void*)data);
    int out = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0644);
    if (out < 0) return -1;
    bool ok = headerSize == 0 || write(out, header, headerSize) == (ssize_t)headerSize;
    size_t done = 0;
    while (ok && done < size) {
        ssize_t n = write(out, data + done, size - done);
        if (n <= 0) break;
        done += (size_t)n;
    }
    close(out);
    if (!ok || done != size || rename(temp, file) != 0) {
        unlink(temp);
        return -1;
    }
    return open(file, O_RDONLY);
}

// The cache's copy of these bytes (written now if missing)
static uint8_t* mapCacheFile(const char* path, const uint8_t* data, size_t size, void** base, size_t* mapped) {
    char file[1100];
    if (!cachePath(path, (uint64_t)size, file, sizeof file)) return NULL;
    int fd = open(file, O_RDONLY);
    struct stat cs;
    if (fd >= 0 && (fstat(fd, &cs) != 0 || (size_t)cs.st_size != size)) {
        close(fd);
        fd = -1;
    }
    if (fd < 0) fd = writeCacheFile(file, NULL, 0, data, size);
    if (fd < 0) return NULL;
    futimes(fd, NULL);  // used now
    uint8_t* p = mapFile(fd, 0, size, base, mapped);
    close(fd);
    return p;
}

// Room in the table for one more (the lock held): taken before a library's pointer is swapped, so a mapping is never
// left unrecorded for the library to free
static bool reserve(void) {
    if (g_count < g_capacity) return true;
    size_t capacity = g_capacity ? g_capacity * 2 : 256;
    struct mapping* maps = realloc(g_maps, capacity * sizeof *maps);
    if (!maps) return false;
    g_maps = maps;
    g_capacity = capacity;
    return true;
}

static void track(enum owner owner, void* key, uint8_t* data, size_t size, void* base, size_t length) {
    g_maps[g_count++] = (struct mapping){owner, key, data, size, base, length};
}

// Counts a texture mapped (the lock held) and says so in the log at every 512 MB
static void counted(size_t size, bool fromGame) {
    if (fromGame) g_fileBytes += size;
    else g_cacheBytes += size;
    if ((g_fileBytes + g_cacheBytes) >> 29 != g_logged) {
        g_logged = (g_fileBytes + g_cacheBytes) >> 29;
        printf("[ktx-shim] textures mapped: %llu MB from the game's files, %llu MB from the cache\n",
               (unsigned long long)(g_fileBytes >> 20), (unsigned long long)(g_cacheBytes >> 20));
        fflush(stdout);
    }
}

// The mapping freed by this key, taken out of the table (the lock held); its slot copied to *out
static bool untrack(enum owner owner, const void* key, struct mapping* out) {
    for (size_t i = 0; i < g_count; i++) {
        if (g_maps[i].owner != owner || g_maps[i].key != key) continue;
        *out = g_maps[i];
        g_maps[i] = g_maps[--g_count];
        return true;
    }
    return false;
}

// KTX2 (libktx). Where in the texture object libktx keeps pData (and dataSize just before it): found by value, so the
// shim depends on no struct layout
static uint8_t** ktxDataField(ktxTexture* t, uint8_t* data, size_t size) {
    uint8_t** slots = (uint8_t**)t;
    for (int i = 1; i < 32; i++)
        if (slots[i] == data && ((size_t*)slots)[i - 1] == size) return &slots[i];
    return NULL;
}

static int shim_ktxTexture_CreateFromNamedFile1(const char* filename, uint32_t flags, ktxTexture** texture) {
    int result = ktxTexture_CreateFromNamedFile1(filename, flags, texture);
    if (result != 0 || !enabled() || !texture || !*texture || !filename) return result;
    ktxTexture* t = *texture;
    uint8_t* data = ktxTexture_GetData1(t);
    size_t size = ktxTexture_GetDataSize1(t);
    if (!data || size < MIN_BYTES || ktxTexture_NeedsTranscoding1(t)) return result;
    uint8_t** field = ktxDataField(t, data, size);
    if (!field) return result;
    void* base;
    size_t mapped;
    bool fromGame = true;
    uint8_t* p = mapKtxFile(filename, data, size, &base, &mapped);
    if (!p) {
        fromGame = false;
        p = mapCacheFile(filename, data, size, &base, &mapped);
    }
    if (!p) return result;
    pthread_mutex_lock(&g_lock);
    if (!reserve()) {
        pthread_mutex_unlock(&g_lock);
        munmap(base, mapped);
        return result;
    }
    track(KTX, t, p, size, base, mapped);
    *field = p;
    free(data);
    counted(size, fromGame);
    pthread_mutex_unlock(&g_lock);
    return result;
}

static void shim_ktxTexture_Destroy1(ktxTexture* t) {
    if (t) {
        pthread_mutex_lock(&g_lock);
        struct mapping m;
        if (untrack(KTX, t, &m)) {
            // libktx frees pData when it isn't NULL; if something replaced it since, leave both alone
            uint8_t** field = ktxDataField(t, m.data, m.size);
            if (field) {
                *field = NULL;
                munmap(m.base, m.length);
            }
        }
        pthread_mutex_unlock(&g_lock);
    }
    ktxTexture_Destroy1(t);
}

// DDS (gli). A gli::texture, as BRUTAL's libgli builds it: a vtable, the shared_ptr to its storage (the object, then
// the control block, whose use count less one follows its vtable), and a cache of where each image starts, a
// std::vector of pointers into the storage at 0x70 (data() returns its first). The storage holds the pixels in a
// std::vector<char>, found by value. Both are pointed at the mapping; when the storage's last owner is released, its
// vector is emptied first, so it frees nothing, and the mapping goes.
struct gliVector { uint8_t* begin; uint8_t* end; uint8_t* capacity; };

static void** gliSlots(gliTexture* t) { return (void**)t; }

static struct gliVector* gliStorageVector(void* storage, uint8_t* data, size_t size) {
    uint8_t** slots = (uint8_t**)storage;
    for (int i = 0; i < 10; i++)
        if (slots[i] == data && slots[i + 1] == data + size && slots[i + 2] >= data + size)
            return (struct gliVector*)&slots[i];
    return NULL;
}

static gliTexture* shim_load(const char* filename) {
    gliTexture* t = load(filename);
    if (!t || !enabled() || !filename) return t;
    uint8_t* pixels = data(t);
    int64_t bytes = size(t);
    if (!pixels || bytes < (int64_t)MIN_BYTES) return t;
    void* storage = gliSlots(t)[1];
    struct gliVector* addresses = (struct gliVector*)&gliSlots(t)[0x70 / sizeof(void*)];
    struct gliVector* vector = storage ? gliStorageVector(storage, pixels, (size_t)bytes) : NULL;
    // The cache holds an image start for each layer, face and level (the faces' and levels' counts just before it)
    size_t images = (size_t)(addresses->end - addresses->begin) / sizeof(uint8_t*);
    size_t faces = (size_t)gliSlots(t)[0x60 / sizeof(void*)], levels = (size_t)gliSlots(t)[0x68 / sizeof(void*)];
    if (!vector || images == 0 || faces == 0 || levels == 0 || images % (faces * levels) != 0 ||
        *(uint8_t**)addresses->begin != pixels)
        return t;
    // Every image's start must lie in the pixels before any is moved
    for (uint8_t** a = (uint8_t**)addresses->begin; a < (uint8_t**)addresses->end; a++)
        if (*a < pixels || *a >= pixels + bytes) return t;
    void* base;
    size_t mapped;
    bool fromGame = true;
    uint8_t* p = mapFileTail(filename, pixels, (size_t)bytes, &base, &mapped);
    if (!p) {
        fromGame = false;
        p = mapCacheFile(filename, pixels, (size_t)bytes, &base, &mapped);
    }
    if (!p) return t;
    pthread_mutex_lock(&g_lock);
    if (!reserve()) {
        pthread_mutex_unlock(&g_lock);
        munmap(base, mapped);
        return t;
    }
    track(GLI, storage, p, (size_t)bytes, base, mapped);
    for (uint8_t** a = (uint8_t**)addresses->begin; a < (uint8_t**)addresses->end; a++) *a = p + (*a - pixels);
    vector->begin = p;
    vector->end = p + bytes;
    vector->capacity = p + bytes;
    free(pixels);
    counted((size_t)bytes, fromGame);
    pthread_mutex_unlock(&g_lock);
    return t;
}

// Every release runs under the lock, so a texture sharing the storage can't drop its count between the check and the
// release
static void shim_release(gliTexture* t) {
    struct mapping m = {0};
    bool unmap = false;
    pthread_mutex_lock(&g_lock);
    if (t) {
        void* storage = gliSlots(t)[1];
        long* control = (long*)gliSlots(t)[2];
        // The last owner of the storage (no other texture shares it): its vector frees nothing
        if (storage && control && control[1] == 0 && untrack(GLI, storage, &m)) {
            struct gliVector* vector = gliStorageVector(storage, m.data, m.size);
            if (vector) {
                *vector = (struct gliVector){0};
                unmap = true;
            }
        }
    }
    release(t);
    pthread_mutex_unlock(&g_lock);
    if (unmap) munmap(m.base, m.length);
}

// PNG and JPEG (stb). stb's pixels are one block that stbi_image_free frees. Decoded once, they go to the cache with
// a header of their size; a later load of the same file finds the copy and maps it without decoding.
struct stbHeader { char magic[4]; int32_t x, y, channels; };

static const size_t StbBytes[] = {1, 2, 4};  // stbi_load, stbi_load_16, stbi_loadf

static void* stbMapCopy(const char* file, int kind, int desired, int* x, int* y, int* channels, void** base,
                        size_t* mapped) {
    int fd = open(file, O_RDONLY);
    if (fd < 0) return NULL;
    struct stbHeader h;
    struct stat st;
    void* p = NULL;
    if (pread(fd, &h, sizeof h, 0) == (ssize_t)sizeof h && !memcmp(h.magic, "KSTB", 4) && h.x > 0 && h.y > 0 &&
        h.channels > 0 && h.channels <= 4 && fstat(fd, &st) == 0 &&
        (size_t)st.st_size == sizeof h + (size_t)h.x * h.y * (desired ? desired : h.channels) * StbBytes[kind]) {
        uint8_t* q = mapFile(fd, 0, (size_t)st.st_size, base, mapped);
        if (q) {
            p = q + sizeof h;
            *x = h.x;
            *y = h.y;
            if (channels) *channels = h.channels;
            futimes(fd, NULL);  // used now
        }
    }
    close(fd);
    return p;
}

static void* stbDecode(int kind, const char* filename, int* x, int* y, int* channels, int desired) {
    switch (kind) {
        case 0: return stbi_load(filename, x, y, channels, desired);
        case 1: return stbi_load_16(filename, x, y, channels, desired);
        default: return stbi_loadf(filename, x, y, channels, desired);
    }
}

static void* stbLoad(int kind, const char* filename, int* x, int* y, int* channels, int desired) {
    if (!enabled() || !filename || !x || !y || desired < 0 || desired > 4)
        return stbDecode(kind, filename, x, y, channels, desired);
    char file[1100];
    bool cached = cachePath(filename, 0x5354420000000000ull | (uint64_t)kind << 8 | (uint64_t)desired, file,
                            sizeof file);
    void* base;
    size_t mapped;
    void* p = cached ? stbMapCopy(file, kind, desired, x, y, channels, &base, &mapped) : NULL;
    if (p) {
        size_t bytes = mapped - sizeof(struct stbHeader);
        pthread_mutex_lock(&g_lock);
        if (!reserve()) {
            pthread_mutex_unlock(&g_lock);
            munmap(base, mapped);
            return stbDecode(kind, filename, x, y, channels, desired);
        }
        track(STB, p, p, bytes, base, mapped);
        counted(bytes, false);
        pthread_mutex_unlock(&g_lock);
        return p;
    }
    int found = 0;
    uint8_t* pixels = stbDecode(kind, filename, x, y, &found, desired);
    if (channels) *channels = found;
    if (!pixels || !cached) return pixels;
    size_t bytes = (size_t)*x * *y * (desired ? desired : found) * StbBytes[kind];
    if (bytes < MIN_BYTES) return pixels;
    struct stbHeader h = {{'K', 'S', 'T', 'B'}, *x, *y, found};
    int fd = writeCacheFile(file, &h, sizeof h, pixels, bytes);
    if (fd < 0) return pixels;
    uint8_t* q = mapFile(fd, 0, sizeof h + bytes, &base, &mapped);
    close(fd);
    if (!q) return pixels;
    pthread_mutex_lock(&g_lock);
    if (!reserve()) {
        pthread_mutex_unlock(&g_lock);
        munmap(base, mapped);
        return pixels;
    }
    track(STB, q + sizeof h, q + sizeof h, bytes, base, mapped);
    counted(bytes, false);
    pthread_mutex_unlock(&g_lock);
    stbi_image_free(pixels);
    return q + sizeof h;
}

static unsigned char* shim_stbi_load(const char* f, int* x, int* y, int* c, int d) { return stbLoad(0, f, x, y, c, d); }
static uint16_t* shim_stbi_load_16(const char* f, int* x, int* y, int* c, int d) { return stbLoad(1, f, x, y, c, d); }
static float* shim_stbi_loadf(const char* f, int* x, int* y, int* c, int d) { return stbLoad(2, f, x, y, c, d); }

static void shim_stbi_image_free(void* pixels) {
    struct mapping m;
    pthread_mutex_lock(&g_lock);
    bool mine = pixels && untrack(STB, pixels, &m);
    pthread_mutex_unlock(&g_lock);
    if (mine) munmap(m.base, m.length);
    else stbi_image_free(pixels);
}

DYLD_INTERPOSE(shim_ktxTexture_CreateFromNamedFile1, ktxTexture_CreateFromNamedFile1)
DYLD_INTERPOSE(shim_ktxTexture_Destroy1, ktxTexture_Destroy1)
DYLD_INTERPOSE(shim_load, load)
DYLD_INTERPOSE(shim_release, release)
DYLD_INTERPOSE(shim_stbi_load, stbi_load)
DYLD_INTERPOSE(shim_stbi_load_16, stbi_load_16)
DYLD_INTERPOSE(shim_stbi_loadf, stbi_loadf)
DYLD_INTERPOSE(shim_stbi_image_free, stbi_image_free)
