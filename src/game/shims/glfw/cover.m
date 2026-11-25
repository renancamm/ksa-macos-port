/*
 * A window that covers its screen: the game's Borderless and Fullscreen (glfw_shim.c, 6. and 7.).
 *
 * Borderless is an undecorated window with the screen's frame (KSA 5917 on: the monitor's position and video mode
 * size, no longer its work area), at the normal window level, so macOS draws the menu bar over the window's top row and
 * the Dock over its bottom. While the window covers its screen, the menu bar and the Dock hide until the pointer reaches
 * them (NSApp's presentation options, which macOS applies only while the game is the active app). Fullscreen's window
 * is above the menu bar's level (GLFW's), so neither shows there.
 *
 * On a display with a camera notch, both put the window's top row beside the camera, where it hides parts of the game's
 * menu and HUD. With Fit Below the Notch (the hooks' NotchFit calls glfwShimFitBelowNotch), the window takes the screen
 * below the notch (NSScreen.safeAreaInsets) and a black window at its level fills the screen behind it, so the strip
 * on either side of the camera is black, and a click there does nothing. The window keeps that frame: GLFW gives a
 * Fullscreen window the screen's whole frame again whenever it switches the display mode (acquireMonitor), and
 * coverCheck, from glfwPollEvents four times a second, puts it back. It also looks again at the window placed last that
 * doesn't cover a screen: right after a display mode switch NSScreen can still hold the old frames.
 */
#import <AppKit/AppKit.h>
#include <time.h>
#define GLFW_EXPOSE_NATIVE_COCOA
#include <GLFW/glfw3.h>
#include <GLFW/glfw3native.h>

#include "cover.h"

static GLFWwindow* g_window;   // the window that covers a screen, if one does
static GLFWwindow* g_placed;   // the window placed last (glfwSetWindowMonitor), which coverCheck looks at again
static NSWindow* g_backdrop;   // the black window behind it below the notch
static BOOL g_menuBarHidden;
static NSApplicationPresentationOptions g_previousOptions;
static BOOL g_fit;             // Fit Below the Notch
static double g_inset;         // the notch's height in points the window was moved below, 0 when it covers its screen

static NSScreen* screenOfDisplay(CGDirectDisplayID display) {
    for (NSScreen* screen in NSScreen.screens)
        if ([screen.deviceDescription[@"NSScreenNumber"] unsignedIntValue] == display)
            return screen;
    return nil;
}

// The part of a screen below its camera notch: the whole screen without one
static NSRect belowNotch(NSScreen* screen) {
    NSRect frame = screen.frame;
    frame.size.height -= screen.safeAreaInsets.top;
    return frame;
}

// The screen a window covers, nil when none: Fullscreen's monitor, or the screen whose frame an undecorated window has,
// or the part below the notch that this file moved it to
static NSScreen* coveredScreen(GLFWwindow* window, NSWindow* nsWindow, BOOL* fullscreen) {
    GLFWmonitor* monitor = glfwGetWindowMonitor(window);

    *fullscreen = monitor != NULL;
    if (monitor)
        return screenOfDisplay(glfwGetCocoaMonitor(monitor));
    if (glfwGetWindowAttrib(window, GLFW_DECORATED))
        return nil;
    for (NSScreen* screen in NSScreen.screens) {
        if (NSEqualRects(nsWindow.frame, screen.frame) ||
            (window == g_window && g_inset > 0 && NSEqualRects(nsWindow.frame, belowNotch(screen))))
            return screen;
    }
    return nil;
}

static void hideMenuBar(BOOL hide) {
    if (hide == g_menuBarHidden)
        return;
    @try {
        if (hide) {
            g_previousOptions = NSApp.presentationOptions;
            NSApp.presentationOptions = (g_previousOptions & ~(NSApplicationPresentationHideDock | NSApplicationPresentationHideMenuBar)) |
                                        NSApplicationPresentationAutoHideDock | NSApplicationPresentationAutoHideMenuBar;
        } else {
            NSApp.presentationOptions = g_previousOptions;
        }
    } @catch (NSException* e) {
        fprintf(stderr, "[glfw-shim] could not %s the menu bar: %s\n", hide ? "hide" : "show", e.reason.UTF8String);
    }
    // Once either way: a refused change would be refused again
    g_menuBarHidden = hide;
}

// The black window behind nsWindow, over the whole screen; nil screen: none. A child window ordered below it, so it
// stays just behind the window whenever the window comes to the front
static void showBackdrop(NSScreen* screen, NSWindow* nsWindow) {
    if (!screen) {
        [g_backdrop.parentWindow removeChildWindow:g_backdrop];
        [g_backdrop orderOut:nil];
        return;
    }
    if (!g_backdrop) {
        g_backdrop = [[NSWindow alloc] initWithContentRect:screen.frame
                                                 styleMask:NSWindowStyleMaskBorderless
                                                   backing:NSBackingStoreBuffered
                                                     defer:NO];
        g_backdrop.releasedWhenClosed = NO;
        g_backdrop.backgroundColor = NSColor.blackColor;
        g_backdrop.opaque = YES;
        g_backdrop.hasShadow = NO;
        // It takes the clicks beside the notch, which would otherwise reach the menu bar under it (Fullscreen) or the
        // app behind the game (Borderless); a borderless window never becomes the key window, so the game keeps it
        g_backdrop.ignoresMouseEvents = NO;
        g_backdrop.excludedFromWindowsMenu = YES;
        g_backdrop.collectionBehavior = NSWindowCollectionBehaviorIgnoresCycle | NSWindowCollectionBehaviorFullScreenNone;
    }
    if (!NSEqualRects(g_backdrop.frame, screen.frame))
        [g_backdrop setFrame:screen.frame display:NO];
    if (g_backdrop.level != nsWindow.level)
        g_backdrop.level = nsWindow.level;
    if (g_backdrop.parentWindow != nsWindow) {
        [g_backdrop.parentWindow removeChildWindow:g_backdrop];
        [nsWindow addChildWindow:g_backdrop ordered:NSWindowBelow];
    }
}

static void release(void) {
    g_window = NULL;
    g_inset = 0;
    hideMenuBar(NO);
    showBackdrop(nil, nil);
}

void coverUpdate(GLFWwindow* window) {
    g_placed = window;
    @autoreleasepool {
        NSWindow* nsWindow = glfwGetCocoaWindow(window);
        BOOL fullscreen;
        NSScreen* screen = nsWindow ? coveredScreen(window, nsWindow, &fullscreen) : nil;
        double inset;
        NSRect frame;

        if (!screen) {
            if (window == g_window)
                release();
            return;
        }
        g_window = window;
        hideMenuBar(!fullscreen);
        inset = g_fit ? screen.safeAreaInsets.top : 0;
        frame = inset > 0 ? belowNotch(screen) : screen.frame;
        g_inset = inset;
        if (!NSEqualRects(nsWindow.frame, frame))
            [nsWindow setFrame:frame display:YES];
        showBackdrop(inset > 0 ? screen : nil, nsWindow);
    }
}

// Four times a second at most, rather than reading the screens every frame: after GLFW's own display mode switch,
// up to a quarter of a second at the screen's whole size
void coverCheck(void) {
    static uint64_t last;
    uint64_t now = clock_gettime_nsec_np(CLOCK_UPTIME_RAW);

    if (!g_placed || now - last < 250000000)
        return;
    last = now;
    coverUpdate(g_placed);
}

void coverForget(GLFWwindow* window) {
    if (window == g_placed)
        g_placed = NULL;
    if (window == g_window)
        release();
}

// For the hooks (NotchFit): Fit Below the Notch, on or off
__attribute__((visibility("default")))
void glfwShimFitBelowNotch(int on) {
    if (g_fit == (on != 0))
        return;
    g_fit = on != 0;
    if (g_placed)
        coverUpdate(g_placed);
}

// For the hooks (NotchFit, HudFit): the notch's height in points the game's window was moved below, 0 when it wasn't
__attribute__((visibility("default")))
double glfwShimNotchInset(void) {
    return g_inset;
}
