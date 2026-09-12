using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using PowerSideBar.Services;
using PowerSideBar.Views;
using Velopack;

namespace PowerSideBar;

public partial class App : Application
{
    private static readonly TimeSpan UpdateSnoozeDuration = TimeSpan.FromDays(7);

    private static Mutex? _singleInstanceMutex;
    private bool _mutexOwned;
    private TaskbarIcon? _trayIcon;
    private SidebarWindow? _sidebarWindow;
    private readonly UpdateService _updateService = new();
    private bool _updateCheckRunning;
    private bool _restartOnExit;
    private string? _restartExePath;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance check
        _singleInstanceMutex = new Mutex(true, "PowerSideBar_SingleInstance", out bool isNewInstance);
        _mutexOwned = isNewInstance;
        if (!isNewInstance)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            MessageBox.Show(Loc.T("app.already_running"), "PowerSideBar",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Create main window
        _sidebarWindow = new SidebarWindow();
        _sidebarWindow.Show();

        // Setup tray icon programmatically
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "PowerSideBar",
        };

        // Try to load icon
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/powersidebar.ico");
            using var stream = GetResourceStream(iconUri)?.Stream;
            if (stream != null)
            {
                _trayIcon.Icon = new Icon(stream);
            }
        }
        catch
        {
            // Default icon if not found
            _trayIcon.Icon = SystemIcons.Application;
        }

        // Double-click tray → toggle visibility
        _trayIcon.TrayMouseDoubleClick += (_, _) => _sidebarWindow?.ToggleVisibility();

        // Context menu
        var contextMenu = new System.Windows.Controls.ContextMenu();

        var showHideItem = new System.Windows.Controls.MenuItem { Header = Loc.T("tray.show_hide") };
        showHideItem.Click += (_, _) => _sidebarWindow?.ToggleVisibility();
        contextMenu.Items.Add(showHideItem);

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var autoStartItem = new System.Windows.Controls.MenuItem
        {
            Header = Loc.T("tray.auto_start"),
            IsCheckable = true,
            IsChecked = _sidebarWindow.ViewModel.AutoStart,
        };
        autoStartItem.Click += (_, _) =>
        {
            if (_sidebarWindow?.ViewModel != null)
            {
                _sidebarWindow.ViewModel.AutoStart = autoStartItem.IsChecked;
            }
        };
        contextMenu.Items.Add(autoStartItem);

        var adBlockItem = new System.Windows.Controls.MenuItem
        {
            Header = Loc.T("tray.ad_block"),
            IsCheckable = true,
            IsChecked = _sidebarWindow.ViewModel.AdBlockEnabled,
        };
        adBlockItem.Click += (_, _) =>
        {
            if (_sidebarWindow?.ViewModel != null)
            {
                _sidebarWindow.ViewModel.AdBlockEnabled = adBlockItem.IsChecked;
            }
        };
        contextMenu.Items.Add(adBlockItem);

        contextMenu.Opened += (_, _) =>
        {
            if (_sidebarWindow?.ViewModel == null) return;
            autoStartItem.IsChecked = _sidebarWindow.ViewModel.AutoStart;
            adBlockItem.IsChecked = _sidebarWindow.ViewModel.AdBlockEnabled;
        };

        contextMenu.Items.Add(new System.Windows.Controls.Separator());

        var settingsItem = new System.Windows.Controls.MenuItem { Header = Loc.T("tray.settings") };
        settingsItem.Click += (_, _) => OpenSettings();
        contextMenu.Items.Add(settingsItem);

        var updateItem = new System.Windows.Controls.MenuItem { Header = Loc.T("tray.check_updates") };
        updateItem.Click += async (_, _) => await CheckForUpdatesInteractiveAsync();
        contextMenu.Items.Add(updateItem);

        var quitItem = new System.Windows.Controls.MenuItem { Header = Loc.T("tray.quit") };
        quitItem.Click += (_, _) =>
        {
            _sidebarWindow?.ForceClose();
            Shutdown();
        };
        contextMenu.Items.Add(quitItem);

        _trayIcon.ContextMenu = contextMenu;

        _ = CheckForUpdatesSilentAsync();
    }

    private SettingsWindow? _settingsWindow;

    public void OpenSettings()
    {
        if (_sidebarWindow?.ViewModel == null) return;

        if (_settingsWindow != null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_sidebarWindow.ViewModel)
        {
            Owner = _sidebarWindow.IsVisible ? _sidebarWindow : null,
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>
    /// Schedules a process restart after exit so the single-instance mutex is fully released first.
    /// </summary>
    public void RestartForLanguageChange()
    {
        _restartExePath = AutoStartService.GetLaunchExecutablePath();
        _restartOnExit = !string.IsNullOrEmpty(_restartExePath);
        Shutdown();
    }

    public Task CheckForUpdatesInteractiveAsync() =>
        CheckForUpdatesAsync(interactive: true);

    private async Task CheckForUpdatesSilentAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        await CheckForUpdatesAsync(interactive: false);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updateCheckRunning)
            return;

        _updateCheckRunning = true;
        try
        {
            if (!_updateService.IsInstalled)
            {
                if (interactive)
                {
                    MessageBox.Show(
                        Loc.T("update.dev_only"),
                        Loc.T("update.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }

            UpdateInfo? update;
            try
            {
                update = await _updateService.CheckForUpdatesAsync();
            }
            catch (Exception ex)
            {
                if (interactive)
                {
                    MessageBox.Show(
                        Loc.Tf("update.check_failed", ex.Message),
                        Loc.T("update.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            if (update == null)
            {
                if (interactive)
                {
                    MessageBox.Show(
                        Loc.Tf("update.up_to_date", _updateService.CurrentVersion),
                        Loc.T("update.title"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }

            var availableVersion = update.TargetFullRelease.Version.ToString();
            if (!interactive && IsUpdateSnoozed(availableVersion))
                return;

            var result = MessageBox.Show(
                Loc.Tf("update.available_body",
                    availableVersion,
                    _updateService.CurrentVersion),
                Loc.T("update.available_title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                SnoozeUpdate(availableVersion);
                return;
            }

            ClearUpdateSnooze();

            try
            {
                await _updateService.DownloadAndApplyAsync(update);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    Loc.Tf("update.apply_failed", ex.Message),
                    Loc.T("update.title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private bool IsUpdateSnoozed(string version)
    {
        var config = _sidebarWindow?.ViewModel.GetConfig();
        if (config == null) return false;
        if (!string.Equals(config.UpdateSnoozedVersion, version, StringComparison.OrdinalIgnoreCase))
            return false;
        return config.UpdateSnoozedUntilUtc > DateTime.UtcNow;
    }

    private void SnoozeUpdate(string version)
    {
        var vm = _sidebarWindow?.ViewModel;
        if (vm == null) return;
        var config = vm.GetConfig();
        config.UpdateSnoozedVersion = version;
        config.UpdateSnoozedUntilUtc = DateTime.UtcNow.Add(UpdateSnoozeDuration);
        vm.SaveConfig();
    }

    private void ClearUpdateSnooze()
    {
        var vm = _sidebarWindow?.ViewModel;
        if (vm == null) return;
        var config = vm.GetConfig();
        config.UpdateSnoozedVersion = string.Empty;
        config.UpdateSnoozedUntilUtc = DateTime.MinValue;
        vm.SaveConfig();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sidebarWindow?.CleanupResources();
        _sidebarWindow?.ViewModel.SaveConfig();
        _trayIcon?.Dispose();

        if (_mutexOwned)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
            _mutexOwned = false;
        }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        // Start the new instance only after the mutex is gone.
        if (_restartOnExit && !string.IsNullOrEmpty(_restartExePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(_restartExePath)
                {
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Ignore relaunch failures.
            }
        }

        base.OnExit(e);
    }
}
