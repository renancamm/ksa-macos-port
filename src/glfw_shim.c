/*
 * libGlfwShim.dylib - makes the bundled Homebrew GLFW behave like the Windows build
 * under Wine. start_mac.command loads it with DYLD_INSERT_LIBRARIES, and it replaces
 * a few calls through dyld interposing, so GLFW itself stays unmodified.
 *
 * 1. CGDisplayCopyAllDisplayModes: GLFW passes no options, so macOS leaves out the
 *    scaled HiDPI modes and the game's resolution list on a MacBook display starts at
 *    1920x1200. Wine's mac driver asks for them with
 *    kCGDisplayShowDuplicateLowResolutionModes; do the same for GLFW's calls.
 * 2. glfwSetInputMode: KSA turns raw mouse motion off without checking
 *    glfwRawMouseMotionSupported(), and GLFW's Cocoa backend reports a PlatformError
 *    ("Raw mouse motion is not supported on this system") after every right-drag.
 *    Skip raw motion calls when it is unsupported.
 * 3. glfwSetWindowMonitor: in Fullscreen mode KSA always passes the desktop's current
 *    size (the Windows build does too), so the Resolution setting does nothing there.
 *    Use display.width/height from settings.toml instead, which makes GLFW switch the
 *    display to that mode. The game applies new settings before it saves them, so
 *    glfwPollEvents rereads the file whenever it changes and switches again.
 *    When the window leaves fullscreen, the game sizes it from the switched mode, so a
 *    size that matches that mode or its work area is redone for the restored mode.
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
    __attribute__((used)) static struct { const void *a; const void *b; } \
    interpose_##replacee __attribute__((section("__DATA,__interpose"))) = \
        { (const void *)&replacement, (const void *)&replacee };

static int called_from_glfw(const void *return_address)
{
    Dl_info info;
    return dladdr(return_address, &info) && info.dli_fname && strstr(info.dli_fname, "libglfw");
}

static CFArrayRef shim_CGDisplayCopyAllDisplayModes(CGDirectDisplayID display, CFDictionaryRef options)
{
    static CFDictionaryRef with_low_res_modes;
    static dispatch_once_t once;

    if (options == NULL && called_from_glfw(__builtin_return_address(0))) {
        dispatch_once(&once, ^{
            const void *keys[] = { kCGDisplayShowDuplicateLowResolutionModes };
            const void *values[] = { kCFBooleanTrue };
            with_low_res_modes = CFDictionaryCreate(NULL, keys, values, 1,
                &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
        });
        options = with_low_res_modes;
    }
    return CGDisplayCopyAllDisplayModes(display, options);
}

static void shim_glfwSetInputMode(GLFWwindow *window, int mode, int value)
{
    if (mode == GLFW_RAW_MOUSE_MOTION && !glfwRawMouseMotionSupported())
        return;
    glfwSetInputMode(window, mode, value);
}

struct display_settings {
    int fullscreen, width, height, refresh;
};

static GLFWwindow *fullscreen_window;
static GLFWmonitor *fullscreen_monitor;
static int fullscreen_width, fullscreen_height, fullscreen_refresh;
static struct timespec settings_mtime;

/* GameSettings.SettingsFilePath: <SpecialFolder.Personal>/My Games/Kitten Space Agency */
static const char *settings_path(void)
{
    static char path[1024];
    static dispatch_once_t once;

    dispatch_once(&once, ^{
        const char *home = getenv("HOME");
        if (home)
            snprintf(path, sizeof(path), "%s/Documents/My Games/Kitten Space Agency/settings.toml", home);
    });
    return path[0] ? path : NULL;
}

/* Reads the [display] table, which Tomlet writes as plain "key = value" lines. */
static int read_display_settings(struct display_settings *settings)
{
    const char *path = settings_path();
    struct stat st;
    FILE *file;
    char line[512], mode[64];
    int in_display = 0;

    memset(settings, 0, sizeof(*settings));
    if (!path || stat(path, &st) != 0 || !(file = fopen(path, "r")))
        return 0;
    settings_mtime = st.st_mtimespec;

    while (fgets(line, sizeof(line), file)) {
        const char *p = line + strspn(line, " \t");
        if (*p == '[') {
            in_display = strncmp(p, "[display]", 9) == 0;
            continue;
        }
        if (!in_display)
            continue;
        if (sscanf(p, "width = %d", &settings->width) == 1 ||
            sscanf(p, "height = %d", &settings->height) == 1 ||
            sscanf(p, "refresh = %d", &settings->refresh) == 1)
            continue;
        if (sscanf(p, "mode = \"%63[^\"]\"", mode) == 1)
            settings->fullscreen = strcmp(mode, "Fullscreen") == 0;
    }
    fclose(file);
    return settings->fullscreen && settings->width > 0 && settings->height > 0;
}

static void shim_glfwSetWindowMonitor(GLFWwindow *window, GLFWmonitor *monitor,
                                      int xpos, int ypos, int width, int height, int refreshRate)
{
    struct display_settings settings;
    int mode_x, mode_y, mode_width, mode_height;
    int work_x, work_y, work_width, work_height;
    const GLFWvidmode *mode;

    if (monitor) {
        if (read_display_settings(&settings)) {
            width = settings.width;
            height = settings.height;
        }
        fullscreen_window = window;
        fullscreen_monitor = monitor;
        fullscreen_width = width;
        fullscreen_height = height;
        fullscreen_refresh = refreshRate;
        glfwSetWindowMonitor(window, monitor, xpos, ypos, width, height, refreshRate);
        return;
    }
    if (window != fullscreen_window || !(mode = glfwGetVideoMode(fullscreen_monitor))) {
        glfwSetWindowMonitor(window, monitor, xpos, ypos, width, height, refreshRate);
        return;
    }

    /* Leaving fullscreen: the game computed this size from the mode GLFW switched to. */
    glfwGetMonitorPos(fullscreen_monitor, &mode_x, &mode_y);
    mode_width = mode->width;
    mode_height = mode->height;
    glfwGetMonitorWorkarea(fullscreen_monitor, &work_x, &work_y, &work_width, &work_height);
    glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);

    if (xpos == work_x && ypos == work_y && width == work_width && height == work_height) {
        glfwGetMonitorWorkarea(fullscreen_monitor, &xpos, &ypos, &width, &height);
        if (width != work_width || height != work_height)
            glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);
    } else if (xpos == mode_x && ypos == mode_y && width == mode_width && height == mode_height &&
               (mode = glfwGetVideoMode(fullscreen_monitor)) &&
               (mode->width != width || mode->height != height)) {
        glfwSetWindowMonitor(window, NULL, xpos, ypos, mode->width, mode->height, refreshRate);
    }
    fullscreen_window = NULL;
}

static void shim_glfwPollEvents(void)
{
    struct display_settings settings;
    const char *path;
    struct stat st;

    glfwPollEvents();

    if (!fullscreen_window || !(path = settings_path()) || stat(path, &st) != 0 ||
        (st.st_mtimespec.tv_sec == settings_mtime.tv_sec &&
         st.st_mtimespec.tv_nsec == settings_mtime.tv_nsec))
        return;
    if (!read_display_settings(&settings) ||
        glfwGetWindowMonitor(fullscreen_window) != fullscreen_monitor ||
        (settings.width == fullscreen_width && settings.height == fullscreen_height))
        return;

    fullscreen_width = settings.width;
    fullscreen_height = settings.height;
    if (settings.refresh > 0)
        fullscreen_refresh = settings.refresh;
    glfwSetWindowMonitor(fullscreen_window, fullscreen_monitor, 0, 0,
                         fullscreen_width, fullscreen_height, fullscreen_refresh);
}

static void shim_glfwDestroyWindow(GLFWwindow *window)
{
    if (window == fullscreen_window)
        fullscreen_window = NULL;
    glfwDestroyWindow(window);
}

DYLD_INTERPOSE(shim_CGDisplayCopyAllDisplayModes, CGDisplayCopyAllDisplayModes)
DYLD_INTERPOSE(shim_glfwSetInputMode, glfwSetInputMode)
DYLD_INTERPOSE(shim_glfwSetWindowMonitor, glfwSetWindowMonitor)
DYLD_INTERPOSE(shim_glfwPollEvents, glfwPollEvents)
DYLD_INTERPOSE(shim_glfwDestroyWindow, glfwDestroyWindow)
