using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering.Planet;

namespace KsaMetalHooks;

// Terrain cache. Every frame, PlanetRenderer.GenerateMeshData recomputes the heights of the nearby body's terrain mesh
// on the GPU: a prepare pass, one pass per terrain modifier (noise, craters, erosion...), then a finalize and a normals
// pass. The first two depend on the body, the mesh's level of detail and its snapped texture rotation, which change
// only now and then, and on the streamed heightmap. When the frame slot's height buffer already holds the result for
// the same inputs, the prepare and modifier passes are skipped; every 2 s they run anyway, for the heightmap. The
// finalize pass always runs: it moves the mesh to the camera. The normals pass computes normals from those positions,
// where the camera's translation cancels out: it is skipped too while the mesh's rotation (the snapped billboard and
// the planet's spin) has turned less than 1e-4 rad since the slot's normals were made. The image is the same.
internal static class TerrainCache
{
    private struct Key
    {
        public object? Buffer;
        public Celestial? Body;
        public int Mesh;
        public float4x4 Texture;
        public long Time;
    }

    private const long RefreshMs = 2000;
    private const float NormalTolerance = 1e-4f;
    private static readonly Key[] Keys = new Key[8];
    private static readonly float4x4[] NormalRotations = new float4x4[8];
    private static System.Reflection.FieldInfo? _buffers;

    [ThreadStatic] private static bool _skip, _skipNormals;
    private static bool _failed;  // the passes run every frame, as without the patch (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        var generate = Find.Method(typeof(PlanetRenderer), "GenerateMeshData");
        _buffers = Find.Field(typeof(PlanetRenderer), "_heightAndGradientBuffers");
        harmony.Patch(generate, prefix: new HarmonyMethod(typeof(TerrainCache), nameof(Prefix)),
            transpiler: new HarmonyMethod(typeof(TerrainCache), nameof(Transpiler)));
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
        return code;
    }

    private static void Prefix(PlanetRenderer __instance, IViewport inViewport, int frameIndex)
    {
        _skip = _skipNormals = false;
        if (_failed)
            return;
        try
        {
            Compare(__instance, inViewport, frameIndex);
        }
        catch (Exception e)
        {
            _skip = _skipNormals = false;
            _failed = true;
            Hooks.Fail("terrain_cache", e);
        }
    }

    // Whether the frame slot's buffer holds this frame's heights (_skip) and normals (_skipNormals) already
    private static void Compare(PlanetRenderer __instance, IViewport inViewport, int frameIndex)
    {
        PlanetViewportData data = __instance.ViewportData(inViewport);
        Celestial? body = data.NearbyCelestial;
        if (body == null || !body.IsBillboarded() || body.BodyTemplate.MeshCollection is not { } meshes ||
            (uint)frameIndex >= Keys.Length)
            return;
        object? buffer = (_buffers!.GetValue(__instance) as Array)?.GetValue(frameIndex);
        int mesh = meshes.Get(data.MeshLodIndex).Hash.GetHashCode();
        float4x4 texture = data.MeshPushConst.TextureMatrix;
        texture.M44 = 1f;  // the modifier loop passes its index there
        long now = Environment.TickCount64;
        ref Key key = ref Keys[frameIndex];
        float4x4 rotation = data.MeshPushConst.MeshMatrix;
        if (buffer != null && Equals(key.Buffer, buffer) && key.Body == body && key.Mesh == mesh && key.Texture == texture &&
            now - key.Time < RefreshMs)
        {
            _skip = true;
            _skipNormals = Turned(NormalRotations[frameIndex], rotation) < NormalTolerance;
            if (!_skipNormals)
                NormalRotations[frameIndex] = rotation;
            return;
        }
        key = new Key { Buffer = buffer, Body = body, Mesh = mesh, Texture = texture, Time = now };
        NormalRotations[frameIndex] = rotation;
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
