using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSideBar.Models;
using PowerSideBar.Services;

namespace PowerSideBar.ViewModels;

// ── Pairing state machine ─────────────────────────────────────────────────────

public enum HuePairingStep
{
    Idle,
    Discovering,
    BridgeFound,
    WaitingButton,
    Connecting,
    Error,
}

// ── Scene ViewModel ───────────────────────────────────────────────────────────

public partial class HueSceneViewModel : ObservableObject
{
    private readonly HueService _service;
    private readonly AppConfig _config;

    public string Id { get; }
    public string Name { get; }
    public Brush CardBrush { get; }

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x1A, 0x4A, 0x7A),
        Color.FromRgb(0x7A, 0x3A, 0x00),
        Color.FromRgb(0x25, 0x5A, 0x10),
        Color.FromRgb(0x5A, 0x10, 0x5A),
        Color.FromRgb(0x6A, 0x15, 0x15),
        Color.FromRgb(0x00, 0x4A, 0x50),
        Color.FromRgb(0x3A, 0x30, 0x05),
        Color.FromRgb(0x1A, 0x1A, 0x5A),
    ];

    public HueSceneViewModel(HueService service, AppConfig config, HueScene scene, int index)
    {
        _service = service;
        _config = config;
        Id = scene.Id;
        Name = scene.Metadata.Name;

        var c = Palette[index % Palette.Length];
        var lighter = Color.FromRgb(
            (byte)Math.Min(255, c.R + 40),
            (byte)Math.Min(255, c.G + 40),
            (byte)Math.Min(255, c.B + 40));
        CardBrush = new LinearGradientBrush(c, lighter, 45);
        CardBrush.Freeze();
    }

    [RelayCommand]
    private async Task ActivateAsync(CancellationToken ct = default)
    {
        try { await _service.ActivateSceneAsync(_config.HueBridgeIp, _config.HueApiKey, Id, ct); }
        catch { /* ignore */ }
    }
}

// ── Per-light ViewModel ───────────────────────────────────────────────────────

public partial class HueLightViewModel : ObservableObject
{
    private readonly HueService _service;
    private readonly AppConfig _config;
    private readonly Action? _onStateChanged;
    private CancellationTokenSource? _debounceCts;

    public string Id { get; }
    public string Name { get; }
    public bool SupportsBrightness { get; }
    public bool SupportsColorTemp { get; }
    public bool SupportsColor { get; }
    public int ColorTempMin { get; }
    public int ColorTempMax { get; }

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private double _brightness = 100;

    [ObservableProperty]
    private int _colorTempMirek = 370;

    [ObservableProperty]
    private Color _currentColor = Colors.White;

    private double? _xyX;
    private double? _xyY;

    // Card background for room detail grid
    public Brush CardBackground
    {
        get
        {
            if (!IsOn) return _offBrush;
            if (SupportsColor)
                return new SolidColorBrush(ForUiGlow(CurrentColor, Brightness / 100.0));
            // Warm amber for white lights
            double t = Brightness / 100.0;
            return new SolidColorBrush(Color.FromRgb(
                (byte)(0x4A + t * 0x25),
                (byte)(0x2A * t),
                (byte)(0x05 * t)));
        }
    }

    private static readonly SolidColorBrush _offBrush =
        new(Color.FromRgb(0x28, 0x28, 0x28));

    static HueLightViewModel() => _offBrush.Freeze();

    public HueLightViewModel(HueService service, AppConfig config,
        HueLight light, Action? onStateChanged = null)
    {
        _service = service;
        _config = config;
        _onStateChanged = onStateChanged;

        Id = light.Id;
        Name = light.Metadata.Name;
        _isOn = light.On?.On ?? false;
        _brightness = light.Dimming?.Brightness ?? 100;

        SupportsBrightness = light.Dimming != null;
        SupportsColorTemp = light.ColorTemperature != null;
        SupportsColor = light.Color != null;

        ColorTempMin = light.ColorTemperature?.MirekSchema?.MirekMinimum ?? 153;
        ColorTempMax = light.ColorTemperature?.MirekSchema?.MirekMaximum ?? 500;
        _colorTempMirek = light.ColorTemperature?.Mirek ?? 370;

        if (light.Color?.Xy != null)
        {
            _xyX = light.Color.Xy.X;
            _xyY = light.Color.Xy.Y;
            // Chromaticity at full brightness; UI dimming is applied separately
            _currentColor = XyToColor(_xyX.Value, _xyY.Value, 1.0);
        }
    }

    partial void OnIsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(CardBackground));
        _onStateChanged?.Invoke();
    }

    partial void OnCurrentColorChanged(Color value)
    {
        OnPropertyChanged(nameof(CardBackground));
        _onStateChanged?.Invoke();
    }

    partial void OnBrightnessChanged(double value)
    {
        OnPropertyChanged(nameof(CardBackground));
        _onStateChanged?.Invoke();
        _ = SendDebouncedAsync(() => _service.SetBrightnessAsync(
            _config.HueBridgeIp, _config.HueApiKey, Id, value));
    }

    /// <summary>Updates brightness locally (e.g. room master slider) without a per-light API call.</summary>
    public void ApplyLocalBrightness(double value)
    {
        if (Math.Abs(_brightness - value) < 0.05) return;
        _brightness = value;
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(CardBackground));
        _onStateChanged?.Invoke();
    }

    partial void OnColorTempMirekChanged(int value) =>
        _ = SendDebouncedAsync(() => _service.SetColorTempAsync(
            _config.HueBridgeIp, _config.HueApiKey, Id, value));

    [RelayCommand]
    private async Task ToggleAsync(CancellationToken ct = default)
    {
        IsOn = !IsOn;
        try { await _service.SetOnOffAsync(_config.HueBridgeIp, _config.HueApiKey, Id, IsOn, ct); }
        catch { IsOn = !IsOn; }
    }

    /// <summary>Called from code-behind when the user clicks the color gradient.</summary>
    public async Task SetColorFromHueAsync(double hueNormalized, CancellationToken ct = default)
    {
        var (x, y) = HsvToXy(hueNormalized * 360.0, 1.0, 1.0);
        _xyX = x;
        _xyY = y;
        CurrentColor = XyToColor(x, y, 1.0);
        try { await _service.SetColorXyAsync(_config.HueBridgeIp, _config.HueApiKey, Id, x, y, ct); }
        catch { }
    }

    /// <summary>
    /// Scales chromaticity for dark-card readability while keeping hue vivid (Hue-app style).
    /// </summary>
    internal static Color ForUiGlow(Color c, double brightness01)
    {
        // Keep gradients saturated like the official Hue app; brightness still modulates a bit
        double factor = 0.72 + 0.28 * Math.Clamp(brightness01, 0.0, 1.0);
        return Color.FromRgb(
            (byte)(c.R * factor),
            (byte)(c.G * factor),
            (byte)(c.B * factor));
    }

    private async Task SendDebouncedAsync(Func<Task> action, int delayMs = 300)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested) await action();
        }
        catch (OperationCanceledException) { }
    }

    // ── Color math ────────────────────────────────────────────────────────────

    public static (double x, double y) HsvToXy(double hueDeg, double sat, double val)
    {
        var (r, g, b) = HsvToRgbLinear(hueDeg, sat, val);
        r = GammaExpand(r); g = GammaExpand(g); b = GammaExpand(b);
        double X = r * 0.664511 + g * 0.154324 + b * 0.162028;
        double Y = r * 0.283881 + g * 0.668433 + b * 0.047685;
        double Z = r * 0.000088 + g * 0.072310 + b * 0.986039;
        double sum = X + Y + Z;
        return sum < 0.0001 ? (0.3127, 0.3290) : (X / sum, Y / sum);
    }

    private static (double r, double g, double b) HsvToRgbLinear(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        int i = (int)(h / 60);
        double f = h / 60 - i, p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    private static double GammaExpand(double c) =>
        c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>
    /// Philips Hue official xy + brightness → sRGB (Wide RGB D65).
    /// https://developers.meethue.com/develop/application-design-guidance/color-conversion-formulas-rgb-to-xy-and-back/
    /// </summary>
    public static Color XyToColor(double x, double y, double brightness = 1.0)
    {
        if (y <= 1e-6) return Colors.Black;
        brightness = Math.Clamp(brightness, 0.0, 1.0);

        double z = 1.0 - x - y;
        double Y = brightness;
        double X = (Y / y) * x;
        double Z = (Y / y) * z;

        // Wide RGB D65
        double r = X * 1.656492 - Y * 0.354851 - Z * 0.255038;
        double g = -X * 0.707196 + Y * 1.655397 + Z * 0.036152;
        double b = X * 0.051713 - Y * 0.121364 + Z * 1.011530;

        r = ReverseGamma(Math.Max(0, r));
        g = ReverseGamma(Math.Max(0, g));
        b = ReverseGamma(Math.Max(0, b));

        double max = Math.Max(r, Math.Max(g, b));
        if (max > 1.0)
        {
            r /= max;
            g /= max;
            b /= max;
        }

        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
    }

    private static double ReverseGamma(double c) =>
        c <= 0.0031308 ? 12.92 * c : (1.0 + 0.055) * Math.Pow(c, 1.0 / 2.4) - 0.055;
}

// ── Per-room ViewModel ────────────────────────────────────────────────────────

public partial class HueRoomViewModel : ObservableObject
{
    private readonly HueService _service;
    private readonly AppConfig _config;
    private readonly string _archetype;
    private CancellationTokenSource? _debounceCts;

    public string Id { get; }
    public string Name { get; }
    public string? GroupedLightId { get; }

    public ObservableCollection<HueLightViewModel> Lights { get; } = new();
    public ObservableCollection<HueSceneViewModel> Scenes { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private double _masterBrightness = 80;

    public bool IsAnyLightOn => Lights.Any(l => l.IsOn);

    public string SubtitleText
    {
        get
        {
            int on = Lights.Count(l => l.IsOn);
            return on == 0 ? "Toutes les lumières sont éteintes"
                 : on == 1 ? "1 lumière allumée"
                 : $"{on} lumières allumées";
        }
    }

    public string RoomIconPath => HueRoomIcons.ForArchetype(_archetype);

    // Card gradient from colors of lights that are on (rainbow hue order)
    public Brush RoomCardBackground
    {
        get
        {
            var colors = Lights
                .Where(l => l.IsOn)
                .Select(GlowColorFor)
                .OrderBy(HueAngle)
                .ToList();
            if (colors.Count == 0) return _roomOffBrush;

            if (colors.Count == 1)
                return new SolidColorBrush(colors[0]);

            var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i < colors.Count; i++)
                grad.GradientStops.Add(new GradientStop(colors[i], (double)i / (colors.Count - 1)));
            return grad;
        }
    }

    /// <summary>HSV hue in degrees (0–360) for rainbow ordering; neutrals map near warm amber.</summary>
    private static double HueAngle(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        if (delta < 1e-6) return 40; // white / gray → amber slot

        double hue;
        if (Math.Abs(max - r) < 1e-9) hue = 60 * (((g - b) / delta) % 6);
        else if (Math.Abs(max - g) < 1e-9) hue = 60 * (((b - r) / delta) + 2);
        else hue = 60 * (((r - g) / delta) + 4);

        if (hue < 0) hue += 360;
        return hue;
    }

    private static Color GlowColorFor(HueLightViewModel light)
    {
        if (light.SupportsColor)
            return HueLightViewModel.ForUiGlow(light.CurrentColor, light.Brightness / 100.0);
        // Warm gold for white / CT lights (matches Hue "ambiance" cards)
        double t = Math.Clamp(light.Brightness / 100.0, 0.0, 1.0);
        return Color.FromRgb(
            (byte)(0x8A + t * 0x55),
            (byte)(0x5A + t * 0x50),
            (byte)(0x18 + t * 0x20));
    }

    private static readonly SolidColorBrush _roomOffBrush =
        new(Color.FromRgb(0x2C, 0x2C, 0x2E));

    static HueRoomViewModel() => _roomOffBrush.Freeze();

    public HueRoomViewModel(HueService service, AppConfig config,
        HueRoom room, System.Collections.Generic.List<HueLight> lights, string? groupedLightId)
    {
        _service = service;
        _config = config;
        Id = room.Id;
        Name = room.Metadata.Name;
        _archetype = room.Metadata.Archetype;
        GroupedLightId = groupedLightId;

        foreach (var l in lights)
            Lights.Add(new HueLightViewModel(service, config, l, RefreshComputedProperties));

        if (Lights.Count > 0)
            _masterBrightness = Lights.Average(l => l.Brightness);
    }

    private void RefreshComputedProperties()
    {
        OnPropertyChanged(nameof(IsAnyLightOn));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(RoomCardBackground));
    }

    partial void OnMasterBrightnessChanged(double value)
    {
        foreach (var l in Lights)
            l.ApplyLocalBrightness(value);
        _ = SendDebouncedBrightnessAsync(value);
    }

    private async Task SendDebouncedBrightnessAsync(double value, int delayMs = 300)
    {
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;
        try
        {
            await Task.Delay(delayMs, token);
            if (!token.IsCancellationRequested && !string.IsNullOrEmpty(GroupedLightId))
                await _service.SetGroupedLightBrightnessAsync(
                    _config.HueBridgeIp, _config.HueApiKey, GroupedLightId, value);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    [RelayCommand]
    private async Task ToggleRoomAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(GroupedLightId)) return;
        bool target = !IsAnyLightOn;
        try
        {
            await _service.ToggleGroupedLightAsync(
                _config.HueBridgeIp, _config.HueApiKey, GroupedLightId, target, ct);
            foreach (var l in Lights) l.IsOn = target;
            RefreshComputedProperties();
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    public async Task LoadScenesAsync(CancellationToken ct = default)
    {
        try
        {
            var scenes = await _service.GetScenesForRoomAsync(
                _config.HueBridgeIp, _config.HueApiKey, Id, ct);
            Scenes.Clear();
            for (int i = 0; i < scenes.Count; i++)
                Scenes.Add(new HueSceneViewModel(_service, _config, scenes[i], i));
        }
        catch { }
    }
}

// ── Main HueViewModel ─────────────────────────────────────────────────────────

public partial class HueViewModel : ObservableObject
{
    private readonly HueService _service;
    private readonly AppConfig _config;

    [ObservableProperty]
    private bool _isConfigured;

    [ObservableProperty]
    private bool _isPairing;

    [ObservableProperty]
    private HuePairingStep _pairingStep = HuePairingStep.Idle;

    [ObservableProperty]
    private string _bridgeFoundIp = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private HueRoomViewModel? _selectedRoom;

    public bool IsRoomsListVisible => !IsPairing && SelectedRoom == null;
    public bool IsRoomDetailVisible => !IsPairing && SelectedRoom != null;

    public ObservableCollection<HueRoomViewModel> Rooms { get; } = new();

    public HueViewModel(HueService service, AppConfig config)
    {
        _service = service;
        _config = config;
        _isConfigured = !string.IsNullOrEmpty(config.HueBridgeIp)
                     && !string.IsNullOrEmpty(config.HueApiKey);
        _isPairing = !_isConfigured;
    }

    /// <summary>Re-reads bridge credentials from config after settings save.</summary>
    public void ApplyConfiguration()
    {
        var configured = !string.IsNullOrEmpty(_config.HueBridgeIp)
                      && !string.IsNullOrEmpty(_config.HueApiKey);

        IsConfigured = configured;
        if (configured)
        {
            IsPairing = false;
            PairingStep = HuePairingStep.Idle;
            ErrorMessage = null;
            _ = RefreshAsync();
        }
        else
        {
            IsPairing = true;
            PairingStep = HuePairingStep.Idle;
        }
    }

    partial void OnSelectedRoomChanged(HueRoomViewModel? value)
    {
        OnPropertyChanged(nameof(IsRoomsListVisible));
        OnPropertyChanged(nameof(IsRoomDetailVisible));
    }

    partial void OnIsPairingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRoomsListVisible));
        OnPropertyChanged(nameof(IsRoomDetailVisible));
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectRoomAsync(HueRoomViewModel room, CancellationToken ct = default)
    {
        SelectedRoom = room;
        if (room.Scenes.Count == 0)
            await room.LoadScenesAsync(ct);
    }

    [RelayCommand]
    private void NavigateBack()
    {
        SelectedRoom = null;
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!IsConfigured || IsLoading) return;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var rooms = await _service.GetRoomsWithLightsAsync(
                _config.HueBridgeIp, _config.HueApiKey, ct);

            Rooms.Clear();
            SelectedRoom = null;
            foreach (var (room, lights, groupedLightId) in rooms)
                Rooms.Add(new HueRoomViewModel(_service, _config, room, lights, groupedLightId));

            if (Rooms.Count == 0)
                ErrorMessage = Loc.T("hue.no_rooms");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorMessage = Loc.Tf("hue.bridge_error", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Pairing flow ──────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task DiscoverBridgeAsync(CancellationToken ct = default)
    {
        PairingStep = HuePairingStep.Discovering;
        ErrorMessage = null;
        var ip = await _service.DiscoverBridgeAsync(ct);
        if (string.IsNullOrEmpty(ip))
        {
            PairingStep = HuePairingStep.Error;
            ErrorMessage = Loc.T("hue.no_bridge");
            return;
        }
        BridgeFoundIp = ip;
        PairingStep = HuePairingStep.BridgeFound;
    }

    [RelayCommand]
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        PairingStep = HuePairingStep.Connecting;
        ErrorMessage = null;
        var (apiKey, error) = await _service.PairAsync(BridgeFoundIp, ct);
        if (apiKey == null)
        {
            PairingStep = HuePairingStep.Error;
            ErrorMessage = error ?? Loc.T("hue.pair_error");
            return;
        }
        _config.HueBridgeIp = BridgeFoundIp;
        _config.HueApiKey = apiKey;
        ConfigService.Save(_config);
        IsConfigured = true;
        IsPairing = false;
        PairingStep = HuePairingStep.Idle;
        await RefreshAsync(ct);
    }

    [RelayCommand]
    private void StartPairing()
    {
        IsPairing = true;
        PairingStep = HuePairingStep.Idle;
        ErrorMessage = null;
        BridgeFoundIp = string.Empty;
    }

    [RelayCommand]
    private void RetryPairing()
    {
        PairingStep = HuePairingStep.Idle;
        ErrorMessage = null;
    }
}
