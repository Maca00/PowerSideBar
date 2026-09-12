using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PowerSideBar.Models;

namespace PowerSideBar.Services;

public sealed class WeatherService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    private List<DailyForecast>? _cachedForecasts;
    private DateTime _cacheTime = DateTime.MinValue;
    private double _cachedLatitude;
    private double _cachedLongitude;
    private string _cachedTimezone = string.Empty;

    public void ClearCache()
    {
        _cachedForecasts = null;
        _cacheTime = DateTime.MinValue;
    }

    public async Task<List<GeoLocation>> SearchLocationsAsync(
        string query, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length < 2)
            return new List<GeoLocation>();

        var url =
            "https://geocoding-api.open-meteo.com/v1/search"
            + $"?name={Uri.EscapeDataString(query)}"
            + "&count=8&language=fr&format=json";

        var json = await HttpClient.GetStringAsync(url, cancellationToken);
        var response = JsonSerializer.Deserialize<GeocodingResponse>(json);
        if (response?.Results == null || response.Results.Length == 0)
            return new List<GeoLocation>();

        var list = new List<GeoLocation>(response.Results.Length);
        foreach (var r in response.Results)
        {
            var name = r.Name ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var parts = new List<string> { name };
            if (!string.IsNullOrWhiteSpace(r.Admin1))
                parts.Add(r.Admin1!);
            if (!string.IsNullOrWhiteSpace(r.Country))
                parts.Add(r.Country!);
            if (r.Postcodes is { Length: > 0 } && !string.IsNullOrWhiteSpace(r.Postcodes[0]))
                parts.Add(r.Postcodes[0]);

            list.Add(new GeoLocation
            {
                Name = name,
                Admin1 = r.Admin1,
                Country = r.Country,
                Latitude = r.Latitude,
                Longitude = r.Longitude,
                Timezone = string.IsNullOrWhiteSpace(r.Timezone) ? "Europe/Paris" : r.Timezone!,
                DisplayLabel = string.Join(", ", parts),
            });
        }

        return list;
    }

    public async Task<List<DailyForecast>> GetForecastAsync(
        double latitude, double longitude, string timezone,
        CancellationToken cancellationToken = default)
    {
        // Return cache if fresh and for the same location
        if (_cachedForecasts != null
            && DateTime.UtcNow - _cacheTime < CacheDuration
            && _cachedLatitude == latitude
            && _cachedLongitude == longitude
            && string.Equals(_cachedTimezone, timezone, StringComparison.Ordinal))
            return _cachedForecasts;

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
            "&daily=temperature_2m_max,temperature_2m_min,weathercode,precipitation_sum,windspeed_10m_max,windgusts_10m_max,winddirection_10m_dominant" +
            "&models=gfs_seamless&timezone={2}&forecast_days=7",
            latitude, longitude, Uri.EscapeDataString(timezone));

        var json = await HttpClient.GetStringAsync(url, cancellationToken);
        var response = JsonSerializer.Deserialize<WeatherResponse>(json);

        if (response?.Daily?.Time == null)
            return _cachedForecasts ?? new List<DailyForecast>();

        var forecasts = new List<DailyForecast>();
        var daily = response.Daily;
        var culture = Loc.Culture;

        for (int i = 0; i < daily.Time.Length; i++)
        {
            var date = DateTime.Parse(daily.Time[i], CultureInfo.InvariantCulture);
            var code = daily.WeatherCode?[i] ?? 0;
            var (label, icon) = MapWeatherCode(code);

            var rawDate = date.ToString("dddd dd MMMM", culture);
            var dayName = culture.TextInfo.ToTitleCase(rawDate);

            forecasts.Add(new DailyForecast(
                DayName: dayName,
                Date: date,
                TempMax: daily.TemperatureMax?[i] ?? 0,
                TempMin: daily.TemperatureMin?[i] ?? 0,
                WeatherCode: code,
                PrecipitationMm: daily.PrecipitationSum?[i] ?? 0,
                WindSpeedMax: daily.WindSpeedMax?[i] ?? 0,
                WindGustsMax: daily.WindGustsMax?[i] ?? 0,
                WindDirectionDegrees: daily.WindDirectionDominant?[i] ?? 0,
                ConditionLabel: label,
                ConditionIcon: icon
            ));
        }

        _cachedForecasts = forecasts;
        _cacheTime = DateTime.UtcNow;
        _cachedLatitude = latitude;
        _cachedLongitude = longitude;
        _cachedTimezone = timezone;
        return forecasts;
    }

    private const string IconBase = "pack://application:,,,/Assets/Weather/Weather Icon";

    public static (string Label, string Icon) MapWeatherCode(int code)
    {
        return code switch
        {
            0 => (Loc.T("weather.clear"), $"{IconBase}.png"),
            1 => (Loc.T("weather.mainly_clear"), $"{IconBase}-4.png"),
            2 => (Loc.T("weather.partly_cloudy"), $"{IconBase}-6.png"),
            3 => (Loc.T("weather.overcast"), $"{IconBase}-2.png"),
            45 or 48 => (Loc.T("weather.fog"), $"{IconBase}-42.png"),
            51 => (Loc.T("weather.drizzle_light"), $"{IconBase}-16.png"),
            53 => (Loc.T("weather.drizzle"), $"{IconBase}-17.png"),
            55 => (Loc.T("weather.drizzle_dense"), $"{IconBase}-19.png"),
            56 or 57 => (Loc.T("weather.drizzle_freezing"), $"{IconBase}-24.png"),
            61 => (Loc.T("weather.rain_light"), $"{IconBase}-16.png"),
            63 => (Loc.T("weather.rain"), $"{IconBase}-18.png"),
            65 => (Loc.T("weather.rain_heavy"), $"{IconBase}-19.png"),
            66 or 67 => (Loc.T("weather.rain_freezing"), $"{IconBase}-24.png"),
            71 => (Loc.T("weather.snow_light"), $"{IconBase}-26.png"),
            73 => (Loc.T("weather.snow"), $"{IconBase}-27.png"),
            75 => (Loc.T("weather.snow_heavy"), $"{IconBase}-29.png"),
            77 => (Loc.T("weather.snow_grains"), $"{IconBase}-36.png"),
            80 => (Loc.T("weather.showers_light"), $"{IconBase}-9.png"),
            81 => (Loc.T("weather.showers"), $"{IconBase}-11.png"),
            82 => (Loc.T("weather.showers_violent"), $"{IconBase}-23.png"),
            85 => (Loc.T("weather.snow_showers"), $"{IconBase}-34.png"),
            86 => (Loc.T("weather.snow_showers_heavy"), $"{IconBase}-29.png"),
            95 => (Loc.T("weather.thunderstorm"), $"{IconBase}-13.png"),
            96 or 99 => (Loc.T("weather.thunderstorm_hail"), $"{IconBase}-14.png"),
            _ => (Loc.T("weather.unknown"), $"{IconBase}-8.png"),
        };
    }
}
