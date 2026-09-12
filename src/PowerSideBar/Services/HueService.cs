using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using PowerSideBar.Models;

namespace PowerSideBar.Services;

public class HueService
{
    // Bridge uses a self-signed TLS certificate → bypass validation
    private static readonly HttpClient _httpClient = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private static readonly HttpClient _discoveryClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ── Bridge discovery ──────────────────────────────────────────────────────

    /// <summary>Queries discovery.meethue.com and returns the first bridge IP found.</summary>
    public async Task<string?> DiscoverBridgeAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _discoveryClient.GetStringAsync(
                "https://discovery.meethue.com/", ct);
            var bridges = JsonSerializer.Deserialize<List<HueBridgeInfo>>(json, _jsonOptions);
            return bridges?.FirstOrDefault(b => !string.IsNullOrEmpty(b.InternalIpAddress))
                          ?.InternalIpAddress;
        }
        catch
        {
            return null;
        }
    }

    // ── Pairing (press bridge button first) ───────────────────────────────────

    /// <summary>
    /// Attempts to create an API key. The user must have pressed the physical
    /// link button on the bridge within the last ~30 seconds.
    /// Returns the API key (username) on success, null on failure.
    /// </summary>
    public async Task<(string? ApiKey, string? ErrorMessage)> PairAsync(
        string bridgeIp, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            devicetype = "PowerSideBar#Windows",
            generateclientkey = true
        });

        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{bridgeIp}/api")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        try
        {
            var response = await _httpClient.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            var results = JsonSerializer.Deserialize<List<HuePairResult>>(json, _jsonOptions);
            if (results == null || results.Count == 0)
                return (null, "Réponse invalide du bridge.");

            var first = results[0];
            if (first.Success != null)
                return (first.Success.Username, null);

            var desc = first.Error?.Description ?? Loc.T("spotify.unknown_error");
            if (desc.Contains("link button", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("101", StringComparison.OrdinalIgnoreCase))
                return (null, "Bouton du bridge non pressé. Appuyez dessus et réessayez.");

            return (null, desc);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, Loc.Tf("hue.bridge_error", ex.Message));
        }
    }

    // ── Rooms + lights ────────────────────────────────────────────────────────

    /// <summary>Returns rooms with their resolved light objects.</summary>
    public async Task<List<(HueRoom Room, List<HueLight> Lights, string? GroupedLightId)>>
        GetRoomsWithLightsAsync(string bridgeIp, string apiKey, CancellationToken ct = default)
    {
        var roomsTask = GetAsync<HueRoom>("room", bridgeIp, apiKey, ct);
        var lightsTask = GetAsync<HueLight>("light", bridgeIp, apiKey, ct);
        var groupedTask = GetAsync<HueGroupedLight>("grouped_light", bridgeIp, apiKey, ct);

        await Task.WhenAll(roomsTask, lightsTask, groupedTask);

        var rooms = await roomsTask;
        var allLights = (await lightsTask).ToDictionary(l => l.Id);
        var groupedLights = (await groupedTask).ToDictionary(g => g.Id);

        var result = new List<(HueRoom, List<HueLight>, string?)>();

        foreach (var room in rooms)
        {
            var lights = room.Children
                .Where(c => c.Rtype == "device")
                .SelectMany(_ => allLights.Values)     // resolve by device→light lookup below
                .ToList();

            // Better: resolve children that are "light" type directly
            lights = room.Children
                .Where(c => c.Rtype == "light" && allLights.ContainsKey(c.Rid))
                .Select(c => allLights[c.Rid])
                .ToList();

            // Fallback: children are "device" rtype → match lights whose owner device is in children
            // (CLIP v2 rooms list device rids, not light rids)
            if (lights.Count == 0)
            {
                var deviceIds = room.Children.Where(c => c.Rtype == "device").Select(c => c.Rid).ToHashSet();
                lights = allLights.Values.ToList(); // will be filtered once we fetch device→light map
                // Simplified: include all lights since CLIP v2 room.children are device-typed
                // A full implementation would GET /resource/device to resolve device→light mapping.
                // For simplicity, we'll distribute lights across rooms proportionally.
                lights = new List<HueLight>();
            }

            var groupedLightId = room.Services
                .FirstOrDefault(s => s.Rtype == "grouped_light")?.Rid;

            result.Add((room, lights, groupedLightId));
        }

        // If lights list is empty for all rooms (device-typed children), fall back to fetching devices
        if (result.All(r => r.Item2.Count == 0))
        {
            return await GetRoomsViaDevicesAsync(bridgeIp, apiKey, rooms, allLights, ct);
        }

        return result;
    }

    /// <summary>
    /// Resolves rooms via /resource/device when room children are device-typed.
    /// This is the standard CLIP v2 topology.
    /// </summary>
    private async Task<List<(HueRoom, List<HueLight>, string?)>>
        GetRoomsViaDevicesAsync(
            string bridgeIp, string apiKey,
            List<HueRoom> rooms,
            Dictionary<string, HueLight> allLights,
            CancellationToken ct)
    {
        var devices = await GetAsync<HueDevice>("device", bridgeIp, apiKey, ct);
        // device.services contains light rids
        var deviceToLights = devices.ToDictionary(
            d => d.Id,
            d => d.Services
                  .Where(s => s.Rtype == "light" && allLights.ContainsKey(s.Rid))
                  .Select(s => allLights[s.Rid])
                  .ToList());

        var result = new List<(HueRoom, List<HueLight>, string?)>();
        foreach (var room in rooms)
        {
            var lights = room.Children
                .Where(c => c.Rtype == "device" && deviceToLights.ContainsKey(c.Rid))
                .SelectMany(c => deviceToLights[c.Rid])
                .ToList();

            var groupedLightId = room.Services
                .FirstOrDefault(s => s.Rtype == "grouped_light")?.Rid;

            result.Add((room, lights, groupedLightId));
        }
        return result;
    }

    // ── Light control ─────────────────────────────────────────────────────────

    public Task SetOnOffAsync(string bridgeIp, string apiKey, string lightId, bool on,
        CancellationToken ct = default)
        => PutAsync($"light/{lightId}", bridgeIp, apiKey, new { on = new { on } }, ct);

    public Task SetBrightnessAsync(string bridgeIp, string apiKey, string lightId, double brightness,
        CancellationToken ct = default)
        => PutAsync($"light/{lightId}", bridgeIp, apiKey,
            new { dimming = new { brightness = Math.Clamp(brightness, 1.0, 100.0) } }, ct);

    public Task SetColorTempAsync(string bridgeIp, string apiKey, string lightId, int mirek,
        CancellationToken ct = default)
        => PutAsync($"light/{lightId}", bridgeIp, apiKey,
            new { color_temperature = new { mirek } }, ct);

    public Task SetColorXyAsync(string bridgeIp, string apiKey, string lightId, double x, double y,
        CancellationToken ct = default)
        => PutAsync($"light/{lightId}", bridgeIp, apiKey,
            new { color = new { xy = new { x, y } } }, ct);

    public Task ToggleGroupedLightAsync(string bridgeIp, string apiKey, string groupedLightId, bool on,
        CancellationToken ct = default)
        => PutAsync($"grouped_light/{groupedLightId}", bridgeIp, apiKey, new { on = new { on } }, ct);

    public Task SetGroupedLightBrightnessAsync(string bridgeIp, string apiKey, string groupedLightId, double brightness,
        CancellationToken ct = default)
        => PutAsync($"grouped_light/{groupedLightId}", bridgeIp, apiKey,
            new { dimming = new { brightness = Math.Clamp(brightness, 1.0, 100.0) } }, ct);

    // ── Scenes ────────────────────────────────────────────────────────────────

    /// <summary>Returns all scenes for a given room ID.</summary>
    public async Task<List<HueScene>> GetScenesForRoomAsync(
        string bridgeIp, string apiKey, string roomId, CancellationToken ct = default)
    {
        var all = await GetAsync<HueScene>("scene", bridgeIp, apiKey, ct);
        return all.Where(s => s.Group?.Rid == roomId && s.Group?.Rtype == "room").ToList();
    }

    /// <summary>Activates a scene.</summary>
    public Task ActivateSceneAsync(string bridgeIp, string apiKey, string sceneId,
        CancellationToken ct = default)
        => PutAsync($"scene/{sceneId}", bridgeIp, apiKey, new { recall = new { action = "active" } }, ct);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<List<T>> GetAsync<T>(
        string resource, string bridgeIp, string apiKey, CancellationToken ct)
    {
        var req = BuildRequest(HttpMethod.Get, $"https://{bridgeIp}/clip/v2/resource/{resource}", apiKey);
        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<HueApiResponse<T>>(json, _jsonOptions);
        return result?.Data ?? new();
    }

    private async Task PutAsync(
        string resource, string bridgeIp, string apiKey, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, _jsonOptions);
        var req = BuildRequest(HttpMethod.Put, $"https://{bridgeIp}/clip/v2/resource/{resource}", apiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string apiKey)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("hue-application-key", apiKey);
        return req;
    }
}

// Internal device model (CLIP v2 topology)
file class HueDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("services")]
    public List<HueResourceLink> Services { get; set; } = new();
}
