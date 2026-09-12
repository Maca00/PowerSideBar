using System.Windows;

namespace PowerSideBar.Views;

public partial class RenameDialog : Window
{
    public string ResultName { get; private set; } = string.Empty;

    public RenameDialog(string currentName)
    {
        InitializeComponent();
        TxtName.Text = currentName;
        TxtName.SelectAll();
        TxtName.Focus();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtName.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        ResultName = name;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
