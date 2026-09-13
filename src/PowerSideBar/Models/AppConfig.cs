using System;
using System.Collections.Generic;

namespace PowerSideBar.Models;

public class AppConfig
{
    public List<ShortcutItem> Shortcuts { get; set; } = new();
    public List<ShortcutGroup> Groups { get; set; } = new();
    public int SidebarWidth { get; set; } = 550;
    public int IconStripWidth { get; set; } = 50;
    public bool AdBlockEnabled { get; set; } = true;

    /// <summary>"left" or "right" — which screen edge the sidebar docks to.</summary>
    public string SidebarSide { get; set; } = "right";

    /// <summary>"en", "fr" or "es" — UI language (default English).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Update version the user dismissed (silent prompt snooze).</summary>
    public string UpdateSnoozedVersion { get; set; } = string.Empty;

    /// <summary>Do not auto-prompt for this snoozed version until this UTC time.</summary>
    public DateTime UpdateSnoozedUntilUtc { get; set; } = DateTime.MinValue;

    /// <summary>Show Météo built-in in the sidebar.</summary>
    public bool EnableWeather { get; set; } = true;
    /// <summary>Show Hue built-in in the sidebar.</summary>
    public bool EnableHue { get; set; } = false;
    /// <summary>Show Volets built-in in the sidebar.</summary>
    public bool EnableShutters { get; set; } = false;
    /// <summary>Show Spotify button / mini player.</summary>
    public bool EnableSpotify { get; set; } = false;

    /// <summary>
    /// True once feature flags have been initialized (avoids treating old configs as all-enabled).
    /// </summary>
    public bool FeaturesInitialized { get; set; }

    // Philips Hue settings
    public string HueBridgeIp { get; set; } = string.Empty;
    public string HueApiKey { get; set; } = string.Empty;

    // Weather settings
    public double WeatherLatitude { get; set; } = 48.86;
    public double WeatherLongitude { get; set; } = 2.35;
    public string WeatherTimezone { get; set; } = "Europe/Paris";
    public string WeatherCityName { get; set; } = "Paris";

    // Shutter (Dooya SHC) settings — filled via Paramètres / config.json only
    public string ShutterHost { get; set; } = string.Empty;
    public int ShutterPort { get; set; }
    public string ShutterUser { get; set; } = string.Empty;
    public string ShutterPassword { get; set; } = string.Empty;
    public string ShutterHostId { get; set; } = string.Empty;

    // Spotify settings
    public string SpotifyClientId { get; set; } = string.Empty;
    public string SpotifyClientSecret { get; set; } = string.Empty;
    public string SpotifyAccessToken { get; set; } = string.Empty;
    public string SpotifyRefreshToken { get; set; } = string.Empty;
    public DateTime SpotifyTokenExpiryUtc { get; set; } = DateTime.MinValue;
}
