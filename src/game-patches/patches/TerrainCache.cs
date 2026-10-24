using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Rendering.Planet;

namespace KsaPatches;

// Terrain cache. Every frame, PlanetRenderer.GenerateMeshData recomputes the heights of the nearby body's terrain mesh
// on the GPU: a prepare pass, one pass per terrain modifier (noise, craters, erosion...), then a finalize and a normals
// pass. The first two depend on the body, the mesh's level of detail and its snapped texture rotation, which change
// only now and then, and on the streamed heightmap. When the frame slot's height buffer already holds the result for
// the same inputs, the prepare and modifier passes are skipped; every 2 s they run anyway, for the heightmap. Finalize
// and normals always run. The image is the same.
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
    private static readonly Key[] Keys = new Key[8];
    private static System.Reflection.FieldInfo? _buffers;

    [ThreadStatic] private static bool _skip;

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
        var ours = Find.Method(typeof(TerrainCache), nameof(Dispatch));
        foreach (string pipeline in new[] { "_meshVertexPreparePipeline", "_proceduralModifierTerrainPipelines" })
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
        _skip = false;
        PlanetViewportData data = __instance.ViewportData(inViewport);
        Celestial? body = data.NearbyCelestial;
        if (body == null || !body.IsBillboarded() || (uint)frameIndex >= Keys.Length)
            return;
        object? buffer = (_buffers!.GetValue(__instance) as Array)?.GetValue(frameIndex);
        int mesh = body.BodyTemplate.MeshCollection.Get(data.MeshLodIndex).Hash.GetHashCode();
        float4x4 texture = data.MeshPushConst.TextureMatrix;
        texture.M44 = 1f;  // the modifier loop passes its index there
        long now = Environment.TickCount64;
        ref Key key = ref Keys[frameIndex];
        if (buffer != null && Equals(key.Buffer, buffer) && key.Body == body && key.Mesh == mesh && key.Texture == texture &&
            now - key.Time < RefreshMs)
        {
            _skip = true;
            return;
        }
        key = new Key { Buffer = buffer, Body = body, Mesh = mesh, Texture = texture, Time = now };
    }

    private static void Dispatch(CommandBuffer handle, int groupCountX, int groupCountY, int groupCountZ)
    {
        if (!_skip)
            handle.Dispatch(groupCountX, groupCountY, groupCountZ);
    }
}
