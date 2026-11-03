using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KsaPort.Common;
using KsaPort.Cli;

// The tests of the port's program that need no game: the shader edits' format and rules on synthetic shaders, the
// port's own .edits files, the ARM64 mark, the cleanup's ELF check and the versions. The build runs them (ctest)
// after the build; a failure stops it.
//
// Usage: dotnet KsaPort.Cli.Tests.dll <repository>
internal static class Tests
{
    private static string _repo = "", _test = "", _scratch = "";
    private static readonly List<string> Failures = new();
    private static readonly List<string> Printed = new();

    private static int Main(string[] args)
    {
        _repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        _scratch = Path.Combine(Path.GetTempPath(), $"ksa-port-cli-tests-{Environment.ProcessId}");
        var tests = new (string Name, Action Run)[]
        {
            ("the port's shader edits", PortEdits),
            ("an edit, made and made again", EditTwice),
            ("an edit that can't be made", EditWarns),
            ("an earlier version's edit", EditEarlier),
            ("done-if", EditDoneIf),
            ("needs", EditNeeds),
            ("checks", Checks),
            ("line endings and bytes", Bytes),
            ("a malformed file", Malformed),
            ("the ARM64 mark", MarkArm64),
            ("the cleanup", Cleanup),
            ("versions", Versions),
        };
        // The port program's messages stay out of the build's output
        var console = Console.Out;
        Console.SetOut(TextWriter.Null);
        foreach (var (name, run) in tests)
        {
            _test = name;
            Directory.CreateDirectory(Path.Combine(_scratch, "Content", "Core", "Shaders"));
            try
            {
                run();
            }
            catch (Exception e)
            {
                Failures.Add($"{name}: {e}");
            }
            finally
            {
                Directory.Delete(_scratch, true);
            }
        }
        Console.SetOut(console);
        foreach (var failure in Failures)
            Console.WriteLine($"FAILED {failure}");
        Console.WriteLine(Failures.Count == 0
            ? $"    The port program's tests: {tests.Length} passed."
            : $"    The port program's tests: {Failures.Count} failures.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            Failures.Add($"{_test}: {what}");
    }

    private static void Equal<T>(T expected, T actual, string what) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");

    private static string Shader(string name) => Path.Combine(_scratch, "Content", "Core", "Shaders", name);

    // The steps of one .edits text applied to the scratch game; the printed lines in Printed
    private static Dictionary<string, ShaderEdits.Status> Apply(string edits)
    {
        Printed.Clear();
        return ShaderEdits.Apply(_scratch, ShaderEdits.Parse(edits, "test.edits"), Printed.Add);
    }

    private const string Replace = """
        # A comment
        @@ edit swap a.frag
        @@ what swapped the call
        @@ find
            x = f();
        @@ replace
            x = g();  // macOS port

        """;

    private static void PortEdits()
    {
        var steps = ShaderEdits.Read(Path.Combine(_repo, "src", "game", "changes"));
        Check(steps.Count > 40, $"only {steps.Count} steps");
        foreach (var s in steps.Where(s => s.Kind == "edit"))
            Check(s.Find != s.Replace, $"{s.Id}: its replacement is its find");
        Check(steps.Any(s => s.Kind == "code" && s.Id == "milky-way"), "no milky-way code edit");
    }

    private static void EditTwice()
    {
        File.WriteAllText(Shader("a.frag"), "void main()\n{\n    x = f();\n}\n");
        Equal(ShaderEdits.Status.Fixed, Apply(Replace)["swap"], "first");
        Equal("    [FIXED] a.frag: swapped the call", Printed.Single(), "first line");
        Equal("void main()\n{\n    x = g();  // macOS port\n}\n", File.ReadAllText(Shader("a.frag")), "edited");
        Equal(ShaderEdits.Status.Ok, Apply(Replace)["swap"], "second");
        Equal("    [OK] a.frag: already swapped the call", Printed.Single(), "second line");
    }

    private static void EditWarns()
    {
        File.WriteAllText(Shader("a.frag"), "    x = f();\n    x = f();\n");
        Equal(ShaderEdits.Status.Warn, Apply(Replace)["swap"], "twice");
        Check(Printed.Single().StartsWith("    [WARN] a.frag: the code changed"), Printed.Single());
        Equal("    x = f();\n    x = f();\n", File.ReadAllText(Shader("a.frag")), "untouched");
        File.Delete(Shader("a.frag"));
        Equal(ShaderEdits.Status.Warn, Apply(Replace)["swap"], "missing");
        Equal("    [WARN] a.frag: not found", Printed.Single(), "missing line");
    }

    private static void EditEarlier()
    {
        File.WriteAllText(Shader("a.frag"), "    x = h();  // old port\n");
        Equal(ShaderEdits.Status.Fixed, Apply(Replace + "@@ earlier\n    x = h();  // old port\n")["swap"], "upgraded");
        Equal("    [FIXED] a.frag: updated: swapped the call", Printed.Single(), "line");
        Equal("    x = g();  // macOS port\n", File.ReadAllText(Shader("a.frag")), "edited");
    }

    private static void EditDoneIf()
    {
        File.WriteAllText(Shader("a.frag"), "    x = f();  // fixed by the game: g\n");
        Equal(ShaderEdits.Status.Ok, Apply(Replace + "@@ done-if fixed by the game\n")["swap"], "done");
        Equal("    x = f();  // fixed by the game: g\n", File.ReadAllText(Shader("a.frag")), "untouched");
        File.WriteAllText(Shader("a.frag"), "    // y = 1;\n    x = f();\n");
        Equal(ShaderEdits.Status.Fixed, Apply(Replace + "@@ done-if-line y = 1;\n")["swap"], "in a comment, not done");
        File.WriteAllText(Shader("a.frag"), "    y = 1;\n    x = f();\n");
        Equal(ShaderEdits.Status.Ok, Apply(Replace + "@@ done-if-line y = 1;\n")["swap"], "a line, done");
    }

    private static void EditNeeds()
    {
        File.WriteAllText(Shader("a.frag"), "nothing here\n");
        File.WriteAllText(Shader("b.frag"), "    y = 1;\n");
        var status = Apply(Replace + """
            @@ edit later b.frag
            @@ what changed y
            @@ needs swap
            @@ find
                y = 1;
            @@ replace
                y = 2;  // macOS port
            """);
        Equal(ShaderEdits.Status.Skipped, status["later"], "skipped");
        Check(Printed.Any(l => l.StartsWith("    [SKIP] b.frag")), string.Join('|', Printed));
        Equal("    y = 1;\n", File.ReadAllText(Shader("b.frag")), "untouched");
    }

    private static void Checks()
    {
        const string check = """
            @@ check uses a.frag
            @@ what the call's skip
            @@ contains
                x = f();
            @@ contains other.glsl
            return x;
            @@ counts x=2,3
            """;
        File.WriteAllText(Shader("a.frag"), "    x = f();\n    y = x;\n");
        File.WriteAllText(Shader("other.glsl"), "return x;\n");
        Equal(ShaderEdits.Status.Ok, Apply(check)["uses"], "holds");
        Check(Printed.Count == 0, "a check that holds prints nothing");
        File.WriteAllText(Shader("a.frag"), "    x = f();\n    y = x + x;\n");
        Equal(ShaderEdits.Status.Warn, Apply(check)["uses"], "a later count, on the game's file");
        File.WriteAllText(Shader("a.frag"), "    x = f();\n    y = x + x;  // !PORT_SKIP_UNLIT_SHADOWS\n");
        Equal(ShaderEdits.Status.Ok, Apply(check)["uses"], "a later count, once edited");
        File.WriteAllText(Shader("other.glsl"), "return y;\n");
        Equal(ShaderEdits.Status.Warn, Apply(check)["uses"], "the other file changed");
        Check(Printed.Single().StartsWith("    [WARN] a.frag: the code changed in this KSA version, skipped: the call's skip"), Printed.Single());
    }

    private static void Bytes()
    {
        var text = Encoding.Latin1.GetBytes("// café ÿ\r\nvoid main()\r\n{\r\n    x = f();\r\n}\r\n");
        File.WriteAllBytes(Shader("a.frag"), text);
        Apply(Replace);
        var expected = Encoding.Latin1.GetBytes("// café ÿ\r\nvoid main()\r\n{\r\n    x = g();  // macOS port\r\n}\r\n");
        Check(File.ReadAllBytes(Shader("a.frag")).SequenceEqual(expected), "CRLF and the other bytes are kept");
        var multi = Replace.Replace("    x = g();  // macOS port", "    x = g();  // macOS port\n    z = 0;");
        File.WriteAllBytes(Shader("a.frag"), text);
        Apply(multi);
        Check(Encoding.Latin1.GetString(File.ReadAllBytes(Shader("a.frag"))).Contains("port\r\n    z = 0;\r\n}"), "a replacement's lines get CRLF");
    }

    private static void Malformed()
    {
        foreach (var (edits, what) in new[]
        {
            ("@@ edit a a.frag\n@@ what w\n@@ find\nx\n", "no replace"),
            ("@@ edit a a.frag\n@@ find\nx\n@@ replace\ny\n", "no what"),
            ("@@ edit a a.frag\n@@ what w\n@@ find\n@@ replace\ny\n", "an empty find"),
            ("@@ frobnicate\n", "an unknown directive"),
            ("@@ edit a a.frag\n@@ what w\n@@ find\nx\n@@ replace\ny\n@@ contains\nz\n", "contains in an edit"),
            ("@@ check c a.frag\n@@ what w\n", "a check of nothing"),
            ("# comment\n\nstray text\n", "text outside a block"),
        })
        {
            try
            {
                ShaderEdits.Parse(edits, "test.edits");
                Check(false, $"{what}: no error");
            }
            catch (FormatException)
            {
            }
        }
    }

    private static void MarkArm64()
    {
        // A minimal PE32+ header: "MZ", the PE header at 0x80, AMD64, the optional header's magic 0x20B
        byte[] Pe(byte m0, byte m1, ushort magic)
        {
            var b = new byte[512];
            b[0] = (byte)'M';
            b[1] = (byte)'Z';
            BitConverter.GetBytes(0x80).CopyTo(b, 60);
            "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
            b[0x84] = m0;
            b[0x85] = m1;
            BitConverter.GetBytes(magic).CopyTo(b, 0x80 + 24);
            return b;
        }
        string amd = Path.Combine(_scratch, "a.dll"), neutral = Path.Combine(_scratch, "n.dll"), text = Path.Combine(_scratch, "t.dll");
        File.WriteAllBytes(amd, Pe(0x64, 0x86, 0x20b));
        File.WriteAllBytes(neutral, Pe(0x4c, 0x01, 0x10b));
        File.WriteAllText(text, "not an assembly");
        Arm64.Mark([amd, neutral, text]);
        Arm64.Mark([amd]);
        var b = File.ReadAllBytes(amd);
        Equal((byte)0xaa, b[0x85], "AMD64 -> ARM64");
        Check(File.ReadAllBytes(neutral).SequenceEqual(Pe(0x4c, 0x01, 0x10b)), "PE32 untouched");
        File.WriteAllBytes(amd, Pe(0x12, 0x34, 0x20b));
        try
        {
            Arm64.Mark([amd]);
            Check(false, "an unexpected machine type: no error");
        }
        catch (PortException)
        {
        }
    }

    private static void Cleanup()
    {
        File.WriteAllText(Path.Combine(_scratch, "KSA.dll"), "MZ");
        File.WriteAllBytes(Path.Combine(_scratch, "libfoo.so.1"), [0x7f, (byte)'E', (byte)'L', (byte)'F', 2]);
        File.CreateSymbolicLink(Path.Combine(_scratch, "libfoo.so"), "libfoo.so.1");
        File.WriteAllText(Path.Combine(_scratch, "ksa.iss"), "installer");
        File.WriteAllText(Path.Combine(_scratch, "notes.txt"), "text");
        Conversion.Cleanup(_scratch);
        var left = Directory.GetFileSystemEntries(_scratch).Select(Path.GetFileName).Order().ToArray();
        Equal("Content KSA.dll notes.txt", string.Join(' ', left), "what is left");
    }

    private static void Versions()
    {
        Check(PortFolder.VersionNewer("2026.10.25.5707", "2026.10.19.5638"), "newer");
        Check(!PortFolder.VersionNewer("2026.10.19", "2026.10.19.0"), "the same");
        Check(PortFolder.VersionNewer("5707.10", "5707.9"), "a number, not text");
        Equal("2026.10.25.5707", PortFolder.DownloadVersion("/x/ksa_linux_v2026.10.25.5707.tar.gz"), "an archive's");
        Equal("", PortFolder.DownloadVersion("/x/other.tar.gz"), "another archive's");
    }
}
