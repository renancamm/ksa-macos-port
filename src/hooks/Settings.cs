using System;
using System.Globalization;

namespace KsaMetalHooks;

// The patches' settings, from the environment: the loader sets these from port-settings.toml (its table,
// src/launcher/port-settings.txt, says which setting is which variable), and a variable set before starting the game
// wins over the file. Unset, each means the table's default.
//   KSA_UPSCALER=off|metalfx|spatial    how the 3D view is scaled up to the display's pixels (off: the game's stretch,
//                                       filtered, and no render scale)
//   KSA_RENDER_SCALE=<0.25..2>          the 3D view's size, as a fraction of the window's size in points
//   KSA_CLOUD_SHADOWS=normal|slow       how fast cloud shadows follow the clouds
//   KSA_PLUME_SHADOWS=normal|light     the size of the smoke's sun shadow cascades
//   KSA_CREW_PORTRAITS=always|shown|shown_15  when the crew panel's faces are drawn (the Mac Port tab changes it live)
//   KSA_TERRAIN_CACHE=0, KSA_OUTLINE_SKIP=0, KSA_WORKER_PARKING=0  leave out that patch
//   KSA_PARK_MS=<ms>                    how long an idle job worker spins before it sleeps (default 0.25)
public static class Settings
{
    public static readonly string Upscaler = (Environment.GetEnvironmentVariable("KSA_UPSCALER") ?? "off").ToLowerInvariant();

    public static readonly float RenderScale =
        Upscaler is "metalfx" or "spatial" &&
        float.TryParse(Environment.GetEnvironmentVariable("KSA_RENDER_SCALE"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out float s) && s >= 0.25f && s <= 2f ? s : 1f;

    public static readonly bool SlowCloudShadows =
        string.Equals(Environment.GetEnvironmentVariable("KSA_CLOUD_SHADOWS"), "slow", StringComparison.OrdinalIgnoreCase);

    public static readonly bool LightPlumeShadows =
        string.Equals(Environment.GetEnvironmentVariable("KSA_PLUME_SHADOWS"), "light", StringComparison.OrdinalIgnoreCase);

    public static string CrewPortraits = Environment.GetEnvironmentVariable("KSA_CREW_PORTRAITS") is { } p && p is "always" or "shown_15"
        ? p : "shown";

    // A patch's on/off variable: anything but 0 is on
    public static bool On(string variable) => Environment.GetEnvironmentVariable(variable) is not "0";
}
