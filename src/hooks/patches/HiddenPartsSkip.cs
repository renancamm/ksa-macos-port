using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Hidden parts skip. The vessels' shaders mark selected parts in the selection outline's mask, a store from the
// fragment shader, and a shader that stores runs before the depth test: every fragment of a vessel is shaded, also
// where other parts or the cockpit hide it, and the GPU can't skip hidden ones. KSA Metal keeps a second state of
// such pipelines that tests depth first, without the stores of hidden fragments; at the start of each frame, in flight
// and in the editor, this tells it whether to draw with them: while no part is selected, when the stores never run.
// The image is the same.
internal static class HiddenPartsSkip
{
    private static int _selected;
    private static bool? _early;  // what the driver was last told

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlEarlyFragmentTests([MarshalAs(UnmanagedType.U1)] bool on);

    public static string Apply(Harmony harmony)
    {
        var setter = Find.Setter(typeof(Part), nameof(Part.Selected));
        var game = Find.Method(typeof(Program), "RenderGame");
        var editor = Find.Method(typeof(Program), "RenderEditor");
        ksaMtlEarlyFragmentTests(false);  // a driver without it fails the hook here
        harmony.Patch(setter, prefix: new HarmonyMethod(typeof(HiddenPartsSkip), nameof(SelectedPrefix)));
        harmony.Patch(game, prefix: new HarmonyMethod(typeof(HiddenPartsSkip), nameof(FramePrefix)));
        harmony.Patch(editor, prefix: new HarmonyMethod(typeof(HiddenPartsSkip), nameof(FramePrefix)));
        return "on";
    }

    private static void SelectedPrefix(Part __instance, bool value)
    {
        if (__instance.Selected != value)
            _selected += value ? 1 : -1;
    }

    private static void FramePrefix()
    {
        bool early = _selected == 0;
        if (_early == early)
            return;
        _early = early;
        ksaMtlEarlyFragmentTests(early);
    }
}
