using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

// KSA Metal's entry table (setup/entries.inc, included by setup/entry.mm): the names vkGetInstanceProcAddr and
// vkGetDeviceProcAddr answer, made from Vulkan's registry (vk.xml, from Vulkan-Headers) and the driver's own sources:
//
//   - every Vulkan function the driver defines (VKFN), at the level the registry gives it: global (vkCreateInstance,
//     vkEnumerateInstance*, vkGetInstanceProcAddr), instance (dispatched on VkInstance or VkPhysicalDevice) or device;
//   - the other names the registry gives those functions (vkGetPhysicalDeviceProperties2KHR), when one of the
//     extensions the driver advertises (instance.mm's lists) brings them, and those aliases.txt names besides.
//
//   KsaMetalEntries generate <vk.xml> <driver folder>   entries.inc written again when it differs
//   KsaMetalEntries check <vk.xml> <driver folder>      stale counts as a problem
//
// Exits with 1 on a problem: a function the registry doesn't know, an alias of nothing the driver defines.
internal static class Generate
{
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("generate" or "check"))
        {
            Console.Error.WriteLine("usage: KsaMetalEntries generate | check <vk.xml> <driver folder>");
            return 2;
        }
        try
        {
            string table = Table(args[1], args[2]), path = Path.Combine(args[2], "setup", "entries.inc");
            if (File.Exists(path) && File.ReadAllText(path) == table)
                return 0;
            if (args[0] == "check")
            {
                Console.Error.WriteLine("    src/ksa-metal/setup/entries.inc is not what the registry and the sources " +
                    "make: run ./build.sh and commit it.");
                return 1;
            }
            File.WriteAllText(path, table);
            Console.WriteLine("    src/ksa-metal/setup/entries.inc made again: commit it with the change.");
            return 0;
        }
        catch (Exception e) when (e is IOException or FormatException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static string Table(string registry, string driver)
    {
        var xml = XDocument.Load(registry).Root!;
        // The registry's functions in its order, with the type of their first parameter, and its aliases
        var first = new Dictionary<string, string>();
        var order = new List<string>();
        var aliasOf = new Dictionary<string, string>();
        // Vulkan's own (the registry also has Vulkan SC's, api="vulkansc")
        static bool Vulkan(XElement e) => e.Attribute("api") is not { } api || api.Value.Split(',').Contains("vulkan");
        foreach (var c in xml.Element("commands")!.Elements("command").Where(Vulkan))
        {
            if (c.Attribute("alias") is { } alias)
            {
                aliasOf[c.Attribute("name")!.Value] = alias.Value;
                continue;
            }
            var name = c.Element("proto")!.Element("name")!.Value;
            first.TryAdd(name, c.Element("param")?.Element("type")?.Value ?? "");
            order.Add(name);
        }
        string Target(string a)
        {
            while (aliasOf.TryGetValue(a, out var t))
                a = t;
            return a;
        }

        // The driver's functions and advertised extensions
        var sources = Directory.EnumerateFiles(driver, "*.*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f) is ".mm" or ".cpp" && !Path.GetRelativePath(driver, f).StartsWith("tests"))
            .Select(File.ReadAllText).ToList();
        var defined = sources.SelectMany(s => Regex.Matches(s, @"VKFN\([^)]*\)\s+(vk\w+)\s*\("))
            .Select(m => m.Groups[1].Value).ToHashSet();
        // The extensions the instance and its devices advertise
        var instance = File.ReadAllText(Path.Combine(driver, "setup", "instance.mm"));
        var macros = Regex.Matches(instance, @"\{(VK_\w+_EXTENSION_NAME),").Select(m => m.Groups[1].Value).ToHashSet();
        var brought = new HashSet<string>();
        foreach (var ext in xml.Element("extensions")!.Elements("extension"))
        {
            var requires = ext.Elements("require").ToList();
            var names = requires.SelectMany(r => r.Elements("enum")).Select(e => e.Attribute("name")?.Value ?? "");
            if (names.Any(macros.Contains))
                brought.UnionWith(requires.SelectMany(r => r.Elements("command")).Select(c => c.Attribute("name")!.Value)
                    .ToList());
        }
        var aliases = brought.Where(a => aliasOf.ContainsKey(a) && defined.Contains(Target(a))).ToHashSet();
        foreach (var line in File.ReadAllLines(Path.Combine(driver, "entries", "aliases.txt")))
        {
            var name = line.Split('#')[0].Trim();
            if (name.Length == 0)
                continue;
            if (!aliasOf.ContainsKey(name) || !defined.Contains(Target(name)))
                throw new FormatException($"aliases.txt: {name} is no alias of a function the driver defines");
            aliases.Add(name);
        }

        // Levels: 0 global, 1 instance, 2 device
        int Level(string f) =>
            f is "vkGetInstanceProcAddr" or "vkCreateInstance" || f.StartsWith("vkEnumerateInstance") ? 0
            : first[f] is "VkInstance" or "VkPhysicalDevice" ? 1
            : first[f] is "VkDevice" or "VkQueue" or "VkCommandBuffer" ? 2
            : throw new FormatException($"{f}: dispatched on {first[f]}, of no level");
        var unknown = defined.Where(f => !first.ContainsKey(f) && !f.EndsWith("KSA")).Order().ToList();
        if (unknown.Count > 0)
            throw new FormatException($"the registry doesn't know {string.Join(", ", unknown)} " +
                "(a function of KSA Metal's own ends in KSA)");

        var t = new StringBuilder("""
            // The names vkGetInstanceProcAddr and vkGetDeviceProcAddr answer (entry.mm): made by KsaMetalEntries
            // (entries/Generate.cs) from Vulkan's registry and the driver's sources, which build.sh runs. Don't edit
            // it: define the function (VKFN), advertise the extension that brings an alias, or name the alias in
            // entries/aliases.txt.

            """);
        var levelMacro = new[] { "G", "I", "D" };
        foreach (int level in new[] { 0, 1, 2 })
        {
            foreach (var f in order.Where(f => defined.Contains(f) && Level(f) == level))
            {
                t.Append($"{levelMacro[level]}({f}),\n");
                foreach (var a in aliases.Where(a => Target(a) == f).Order(StringComparer.Ordinal))
                    t.Append($"{levelMacro[level]}A({a}, {f}),\n");
            }
        }
        return t.ToString();
    }
}
