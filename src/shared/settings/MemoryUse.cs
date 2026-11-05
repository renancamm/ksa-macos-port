using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace KsaPort.Common;

// Memory Use, the Presets section's row (memory_use): on Auto, the memory savers (the table's Memory section) and the
// game's texture and shadow sizes follow this Mac's memory, in three tiers. With 16 GB or less the savers are on; with
// 8 GB or less the game's textures and shadow maps are also at most 2048 pixels wide (the solar system doesn't fit an
// 8 GB Mac's GPU memory otherwise); with more than 16 GB the game keeps its own memory use: nothing gained, no part of
// the port's left in the way. Auto acts as a preset does: when the player picks it in the tab, with every preset card,
// on a fresh install's first start (the hooks' loader, as it writes port-settings.toml) and in a benchmark run with
// --defaults. A row changed by hand afterwards keeps its value.
public static class MemoryUse
{
    public const string Key = "memory_use";

    public enum Tier { Small, Medium, Large }

    // The memory savers, the table's Memory section
    public static readonly string[] Rows =
    [
        "texture_file_mapping", "sound_file_mapping", "release_freed_memory", "compact_ray_tracing_memory",
        "load_textures_once", "unload_far_terrain", "unload_far_textures", "load_terrain_by_altitude",
    ];

    // The game's settings with 8 GB or less: the field of GameSettings or GameSettings.Graphics, its key in
    // settings.toml, and the largest value it keeps (0, none, becomes it)
    public static readonly (string Field, string Key, int AtMost)[] SmallGame =
    [
        ("TerrainTextureCap", "celestialTextureCap", 2048),
        ("VesselTextureCap", "vesselTextureCap", 2048),
        ("TerrainShadowMapSize", "terrainShadowMapSize", 2048),
        ("CascadedShadowMapSize", "cascadedShadowMapSize", 2048),
    ];

    // This Mac's memory in GB (hw.memsize, rounded); KSA_PORT_MEMORY_GB stands in for it, to try another tier
    public static int MemoryGb()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("KSA_PORT_MEMORY_GB"), out var gb) && gb > 0)
            return gb;
        long value = 0;
        nint size = sizeof(long);
        return SysctlByName("hw.memsize", ref value, ref size, 0, 0) == 0 ? (int)Math.Round(value / 1073741824.0) : 0;
    }

    // Unknown memory (0) counts as plenty: the game as it is
    public static Tier TierOf(int gb) => gb is > 0 and <= 8 ? Tier.Small : gb is > 0 and <= 16 ? Tier.Medium : Tier.Large;

    public static Tier ThisMac() => TierOf(MemoryGb());

    public static string Describe(Tier tier) => tier switch
    {
        Tier.Small => "8 GB or less",
        Tier.Medium => "16 GB or less",
        _ => "more than 16 GB",
    };

    // The savers' values on Auto
    public static Dictionary<string, string> Port(Tier tier) => Rows.ToDictionary(key => key, _ => tier == Tier.Large ? "false" : "true");

    // The savers' values on Off: the game as it is
    public static Dictionary<string, string> Off() => Rows.ToDictionary(key => key, _ => "false");

    // A size or cap with the small tier's limit
    public static int Limit(int value, int atMost) => value == 0 || value > atMost ? atMost : value;

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, ref long value, ref nint size, nint newValue, nint newSize);
}
