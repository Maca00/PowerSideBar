using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PowerSideBar.Models;
using PowerSideBar.Services;

namespace PowerSideBar.Views;

public partial class AddShortcutDialog : Window
{
    private string _customIconPath = string.Empty;
    public ShortcutItem? Result { get; private set; }

    public AddShortcutDialog()
    {
        InitializeComponent();
        TxtName.Focus();
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        var url = TxtUrl.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(Loc.T("dialog.name_required"), Loc.T("common.validation"), MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show(Loc.T("dialog.url_required"), Loc.T("common.validation"), MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtUrl.Focus();
            return;
        }

        url = ConfigService.NormalizeUrl(url);

        Result = new ShortcutItem
        {
            Name = name,
            Url = url,
            IconPath = _customIconPath,
        };

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnBrowseIcon_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.T("dialog.browse_icon"),
            Filter = "Images|*.ico;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
        };

        if (ofd.ShowDialog() == true)
        {
            try
            {
                // Copy icon to cache so it persists even if the original is moved/deleted
                var ext = Path.GetExtension(ofd.FileName);
                var destPath = Path.Combine(ConfigService.IconsCacheFolder, $"custom_{Guid.NewGuid():N}{ext}");
                File.Copy(ofd.FileName, destPath, true);
                _customIconPath = destPath;

                FaviconPreview.Source = new BitmapImage(new Uri(_customIconPath));
            }
            catch
            {
                // Invalid image or copy failed
                _customIconPath = ofd.FileName;
            }
        }
    }
}
