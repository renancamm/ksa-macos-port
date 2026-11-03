using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KsaPort.Common;

namespace KsaPort.Porter;

// port.sh --update: the newest release of the port, when it is newer than this folder, downloaded, checked against the
// checksum GitHub publishes and unpacked into the folder's .update/. port.sh then replaces the folder's contents with
// it (this program runs from the folder, so it doesn't do that itself) and starts the new port.sh.
public static class Update
{
    // Whether a newer port folder is ready in .update/
    public static bool Download()
    {
        var current = PortFolder.VersionFile("version");
        if (current.Length == 0 || current.StartsWith("dev"))
        {
            Output.Info($"This port folder was built locally (version {(current.Length > 0 ? current : "unknown")}); it is not updated online.");
            return false;
        }
        Output.Step("Checking for a newer version of the port");
        // No time limit, as curl had none: a slow connection takes its time
        using var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ksa-macos-port");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var api = Environment.GetEnvironmentVariable("KSA_PORT_RELEASE_API") is { Length: > 0 } a
            ? a : $"https://api.github.com/repos/{PortFolder.ReleasesRepo}/releases/latest";
        JsonElement release;
        try
        {
            release = JsonDocument.Parse(Retry(2, () => Get(http, api))).RootElement;
        }
        catch (Exception e) when (e is AggregateException or HttpRequestException or JsonException or InvalidOperationException
                                       or UriFormatException or IOException)
        {
            Output.Warn("could not reach GitHub, so the port itself was not checked for updates.");
            return false;
        }

        // Releases of the port are tagged v<KSA build>.<n>
        var tag = Text(release, "tag_name");
        if (!Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+$"))
        {
            Output.Info("No release of the port on GitHub to compare with.");
            return false;
        }
        var latest = tag[1..];
        if (!PortFolder.VersionNewer(latest, current))
        {
            Output.Info($"This is the newest version ({current}).");
            return false;
        }
        var asset = release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
            ? assets.EnumerateArray().FirstOrDefault(x => Regex.IsMatch(Text(x, "name"), @"^ksa-macos-port-.*\.zip$"))
            : default;
        if (asset.ValueKind != JsonValueKind.Object)
            throw new PortException($"release {tag} has no download of the port.");
        string url = Text(asset, "browser_download_url"), digest = Text(asset, "digest");
        if (url.Length == 0)
            throw new PortException($"release {tag} has no download of the port.");

        Output.Info($"Downloading {tag} (this folder has {current})");
        var tmp = Directory.CreateTempSubdirectory("ksa-port-").FullName;
        try
        {
            var zip = Path.Combine(tmp, "port.zip");
            try
            {
                File.WriteAllBytes(zip, Retry(3, () => GetBytes(http, url)));
            }
            catch (Exception e) when (e is AggregateException or HttpRequestException or InvalidOperationException or UriFormatException
                                           or IOException)
            {
                throw new PortException($"could not download {url}");
            }
            if (!digest.StartsWith("sha256:"))
                throw new PortException($"GitHub publishes no checksum for the download of {tag}, so it was not installed.");
            using (var f = File.OpenRead(zip))
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(f)), digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase))
                    throw new PortException("the download does not match the checksum GitHub publishes for it.");

            var staged = Path.Combine(PortFolder.Folder, ".update");
            App.Remove(staged);
            Tool.Run("ditto", "-x", "-k", zip, staged);
            var folder = Path.Combine(staged, "ksa-macos-port");
            string[] required = ["VERSION", "port.sh", "lib/common.sh", "lib/port.env"];
            if (!required.All(f => File.Exists(Path.Combine(folder, f))))
            {
                App.Remove(staged);
                throw new PortException($"the download of {tag} has an unexpected layout.");
            }
            return true;
        }
        finally
        {
            Directory.Delete(tmp, true);
        }
    }

    // An http(s) address, or a file:// one (for a test, as curl takes it)
    private static byte[] GetBytes(HttpClient http, string url) =>
        url.StartsWith("file://") ? File.ReadAllBytes(new Uri(url).LocalPath) : http.GetByteArrayAsync(url).Result;

    private static string Get(HttpClient http, string url) => System.Text.Encoding.UTF8.GetString(GetBytes(http, url));

    private static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // Tries again after a failure, <retries> times, as curl --retry does
    private static T Retry<T>(int retries, Func<T> get)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                return get();
            }
            catch (Exception) when (i < retries)
            {
                System.Threading.Thread.Sleep(1000 << i);
            }
        }
    }
}
