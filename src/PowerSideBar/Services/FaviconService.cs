using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PowerSideBar.Services;

public sealed class FaviconService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(5),
    };

    private readonly string _cacheFolder;

    public FaviconService()
    {
        _cacheFolder = ConfigService.IconsCacheFolder;
        Directory.CreateDirectory(_cacheFolder);
    }

    /// <summary>
    /// Downloads and caches a favicon for the given URL.
    /// Returns the local file path or empty string on failure.
    /// </summary>
    public async Task<string> GetFaviconAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri(ConfigService.NormalizeUrl(url));
            var domain = uri.Host;
            var cacheFile = Path.Combine(_cacheFolder, $"{domain}.ico");

            if (File.Exists(cacheFile))
                return cacheFile;

            // Try direct favicon.ico first
            var faviconUrl = $"https://{domain}/favicon.ico";
            var bytes = await TryDownloadAsync(faviconUrl, cancellationToken);

            // Fallback: Google Favicons API
            if (bytes == null || bytes.Length == 0)
            {
                faviconUrl = $"https://www.google.com/s2/favicons?domain={domain}&sz=32";
                bytes = await TryDownloadAsync(faviconUrl, cancellationToken);
            }

            if (bytes != null && bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(cacheFile, bytes, cancellationToken);
                return cacheFile;
            }
        }
        catch
        {
            // Silently fail — icon will show default
        }

        return string.Empty;
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }
}
