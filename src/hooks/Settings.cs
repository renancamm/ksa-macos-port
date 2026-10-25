using System;
using System.Globalization;
using System.Linq;

namespace KsaMetalHooks;

// The patches' settings, from the environment: the launcher sets these from port-settings.toml (src/launcher/), and a
// variable set before starting the game wins over the file.
//   KSA_PATCHES_OFF=<key>,...      patches not to apply (their keys in Patches.Apply)
//   KSA_RENDER_SCALE=<0.25..1>     the 3D view's size, as a fraction of the window
//   KSA_UPSCALER=metalfx|spatial|none  how the view is scaled back up to the window
//   KSA_CLOUD_SHADOWS=normal|slow  how fast cloud shadows follow the clouds
//   KSA_PARK_MS=<ms>               how long an idle job worker spins before it sleeps (default 0.25)
//   KSA_PORTRAIT_FPS=<n>           crew portraits refreshed at most n times a second (0: every frame)
public static class Settings
{
    private static readonly string[] Off = (Environment.GetEnvironmentVariable("KSA_PATCHES_OFF") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Disabled(string key) => Off.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static readonly float RenderScale =
        float.TryParse(Environment.GetEnvironmentVariable("KSA_RENDER_SCALE"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float s) && s >= 0.25f && s < 1f ? s : 1f;

    public static readonly string Upscaler = (Environment.GetEnvironmentVariable("KSA_UPSCALER") ?? "metalfx").ToLowerInvariant();

    public static readonly bool SlowCloudShadows =
        string.Equals(Environment.GetEnvironmentVariable("KSA_CLOUD_SHADOWS"), "slow", StringComparison.OrdinalIgnoreCase);

    public static readonly int PortraitFps =
        int.TryParse(Environment.GetEnvironmentVariable("KSA_PORTRAIT_FPS"), out int fps) && fps > 0 ? fps : 0;
}
