/*
 * libGlfwShim.dylib - makes the bundled Homebrew GLFW behave like the Windows build
 * under Wine. start_mac.command loads it with DYLD_INSERT_LIBRARIES, and it replaces
 * two calls through dyld interposing, so GLFW itself stays unmodified.
 *
 * 1. CGDisplayCopyAllDisplayModes: GLFW passes no options, so macOS leaves out the
 *    scaled HiDPI modes and the game's resolution list on a MacBook display starts at
 *    1920x1200. Wine's mac driver asks for them with
 *    kCGDisplayShowDuplicateLowResolutionModes; do the same for GLFW's calls.
 * 2. glfwSetInputMode: KSA turns raw mouse motion off without checking
 *    glfwRawMouseMotionSupported(), and GLFW's Cocoa backend reports a PlatformError
 *    ("Raw mouse motion is not supported on this system") after every right-drag.
 *    Skip raw motion calls when it is unsupported.
 */
#include <CoreGraphics/CoreGraphics.h>
#include <GLFW/glfw3.h>
#include <dispatch/dispatch.h>
#include <dlfcn.h>
#include <string.h>

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

DYLD_INTERPOSE(shim_CGDisplayCopyAllDisplayModes, CGDisplayCopyAllDisplayModes)
DYLD_INTERPOSE(shim_glfwSetInputMode, glfwSetInputMode)
