using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSideBar.Models;
using PowerSideBar.Services;

namespace PowerSideBar.ViewModels;

public partial class WeatherViewModel : ObservableObject
{
    private readonly WeatherService _weatherService;
    private double _latitude;
    private double _longitude;
    private string _timezone;

    [ObservableProperty]
    private string _cityName = string.Empty;

    [ObservableProperty]
    private string _currentDateLabel = string.Empty;

    [ObservableProperty]
    private string _currentDateSubtitle = string.Empty;

    [ObservableProperty]
    private string _currentTemp = "--°";

    [ObservableProperty]
    private string _currentTempMin = string.Empty;

    [ObservableProperty]
    private double _currentTempMaxValue;

    [ObservableProperty]
    private double _currentTempMinValue;

    [ObservableProperty]
    private string _currentConditionIcon = "pack://application:,,,/Assets/Weather/Weather Icon-8.png";

    [ObservableProperty]
    private string _currentConditionLabel = string.Empty;

    [ObservableProperty]
    private string _currentWind = string.Empty;

    [ObservableProperty]
    private string _currentWindGusts = string.Empty;

    [ObservableProperty]
    private double _currentWindArrowAngle;

    [ObservableProperty]
    private string _currentPrecipitation = string.Empty;

    [ObservableProperty]
    private bool _hasCurrentPrecipitation;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _hasData;

    public ObservableCollection<DailyForecast> Forecasts { get; } = new();

    public WeatherViewModel(
        WeatherService weatherService,
        double latitude, double longitude,
        string timezone, string cityName)
    {
        _weatherService = weatherService;
        _latitude = latitude;
        _longitude = longitude;
        _timezone = timezone;
        CityName = cityName;
    }

    /// <summary>Updates location used by the next Refresh (e.g. after settings save).</summary>
    public void UpdateLocation(double latitude, double longitude, string timezone, string cityName)
    {
        _latitude = latitude;
        _longitude = longitude;
        _timezone = timezone;
        CityName = cityName;
        _weatherService.ClearCache();
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsLoading) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var forecasts = await _weatherService.GetForecastAsync(
                _latitude, _longitude, _timezone, cancellationToken);

            Forecasts.Clear();

            if (forecasts.Count > 0)
            {
                var today = forecasts[0];
                CurrentDateLabel = Loc.T("weather.today");
                CurrentDateSubtitle = today.DayName;
                CurrentTemp = $"{today.TempMax:0}°";
                CurrentTempMin = $"{today.TempMin:0}°";
                CurrentTempMaxValue = today.TempMax;
                CurrentTempMinValue = today.TempMin;
                CurrentConditionIcon = today.ConditionIcon;
                CurrentConditionLabel = today.ConditionLabel;
                CurrentWind = $"{today.WindSpeedMax:0} km/h";
                CurrentWindGusts = $"{today.WindGustsMax:0} km/h";
                CurrentWindArrowAngle = today.WindArrowAngle;
                HasCurrentPrecipitation = today.HasPrecipitation;
                CurrentPrecipitation = today.PrecipitationText;
                HasData = true;

                // Upcoming days only (today is shown in the header)
                for (int i = 1; i < forecasts.Count; i++)
                    Forecasts.Add(forecasts[i]);
            }
            else
            {
                HasData = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown
        }
        catch
        {
            ErrorMessage = Loc.T("weather.load_error");
            HasData = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
