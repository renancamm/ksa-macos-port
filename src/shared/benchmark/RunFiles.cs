// A run's folder: what a person opens and shares on top (report.html, compare.html after a comparison,
// to-share_<run>.zip), everything else in data/ (the run's store and record, its plan, logs, screenshots and the tools'
// files).
using System.IO;

namespace KsaPort.Common.Benchmark;

public static class RunFiles
{
    public const string DataFolder = "data", Page = "report.html", ComparePage = "compare.html";

    // Where a run's files are: its data/, given the run's folder or data/ itself
    public static string Data(string dir) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)) == DataFolder ? dir : Path.Combine(dir, DataFolder);

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
