using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PowerSideBar.Services;

public sealed class AdBlockService
{
    private const string EasyListUrl = "https://easylist.to/easylist/easylist.txt";
    private static readonly string CacheFile = Path.Combine(ConfigService.AppDataFolder, "easylist.txt");
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HashSet<string> _blockedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _whitelistedDomains = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    /// <summary>
    /// Sites that actively detect ad blockers and break when blocked (e.g. YouTube).
    /// </summary>
    private static readonly string[] ExemptedSitesList = ["youtube.com", "youtu.be", "music.youtube.com"];
    private static readonly HashSet<string> ExemptedSites = new(ExemptedSitesList, StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled { get; set; } = true;
    public int BlockedDomainCount => _blockedDomains.Count;

    /// <summary>
    /// CSS script to inject into pages to hide common ad elements.
    /// Generated from ExemptedSitesList so the exemption list is defined once.
    /// </summary>
    public static readonly string AdHidingScript = GenerateAdHidingScript();

    private static string GenerateAdHidingScript()
    {
        var exempted = string.Join(",", ExemptedSitesList.Select(s => $"'{s}'"));
        return $$"""
        (function() {
            var exempted = [{{exempted}}];
            var host = window.location.hostname;
            for (var i = 0; i < exempted.length; i++) {
                if (host === exempted[i] || host.endsWith('.' + exempted[i])) return;
            }
            var style = document.createElement('style');
            style.textContent = `
                [id*="ad-"], [class*="ad-"], [id*="ads-"], [class*="ads-"],
                [id*="advert"], [class*="advert"],
                [id*="banner-ad"], [class*="banner-ad"],
                iframe[src*="ads"], iframe[src*="doubleclick"],
                iframe[src*="googlesyndication"],
                .adsbygoogle, #google_ads, .ad-container, .ad-wrapper,
                .ad-slot, .ad-unit, .ad-banner, .ad-placement,
                div[data-ad], div[data-ads], div[data-ad-slot],
                ins.adsbygoogle,
                [aria-label="advertisement"], [aria-label="Advertisements"]
                { display: none !important; visibility: hidden !important; height: 0 !important; overflow: hidden !important; }
            `;
            (document.head || document.documentElement).appendChild(style);
        })();
        """;
    }

    /// <summary>
    /// Loads the blocklist from cache or downloads it.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded) return;

        try
        {
            Directory.CreateDirectory(ConfigService.AppDataFolder);

            string? content = null;

            // Use cache if fresh enough
            if (File.Exists(CacheFile))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile);
                if (age < RefreshInterval)
                {
                    content = await File.ReadAllTextAsync(CacheFile, cancellationToken);
                }
            }

            // Download if no cache or stale
            if (content == null)
            {
                content = await DownloadListAsync(cancellationToken);
                if (content != null)
                {
                    await File.WriteAllTextAsync(CacheFile, content, cancellationToken);
                }
            }

            if (content != null)
            {
                ParseEasyList(content);
            }
        }
        catch
        {
            // Non-critical: ad blocking just won't work
        }

        // Fallback: add common ad domains if EasyList failed
        if (_blockedDomains.Count == 0)
        {
            AddFallbackDomains();
        }

        _isLoaded = true;
    }

    /// <summary>
    /// Returns true if ad blocking should be skipped for this page URL.
    /// </summary>
    public bool IsExempted(string pageUrl)
    {
        try
        {
            var host = new Uri(pageUrl).Host;
            return IsDomainInSet(host, ExemptedSites);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if the URL should be blocked.
    /// </summary>
    public bool ShouldBlock(string url)
    {
        if (!IsEnabled || string.IsNullOrEmpty(url))
            return false;

        try
        {
            var uri = new Uri(url);
            var host = uri.Host;

            // Check whitelist first (exact + parent domains)
            if (IsDomainInSet(host, _whitelistedDomains))
                return false;

            // Check blocklist (exact + parent domains)
            return IsDomainInSet(host, _blockedDomains);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDomainInSet(string host, HashSet<string> domainSet)
    {
        // Check exact match
        if (domainSet.Contains(host))
            return true;

        // Check parent domains (e.g. ads.example.com → example.com)
        var dotIndex = host.IndexOf('.');
        while (dotIndex >= 0 && dotIndex < host.Length - 1)
        {
            host = host[(dotIndex + 1)..];
            if (domainSet.Contains(host))
                return true;
            dotIndex = host.IndexOf('.');
        }

        return false;
    }

    private void ParseEasyList(string content)
    {
        using var reader = new StringReader(content);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();

            // Skip comments and empty lines
            if (line.Length == 0 || line[0] == '!' || line[0] == '[')
                continue;

            // Exception rules: @@||domain^
            if (line.StartsWith("@@||"))
            {
                var domain = ExtractDomain(line, 4);
                if (domain != null)
                    _whitelistedDomains.Add(domain);
                continue;
            }

            // Domain blocking rules: ||domain^
            if (line.StartsWith("||"))
            {
                var domain = ExtractDomain(line, 2);
                if (domain != null)
                    _blockedDomains.Add(domain);
            }
        }
    }

    private static string? ExtractDomain(string line, int startIndex)
    {
        // Find the end of the domain: ^ or $ or / or end of line
        var endIndex = line.Length;
        for (int i = startIndex; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '^' || c == '$' || c == '/' || c == '*')
            {
                endIndex = i;
                break;
            }
        }

        if (endIndex <= startIndex)
            return null;

        var domain = line[startIndex..endIndex];

        // Validate: must look like a domain (contains dot, no special chars)
        if (!domain.Contains('.') || domain.Contains(' ') || domain.Contains(':'))
            return null;

        return domain;
    }

    private static async Task<string?> DownloadListAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await HttpClient.GetStringAsync(EasyListUrl, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private void AddFallbackDomains()
    {
        string[] domains =
        [
            "doubleclick.net", "googlesyndication.com", "googleadservices.com",
            "google-analytics.com", "googletagmanager.com", "googletagservices.com",
            "adnxs.com", "adsrvr.org", "adform.net", "adroll.com",
            "advertising.com", "outbrain.com", "taboola.com", "criteo.com",
            "amazon-adsystem.com",
            "moatads.com", "rubiconproject.com", "pubmatic.com",
            "openx.net", "casalemedia.com", "scorecardresearch.com",
            "quantserve.com", "bluekai.com", "exelator.com",
            "turn.com", "mathtag.com", "serving-sys.com",
            "2mdn.net", "adsafeprotected.com", "ad.doubleclick.net",
        ];

        foreach (var d in domains)
            _blockedDomains.Add(d);
    }
}
