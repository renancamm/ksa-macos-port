using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.GlfwApi.Cocoa;

namespace KsaMetalHooks;

// Each display's desktop mode: its size in points and its pixels per point, read when the hooks are applied, before the
// game's Fullscreen can switch a display to another mode. A display connected later gets its mode when first asked.
// The GLFW shim lists the sizes of the same reference (the mode a display had when GLFW first asked).
internal static class Displays
{
    public readonly record struct Mode(int Width, int Height, int Scale);

    // By CGDirectDisplayID
    private static readonly Dictionary<uint, Mode> Desktops = new();

    public static void Read()
    {
        if (Desktops.Count > 0)
            return;
        var displays = new uint[16];
        if (CGGetActiveDisplayList((uint)displays.Length, displays, out uint count) != 0)
            return;
        for (int i = 0; i < count; i++)
            Add(displays[i]);
    }

    // The desktop mode of a monitor of the game's; null when macOS doesn't say
    public static Mode? Desktop(GlfwMonitor monitor)
    {
        uint display = monitor.IsNull() ? 0 : monitor.GetCocoaMonitor();
        if (display == 0)
            return null;
        return Desktops.TryGetValue(display, out var mode) ? mode : Add(display);
    }

    private static Mode? Add(uint display)
    {
        nint mode = CGDisplayCopyDisplayMode(display);
        if (mode == 0)
            return null;
        int width = (int)CGDisplayModeGetWidth(mode), height = (int)CGDisplayModeGetHeight(mode);
        int scale = width > 0 ? (int)CGDisplayModeGetPixelWidth(mode) / width : 1;
        CGDisplayModeRelease(mode);
        if (width <= 0 || height <= 0)
            return null;
        return Desktops[display] = new Mode(width, height, scale);
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] displays, out uint count);

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
