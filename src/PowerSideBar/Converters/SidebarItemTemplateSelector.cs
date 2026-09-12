using System.Windows;
using System.Windows.Controls;
using PowerSideBar.Models;

namespace PowerSideBar.Converters;

public class SidebarItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? GroupTemplate { get; set; }
    public DataTemplate? ShortcutTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        return item switch
        {
            ShortcutGroup => GroupTemplate,
            ShortcutItem => ShortcutTemplate,
            _ => base.SelectTemplate(item, container),
        };
    }
}
