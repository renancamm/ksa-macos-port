using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.PostProcessing;

namespace KsaMetalHooks;

// MetalFX for the render scale (upscaling "metalfx" or "spatial"), and the motion that frame interpolation needs. Before
// the final composite, KSA Metal's vkCmdUpscaleKSA (src/ksa-metal/upscale.mm) scales the main view's image into an
// image of the swapchain's size (the display's pixels), which the composite then reads instead. For MetalFX's
// temporal upscaler, the projection moves by a different sub-pixel offset every frame (a Halton sequence of 16), and
// the driver makes the motion vectors from the depth buffer and two reprojections passed here, both computed in
// double: the scene through the nearby body's rotating frame, and inside its bounding sphere the vessel the camera
// follows, through its own frame. A copy of the image before the exhausts, particles and glass lets the driver mark
// what those change as reactive (no smearing). With frame interpolation, the final composite then marks where the UI
// starts, so that MetalFX can draw this frame's UI over the frame it makes.
internal static class Upscale
{
    private static bool Interpolate;
    public static bool Wanted, Spatial;  // set by Configure

    // Whether MetalFX runs: it scales the main view up to the swapchain (the display's pixels)
    public static void Configure()
    {
        Interpolate = Settings.On("frame_interpolation");
        Wanted = Settings.Upscaler is "metalfx" or "spatial" || Interpolate;
        Spatial = Settings.Upscaler == "spatial" && !Interpolate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkUpscaleInfoKSA
    {
        public ulong Color, Depth, Output;
        public uint InputWidth, InputHeight;
        public float JitterX, JitterY;
        public fixed float EgoFromClip[16];
        public fixed float PrevClipFromEgo[16];
        public fixed float PrevClipFromEgoObject[16];
        public fixed float ObjectSphere[4];
        public float FovY, NearPlane, FarPlane, DeltaTime;
        public uint Flags, Pad;
    }

    private const uint Reset = 1, SpatialOnly = 2;

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdUpscaleKSA(nint commandBuffer, ref VkUpscaleInfoKSA info);

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdUpscaleSnapshotKSA(nint commandBuffer, ulong image);

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdMarkUiKSA(nint commandBuffer);

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(ScreenspaceRenderer), nameof(ScreenspaceRenderer.AddImage)),
            prefix: new HarmonyMethod(typeof(Upscale), nameof(AddImagePrefix)));
        harmony.Patch(Find.Method(typeof(Program), "RenderAaPreparePasses"),
            postfix: new HarmonyMethod(typeof(Upscale), nameof(AaPreparePostfix)));
        if (!Spatial)
            harmony.Patch(Find.Method(typeof(Program), "RenderGame"),
                transpiler: new HarmonyMethod(typeof(Upscale), nameof(SnapshotTranspiler)));
        if (Interpolate)
            harmony.Patch(Find.Method(typeof(Program), "RenderFinalComposite"),
                postfix: new HarmonyMethod(typeof(Upscale), nameof(CompositePostfix)));
        harmony.Patch(Find.Method(typeof(Cmaa2Renderer), "CreateResourcesIfNeeded"),
            transpiler: new HarmonyMethod(typeof(Upscale), nameof(NoCmaa2Transpiler)));
        if (!Spatial && Settings.On("override_antialiasing"))
        {
            var keep = new HarmonyMethod(typeof(Upscale), nameof(KeepSampleCount));
            harmony.Patch(Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.OnPostRender)), postfix: keep);
            harmony.Patch(Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.Abort)), postfix: keep);
            // Last: a Skip undoes the patches, not this
            GameSettings.SampleCountOverride = VkSampleCountFlags._1Bit;
        }
    }

    // The game's anti-aliasing. The temporal upscaler smooths edges itself: the game's MSAA under it would cost 1-3 ms
    // more on an M5 and leave dark specks on Earth's views, so with override_antialiasing (on by default) the game runs
    // with one sample (GameSettings.SampleCountOverride, the door its high-resolution screenshots use, which also turns
    // CMAA2 off; a screenshot resets it, so it is set again after one). The spatial upscaler wants smoothed edges: MSAA
    // stays. CMAA2 is off with either: it would smooth a copy of MetalFX's image shrunk back to the render size, and
    // stretch that.
    private static void KeepSampleCount() => GameSettings.SampleCountOverride ??= VkSampleCountFlags._1Bit;

    // Cmaa2Renderer.CreateResourcesIfNeeded: GameSettings.IsCmaa2Enabled() -> false
    private static IEnumerable<CodeInstruction> NoCmaa2Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var enabled = Find.Method(typeof(GameSettings), nameof(GameSettings.IsCmaa2Enabled));
        int at = Find.Once(code, c => c.Calls(enabled), "CMAA2's switch");
        code[at] = new CodeInstruction(OpCodes.Ldc_I4_0).MoveLabelsFrom(code[at]).MoveBlocksFrom(code[at]);
        return code;
    }

    // Frame interpolation: the main view's composite is done and its UI comes next, in the same pass
    private static void CompositePostfix(CommandBuffer commandBuffer, int shaderSlot)
    {
        if (shaderSlot != Program.MainViewport.ShaderSlot)
            return;
        VkCommandBuffer handle = commandBuffer.Handle;
        vkCmdMarkUiKSA(Unsafe.As<VkCommandBuffer, nint>(ref handle));
    }

    private static RenderImage? _output;
    private static bool _reset = true, _havePrevious;
    private static int _frame;
    private static float2 _jitter;
    private static float4x4 _egoFromClip, _scene, _object, _previousViewProjection;
    private static float4 _sphere;
    private static float _fovY = 1f, _deltaTime;
    private static double4x4 _previousEgoToFrame, _previousEgoToObject;
    private static Vehicle? _previousObject;

    // The composite reads the upscaled image instead of the main view's
    private static void AddImagePrefix(ref VkImageView inImageView)
    {
        if (Program.OffscreenTarget is not { ColorImage: { } color } || !inImageView.Equals(color.ImageView))
            return;
        Renderer renderer = Program.GetRenderer();
        VkExtent2D size = renderer.SwapchainExtent;
        if (_output == null || _output.Width != size.Width || _output.Height != size.Height)
        {
            _output?.Dispose();
            _output = new RenderImage(new RenderImageCreateInfo
            {
                Name = "MetalFX Output",
                Device = renderer.Device,
                Allocator = renderer.Allocator,
                Width = size.Width,
                Height = size.Height,
                Format = Program.Instance.ColorFormat,
                Usage = VkImageUsageFlags.StorageBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.ColorAttachmentBit
            });
            _reset = true;
        }
        inImageView = _output.ImageView;
    }

    private static float Halton(int index, int radix)
    {
        float result = 0f, f = 1f;
        for (; index > 0; index /= radix)
        {
            f /= radix;
            result += f * (index % radix);
        }
        return result;
    }

    private static double4x4 ToDouble(in float4x4 m)
    {
        double4x4 d = double4x4.Identity;
        d.M11 = m.M11; d.M12 = m.M12; d.M13 = m.M13; d.M14 = m.M14;
        d.M21 = m.M21; d.M22 = m.M22; d.M23 = m.M23; d.M24 = m.M24;
        d.M31 = m.M31; d.M32 = m.M32; d.M33 = m.M33; d.M34 = m.M34;
        d.M41 = m.M41; d.M42 = m.M42; d.M43 = m.M43; d.M44 = m.M44;
        return d;
    }

    // Camera-centred ecliptic coordinates -> the nearby body's rotating frame (or the ecliptic frame in space)
    private static double4x4 EgoToFrame(Camera camera)
    {
        Celestial? body = camera.GetEffectiveNearbyCelestial();
        return body != null
            ? double4x4.CreateTranslation(camera.PositionEcl - body.GetPositionEcl()) * double4x4.CreateFromQuaternion(body.GetCce2Ccf())
            : double4x4.CreateTranslation(camera.PositionEcl);
    }

    // After the main view's camera data is set (RenderScale.ShaderDataPostfix): the jittered projection, and the
    // reprojections from this frame's ego space to the previous frame's clip space
    public static void UpdateCamera(IViewport viewport, ref UboCameraData data)
    {
        Camera camera = viewport.GetCamera();
        int2 size = viewport.Size;
        float jx = 0f, jy = 0f;
        if (!Spatial)
        {
            int i = _frame++ % 16 + 1;
            jx = Halton(i, 2) - 0.5f;
            jy = Halton(i, 3) - 0.5f;
        }
        _jitter = new float2(jx, jy);
        float4x4 p = camera.MVP.projection;
        _fovY = 2f * MathF.Atan(1f / MathF.Abs(p.M22));
        _deltaTime = data.DeltaTime;
        p.M31 -= 2f * jx / size.X;
        p.M32 -= 2f * jy / size.Y;
        float4x4 viewProjection = camera.MVP.view * p;
        float4x4.Invert(p, out var inverseProjection);
        data.Projection = p;
        data.ViewProjection = viewProjection;
        data.InverseProjection = inverseProjection;
        // The inverse in double: the depth range is 0.1 m to 1 AU
        double4x4.Invert(ToDouble(in viewProjection), out var egoFromClip);
        _egoFromClip = float4x4.Pack(in egoFromClip);
        double4x4 egoToFrame = EgoToFrame(camera);
        if (_havePrevious)
        {
            double4x4.Invert(_previousEgoToFrame, out var frameToPreviousEgo);
            double4x4 delta = egoToFrame * frameToPreviousEgo;
            _scene = float4x4.Pack(in delta) * _previousViewProjection;
            if (Math.Sqrt(delta.M41 * delta.M41 + delta.M42 * delta.M42 + delta.M43 * delta.M43) > 5000.0)
                _reset = true;  // a jump, not motion
        }
        else
        {
            _scene = camera.MVP.viewProjection;
            _reset = true;
        }
        _object = _scene;
        _sphere = default;
        if (camera.Following is Vehicle vehicle)
        {
            double4x4.Invert(vehicle.GetMatrixAsmb2Ego(camera), out var egoToObject);
            if (_havePrevious && _previousObject == vehicle)
            {
                double4x4.Invert(_previousEgoToObject, out var objectToPreviousEgo);
                double4x4 delta = egoToObject * objectToPreviousEgo;
                _object = float4x4.Pack(in delta) * _previousViewProjection;
                double3 centre = camera.GetPositionEgo(vehicle);
                _sphere = new float4(float3.Pack(in centre), vehicle.BoundingSphereRadiusBody * 1.2f + 1f);
            }
            _previousObject = vehicle;
            _previousEgoToObject = egoToObject;
        }
        else
        {
            _previousObject = null;
        }
        _havePrevious = true;
        _previousEgoToFrame = egoToFrame;
        _previousViewProjection = camera.MVP.viewProjection;
    }

    private static unsafe void Put(float* d, in float4x4 m)
    {
        d[0] = m.M11; d[1] = m.M12; d[2] = m.M13; d[3] = m.M14;
        d[4] = m.M21; d[5] = m.M22; d[6] = m.M23; d[7] = m.M24;
        d[8] = m.M31; d[9] = m.M32; d[10] = m.M33; d[11] = m.M34;
        d[12] = m.M41; d[13] = m.M42; d[14] = m.M43; d[15] = m.M44;
    }

    // Before the main view's final composite pass begins
    private static unsafe void AaPreparePostfix(CommandBuffer commandBuffer, int shaderSlot)
    {
        if (_output == null || Program.OffscreenTarget is not { ColorImage: { } colorImage, DepthImage: { } depthImage } ||
            shaderSlot != Program.MainViewport.ShaderSlot)
            return;
        VkImage color = colorImage.Image, depth = depthImage.Image, output = _output.Image;
        VkExtent2D input = Program.GetRenderer().Extent;
        var info = new VkUpscaleInfoKSA
        {
            Color = Unsafe.As<VkImage, ulong>(ref color),
            Depth = Spatial ? 0 : Unsafe.As<VkImage, ulong>(ref depth),
            Output = Unsafe.As<VkImage, ulong>(ref output),
            InputWidth = (uint)input.Width,
            InputHeight = (uint)input.Height,
            JitterX = _jitter.X,
            JitterY = _jitter.Y,
            FovY = _fovY,
            NearPlane = 0.1f,
            FarPlane = 1.4959786E+11f,
            DeltaTime = _deltaTime,
            Flags = (_reset ? Reset : 0) | (Spatial ? SpatialOnly : 0)
        };
        Put(info.EgoFromClip, in _egoFromClip);
        Put(info.PrevClipFromEgo, in _scene);
        Put(info.PrevClipFromEgoObject, in _object);
        info.ObjectSphere[0] = _sphere.X;
        info.ObjectSphere[1] = _sphere.Y;
        info.ObjectSphere[2] = _sphere.Z;
        info.ObjectSphere[3] = _sphere.W;
        VkCommandBuffer handle = commandBuffer.Handle;
        vkCmdUpscaleKSA(Unsafe.As<VkCommandBuffer, nint>(ref handle), ref info);
        _reset = false;
    }

    // RenderGame: the snapshot goes right before the volumetric exhausts' profiler region opens on the command buffer
    private static IEnumerable<CodeInstruction> SnapshotTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int at = Find.Once(code, c => c.opcode == OpCodes.Ldsfld && c.operand is System.Reflection.FieldInfo f &&
            f.Name == "VolumetricExhausts" && f.DeclaringType?.Name == "GpuTag", "the exhausts' profiler region");
        if (at == 0 || !code[at - 1].IsLdloc())
            throw new Skip("the exhausts' profiler region no longer follows the command buffer");
        var loadCommandBuffer = new CodeInstruction(code[at - 1].opcode, code[at - 1].operand).MoveLabelsFrom(code[at - 1]);
        code.InsertRange(at - 1, new[] { loadCommandBuffer, new CodeInstruction(OpCodes.Call, Find.Method(typeof(Upscale), nameof(Snapshot))) });
        return code;
    }

    private static void Snapshot(CommandBuffer commandBuffer)
    {
        if (Program.OffscreenTarget is not { ColorImage: { } colorImage })
            return;
        VkImage color = colorImage.Image;
        VkCommandBuffer handle = commandBuffer.Handle;
        vkCmdUpscaleSnapshotKSA(Unsafe.As<VkCommandBuffer, nint>(ref handle), Unsafe.As<VkImage, ulong>(ref color));
    }
}
