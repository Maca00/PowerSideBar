using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PowerSideBar.Models;
using PowerSideBar.Services;
using PowerSideBar.ViewModels;

namespace PowerSideBar.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] BarWidthChoices = { 40, 50, 60 };

    private readonly SidebarViewModel _viewModel;
    private readonly AppConfig _config;
    private readonly WeatherService _weatherService = new();
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _geoSearchTimer;
    private CancellationTokenSource? _geoSearchCts;
    private bool _suppressGeoSelection;
    private bool _suppressWeatherSearch;
    private string _weatherCityName = string.Empty;
    private double _weatherLatitude;
    private double _weatherLongitude;
    private string _weatherTimezone = string.Empty;

    public SettingsWindow(SidebarViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _config = viewModel.GetConfig();

        _geoSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _geoSearchTimer.Tick += async (_, _) =>
        {
            _geoSearchTimer.Stop();
            await RunGeocodeSearchAsync();
        };

        LoadValues();
        UpdateFeaturePanelsVisibility();
        ShowPage("general");
    }

    private void LoadValues()
    {
        TxtAppVersion.Text = Loc.Tf("update.version_label", _updateService.CurrentVersion);
        SelectLanguage(_config.Language);

        ChkAutoStart.IsChecked = _viewModel.AutoStart;
        ChkAdBlock.IsChecked = _viewModel.AdBlockEnabled;

        SelectBarWidth(SnapBarWidth(_config.IconStripWidth));
        SelectSidebarSide(_config.SidebarSide);

        ChkEnableWeather.IsChecked = _config.EnableWeather;
        ChkEnableHue.IsChecked = _config.EnableHue;
        ChkEnableShutters.IsChecked = _config.EnableShutters;
        ChkEnableSpotify.IsChecked = _config.EnableSpotify;

        TxtWeatherSearch.Text = string.Empty;
        TxtWeatherSearchStatus.Text = string.Empty;
        LstWeatherResults.ItemsSource = null;
        LstWeatherResults.Visibility = Visibility.Collapsed;
        _weatherCityName = _config.WeatherCityName;
        _weatherLatitude = _config.WeatherLatitude;
        _weatherLongitude = _config.WeatherLongitude;
        _weatherTimezone = _config.WeatherTimezone;
        UpdateWeatherCityLabel();

        TxtHueIp.Text = _config.HueBridgeIp;
        TxtHueKey.Text = _config.HueApiKey;

        TxtShutterHost.Text = _config.ShutterHost;
        TxtShutterPort.Text = _config.ShutterPort > 0
            ? _config.ShutterPort.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        TxtShutterUser.Text = _config.ShutterUser;
        TxtShutterPassword.Password = _config.ShutterPassword;
        TxtShutterHostId.Text = _config.ShutterHostId;

        TxtSpotifyClientId.Text = _config.SpotifyClientId;
        TxtSpotifyClientSecret.Password = _config.SpotifyClientSecret;
    }

    private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdates.IsEnabled = false;
        try
        {
            if (Application.Current is App app)
                await app.CheckForUpdatesInteractiveAsync();
        }
        finally
        {
            BtnCheckUpdates.IsEnabled = true;
            TxtAppVersion.Text = Loc.Tf("update.version_label", _updateService.CurrentVersion);
        }
    }

    private void BtnKoFi_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://ko-fi.com/A8V826XP84",
            UseShellExecute = true
        });
    }

    private void SelectLanguage(string? language)
    {
        var lang = Loc.Normalize(language);
        foreach (ComboBoxItem item in CmbLanguage.Items)
        {
            if (string.Equals(item.Tag as string, lang, StringComparison.OrdinalIgnoreCase))
            {
                CmbLanguage.SelectedItem = item;
                return;
            }
        }
        CmbLanguage.SelectedIndex = 0;
    }

    private string GetSelectedLanguage()
    {
        if (CmbLanguage.SelectedItem is ComboBoxItem { Tag: string tag })
            return Loc.Normalize(tag);
        return Loc.English;
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
            ShowPage(page);
    }

    private void ShowPage(string page)
    {
        PageGeneral.Visibility = page == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageWeather.Visibility = page == "weather" ? Visibility.Visible : Visibility.Collapsed;
        PageHue.Visibility = page == "hue" ? Visibility.Visible : Visibility.Collapsed;
        PageShutters.Visibility = page == "shutters" ? Visibility.Visible : Visibility.Collapsed;
        PageSpotify.Visibility = page == "spotify" ? Visibility.Visible : Visibility.Collapsed;

        SetNavActive(NavGeneral, page == "general");
        SetNavActive(NavWeather, page == "weather");
        SetNavActive(NavHue, page == "hue");
        SetNavActive(NavShutters, page == "shutters");
        SetNavActive(NavSpotify, page == "spotify");
    }

    private void SetNavActive(Button button, bool active)
    {
        button.Style = (Style)FindResource(active ? "NavButtonActiveStyle" : "NavButtonStyle");
    }

    private void FeatureToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFeaturePanelsVisibility();
    }

    private void UpdateFeaturePanelsVisibility()
    {
        PanelWeatherFields.Visibility = ChkEnableWeather.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        PanelHueFields.Visibility = ChkEnableHue.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        PanelShutterFields.Visibility = ChkEnableShutters.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        PanelSpotifyFields.Visibility = ChkEnableSpotify.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int SnapBarWidth(int width)
    {
        var best = BarWidthChoices[1];
        var bestDist = Math.Abs(width - best);
        foreach (var choice in BarWidthChoices)
        {
            var dist = Math.Abs(width - choice);
            if (dist < bestDist)
            {
                best = choice;
                bestDist = dist;
            }
        }
        return best;
    }

    private void SelectSidebarSide(string side)
    {
        var left = string.Equals(side, "left", StringComparison.OrdinalIgnoreCase);
        RbSideLeft.IsChecked = left;
        RbSideRight.IsChecked = !left;
    }

    private string GetSelectedSidebarSide() =>
        RbSideLeft.IsChecked == true ? "left" : "right";

    private void SelectBarWidth(int width)
    {
        RbBarNarrow.IsChecked = width == 40;
        RbBarNormal.IsChecked = width == 50;
        RbBarWide.IsChecked = width == 60;
        if (RbBarNarrow.IsChecked != true && RbBarNormal.IsChecked != true && RbBarWide.IsChecked != true)
            RbBarNormal.IsChecked = true;
    }

    private int GetSelectedBarWidth()
    {
        if (RbBarNarrow.IsChecked == true) return 40;
        if (RbBarWide.IsChecked == true) return 60;
        return 50;
    }

    private void UpdateWeatherCityLabel()
    {
        var city = string.IsNullOrWhiteSpace(_weatherCityName) ? "—" : _weatherCityName;
        TxtWeatherCityLabel.Text = Loc.Tf("settings.weather.city", city);
    }

    private void TxtWeatherSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressWeatherSearch)
            return;

        _geoSearchTimer.Stop();
        var query = TxtWeatherSearch.Text.Trim();
        if (query.Length < 2)
        {
            _geoSearchCts?.Cancel();
            LstWeatherResults.ItemsSource = null;
            LstWeatherResults.Visibility = Visibility.Collapsed;
            TxtWeatherSearchStatus.Text = query.Length == 0
                ? string.Empty
                : Loc.T("settings.weather.min_chars");
            return;
        }

        TxtWeatherSearchStatus.Text = Loc.T("settings.weather.searching");
        _geoSearchTimer.Start();
    }

    private async Task RunGeocodeSearchAsync()
    {
        var query = TxtWeatherSearch.Text.Trim();
        if (query.Length < 2)
            return;

        _geoSearchCts?.Cancel();
        _geoSearchCts = new CancellationTokenSource();
        var token = _geoSearchCts.Token;

        try
        {
            var results = await _weatherService.SearchLocationsAsync(query, token);
            if (token.IsCancellationRequested)
                return;

            _suppressGeoSelection = true;
            LstWeatherResults.ItemsSource = results;
            LstWeatherResults.SelectedItem = null;
            _suppressGeoSelection = false;

            if (results.Count == 0)
            {
                LstWeatherResults.Visibility = Visibility.Collapsed;
                TxtWeatherSearchStatus.Text = Loc.T("settings.weather.no_results");
            }
            else
            {
                LstWeatherResults.Visibility = Visibility.Visible;
                TxtWeatherSearchStatus.Text = Loc.Tf("settings.weather.results_count", results.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer search
        }
        catch (Exception)
        {
            if (token.IsCancellationRequested)
                return;
            LstWeatherResults.ItemsSource = null;
            LstWeatherResults.Visibility = Visibility.Collapsed;
            TxtWeatherSearchStatus.Text = Loc.T("settings.weather.search_error");
        }
    }

    private void LstWeatherResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGeoSelection)
            return;
        if (LstWeatherResults.SelectedItem is not GeoLocation loc)
            return;

        _weatherCityName = loc.Name;
        _weatherLatitude = loc.Latitude;
        _weatherLongitude = loc.Longitude;
        _weatherTimezone = loc.Timezone;
        UpdateWeatherCityLabel();

        _geoSearchCts?.Cancel();
        _geoSearchTimer.Stop();
        _suppressWeatherSearch = true;
        TxtWeatherSearch.Text = string.Empty;
        _suppressWeatherSearch = false;
        TxtWeatherSearchStatus.Text = string.Empty;
        LstWeatherResults.ItemsSource = null;
        LstWeatherResults.Visibility = Visibility.Collapsed;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _geoSearchCts?.Cancel();
        Close();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var enableWeather = ChkEnableWeather.IsChecked == true;
        var enableHue = ChkEnableHue.IsChecked == true;
        var enableShutters = ChkEnableShutters.IsChecked == true;
        var enableSpotify = ChkEnableSpotify.IsChecked == true;

        if (enableWeather)
        {
            if (string.IsNullOrWhiteSpace(_weatherCityName)
                || _weatherLatitude < -90 || _weatherLatitude > 90
                || _weatherLongitude < -180 || _weatherLongitude > 180
                || string.IsNullOrWhiteSpace(_weatherTimezone))
            {
                ShowPage("weather");
                MessageBox.Show(
                    Loc.T("settings.weather.need_city"),
                    Loc.T("common.validation"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtWeatherSearch.Focus();
                return;
            }
        }

        var shutterPort = _config.ShutterPort;
        if (enableShutters)
        {
            var shutterPortText = TxtShutterPort.Text.Trim();
            shutterPort = 0;
            if (!string.IsNullOrEmpty(shutterPortText))
            {
                if (!int.TryParse(shutterPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out shutterPort)
                    || shutterPort < 1 || shutterPort > 65535)
                {
                    ShowPage("shutters");
                    MessageBox.Show(Loc.T("settings.shutters.port_invalid"), Loc.T("common.validation"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtShutterPort.Focus();
                    return;
                }
            }
        }

        var selectedLanguage = GetSelectedLanguage();
        var languageChanged = selectedLanguage != Loc.Normalize(_config.Language);

        _viewModel.AutoStart = ChkAutoStart.IsChecked == true;
        _viewModel.AdBlockEnabled = ChkAdBlock.IsChecked == true;

        _config.IconStripWidth = GetSelectedBarWidth();
        _config.SidebarSide = GetSelectedSidebarSide();
        _config.Language = selectedLanguage;

        _config.EnableWeather = enableWeather;
        _config.EnableHue = enableHue;
        _config.EnableShutters = enableShutters;
        _config.EnableSpotify = enableSpotify;
        _config.FeaturesInitialized = true;

        if (enableWeather)
        {
            _config.WeatherCityName = _weatherCityName.Trim();
            _config.WeatherLatitude = _weatherLatitude;
            _config.WeatherLongitude = _weatherLongitude;
            _config.WeatherTimezone = _weatherTimezone.Trim();
        }

        if (enableHue)
        {
            _config.HueBridgeIp = TxtHueIp.Text.Trim();
            _config.HueApiKey = TxtHueKey.Text.Trim();
        }

        if (enableShutters)
        {
            _config.ShutterHost = TxtShutterHost.Text.Trim();
            _config.ShutterPort = shutterPort;
            _config.ShutterUser = TxtShutterUser.Text.Trim();
            _config.ShutterPassword = TxtShutterPassword.Password;
            _config.ShutterHostId = TxtShutterHostId.Text.Trim();
        }

        if (enableSpotify)
        {
            _config.SpotifyClientId = TxtSpotifyClientId.Text.Trim();
            _config.SpotifyClientSecret = TxtSpotifyClientSecret.Password;
        }

        _viewModel.ApplyBuiltInPanelSettings();
        _viewModel.SaveConfig();
        _viewModel.NotifySettingsApplied();
        _geoSearchCts?.Cancel();

        if (languageChanged)
        {
            MessageBox.Show(
                Loc.T("settings.language_restart"),
                Loc.T("settings.language"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Close();
            if (Application.Current is App app)
                app.RestartForLanguageChange();
            return;
        }

        Close();
    }
}
