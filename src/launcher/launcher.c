/*
 * KSA - the app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by lib/make-app.sh from the
 * port folder.
 *
 * Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS. As a program of the app, it makes
 * macOS treat the game as the app from its start, which Game Mode needs. It sets:
 *   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
 *                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
 *   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c) and the KTX shim (src/ktx-shim/ktx_shim.c)
 *   DOTNET_STARTUP_HOOKS   KSA Metal Hooks' loader (src/hooks/loader/), which applies the player's port settings
 *                          (port-settings.toml in the game's documents folder) and puts the game's output in
 *                          console.log, before the game starts
 *   KSA_PORT_LAUNCHER      this program, for the hooks to find the app and start the game again
 * and then becomes dotnet KSA.dll, so the game runs in this process.
 *
 * With the StarMap mod loader in the app (starmap.sh), the game starts through it; KSA_STARMAP_DIR names another
 * StarMap folder to use instead.
 */
#include <CoreFoundation/CoreFoundation.h>
#include <limits.h>
#include <mach-o/dyld.h>
#include <signal.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
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
    snprintf(path, sizeof path, "%s/Hooks/KsaMetalHooksLoader.dll", res);
    if (isFile(path)) prepend("DOTNET_STARTUP_HOOKS", path);

    // dotnet and the program, then the arguments, without the process serial number older macOS versions pass to
    // apps
    const char** arguments = calloc((size_t)argc + 2, sizeof *arguments);
    int count = 0;
    arguments[count++] = dotnet;
    arguments[count++] = "KSA.dll";
    for (int i = 1; i < argc; i++)
        if (strncmp(argv[i], "-psn_", 5) != 0) arguments[count++] = argv[i];

    char starMap[PATH_MAX];
    const char* starMapDir = getenv("KSA_STARMAP_DIR");
    if (starMapDir && *starMapDir)
        snprintf(starMap, sizeof starMap, "%s/StarMap.dll", starMapDir);
    else
        snprintf(starMap, sizeof starMap, "%s/StarMap/StarMap.dll", res);
    if (isFile(starMap)) {
        *strrchr(starMap, '/') = 0;
        if (chdir(starMap) != 0) fail("The StarMap folder can't be opened. Add StarMap again with ./starmap.sh.");
        writeStarMapConfig(game);
        arguments[1] = "StarMap.dll";
    } else if (chdir(res) != 0) {
        fail("The game folder can't be opened. Convert the game again with ./port.sh.");
    }

    execv(dotnet, (char* const*)arguments);
    fail("The app's .NET runtime could not be started. Run ./port.sh in the port folder (ksa-macos-port).");
}
