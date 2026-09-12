using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PowerSideBar.Models;

public partial class ShortcutGroup : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private int _order;

    /// <summary>
    /// Whether the group's shortcuts are visible in the icon strip.
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded = true;
}
