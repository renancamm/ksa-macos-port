using System;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace KsaMetalHooks;

// Overlay skip. After the bloom, every frame, GizmoPass.Run draws the gizmos in two passes, each a clear of an image
// of its own and a blend of that image over the whole view, also when there are no gizmos. With MSAA, the main view
// is also copied into its multisampled image first (SingleToMultisamplePass), so that the orbit lines and the gizmos
// are drawn with MSAA, and resolved back after them. Without gizmos, the gizmo passes are skipped; in the main view
// without orbit lines too, the copy is skipped and the resolve after it resolves depth only: the copy gave every
// sample the view's own value, so resolving the colour would give the view back as it was. Other views (the crew
// portraits) draw their translucent meshes between the copy and the resolve: they keep both. The image is the same.
internal static class OverlaySkip
{
    private static AccessTools.FieldRef<RenderTarget, TargetImage?> _color = null!;
    [ThreadStatic] private static RenderTarget? _pending;  // the copy was skipped: its resolve resolves depth only
    private static bool _failed;  // the game's passes from then on (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        _color = AccessTools.FieldRefAccess<RenderTarget, TargetImage?>(Find.Field(typeof(RenderTarget), "_color"));
        var gizmos = Find.Method(typeof(GizmoPass), nameof(GizmoPass.Run));
        var copy = Find.Method(typeof(SingleToMultisamplePass), nameof(SingleToMultisamplePass.Run));
        var resolve = Find.Method(typeof(RenderTarget), nameof(RenderTarget.ResolveAttachments));
        harmony.Patch(gizmos, prefix: new HarmonyMethod(typeof(OverlaySkip), nameof(GizmoPrefix)));
        harmony.Patch(copy, prefix: new HarmonyMethod(typeof(OverlaySkip), nameof(CopyPrefix)));
        harmony.Patch(resolve, prefix: new HarmonyMethod(typeof(OverlaySkip), nameof(ResolvePrefix)),
            finalizer: new HarmonyMethod(typeof(OverlaySkip), nameof(ResolveFinalizer)));
        return "on";
    }

    // Whether a gizmo has instances in either pass this frame (GizmoParent.Render draws them, then clears the list)
    private static bool HasGizmos(IViewport viewport, int frameIndex)
    {
        foreach (GizmoParent parent in GenericGizmo.Static.GizmoParents)
        {
            if (parent.TryGetViewportData(viewport) is not { } data)
                continue;
            for (int pass = 0; pass < GizmoPass.PASS_COUNT; pass++)
            {
                if ((int)data.InstanceVector[pass][frameIndex].ElementCount > 0)
                    return true;
            }
        }
        return false;
    }

    // OrbitLinePass.Run's own condition
    private static bool HasOrbitLines(IViewport viewport, int frameIndex) =>
        Program.DrawUI && (int)OrbitLinePass.ColorDataVector[viewport.ShaderSlot][frameIndex].ElementCount > 0;

    private static bool GizmoPrefix(IViewport viewport, int frameIndex)
    {
        if (_failed)
            return true;
        try
        {
            return HasGizmos(viewport, frameIndex);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("overlay_skip", e);
            return true;
        }
    }

    private static bool CopyPrefix(IViewport viewport, int frameIndex)
    {
        _pending = null;
        if (_failed || !viewport.IsMain())
            return true;
        try
        {
            if (HasGizmos(viewport, frameIndex) || HasOrbitLines(viewport, frameIndex))
                return true;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("overlay_skip", e);
            return true;
        }
        _pending = viewport.OffscreenTarget;
        return false;
    }

    // The resolve right after a skipped copy: the colour is hidden from it, which then resolves the depth alone
    private static void ResolvePrefix(RenderTarget __instance, bool inResolveDepth, out TargetImage? __state)
    {
        __state = null;
        RenderTarget? pending = _pending;
        _pending = null;
        if (!ReferenceEquals(__instance, pending) || !inResolveDepth)
            return;
        __state = _color(__instance);
        _color(__instance) = null;
    }

    private static Exception? ResolveFinalizer(RenderTarget __instance, TargetImage? __state, Exception? __exception)
    {
        if (__state != null)
            _color(__instance) = __state;
        return __exception;
    }
}
