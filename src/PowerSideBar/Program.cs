using System;
using PowerSideBar.Services;
using Velopack;

namespace PowerSideBar;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before WPF so install/update hooks exit quickly.
        VelopackApp.Build().Run();

        // Default to English before loading config (SyncBuiltInShortcuts uses Loc.T).
        Loc.SetLanguage(Loc.English);
        var config = ConfigService.Load();
        Loc.SetLanguage(config.Language);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
