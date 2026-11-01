using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace KsaMetalHooks;

// Harmony patches on generic methods, on arm64. Instantiations over reference types share one body, which takes a
// hidden generic context; a method's own entry point is an instantiating stub that loads the context and jumps there.
// Harmony's detour library (MonoMod) walks from the entry point to the code it detours. It recognizes this stub on x64,
// not on arm64, so here the detour goes on the stub. Calls that go straight to the shared body then skip the patch, and
// calls through the stub come without a context that MonoMod's glue drops anyway, which shifts the arguments by one.
//
// So every detour on a shared instantiation starts from the shared method, read from the stub's jump: the patch runs
// from every caller, and MonoMod recognizes the method when tiered compilation compiles it again (on x64 it doesn't,
// and the patch stops running). As on x64, a patch on M<string> then also runs for M<object>, with T = string, and of
// patches on both instantiations only the last one made runs.
//
// A method whose arguments spill to the stack has a compiled stub, which can't be read, and neither can a stub of an
// unknown shape. The detour stays on that stub without dropping a context: virtual calls, delegates and reflection see
// the right arguments, direct calls skip the patch, and the log says so. Limits of MonoMod's own, with or without this:
// a shared method returning a struct through a return buffer can't be patched (its glue is invalid on arm64:
// InvalidProgramException), neither can a virtual method of a generic struct over a reference type (NotSupportedException).
// Constructors stay as they are.
//
// Applied before any mod patches. It changes MonoMod's private code in the Harmony the game's context loaded; a mod that
// brings its own copy of Harmony isn't covered.
internal static unsafe class GenericDetours
{
    private static readonly Type? Canon = typeof(object).Assembly.GetType("System.__Canon");
    // Under this lock: what the shared methods' reflection objects really stand for, and the methods left on their stub
    private static readonly Dictionary<MethodBase, RuntimeMethodHandle> SharedHandles = new();
    private static readonly HashSet<MethodBase> LeftOnStub = new();

    private static object _triple = null!;
    private static MethodInfo _compile = null!;
    private static MethodInfo _nativeBody = null!;
    private static MethodInfo _disableInlining = null!;

    public static string Apply(Harmony harmony)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            return "off, not on arm64";
        Type? triple = AccessTools.TypeByName("MonoMod.Core.Platforms.PlatformTriple");
        Type? runtime = AccessTools.TypeByName("MonoMod.Core.Platforms.Runtimes.FxCoreBaseRuntime");
        Type? detour = AccessTools.TypeByName("MonoMod.Core.Platforms.PlatformTripleDetourFactory")
            ?.GetNestedType("Detour", BindingFlags.NonPublic);
        ConstructorInfo? detourCtor = detour?.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(c => c.GetParameters().Select(p => p.ParameterType.Name).SequenceEqual(
                new[] { "PlatformTriple", nameof(MethodBase), nameof(MethodBase) }));
        object? current = triple == null ? null : AccessTools.PropertyGetter(triple, "Current")?.Invoke(null, null);
        MethodInfo? compile = triple == null ? null : AccessTools.Method(triple, "Compile");
        MethodInfo? nativeBody = triple == null ? null : AccessTools.Method(triple, "GetNativeMethodBody");
        MethodInfo? disableInlining = triple == null ? null : AccessTools.Method(triple, "TryDisableInlining");
        MethodInfo? methodHandle = runtime == null ? null : AccessTools.Method(runtime, "GetMethodHandle");
        MethodInfo? requiresContext = runtime == null ? null : AccessTools.Method(runtime, "RequiresGenericContext");
        if (Canon == null || current == null || compile == null || nativeBody == null || disableInlining == null ||
            detourCtor == null || methodHandle == null || requiresContext == null)
            return "off, this Harmony's detour library isn't the one it knows";
        _triple = current;
        _compile = compile;
        _nativeBody = nativeBody;
        _disableInlining = disableInlining;
        harmony.Patch(methodHandle, prefix: new HarmonyMethod(typeof(GenericDetours), nameof(MethodHandlePrefix)));
        harmony.Patch(requiresContext, prefix: new HarmonyMethod(typeof(GenericDetours), nameof(RequiresContextPrefix)));
        harmony.Patch(detourCtor, prefix: new HarmonyMethod(typeof(GenericDetours), nameof(DetourPrefix)));
        return "on";
    }

    // The detour's source (its second argument) becomes the shared method, as the compile hook will name it
    private static void DetourPrefix(object __0, ref MethodBase __1)
    {
        try
        {
            if (__1 is MethodInfo method && SharedForm(method) is { } shared)
            {
                _disableInlining.Invoke(__0, new object[] { shared });
                __1 = shared;
            }
        }
        catch (Exception e)
        {
            Hooks.Log($"generic detours: {__1} left as it is ({Hooks.Describe(e)})");
        }
    }

    // A detour's way to its method's code and flags is this handle
    private static bool MethodHandlePrefix(MethodBase method, ref RuntimeMethodHandle __result)
    {
        lock (SharedHandles)
        {
            if (!SharedHandles.TryGetValue(method, out RuntimeMethodHandle handle))
                return true;
            __result = handle;
            return false;
        }
    }

    private static bool RequiresContextPrefix(MethodBase method, ref bool __result)
    {
        lock (SharedHandles)
        {
            if (!LeftOnStub.Contains(method))
                return true;
            __result = false;
            return false;
        }
    }

    // The shared method of an instantiation, or null when it has none or it can't be found
    private static MethodBase? SharedForm(MethodInfo method)
    {
        if (method.DeclaringType is not { } declaring)
            return null;
        bool sharedType = declaring.IsGenericType && declaring.GetGenericArguments().Any(t => CanonForm(t) != t);
        bool sharedMethod = method.IsGenericMethod && method.GetGenericArguments().Any(t => CanonForm(t) != t);
        if (!sharedType && !sharedMethod)
            return null;
        RuntimeTypeHandle canonType = (declaring.IsGenericType ? CanonForm(declaring, true) : declaring).TypeHandle;
        // A non-generic instance method of a generic class has no stub: its context is `this`, its handle the shared method's
        if (!method.IsGenericMethod && !method.IsStatic && !declaring.IsValueType && !declaring.IsInterface)
            return MethodBase.GetMethodFromHandle(method.MethodHandle, canonType);
        _compile.Invoke(_triple, new object[] { method });
        nint desc = SharedDescOfStub((nint)_nativeBody.Invoke(_triple, new object[] { method })!);
        RuntimeMethodHandle handle = desc == 0 ? default : RuntimeMethodHandle.FromIntPtr(desc);
        MethodBase? shared = desc == 0 ? null : MethodBase.GetMethodFromHandle(handle, canonType);
        lock (SharedHandles)
        {
            if (shared == null || shared.MetadataToken != method.MetadataToken)
            {
                LeftOnStub.Add(method);
                Hooks.Log($"generic detours: {declaring.Name}.{method} stays on its stub, so direct calls skip its patches");
                return null;
            }
            SharedHandles[shared] = handle;
        }
        return shared;
    }

    // What an instantiation shares with: reference types become __Canon, in structs' arguments too
    private static Type CanonForm(Type type, bool declaring = false)
    {
        if (!declaring && !type.IsValueType)
            return Canon!;
        return type.IsGenericType
            ? type.GetGenericTypeDefinition().MakeGenericType(type.GetGenericArguments().Select(t => CanonForm(t)).ToArray())
            : type;
    }

    // An instantiating stub: register moves and the context's constant, ldr x16 and br x16. It jumps to the shared
    // method's FixupPrecode: ldr x11 (target), br x11, dmb, ldr x12 (the method), which the runtime reads when the
    // method runs for the first time. The address of the shared method, or 0 for any other code.
    private static nint SharedDescOfStub(nint stub)
    {
        const uint OrrRegister = 0xAA0003E0, OrrRegisterMask = 0xFFE0FFE0;
        const uint Movz = 0xD2800000, Movk = 0xF2800000, MoveWideMask = 0xFF800000;
        const uint BrX16 = 0xD61F0200, BrX11 = 0xD61F0160, DmbIshld = 0xD50339BF;
        for (nint at = stub; at < stub + 16 * sizeof(uint); at += sizeof(uint))
        {
            uint word = *(uint*)at;
            if (LiteralOfLdr(at, 16) is var literal && literal != 0 && ((uint*)at)[1] == BrX16)
            {
                nint precode = *(nint*)literal;
                nint slot = ((uint*)precode)[1] == BrX11 && ((uint*)precode)[2] == DmbIshld && LiteralOfLdr(precode, 11) != 0
                    ? LiteralOfLdr(precode + 3 * sizeof(uint), 12)
                    : 0;
                return slot == 0 ? 0 : *(nint*)slot;
            }
            if ((word & OrrRegisterMask) != OrrRegister && (word & MoveWideMask) != Movz && (word & MoveWideMask) != Movk)
                return 0;
        }
        return 0;
    }

    // The address an `ldr x<register>, literal` loads from, or 0 for another instruction
    private static nint LiteralOfLdr(nint at, uint register)
    {
        uint word = *(uint*)at;
        if ((word & 0xFF00001F) != (0x58000000 | register))
            return 0;
        int words = (int)(word << 8) >> 13;
        return at + words * sizeof(uint);
    }
}
