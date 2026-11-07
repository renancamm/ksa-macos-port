using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace KsaPort.Common;

// Auto Memory Saving, the Presets section's second preset (auto_memory_saving), beside the graphics presets. On, it
// gives the rows with values in the table's memory column the values for this Mac's memory, in three tiers: with 8 GB
// or less it also limits the game's texture and shadow sizes and its texture streaming budget (the solar system
// doesn't fit an 8 GB Mac's GPU memory otherwise); with more than 16 GB the game keeps its own memory use, nothing
// gained, no part of the port's left in the way. Off gives the rows their defaults, the game's own, and the budget
// back. Its rows are the Memory section's, which the graphics presets never touch. Picked like a preset: in the tab,
// on a fresh install's first start (the hooks' loader, as it writes port-settings.toml), on the game's Reset, and in a
// benchmark run with --defaults. A row changed by hand afterwards keeps its value; with 8 GB or less every graphics
// preset's texture and shadow sizes take the limits.
public static class AutoMemorySaving
{
    public const string Key = "auto_memory_saving";

    // In the order of the table's memory column: 8 GB or less, 16 GB or less, more
    public enum Tier { Small, Medium, Large }

    // The rows Auto Memory Saving sets: those with values in the table's memory column
    public static IEnumerable<PortSetting> Rows => PortSettings.All.Where(s => s.Memory.Length > 0);

    // The game's settings with 8 GB or less: the field of GameSettings or GameSettings.Graphics, its key in
    // settings.toml, and the largest value it keeps (0, none or automatic, becomes it). The texture streamer's budget,
    // on automatic, is worked out once from the GPU memory free at its first frame (90 % of it, in powers of two,
    // 512 MB at least): an 8 GB Mac gets 512, and memory the rows free at load would only grow it (to 1024 MB in the
    // solar system with Fewer Effects at Once, more than it saves)
    public static readonly (string Field, string Key, int AtMost)[] SmallGame =
    [
        ("TerrainTextureCap", "celestialTextureCap", 2048),
        ("VesselTextureCap", "vesselTextureCap", 2048),
        ("TerrainShadowMapSize", "terrainShadowMapSize", 2048),
        ("CascadedShadowMapSize", "cascadedShadowMapSize", 2048),
        ("TextureStreamingBudget", "textureStreamingBudgetMb", 512),
    ];

    // Of those, the ones the game's tabs can't change (its Graphics tab shows the streaming budget, without a control):
    // Off gives them the game's own values again, as it does the rows
    public static readonly string[] OffRestores = ["TextureStreamingBudget"];

    // This Mac's memory in GB (hw.memsize, rounded); KSA_PORT_MEMORY_GB stands in for it, to try another tier
    public static int MemoryGb()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("KSA_PORT_MEMORY_GB"), out var gb) && gb > 0)
            return gb;
        long value = 0;
        nint size = sizeof(long);
        return SysctlByName("hw.memsize", ref value, ref size, 0, 0) == 0 ? (int)Math.Round(value / 1073741824.0) : 0;
    }

    // Unknown memory (0) counts as plenty: the game as it is. The launcher applies the same rule to its one setting
    // before the file exists (src/game/launcher/launcher.c)
    public static Tier TierOf(int gb) => gb is > 0 and <= 8 ? Tier.Small : gb is > 0 and <= 16 ? Tier.Medium : Tier.Large;

    public static Tier ThisMac() => TierOf(MemoryGb());

    public static string Describe(Tier tier) => tier switch
    {
        Tier.Small => "8 GB or less",
        Tier.Medium => "16 GB or less",
        _ => "more than 16 GB",
    };

    // The rows' values when on, for a tier
    public static Dictionary<string, string> Port(Tier tier) => Rows.ToDictionary(s => s.Key, s => s.Memory[(int)tier]);

    // The rows' values when off: their defaults, the game's own
    public static Dictionary<string, string> Off() => Rows.ToDictionary(s => s.Key, s => s.Default);

    // A size or cap with the small tier's limit
    public static int Limit(int value, int atMost) => value == 0 || value > atMost ? atMost : value;

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, ref long value, ref nint size, nint newValue, nint newSize);
}
