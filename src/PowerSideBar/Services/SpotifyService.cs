using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace PowerSideBar.Services;

public class SpotifyPlaybackState
{
    [JsonPropertyName("item")]
    public SpotifyTrack? Track { get; set; }

    [JsonPropertyName("is_playing")]
    public bool IsPlaying { get; set; }

    [JsonPropertyName("progress_ms")]
    public int ProgressMs { get; set; }

    [JsonPropertyName("device")]
    public SpotifyDevice? Device { get; set; }
}

public class SpotifyTrack
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("artists")]
    public List<SpotifyArtist> Artists { get; set; } = new();

    [JsonPropertyName("album")]
    public SpotifyAlbum? Album { get; set; }

    // For episodes/podcasts, images are often directly on the item.
    [JsonPropertyName("images")]
    public List<SpotifyImage> Images { get; set; } = new();

    [JsonPropertyName("show")]
    public SpotifyShow? Show { get; set; }

    [JsonPropertyName("duration_ms")]
    public int DurationMs { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "track";
}

public class SpotifyArtist
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class SpotifyAlbum
{
    [JsonPropertyName("images")]
    public List<SpotifyImage> Images { get; set; } = new();
}

/// <summary>Show metadata when the playing item is an episode (podcast).</summary>
public class SpotifyShow
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public List<SpotifyImage> Images { get; set; } = new();
}

public class SpotifyImage
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }
}

public class SpotifyDevice
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("is_restricted")]
    public bool IsRestricted { get; set; }

    [JsonPropertyName("supports_volume")]
    public bool SupportsVolume { get; set; } = true;

    [JsonPropertyName("volume_percent")]
    public int? VolumePercent { get; set; }
}

public class SpotifyDevicesResponse
{
    [JsonPropertyName("devices")]
    public List<SpotifyDevice> Devices { get; set; } = new();
}

public enum SpotifyPlaybackResult
{
    Success,
    NoActiveDevice,
    NotPremium,
    Unauthorized,
    MissingScope,
    UnsupportedItem,
    Error,
}

public class SpotifyAuthResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;
}

public class SpotifyService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string ApiBaseUrl = "https://api.spotify.com/v1";
    private const string AuthUrl = "https://accounts.spotify.com/api/token";

    private string _accessToken = string.Empty;
    private DateTime _tokenExpiry = DateTime.MinValue;
    public string LastLibraryError { get; private set; } = string.Empty;

    public DateTime TokenExpiryUtc => _tokenExpiry;
    public bool HasAccessToken => !string.IsNullOrEmpty(_accessToken);

    /// <summary>
    /// Starts a local TCP listener on the redirect URI and waits for Spotify to redirect
    /// the browser there with the authorization code (or an error).
    /// Uses TcpListener (not HttpListener) to avoid Windows URL-ACL permission issues
    /// when binding to 127.0.0.1 (now required by Spotify since Apr 2025).
    /// Returns the authorization code, or null on failure / cancellation.
    /// </summary>
    public static async Task<string?> ListenForAuthCodeAsync(
        string redirectUri,
        CancellationToken ct = default)
    {
        Uri uri;
        try { uri = new Uri(redirectUri); } catch { return null; }

        var address = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.Parse(uri.Host);
        var port = uri.Port;

        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(address, port);
            listener.Start();
        }
        catch
        {
            try { listener?.Stop(); } catch { }
            return null;
        }

        try
        {
            using var reg = ct.Register(() =>
            {
                try { listener.Stop(); } catch { }
            });

            using var client = await listener.AcceptTcpClientAsync(ct);
            using var stream = client.GetStream();

            // Read request line + headers (until blank line)
            var requestText = new StringBuilder();
            var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;
                requestText.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (requestText.ToString().Contains("\r\n\r\n")) break;
                if (requestText.Length > 16384) break; // safety cap
            }

            // Parse "GET /callback?code=...&state=... HTTP/1.1"
            var firstLine = requestText.ToString().Split("\r\n", 2)[0];
            var parts = firstLine.Split(' ');
            string? code = null;
            string? error = null;
            if (parts.Length >= 2)
            {
                var path = parts[1];
                var qIdx = path.IndexOf('?');
                if (qIdx >= 0)
                {
                    var query = HttpUtility.ParseQueryString(path.Substring(qIdx + 1));
                    code = query["code"];
                    error = query["error"];
                }
            }

            var html = !string.IsNullOrEmpty(code)
                ? "<html><body style='font-family:sans-serif;background:#1A1A1A;color:#1DB954;text-align:center;padding-top:60px'>" +
                  $"<h2>{WebUtility.HtmlEncode(Loc.T("spotify.oauth_ok_title"))} ✔</h2><p>{WebUtility.HtmlEncode(Loc.T("spotify.oauth_ok_body"))}</p></body></html>"
                : "<html><body style='font-family:sans-serif;background:#1A1A1A;color:#E55;text-align:center;padding-top:60px'>" +
                  $"<h2>{WebUtility.HtmlEncode(Loc.T("spotify.oauth_fail_title"))}</h2><p>{WebUtility.HtmlEncode(error ?? Loc.T("spotify.unknown_error"))}</p></body></html>";

            var body = Encoding.UTF8.GetBytes(html);
            var headers =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(headers);
            try
            {
                await stream.WriteAsync(headerBytes, 0, headerBytes.Length, ct);
                await stream.WriteAsync(body, 0, body.Length, ct);
                await stream.FlushAsync(ct);
            }
            catch { }

            return !string.IsNullOrEmpty(code) ? code : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }
    }

    public async Task<SpotifyAuthResponse?> GetAuthorizationCodeAsync(
        string code,
        string clientId,
        string clientSecret,
        string redirectUri,
        CancellationToken ct = default)
    {
        try
        {
            var authHeader = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
            });

            var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
            {
                Headers = { { "Authorization", $"Basic {authHeader}" } },
                Content = content
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var auth = JsonSerializer.Deserialize<SpotifyAuthResponse>(json, _jsonOptions);
            if (auth != null && !string.IsNullOrEmpty(auth.AccessToken))
            {
                _accessToken = auth.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(auth.ExpiresIn - 60);
            }

            return auth;
        }
        catch
        {
            return null;
        }
    }

    public async Task<SpotifyAuthResponse?> RefreshAccessTokenAsync(
        string refreshToken,
        string clientId,
        string clientSecret,
        CancellationToken ct = default)
    {
        try
        {
            var authHeader = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            });

            var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
            {
                Headers = { { "Authorization", $"Basic {authHeader}" } },
                Content = content
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var auth = JsonSerializer.Deserialize<SpotifyAuthResponse>(json, _jsonOptions);
            if (auth != null && !string.IsNullOrEmpty(auth.AccessToken))
            {
                _accessToken = auth.AccessToken;
                _tokenExpiry = DateTime.UtcNow.AddSeconds(auth.ExpiresIn - 60);
                return auth;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public void SetAccessToken(string token, int expiresInSeconds)
    {
        _accessToken = token;
        _tokenExpiry = DateTime.UtcNow.AddSeconds(expiresInSeconds - 60);
    }

    public void SetAccessToken(string token, DateTime expiryUtc)
    {
        _accessToken = token;
        _tokenExpiry = expiryUtc;
    }

    public void ClearAccessToken()
    {
        _accessToken = string.Empty;
        _tokenExpiry = DateTime.MinValue;
    }

    public bool IsTokenExpired => DateTime.UtcNow >= _tokenExpiry;

    private async Task<T?> GetAsync<T>(
        string endpoint,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            return default;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}{endpoint}")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } }
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return default;

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }
        catch
        {
            return default;
        }
    }

    public async Task<SpotifyPlaybackState?> GetCurrentPlaybackAsync(CancellationToken ct = default)
    {
        var playback = await GetAsync<SpotifyPlaybackState>("/me/player", ct);
        if (playback?.Track != null)
            return playback;

        // Fallback: Web Player can briefly return incomplete /me/player data
        // while currently-playing already has the new item.
        var current = await GetAsync<SpotifyPlaybackState>("/me/player/currently-playing", ct);
        return current ?? playback;
    }

    /// <summary>
    /// When playback payloads omit artwork, fetch full track/episode metadata (official Web API).
    /// </summary>
    public async Task<string?> ResolveBestImageUrlAsync(string? itemId, string itemType, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(itemId) || string.IsNullOrEmpty(_accessToken))
            return null;

        var type = string.IsNullOrWhiteSpace(itemType) ? "track" : itemType.Trim().ToLowerInvariant();
        var endpoint = type == "episode"
            ? $"/episodes/{Uri.EscapeDataString(itemId)}"
            : $"/tracks/{Uri.EscapeDataString(itemId)}";

        var detail = await GetAsync<SpotifyTrack>(endpoint, ct);
        if (detail == null)
            return null;

        return PickBestImageUrl(detail.Album?.Images, detail.Images, detail.Show?.Images);
    }

    public static string? GetBestImageUrlFromTrack(SpotifyTrack? track)
    {
        if (track == null) return null;
        return PickBestImageUrl(track.Album?.Images, track.Images, track.Show?.Images);
    }

    private static string? PickBestImageUrl(
        IEnumerable<SpotifyImage>? albumImages,
        IEnumerable<SpotifyImage>? itemImages,
        IEnumerable<SpotifyImage>? showImages)
    {
        foreach (var set in new[] { albumImages, itemImages, showImages })
        {
            if (set == null) continue;
            var list = set as IList<SpotifyImage> ?? set.ToList();
            if (list.Count == 0) continue;
            var best = list.OrderByDescending(i => i.Width).FirstOrDefault();
            if (best != null && !string.IsNullOrEmpty(best.Url))
                return best.Url;
        }

        return null;
    }

    public async Task<List<SpotifyDevice>> GetDevicesAsync(CancellationToken ct = default)
    {
        var resp = await GetAsync<SpotifyDevicesResponse>("/me/player/devices", ct);
        return resp?.Devices ?? new List<SpotifyDevice>();
    }

    public async Task<bool> TransferPlaybackAsync(string deviceId, bool play, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(deviceId))
                return false;

            var body = $"{{\"device_ids\":[\"{deviceId}\"],\"play\":{(play ? "true" : "false")}}}";
            var request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBaseUrl}/me/player")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } },
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SpotifyPlaybackResult> SendPlaybackAsync(
        HttpMethod method,
        string endpoint,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_accessToken))
            return SpotifyPlaybackResult.Unauthorized;

        try
        {
            var request = new HttpRequestMessage(method, $"{ApiBaseUrl}{endpoint}")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } },
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return SpotifyPlaybackResult.Success;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return SpotifyPlaybackResult.Unauthorized;

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                // 403 is typically "user is not premium" for playback endpoints
                return SpotifyPlaybackResult.NotPremium;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // 404 NO_ACTIVE_DEVICE is the common case
                return SpotifyPlaybackResult.NoActiveDevice;
            }

            return SpotifyPlaybackResult.Error;
        }
        catch
        {
            return SpotifyPlaybackResult.Error;
        }
    }

    public async Task<SpotifyPlaybackResult> SetVolumeAsync(int volumePercent, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken))
            return SpotifyPlaybackResult.Unauthorized;

        volumePercent = Math.Clamp(volumePercent, 0, 100);

        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Put,
                $"{ApiBaseUrl}/me/player/volume?volume_percent={volumePercent}")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } },
            };

            var response = await _httpClient.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
                return SpotifyPlaybackResult.Success;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return SpotifyPlaybackResult.Unauthorized;
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return SpotifyPlaybackResult.NotPremium;
            if (response.StatusCode == HttpStatusCode.NotFound)
                return SpotifyPlaybackResult.NoActiveDevice;

            return SpotifyPlaybackResult.Error;
        }
        catch
        {
            return SpotifyPlaybackResult.Error;
        }
    }

    private async Task<SpotifyPlaybackResult> SendLibraryAsync(
        HttpMethod method,
        string itemType,
        string trackId,
        CancellationToken ct)
    {
        LastLibraryError = string.Empty;

        if (string.IsNullOrEmpty(_accessToken))
        {
            LastLibraryError = "No access token";
            return SpotifyPlaybackResult.Unauthorized;
        }
        if (string.IsNullOrEmpty(trackId))
        {
            LastLibraryError = "No track id";
            return SpotifyPlaybackResult.Error;
        }

        try
        {
            var normalizedType = string.IsNullOrWhiteSpace(itemType) ? "track" : itemType.Trim().ToLowerInvariant();
            var libraryUri = $"spotify:{normalizedType}:{trackId}";
            var request = new HttpRequestMessage(
                method,
                $"{ApiBaseUrl}/me/library?uris={Uri.EscapeDataString(libraryUri)}")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } },
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return SpotifyPlaybackResult.Success;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                LastLibraryError = "401 unauthorized";
                return SpotifyPlaybackResult.Unauthorized;
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                LastLibraryError = $"403 forbidden: {body}";
                if (body.IndexOf("insufficient_scope", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SpotifyPlaybackResult.MissingScope;
            }
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                LastLibraryError = $"400 bad_request: {body}";
                if (body.IndexOf("track", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    body.IndexOf("episode", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SpotifyPlaybackResult.UnsupportedItem;
            }
            LastLibraryError = $"{(int)response.StatusCode} {response.StatusCode}";
            return SpotifyPlaybackResult.Error;
        }
        catch (Exception ex)
        {
            LastLibraryError = ex.Message;
            return SpotifyPlaybackResult.Error;
        }
    }

    public async Task<bool?> IsTrackSavedAsync(string trackId, string itemType = "track", CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(trackId))
            return null;

        try
        {
            var normalizedType = string.IsNullOrWhiteSpace(itemType) ? "track" : itemType.Trim().ToLowerInvariant();
            var libraryUri = $"spotify:{normalizedType}:{trackId}";
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{ApiBaseUrl}/me/library/contains?uris={Uri.EscapeDataString(libraryUri)}")
            {
                Headers = { { "Authorization", $"Bearer {_accessToken}" } },
            };

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct);
            var saved = JsonSerializer.Deserialize<bool[]>(json);
            if (saved == null || saved.Length == 0)
                return null;
            return saved[0];
        }
        catch
        {
            return null;
        }
    }

    public Task<SpotifyPlaybackResult> SaveTrackAsync(string trackId, string itemType = "track", CancellationToken ct = default) =>
        SendLibraryAsync(HttpMethod.Put, itemType, trackId, ct);

    public Task<SpotifyPlaybackResult> RemoveTrackAsync(string trackId, string itemType = "track", CancellationToken ct = default) =>
        SendLibraryAsync(HttpMethod.Delete, itemType, trackId, ct);

    public Task<SpotifyPlaybackResult> PlayAsync(CancellationToken ct = default) =>
        SendPlaybackAsync(HttpMethod.Put, "/me/player/play", ct);

    public Task<SpotifyPlaybackResult> PauseAsync(CancellationToken ct = default) =>
        SendPlaybackAsync(HttpMethod.Put, "/me/player/pause", ct);

    public Task<SpotifyPlaybackResult> NextAsync(CancellationToken ct = default) =>
        SendPlaybackAsync(HttpMethod.Post, "/me/player/next", ct);

    public Task<SpotifyPlaybackResult> PreviousAsync(CancellationToken ct = default) =>
        SendPlaybackAsync(HttpMethod.Post, "/me/player/previous", ct);
}
