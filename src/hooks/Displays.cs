using System;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Cocoa;

namespace KsaMetalHooks;

// A display's desktop mode: its size in points and its pixels per point. The GLFW shim answers, as it lists the sizes
// of the same mode: the display's current one, or, while the game's Fullscreen has it switched to another, the one it
// had before. Without the shim, the current mode.
internal static class Displays
{
    public readonly record struct Mode(int Width, int Height, int Scale);

    private static bool _noShim;

    // The desktop mode of a monitor of the game's; null when macOS doesn't say
    public static Mode? Desktop(GlfwMonitor monitor)
    {
        uint display = monitor.IsNull() ? 0 : monitor.GetCocoaMonitor();
        if (display == 0)
            return null;
        int width, height, pixelWidth;
        if (!_noShim)
        {
            try
            {
                if (glfwShimDesktopMode(display, out width, out height, out pixelWidth, out _) == 0)
                    return null;
                return new Mode(width, height, pixelWidth / width);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                _noShim = true;
            }
        }
        nint mode = CGDisplayCopyDisplayMode(display);
        if (mode == 0)
            return null;
        width = (int)CGDisplayModeGetWidth(mode);
        height = (int)CGDisplayModeGetHeight(mode);
        pixelWidth = (int)CGDisplayModeGetPixelWidth(mode);
        CGDisplayModeRelease(mode);
        if (width <= 0 || height <= 0)
            return null;
        return new Mode(width, height, pixelWidth / width);
    }

    [DllImport("libGlfwShim.dylib")]
    private static extern int glfwShimDesktopMode(uint display, out int width, out int height, out int pixelWidth,
        out int pixelHeight);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetWidth(nint mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetHeight(nint mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(nint mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(nint mode);
}
