using System;
using System.Globalization;
using System.Text.Json.Serialization;
using PowerSideBar.Services;

namespace PowerSideBar.Models;

/// <summary>
/// View-friendly daily forecast record.
/// </summary>
public record DailyForecast(
    string DayName,
    DateTime Date,
    double TempMax,
    double TempMin,
    int WeatherCode,
    double PrecipitationMm,
    double WindSpeedMax,
    double WindGustsMax,
    double WindDirectionDegrees,
    string ConditionLabel,
    string ConditionIcon
)
{
    public bool HasPrecipitation => PrecipitationMm >= 0.1;

    public string PrecipitationText =>
        string.Format(Loc.Culture, "{0:0.#} mm", PrecipitationMm);

    /// <summary>
    /// WPF rotation for a right-pointing arrow so it shows where the wind blows
    /// (Open-Meteo direction = meteorological "from").
    /// </summary>
    public double WindArrowAngle => WindDirectionDegrees + 90.0;
}

/// <summary>
/// Mirrors the Open-Meteo JSON response for direct deserialization.
/// </summary>
public class WeatherResponse
{
    [JsonPropertyName("daily")]
    public DailyData? Daily { get; set; }
}

public class DailyData
{
    [JsonPropertyName("time")]
    public string[]? Time { get; set; }

    [JsonPropertyName("temperature_2m_max")]
    public double[]? TemperatureMax { get; set; }

    [JsonPropertyName("temperature_2m_min")]
    public double[]? TemperatureMin { get; set; }

    [JsonPropertyName("weathercode")]
    public int[]? WeatherCode { get; set; }

    [JsonPropertyName("precipitation_sum")]
    public double[]? PrecipitationSum { get; set; }

    [JsonPropertyName("windspeed_10m_max")]
    public double[]? WindSpeedMax { get; set; }

    [JsonPropertyName("windgusts_10m_max")]
    public double[]? WindGustsMax { get; set; }

    [JsonPropertyName("winddirection_10m_dominant")]
    public double[]? WindDirectionDominant { get; set; }
}

/// <summary>One result from Open-Meteo geocoding search.</summary>
public sealed class GeoLocation
{
    public string Name { get; init; } = string.Empty;
    public string? Admin1 { get; init; }
    public string? Country { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Timezone { get; init; } = "Europe/Paris";
    public string DisplayLabel { get; init; } = string.Empty;
}

public class GeocodingResponse
{
    [JsonPropertyName("results")]
    public GeocodingResult[]? Results { get; set; }
}

public class GeocodingResult
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("admin1")]
    public string? Admin1 { get; set; }

    [JsonPropertyName("postcodes")]
    public string[]? Postcodes { get; set; }
}
