using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSideBar.Models;
using PowerSideBar.Services;

namespace PowerSideBar.ViewModels;

public partial class SpotifyViewModel : ObservableObject, IDisposable
{
    private const string RedirectUri = "http://127.0.0.1:8888/callback";
    private const string Scopes = "user-read-currently-playing user-read-playback-state user-modify-playback-state user-library-read user-library-modify";
    private const int RefreshIntervalMs = 1000;

    private readonly SpotifyService _service;
    private readonly AppConfig _config;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _authCts;
    private CancellationTokenSource? _volumeDebounceCts;

    /// <summary>Next OAuth request uses show_dialog=true (e.g. library scope missing).</summary>
    private bool _authorizeForceSpotifyConsentDialog;

    private string? _cachedArtTrackId;
    private string? _cachedArtUrl;

    // Prevents the Refresh loop from firing volume API calls when it syncs the UI
    private bool _isSyncingVolume;
    // When true, the local volume has been changed by the user recently;
    // ignore incoming API values until we re-sync
    private DateTime _lastUserVolumeChangeUtc = DateTime.MinValue;

    [ObservableProperty]
    private string? _currentTrack;

    [ObservableProperty]
    private string? _currentArtist;

    [ObservableProperty]
    private string? _albumArtUrl;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int _progressMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private int _durationMs;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private int _volume = 50;

    [ObservableProperty]
    private bool _canControlVolume;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveDeviceLabel))]
    private string? _activeDeviceName;

    public string? ActiveDeviceLabel =>
        string.IsNullOrEmpty(ActiveDeviceName)
            ? null
            : Loc.Tf("spotify.on_device", ActiveDeviceName);

    [ObservableProperty]
    private string? _currentTrackId;

    [ObservableProperty]
    private string _currentTrackType = "track";

    [ObservableProperty]
    private bool _isCurrentTrackLiked;

    public string ProgressText => FormatMs(ProgressMs);
    public string DurationText => FormatMs(DurationMs);

    partial void OnVolumeChanged(int value)
    {
        if (_isSyncingVolume) return;
        _lastUserVolumeChangeUtc = DateTime.UtcNow;
        DebouncedSendVolume(value);
    }

    private void DebouncedSendVolume(int value)
    {
        _volumeDebounceCts?.Cancel();
        _volumeDebounceCts?.Dispose();
        _volumeDebounceCts = new CancellationTokenSource();
        var token = _volumeDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, token);
                if (token.IsCancellationRequested) return;

                if (!IsAuthenticated) return;

                var result = await _service.SetVolumeAsync(value, token);
                if (result == SpotifyPlaybackResult.NoActiveDevice)
                {
                    StatusMessage = Loc.T("spotify.no_device_volume");
                }
                else if (result == SpotifyPlaybackResult.NotPremium)
                {
                    StatusMessage = Loc.T("spotify.premium_required");
                }
                else if (result == SpotifyPlaybackResult.Unauthorized)
                {
                    IsAuthenticated = false;
                    StatusMessage = Loc.T("spotify.session_expired");
                }
            }
            catch (OperationCanceledException) { }
            catch { }
        }, token);
    }

    public SpotifyViewModel(SpotifyService service, AppConfig config)
    {
        _service = service;
        _config = config;

        IsConfigured =
            !string.IsNullOrEmpty(config.SpotifyClientId) &&
            !string.IsNullOrEmpty(config.SpotifyClientSecret);

        IsAuthenticated =
            !string.IsNullOrEmpty(config.SpotifyAccessToken) &&
            !string.IsNullOrEmpty(config.SpotifyRefreshToken);

        if (IsAuthenticated)
        {
            var expiry = config.SpotifyTokenExpiryUtc == DateTime.MinValue
                ? DateTime.UtcNow // will trigger refresh on first call
                : config.SpotifyTokenExpiryUtc;
            _service.SetAccessToken(config.SpotifyAccessToken, expiry);
            StatusMessage = Loc.T("spotify.connected");
        }
        else if (!IsConfigured)
        {
            StatusMessage = Loc.T("spotify.not_configured");
        }
        else
        {
            StatusMessage = Loc.T("spotify.not_connected");
        }
    }

    /// <summary>Re-reads Client ID/Secret from config after settings save.</summary>
    public void ApplyConfiguration()
    {
        IsConfigured =
            !string.IsNullOrEmpty(_config.SpotifyClientId) &&
            !string.IsNullOrEmpty(_config.SpotifyClientSecret);

        if (IsAuthenticated)
        {
            StatusMessage = Loc.T("spotify.connected");
        }
        else if (!IsConfigured)
        {
            StatusMessage = Loc.T("spotify.not_configured");
        }
        else
        {
            StatusMessage = Loc.T("spotify.not_connected");
        }
    }

    private static string FormatMs(int ms)
    {
        if (ms <= 0) return "0:00";
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    [RelayCommand]
    private async Task AuthorizeAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            StatusMessage = Loc.T("spotify.not_configured");
            return;
        }

        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _authCts.Token;

        try
        {
            IsLoading = true;
            StatusMessage = Loc.T("spotify.opening_browser");

            var showSpotifyConsentDialog = _authorizeForceSpotifyConsentDialog;
            _authorizeForceSpotifyConsentDialog = false;

            // show_dialog=true only when Spotify refused an action (e.g. missing library scope).
            // Do not open OAuth on every generic API error (that was reopening the browser on each heart click).
            var authUrl = "https://accounts.spotify.com/authorize"
                + $"?client_id={Uri.EscapeDataString(_config.SpotifyClientId)}"
                + "&response_type=code"
                + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                + $"&scope={Uri.EscapeDataString(Scopes)}"
                + (showSpotifyConsentDialog ? "&show_dialog=true" : "&show_dialog=false");

            // Start the local listener BEFORE opening the browser
            var listenerTask = SpotifyService.ListenForAuthCodeAsync(RedirectUri, token);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                StatusMessage = Loc.T("spotify.browser_failed");
                _authCts.Cancel();
                return;
            }

            StatusMessage = Loc.T("spotify.wait_auth");

            var code = await listenerTask;
            if (string.IsNullOrEmpty(code))
            {
                StatusMessage = Loc.T("spotify.auth_cancelled");
                return;
            }

            StatusMessage = Loc.T("spotify.exchanging");
            var auth = await _service.GetAuthorizationCodeAsync(
                code,
                _config.SpotifyClientId,
                _config.SpotifyClientSecret,
                RedirectUri,
                token);

            if (auth == null || string.IsNullOrEmpty(auth.AccessToken))
            {
                StatusMessage = Loc.T("spotify.auth_failed");
                return;
            }

            _config.SpotifyAccessToken = auth.AccessToken;
            if (!string.IsNullOrEmpty(auth.RefreshToken))
                _config.SpotifyRefreshToken = auth.RefreshToken;
            _config.SpotifyTokenExpiryUtc = _service.TokenExpiryUtc;
            ConfigService.Save(_config);

            IsAuthenticated = true;
            StatusMessage = Loc.T("spotify.connected_ok");

            await RefreshCommand.ExecuteAsync(null);
            StartAutoRefresh();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Loc.T("spotify.auth_cancelled");
        }
        catch
        {
            StatusMessage = Loc.T("spotify.auth_error");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        StopAutoRefresh();
        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = null;

        _service.ClearAccessToken();
        _config.SpotifyAccessToken = string.Empty;
        _config.SpotifyRefreshToken = string.Empty;
        _config.SpotifyTokenExpiryUtc = DateTime.MinValue;
        ConfigService.Save(_config);

        IsAuthenticated = false;
        CurrentTrack = null;
        CurrentArtist = null;
        AlbumArtUrl = null;
        IsPlaying = false;
        ProgressMs = 0;
        DurationMs = 0;
        CurrentTrackId = null;
        IsCurrentTrackLiked = false;
        _cachedArtTrackId = null;
        _cachedArtUrl = null;
        StatusMessage = Loc.T("spotify.disconnected");
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return;

        try
        {
            if (_service.IsTokenExpired && !string.IsNullOrEmpty(_config.SpotifyRefreshToken))
            {
                var auth = await _service.RefreshAccessTokenAsync(
                    _config.SpotifyRefreshToken,
                    _config.SpotifyClientId,
                    _config.SpotifyClientSecret,
                    ct);

                if (auth == null)
                {
                    IsAuthenticated = false;
                    StatusMessage = Loc.T("spotify.session_expired_reauth");
                    return;
                }

                _config.SpotifyAccessToken = auth.AccessToken;
                if (!string.IsNullOrEmpty(auth.RefreshToken))
                    _config.SpotifyRefreshToken = auth.RefreshToken;
                _config.SpotifyTokenExpiryUtc = _service.TokenExpiryUtc;
                ConfigService.Save(_config);
            }

            var playback = await _service.GetCurrentPlaybackAsync(ct);

            // Sync device info (even if no track is playing)
            if (playback?.Device != null)
            {
                ActiveDeviceName = playback.Device.Name;
                CanControlVolume = playback.Device.SupportsVolume && !playback.Device.IsRestricted;

                if (playback.Device.VolumePercent.HasValue && CanControlVolume)
                {
                    // Only sync from API if the user hasn't touched the slider in the last 2s
                    if ((DateTime.UtcNow - _lastUserVolumeChangeUtc).TotalSeconds > 2)
                    {
                        _isSyncingVolume = true;
                        try { Volume = playback.Device.VolumePercent.Value; }
                        finally { _isSyncingVolume = false; }
                    }
                }
            }
            else
            {
                ActiveDeviceName = null;
                CanControlVolume = false;
            }

            if (playback?.Track != null)
            {
                var previousTrackId = CurrentTrackId;
                CurrentTrackId = playback.Track.Id;
                CurrentTrackType = string.IsNullOrEmpty(playback.Track.Type) ? "track" : playback.Track.Type;
                CurrentTrack = playback.Track.Name;
                CurrentArtist = string.Join(", ", playback.Track.Artists.Select(a => a.Name));
                IsPlaying = playback.IsPlaying;
                ProgressMs = playback.ProgressMs;
                DurationMs = playback.Track.DurationMs;

                // Only re-check like state when the track changes.
                // This avoids flicker/reset when Spotify temporarily fails to answer this endpoint.
                if (!string.IsNullOrEmpty(CurrentTrackId) && CurrentTrackId != previousTrackId)
                {
                    var liked = await _service.IsTrackSavedAsync(CurrentTrackId, CurrentTrackType, ct);
                    if (liked.HasValue)
                        IsCurrentTrackLiked = liked.Value;
                }

                AlbumArtUrl = SpotifyService.GetBestImageUrlFromTrack(playback.Track);
                if (string.IsNullOrEmpty(AlbumArtUrl) && !string.IsNullOrEmpty(CurrentTrackId))
                {
                    if (CurrentTrackId == _cachedArtTrackId && !string.IsNullOrEmpty(_cachedArtUrl))
                        AlbumArtUrl = _cachedArtUrl;
                    else
                    {
                        var resolved = await _service.ResolveBestImageUrlAsync(CurrentTrackId, CurrentTrackType, ct);
                        _cachedArtTrackId = CurrentTrackId;
                        _cachedArtUrl = resolved;
                        AlbumArtUrl = resolved;
                    }
                }
                else
                {
                    _cachedArtTrackId = null;
                    _cachedArtUrl = null;
                }

                StatusMessage = IsPlaying ? Loc.T("spotify.playing") : Loc.T("spotify.paused");
            }
            else
            {
                CurrentTrack = null;
                CurrentArtist = null;
                AlbumArtUrl = null;
                IsPlaying = false;
                ProgressMs = 0;
                DurationMs = 0;
                CurrentTrackId = null;
                CurrentTrackType = "track";
                IsCurrentTrackLiked = false;
                _cachedArtTrackId = null;
                _cachedArtUrl = null;
                StatusMessage = Loc.T("spotify.no_playback_status");
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            StatusMessage = Loc.T("spotify.fetch_error");
        }
    }

    /// <summary>
    /// Executes a playback action. If it fails with NoActiveDevice, tries to transfer
    /// playback to the first available device and retry once.
    /// </summary>
    private async Task<SpotifyPlaybackResult> TryPlaybackAsync(
        Func<CancellationToken, Task<SpotifyPlaybackResult>> action,
        bool playOnTransfer,
        CancellationToken ct)
    {
        var result = await action(ct);

        if (result == SpotifyPlaybackResult.NoActiveDevice)
        {
            StatusMessage = Loc.T("spotify.searching_device");
            var devices = await _service.GetDevicesAsync(ct);
            var target = devices.FirstOrDefault(d => !d.IsRestricted && !string.IsNullOrEmpty(d.Id));

            if (target == null)
            {
                StatusMessage = Loc.T("spotify.no_device");
                return result;
            }

            StatusMessage = Loc.Tf("spotify.transferring", target.Name);
            var transferred = await _service.TransferPlaybackAsync(target.Id!, playOnTransfer, ct);
            if (!transferred)
            {
                StatusMessage = Loc.T("spotify.transfer_failed");
                return result;
            }

            // Give Spotify a moment to activate the device, then retry
            await Task.Delay(700, ct);
            result = await action(ct);
        }

        switch (result)
        {
            case SpotifyPlaybackResult.NotPremium:
                StatusMessage = Loc.T("spotify.premium_playback");
                break;
            case SpotifyPlaybackResult.Unauthorized:
                StatusMessage = Loc.T("spotify.session_expired_reauth");
                IsAuthenticated = false;
                break;
            case SpotifyPlaybackResult.Error:
                StatusMessage = Loc.T("spotify.api_error");
                break;
        }

        return result;
    }

    [RelayCommand]
    private async Task PlayAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return;
        try
        {
            var result = await TryPlaybackAsync(_service.PlayAsync, playOnTransfer: true, ct);
            if (result == SpotifyPlaybackResult.Success)
            {
                IsPlaying = true;
                await Task.Delay(400, ct);
                await RefreshCommand.ExecuteAsync(null);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    [RelayCommand]
    private async Task PauseAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return;
        try
        {
            var result = await _service.PauseAsync(ct);
            if (result == SpotifyPlaybackResult.Success)
            {
                IsPlaying = false;
                await RefreshCommand.ExecuteAsync(null);
            }
            else if (result == SpotifyPlaybackResult.NotPremium)
            {
                StatusMessage = Loc.T("spotify.premium_required");
            }
            else if (result == SpotifyPlaybackResult.Unauthorized)
            {
                IsAuthenticated = false;
                StatusMessage = Loc.T("spotify.session_expired");
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task NextAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return;
        try
        {
            var result = await TryPlaybackAsync(_service.NextAsync, playOnTransfer: true, ct);
            if (result == SpotifyPlaybackResult.Success)
            {
                await Task.Delay(500, ct);
                await RefreshCommand.ExecuteAsync(null);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    [RelayCommand]
    private async Task PreviousAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated) return;
        try
        {
            var result = await TryPlaybackAsync(_service.PreviousAsync, playOnTransfer: true, ct);
            if (result == SpotifyPlaybackResult.Success)
            {
                await Task.Delay(500, ct);
                await RefreshCommand.ExecuteAsync(null);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    [RelayCommand]
    private async Task TogglePlayPauseAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
        {
            await AuthorizeAsync(ct);
            return;
        }

        if (IsPlaying)
            await PauseAsync(ct);
        else
            await PlayAsync(ct);
    }

    [RelayCommand]
    private async Task ToggleLikeAsync(CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return;
        if (string.IsNullOrEmpty(CurrentTrackId))
        {
            StatusMessage = Loc.T("spotify.no_track_like");
            return;
        }

        try
        {
            var result = IsCurrentTrackLiked
                ? await _service.RemoveTrackAsync(CurrentTrackId, CurrentTrackType, ct)
                : await _service.SaveTrackAsync(CurrentTrackId, CurrentTrackType, ct);

            if (result == SpotifyPlaybackResult.Success)
            {
                IsCurrentTrackLiked = !IsCurrentTrackLiked;
            }
            else if (result == SpotifyPlaybackResult.MissingScope)
            {
                StatusMessage = Loc.T("spotify.like_auth");
                _authorizeForceSpotifyConsentDialog = true;
                await AuthorizeAsync(ct);

                // Retry once right after re-authorization
                if (!IsAuthenticated || string.IsNullOrEmpty(CurrentTrackId))
                    return;

                result = IsCurrentTrackLiked
                    ? await _service.RemoveTrackAsync(CurrentTrackId, CurrentTrackType, ct)
                    : await _service.SaveTrackAsync(CurrentTrackId, CurrentTrackType, ct);

                if (result == SpotifyPlaybackResult.Success)
                {
                    IsCurrentTrackLiked = !IsCurrentTrackLiked;
                    return;
                }
                if (result == SpotifyPlaybackResult.MissingScope)
                {
                    StatusMessage = Loc.T("spotify.like_scope");
                    return;
                }
            }

            if (result == SpotifyPlaybackResult.Unauthorized)
            {
                IsAuthenticated = false;
                StatusMessage = Loc.T("spotify.session_expired");
            }
            else if (result == SpotifyPlaybackResult.UnsupportedItem)
            {
                StatusMessage = Loc.T("spotify.like_unsupported");
            }
            else if (result != SpotifyPlaybackResult.Success)
            {
                StatusMessage = Loc.Tf("spotify.like_failed", _service.LastLibraryError);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            StatusMessage = Loc.T("spotify.like_error");
        }
    }

    public void StartAutoRefresh()
    {
        StopAutoRefresh();
        if (!IsAuthenticated) return;
        _refreshCts = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_refreshCts.Token);
    }

    public void StopAutoRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    public void Dispose()
    {
        StopAutoRefresh();
        _authCts?.Cancel();
        _authCts?.Dispose();
        _authCts = null;
        _volumeDebounceCts?.Cancel();
        _volumeDebounceCts?.Dispose();
        _volumeDebounceCts = null;
    }

    private async Task AutoRefreshLoopAsync(CancellationToken ct)
    {
        // First refresh immediately
        try { await RefreshCommand.ExecuteAsync(null); } catch { }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshIntervalMs, ct);
                if (ct.IsCancellationRequested) break;
                await RefreshCommand.ExecuteAsync(null);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Continue on transient errors
            }
        }
    }
}
