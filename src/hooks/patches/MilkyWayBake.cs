using System;
using System.IO;
using System.Reflection;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using RenderCore;
using RenderCore.Shaders;

namespace KsaMetalHooks;

// Milky Way bake. MilkyWay.frag draws the Milky Way's dust clouds for every pixel of the band it covers, from the view
// direction alone: about 90 lookups of a noise texture each, 1 to 2 ms of a frame with much sky on an M5 (5 to 7 at the game's Milky
// Way Quality Ultra). The port's
// shader edit (src/port/lib/convert-shaders.sh, 8) moves the clouds into a function, and makes MilkyWayBake.comp from
// the same file. When MilkyWayRenderer has made its noise texture, the patch makes a strip of the clouds over the
// whole band (8192 x 512), puts it in the noise's place, so the game binds it to the shader, and fills it with the
// bake, again when the game's Milky Way Quality changes (it sets the clouds' octaves). The shader reads the clouds
// from the strip when the texture it is given isn't square: nearly the same image, the clouds sampled from the strip
// instead of worked out at each pixel.
internal static class MilkyWayBake
{
    private const int Width = 8192, Height = 512;
    private static FieldInfo? _noiseImage;
    private static string _shader = "";
    private static RenderImage? _noise, _strip;
    private static int _bakedQuality = -1;
    private static bool _submitted;  // the GPU was given the last bake: the strip may be in use
    private static bool _failed;  // no bake from then on (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        string shaders = Path.Combine(Hooks.Resources, "Content", "Core", "Shaders");
        _shader = Path.Combine(shaders, "MilkyWayBake.comp");
        if (!File.Exists(_shader) || !File.ReadAllText(Path.Combine(shaders, "MilkyWay.frag")).Contains("portMilkyWayCloudsAt"))
            throw new Skip("the port's Milky Way shader edit isn't there");
        _noiseImage = Find.Field(typeof(MilkyWayRenderer), "_noiseImage");
        harmony.Patch(Find.Method(typeof(MilkyWayRenderer), "GenerateNoise"),
            postfix: new HarmonyMethod(typeof(MilkyWayBake), nameof(GenerateNoisePostfix)));
        harmony.Patch(Find.Method(typeof(MilkyWayRenderer), "BuildPipeline"),
            postfix: new HarmonyMethod(typeof(MilkyWayBake), nameof(BuildPipelinePostfix)));
        harmony.Patch(Find.Method(typeof(MilkyWayRenderer), nameof(MilkyWayRenderer.Dispose)),
            postfix: new HarmonyMethod(typeof(MilkyWayBake), nameof(DisposePostfix)));
        return "on";
    }

    // The game made its noise: the strip, filled, takes its place for the constructor to bind
    private static void GenerateNoisePostfix(MilkyWayRenderer __instance)
    {
        if (_failed)
            return;
        try
        {
            _noise = (RenderImage)_noiseImage!.GetValue(__instance)!;
            _strip = RenderImage.CreateColorStorage(Program.GetRenderer(), "MilkyWayRenderer.PortClouds",
                new VkExtent2D(Width, Height), VkFormat.R16SFloat);
            _bakedQuality = -1;
            Bake(GameSettings.GetMilkyWayQuality());
            _noiseImage.SetValue(__instance, _strip);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("milky_way_bake", e);
            if (!_submitted)
                _strip?.Dispose();
            _strip = null;
        }
    }

    // A rebuild's pipeline for another Milky Way Quality: the strip is filled again. When that fails, the sky keeps
    // the clouds of the quality before
    private static void BuildPipelinePostfix()
    {
        if (_failed || _strip == null || GameSettings.GetMilkyWayQuality() == _bakedQuality)
            return;
        try
        {
            Bake(GameSettings.GetMilkyWayQuality());
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("milky_way_bake", e);
        }
    }

    // The game disposed the strip as its noise: the noise goes too
    private static void DisposePostfix()
    {
        if (_strip == null)
            return;
        _noise?.Dispose();
        _noise = _strip = null;
    }

    // MilkyWayBake.comp over the strip, as GenerateNoise fills the noise: one submit, waited for. Filled again, it
    // waits for the GPU first, which may still draw the sky from it. The wrapper describes the noise in the General
    // layout while it is in ShaderReadOnlyOptimal: KSA Metal has no image layouts.
    private static unsafe void Bake(int quality)
    {
        var renderer = Program.GetRenderer();
        if (_bakedQuality >= 0)
            renderer.Device.WaitIdle();
        _submitted = false;
        VkShaderModule? module = null;
        ComputePipelineWrapper? pipeline = null;
        CommandBuffer? commandBuffer = null;
        VkFence? fence = null;
        try
        {
            module = ShaderModuleUtils.FromFile(renderer.Device, _shader, out _,
                ShaderCompilerResolve.GetCompileOptionsWithIncludeCallback());
            var reference = new ShaderReference();
            Find.Setter(typeof(ShaderReference), nameof(ShaderReference.Shader)).Invoke(reference, [module]);
            var entry = new VkSpecializationMapEntry { ConstantID = 0, Offset = ByteSize.Zero, Size = ByteSize.Of<int>() };
            var specialization = new VkSpecializationInfo
            {
                MapEntryCount = 1,
                MapEntries = &entry,
                DataSize = ByteSize.Of<int>(),
                Data = &quality
            };
            IRenderImage strip = _strip!, noise = _noise!;
            pipeline = new ComputePipelineWrapper(new Span<IRenderImage>(ref strip), default, default, default, reference,
                default, default, renderer.MaxFramesInFlight, renderer, "MilkyWayRenderer.PortBake",
                Program.LinearClampedSampler, Program.LinearClampedSampler, specializationInfo: specialization,
                colorSamplerLinearRepeatRenderTargets: new Span<IRenderImage>(ref noise),
                linearRepeatSampler: Program.LinearRepeatSampler);
            CommandBuffer commands = renderer.Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo
            {
                CommandPool = renderer.GraphicsAndComputeCommandPool,
                Level = VkCommandBufferLevel.Primary
            });
            commandBuffer = commands;
            commands.Begin();
            Span<VkImageMemoryBarrier2> images = stackalloc VkImageMemoryBarrier2[1];
            var barriers = new BarrierBatch(images);
            barriers.Add(_strip!, ImageBarrierInfo.Presets.StorageWriteC);
            barriers.SubmitAndFlush(commands);
            pipeline.BindPipeline(commands, 0, default, default);
            commands.Dispatch((Width + 7) / 8, (Height + 7) / 8, 1);
            barriers.Add(_strip!, ImageBarrierInfo.Presets.SampledReadF);
            barriers.SubmitAndFlush(commands);
            commands.End();
            fence = renderer.Device.CreateFence(VkFenceCreateFlags.None, null);
            _submitted = true;
            renderer.Graphics.Submit(default, default, new Span<CommandBuffer>(ref commands), default, fence.Value);
            renderer.Device.WaitForFence(fence.Value, -1);
            _bakedQuality = quality;
        }
        finally
        {
            // What a bake the GPU may still run uses stays
            if (!_submitted || _bakedQuality == quality)
            {
                if (fence is { } f)
                    renderer.Device.DestroyFence(f, null);
                if (commandBuffer is { } c)
                    renderer.Device.FreeCommandBuffers(renderer.GraphicsAndComputeCommandPool, new ReadOnlySpan<CommandBuffer>(ref c));
                pipeline?.Destroy(renderer.Device);
                if (module is { } m)
                    renderer.Device.DestroyShaderModule(m, null);
            }
        }
    }
}
