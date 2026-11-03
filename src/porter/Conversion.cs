using System;
using System.IO;
using System.Linq;

namespace KsaPorter;

// The conversion of a KSA game folder for macOS, in the order port.sh makes it: what macOS can't use goes, the .NET
// files run on the app's .NET runtime, the shaders get the port's edits (ShaderEdits.cs). Each step is safe to run
// again, on a fresh game folder or on the one in an app.
public static class Conversion
{
    public static void Game(string game, string runtime)
    {
        Cleanup(game);
        Dotnet(game, runtime);
        ShaderEdits.Apply(game, ShaderEdits.Read(PortFolder.ShaderEditsFolder), Console.WriteLine);
    }

    // Removes the Linux executables and libraries, and the Windows installer files
    public static void Cleanup(string game)
    {
        RequireGame(game);
        // Linux binaries: every top-level file in ELF format (the .so libraries and their links, the KSA launcher,
        // createdump, Brutal.Monitor.Subprocess). macOS cannot load any of them; the port brings macOS builds.
        // All found first: a link is found through the library it points to
        var elf = Directory.GetFileSystemEntries(game).Where(f => !Path.GetFileName(f).StartsWith('.') && IsFile(f) && IsElf(f)).ToList();
        foreach (var f in elf)
            File.Delete(f);
        // The Windows installer's script and signing batch file
        foreach (var f in new[] { "ksa.iss", "ksa_sign.bat" }.Select(n => Path.Combine(game, n)))
        {
            if (Directory.Exists(f) && !IsLink(f))
                Directory.Delete(f, true);
            else if (File.Exists(f) || IsLink(f))
                File.Delete(f);
        }
        Output.Info($"Removed {elf.Count} Linux binaries.");
    }

    // A regular file, or a link to one
    private static bool IsFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget == null)
            return info.Exists;
        return info.ResolveLinkTarget(true) is FileInfo { Exists: true };
    }

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

    private static bool IsElf(string path)
    {
        using var f = File.OpenRead(path);
        Span<byte> magic = stackalloc byte[4];
        return f.Read(magic) == 4 && magic.SequenceEqual("\u007fELF"u8);
    }

    // Makes KSA's .NET files run on the macOS .NET runtime the app bundles. The Linux release is a self-contained
    // linux-x64 .NET 10 program:
    //   1. It carries the Linux build of the .NET runtime libraries (System.*.dll, mscorlib.dll, ...). Left in place,
    //      .NET loads them and fails. They are exactly the DLLs whose names are also in Microsoft's macOS runtime, so
    //      those are removed. The game's own DLLs and its NuGet packages (System.CommandLine, System.IO.Hashing,
    //      Microsoft.Extensions.*) are not in the runtime and stay.
    //   2. KSA.deps.json points the .NET host at the linux-x64 runtime pack: removed.
    //   3. KSA.runtimeconfig.json describes a self-contained program: replaced by one that uses the shared .NET 10
    //      runtime (the one in the app).
    //   4. KSA.dll is a 64-bit assembly marked AMD64, which .NET on Apple Silicon does not load: marked ARM64
    //      (Arm64.Mark). The other game DLLs are platform-neutral and load as they are.
    public static void Dotnet(string game, string runtime)
    {
        RequireGame(game);
        var shared = Path.Combine(runtime, "shared", "Microsoft.NETCore.App");
        var framework = Directory.Exists(shared) ? Directory.GetDirectories(shared).Order(StringComparer.Ordinal).FirstOrDefault() : null;
        if (framework == null)
            throw new PortException($"no .NET runtime in {runtime}.");
        CheckBrutal(game);

        int removed = 0;
        foreach (var dll in Directory.GetFiles(framework, "*.dll"))
        {
            var name = Path.Combine(game, Path.GetFileName(dll));
            if (File.Exists(name))
            {
                File.Delete(name);
                removed++;
            }
        }
        Output.Info($"Removed {removed} Linux .NET runtime libraries.");

        File.Delete(Path.Combine(game, "KSA.deps.json"));
        File.WriteAllText(Path.Combine(game, "KSA.runtimeconfig.json"), """
            {
              "runtimeOptions": {
                "tfm": "net10.0",
                "frameworks": [
                  {
                    "name": "Microsoft.NETCore.App",
                    "version": "10.0.0"
                  }
                ],
                "rollForward": "LatestMinor",
                "configProperties": {
                  "System.Reflection.Metadata.MetadataUpdater.IsSupported": false,
                  "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false
                }
              }
            }

            """);
        Arm64.Mark(Directory.GetFiles(game, "*.dll").Order(StringComparer.Ordinal));
    }

    // The port's BRUTAL libraries come from RocketWerkz's packages listed in lib/brutal.txt, each matching one of the
    // game's BRUTAL assemblies (Ahwoo.BRUTAL.Glfw.Native: Brutal.Glfw.dll). An older game doesn't work with them (KSA
    // 2026.10.13 passes the 2026.10 VMA library no Vulkan functions and crashes); a newer one may expect functions
    // they lack.
    private static void CheckBrutal(string game)
    {
        var list = Path.Combine(PortFolder.Lib, "brutal.txt");
        if (!File.Exists(list))
            throw new PortException($"{list} is missing. Download the port again.");
        foreach (var line in File.ReadAllLines(list).Where(l => l.Trim().Length > 0))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string id = fields[0], version = fields.Length > 1 ? fields[1] : "";
            var assembly = "Brutal." + id["Ahwoo.BRUTAL.".Length..];
            assembly = (assembly.EndsWith(".Native") ? assembly[..^".Native".Length] : assembly) + ".dll";
            var brutal = PortFolder.FirstMatch(Path.Combine(game, assembly), @"20[0-9]{2}\.[0-9]+\.[0-9]+\+[0-9a-f]{40}");
            if (brutal.Length == 0)
                continue;
            brutal = brutal[..brutal.IndexOf('+')];
            if (PortFolder.VersionNewer(version, brutal))
                throw new PortException($"this KSA is older than the port supports ({assembly} {brutal}, the port's library is " +
                    $"{version}). Download the current Linux version of KSA from https://ksa.ahwoo.com.");
            if (PortFolder.VersionNewer(brutal, version))
                Output.Warn($"this KSA uses {assembly} {brutal}, newer than the port's library ({version}). If the game fails " +
                    "to start, the port needs an update.");
        }
    }

    public static void RequireGame(string game)
    {
        if (!File.Exists(Path.Combine(game, "KSA.dll")))
            throw new PortException($"{game} has no KSA.dll.");
    }
}

// Marks 64-bit .NET assemblies built for AMD64 as ARM64, so .NET on Apple Silicon loads them. A 64-bit (PE32+)
// assembly carries the machine type it was built for: AMD64 (0x8664) for KSA.dll and StarMap.dll. .NET on Apple
// Silicon only loads 64-bit assemblies marked ARM64 (0xAA64). These contain only IL code, so changing the mark is
// enough. Platform-neutral (PE32) assemblies load as they are and are left alone, and so are files that are not
// assemblies. Safe to run again.
public static class Arm64
{
    public static void Mark(System.Collections.Generic.IEnumerable<string> files)
    {
        foreach (var dll in files)
        {
            // PE layout: offset 0x3C holds the PE header's offset; the header starts with "PE\0\0", the 2-byte machine
            // type follows, and the optional header's magic (0x20B for PE32+) is 24 bytes in
            byte[] b;
            using (var r = File.OpenRead(dll))
            {
                b = new byte[Math.Min(r.Length, 4096)];
                r.ReadExactly(b);
            }
            if (b.Length < 64)
                continue;
            int pe = BitConverter.ToInt32(b, 60);
            if (pe < 0 || pe + 26 > b.Length || !b.AsSpan(pe, 4).SequenceEqual("PE\0\0"u8) || b[pe + 24] != 0x0b || b[pe + 25] != 0x02)
                continue;
            switch ((b[pe + 4], b[pe + 5]))
            {
                case (0x64, 0x86):
                    using (var f = new FileStream(dll, FileMode.Open, FileAccess.Write))
                    {
                        f.Position = pe + 4;
                        f.Write([0x64, 0xaa]);
                    }
                    Output.Info($"{Path.GetFileName(dll)}: AMD64 -> ARM64");
                    break;
                case (0x64, 0xaa):
                    break;
                default:
                    throw new PortException($"{Path.GetFileName(dll)} has an unexpected machine type. The port needs an update for this version.");
            }
        }
    }
}
