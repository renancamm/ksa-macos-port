using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

// KSA Metal Hooks' entry point: the launcher names this assembly in DOTNET_STARTUP_HOOKS, so .NET runs Initialize
// before the program it starts (KSA, or StarMap, which loads KSA itself). First it applies the port settings
// (PortEnvironment.cs). It references nothing of the game: it waits until KSA.dll is loaded, then loads
// KsaMetalHooks.dll from its own folder into the same load context and applies it, unless the settings turn the hooks
// off (then only for a benchmark run, KSA_BENCH_OUT, which the hooks start). Harmony is not loaded before that:
// StarMap loads its own copy at start, and the hooks then use that one.
internal class StartupHook
{
    private static bool _applied;

    public static void Initialize()
    {
        // Only in the game's process: .NET programs it starts (the benchmark's report) inherit DOTNET_STARTUP_HOOKS
        if (Assembly.GetEntryAssembly()?.GetName().Name is not ("KSA" or "StarMap"))
            return;
        string folder = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;
        bool hooks = true;
        try
        {
            hooks = PortEnvironment.Apply(Path.GetFullPath(Path.Combine(folder, "..")));
        }
        catch (Exception e)
        {
            Console.WriteLine($"[hooks] port settings not applied: {e}");
        }
        if (!hooks && Environment.GetEnvironmentVariable("KSA_BENCH_OUT") is not { Length: > 0 })
            return;
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
            // KsaMetalHooks.dll's references that the context can't find are in this folder (Harmony, without StarMap),
            // or in the benchmark's next to it (its director)
            context.Resolving += (ctx, name) =>
            {
                foreach (string dir in new[] { folder, Path.Combine(folder, "..", "Benchmark") })
                {
                    string path = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(path))
                        return ctx.LoadFromAssemblyPath(Path.GetFullPath(path));
                }
                return null;
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
