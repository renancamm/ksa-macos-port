using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KsaPort.Common;

namespace KsaPort.Cli;

// The edit for Baked Milky Way (features/baked-milky-way.edits), made by code, which the port's program
// compiles: it moves a whole block of MilkyWay.frag. The clouds' noise moves into a function, portMilkyWayClouds;
// main reads the clouds through portMilkyWayCloudsAt, which takes them from the strip the hooks bind in
// milkyWayNoise's place (it isn't square), or works them out as the game does. BakedMilkyWay.comp, the compute shader
// that fills the strip, is MilkyWay.frag without its inputs, outputs and main, the cubemap's binding replaced by the
// strip's: for each texel of a strip that spans the band, the longitude -PI..PI across and sampleDir.y
// -BAND_HEIGHT..BAND_HEIGHT down (texel centres), the clouds the fragment shader would see in that direction. Its
// patterns are Perl's, with ASCII classes as Perl matched them on the shader's bytes ([ \t\r\n\f\v] for \s).
public static class BakedMilkyWay
{
    private const RegexOptions Lines = RegexOptions.Multiline | RegexOptions.CultureInvariant;

    public static ShaderEdits.Status Apply(string shaders, Action<string> print)
    {
        string path = Path.Combine(shaders, "MilkyWay.frag"), bake = Path.Combine(shaders, "BakedMilkyWay.comp");
        void Status(string status, string file, string what) => print($"    [{status}] {file}: {what}");
        ShaderEdits.Status Warned(string what)
        {
            Status("WARN", "MilkyWay.frag", $"the code changed in this KSA version, skipped: {what}. Please open an issue.");
            File.Delete(bake);
            return ShaderEdits.Status.Warn;
        }
        if (!File.Exists(path))
        {
            Status("WARN", "MilkyWay.frag", "not found");
            return ShaderEdits.Status.Warn;
        }
        // An earlier version of the port wrote the bake's shader under another name
        File.Delete(Path.Combine(shaders, "MilkyWayBake.comp"));
        var text = ShaderEdits.ReadBytes(path);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        // The lookup, the part of the edit that is the port's own: a file edited by another version of the port says so
        var lookup = string.Join(nl,
            "float portMilkyWayCloudsAt(vec2 cloudUV, vec3 sampleDir)",
            "{",
            "    ivec2 size = textureSize(milkyWayNoise, 0);",
            "    if (size.x == size.y)",
            "        return portMilkyWayClouds(cloudUV, sampleDir);",
            "    vec2 strip = vec2(cloudUV.x / (2.0 * PI) + 0.5, cloudUV.y / (2.0 * BAND_HEIGHT) + 0.5);",
            "    strip.y = clamp(strip.y, 0.5 / float(size.y), 1.0 - 0.5 / float(size.y));",
            "    return textureLod(milkyWayNoise, strip, 0.0).r;",
            "}");
        bool edited = text.Contains("portMilkyWayCloudsAt", StringComparison.Ordinal);
        if (edited && !text.Contains(lookup, StringComparison.Ordinal))
        {
            Status("WARN", "MilkyWay.frag", "edited by another version of the port, skipped: the Milky Way's clouds in a " +
                "function. Port the game again from its archive.");
            File.Delete(bake);
            return ShaderEdits.Status.Warn;
        }
        // What the function's comment says, and what an earlier version of the port wrote there, with its shader's name
        var comment = string.Join(nl,
            "// macOS port: the clouds depend on the view direction alone. The hooks' Baked Milky Way fills a strip with them",
            "// once (BakedMilkyWay.comp, made from this file) and binds it in milkyWayNoise's place: it isn't square.");
        var earlier = string.Join(nl,
            "// macOS port: the clouds depend on the view direction alone. The hooks' Milky Way bake fills a strip with them",
            "// once (MilkyWayBake.comp, made from this file) and binds it in milkyWayNoise's place: it isn't square.");
        var result = ShaderEdits.Status.Ok;
        if (!edited)
        {
            const string function = "the Milky Way's clouds in a function";
            if (Regex.Matches(text, @"^void main\(\)", Lines).Count != 1)
                return Warned(function);
            var clouds = new Regex(
                @"(^    vec2 cloudUV = vec2\(fastAtan2\(-sampleDir\.x, sampleDir\.z\), sampleDir\.y\);\r?\n)" +
                @"(.*?^        cloudsFinal = mix\(0\.0, mix\(seamClouds, cloudsFinal, seamMask\), seamMask\);\r?\n    \}\r?\n)",
                Lines | RegexOptions.Singleline);
            var m = clouds.Match(text);
            if (!m.Success)
                return Warned(function);
            text = text[..m.Index] + m.Groups[1].Value + nl +
                "    float cloudsFinal = portMilkyWayCloudsAt(cloudUV, sampleDir);  // macOS port: the clouds' function" + nl +
                text[(m.Index + m.Length)..];
            var body = m.Groups[2].Value;
            // The rest of main must not need what moved into the function
            var rest = Regex.Match(text, @"^void main\(\)(.*)", Lines | RegexOptions.Singleline).Groups[1].Value;
            if (Regex.IsMatch(rest, @"(?<![A-Za-z0-9_])(?:cloudsLowA|cloudsLowB|cloudsHigh|seamClouds|seamThresh|seamMask|SEED_[A-Za-z0-9_]+)(?![A-Za-z0-9_])"))
                return Warned(function);
            var fn = string.Join(nl, comment, "float portMilkyWayClouds(vec2 cloudUV, vec3 sampleDir)", "{") + body + string.Join(nl, "    return cloudsFinal;", "}", "", lookup, "", "");
            var main = Regex.Match(text, @"^void main\(\)", Lines);
            text = text[..main.Index] + fn + text[main.Index..];
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
            Status("FIXED", "MilkyWay.frag", "put the clouds in a function Baked Milky Way shares");
            result = ShaderEdits.Status.Fixed;
        }
        else if (text.Contains(earlier, StringComparison.Ordinal))
        {
            // Ported by an earlier version: its comment
            text = text.Replace(earlier, comment, StringComparison.Ordinal);
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text));
            Status("FIXED", "MilkyWay.frag", "updated: put the clouds in a function Baked Milky Way shares");
            result = ShaderEdits.Status.Fixed;
        }
        else
        {
            Status("OK", "MilkyWay.frag", "already puts the clouds in a function Baked Milky Way shares");
        }

        // The bake: this file without its inputs, outputs and main, the cubemap's binding replaced by the strip's
        var comp = text;
        var io = new Regex(@"^layout[ \t\r\n\f\v]*\(location[ \t\r\n\f\v]*=[ \t\r\n\f\v]*0\)[ \t\r\n\f\v]*(?:in|out)[ \t\r\n\f\v][^\n]*\n", Lines);
        if (io.Matches(comp).Count != 2)
            return Warned("the bake's inputs and outputs");
        comp = io.Replace(comp, "");
        var cube = new Regex(@"^layout[ \t\r\n\f\v]*\(set[ \t\r\n\f\v]*=[ \t\r\n\f\v]*1,[ \t\r\n\f\v]*binding[ \t\r\n\f\v]*=[ \t\r\n\f\v]*0\)[ \t\r\n\f\v]*uniform[ \t\r\n\f\v]+samplerCube[ \t\r\n\f\v]+milkyWay;", Lines);
        if (!cube.IsMatch(comp))
            return Warned("the bake's bindings");
        comp = cube.Replace(comp, "layout (set = 1, binding = 0, r16f) uniform writeonly image2D portMilkyWayStrip;" + nl +
            "layout (local_size_x = 8, local_size_y = 8) in;", 1);
        if (!Regex.IsMatch(comp, @"^layout[ \t\r\n\f\v]*\(set[ \t\r\n\f\v]*=[ \t\r\n\f\v]*1,[ \t\r\n\f\v]*binding[ \t\r\n\f\v]*=[ \t\r\n\f\v]*1\)[ \t\r\n\f\v]*uniform[ \t\r\n\f\v]+sampler2D[ \t\r\n\f\v]+milkyWayNoise;", Lines))
            return Warned("the bake's bindings");
        var mainAt = Regex.Match(comp, @"^void main\(\)", Lines);
        if (mainAt.Success)
            comp = comp[..mainAt.Index];
        // The file's first line only, as in the game
        var version = Regex.Match(comp, @"^(#version[^\n]*\n)", RegexOptions.CultureInvariant);
        if (!version.Success)
            return Warned("the bake's version line");
        int after = version.Index + version.Length;
        comp = comp[..after] + "// macOS port: made from MilkyWay.frag by the port, for the hooks' Baked Milky Way" + nl + comp[after..];
        comp += string.Join(nl,
            "void main()",
            "{",
            "    ivec2 size = imageSize(portMilkyWayStrip);",
            "    ivec2 texel = ivec2(gl_GlobalInvocationID.xy);",
            "    if (any(greaterThanEqual(texel, size)))",
            "        return;",
            "    vec2 cloudUV = vec2(((float(texel.x) + 0.5) / float(size.x) - 0.5) * 2.0 * PI,",
            "                        ((float(texel.y) + 0.5) / float(size.y) - 0.5) * 2.0 * BAND_HEIGHT);",
            "    float across = sqrt(1.0 - cloudUV.y * cloudUV.y);",
            "    vec3 sampleDir = vec3(-sin(cloudUV.x) * across, cloudUV.y, cos(cloudUV.x) * across);",
            "    imageStore(portMilkyWayStrip, texel, vec4(portMilkyWayClouds(cloudUV, sampleDir)));",
            "}", "");
        if (File.Exists(bake) && ShaderEdits.ReadBytes(bake) == comp)
        {
            Status("OK", "BakedMilkyWay.comp", "already made Baked Milky Way's shader");
            return result;
        }
        File.WriteAllBytes(bake, Encoding.Latin1.GetBytes(comp));
        Status("FIXED", "BakedMilkyWay.comp", "made Baked Milky Way's shader");
        return result == ShaderEdits.Status.Ok ? ShaderEdits.Status.Fixed : result;
    }
}
