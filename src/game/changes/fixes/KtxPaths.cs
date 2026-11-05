using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using Loader = Brutal.TextureApi.Ktx.Loader;
using KtxTexture = Brutal.TextureApi.Ktx.KtxTexture;

namespace KsaMetal.Hooks.Fixes;

// KTX paths. A bug in the game's texture library: Ktx.Loader.LoadFromFile and KtxTexture.SaveToFile hand libktx the
// file's path as Encoding.UTF8.GetBytes(path), bytes without the terminating zero a C string needs, and libktx reads
// on past the array's end until it meets one. A path whose length is a multiple of 8 ends where the next object in the
// .NET heap begins, and that byte is not always zero: the path then names no file. Seen in the game's load, for the
// 56-byte paths of the parts' texture atlases (CoreCommandA_TextureAtlas_PBR and others), which read with a 0x01 after
// them by the time KSA Metal's KTX shim read them, just after libktx had opened the file. A transpiler gives both the
// same bytes with a zero after them.
[Hook(Phase.Default)]
internal static class KtxPaths
{
    private static MethodInfo _getBytes = null!;

    public static string Apply(Harmony harmony)
    {
        _getBytes = Find.Method(typeof(Encoding), nameof(Encoding.GetBytes), typeof(string));
        var methods = new[]
        {
            Find.Method(typeof(Loader), nameof(Loader.LoadFromFile), typeof(string), typeof(Loader.LoadSettings)),
            Find.Method(typeof(KtxTexture), nameof(KtxTexture.SaveToFile), typeof(string)),
        };
        foreach (var method in methods)
        {
            int calls = PatchProcessor.GetOriginalInstructions(method).Count(i => i.Calls(_getBytes));
            if (calls != 1)
                throw new Skip($"{method.DeclaringType!.Name}.{method.Name}: Encoding.GetBytes found {calls} times");
        }
        foreach (var method in methods)
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(KtxPaths), nameof(Transpiler)));
        return "on";
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var i in instructions)
            yield return i.Calls(_getBytes)
                ? new CodeInstruction(i) { opcode = OpCodes.Call, operand = AccessTools.Method(typeof(KtxPaths), nameof(Terminated)) }
                : i;
    }

    // In place of encoding.GetBytes(path): the same bytes and a zero
    private static byte[] Terminated(Encoding encoding, string path)
    {
        var bytes = new byte[encoding.GetByteCount(path) + 1];
        encoding.GetBytes(path, 0, path.Length, bytes, 0);
        return bytes;
    }
}
