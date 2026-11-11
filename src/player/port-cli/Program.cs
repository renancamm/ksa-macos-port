using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using KsaPort.Common;

namespace KsaPort.Cli;

// The port's program: what the port folder's commands do (README.md in this folder). The commands start it on the
// folder's .NET runtime from lib/common.sh:
//
//   KsaPort.Cli port [options] [archive | folder]   port.sh
//   KsaPort.Cli convert-dotnet <game> <runtime>     the build: KSA's assemblies, for the hooks and the benchmark
//   KsaPort.Cli edit-shaders <game>                 the shader edits alone, on a game folder
//
// Exit codes: 0 done, 1 a problem or a wrong option (the message says which), 75 a newer port folder is in .update/
// (port --update): port.sh puts it in place and runs again.
internal static class Program
{
    public const int UpdateReady = 75;

    private static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["port", .. var rest]:
                    return Port.Run(rest);
                case ["convert-dotnet", var game, var runtime]:
                    Conversion.Dotnet(game, runtime);
                    return 0;
                case ["edit-shaders", var game]:
                    ShaderEdits.Apply(game, ShaderEdits.Read(PortFolder.ShaderEditsFolder), Console.WriteLine);
                    return 0;
                default:
                    Console.Error.WriteLine("usage: KsaPort.Cli port | convert-dotnet <game> <runtime> | " +
                        "edit-shaders <game>");
                    return 2;
            }
        }
        catch (Exception e) when (e is PortException or IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }
}

// port.sh: makes the app from KSA's Linux release, or updates the one there is
public static class Port
{
    private const string Usage = """
        Turns the Linux release of Kitten Space Agency into a Mac app, ~/Applications/Kitten Space Agency.app, or updates
        the one you have. Saves and settings stay.

        Usage: ./port.sh [options] [ksa_linux_vX.tar.gz | extracted folder]
          With an archive, makes the app from it, replacing the one you have. Without one, takes the newest
          ksa_linux_v*.tar.gz (or .tar) in ~/Downloads when it is newer than the app you have (or there is no app yet);
          otherwise it puts this folder's files into the app you have.

        Options:
          --update          first update this folder to the newest release of the port, from GitHub
          --app <path>      where the app is (default: ~/Applications/Kitten Space Agency.app)

        Steps:
          1. extract the archive next to the app
          2. convert            remove what macOS can't use, make the .NET files run on the app's runtime, edit the shaders
          3. app                make the app around the game, or put the port's files into the app you have
        """;

    public static int Run(string[] args)
    {
        string app = PortFolder.DefaultApp, source = "";
        bool update = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--update":
                    update = true;
                    break;
                case "--app" when i + 1 < args.Length:
                    app = args[++i].TrimEnd('/');
                    break;
                case "--app":
                    throw new PortException("--app needs the app's path.");
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                case var a when a.StartsWith('-'):
                    Console.WriteLine($"Unknown option: {a}");
                    Console.WriteLine(Usage);
                    return 1;
                default:
                    source = args[i].TrimEnd('/');
                    break;
            }
        }
        if (update && Update.Download())
            return Program.UpdateReady;

        var runtime = Path.Combine(PortFolder.Skeleton, "MacOS");
        var installed = PortFolder.AppVersion(app);
        if (source.Length == 0)
        {
            if (installed.Length == 0)
            {
                source = PortFolder.NewestDownload();
                if (source.Length == 0)
                    throw new PortException("no ksa_linux_v*.tar.gz (or .tar) in ~/Downloads. Download the Linux version of KSA " +
                        "from https://ksa.ahwoo.com, or pass the archive's path.");
                Output.Info($"Using {source}");
            }
            else
            {
                // A KSA in ~/Downloads newer than the app's
                source = PortFolder.NewestDownload(installed);
                if (source.Length > 0)
                    Output.Info($"Found KSA {PortFolder.DownloadVersion(source)} in ~/Downloads (the app has {installed}).");
            }
        }

        // No newer game: this folder's files into the app there is
        if (source.Length == 0)
        {
            Output.Step($"Updating the port's files in {app} (KSA {installed})");
            PortFolder.CheckKsaMatch(installed);
            var res = Path.Combine(app, "Contents", "Resources");
            Conversion.Game(res, runtime);
            App.Refresh(app);
            Console.WriteLine();
            Output.Step($"Done: {app} is up to date (KSA {installed}, port {PortFolder.VersionFile("version")}).");
            Output.Info("To update the game itself, download its new Linux version into ~/Downloads and run ./port.sh again.");
            return 0;
        }

        if (!File.Exists(source) && !Directory.Exists(source))
            throw new PortException($"{source} not found.");
        if (Directory.Exists(source) && !File.Exists(Path.Combine(source, "KSA.dll")))
            throw new PortException($"{source} has no KSA.dll.");

        // Extracted next to the app, on the same volume, so moving the game into the app is instant. Ctrl-C (or TERM)
        // takes the extracted game away too.
        string game, stage = "";
        var removing = new object();
        // Once, by whichever comes first: the end of the port or a signal (which also stops tar, maybe still writing)
        void RemoveStage()
        {
            lock (removing)
            {
                for (int i = 0; i < 10 && stage.Length > 0 && Directory.Exists(stage); i++)
                {
                    try
                    {
                        Directory.Delete(stage, true);
                    }
                    catch (IOException)
                    {
                        System.Threading.Thread.Sleep(200);
                    }
                }
            }
        }
        void Clean(PosixSignalContext signal)
        {
            signal.Cancel = true;
            RemoveStage();
            Environment.Exit(130);
        }
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Clean);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Clean);
        try
        {
            if (Directory.Exists(source))
            {
                Output.Step($"[1/3] Using the folder {source} (it is converted in place and moved into the app)");
                game = Path.GetFullPath(source);
            }
            else
            {
                Output.Step($"[1/3] Extracting {Path.GetFileName(source)}");
                var parent = Path.GetDirectoryName(Path.GetFullPath(app))!;
                Directory.CreateDirectory(parent);
                stage = Path.Combine(parent, $".ksa-port.{Guid.NewGuid():N}"[..17]);
                Directory.CreateDirectory(stage);
                Tool.Run("tar", "-xf", source, "-C", stage);
                var dll = Directory.EnumerateFiles(stage, "KSA.dll", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 1 })
                    .FirstOrDefault();
                if (dll == null)
                    throw new PortException($"no KSA.dll in {Path.GetFileName(source)}. Is this the Linux version of KSA?");
                game = Path.GetDirectoryName(dll)!;
            }
            var version = PortFolder.GameVersion(game);
            Output.Info($"KSA {version}");
            PortFolder.CheckKsaMatch(version);

            Output.Step("[2/3] Converting the game for macOS");
            Conversion.Game(game, runtime);

            Output.Step("[3/3] Making the app");
            App.Create(game, app);
        }
        finally
        {
            RemoveStage();
        }
        Console.WriteLine();
        Output.Step($"Done: {app} (KSA {PortFolder.AppVersion(app)})");
        Output.Info($"Open \"{PortFolder.AppName}\" from {Path.GetDirectoryName(app)}.");
        return 0;
    }
}
