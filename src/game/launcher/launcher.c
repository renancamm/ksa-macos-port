/*
 * KSA - the app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by the port's program
 * (src/player/port-cli/App.cs) from the port folder.
 *
 * Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS. As a program of the app, it makes
 * macOS treat the game as the app from its start, which Game Mode needs. It sets:
 *   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
 *                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
 *   DYLD_INSERT_LIBRARIES  the GLFW shim (src/game/shims/glfw/glfw_shim.c) and the KTX shim
 *                          (src/game/shims/ktx/ktx_shim.c)
 *   DOTNET_STARTUP_HOOKS   KSA Metal Hooks' loader (src/game/hooks/loader/), which applies the player's port settings
 *                          (port-settings.toml in the game's documents folder) and puts the game's output in
 *                          console.log, before the game starts
 *   KSA_PORT_LAUNCHER      this program, for the hooks to find the app and start the game again
 *   KSA_PORT_PROGRAM       the program it starts, for the hooks' loader to know the game's process
 *   MallocLargeCache=0     with Release Freed Memory on (release_freed_memory, read from port-settings.toml here,
 *                          as macOS's allocator reads its variables when the process starts, before the hooks)
 * and then becomes dotnet KSA.dll in the game folder, so the game runs in this process.
 *
 * KSA <program.dll> [arguments] runs that program instead, in its own folder, with the same environment and runtime: a
 * mod loader that loads the game itself (StarMap, as Borea starts it). It stands in for dotnet <program.dll>
 * [arguments]. Contents/Resources/KSA, a link to this program, is the game folder's executable, as KSA is in the Linux
 * release's. Without a program, the game starts through the StarMap mod loader when the app has it (starmap.sh);
 * KSA_STARMAP_DIR names another StarMap folder to use instead.
 */
#include <CoreFoundation/CoreFoundation.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <stdint.h>
#include <sys/stat.h>
#include <sys/sysctl.h>
#include <unistd.h>

// Tells the player in an alert (and the terminal, when there is one), then quits
static void fail(const char* message) {
    printf("ERROR: %s\n", message);
    fflush(stdout);
    CFStringRef text = CFStringCreateWithCString(NULL, message, kCFStringEncodingUTF8);
    CFUserNotificationDisplayAlert(0, kCFUserNotificationStopAlertLevel, NULL, NULL, NULL,
        CFSTR("Kitten Space Agency"), text, NULL, NULL, NULL, NULL);
    CFRelease(text);
    exit(1);
}

static bool isFile(const char* path) {
    struct stat st;
    return stat(path, &st) == 0 && S_ISREG(st.st_mode);
}

// Whether a list separated by ':' holds the item
static bool listHas(const char* list, const char* item) {
    size_t length = strlen(item);
    for (const char* p = list; *p; p += strcspn(p, ":"), p += *p == ':')
        if (strncmp(p, item, length) == 0 && (p[length] == ':' || p[length] == 0)) return true;
    return false;
}

// Puts the item first in the variable's list, unless the list holds it already
static void prepend(const char* name, const char* item) {
    const char* list = getenv(name);
    if (list && *list && listHas(list, item)) return;
    size_t size = strlen(item) + (list ? strlen(list) : 0) + 2;
    char* value = malloc(size);
    snprintf(value, size, "%s%s%s", item, list && *list ? ":" : "", list && *list ? list : "");
    setenv(name, value, 1);
    free(value);
}

// Release Freed Memory (release_freed_memory). macOS's allocator keeps the blocks of 128 KB and more that a program
// frees, dirty and counted as its memory (Sol: about 0.8 GB, swapped out like any other), for the next allocation of
// their size; MallocLargeCache=0, read when the process starts, has it give them back at once. On: its variable when
// set (1), otherwise the line in port-settings.toml (the game's documents folder, as the hooks' loader finds it);
// with no file yet, a fresh install, what Auto Memory Saving is about to write: on with 16 GB or less.
static bool releaseFreedMemory(void) {
    const char* variable = getenv("KSA_MTL_RELEASE_FREED_MEMORY");
    if (variable) return strcmp(variable, "1") == 0;  // set outside the game, even empty: the loader leaves it so too
    char file[PATH_MAX];
    const char* instance = getenv("STARMAP_INSTANCE_PATH");
    const char* home = getenv("HOME");
    if (instance && *instance) snprintf(file, sizeof file, "%s/port-settings.toml", instance);
    else if (home) snprintf(file, sizeof file, "%s/Documents/My Games/Kitten Space Agency/port-settings.toml", home);
    else return false;
    FILE* settings = fopen(file, "r");
    if (!settings) {
        // This Mac's memory in GB, rounded, as Auto Memory Saving takes it (KSA_PORT_MEMORY_GB stands in for it)
        const char* stand = getenv("KSA_PORT_MEMORY_GB");
        uint64_t memory = 0;
        size_t size = sizeof memory;
        long gb = stand && atol(stand) > 0 ? atol(stand)
                : sysctlbyname("hw.memsize", &memory, &size, NULL, 0) == 0 ? (long)((memory + (1ull << 29)) >> 30) : 0;
        return gb > 0 && gb <= 16;
    }
    bool on = false;
    char line[512];
    static const char key[] = "release_freed_memory";
    while (fgets(line, sizeof line, settings)) {
        const char* p = line + strspn(line, " \t");
        if (strncmp(p, key, sizeof key - 1) != 0) continue;
        p += sizeof key - 1;
        p += strspn(p, " \t");
        if (*p != '=') continue;
        p += 1 + strspn(p + 1, " \t");
        // A value the loader takes: true or false, quoted (what follows ignored) or followed by nothing but a comment; a
        // later valid line wins
        char quote = *p == '"' || *p == '\'' ? *p++ : 0;
        size_t length = strncmp(p, "true", 4) == 0 ? 4 : strncmp(p, "false", 5) == 0 ? 5 : 0;
        if (!length || (quote && p[length] != quote)) continue;
        const char* rest = p + length + (quote ? 1 : 0);
        rest += strspn(rest, " \t\r\n");
        if (quote || *rest == 0 || *rest == '#') on = length == 4;
    }
    fclose(settings);
    return on;
}

// Whether the argument names a program for the runtime: a .dll
static bool isProgram(const char* argument) {
    size_t length = strlen(argument);
    return length > 4 && strcasecmp(argument + length - 4, ".dll") == 0;
}

// StarMap reads its config from the current folder and needs the game's full path, which changes when the app
// moves: the config is written at every start
static void writeStarMapConfig(const char* game) {
    FILE* config = fopen("StarMapConfig.json", "w");
    if (!config) return;
    fputs("{ \"GameLocation\": \"", config);
    for (const char* c = game; *c; c++) {
        if (*c == '"' || *c == '\\') fputc('\\', config);
        fputc(*c, config);
    }
    fputs("\", \"RepositoryLocation\": \"\", \"GameArguments\": [] }\n", config);
    fclose(config);
}

int main(int argc, char** argv) {
    char executable[PATH_MAX], self[PATH_MAX];
    uint32_t size = sizeof executable;
    if (_NSGetExecutablePath(executable, &size) != 0 || !realpath(executable, self))
        fail("The app's location is unknown.");
    char macos[PATH_MAX], contents[PATH_MAX], res[PATH_MAX];
    strlcpy(macos, self, sizeof macos);
    *strrchr(macos, '/') = 0;
    strlcpy(contents, macos, sizeof contents);
    *strrchr(contents, '/') = 0;
    snprintf(res, sizeof res, "%s/Resources", contents);

    // Started again by the hooks (Restart) while the game was still running: begin once it has quit
    const char* wait = getenv("KSA_PORT_WAIT");
    if (wait && *wait) {
        pid_t pid = (pid_t)atoi(wait);
        while (pid > 0 && kill(pid, 0) == 0)
            usleep(200000);
        unsetenv("KSA_PORT_WAIT");
    }

    char dotnet[PATH_MAX], game[PATH_MAX];
    snprintf(dotnet, sizeof dotnet, "%s/dotnet", macos);
    snprintf(game, sizeof game, "%s/KSA.dll", res);
    if (access(dotnet, X_OK) != 0)
        fail("The .NET runtime is missing from the app. Run ./port.sh in the port folder (ksa-macos-port).");
    if (!isFile(game)) fail("The game files are missing from the app. Convert the game again with ./port.sh.");

    char path[PATH_MAX];
    snprintf(path, sizeof path, "%s/KSA", macos);
    setenv("KSA_PORT_LAUNCHER", path, 1);
    setenv("DOTNET_ROOT", macos, 1);
    prepend("PATH", macos);
    setenv("DYLD_LIBRARY_PATH", res, 1);
    snprintf(path, sizeof path, "%s/libGlfwShim.dylib:%s/libKtxShim.dylib", res, res);
    setenv("DYLD_INSERT_LIBRARIES", path, 1);
    snprintf(path, sizeof path, "%s/Hooks/KsaMetal.Hooks.Loader.dll", res);
    if (isFile(path)) prepend("DOTNET_STARTUP_HOOKS", path);
    // Off: also when a game started again (Restart) hands this one its environment
    if (releaseFreedMemory()) setenv("MallocLargeCache", "0", 1);
    else unsetenv("MallocLargeCache");

    // The program: one given as the first argument (a mod loader that loads the game, as Borea starts StarMap), else the
    // app's StarMap, else the game. The arguments follow, without the process serial number older macOS versions pass
    // to apps.
    char program[PATH_MAX];
    int first = 1;
    while (first < argc && strncmp(argv[first], "-psn_", 5) == 0)
        first++;
    // The app's StarMap, which reads its config from its folder: written at every start, also when it is the program
    // given (the game's Restart starts the program it ran)
    char appStarMap[PATH_MAX], found[PATH_MAX];
    const char* starMapDir = getenv("KSA_STARMAP_DIR");
    if (starMapDir && *starMapDir)
        snprintf(found, sizeof found, "%s/StarMap.dll", starMapDir);
    else
        snprintf(found, sizeof found, "%s/StarMap/StarMap.dll", res);
    bool hasStarMap = isFile(found) && realpath(found, appStarMap);
    bool given = first < argc && isProgram(argv[first]), starMap;
    if (given) {
        if (!isFile(argv[first]) || !realpath(argv[first], program)) fail("The program to start is not there.");
        first++;
        starMap = hasStarMap && strcmp(program, appStarMap) == 0;
    } else {
        starMap = hasStarMap;
        strlcpy(program, starMap ? appStarMap : game, sizeof program);
    }
    // Started by its file name in its folder, as dotnet KSA.dll: .NET still gives the program its full path
    char folder[PATH_MAX];
    strlcpy(folder, program, sizeof folder);
    char* name = strrchr(folder, '/');
    *name++ = 0;
    if (chdir(folder) != 0)
        fail(given     ? "The folder of the program to start can't be opened."
             : starMap ? "The StarMap folder can't be opened. Add StarMap again with ./starmap.sh."
                       : "The game folder can't be opened. Convert the game again with ./port.sh.");
    if (starMap) writeStarMapConfig(game);
    setenv("KSA_PORT_PROGRAM", program, 1);

    const char** arguments = calloc((size_t)argc + 2, sizeof *arguments);
    int count = 0;
    arguments[count++] = dotnet;
    arguments[count++] = name;
    for (int i = first; i < argc; i++)
        if (strncmp(argv[i], "-psn_", 5) != 0) arguments[count++] = argv[i];

    execv(dotnet, (char* const*)arguments);
    fail("The app's .NET runtime could not be started. Run ./port.sh in the port folder (ksa-macos-port).");
}
