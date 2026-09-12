using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using Velopack.Locators;

namespace PowerSideBar.Services;

public sealed class AutoStartService
{
    private const string AppName = "PowerSideBar";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, false);
        return key?.GetValue(AppName) != null;
    }

    public void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, true);
        if (key == null) return;

        if (enable)
        {
            // Prefer the Velopack root stub so Run survives updates (current/ is replaced).
            var exePath = GetLaunchExecutablePath();
            key.SetValue(AppName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AppName, false);
        }
    }

    /// <summary>
    /// Stable launch path: Velopack root stub when installed, otherwise the current process path.
    /// </summary>
    public static string GetLaunchExecutablePath()
    {
        try
        {
            var root = VelopackLocator.Current?.RootAppDir;
            if (!string.IsNullOrEmpty(root))
            {
                var stub = Path.Combine(root, "PowerSideBar.exe");
                if (File.Exists(stub))
                    return stub;
            }
        }
        catch
        {
            // Not installed / locator unavailable.
        }

        return Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location;
    }
}
