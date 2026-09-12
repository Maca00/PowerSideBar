using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerSideBar.Models;

public partial class ShortcutItem : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _iconPath = string.Empty;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isSelected;

    /// <summary>
    /// Per-tab width in WPF units. 0 means use the global SidebarWidth.
    /// </summary>
    [ObservableProperty]
    private int _customWidth;

    /// <summary>
    /// When true, use desktop user agent. When false (default), use mobile.
    /// </summary>
    [ObservableProperty]
    private bool _useDesktopUserAgent;

    /// <summary>
    /// When true, the tab stays active in the background (not suspended/evicted).
    /// </summary>
    [ObservableProperty]
    private bool _keepInBackground;

    /// <summary>
    /// When true, the panel floats on top instead of pushing desktop content.
    /// </summary>
    [ObservableProperty]
    private bool _overlayMode;

    /// <summary>
    /// When true, collapsing via a second click disposes the WebView2 (same as "Fermer l'onglet").
    /// When false, the instance is kept in memory and can be restored instantly.
    /// </summary>
    [ObservableProperty]
    private bool _closeOnCollapse;

    /// <summary>
    /// Group this shortcut belongs to. Empty = ungrouped.
    /// </summary>
    [ObservableProperty]
    private string _groupId = string.Empty;

    /// <summary>
    /// Identifies a built-in panel type (e.g. "weather"). Empty = normal web shortcut.
    /// </summary>
    [ObservableProperty]
    private string _builtInType = string.Empty;

    /// <summary>
    /// Runtime-only: true when a WebView2 instance exists for this tab.
    /// </summary>
    [ObservableProperty]
    [property: JsonIgnore]
    private bool _isOpen;
}
