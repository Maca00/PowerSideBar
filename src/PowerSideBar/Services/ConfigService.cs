using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using PowerSideBar.Models;

namespace PowerSideBar.Services;

public static class ConfigService
{
    public static readonly string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerSideBar");

    private static readonly string ConfigFilePath =
        Path.Combine(AppDataFolder, "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Built-in weather shortcut icon (pack resource).</summary>
    public const string WeatherIconPath = "pack://application:,,,/Assets/Weather/Weather Icon-6.png";

    /// <summary>Built-in Philips Hue shortcut icon (pack resource).</summary>
    public const string HueIconPath = "pack://application:,,,/Assets/Icons/hue.png";

    /// <summary>Built-in shutters shortcut icon (pack resource).</summary>
    public const string ShutterIconPath = "pack://application:,,,/Assets/Icons/shutter.png";

    public static string IconsCacheFolder => Path.Combine(AppDataFolder, "icons");
    public static string WebView2DataFolder => Path.Combine(AppDataFolder, "WebView2Data");

    public static AppConfig Load()
    {
        EnsureDirectories();

        if (!File.Exists(ConfigFilePath))
        {
            var defaultConfig = CreateDefaultConfig();
            Save(defaultConfig);
            return defaultConfig;
        }

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? CreateDefaultConfig();
            MigrateIfNeeded(config);
            return config;
        }
        catch
        {
            return CreateDefaultConfig();
        }
    }

    private static readonly object _saveLock = new();

    public static void Save(AppConfig config)
    {
        lock (_saveLock)
        {
            EnsureDirectories();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var tmpPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, ConfigFilePath, overwrite: true);
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        var config = new AppConfig
        {
            EnableWeather = true,
            EnableHue = false,
            EnableShutters = false,
            EnableSpotify = false,
            FeaturesInitialized = true,
            SidebarWidth = 550,
            IconStripWidth = 50,
        };
        SyncBuiltInShortcuts(config);
        return config;
    }

    /// <summary>
    /// Ensures a URL has a protocol prefix (defaults to https://).
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        return "https://" + url;
    }

    /// <summary>
    /// Adds or removes built-in shortcuts according to EnableWeather / EnableHue / EnableShutters.
    /// </summary>
    public static void SyncBuiltInShortcuts(AppConfig config)
    {
        SyncOne(config, config.EnableWeather, "weather", Loc.T("builtin.weather"), WeatherIconPath);
        SyncOne(config, config.EnableHue, "hue", Loc.T("builtin.hue"), HueIconPath);
        SyncOne(config, config.EnableShutters, "shutter", Loc.T("builtin.shutters"), ShutterIconPath);

        for (var i = 0; i < config.Shortcuts.Count; i++)
            config.Shortcuts[i].Order = i;
    }

    private static void SyncOne(AppConfig config, bool enabled, string type, string name, string iconPath)
    {
        if (enabled)
        {
            if (!config.Shortcuts.Any(s => s.BuiltInType == type))
            {
                config.Shortcuts.Add(new ShortcutItem
                {
                    Name = name,
                    BuiltInType = type,
                    IconPath = iconPath,
                    Order = config.Shortcuts.Count,
                });
            }

            foreach (var item in config.Shortcuts.Where(s => s.BuiltInType == type))
            {
                item.IconPath = iconPath;
                item.Name = name;
            }
        }
        else
        {
            config.Shortcuts.RemoveAll(s => s.BuiltInType == type);
        }
    }

    /// <summary>
    /// Ensures built-in shortcuts match feature flags; removes obsolete Spotify list entry.
    /// </summary>
    private static void MigrateIfNeeded(AppConfig config)
    {
        if (!config.FeaturesInitialized)
        {
            // Old configs: derive flags from whatever built-ins are already present
            config.EnableWeather = config.Shortcuts.Any(s => s.BuiltInType == "weather");
            config.EnableHue = config.Shortcuts.Any(s => s.BuiltInType == "hue");
            config.EnableShutters = config.Shortcuts.Any(s => s.BuiltInType == "shutter");
            config.EnableSpotify = false;
            if (!config.EnableWeather && !config.EnableHue && !config.EnableShutters)
            {
                config.EnableWeather = true;
            }
            config.FeaturesInitialized = true;
        }

        SyncBuiltInShortcuts(config);

        var removedSpotify = config.Shortcuts.RemoveAll(s => s.BuiltInType == "spotify");
        if (removedSpotify > 0)
        {
            for (var i = 0; i < config.Shortcuts.Count; i++)
                config.Shortcuts[i].Order = i;
        }

        Save(config);
    }

    private static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataFolder);
        Directory.CreateDirectory(IconsCacheFolder);
        Directory.CreateDirectory(WebView2DataFolder);
    }
}
