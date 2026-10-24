/*
 * libKtxShim.dylib - keeps the pixels of KSA's textures in memory that macOS can page out without swapping.
 *
 * KSA keeps a copy of every texture's pixels in memory for the whole session (TextureAsset: the terrain's height
 * queries, the texture streamer's uploads), about 3.5 GB with the solar system. libktx reads them into memory of
 * its own (malloc), which macOS can only compress or write to swap when memory runs short. The launcher loads this
 * shim with DYLD_INSERT_LIBRARIES; it replaces libktx's create and destroy calls (through dyld interposing, libktx
 * stays unmodified) and, once libktx has loaded a texture of 1 MB or more, swaps its pixels for the same bytes
 * mapped from a file:
 *
 * - the game's own file, when the texture is stored uncompressed and its bytes are the file's (compared);
 * - otherwise a copy written once to textures/ in KSA Metal's cache folder (~/Library/Caches/ksa-metal, or
 *   KSA_MTL_CACHE_DIR), named after the file's path, size and date (a zstd-compressed texture is decompressed there
 *   once). A copy is dated when used; copies unused for 60 days (from an older version of the game) are deleted when
 *   the game starts.
 *
 * Mapped pages are the file's: macOS drops them when memory runs short and reads them back when the game touches
 * them again, instead of swapping them. The mapping is private and writable, so a write changes only this process's
 * copy of the page. Textures that need transcoding (Basis) stay as libktx made them. KSA_KTX_SHIM=0 turns it off.
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

#define MIN_BYTES (1u << 20)

struct mapping {
    ktxTexture* texture;
    uint8_t* data;  // what pData points to
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
        const char* e = getenv("KSA_KTX_SHIM");
        g_enabled = !(e && !strcmp(e, "0"));
    }
    return g_enabled;
}

// Where in the texture object libktx keeps pData (and dataSize just before it): found by value, so the shim
// depends on no struct layout
static uint8_t** dataField(ktxTexture* t, uint8_t* data, size_t size) {
    uint8_t** slots = (uint8_t**)t;
    for (int i = 1; i < 32; i++)
        if (slots[i] == data && ((size_t*)slots)[i - 1] == size) return &slots[i];
    return NULL;
}

// A private, writable mapping of length bytes of fd from offset; the pointer to offset (base and mapped length out)
static uint8_t* mapFile(int fd, off_t offset, size_t length, void** base, size_t* mapped) {
    off_t page = offset & ~(off_t)(getpagesize() - 1);
    *mapped = length + (size_t)(offset - page);
    *base = mmap(NULL, *mapped, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd, page);
    if (*base == MAP_FAILED) return NULL;
    return (uint8_t*)*base + (offset - page);
}

// The game's file, when it is an uncompressed KTX2 whose data region holds exactly these bytes
static uint8_t* mapGameFile(const char* path, const uint8_t* data, size_t size, void** base, size_t* mapped) {
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
        if (scheme == 0 && start != UINT64_MAX && fstat(fd, &st) == 0 && start + size <= (uint64_t)st.st_size) {
            uint8_t* p = mapFile(fd, (off_t)start, size, base, mapped);
            if (p && !memcmp(p, data, size)) result = p;
            else if (p) munmap(*base, *mapped);
        }
    }
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

static char g_cacheDir[1024];
static pthread_once_t g_cleanOnce = PTHREAD_ONCE_INIT;
static void cleanOnce(void) { cleanCache(g_cacheDir); }

// The cache's copy (written now if missing), named after the file's path, size and date and the data's size
static uint8_t* mapCacheFile(const char* path, const uint8_t* data, size_t size, void** base, size_t* mapped) {
    struct stat st;
    if (stat(path, &st) != 0) return NULL;
    uint64_t h = 1469598103934665603ull;  // FNV-1a
    for (const char* c = path; *c; c++) h = (h ^ (uint8_t)*c) * 1099511628211ull;
    h = (h ^ (uint64_t)st.st_size) * 1099511628211ull;
    h = (h ^ (uint64_t)st.st_mtimespec.tv_sec) * 1099511628211ull;
    h = (h ^ (uint64_t)size) * 1099511628211ull;
    // KSA Metal's cache folder, like the driver picks it (shadercache.mm)
    char parent[1024], dir[1024], file[1100], temp[1200];
    const char* custom = getenv("KSA_MTL_CACHE_DIR");
    const char* home = getenv("HOME");
    if (custom && *custom) snprintf(parent, sizeof parent, "%s", custom);
    else if (home) snprintf(parent, sizeof parent, "%s/Library/Caches/ksa-metal", home);
    else return NULL;
    snprintf(dir, sizeof dir, "%s/textures", parent);
    snprintf(file, sizeof file, "%s/%016llx", dir, (unsigned long long)h);
    snprintf(g_cacheDir, sizeof g_cacheDir, "%s", dir);
    pthread_once(&g_cleanOnce, cleanOnce);
    int fd = open(file, O_RDONLY);
    struct stat cs;
    if (fd >= 0 && (fstat(fd, &cs) != 0 || (size_t)cs.st_size != size)) {
        close(fd);
        fd = -1;
    }
    if (fd < 0) {
        mkdir(parent, 0755);
        mkdir(dir, 0755);
        snprintf(temp, sizeof temp, "%s.%d.%p", file, getpid(), (const void*)data);
        int out = open(temp, O_WRONLY | O_CREAT | O_TRUNC, 0644);
        if (out < 0) return NULL;
        size_t done = 0;
        while (done < size) {
            ssize_t n = write(out, data + done, size - done);
            if (n <= 0) break;
            done += (size_t)n;
        }
        close(out);
        if (done != size || rename(temp, file) != 0) {
            unlink(temp);
            return NULL;
        }
        fd = open(file, O_RDONLY);
        if (fd < 0) return NULL;
    }
    futimes(fd, NULL);  // used now
    uint8_t* p = mapFile(fd, 0, size, base, mapped);
    close(fd);
    return p;
}

static void track(ktxTexture* t, uint8_t* data, size_t size, void* base, size_t length) {
    if (g_count == g_capacity) {
        size_t capacity = g_capacity ? g_capacity * 2 : 256;
        struct mapping* maps = realloc(g_maps, capacity * sizeof *maps);
        if (!maps) return;
        g_maps = maps;
        g_capacity = capacity;
    }
    g_maps[g_count++] = (struct mapping){t, data, size, base, length};
}

static int shim_ktxTexture_CreateFromNamedFile1(const char* filename, uint32_t flags, ktxTexture** texture) {
    int result = ktxTexture_CreateFromNamedFile1(filename, flags, texture);
    if (result != 0 || !enabled() || !texture || !*texture || !filename) return result;
    ktxTexture* t = *texture;
    uint8_t* data = ktxTexture_GetData1(t);
    size_t size = ktxTexture_GetDataSize1(t);
    if (!data || size < MIN_BYTES || ktxTexture_NeedsTranscoding1(t)) return result;
    uint8_t** field = dataField(t, data, size);
    if (!field) return result;
    void* base;
    size_t mapped;
    bool fromGame = true;
    uint8_t* p = mapGameFile(filename, data, size, &base, &mapped);
    if (!p) {
        fromGame = false;
        p = mapCacheFile(filename, data, size, &base, &mapped);
    }
    if (!p) return result;
    pthread_mutex_lock(&g_lock);
    track(t, p, size, base, mapped);
    *field = p;
    free(data);
    if (fromGame) g_fileBytes += size;
    else g_cacheBytes += size;
    if ((g_fileBytes + g_cacheBytes) >> 29 != g_logged) {
        g_logged = (g_fileBytes + g_cacheBytes) >> 29;
        printf("[ktx-shim] textures mapped: %llu MB from the game's files, %llu MB from the cache\n",
               (unsigned long long)(g_fileBytes >> 20), (unsigned long long)(g_cacheBytes >> 20));
        fflush(stdout);
    }
    pthread_mutex_unlock(&g_lock);
    return result;
}

static void shim_ktxTexture_Destroy1(ktxTexture* t) {
    if (t) {
        pthread_mutex_lock(&g_lock);
        for (size_t i = 0; i < g_count; i++) {
            if (g_maps[i].texture != t) continue;
            // libktx frees pData when it isn't NULL; if something replaced it since, leave both alone
            uint8_t** field = dataField(t, g_maps[i].data, g_maps[i].size);
            if (field) {
                *field = NULL;
                munmap(g_maps[i].base, g_maps[i].length);
            }
            g_maps[i] = g_maps[--g_count];
            break;
        }
        pthread_mutex_unlock(&g_lock);
    }
    ktxTexture_Destroy1(t);
}

DYLD_INTERPOSE(shim_ktxTexture_CreateFromNamedFile1, ktxTexture_CreateFromNamedFile1)
DYLD_INTERPOSE(shim_ktxTexture_Destroy1, ktxTexture_Destroy1)
