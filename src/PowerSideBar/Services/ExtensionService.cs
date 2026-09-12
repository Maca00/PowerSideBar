using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace PowerSideBar.Services;

public sealed class ExtensionService
{
    private const string UBlockLiteFolder = "Assets/Extensions/ublock-lite";
    private const string UBlockLiteName = "uBlock Origin Lite";

    public bool IsUBlockInstalled { get; private set; }

    /// <summary>
    /// Installs uBlock Origin Lite if not already present.
    /// Must be called after a CoreWebView2 is initialized (needs Profile access).
    /// </summary>
    public async Task InstallIfNeededAsync(CoreWebView2 coreWebView)
    {
        try
        {
            var profile = coreWebView.Profile;

            // Check if already installed
            var extensions = await profile.GetBrowserExtensionsAsync();
            if (extensions.Any(ext => ext.Name == UBlockLiteName && ext.IsEnabled))
            {
                IsUBlockInstalled = true;
                return;
            }

            // Resolve extension folder path relative to the exe
            var exeDir = AppContext.BaseDirectory;
            var extensionPath = Path.GetFullPath(Path.Combine(exeDir, UBlockLiteFolder));

            if (!File.Exists(Path.Combine(extensionPath, "manifest.json")))
                return;

            await profile.AddBrowserExtensionAsync(extensionPath);
            IsUBlockInstalled = true;
        }
        catch
        {
            // Non-critical: ad blocking falls back to AdBlockService
            IsUBlockInstalled = false;
        }
    }
}
