using System;
using System.IO;
using System.Linq;

namespace KsaPorter;

// "Kitten Space Agency.app": built around a converted game folder, or the port's files put into one again. The port's
// part of the app is the port folder's payload/app/Contents/, the app without the game:
//   Contents/MacOS/KSA            the launcher
//   Contents/MacOS/dotnet, ...    Microsoft's .NET runtime
//   Contents/Resources/*.dylib    the native libraries, which the game finds next to it
//   Contents/Resources/Brutal.Monitor.Subprocess   BRUTAL's crash-dump helper, which the game starts
//   Contents/Resources/Hooks/     KSA Metal Hooks, which the launcher loads, and Harmony; their loader applies the
//                                 port settings
//   Contents/Resources/Benchmark/ the benchmark: its director, which the hooks start, the report program, the tour
//   Contents/Resources/port-settings.txt   the table of the port settings, which the hooks read
//   Contents/Resources/port-settings.toml  the port settings' template, which the hooks copy for the player
//   Contents/Resources/VERSION    the port's version, which the Mac Port tab's title shows
//   Contents/Resources/AppIcon.icns, Licenses/
// The bundle is not signed as a whole: that would replace Microsoft's signature on dotnet. The quarantine attribute a
// browser download puts on the files is removed, so macOS opens the app as one made on this Mac.
public static class App
{
    // Moves the converted game folder into a new app as Contents/Resources. An app this port made earlier at that path
    // is replaced, keeping the game's window layout (imgui.ini) and StarMap, and goes to the Trash.
    public static void Create(string game, string app)
    {
        Conversion.RequireGame(game);
        string old = "", oldVersion = "", target = app;
        if (Directory.Exists(app) || File.Exists(app))
        {
            oldVersion = PortFolder.AppVersion(app);
            if (oldVersion.Length == 0)
                throw new PortException($"{app} exists and was not made by this port. Move it away or choose another path.");
            old = app;
            // Built next to the old app and swapped at the end, so a failure leaves the old one untouched
            target = Path.Combine(Path.GetDirectoryName(app)!, $".{Path.GetFileName(app)}.new");
            if (Directory.Exists(target) || File.Exists(target))
                throw new PortException($"{target} is left over from a failed update. Delete it and run again.");
        }
        Directory.CreateDirectory(Path.Combine(target, "Contents"));
        // mv: the folder may be on another volume
        Tool.Run("mv", game, Path.Combine(target, "Contents", "Resources"));
        InstallPortFiles(Path.Combine(target, "Contents"));
        if (old.Length > 0)
        {
            var ini = Path.Combine(old, "Contents", "Resources", "imgui.ini");
            if (File.Exists(ini))
                Tool.Run("cp", ini, Path.Combine(target, "Contents", "Resources") + "/");
            if (Directory.Exists(Path.Combine(old, "Contents", "Resources", "StarMap")))
            {
                InstallStarMap(Path.Combine(target, "Contents", "Resources", "StarMap"));
                Output.Info("StarMap is in the new app too.");
            }
            var trash = Path.Combine(PortFolder.Home, ".Trash");
            Directory.CreateDirectory(trash);
            Tool.Run("mv", old, Path.Combine(trash, $"{PortFolder.AppName} {oldVersion} {DateTime.Now:yyyyMMdd-HHmmss}.app"));
            Tool.Run("mv", target, app);
            Output.Info($"The previous app ({oldVersion}) is in the Trash.");
        }
        Touch(app);
    }

    // Puts this folder's launcher, .NET runtime, libraries, icon, licences and Info.plist into an existing app
    public static void Refresh(string app)
    {
        if (PortFolder.AppVersion(app).Length == 0)
            throw new PortException($"{app} is not an app made by this port.");
        InstallPortFiles(Path.Combine(app, "Contents"));
        var starMap = Path.Combine(app, "Contents", "Resources", "StarMap");
        if (Directory.Exists(starMap))
            InstallStarMap(starMap);
        Touch(app);
    }

    private static void InstallPortFiles(string contents)
    {
        string res = Path.Combine(contents, "Resources"), macos = Path.Combine(contents, "MacOS");
        string skeleton = PortFolder.Skeleton, from = Path.Combine(skeleton, "Resources");

        // Native libraries: every .dylib next to the game is the port's (the game ships none for macOS). And BRUTAL's
        // crash-dump helper, which the game starts from beside it. The libraries' links stay links.
        foreach (var dylib in Directory.GetFiles(res, "*.dylib"))
            File.Delete(dylib);
        var natives = Directory.GetFileSystemEntries(from, "*.dylib").Order(StringComparer.Ordinal)
            .Append(Path.Combine(from, "Brutal.Monitor.Subprocess")).ToList();
        Tool.Run("cp", ["-P", .. natives, res + "/"]);

        // .NET runtime and launcher (ditto keeps Microsoft's signatures)
        Remove(macos);
        Tool.Run("ditto", Path.Combine(skeleton, "MacOS"), macos);

        foreach (var folder in new[] { "Licenses", "Hooks", "Benchmark" })
        {
            Remove(Path.Combine(res, folder));
            Tool.Run("ditto", Path.Combine(from, folder), Path.Combine(res, folder));
        }
        Tool.Run("cp", [.. new[] { "AppIcon.icns", "port-settings.txt", "port-settings.toml", "VERSION" }
            .Select(f => Path.Combine(from, f)), res + "/"]);
        WriteInfoPlist(contents);
        Tool.Exec("xattr", ["-cr", contents], out _);
    }

    private static void WriteInfoPlist(string contents)
    {
        var version = PortFolder.GameVersion(Path.Combine(contents, "Resources"));
        if (version.Length == 0)
            version = "1.0";
        // NSHighResolutionCapable: the swapchain has the display's pixels, where the game's UI is drawn; the hooks
        // render the 3D view at the render scale times the window's size in points and scale it up
        var plist = Path.Combine(contents, "Info.plist");
        File.WriteAllText(plist, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleExecutable</key><string>KSA</string>
                <key>CFBundleName</key><string>{PortFolder.AppName}</string>
                <key>CFBundleDisplayName</key><string>{PortFolder.AppName}</string>
                <key>CFBundleIdentifier</key><string>{PortFolder.BundleId}</string>
                <key>CFBundlePackageType</key><string>APPL</string>
                <key>CFBundleShortVersionString</key><string>{version}</string>
                <key>CFBundleVersion</key><string>{version}</string>
                <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
                <key>CFBundleIconFile</key><string>AppIcon</string>
                <key>LSMinimumSystemVersion</key><string>{PortFolder.MacosMin}</string>
                <key>LSRequiresNativeExecution</key><true/>
                <key>LSApplicationCategoryType</key><string>public.app-category.simulation-games</string>
                <key>NSHighResolutionCapable</key><true/>
                <key>KSAPortVersion</key><string>{PortFolder.VersionFile("version")}</string>
                <key>KSAPortCommit</key><string>{PortFolder.VersionFile("commit")}</string>
            </dict>
            </plist>

            """);
        Tool.Run("/usr/bin/plutil", "-lint", plist);
    }

    // A copy of the port folder's StarMap there (the launcher starts the game through the app's
    // Contents/Resources/StarMap, or through the folder KSA_STARMAP_DIR names)
    public static void InstallStarMap(string target)
    {
        Remove(target);
        Tool.Run("ditto", Path.Combine(PortFolder.Payload, "starmap"), target);
        Tool.Exec("xattr", ["-cr", target], out _);
    }

    public static void Remove(string path)
    {
        if (new FileInfo(path).LinkTarget != null || File.Exists(path))
            File.Delete(path);
        else if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    // Finder and Launch Services see the app as changed
    public static void Touch(string path) => Directory.SetLastWriteTime(path, DateTime.Now);
}
