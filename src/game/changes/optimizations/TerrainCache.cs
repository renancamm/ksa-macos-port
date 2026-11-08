using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Planet;
using RenderCore;

namespace KsaMetal.Hooks.Optimizations;

// Terrain cache. Every frame, PlanetRenderer.GenerateMeshData recomputes the heights of the nearby body's terrain mesh
// on the GPU: a prepare pass, one pass per terrain modifier (noise, craters, erosion...), then a finalize and a normals
// pass. The first two depend on the body, the mesh's level of detail and its snapped texture rotation, which change
// only now and then, and on the streamed heightmap. When the frame slot's height buffer already holds the result for
// the same inputs, the prepare and modifier passes are skipped; every 2 s they run anyway, for the heightmap. The
// finalize pass always runs: it moves the mesh to the camera. The normals pass computes normals from those positions,
// where the camera's translation cancels out: it is skipped too while the mesh's rotation (the snapped billboard and
// the planet's spin) has turned less than 1e-4 rad since the slot's normals were made. The image is the same.
// The finalize pass also turns the vertex colours the modifiers added (premultiplied) into straight colours, in place:
// when the passes before it are skipped, a build of it that leaves them as they are (terrain-cache.edits,
// PORT_TERRAIN_CACHE_HIT) is bound instead, made the first time the renderer generates a mesh and destroyed with it.
[Hook(Phase.Default, Switch = nameof(Settings.TerrainCache))]
internal static class TerrainCache
{
    private struct Key
    {
        public object? Buffer;
        public Celestial? Body;
        public int Mesh;
        public object? Buffers;  // the level's mesh: FarTerrain gives a level new buffers under the same hash
        public float4x4 Texture;
        public long Time;
    }

    private const long RefreshMs = 2000;
    private const float NormalTolerance = 1e-4f;
    private static readonly Key[] Keys = new Key[8];
    private static readonly float4x4[] NormalRotations = new float4x4[8];
    private static System.Reflection.FieldInfo? _buffers, _layout;
    private static AccessTools.FieldRef<PlanetRenderer, VkPipeline>? _finalize;
    // The finalize pass's build for a skip, and the renderer it is made for
    private static VkPipeline _keepColors;
    private static PlanetRenderer? _keepColorsFor;

    [ThreadStatic] private static bool _skip, _skipNormals;
    private static bool _failed;  // the passes run every frame, as without the patch (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        var generate = Find.Method(typeof(PlanetRenderer), "GenerateMeshData");
        _buffers = Find.Field(typeof(PlanetRenderer), "_heightAndGradientBuffers");
        _layout = Find.Field(typeof(PlanetRenderer), "_meshPipelineLayout");
        _finalize = AccessTools.FieldRefAccess<PlanetRenderer, VkPipeline>(Find.Field(typeof(PlanetRenderer), "_meshVertexFinalizePipeline"));
        Find.Method(typeof(ComputePipelineWrapper), nameof(ComputePipelineWrapper.BuildPipeline));
        harmony.Patch(generate, prefix: new HarmonyMethod(typeof(TerrainCache), nameof(Prefix)),
            transpiler: new HarmonyMethod(typeof(TerrainCache), nameof(Transpiler)));
        harmony.Patch(Find.Method(typeof(PlanetRenderer), nameof(PlanetRenderer.Dispose)),
            prefix: new HarmonyMethod(typeof(TerrainCache), nameof(Disposing)));
        return "on";
    }

    // The dispatch of the prepare pass and the one in the modifier loop go through Dispatch below
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        // VkDeviceExtensions.Dispatch<T>, made for CommandBuffer at the call
        bool isDispatch(CodeInstruction c) => c.operand is System.Reflection.MethodInfo m && m.Name == "Dispatch" &&
            m.DeclaringType == typeof(VkDeviceExtensions) && m.GetParameters() is { Length: 4 } p && p[0].ParameterType == typeof(CommandBuffer);
        var heights = Find.Method(typeof(TerrainCache), nameof(Dispatch));
        var normals = Find.Method(typeof(TerrainCache), nameof(DispatchNormals));
        foreach (var (pipeline, ours) in new[] { ("_meshVertexPreparePipeline", heights),
                     ("_proceduralModifierTerrainPipelines", heights), ("_meshNormalPipeline", normals) })
        {
            var field = Find.Field(typeof(PlanetRenderer), pipeline);
            int load = Find.Once(code, c => c.LoadsField(field), pipeline);
            int call = Find.Next(code, isDispatch, $"the dispatch after {pipeline}", load);
            code[call] = new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(code[call]).MoveBlocksFrom(code[call]);
        }
        // The finalize pipeline bound: the colour-keeping build on a skip
        var finalize = Find.Field(typeof(PlanetRenderer), "_meshVertexFinalizePipeline");
        int bind = Find.Once(code, c => c.LoadsField(finalize), "_meshVertexFinalizePipeline");
        code[bind] = new CodeInstruction(OpCodes.Call, Find.Method(typeof(TerrainCache), nameof(FinalizePipeline))).MoveLabelsFrom(code[bind]);
        return code;
    }

    private static void Prefix(PlanetRenderer __instance, IViewport inViewport, int frameIndex)
    {
        _skip = _skipNormals = false;
        if (_failed)
            return;
        try
        {
            if (_keepColorsFor != __instance)
                BuildKeepColors(__instance);
            Compare(__instance, inViewport, frameIndex);
        }
        catch (Exception e)
        {
            _skip = _skipNormals = false;
            _failed = true;
            Hooks.Fail(nameof(TerrainCache), e);
        }
    }

    // Whether the frame slot's buffer holds this frame's heights (_skip) and normals (_skipNormals) already
    private static void Compare(PlanetRenderer __instance, IViewport inViewport, int frameIndex)
    {
        PlanetViewportData data = __instance.ViewportData(inViewport);
        Celestial? body = data.NearbyCelestial;
        if (body == null || !body.IsBillboarded() || body.BodyTemplate.MeshCollection?.Value is not { } meshes ||
            (uint)frameIndex >= Keys.Length)
            return;
        object? buffer = (_buffers!.GetValue(__instance) as Array)?.GetValue(frameIndex);
        var level = meshes.Get(data.MeshLodIndex);
        int mesh = level.Hash.GetHashCode();
        float4x4 texture = data.MeshPushConst.TextureMatrix;
        texture.M44 = 1f;  // the modifier loop passes its index there
        long now = Environment.TickCount64;
        ref Key key = ref Keys[frameIndex];
        float4x4 rotation = data.MeshPushConst.MeshMatrix;
        if (buffer != null && Equals(key.Buffer, buffer) && key.Body == body && key.Mesh == mesh && ReferenceEquals(key.Buffers, level.Mesh) &&
            key.Texture == texture &&
            now - key.Time < RefreshMs)
        {
            _skip = true;
            _skipNormals = Turned(NormalRotations[frameIndex], rotation) < NormalTolerance;
            if (!_skipNormals)
                NormalRotations[frameIndex] = rotation;
            return;
        }
        key = new Key { Buffer = buffer, Body = body, Mesh = mesh, Buffers = level.Mesh, Texture = texture, Time = now };
        NormalRotations[frameIndex] = rotation;
    }

    // The finalize shader as the port edits it, built with PORT_TERRAIN_CACHE_HIT. A game without the edit would get its
    // own shader again, which on a skip turns the colours twice: the hook fails then (the passes always run)
    private static void BuildKeepColors(PlanetRenderer renderer)
    {
        DestroyKeepColors();
        var shader = ModLibrary.Get<ShaderReference>("FinalizeModifiersComp") ??
            throw new InvalidOperationException("no FinalizeModifiersComp shader");
        if (!System.IO.File.ReadAllText(shader.ModPath).Contains("PORT_TERRAIN_CACHE_HIT"))
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(shader.ModPath)} lacks the port's edit");
        using var options = ShaderModuleUtils.CloneDefaultCompileOptions();
        options.AddMacroDefinition("PORT_TERRAIN_CACHE_HIT"u8, ReadOnlySpan<byte>.Empty);
        _keepColors = ComputePipelineWrapper.BuildPipeline((VkPipelineLayout)_layout!.GetValue(renderer)!, shader,
            Program.GetRenderer(), null, options);
        _keepColorsFor = renderer;
    }

    private static VkPipeline FinalizePipeline(PlanetRenderer renderer) =>
        _skip && _keepColorsFor == renderer ? _keepColors : _finalize!(renderer);

    private static void Disposing(PlanetRenderer __instance)
    {
        if (_keepColorsFor == __instance)
            DestroyKeepColors();
    }

    private static void DestroyKeepColors()
    {
        if (_keepColorsFor == null)
            return;
        Program.GetRenderer().Device.DestroyPipeline(_keepColors, null);
        _keepColors = default;
        _keepColorsFor = null;
    }

    // How far the rotation part of b differs from a's (largest change of a unit axis), with the scale divided out
    private static float Turned(in float4x4 a, in float4x4 b)
    {
        float scale = MathF.Sqrt(b.M11 * b.M11 + b.M12 * b.M12 + b.M13 * b.M13);
        if (scale <= 0f)
            return float.PositiveInfinity;
        float d = MathF.Max(MathF.Max(MathF.Abs(a.M11 - b.M11), MathF.Abs(a.M12 - b.M12)), MathF.Abs(a.M13 - b.M13));
        d = MathF.Max(d, MathF.Max(MathF.Max(MathF.Abs(a.M21 - b.M21), MathF.Abs(a.M22 - b.M22)), MathF.Abs(a.M23 - b.M23)));
        d = MathF.Max(d, MathF.Max(MathF.Max(MathF.Abs(a.M31 - b.M31), MathF.Abs(a.M32 - b.M32)), MathF.Abs(a.M33 - b.M33)));
        return d / scale;
    }

    private static void Dispatch(CommandBuffer handle, int groupCountX, int groupCountY, int groupCountZ)
    {
        if (!_skip)
            handle.Dispatch(groupCountX, groupCountY, groupCountZ);
    }

    private static void DispatchNormals(CommandBuffer handle, int groupCountX, int groupCountY, int groupCountZ)
    {
        if (!_skipNormals)
            handle.Dispatch(groupCountX, groupCountY, groupCountZ);
    }
}
