using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PowerSideBar.Models;
using PowerSideBar.Services;

namespace PowerSideBar.ViewModels;

// ── Per-device ViewModel ──────────────────────────────────────────────────────

public partial class ShutterDeviceViewModel : ObservableObject
{
    private readonly ShutterService _service;
    private readonly ShutterDevice _device;

    [ObservableProperty] private bool _isBusy;

    public string Name => _device.Name;
    public int Channel => _device.Channel;

    public ShutterDeviceViewModel(ShutterService service, ShutterDevice device)
    {
        _service = service;
        _device = device;
    }

    [RelayCommand]
    private async Task UpAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try { await _service.SendCommandAsync(_device, ShutterCommand.Up, ct); }
        catch { /* ignore */ }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try { await _service.SendCommandAsync(_device, ShutterCommand.Stop, ct); }
        catch { /* ignore */ }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DownAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try { await _service.SendCommandAsync(_device, ShutterCommand.Down, ct); }
        catch { /* ignore */ }
        finally { IsBusy = false; }
    }

    /// <summary>Envoie un code brut (1-7) à ce volet uniquement. Pour tester quel code = Monter / Descendre / Stop.</summary>
    [RelayCommand]
    private async Task SendTestCodeAsync(object? codeParam, CancellationToken ct = default)
    {
        var code = codeParam switch
        {
            int i => i,
            string s when int.TryParse(s, out var n) => n,
            _ => 0
        };
        var zdcmd = (byte)(code & 0xFF);
        await _service.SendRawCommandAsync(_device, zdcmd, ct);
    }
}

// ── Main panel ViewModel ──────────────────────────────────────────────────────

public partial class ShutterViewModel : ObservableObject, IDisposable
{
    private readonly AppConfig _config;
    private readonly ShutterService _service = new();

    /// <summary>Canaux RDC dans l'ordre d'affichage.</summary>
    private static readonly int[] GroundFloorChannels = { 13, 15, 14, 11 };

    /// <summary>Canaux étage dans l'ordre d'affichage.</summary>
    private static readonly int[] UpperFloorChannels = { 1, 7, 2, 10 };

    [ObservableProperty] private ShutterConnectionState _state = ShutterConnectionState.Idle;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public ObservableCollection<ShutterDeviceViewModel> Devices { get; } = new();
    public ObservableCollection<ShutterDeviceViewModel> GroundFloorDevices { get; } = new();
    public ObservableCollection<ShutterDeviceViewModel> UpperFloorDevices { get; } = new();

    private readonly List<ShutterDevice> _loadedDevices = new();

    public ShutterViewModel(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// True when shutter connection parameters are set in config.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.ShutterHost) &&
        _config.ShutterPort > 0 &&
        !string.IsNullOrWhiteSpace(_config.ShutterUser) &&
        !string.IsNullOrWhiteSpace(_config.ShutterPassword) &&
        !string.IsNullOrWhiteSpace(_config.ShutterHostId);

    // ── Connect / refresh ─────────────────────────────────────────────────────

    [RelayCommand]
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        State = ShutterConnectionState.Connecting;
        ErrorMessage = string.Empty;
        ClearDeviceLists();

        var ok = await _service.ConnectAsync(
            _config.ShutterHost,
            _config.ShutterPort,
            _config.ShutterUser,
            _config.ShutterPassword,
            _config.ShutterHostId,
            ct);

        if (!ok)
        {
            State = ShutterConnectionState.Error;
            ErrorMessage = _service.LastError
                ?? Loc.Tf("shutters.connect_failed", _config.ShutterHost, _config.ShutterPort);
            return;
        }

        await LoadDevicesAsync(ct);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_service.IsConnected)
        {
            await ConnectAsync(ct);
            return;
        }

        await LoadDevicesAsync(ct);
    }

    /// <summary>Envoie la commande à tous les volets (scene).</summary>
    [RelayCommand]
    private async Task AllUpAsync(CancellationToken ct = default)
    {
        foreach (var d in _loadedDevices)
            await _service.SendCommandAsync(d, ShutterCommand.Up, ct);
    }

    [RelayCommand]
    private async Task AllStopAsync(CancellationToken ct = default)
    {
        foreach (var d in _loadedDevices)
            await _service.SendCommandAsync(d, ShutterCommand.Stop, ct);
    }

    [RelayCommand]
    private async Task AllDownAsync(CancellationToken ct = default)
    {
        foreach (var d in _loadedDevices)
            await _service.SendCommandAsync(d, ShutterCommand.Down, ct);
    }

    [RelayCommand]
    private async Task GroundFloorDownAsync(CancellationToken ct = default)
    {
        foreach (var d in _loadedDevices.Where(x => GroundFloorChannels.Contains(x.Channel)))
            await _service.SendCommandAsync(d, ShutterCommand.Down, ct);
    }

    [RelayCommand]
    private async Task UpperFloorDownAsync(CancellationToken ct = default)
    {
        foreach (var d in _loadedDevices.Where(x => UpperFloorChannels.Contains(x.Channel)))
            await _service.SendCommandAsync(d, ShutterCommand.Down, ct);
    }

    /// <summary>Envoie un code brut (0-7) à tous les volets. Pour tester quel code = Monter / Descendre / Stop.</summary>
    [RelayCommand]
    private async Task SendTestCodeAsync(object? codeParam, CancellationToken ct = default)
    {
        var code = codeParam switch
        {
            int i => i,
            string s when int.TryParse(s, out var n) => n,
            _ => 0
        };
        var zdcmd = (byte)(code & 0xFF);
        foreach (var d in _loadedDevices)
            await _service.SendRawCommandAsync(d, zdcmd, ct);
    }

    private void ClearDeviceLists()
    {
        Devices.Clear();
        GroundFloorDevices.Clear();
        UpperFloorDevices.Clear();
    }

    private async Task LoadDevicesAsync(CancellationToken ct)
    {
        var devices = await _service.GetDevicesAsync(ct);

        _loadedDevices.Clear();
        _loadedDevices.AddRange(devices);
        ClearDeviceLists();

        var vmsByChannel = new Dictionary<int, ShutterDeviceViewModel>();
        foreach (var d in devices.OrderBy(x => x.Channel))
        {
            var vm = new ShutterDeviceViewModel(_service, d);
            Devices.Add(vm);
            vmsByChannel[d.Channel] = vm;
        }

        foreach (var ch in GroundFloorChannels)
        {
            if (vmsByChannel.TryGetValue(ch, out var vm))
                GroundFloorDevices.Add(vm);
        }

        foreach (var ch in UpperFloorChannels)
        {
            if (vmsByChannel.TryGetValue(ch, out var vm))
                UpperFloorDevices.Add(vm);
        }

        State = Devices.Count > 0
            ? ShutterConnectionState.Connected
            : ShutterConnectionState.Error;

        if (State == ShutterConnectionState.Error)
        {
            var diag = _service.LastGetDevicesDiag;
            ErrorMessage = string.IsNullOrEmpty(diag)
                ? Loc.T("shutters.none_found")
                : Loc.Tf("shutters.none_found_diag", diag);
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _service.Dispose();
}
