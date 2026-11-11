/*
 * KSA - the app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by the port's program
 * (src/player/port-cli/App.cs) from the port folder.
 *
 * Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS, in its own process, as .NET's app
 * host does (KSA in the Linux release, KSA.exe on Windows): the game's process is the app's executable, named KSA, from
 * its start, which Game Mode needs, and tools that look for the game by name find it. It sets:
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
 * and then starts itself again (KSA_PORT_HOSTED, as macOS's loader and allocator read their variables only when a
 * process starts; KSA <program.dll> [arguments], what ps shows) and runs the program with .NET's host library
 * (Contents/MacOS/host/fxr/<version>/libhostfxr.dylib, hostfxr_main_startupinfo, what the app host calls), in its
 * folder.
 *
 * KSA <program.dll> [arguments] runs that program instead, in its own folder, with the same environment and runtime: a
 * mod loader that loads the game itself (StarMap, as Borea starts it), in a process named KSA too. It stands in for
 * dotnet <program.dll> [arguments]. Contents/Resources/KSA, a link to this program, is the game folder's executable, as
 * KSA is in the Linux release's: Borea starts the game and its loaders through it.
 */
#include <CoreFoundation/CoreFoundation.h>
#include <dirent.h>
#include <dlfcn.h>
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

// The newer of two versions (10.0.13), by their numbers; of the same numbers, a release over a preview (10.0.0-rc.1)
static bool newer(const char* a, const char* b) {
    int x[3] = {0}, y[3] = {0};
    sscanf(a, "%d.%d.%d", &x[0], &x[1], &x[2]);
    sscanf(b, "%d.%d.%d", &y[0], &y[1], &y[2]);
    for (int i = 0; i < 3; i++)
        if (x[i] != y[i]) return x[i] > y[i];
    return !strchr(a, '-') && strchr(b, '-');
}

// .NET's host library in the app, of its newest version: host/fxr/<version>/libhostfxr.dylib
static bool findHostfxr(const char* macos, char* fxr, size_t size) {
    char folder[PATH_MAX], version[NAME_MAX + 1] = "";
    snprintf(folder, sizeof folder, "%s/host/fxr", macos);
    DIR* versions = opendir(folder);
    if (!versions) return false;
    for (struct dirent* entry; (entry = readdir(versions));)
        if (entry->d_name[0] != '.' && (!*version || newer(entry->d_name, version)))
            strlcpy(version, entry->d_name, sizeof version);
    closedir(versions);
    snprintf(fxr, size, "%s/%s/libhostfxr.dylib", folder, version);
    return *version && isFile(fxr);
}

// What .NET's host library reports when it can't start the program (a missing framework, a broken
// runtimeconfig.json): to the terminal, as it does itself, and kept for the alert
static char hostErrors[2048];
static void hostError(const char* message) {
    fprintf(stderr, "%s\n", message);
    strlcat(hostErrors, message, sizeof hostErrors);
    strlcat(hostErrors, "\n", sizeof hostErrors);
}

// The second start, argv = this program, the program to run, its arguments: the environment is in place. Runs the
// program in this process with .NET's host library, as the app host does (argv[0] the host, then the program's
// arguments; the program as app_path), and quits with its exit code
typedef int (*HostfxrMain)(int argc, const char** argv, const char* hostPath, const char* dotnetRoot,
                           const char* appPath);
typedef void (*HostfxrErrorWriter)(const char* message);
typedef HostfxrErrorWriter (*HostfxrSetErrorWriter)(HostfxrErrorWriter writer);
static void run(const char* self, const char* macos, int argc, char** argv) {
    char fxr[PATH_MAX];
    if (argc < 2 || !findHostfxr(macos, fxr, sizeof fxr))
        fail("The .NET runtime is missing from the app. Run ./port.sh in the port folder (ksa-macos-port).");
    void* library = dlopen(fxr, RTLD_NOW | RTLD_LOCAL);
    HostfxrMain start = library ? (HostfxrMain)dlsym(library, "hostfxr_main_startupinfo") : NULL;
    if (!start) fail("The app's .NET runtime could not be started. Run ./port.sh in the port folder (ksa-macos-port).");
    HostfxrSetErrorWriter setErrorWriter = (HostfxrSetErrorWriter)dlsym(library, "hostfxr_set_error_writer");
    if (setErrorWriter) setErrorWriter(hostError);
    const char* program = argv[1];
    argv[1] = (char*)self;
    int status = start(argc - 1, (const char**)argv + 1, self, macos, program);
    if (status != 0 && hostErrors[0]) fail(hostErrors);
    exit(status);
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

    // The second start, by this program (the marker is this process's id, which the start again keeps): the marker
    // goes, so that nothing the game starts inherits it
    const char* hosted = getenv("KSA_PORT_HOSTED");
    if (hosted && atoi(hosted) == (int)getpid()) {
        unsetenv("KSA_PORT_HOSTED");
        run(self, macos, argc, argv);
    }

    // Started again by the hooks (Restart) while the game was still running: begin once it has quit
    const char* wait = getenv("KSA_PORT_WAIT");
    if (wait && *wait) {
        pid_t pid = (pid_t)atoi(wait);
        while (pid > 0 && kill(pid, 0) == 0)
            usleep(200000);
        unsetenv("KSA_PORT_WAIT");
    }

    char fxr[PATH_MAX], game[PATH_MAX];
    snprintf(game, sizeof game, "%s/KSA.dll", res);
    if (!findHostfxr(macos, fxr, sizeof fxr))
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
    // game. The arguments follow, without the process serial number older macOS versions pass to apps.
    char program[PATH_MAX];
    int first = 1;
    while (first < argc && strncmp(argv[first], "-psn_", 5) == 0)
        first++;
    bool given = first < argc && isProgram(argv[first]);
    if (given) {
        if (!isFile(argv[first]) || !realpath(argv[first], program)) fail("The program to start is not there.");
        first++;
    } else {
        strlcpy(program, game, sizeof program);
    }
    // In its folder
    char folder[PATH_MAX];
    strlcpy(folder, program, sizeof folder);
    *strrchr(folder, '/') = 0;
    if (chdir(folder) != 0)
        fail(given ? "The folder of the program to start can't be opened."
                   : "The game folder can't be opened. Convert the game again with ./port.sh.");
    setenv("KSA_PORT_PROGRAM", program, 1);

    // This program again, with the program and its arguments, which ps shows
    const char** arguments = calloc((size_t)argc + 2, sizeof *arguments);
    int count = 0;
    arguments[count++] = self;
    arguments[count++] = program;
    for (int i = first; i < argc; i++)
        if (strncmp(argv[i], "-psn_", 5) != 0) arguments[count++] = argv[i];
    char marker[16];
    snprintf(marker, sizeof marker, "%d", (int)getpid());
    setenv("KSA_PORT_HOSTED", marker, 1);
    execv(self, (char* const*)arguments);
    fail("The game could not be started. Run ./port.sh in the port folder (ksa-macos-port).");
}
