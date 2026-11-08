// A run's folder: what a player opens and shares on top (report.html, to-share_<run>.zip), everything else in data/ (the
// run's store and record, its plan, logs, screenshots and the tools' files). A run's folder from before data/ has it all
// on top: Data finds either, so every reader takes both, and only a new run's folder is made with data/ (Create).
using System.IO;

namespace KsaPort.Common.Benchmark;

public static class RunFiles
{
    public const string DataFolder = "data", Page = "report.html";

    // Where a run's files are: its data/, or the folder itself when it has none (a run of before). Given data/, data/
    public static string Data(string dir) =>
        Directory.Exists(Path.Combine(dir, DataFolder)) ? Path.Combine(dir, DataFolder) : dir;

    // A new run's folder with its data/, which it returns
    public static string Create(string run)
    {
        var data = Path.Combine(run, DataFolder);
        Directory.CreateDirectory(data);
        return data;
    }

    // The run's folder, given it or its data/
    public static string Folder(string dir)
    {
        var full = Path.GetFullPath(dir).TrimEnd('/');
        return Path.GetFileName(full) == DataFolder ? Path.GetDirectoryName(full)! : full;
    }

    // The run's name: its folder's (2026-11-08_0842)
    public static string Name(string dir) => Path.GetFileName(Folder(dir));

    public static string PagePath(string dir) => Path.Combine(Folder(dir), Page);

    // The run to share: its report and data/, with the home folder's name as ~ and the screenshots as JPEG (Finish.Package)
    public static string Zip(string dir) => Path.Combine(Folder(dir), $"to-share_{Name(dir)}.zip");
}
