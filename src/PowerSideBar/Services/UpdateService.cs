using System;
using System.Reflection;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace PowerSideBar.Services;

public sealed class UpdateService
{
    public const string GitHubRepoUrl = "https://github.com/Maca00/PowerSideBar";

    private readonly UpdateManager _manager = new(
        new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion
    {
        get
        {
            if (_manager.CurrentVersion is { } installed)
                return installed.ToString();

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        if (!_manager.IsInstalled)
            return null;

        return await _manager.CheckForUpdatesAsync();
    }

    public async Task DownloadAndApplyAsync(UpdateInfo update, Action<int>? progress = null)
    {
        await _manager.DownloadUpdatesAsync(update, progress);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
