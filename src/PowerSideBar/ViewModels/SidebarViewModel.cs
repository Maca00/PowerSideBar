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

public partial class SidebarViewModel : ObservableObject
{
    public const string NavBack = "back";
    public const string NavForward = "forward";
    public const string NavRefresh = "refresh";

    private readonly FaviconService _faviconService = new();
    private readonly AutoStartService _autoStartService = new();
    private AppConfig _config = null!;
    private bool _initializing;
    private CancellationTokenSource? _faviconCts;

    public ObservableCollection<ShortcutItem> Shortcuts { get; } = new();
    public ObservableCollection<ShortcutGroup> Groups { get; } = new();

    /// <summary>
    /// Mixed collection of ShortcutGroup headers + ShortcutItem entries for UI binding.
    /// </summary>
    public ObservableCollection<object> DisplayItems { get; } = new();

    [ObservableProperty]
    private ShortcutItem? _selectedShortcut;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _adBlockEnabled = true;

    [ObservableProperty]
    private string _currentUrl = "about:blank";

    public int SidebarWidth => _config?.SidebarWidth ?? 550;
    public int IconStripWidth => _config?.IconStripWidth ?? 50;
    public int CollapsedWidth => IconStripWidth;
    public int ExpandedWidth => SelectedShortcut?.CustomWidth > 0
        ? Math.Clamp(SelectedShortcut.CustomWidth, 200, 1200)
        : SidebarWidth;
    public int CurrentWidth => IsExpanded ? ExpandedWidth : CollapsedWidth;

    /// <summary>
    /// Raised when the user selects a tab and the View should show/create its WebView2.
    /// </summary>
    public event Action<ShortcutItem>? TabSwitchRequested;

    /// <summary>
    /// Raised when a shortcut is removed and its WebView2 should be disposed.
    /// </summary>
    public event Action<string>? TabRemoved;

    /// <summary>
    /// Raised when the sidebar should navigate the active tab to a URL (GoHome).
    /// </summary>
    public event Action<string>? NavigateRequested;

    /// <summary>
    /// Raised when the sidebar width changes (collapse/expand).
    /// </summary>
    public event Action<int>? WidthChanged;

    /// <summary>
    /// Raised after settings are saved so live panels can reload from AppConfig.
    /// </summary>
    public event Action? SettingsApplied;

    /// <summary>
    /// Raised when a navigation action is requested (back, forward, refresh, home).
    /// </summary>
    public event Action<string>? NavigationAction;

    /// <summary>
    /// Raised when the active shortcut is closed via a second click (collapse + deselect).
    /// The view should hide panels and optionally dispose the WebView.
    /// </summary>
    public event Action<ShortcutItem>? ContentDeselected;

    public void Initialize()
    {
        _initializing = true;
        _config = ConfigService.Load();
        AutoStart = _autoStartService.IsAutoStartEnabled();
        AdBlockEnabled = _config.AdBlockEnabled;
        _initializing = false;

        Groups.Clear();
        foreach (var group in _config.Groups.OrderBy(g => g.Order))
        {
            Groups.Add(group);
        }

        Shortcuts.Clear();
        foreach (var shortcut in _config.Shortcuts.OrderBy(s => s.Order))
        {
            Shortcuts.Add(shortcut);
        }

        RebuildDisplayItems();

        // Load favicons in background
        _faviconCts?.Cancel();
        _faviconCts?.Dispose();
        _faviconCts = new CancellationTokenSource();
        _ = LoadFaviconsAsync(_faviconCts.Token);
    }

    /// <summary>
    /// Rebuilds the DisplayItems list: groups (with their shortcuts) then ungrouped shortcuts.
    /// </summary>
    public void RebuildDisplayItems()
    {
        DisplayItems.Clear();

        // Ungrouped shortcuts first (no group assigned)
        foreach (var s in Shortcuts.Where(s => string.IsNullOrEmpty(s.GroupId)).OrderBy(s => s.Order))
        {
            DisplayItems.Add(s);
        }

        // Then each group + its shortcuts
        foreach (var group in Groups.OrderBy(g => g.Order))
        {
            DisplayItems.Add(group);
            if (group.IsExpanded)
            {
                foreach (var s in Shortcuts
                    .Where(s => s.GroupId == group.Id)
                    .OrderBy(s => s.Order))
                {
                    DisplayItems.Add(s);
                }
            }
        }
    }

    private async Task LoadFaviconsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var shortcut in Shortcuts.ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(shortcut.IconPath))
                {
                    if (!string.IsNullOrEmpty(shortcut.BuiltInType))
                        continue;

                    var iconPath = await _faviconService.GetFaviconAsync(shortcut.Url, cancellationToken);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        shortcut.IconPath = iconPath;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch
        {
            // Non-critical: favicons will show fallback letters
        }
    }

    [RelayCommand]
    private void SelectShortcut(ShortcutItem? item)
    {
        if (item == null) return;

        // Toggle: second click on the active shortcut collapses (and may unload the tab)
        if (SelectedShortcut == item && IsExpanded)
        {
            item.IsSelected = false;
            SelectedShortcut = null;
            ContentDeselected?.Invoke(item);
            ToggleExpand();
            return;
        }

        if (SelectedShortcut != null)
            SelectedShortcut.IsSelected = false;

        SelectedShortcut = item;
        item.IsSelected = true;
        CurrentUrl = item.Url;
        TabSwitchRequested?.Invoke(item);

        if (!IsExpanded)
        {
            ToggleExpand();
        }
        else
        {
            // Tab changed while expanded — apply per-tab width
            WidthChanged?.Invoke(CurrentWidth);
        }
    }

    [RelayCommand]
    private void GoBack() => NavigationAction?.Invoke(NavBack);

    [RelayCommand]
    private void GoForward() => NavigationAction?.Invoke(NavForward);

    [RelayCommand]
    private void GoHome()
    {
        if (SelectedShortcut != null)
        {
            NavigateRequested?.Invoke(SelectedShortcut.Url);
        }
    }

    [RelayCommand]
    private void Refresh() => NavigationAction?.Invoke(NavRefresh);

    [RelayCommand]
    private void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        WidthChanged?.Invoke(CurrentWidth);
    }

    [RelayCommand]
    private async Task AddShortcut(ShortcutItem item)
    {
        item.Order = Shortcuts.Count;
        Shortcuts.Add(item);

        // Download favicon
        var iconPath = await _faviconService.GetFaviconAsync(item.Url);
        if (!string.IsNullOrEmpty(iconPath))
        {
            item.IconPath = iconPath;
        }

        RebuildDisplayItems();
        SaveConfig();
    }

    [RelayCommand]
    private void RemoveShortcut(ShortcutItem? item)
    {
        if (item == null) return;

        var removedId = item.Id;
        var wasSelected = SelectedShortcut == item;

        if (wasSelected)
        {
            item.IsSelected = false;
            SelectedShortcut = null;
        }

        Shortcuts.Remove(item);

        // Re-order remaining
        for (int i = 0; i < Shortcuts.Count; i++)
        {
            Shortcuts[i].Order = i;
        }

        // Dispose the removed tab's WebView2 first
        TabRemoved?.Invoke(removedId);

        // Then switch to fallback tab
        if (wasSelected)
        {
            var fallback = Shortcuts.FirstOrDefault();
            if (fallback != null)
            {
                SelectedShortcut = fallback;
                fallback.IsSelected = true;
                TabSwitchRequested?.Invoke(fallback);
            }
        }

        RebuildDisplayItems();
        SaveConfig();
    }

    /// <summary>
    /// Raised when ad block enabled state changes.
    /// </summary>
    public event Action<bool>? AdBlockChanged;

    partial void OnAutoStartChanged(bool value)
    {
        if (!_initializing)
        {
            _autoStartService.SetAutoStart(value);
        }
    }

    partial void OnAdBlockEnabledChanged(bool value)
    {
        if (!_initializing)
        {
            _config.AdBlockEnabled = value;
            SaveConfig();
            AdBlockChanged?.Invoke(value);
        }
    }

    public void MoveShortcut(ShortcutItem item, ShortcutItem target)
    {
        // If dropping onto a shortcut in a different group, move to that group
        if (item.GroupId != target.GroupId)
        {
            item.GroupId = target.GroupId;
        }

        var oldIndex = Shortcuts.IndexOf(item);
        var newIndex = Shortcuts.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;

        Shortcuts.Move(oldIndex, newIndex);

        for (int i = 0; i < Shortcuts.Count; i++)
        {
            Shortcuts[i].Order = i;
        }

        RebuildDisplayItems();
        SaveConfig();
    }

    /// <summary>
    /// Moves a shortcut into a group (drop shortcut onto group header).
    /// </summary>
    public void MoveShortcutToGroup(ShortcutItem item, ShortcutGroup group)
    {
        item.GroupId = group.Id;
        RebuildDisplayItems();
        SaveConfig();
    }

    /// <summary>
    /// Removes a shortcut from its group (back to ungrouped).
    /// </summary>
    public void RemoveShortcutFromGroup(ShortcutItem item)
    {
        item.GroupId = string.Empty;
        RebuildDisplayItems();
        SaveConfig();
    }

    // ── Group management ──────────────────────────────────

    public ShortcutGroup AddGroup(string name)
    {
        var group = new ShortcutGroup
        {
            Name = name,
            Order = Groups.Count,
        };
        Groups.Add(group);
        RebuildDisplayItems();
        SaveConfig();
        return group;
    }

    public void RenameGroup(ShortcutGroup group, string newName)
    {
        group.Name = newName;
        SaveConfig();
    }

    public void RemoveGroup(ShortcutGroup group)
    {
        // Ungroup all shortcuts in this group
        foreach (var s in Shortcuts.Where(s => s.GroupId == group.Id))
        {
            s.GroupId = string.Empty;
        }
        Groups.Remove(group);

        // Re-order remaining groups
        for (int i = 0; i < Groups.Count; i++)
            Groups[i].Order = i;

        RebuildDisplayItems();
        SaveConfig();
    }

    public void ToggleGroupExpanded(ShortcutGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
        RebuildDisplayItems();
        SaveConfig();
    }

    public void MoveGroup(ShortcutGroup item, ShortcutGroup target)
    {
        var oldIndex = Groups.IndexOf(item);
        var newIndex = Groups.IndexOf(target);
        if (oldIndex < 0 || newIndex < 0) return;

        Groups.Move(oldIndex, newIndex);
        for (int i = 0; i < Groups.Count; i++)
            Groups[i].Order = i;

        RebuildDisplayItems();
        SaveConfig();
    }

    /// <summary>
    /// Returns all groups for building context menu submenus.
    /// </summary>
    public IReadOnlyList<ShortcutGroup> GetGroups() => Groups.ToList();

    public void SetCurrentTabWidth(int widthWpf)
    {
        if (SelectedShortcut == null) return;
        SelectedShortcut.CustomWidth = widthWpf;
        WidthChanged?.Invoke(CurrentWidth);
    }

    public AppConfig GetConfig() => _config;

    public void CancelPendingOperations()
    {
        _faviconCts?.Cancel();
        _faviconCts?.Dispose();
        _faviconCts = null;
    }

    public void SaveConfig()
    {
        _config.Shortcuts = Shortcuts.ToList();
        _config.Groups = Groups.ToList();
        ConfigService.Save(_config);
    }

    /// <summary>
    /// Applies layout + integration settings that were written to AppConfig (settings window).
    /// </summary>
    public void NotifySettingsApplied()
    {
        WidthChanged?.Invoke(CurrentWidth);
        SettingsApplied?.Invoke();
    }

    /// <summary>
    /// Syncs Météo / Hue / Volets shortcuts with Enable* flags after settings save.
    /// </summary>
    public void ApplyBuiltInPanelSettings()
    {
        _config.FeaturesInitialized = true;
        ConfigService.SyncBuiltInShortcuts(_config);

        ApplyBuiltIn("weather", _config.EnableWeather, "Météo", ConfigService.WeatherIconPath);
        ApplyBuiltIn("hue", _config.EnableHue, "Hue", ConfigService.HueIconPath);
        ApplyBuiltIn("shutter", _config.EnableShutters, "Volets", ConfigService.ShutterIconPath);

        for (var i = 0; i < Shortcuts.Count; i++)
            Shortcuts[i].Order = i;

        RebuildDisplayItems();
    }

    /// <summary>Closes the current panel/tab selection (used when disabling a feature).</summary>
    public void DeselectCurrentContent()
    {
        if (SelectedShortcut == null) return;

        var item = SelectedShortcut;
        item.IsSelected = false;
        SelectedShortcut = null;
        ContentDeselected?.Invoke(item);
        if (IsExpanded)
            ToggleExpand();
    }

    private void ApplyBuiltIn(string type, bool enabled, string name, string iconPath)
    {
        var items = Shortcuts.Where(s => s.BuiltInType == type).ToList();
        if (enabled)
        {
            if (items.Count == 0)
            {
                var fromConfig = _config.Shortcuts.FirstOrDefault(s => s.BuiltInType == type);
                Shortcuts.Add(fromConfig ?? new ShortcutItem
                {
                    Name = name,
                    BuiltInType = type,
                    IconPath = iconPath,
                    Order = Shortcuts.Count,
                });
            }
            else
            {
                foreach (var item in items)
                    item.IconPath = iconPath;
            }
            return;
        }

        foreach (var item in items)
        {
            var wasSelected = SelectedShortcut == item;
            if (wasSelected)
            {
                item.IsSelected = false;
                SelectedShortcut = null;
                IsExpanded = false;
                ContentDeselected?.Invoke(item);
            }

            Shortcuts.Remove(item);
            TabRemoved?.Invoke(item.Id);
        }
    }
}
