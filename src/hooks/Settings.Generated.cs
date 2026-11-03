// The port settings the hooks read, as properties of Settings (Settings.cs): made from
// src/settings/port-settings.txt by KsaPortSettingsGenerator (src/settings/README.md), which build.sh runs.
// Edit the table, not this. A setting that applies when the game starts is read once, when the hooks load; a
// live one each time, since the Mac Port tab's Apply changes it.
using KsaPortSettings;

namespace KsaMetalHooks;

public static partial class Settings
{
    private static PortSetting _crewPortraitsAt15Fps = null!;
    private static PortSetting _skipHiddenCrewPortraits = null!;
    private static PortSetting _fitHudToDisplay = null!;

    public static string Upscaling { get; private set; } = "";

    public static double RenderScale { get; private set; }

    public static bool SkipGameAntiAliasing { get; private set; }

    public static bool FewerCloudShadowUpdates { get; private set; }

    public static bool LighterWarpCloudShadows { get; private set; }

    public static bool LighterPlumeShadows { get; private set; }

    public static bool SimplerPcssShadows { get; private set; }

    public static bool BakedMilkyWay { get; private set; }

    public static bool CrewPortraitsAt15Fps => On(_crewPortraitsAt15Fps);

    public static bool LighterTumblingPhysics { get; private set; }

    public static bool DebrisSettlesInWarp { get; private set; }

    public static bool TerrainCache { get; private set; }

    public static bool SkipUnusedOutlines { get; private set; }

    public static bool SkipUnusedOverlays { get; private set; }

    public static bool SkipHiddenVesselParts { get; private set; }

    public static bool SkipUnusedOceanBlending { get; private set; }

    public static bool ClearFadedSmoke { get; private set; }

    public static bool SkipHiddenCrewPortraits => On(_skipHiddenCrewPortraits);

    public static bool IdleThreadSleep { get; private set; }

    public static bool LoadingScreenFpsCap { get; private set; }

    public static bool FitHudToDisplay => On(_fitHudToDisplay);

    public static bool Hooks { get; private set; }

    public static bool HarmonyGenericDetours { get; private set; }

    public static bool FrameInterpolation { get; private set; }

    public static double[] TrailCaches { get; private set; } = [];

    public static double[] TrailShadows { get; private set; } = [];

    public static double WorkerParkMs { get; private set; }

    private static void Bind()
    {
        Upscaling = Text(Get("upscaling"));
        RenderScale = Number(Get("render_scale"));
        SkipGameAntiAliasing = On(Get("skip_game_anti_aliasing"));
        FewerCloudShadowUpdates = On(Get("fewer_cloud_shadow_updates"));
        LighterWarpCloudShadows = On(Get("lighter_warp_cloud_shadows"));
        LighterPlumeShadows = On(Get("lighter_plume_shadows"));
        SimplerPcssShadows = On(Get("simpler_pcss_shadows"));
        BakedMilkyWay = On(Get("baked_milky_way"));
        _crewPortraitsAt15Fps = Get("crew_portraits_at_15_fps");
        LighterTumblingPhysics = On(Get("lighter_tumbling_physics"));
        DebrisSettlesInWarp = On(Get("debris_settles_in_warp"));
        TerrainCache = On(Get("terrain_cache"));
        SkipUnusedOutlines = On(Get("skip_unused_outlines"));
        SkipUnusedOverlays = On(Get("skip_unused_overlays"));
        SkipHiddenVesselParts = On(Get("skip_hidden_vessel_parts"));
        SkipUnusedOceanBlending = On(Get("skip_unused_ocean_blending"));
        ClearFadedSmoke = On(Get("clear_faded_smoke"));
        _skipHiddenCrewPortraits = Get("skip_hidden_crew_portraits");
        IdleThreadSleep = On(Get("idle_thread_sleep"));
        LoadingScreenFpsCap = On(Get("loading_screen_fps_cap"));
        _fitHudToDisplay = Get("fit_hud_to_display");
        Hooks = On(Get("hooks"));
        HarmonyGenericDetours = On(Get("harmony_generic_detours"));
        FrameInterpolation = On(Get("frame_interpolation"));
        TrailCaches = Numbers(Get("trail_caches"));
        TrailShadows = Numbers(Get("trail_shadows"));
        WorkerParkMs = Number(Get("worker_park_ms"));
    }
}
