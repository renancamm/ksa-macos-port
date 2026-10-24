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
 *
 * And one choice: with retina = true in mac-settings.toml (KSA_MTL_RETINA=1), the app is marked
 * NSHighResolutionCapable, and the framebuffer size, content scales and framebuffer size callback
 * are reported in points, so the game draws as it does without Retina.
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

#define MAX_TRACKED_WINDOWS 64

struct tracked_window {
    GLFWwindow *window;
    GLFWwindowposfun callback;  /* the game's */
    int x, y;                   /* the last position the game was told */
    int set_monitor;            /* went through glfwSetWindowMonitor */
};

static struct tracked_window tracked_windows[MAX_TRACKED_WINDOWS];

static struct tracked_window *find_tracked(GLFWwindow *window)
{
    for (int i = 0; i < MAX_TRACKED_WINDOWS; i++)
        if (tracked_windows[i].window == window)
            return &tracked_windows[i];
    return NULL;
}

static void report_position(GLFWwindow *window, int x, int y)
{
    struct tracked_window *tracked = find_tracked(window);

    if (!tracked)
        return;
    tracked->x = x;
    tracked->y = y;
    if (tracked->callback)
        tracked->callback(window, x, y);
}

static GLFWwindowposfun shim_glfwSetWindowPosCallback(GLFWwindow *window, GLFWwindowposfun callback)
{
    struct tracked_window *tracked = find_tracked(window);
    GLFWwindowposfun previous;

    if (!tracked) {
        if (!callback || !(tracked = find_tracked(NULL)))
            return glfwSetWindowPosCallback(window, callback);
        memset(tracked, 0, sizeof(*tracked));
        tracked->window = window;
        glfwGetWindowPos(window, &tracked->x, &tracked->y);
    }
    previous = tracked->callback;
    tracked->callback = callback;
    glfwSetWindowPosCallback(window, callback ? report_position : NULL);
    return previous;
}

/* Tells the game where a window really is when GLFW did not (see 4.) */
static void update_position(struct tracked_window *tracked)
{
    int x, y;

    if (!tracked->callback)
        return;
    glfwGetWindowPos(tracked->window, &x, &y);
    if (x != tracked->x || y != tracked->y)
        report_position(tracked->window, x, y);
}

static void update_positions(void)
{
    for (int i = 0; i < MAX_TRACKED_WINDOWS; i++)
        if (tracked_windows[i].window && tracked_windows[i].set_monitor)
            update_position(&tracked_windows[i]);
}

enum display_mode { MODE_WINDOWED, MODE_BORDERLESS, MODE_FULLSCREEN };

struct display_settings {
    enum display_mode mode;
    int width, height, refresh;
};

static GLFWwindow *fullscreen_window;
static GLFWmonitor *fullscreen_monitor;
static int fullscreen_width, fullscreen_height, fullscreen_refresh;
/* A window that left fullscreen with a size cut to the switched mode's work area */
static GLFWwindow *windowed_window;
static int windowed_x, windowed_y;
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

/* Reads the [display] table, which Tomlet writes as plain "key = value" lines. Returns
 * whether it has a resolution. */
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
            settings->mode = strcmp(mode, "Fullscreen") == 0 ? MODE_FULLSCREEN :
                             strcmp(mode, "Borderless") == 0 ? MODE_BORDERLESS : MODE_WINDOWED;
    }
    fclose(file);
    return settings->width > 0 && settings->height > 0;
}

static int min_int(int a, int b)
{
    return a < b ? a : b;
}

/* Redoes GameSettings.ApplyTo's Windowed size (the Resolution, cut to the primary monitor's
 * work area from the window position) with the restored mode's work area */
static void resize_windowed(GLFWwindow *window, int xpos, int ypos, const struct display_settings *settings)
{
    int work_width, work_height, width, height, new_width, new_height;

    glfwGetMonitorWorkarea(glfwGetPrimaryMonitor(), NULL, NULL, &work_width, &work_height);
    glfwGetWindowSize(window, &width, &height);
    new_width = min_int(work_width - xpos, settings->width);
    new_height = min_int(work_height - ypos, settings->height);
    if (new_width > 0 && new_height > 0 && (new_width != width || new_height != height))
        glfwSetWindowMonitor(window, NULL, xpos, ypos, new_width, new_height, 0);
}

static void set_window_monitor(GLFWwindow *window, GLFWmonitor *monitor,
                               int xpos, int ypos, int width, int height, int refreshRate)
{
    struct display_settings settings;
    int mode_x, mode_y, mode_width, mode_height;
    int work_x, work_y, work_width, work_height;
    int primary_width, primary_height;
    const GLFWvidmode *mode;

    if (window == windowed_window)
        windowed_window = NULL;
    if (monitor) {
        if (read_display_settings(&settings) && settings.mode == MODE_FULLSCREEN) {
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
    glfwGetMonitorWorkarea(glfwGetPrimaryMonitor(), NULL, NULL, &primary_width, &primary_height);
    glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);

    if (xpos == work_x && ypos == work_y && width == work_width && height == work_height) {
        glfwGetMonitorWorkarea(fullscreen_monitor, &xpos, &ypos, &width, &height);
        if (width != work_width || height != work_height)
            glfwSetWindowMonitor(window, NULL, xpos, ypos, width, height, refreshRate);
    } else if (xpos == mode_x && ypos == mode_y && width == mode_width && height == mode_height &&
               (mode = glfwGetVideoMode(fullscreen_monitor)) &&
               (mode->width != width || mode->height != height)) {
        glfwSetWindowMonitor(window, NULL, xpos, ypos, mode->width, mode->height, refreshRate);
    } else if (width == primary_width - xpos || height == primary_height - ypos) {
        /* Windowed, cut to the switched mode's work area. The game saves its settings after
         * applying them, so use the Resolution saved now and again once the file changes. */
        if (read_display_settings(&settings))
            resize_windowed(window, xpos, ypos, &settings);
        windowed_window = window;
        windowed_x = xpos;
        windowed_y = ypos;
    }
    fullscreen_window = NULL;
}

static void shim_glfwSetWindowMonitor(GLFWwindow *window, GLFWmonitor *monitor,
                                      int xpos, int ypos, int width, int height, int refreshRate)
{
    struct tracked_window *tracked = find_tracked(window);

    set_window_monitor(window, monitor, xpos, ypos, width, height, refreshRate);
    if (tracked) {
        tracked->set_monitor = 1;
        update_position(tracked);
    }
}

/* The game applies new settings before it saves them: redo what depends on them once the
 * file changes */
static void reapply_saved_settings(void)
{
    struct display_settings settings;
    const char *path;
    struct stat st;

    if ((!fullscreen_window && !windowed_window) || !(path = settings_path()) || stat(path, &st) != 0 ||
        (st.st_mtimespec.tv_sec == settings_mtime.tv_sec &&
         st.st_mtimespec.tv_nsec == settings_mtime.tv_nsec) ||
        !read_display_settings(&settings))
        return;

    if (windowed_window) {
        if (settings.mode == MODE_WINDOWED && !glfwGetWindowMonitor(windowed_window))
            resize_windowed(windowed_window, windowed_x, windowed_y, &settings);
        windowed_window = NULL;
    }
    if (fullscreen_window && settings.mode == MODE_FULLSCREEN &&
        glfwGetWindowMonitor(fullscreen_window) == fullscreen_monitor &&
        (settings.width != fullscreen_width || settings.height != fullscreen_height)) {
        fullscreen_width = settings.width;
        fullscreen_height = settings.height;
        if (settings.refresh > 0)
            fullscreen_refresh = settings.refresh;
        glfwSetWindowMonitor(fullscreen_window, fullscreen_monitor, 0, 0,
                             fullscreen_width, fullscreen_height, fullscreen_refresh);
    }
}

static void shim_glfwPollEvents(void)
{
    glfwPollEvents();
    reapply_saved_settings();
    update_positions();
}

static void shim_glfwDestroyWindow(GLFWwindow *window)
{
    struct tracked_window *tracked = find_tracked(window);

    if (window == fullscreen_window)
        fullscreen_window = NULL;
    if (window == windowed_window)
        windowed_window = NULL;
    if (tracked)
        tracked->window = NULL;
    glfwDestroyWindow(window);
}

/* Retina (KSA_MTL_RETINA=1, which the launcher sets from mac-settings.toml along with NSHighResolutionCapable): the
 * game still sees a window in points (framebuffer in points, content scale 1), and KSA Metal presents its image in a
 * drawable at the display's scale, upscaled with MetalFX */
static int retina_present(void)
{
    static int value = -1;

    if (value < 0) {
        const char *e = getenv("KSA_MTL_RETINA");
        value = e && atoi(e) != 0;
    }
    return value;
}

static void shim_glfwGetFramebufferSize(GLFWwindow *window, int *width, int *height)
{
    if (retina_present())
        glfwGetWindowSize(window, width, height);
    else
        glfwGetFramebufferSize(window, width, height);
}

static void shim_glfwGetWindowContentScale(GLFWwindow *window, float *x, float *y)
{
    glfwGetWindowContentScale(window, x, y);
    if (retina_present()) {
        if (x) *x = 1;
        if (y) *y = 1;
    }
}

static void shim_glfwGetMonitorContentScale(GLFWmonitor *monitor, float *x, float *y)
{
    glfwGetMonitorContentScale(monitor, x, y);
    if (retina_present()) {
        if (x) *x = 1;
        if (y) *y = 1;
    }
}

static GLFWframebuffersizefun game_framebuffer_callback;

static void framebuffer_size_in_points(GLFWwindow *window, int width, int height)
{
    glfwGetWindowSize(window, &width, &height);
    if (game_framebuffer_callback)
        game_framebuffer_callback(window, width, height);
}

static GLFWframebuffersizefun shim_glfwSetFramebufferSizeCallback(GLFWwindow *window, GLFWframebuffersizefun callback)
{
    GLFWframebuffersizefun previous;

    if (!retina_present())
        return glfwSetFramebufferSizeCallback(window, callback);
    previous = game_framebuffer_callback;
    game_framebuffer_callback = callback;
    glfwSetFramebufferSizeCallback(window, callback ? framebuffer_size_in_points : NULL);
    return previous;
}

DYLD_INTERPOSE(shim_CGDisplayCopyAllDisplayModes, CGDisplayCopyAllDisplayModes)
DYLD_INTERPOSE(shim_glfwSetInputMode, glfwSetInputMode)
DYLD_INTERPOSE(shim_glfwSetWindowMonitor, glfwSetWindowMonitor)
DYLD_INTERPOSE(shim_glfwPollEvents, glfwPollEvents)
DYLD_INTERPOSE(shim_glfwDestroyWindow, glfwDestroyWindow)
DYLD_INTERPOSE(shim_glfwSetWindowPosCallback, glfwSetWindowPosCallback)
DYLD_INTERPOSE(shim_glfwGetFramebufferSize, glfwGetFramebufferSize)
DYLD_INTERPOSE(shim_glfwGetWindowContentScale, glfwGetWindowContentScale)
DYLD_INTERPOSE(shim_glfwGetMonitorContentScale, glfwGetMonitorContentScale)
DYLD_INTERPOSE(shim_glfwSetFramebufferSizeCallback, glfwSetFramebufferSizeCallback)
