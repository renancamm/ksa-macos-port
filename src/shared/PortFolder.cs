using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KsaPort.Common;

// A problem that stops the command: printed as "Error: <message>", exit code 1
public sealed class PortException(string message) : Exception(message);

// The port's messages, as the port folder's commands print them
public static class Output
{
    public static void Step(string text) => Console.WriteLine($"==> {text}");
    public static void Info(string text) => Console.WriteLine($"    {text}");
    public static void Warn(string text) => Console.Error.WriteLine($"    WARNING: {text}");
}

// macOS's own tools, for what they do best: ditto keeps Microsoft's signatures and the links of a copy, tar and plutil
// read what they wrote
public static class Tool
{
    public static void Run(string tool, params string[] args)
    {
        if (Exec(tool, args, out var error) != 0)
            throw new PortException($"{tool} {string.Join(' ', args)} failed: {error.Trim()}");
    }

    // The tool's exit code, its standard output in output; its errors are kept apart
    public static int Exec(string tool, IEnumerable<string> args, out string output) => Exec(tool, args, out output, out _);

    public static int Exec(string tool, IEnumerable<string> args, out string output, out string error)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var stderr = p.StandardError.ReadToEndAsync();
        output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        error = stderr.Result;
        return p.ExitCode;
    }
}

// The port folder the running program is in (the porter, in its lib/porter/), its VERSION file, and what the commands
// share: KSA's versions, the archives in ~/Downloads, the apps this port made
public static class PortFolder
{
    // The port's names, from lib/common.sh, which the commands and the build (CMakeLists.txt) read them from too
    public static string AppName => Constant("APP_NAME");
    public static string BundleId => Constant("BUNDLE_ID");
    // Where releases are published (port.sh --update)
    public static string ReleasesRepo => Constant("RELEASES_REPO");
    // Oldest macOS the app supports
    public static string MacosMin => Constant("MACOS_MIN");

    private static Dictionary<string, string>? _constants;

    private static string Constant(string name)
    {
        if (_constants == null)
        {
            var common = Path.Combine(Lib, "common.sh");
            if (!File.Exists(common))
                throw new PortException($"{common} is missing. Download the port again.");
            _constants = File.ReadAllLines(common).Select(l => Regex.Match(l, @"^([A-Z_]+)=""([^""$]*)""$"))
                .Where(m => m.Success).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        }
        return _constants.TryGetValue(name, out var value) ? value : throw new PortException($"lib/common.sh has no {name}.");
    }

    public static string Home => Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DefaultApp => Path.Combine(Home, "Applications", AppName + ".app");

    // KSA_PORT_FOLDER: another folder, for the tests
    public static string Folder { get; set; } = Environment.GetEnvironmentVariable("KSA_PORT_FOLDER") is { Length: > 0 } f
        ? f : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
    public static string Lib => Path.Combine(Folder, "lib");
    public static string Payload => Path.Combine(Folder, "payload");
    // The app without the game
    public static string Skeleton => Path.Combine(Payload, "app", "Contents");
    public static string ShaderEditsFolder => Path.Combine(AppContext.BaseDirectory, "shader-edits");

    // A value of the folder's VERSION file (version, ksa, commit), empty when it has none
    public static string VersionFile(string key)
    {
        var file = Path.Combine(Folder, "VERSION");
        if (!File.Exists(file))
            return "";
        return File.ReadAllLines(file).Where(l => l.StartsWith(key + "=")).Select(l => l[(key.Length + 1)..]).FirstOrDefault() ?? "";
    }

    // The first match of a pattern in a file's bytes, empty when none
    public static string FirstMatch(string file, string pattern)
    {
        if (!File.Exists(file))
            return "";
        var m = Regex.Match(Encoding.Latin1.GetString(File.ReadAllBytes(file)), pattern);
        return m.Success ? m.Value : "";
    }

    // KSA's version from KSA.dll (for example 2026.10.19.5638)
    public static string GameVersion(string game) =>
        FirstMatch(Path.Combine(game, "KSA.dll"), @"20[0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2}\.[0-9]+");

    // Whether dotted version a is newer than b
    public static bool VersionNewer(string a, string b)
    {
        static long[] Parts(string v) => v.Split('.').Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
        long[] x = Parts(a), y = Parts(b);
        for (int i = 0; i < 4; i++)
        {
            long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q)
                return p > q;
        }
        return false;
    }

    // KSA's version from an archive's name (2026.10.19.5638 for ksa_linux_v2026.10.19.5638.tar.gz), empty for another
    public static string DownloadVersion(string archive)
    {
        var m = Regex.Match(Path.GetFileName(archive), @"^ksa_linux_v([0-9]+(\.[0-9]+)+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    // The KSA archive in ~/Downloads with the newest version, newer than <version> when given; empty when there is
    // none. Safari unpacks the .gz of a download, so a .tar counts too.
    public static string NewestDownload(string newerThan = "0")
    {
        var downloads = Path.Combine(Home, "Downloads");
        string[] archives;
        try
        {
            archives = Directory.GetFiles(downloads, "ksa_linux_v*.tar.gz").Order(StringComparer.Ordinal)
                .Concat(Directory.GetFiles(downloads, "ksa_linux_v*.tar").Order(StringComparer.Ordinal)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // None there, or Terminal may not look (macOS's privacy settings): none found
            return "";
        }
        string newest = "", best = newerThan;
        foreach (var f in archives)
        {
            var v = DownloadVersion(f);
            if (v.Length > 0 && VersionNewer(v, best))
                (newest, best) = (f, v);
        }
        return newest;
    }

    // The KSA version an app built by this port contains, empty for anything else
    public static string AppVersion(string app)
    {
        var plist = Path.Combine(app, "Contents", "Info.plist");
        return File.Exists(plist) && Plist(plist, "CFBundleIdentifier") == BundleId ? Plist(plist, "CFBundleShortVersionString") : "";
    }

    private static string Plist(string plist, string key) =>
        Tool.Exec("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", plist], out var value) == 0 ? value.Trim() : "";

    // Warns when <version> differs from the KSA this port folder was made for
    public static void CheckKsaMatch(string version)
    {
        var madeFor = VersionFile("ksa");
        if (madeFor.Length == 0 || version == madeFor)
            return;
        Output.Warn(VersionNewer(version, madeFor)
            ? $"this version of the port was made for KSA {madeFor}, not {version}. If something doesn't work, ./port.sh --update may find a newer one."
            : $"this version of the port was made for KSA {madeFor}, not {version}. Download the current Linux version of KSA from https://ksa.ahwoo.com.");
    }
}
