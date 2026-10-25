using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

// KSA Metal Hooks' entry point: the launcher names this assembly in DOTNET_STARTUP_HOOKS, so .NET runs Initialize
// before the program it starts (KSA, or StarMap, which loads KSA itself). It references nothing of the game: it waits
// until KSA.dll is loaded, then loads KsaMetalHooks.dll from its own folder into the same load context and applies it.
// Harmony is not loaded before that: StarMap loads its own copy at start, and the hooks then use that one.
internal class StartupHook
{
    private static bool _applied;

    public static void Initialize()
    {
        string folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        AppDomain.CurrentDomain.AssemblyLoad += (_, e) => Apply(e.LoadedAssembly, folder);
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            Apply(assembly, folder);
    }

    private static void Apply(Assembly assembly, string folder)
    {
        if (_applied || assembly.GetName().Name != "KSA")
            return;
        _applied = true;
        try
        {
            AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(assembly)!;
            // KsaMetalHooks.dll's references that the context can't find (Harmony, without StarMap) are in this folder
            context.Resolving += (ctx, name) =>
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
            };
            Assembly hooks = context.LoadFromAssemblyPath(Path.Combine(folder, "KsaMetalHooks.dll"));
            hooks.GetType("KsaMetalHooks.Hooks", true)!.GetMethod("Apply")!.Invoke(null, null);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[hooks] not applied: {(e as TargetInvocationException)?.InnerException ?? e}");
        }
    }
}
