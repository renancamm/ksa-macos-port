using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using KSA.Atmosphere.Rendering;
using KSA.Rendering.Raytracing;

namespace KsaMetal.Hooks.Features;

// MetalFX for the render scale (upscaling "metalfx" or "spatial"), and the motion that frame interpolation needs.
// Before the final composite, the driver's vkCmdUpscaleKSA (src/game/driver/presentation/upscale.mm) scales the main
// view's image into an image of the swapchain's size (the display's pixels), which the composite then reads instead.
// For MetalFX's temporal upscaler, the projection moves by a different sub-pixel offset every frame (a Halton sequence
// of 16), the cockpit's ray tracing's too, and the driver makes the motion vectors from the depth buffer and the
// reprojections passed here, computed in double: the ground and what is on it through the nearby body's rotating frame,
// what is higher up (or anywhere away from a body) with the centre of the vessel the camera follows, the sky by the
// camera's turn alone, and inside its bounding sphere that vessel, through its own frame. Copies of the image before
// the exhausts, particles and glass and on both sides of the bloom let the driver mark what those effects and what is
// drawn over the scene change as reactive (no smearing), but not the bloom. MetalFX gets the game's exposure (its
// Tonemap Exposure) rather than estimating one: its estimate in dark views drew faint stars as streaks. With frame
// interpolation, the final composite then marks where the UI starts, so that MetalFX can draw this frame's UI over the
// frame it makes. The cockpit's ray-traced lighting keeps a history of its own (RayHistoryPostfix), so that MetalFX
// gets it steady.
internal static class Upscale
{
    private static bool Interpolate;
    public static bool Wanted, Spatial;  // set by Configure

    // Whether MetalFX runs: it scales the main view up to the swapchain (the display's pixels)
    public static void Configure()
    {
        Interpolate = Settings.FrameInterpolation;
        Wanted = Settings.Upscaling is "metalfx" or "spatial" || Interpolate;
        Spatial = Settings.Upscaling == "spatial" && !Interpolate;
    }

    // The driver's structs and calls, field for field (src/game/driver/exports.h, which checks their layout)
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkUpscaleInfoKSA
    {
        public ulong Color, Depth, Output;
        public uint InputWidth, InputHeight;
        public float JitterX, JitterY;
        public fixed float EgoFromClip[16];
        public fixed float PrevClipFromEgo[16];
        public fixed float PrevClipFromEgoObject[16];
        public fixed float PrevClipFromEgoSky[16];
        public fixed float PrevClipFromEgoNearby[16];
        public fixed float ObjectSphere[4];
        public fixed float BodySphere[4];
        public float FovY, NearPlane, FarPlane, DeltaTime;
        public uint Flags;
        public float Exposure;
        public ulong CloudColor, CloudMotion;
        public uint ContentWidth, ContentHeight;
    }

    private const uint Reset = 1, SpatialOnly = 2;

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdUpscaleKSA(nint commandBuffer, ref VkUpscaleInfoKSA info);

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdUpscaleSnapshotKSA(nint commandBuffer, ulong image, uint stage);

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdMarkUiKSA(nint commandBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct VkRayHistoryInfoKSA
    {
        public fixed ulong Images[4];
        public fixed float MaxFrames[4];
        public fixed float Clip[4];
        public ulong Depth, Normals;
        public fixed float EgoFromClip[16];
        public fixed float Scene[16];
        public fixed float Object[16];
        public fixed float Nearby[16];
        public fixed float ObjectSphere[4];
        public fixed float BodySphere[4];
        public float JitterX, JitterY;
        public uint Flags, Frame;
    }

    [DllImport("libvulkan.1.dylib")]
    private static extern void vkCmdRayHistoryKSA(nint commandBuffer, ref VkRayHistoryInfoKSA info);

    // The cockpit's ray-traced lighting images, in the order the game makes them, and how many frames each one's history
    // holds and how far the history may stray from the pixels around (in their spread; ray history in the driver): the
    // direct light (the sun's patches, the kittens' shadows), the indirect light (smoothed by the game's a-trous passes
    // before this), the reflections. The glass's reflections come from the translucent prepass, which isn't reprojected.
    private static readonly (float Frames, float Clip)[] RayHistory = [(8f, 1.5f), (16f, 2f), (8f, 1f), (0f, 0f)];

    public static void Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(ScreenspaceRenderer), nameof(ScreenspaceRenderer.AddImage)),
            prefix: new HarmonyMethod(typeof(Upscale), nameof(AddImagePrefix)));
        harmony.Patch(Find.Method(typeof(Program), "RenderAaPreparePasses"),
            postfix: new HarmonyMethod(typeof(Upscale), nameof(AaPreparePostfix)));
        harmony.Patch(Find.Method(typeof(Program), "RenderGame"),
            transpiler: new HarmonyMethod(typeof(Upscale), nameof(CaptureTranspiler)));
        if (!Spatial)
        {
            harmony.Patch(Find.Method(typeof(Program), "RenderGame"),
                transpiler: new HarmonyMethod(typeof(Upscale), nameof(SnapshotTranspiler)));
            harmony.Patch(Find.Method(typeof(Program), "RenderGame"),
                transpiler: new HarmonyMethod(typeof(Upscale), nameof(BloomTranspiler)));
            harmony.Patch(Find.Method(typeof(RaytracingRenderer), nameof(RaytracingRenderer.RunRaytracePass)),
                transpiler: new HarmonyMethod(typeof(Upscale), nameof(RayTracingTranspiler)));
            harmony.Patch(Find.Method(typeof(RaytracePipeline), nameof(RaytracePipeline.Dispatch)),
                prefix: new HarmonyMethod(typeof(Upscale), nameof(RayTracingSeedPrefix)));
            harmony.Patch(Find.Method(typeof(RaytracingRenderer), "ApplyDenoise"),
                postfix: new HarmonyMethod(typeof(Upscale), nameof(RayHistoryPostfix)));
            ApplyCloudMotion(harmony);
        }
        if (Interpolate)
        {
            harmony.Patch(Find.Method(typeof(Program), "RenderFinalComposite"),
                postfix: new HarmonyMethod(typeof(Upscale), nameof(CompositePostfix)));
            harmony.Patch(Find.Method(typeof(UiCoverageMaskSystem), nameof(UiCoverageMaskSystem.RecordMaskGeneration)),
                transpiler: new HarmonyMethod(typeof(Upscale), nameof(NoUiCullingTranspiler)));
        }
        harmony.Patch(Find.Method(typeof(Cmaa2Renderer), "CreateResourcesIfNeeded"),
            transpiler: new HarmonyMethod(typeof(Upscale), nameof(NoCmaa2Transpiler)));
        if (!Spatial && Settings.SkipGameAntiAliasing)
        {
            var keep = new HarmonyMethod(typeof(Upscale), nameof(KeepSampleCount));
            harmony.Patch(Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.OnPostRender)), postfix: keep);
            harmony.Patch(Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.Abort)), postfix: keep);
            // Last: a Skip undoes the patches, not this
            GameSettings.SampleCountOverride = VkSampleCountFlags._1Bit;
        }
    }

    // The game's cloud upscaler (CloudRenderer.PerformUpscaling) draws the clouds and the smoke trails at the view's size,
    // each frame into the other of two sets of images: their colour with the light that passes them, and their own motion
    // (the clouds' drift with the wind and the planet, the smoke's with its trail). They write no depth, so the driver's
    // motion from the depth gave them the motion of what is behind them: in a descent through the clouds, the sea's, and
    // MetalFX smeared their edges. Where they hide most of what is behind them, the driver takes their own motion
    private static FieldInfo? _isMain, _writeFlip, _colorFlip, _colorFlop, _motionFlip, _motionFlop;
    private static VkImage _cloudColor, _cloudMotion;
    private static bool _cloudsDrawn;

    private static void ApplyCloudMotion(Harmony harmony)
    {
        try
        {
            var resources = AccessTools.Inner(typeof(CloudRenderer), "ViewportResources") ?? throw new Skip("CloudRenderer.ViewportResources not found");
            _isMain = Find.Field(resources, "IsMain");
            _writeFlip = Find.Field(resources, "WriteHistoryToFlip");
            _colorFlip = Find.Field(resources, "UpscaledCloudColorFlipTarget");
            _colorFlop = Find.Field(resources, "UpscaledCloudColorFlopTarget");
            _motionFlip = Find.Field(resources, "UpscaledMotionVectorsFlipTarget");
            _motionFlop = Find.Field(resources, "UpscaledMotionVectorsFlopTarget");
            harmony.Patch(Find.Method(typeof(CloudRenderer), "PerformUpscaling"),
                postfix: new HarmonyMethod(typeof(Upscale), nameof(CloudsUpscaled)));
        }
        catch (Exception e)
        {
            // MetalFX runs on without it, the clouds moving with what is behind them
            Hooks.Log($"MetalFX's cloud motion: off ({Skip.In(e)?.Message ?? e.Message})");
        }
    }

    // CloudRenderer.PerformUpscaling, after it: which set the main view's clouds went into this frame (it has turned the
    // switch for the next frame already)
    private static void CloudsUpscaled(object inResources)
    {
        if (_isMain!.GetValue(inResources) is not true)
            return;
        bool flip = _writeFlip!.GetValue(inResources) is false;
        if ((flip ? _colorFlip : _colorFlop)!.GetValue(inResources) is not RenderImage color ||
            (flip ? _motionFlip : _motionFlop)!.GetValue(inResources) is not RenderImage motion)
            return;
        _cloudColor = color.Image;
        _cloudMotion = motion.Image;
        _cloudsDrawn = true;
    }

    // The game's anti-aliasing. The temporal upscaler smooths edges itself: the game's MSAA under it would cost 1-3 ms
    // more on an M5 and leave dark specks on Earth's views, so with skip_game_anti_aliasing (on by default) the game runs
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

    // Frame interpolation: UiCoverageMaskSystem.RecordMaskGeneration: GameSettings.UiPixelCulling() -> false. With UI
    // pixel culling (the game's default) the scene is left black in 8x8 tiles under opaque UI, and in the frames MetalFX
    // makes, the HUD's labels smeared over that black (and it can warp the tiles out from under the HUD); so the scene
    // is drawn under the UI too
    private static IEnumerable<CodeInstruction> NoUiCullingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var culling = Find.Method(typeof(GameSettings), nameof(GameSettings.UiPixelCulling));
        int at = Find.Once(code, c => c.Calls(culling), "UI pixel culling's switch");
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
    private static bool _reading;  // the composites, as last built, read _output: MetalFX runs (and the jitter with it)
    private static bool _reset = true, _havePrevious;
    private static int _frame;
    private static int _cameraFrame;  // the main view's frames: the ray history counts only from the frame just before
    private static uint _jitterIndex;  // this frame's place in the jitter's sequence, 1 to 16; 0: no jitter
    private static float2 _jitter, _jitterClip;
    private static float4x4 _egoFromClip, _scene, _object, _sky, _nearby, _previousViewProjection;
    private static float4 _sphere, _bodySphere;
    private static double3 _previousCentre;
    private static float _fovY = 1f, _deltaTime;
    private static double4x4 _previousEgoToFrame, _previousEgoToObject;
    private static Vehicle? _previousObject;
    private static Celestial? _previousBody;
    private static CameraMode _previousMode;
    private static object? _previousFollowing;
    private static double _previousTime;
    private static double3 _velocity;  // the camera's, in the nearby body's frame, from the frames before

    // The composite reads the upscaled image instead of the main view's. Not while a high-resolution screenshot renders
    // the main view larger than the display: its composites read the main view's own image, at its size
    private static void AddImagePrefix(ref VkImageView inImageView)
    {
        if (Program.OffscreenTarget is not { ColorImage: { } color } main || !inImageView.Equals(color.ImageView))
            return;
        _reading = RenderScale.Upscalable(main.Extent);
        if (!_reading)
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

    // How far from its centre the body's ground or sea reaches, at most (the game's own bound for what is on it)
    private static double GroundRadius(Celestial body) =>
        body.GetOceanReference() is { } ocean ? Math.Max(body.MaxTerrainRadius, body.MeanRadius + (double)ocean.Level) : body.MaxTerrainRadius;

    // Camera-centred ecliptic coordinates -> the nearby body's rotating frame (or the ecliptic frame in space)
    private static double4x4 EgoToFrame(Camera camera, Celestial? body)
    {
        return body != null
            ? double4x4.CreateTranslation(camera.PositionEcl - body.GetPositionEcl()) * double4x4.CreateFromQuaternion(body.GetCce2Ccf())
            : double4x4.CreateTranslation(camera.PositionEcl);
    }

    // After the main view's camera data is set (RenderScale.ShaderDataPostfix): the jittered projection, and the
    // reprojections from this frame's ego space to the previous frame's clip space
    public static void UpdateCamera(IViewport viewport, ref UboCameraData data)
    {
        Camera camera = viewport.GetCamera();
        int2 size = DynamicResolution.AtLevel(viewport.Size);
        _cameraFrame++;
        float jx = 0f, jy = 0f;
        // A high-resolution screenshot is drawn without jitter, and the history starts again after it
        if (!_reading)
            _reset = true;
        // A cut: into the cockpit or out, to the map or the free camera, to another vessel (one close by moves the camera
        // too little to count as a jump). The last frame's image would show for a few frames over the new view
        if (_havePrevious && (viewport.Mode != _previousMode || !ReferenceEquals(camera.Following, _previousFollowing)))
            _reset = true;
        _previousMode = viewport.Mode;
        _previousFollowing = camera.Following;
        _jitterIndex = 0;
        if (!Spatial && _reading)
        {
            int i = _frame++ % 16 + 1;
            jx = Halton(i, 2) - 0.5f;
            jy = Halton(i, 3) - 0.5f;
            _jitterIndex = (uint)i;
        }
        _jitter = new float2(jx, jy);
        _jitterClip = new float2(2f * jx / size.X, 2f * jy / size.Y);
        float4x4 p = camera.MVP.projection;
        _fovY = 2f * MathF.Atan(1f / MathF.Abs(p.M22));
        _deltaTime = data.DeltaTime;
        AddJitter(ref p);
        float4x4 viewProjection = camera.MVP.view * p;
        float4x4.Invert(p, out var inverseProjection);
        data.Projection = p;
        data.ViewProjection = viewProjection;
        data.InverseProjection = inverseProjection;
        // The inverse in double: the depth range is 0.1 m to 1 AU
        double4x4.Invert(ToDouble(in viewProjection), out var egoFromClip);
        _egoFromClip = float4x4.Pack(in egoFromClip);
        // The sky (stars, the Milky Way, the Sun's disc) is fixed in the ecliptic axes of the ego space: only the
        // camera's turn moves it
        _sky = _havePrevious ? _previousViewProjection : camera.MVP.viewProjection;
        Celestial? body = camera.GetEffectiveNearbyCelestial();
        double4x4 egoToFrame = EgoToFrame(camera, body);
        double time = Universe.GetElapsedSeconds();
        if (_havePrevious && body == _previousBody)
        {
            double4x4.Invert(_previousEgoToFrame, out var frameToPreviousEgo);
            double4x4 delta = egoToFrame * frameToPreviousEgo;
            _scene = float4x4.Pack(in delta) * _previousViewProjection;
            // A jump is what the camera's own motion doesn't explain: in time warp, an orbit's camera moves hundreds of
            // kilometres a frame. Its position in the frame is where egoToFrame takes the ego origin (its last row)
            var moved = new double3(egoToFrame.M41 - _previousEgoToFrame.M41, egoToFrame.M42 - _previousEgoToFrame.M42,
                egoToFrame.M43 - _previousEgoToFrame.M43);
            double elapsed = time - _previousTime;
            double3 expected = _velocity * elapsed;
            if ((moved - expected).Length() > 5000.0 + 0.25 * expected.Length())
                _reset = true;
            // Learnt after a reset too: otherwise, in time warp, every frame after one would look like a jump
            if (elapsed > 0)
                _velocity = moved / elapsed;
        }
        else
        {
            _scene = camera.MVP.viewProjection;
            _reset = true;
            _velocity = default;
        }
        _object = _scene;
        _nearby = _scene;
        _sphere = default;
        // Above the body's highest ground, what the camera sees beside the vessel it follows (another vessel by it in
        // orbit, a kitten on EVA, a stage falling away) moves with that vessel far more than with the ground below
        _bodySphere = body != null ? new float4(float3.Pack(camera.GetPositionEgo(body)), (float)GroundRadius(body) + 1000f) : default;
        if (camera.Following is Vehicle vehicle)
        {
            double4x4.Invert(vehicle.GetMatrixAsmb2Ego(camera), out var egoToObject);
            double3 centre = camera.GetPositionEgo(vehicle);
            if (_havePrevious && _previousObject == vehicle)
            {
                double4x4.Invert(_previousEgoToObject, out var objectToPreviousEgo);
                double4x4 delta = egoToObject * objectToPreviousEgo;
                _object = float4x4.Pack(in delta) * _previousViewProjection;
                _sphere = new float4(float3.Pack(in centre), vehicle.BoundingSphereRadiusBody * 1.2f + 1f);
                // Moving with the vessel's centre, its axes the ecliptic's: the ego space shifted by how far the
                // vessel moved from the camera
                double4x4 shift = double4x4.CreateTranslation(_previousCentre - centre);
                _nearby = float4x4.Pack(in shift) * _previousViewProjection;
            }
            _previousObject = vehicle;
            _previousEgoToObject = egoToObject;
            _previousCentre = centre;
        }
        else
        {
            _previousObject = null;
        }
        _havePrevious = true;
        _previousBody = body;
        _previousTime = time;
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

    // Before the main view's final composite pass begins: MetalFX, then a screenshot's copy of its image
    private static void AaPreparePostfix(CommandBuffer commandBuffer, int shaderSlot)
    {
        if (shaderSlot != Program.MainViewport.ShaderSlot)
            return;
        Capture? pending = _capture;
        _capture = null;
        UpscaleMainView(commandBuffer);
        if (pending is { } capture && capture.CommandBuffer.Handle.Equals(commandBuffer.Handle))
        {
            CompositeData data = capture.Data;
            capture.Screenshot.OnRenderGameCapture(capture.CommandBuffer, capture.FrameIndex, in data);
        }
    }

    // RenderGame: ScreenshotCapture.OnRenderGameCapture -> DeferCapture. The game copies the screenshot right before its
    // anti-aliasing passes, where MetalFX makes this frame's image: the copy took the frame before's, drawn with the
    // UI that a screenshot leaves out (orbit lines, the gauges in the scene)
    private static IEnumerable<CodeInstruction> CaptureTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var capture = Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.OnRenderGameCapture));
        int at = Find.Once(code, c => c.Calls(capture), "the screenshot's copy");
        code[at] = new CodeInstruction(OpCodes.Call, Find.Method(typeof(Upscale), nameof(DeferCapture)))
            .MoveLabelsFrom(code[at]).MoveBlocksFrom(code[at]);
        return code;
    }

    private record struct Capture(ScreenshotCapture Screenshot, CommandBuffer CommandBuffer, int FrameIndex, CompositeData Data);

    private static Capture? _capture;

    private static void DeferCapture(ScreenshotCapture screenshot, CommandBuffer commandBuffer, int frameIndex, in CompositeData data) =>
        _capture = new Capture(screenshot, commandBuffer, frameIndex, data);

    private static unsafe void UpscaleMainView(CommandBuffer commandBuffer)
    {
        // Only this frame's clouds: in space, or with the clouds off, the upscaler doesn't run, and a frame without
        // MetalFX (a high-resolution screenshot) mustn't leave its clouds to the next
        bool cloudsDrawn = _cloudsDrawn;
        _cloudsDrawn = false;
        if (_output == null || !_reading || Program.OffscreenTarget is not { ColorImage: { } colorImage, DepthImage: { } depthImage } target)
            return;
        VkImage color = colorImage.Image, depth = depthImage.Image, output = _output.Image;
        VkImage cloudColor = cloudsDrawn ? _cloudColor : default, cloudMotion = cloudsDrawn ? _cloudMotion : default;
        VkExtent2D input = target.Extent;
        int2 content = DynamicResolution.AtLevel(new int2(input.Width, input.Height));
        var info = new VkUpscaleInfoKSA
        {
            ContentWidth = (uint)content.X,
            ContentHeight = (uint)content.Y,
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
            Flags = (_reset ? Reset : 0) | (Spatial ? SpatialOnly : 0),
            Exposure = GameSettings.Current.Graphics.TonemapExposure,
            CloudColor = Unsafe.As<VkImage, ulong>(ref cloudColor),
            CloudMotion = Unsafe.As<VkImage, ulong>(ref cloudMotion)
        };
        Put(info.EgoFromClip, in _egoFromClip);
        Put(info.PrevClipFromEgo, in _scene);
        Put(info.PrevClipFromEgoObject, in _object);
        Put(info.PrevClipFromEgoSky, in _sky);
        Put(info.PrevClipFromEgoNearby, in _nearby);
        info.ObjectSphere[0] = _sphere.X;
        info.ObjectSphere[1] = _sphere.Y;
        info.ObjectSphere[2] = _sphere.Z;
        info.ObjectSphere[3] = _sphere.W;
        info.BodySphere[0] = _bodySphere.X;
        info.BodySphere[1] = _bodySphere.Y;
        info.BodySphere[2] = _bodySphere.Z;
        info.BodySphere[3] = _bodySphere.W;
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
        code.InsertRange(at - 1, new[] { loadCommandBuffer, new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Call, Find.Method(typeof(Upscale), nameof(Snapshot))) });
        return code;
    }

    // RenderGame: snapshots right before the sun's bloom and right after the overall bloom, so that the reactive mask
    // takes what the effects changed before the bloom and what is drawn over the scene after it, not the bloom: the glow
    // beside a bright edge (a sunlit hull against space) made the edge reactive, and MetalFX showed it as each frame's
    // jittered samples, flickering
    private static IEnumerable<CodeInstruction> BloomTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var snapshot = Find.Method(typeof(Upscale), nameof(Snapshot));
        int sun = Find.Once(code, c => c.Calls(Find.Method(typeof(SunbloomRenderer), nameof(SunbloomRenderer.Render))), "the sun's bloom");
        int overall = Find.Once(code, c => c.Calls(Find.Method(typeof(OverallBloomRenderer), nameof(OverallBloomRenderer.Render))), "the overall bloom");
        // ldarg.0, ldfld _sunbloomRenderer, ldloc <command buffer>, ...
        int start = code.FindLastIndex(sun, c => c.opcode == OpCodes.Ldarg_0);
        var renderer = Find.Field(typeof(Program), "_sunbloomRenderer");
        if (start < 0 || overall < sun || !code[start + 1].LoadsField(renderer) || !code[start + 2].IsLdloc())
            throw new Skip("the bloom no longer follows the command buffer");
        CodeInstruction LoadCommandBuffer() => new(code[start + 2].opcode, code[start + 2].operand);
        code.InsertRange(overall + 1, new[] { LoadCommandBuffer(), new CodeInstruction(OpCodes.Ldc_I4_2), new CodeInstruction(OpCodes.Call, snapshot) });
        var first = LoadCommandBuffer().MoveLabelsFrom(code[start]);
        code.InsertRange(start, new[] { first, new CodeInstruction(OpCodes.Ldc_I4_1), new CodeInstruction(OpCodes.Call, snapshot) });
        return code;
    }

    // RaytracingRenderer.RunRaytracePass (the cockpit's ray tracing): camera.MVP.projection -> the jittered one. The
    // ray tracing finds each pixel's surface from the prepass depth through the inverse of this projection; without the
    // jitter the prepass was drawn with, each point lands up to half a pixel off its surface, in front or behind,
    // differently every frame, and the shadow rays from behind a surface hit it: the cockpit's ray-traced lighting
    // flickers, most on walls farther away or seen at an angle
    private static IEnumerable<CodeInstruction> RayTracingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var projection = Find.Field(typeof(ViewProjection), nameof(ViewProjection.projection));
        int at = Find.Once(code, c => c.LoadsField(projection), "the ray tracing's projection");
        code.InsertRange(at + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Call, Find.Method(typeof(Upscale), nameof(Jittered)))
        });
        return code;
    }

    // RaytracePipeline.Dispatch (the cockpit's ray tracing, each of its passes): this frame's place in the jitter's
    // sequence in the uniforms' spare Margin, which upscale.edits makes the ray generation's random seed (the game's is 1
    // every frame). The game's rays are fixed to each pixel and its denoise is spatial: with the jitter, each frame moved
    // the same rays across the window's glass and the sunlight on the cockpit's walls flickered in blobs; with a seed for
    // each of the 16 jittered frames, MetalFX gathers 16 sets of rays into the soft shadow
    private static void RayTracingSeedPrefix(ref RaytracePipeline.UniformData uniformData, int inShaderSlot)
    {
        if (inShaderSlot == Program.MainViewport.ShaderSlot)
            uniformData.Margin = _jitterIndex;
    }

    // RaytracingRenderer.ApplyDenoise, after it (the last of the cockpit's ray tracing before its images are read): each
    // lighting image blended with its own history in the driver (vkCmdRayHistoryKSA, src/game/driver/presentation/
    // rayhistory.mm), reprojected as MetalFX's motion is. The game traces them anew every frame at a fraction of the
    // render size and smooths them only in space; with the jitter's seed and the jitter moving where each ray starts,
    // the sun's patches crawled with speckles (a kitten's fur, sampled at half size), the window's rim sparkled and the
    // indirect light's blotches moved from frame to frame, which MetalFX can't settle on: the cockpit's walls shimmered.
    // After the game's smoothing, not before: its a-trous passes compress what is brighter than a knee before averaging,
    // so a history before them would keep more of the indirect light's rare bright samples and brighten the cockpit.
    private static unsafe void RayHistoryPostfix(RaytracingRenderer __instance, CommandBuffer commandBuffer)
    {
        if (!_reading || Program.Instance.PrePassRenderer.OpaquePrePassData is not { } prepass)
            return;
        VkImage depth = prepass.DepthTextureForSampling.Image, normals = prepass.NormalTextureForSampling.Image;
        var info = new VkRayHistoryInfoKSA
        {
            Depth = Unsafe.As<VkImage, ulong>(ref depth),
            Normals = Unsafe.As<VkImage, ulong>(ref normals),
            JitterX = _jitter.X,
            JitterY = _jitter.Y,
            Flags = _reset ? Reset : 0u,
            Frame = (uint)_cameraFrame
        };
        for (int i = 0; i < RayHistory.Length; i++)
        {
            if (RayHistory[i].Frames <= 0f)
                continue;
            VkImage image = (i switch
            {
                0 => __instance.OpaqueDirectLightingImage,
                1 => __instance.OpaqueIndirectLightingImage,
                2 => __instance.OpaqueReflectionImage,
                _ => __instance.TranslucentReflectionImage
            }).Image;
            info.Images[i] = Unsafe.As<VkImage, ulong>(ref image);
            info.MaxFrames[i] = RayHistory[i].Frames;
            info.Clip[i] = RayHistory[i].Clip;
        }
        Put(info.EgoFromClip, in _egoFromClip);
        Put(info.Scene, in _scene);
        Put(info.Object, in _object);
        Put(info.Nearby, in _nearby);
        info.ObjectSphere[0] = _sphere.X; info.ObjectSphere[1] = _sphere.Y; info.ObjectSphere[2] = _sphere.Z; info.ObjectSphere[3] = _sphere.W;
        info.BodySphere[0] = _bodySphere.X; info.BodySphere[1] = _bodySphere.Y; info.BodySphere[2] = _bodySphere.Z; info.BodySphere[3] = _bodySphere.W;
        VkCommandBuffer handle = commandBuffer.Handle;
        vkCmdRayHistoryKSA(Unsafe.As<VkCommandBuffer, nint>(ref handle), ref info);
    }

    // The main view's projection with this frame's jitter, as UpdateCamera gives the camera data
    private static float4x4 Jittered(float4x4 projection, IViewport viewport)
    {
        if (viewport.Type == ViewportType.Main)
            AddJitter(ref projection);
        return projection;
    }

    // Clip x and y moved by the jitter's share of w: the column of w added to theirs, which holds for the perspective
    // projection (w from the view's z) and for the Vehicle Editor's orthographic one (w = 1) alike
    private static void AddJitter(ref float4x4 p)
    {
        p.M11 += _jitterClip.X * p.M14; p.M21 += _jitterClip.X * p.M24; p.M31 += _jitterClip.X * p.M34; p.M41 += _jitterClip.X * p.M44;
        p.M12 += _jitterClip.Y * p.M14; p.M22 += _jitterClip.Y * p.M24; p.M32 += _jitterClip.Y * p.M34; p.M42 += _jitterClip.Y * p.M44;
    }

    // stage 0: before the translucent effects, 1: before the bloom, 2: after it
    private static void Snapshot(CommandBuffer commandBuffer, uint stage)
    {
        if (!_reading || Program.OffscreenTarget is not { ColorImage: { } colorImage })
            return;
        VkImage color = colorImage.Image;
        VkCommandBuffer handle = commandBuffer.Handle;
        vkCmdUpscaleSnapshotKSA(Unsafe.As<VkCommandBuffer, nint>(ref handle), Unsafe.As<VkImage, ulong>(ref color), stage);
    }
}
