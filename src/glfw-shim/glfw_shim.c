/*
 * libGlfwShim.dylib - fixes a few things in how the GLFW that KSA ships (RocketWerkz's fork of GLFW
 * 3.5.1, from the Ahwoo.BRUTAL.Glfw package) and the game work together on macOS. The app's launcher
 * loads it with DYLD_INSERT_LIBRARIES, and it replaces a few calls through dyld interposing, so GLFW
 * itself stays unmodified.
 *
 * The fork already adds the desktop's (HiDPI) mode to the video mode list and keeps it when a
 * full screen window asks for its size. These are the fixes it does not have:
 *
 * 1. CGDisplayCopyAllDisplayModes: GLFW passes no options, so macOS leaves out the
 *    scaled HiDPI modes and the game's resolution list on a MacBook display holds only the
 *    desktop mode and the modes from 1920x1200 up. Ask for them with
 *    kCGDisplayShowDuplicateLowResolutionModes in GLFW's calls.
 * 2. glfwSetInputMode: KSA checks glfwRawMouseMotionSupported() before it turns raw mouse
 *    motion on, but not before it turns it off, and GLFW's Cocoa backend reports a
 *    PlatformError ("Raw mouse motion is not supported on this system") after every
 *    right-drag. Skip raw motion calls when it is unsupported.
 * 3. glfwSetWindowMonitor: in Fullscreen mode KSA always passes the desktop's current
 *    size, so the Resolution setting does nothing there.
 *    Use display.width/height from settings.toml instead, which makes GLFW switch the
 *    display to that mode. The game applies new settings before it saves them, so
 *    glfwPollEvents rereads the file whenever it changes and switches again.
 *    When the window leaves fullscreen, the game sizes it from the switched mode, so a
 *    size that matches that mode or its work area, or that the game cut to the work area
 *    of the switched mode (Windowed), is redone for the restored mode.
 * 4. Window positions: GLFW's Cocoa backend reports no move when glfwSetWindowMonitor
 *    moves and resizes a window at once, and reports a position from the middle of the
 *    switch when a window enters or leaves fullscreen. The game keeps the last reported
 *    position and its ImGui backend places the main viewport and the mouse with it, so
 *    after a mode change popups would open in the wrong spot, often on another monitor.
 *    After glfwSetWindowMonitor and every glfwPollEvents, report the real position of a
 *    window that went through glfwSetWindowMonitor when it differs from the last one.
 */
#include <CoreGraphics/CoreGraphics.h>
#include <GLFW/glfw3.h>
#include <dispatch/dispatch.h>
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#define DYLD_INTERPOSE(replacement, replacee) \
    __attribute__((used)) static struct { const void* replacement; const void* replacee; } \
    interpose_##replacee __attribute__((section("__DATA,__interpose"))) = \
    { (const void*)&replacement, (const void*)&replacee };

static int calledFromGlfw(const void* returnAddress) {
    Dl_info info;
    return dladdr(returnAddress, &info) && info.dli_fname && strstr(info.dli_fname, "libglfw");
}

static CFArrayRef shim_CGDisplayCopyAllDisplayModes(CGDirectDisplayID display, CFDictionaryRef options) {
    static CFDictionaryRef withLowResModes;
    static dispatch_once_t once;

    if (options == NULL && calledFromGlfw(__builtin_return_address(0))) {
        dispatch_once(&once, ^{
            const void* keys[] = { kCGDisplayShowDuplicateLowResolutionModes };
            const void* values[] = { kCFBooleanTrue };
            withLowResModes = CFDictionaryCreate(NULL, keys, values, 1,
                &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        });
        options = withLowResModes;
    }
    return CGDisplayCopyAllDisplayModes(display, options);
}

static void shim_glfwSetInputMode(GLFWwindow* window, int mode, int value) {
    if (mode == GLFW_RAW_MOUSE_MOTION && !glfwRawMouseMotionSupported())
        return;
    glfwSetInputMode(window, mode, value);
}

#define MAX_TRACKED_WINDOWS 64

struct trackedWindow {
    GLFWwindow* window;
    GLFWwindowposfun callback;  // the game's
    int x, y;                   // the last position the game was told
    int setMonitor;             // went through glfwSetWindowMonitor
};

static struct trackedWindow g_trackedWindows[MAX_TRACKED_WINDOWS];

static struct trackedWindow* findTracked(GLFWwindow* window) {
    for (int i = 0; i < MAX_TRACKED_WINDOWS; i++)
        if (g_trackedWindows[i].window == window)
            return &g_trackedWindows[i];
    return NULL;
}

static void reportPosition(GLFWwindow* window, int x, int y) {
    struct trackedWindow* tracked = findTracked(window);

    if (!tracked)
        return;
    tracked->x = x;
    tracked->y = y;
    if (tracked->callback)
        tracked->callback(window, x, y);
}

static GLFWwindowposfun shim_glfwSetWindowPosCallback(GLFWwindow* window, GLFWwindowposfun callback) {
    struct trackedWindow* tracked = findTracked(window);
    GLFWwindowposfun previous;

    if (!tracked) {
        if (!callback || !(tracked = findTracked(NULL)))
            return glfwSetWindowPosCallback(window, callback);
        memset(tracked, 0, sizeof(*tracked));
        tracked->window = window;
        glfwGetWindowPos(window, &tracked->x, &tracked->y);
    }
    previous = tracked->callback;
    tracked->callback = callback;
    glfwSetWindowPosCallback(window, callback ? reportPosition : NULL);
    return previous;
}

// Tells the game where a window really is when GLFW did not (see 4.)
static void updatePosition(struct trackedWindow* tracked) {
    int x, y;

    if (!tracked->callback)
        return;
    glfwGetWindowPos(tracked->window, &x, &y);
    if (x != tracked->x || y != tracked->y)
        reportPosition(tracked->window, x, y);
}

static void updatePositions(void) {
    for (int i = 0; i < MAX_TRACKED_WINDOWS; i++)
        if (g_trackedWindows[i].window && g_trackedWindows[i].setMonitor)
            updatePosition(&g_trackedWindows[i]);
}

enum displayMode { MODE_WINDOWED, MODE_BORDERLESS, MODE_FULLSCREEN };

struct displaySettings {
    enum displayMode mode;
    int width, height, refresh;
};

static GLFWwindow* g_fullscreenWindow;
static GLFWmonitor* g_fullscreenMonitor;
static int g_fullscreenWidth, g_fullscreenHeight, g_fullscreenRefresh;
// A window that left fullscreen with a size cut to the switched mode's work area
static GLFWwindow* g_windowedWindow;
static int g_windowedX, g_windowedY;
static struct timespec g_settingsMtime;

// GameSettings.SettingsFilePath: <SpecialFolder.Personal>/My Games/Kitten Space Agency, or the
// folder StarMap moved it to (STARMAP_INSTANCE_PATH, as the benchmark runs the game)
static const char* settingsPath(void) {
    static char path[1024];
    static dispatch_once_t once;

    dispatch_once(&once, ^{
        const char* instance = getenv("STARMAP_INSTANCE_PATH");
        const char* home = getenv("HOME");
        if (instance && *instance)
            snprintf(path, sizeof(path), "%s/settings.toml", instance);
        else if (home)
            snprintf(path, sizeof(path), "%s/Documents/My Games/Kitten Space Agency/settings.toml", home);
    });
    return path[0] ? path : NULL;
}

// Reads the [display] table, which Tomlet writes as plain "key = value" lines. Returns
// whether it has a resolution.
static int readDisplaySettings(struct displaySettings* settings) {
    const char* path = settingsPath();
    struct stat st;
    FILE* file;
    char line[512], mode[64];
    int inDisplay = 0;

    memset(settings, 0, sizeof(*settings));
    if (!path || stat(path, &st) != 0 || !(file = fopen(path, "r")))
        return 0;
    g_settingsMtime = st.st_mtimespec;

    while (fgets(line, sizeof(line), file)) {
        const char* p = line + strspn(line, " \t");
        if (*p == '[') {
            inDisplay = strncmp(p, "[display]", 9) == 0;
            continue;
        }
        if (!inDisplay)
            continue;
        if (sscanf(p, "width = %d", &settings->width) == 1 ||
            sscanf(p, "height = %d", &settings->height) == 1 ||
            sscanf(p, "refresh = %d", &settings->refresh) == 1)
            continue;
        if (sscanf(p, "mode = \"%63[^\"]\"", mode) == 1)
            settings->mode = strcmp(mode, "Fullscreen") == 0 ? MODE_FULLSCREEN :
                             strcmp(mode, "Borderless") == 0 ? MODE_BORDERLESS : MODE_WINDOWED;
    }
    fclose(file);
    return settings->width > 0 && settings->height > 0;
}

static int minInt(int a, int b) {
    return a < b ? a : b;
}

// Redoes GameSettings.ApplyTo's Windowed size (the Resolution, cut to the primary monitor's
// work area from the window position) with the restored mode's work area
static void resizeWindowed(GLFWwindow* window, int xpos, int ypos, const struct displaySettings* settings) {
    int workWidth, workHeight, width, height, newWidth, newHeight;

    glfwGetMonitorWorkarea(glfwGetPrimaryMonitor(), NULL, NULL, &workWidth, &workHeight);
    glfwGetWindowSize(window, &width, &height);
    newWidth = minInt(workWidth - xpos, settings->width);
    newHeight = minInt(workHeight - ypos, settings->height);
    if (newWidth > 0 && newHeight > 0 && (newWidth != width || newHeight != height))
        glfwSetWindowMonitor(window, NULL, xpos, ypos, newWidth, newHeight, 0);
}

static void setWindowMonitor(GLFWwindow* window, GLFWmonitor* monitor,
                             int xpos, int ypos, int width, int height, int refreshRate) {
    struct displaySettings settings;
    int modeX, modeY, modeWidth, modeHeight;
    int workX, workY, workWidth, workHeight;
    int primaryWidth, primaryHeight;
    const GLFWvidmode* mode;

    if (window == g_windowedWindow)
        g_windowedWindow = NULL;
    if (monitor) {
        if (readDisplaySettings(&settings) && settings.mode == MODE_FULLSCREEN) {
            width = settings.width;
            height = settings.height;
        }
        g_fullscreenWindow = window;
        g_fullscreenMonitor = monitor;
        g_fullscreenWidth = width;
        g_fullscreenHeight = height;
        g_fullscreenRefresh = refreshRate;
        glfwSetWindowMonitor(window, monitor, xpos, ypos, width, height, refreshRate);
        return;
    }
    if (window != g_fullscreenWindow || !(mode = glfwGetVideoMode(g_fullscreenMonitor))) {
        glfwSetWindowMonitor(window, monitor, xpos, ypos, width, height, refreshRate);
        return;
    }

    // Leaving fullscreen: the game computed this size from the mode GLFW switched to.
    glfwGetMonitorPos(g_fullscreenMonitor, &modeX, &modeY);
    modeWidth = mode->width;
    modeHeight = mode->height;
    glfwGetMonitorWorkarea(g_fullscreenMonitor, &workX, &workY, &workWidth, &workHeight);
    glfwGetMonitorWorkarea(glfwGetPrimaryMonitor(), NULL, NULL, &primaryWidth, &primaryHeight);
    glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);

    if (xpos == workX && ypos == workY && width == workWidth && height == workHeight) {
        glfwGetMonitorWorkarea(g_fullscreenMonitor, &xpos, &ypos, &width, &height);
        if (width != workWidth || height != workHeight)
            glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);
    } else if (xpos == modeX && ypos == modeY && width == modeWidth && height == modeHeight &&
               (mode = glfwGetVideoMode(g_fullscreenMonitor)) &&
               (mode->width != width || mode->height != height)) {
        glfwSetWindowMonitor(window, NULL, xpos, ypos, mode->width, mode->height, refreshRate);
    } else if (width == primaryWidth - xpos || height == primaryHeight - ypos) {
        // Windowed, cut to the switched mode's work area. The game saves its settings after
        // applying them, so use the Resolution saved now and again once the file changes.
        if (readDisplaySettings(&settings))
            resizeWindowed(window, xpos, ypos, &settings);
        g_windowedWindow = window;
        g_windowedX = xpos;
        g_windowedY = ypos;
    }
    g_fullscreenWindow = NULL;
}

static void shim_glfwSetWindowMonitor(GLFWwindow* window, GLFWmonitor* monitor,
                                      int xpos, int ypos, int width, int height, int refreshRate) {
    struct trackedWindow* tracked = findTracked(window);

    setWindowMonitor(window, monitor, xpos, ypos, width, height, refreshRate);
    if (tracked) {
        tracked->setMonitor = 1;
        updatePosition(tracked);
    }
}

// The game applies new settings before it saves them: redo what depends on them once the
// file changes
static void reapplySavedSettings(void) {
    struct displaySettings settings;
    const char* path;
    struct stat st;

    if ((!g_fullscreenWindow && !g_windowedWindow) || !(path = settingsPath()) || stat(path, &st) != 0 ||
        (st.st_mtimespec.tv_sec == g_settingsMtime.tv_sec &&
         st.st_mtimespec.tv_nsec == g_settingsMtime.tv_nsec) ||
        !readDisplaySettings(&settings))
        return;

    if (g_windowedWindow) {
        if (settings.mode == MODE_WINDOWED && !glfwGetWindowMonitor(g_windowedWindow))
            resizeWindowed(g_windowedWindow, g_windowedX, g_windowedY, &settings);
        g_windowedWindow = NULL;
    }
    if (g_fullscreenWindow && settings.mode == MODE_FULLSCREEN &&
        glfwGetWindowMonitor(g_fullscreenWindow) == g_fullscreenMonitor &&
        (settings.width != g_fullscreenWidth || settings.height != g_fullscreenHeight)) {
        g_fullscreenWidth = settings.width;
        g_fullscreenHeight = settings.height;
        if (settings.refresh > 0)
            g_fullscreenRefresh = settings.refresh;
        glfwSetWindowMonitor(g_fullscreenWindow, g_fullscreenMonitor, 0, 0,
                             g_fullscreenWidth, g_fullscreenHeight, g_fullscreenRefresh);
    }
}

static void shim_glfwPollEvents(void) {
    glfwPollEvents();
    reapplySavedSettings();
    updatePositions();
}

static void shim_glfwDestroyWindow(GLFWwindow* window) {
    struct trackedWindow* tracked = findTracked(window);

    if (window == g_fullscreenWindow)
        g_fullscreenWindow = NULL;
    if (window == g_windowedWindow)
        g_windowedWindow = NULL;
    if (tracked)
        tracked->window = NULL;
    glfwDestroyWindow(window);
}

DYLD_INTERPOSE(shim_CGDisplayCopyAllDisplayModes, CGDisplayCopyAllDisplayModes)
DYLD_INTERPOSE(shim_glfwSetInputMode, glfwSetInputMode)
DYLD_INTERPOSE(shim_glfwSetWindowMonitor, glfwSetWindowMonitor)
DYLD_INTERPOSE(shim_glfwPollEvents, glfwPollEvents)
DYLD_INTERPOSE(shim_glfwDestroyWindow, glfwDestroyWindow)
DYLD_INTERPOSE(shim_glfwSetWindowPosCallback, glfwSetWindowPosCallback)
