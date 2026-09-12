using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PowerSideBar.Interop;
using PowerSideBar.Models;
using PowerSideBar.Services;
using PowerSideBar.ViewModels;

namespace PowerSideBar.Views;

public partial class SidebarWindow : Window
{
    private static readonly HttpClient MiniArtHttpClient = CreateMiniArtHttpClient();

    private static HttpClient CreateMiniArtHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PowerSideBar/1.0");
        return c;
    }

    private const string DragDropFormat = "ShortcutItem";
    private const string GroupDragDropFormat = "ShortcutGroup";
    private const string MobileUserAgent = "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36";
    private const string DesktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly SolidColorBrush GroupHighlightBrush = CreateFrozenBrush(0x3E, 0x3E, 0x3E);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly AppBarManager _appBarManager = new();
    private readonly AdBlockService _adBlockService = new();
    private readonly ExtensionService _extensionService = new();
    private readonly SidebarViewModel _viewModel;
    private readonly CancellationTokenSource _cts = new();
    private HwndSource? _windowSource;
    private bool _isClosing;
    private bool _cleanedUp;
    private Point _dragStartPoint;
    private bool _suppressNextGroupClick;

    // Multi-WebView2 state
    private CoreWebView2Environment? _webViewEnvironment;
    private readonly Dictionary<string, WebView2> _webViews = new();
    private readonly HashSet<string> _pendingCreations = new();
    private readonly LinkedList<string> _lruOrder = new();
    private WebView2? _activeWebView;

    // Weather panel state
    private WeatherViewModel? _weatherViewModel;
    private bool _weatherPanelActive;

    // Hue panel state
    private HueViewModel? _hueViewModel;
    private bool _huePanelActive;

    // Shutter panel state
    private ShutterViewModel? _shutterViewModel;
    private bool _shutterPanelActive;

    // Spotify panel state
    private SpotifyViewModel? _spotifyViewModel;
    private bool _spotifyPanelActive;
    /// <summary>Fallback item used by the bottom Spotify button when no "spotify" shortcut exists in the list.</summary>
    private ShortcutItem? _spotifyFallbackItem;

    /// <summary>Increments on each mini-cover request so stale downloads are ignored.</summary>
    private int _miniArtLoadId;

    private const double MiniTextClipFallbackWidth = 40;
    /// <summary>Extra pixels scrolled past the natural end so sub-pixel rounding / anti-aliasing does not clip glyphs.</summary>
    private const double MiniTextEndOvershootPx = 6;
    private const double MiniTextScrollSpeedPxPerSec = 22;
    private const double MiniTextStartPauseSec = 2.0; // requested: 2 s pause before each new scroll cycle
    private const double MiniTextEndPauseSec = 1.0;
    private const double MiniTextReturnSec = 0.8;
    private const double MiniTextMinScrollSec = 1.5;

    // Track the last text we started a marquee for, so frequent PropertyChanged events
    // (e.g. ProgressMs ticking every second) don't restart and freeze the animation.
    private string _miniTitleActiveText = string.Empty;
    private string _miniArtistActiveText = string.Empty;

    // Global mouse hook for overlay mode
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseHookProc;
    private bool _closingOverlay; // Flag to prevent re-triggering while closing

    public SidebarViewModel ViewModel => _viewModel;

    public SidebarWindow()
    {
        InitializeComponent();

        _viewModel = new SidebarViewModel();
        DataContext = _viewModel;

        _viewModel.TabSwitchRequested += OnTabSwitchRequested;
        _viewModel.TabRemoved += OnTabRemoved;
        _viewModel.NavigateRequested += OnNavigateRequested;
        _viewModel.NavigationAction += OnNavigationAction;
        _viewModel.WidthChanged += OnWidthChanged;
        _viewModel.ContentDeselected += OnContentDeselected;
        _viewModel.AdBlockChanged += OnAdBlockChanged;
        _viewModel.SettingsApplied += OnSettingsApplied;
        _appBarManager.PositionChanged += OnAppBarPositionChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnAdBlockChanged(bool enabled) => _adBlockService.IsEnabled = enabled;

    private void OnSettingsApplied()
    {
        ApplySidebarSideLayout();
        _appBarManager.SetDockLeft(IsDockedLeft());
        RefreshSidebarPlacement();

        var config = _viewModel.GetConfig();

        UpdateSpotifyFeatureVisibility();

        if (config.EnableSpotify)
            _spotifyViewModel?.ApplyConfiguration();

        if (_weatherViewModel != null && config.EnableWeather)
        {
            _weatherViewModel.UpdateLocation(
                config.WeatherLatitude,
                config.WeatherLongitude,
                config.WeatherTimezone,
                config.WeatherCityName);
            _ = _weatherViewModel.RefreshCommand.ExecuteAsync(null);
        }

        if (config.EnableHue)
            _hueViewModel?.ApplyConfiguration();

        if (config.EnableShutters &&
            _shutterViewModel != null &&
            _shutterViewModel.State != ShutterConnectionState.Connecting)
        {
            _ = _shutterViewModel.ConnectCommand.ExecuteAsync(null);
        }
    }

    private bool IsDockedLeft() =>
        string.Equals(_viewModel.GetConfig().SidebarSide, "left", StringComparison.OrdinalIgnoreCase);

    private void ApplySidebarSideLayout()
    {
        var dockLeft = IsDockedLeft();
        var iconW = new GridLength(_viewModel.IconStripWidth);
        var star = new GridLength(1, GridUnitType.Star);

        if (dockLeft)
        {
            Grid.SetColumn(IconStripPanel, 0);
            Grid.SetColumn(ContentPanel, 1);
            ContentColumn.Width = iconW;
            IconStripColumn.Width = star;
            RootBorder.BorderThickness = new Thickness(0, 0, 1, 0);
            ResizeGripLeft.Visibility = Visibility.Collapsed;
            ResizeGripRight.Visibility = Visibility.Visible;
        }
        else
        {
            Grid.SetColumn(ContentPanel, 0);
            Grid.SetColumn(IconStripPanel, 1);
            ContentColumn.Width = star;
            IconStripColumn.Width = iconW;
            RootBorder.BorderThickness = new Thickness(1, 0, 0, 0);
            ResizeGripLeft.Visibility = Visibility.Visible;
            ResizeGripRight.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSpotifyFeatureVisibility()
    {
        var enabled = _viewModel.GetConfig().EnableSpotify;
        var visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (BtnSpotifyTab != null)
            BtnSpotifyTab.Visibility = visibility;
        if (SpotifyMiniBar != null)
            SpotifyMiniBar.Visibility = visibility;

        if (!enabled)
        {
            if (SpotifyPanelInstance.Visibility == Visibility.Visible || _spotifyPanelActive)
            {
                SpotifyPanelInstance.Visibility = Visibility.Collapsed;
                _spotifyPanelActive = false;
                _viewModel.DeselectCurrentContent();
            }

            StopSpotifyMiniMarquees();
            _spotifyViewModel?.StopAutoRefresh();
        }
        else if (_spotifyViewModel == null)
        {
            InitializeSpotifyMiniPlayer();
        }
        else if (_spotifyViewModel.IsAuthenticated)
        {
            _spotifyViewModel.StartAutoRefresh();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Hide from Alt+Tab by adding WS_EX_TOOLWINDOW extended style (64-bit safe)
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        _windowSource = HwndSource.FromHwnd(hwnd);
        _windowSource?.AddHook(WindowWndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine("=== SidebarWindow OnLoaded START ===");
        
        _viewModel.Initialize();
        ApplySidebarSideLayout();
        // Hide disabled Spotify UI immediately (before slow WebView2 init)
        UpdateSpotifyFeatureVisibility();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Register as AppBar
        var dpiScale = GetDpiScale();
        var widthPx = (int)(_viewModel.CurrentWidth * dpiScale);
        _appBarManager.Register(this, widthPx, IsDockedLeft());

        try
        {
            // Create shared environment with extensions enabled
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AreBrowserExtensionsEnabled = true,
            };
            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: ConfigService.WebView2DataFolder,
                options: envOptions);

            // Install uBlock Origin Lite (needs a temporary WebView for Profile access)
            await InstallExtensionsAsync();

            // Init fallback ad blocker only if uBOL extension is not available
            if (!_extensionService.IsUBlockInstalled)
            {
                await _adBlockService.InitializeAsync(_cts.Token);
                _adBlockService.IsEnabled = _viewModel.AdBlockEnabled;
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown during initialization
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Loc.Tf("webview.runtime_missing", ex.Message),
                Loc.T("webview.runtime_title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        // Mouse hook is installed only while overlay mode is active (see SyncMouseHookForOverlay)

        // Initialize Spotify mini player (respects EnableSpotify)
        if (_viewModel.GetConfig().EnableSpotify)
            InitializeSpotifyMiniPlayer();
        UpdateSpotifyFeatureVisibility();
        
        System.Diagnostics.Debug.WriteLine("=== SidebarWindow OnLoaded END ===");
    }

    private void InitializeSpotifyMiniPlayer()
    {
        try
        {
            // Create Spotify service and ViewModel (shared between mini player and full panel)
            var config = _viewModel.GetConfig();
            _spotifyViewModel = new SpotifyViewModel(new SpotifyService(), config);
            SpotifyPanelInstance.DataContext = _spotifyViewModel;

            System.Diagnostics.Debug.WriteLine($"SpotifyViewModel created. IsAuthenticated: {_spotifyViewModel.IsAuthenticated}");

            // Ensure mini bar exists and is visible
            if (SpotifyMiniBar != null)
            {
                SpotifyMiniBar.Visibility = Visibility.Visible;
                SpotifyMiniBar.MinHeight = 0;
                System.Diagnostics.Debug.WriteLine("SpotifyMiniBar found and set to visible");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: SpotifyMiniBar not found!");
                return;
            }

            // Wire up button clicks WITH ERROR HANDLING
            if (BtnSpotifyPrev != null)
            {
                BtnSpotifyPrev.Click += async (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"BtnSpotifyPrev clicked. IsAuthenticated: {_spotifyViewModel?.IsAuthenticated}");
                    if (_spotifyViewModel != null)
                    {
                        if (!_spotifyViewModel.IsAuthenticated)
                        {
                            await _spotifyViewModel.AuthorizeCommand.ExecuteAsync(null);
                        }
                        else
                        {
                            await _spotifyViewModel.PreviousCommand.ExecuteAsync(null);
                        }
                    }
                };
                System.Diagnostics.Debug.WriteLine("BtnSpotifyPrev wired successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: BtnSpotifyPrev not found!");
            }

            if (BtnSpotifyPlayPause != null)
            {
                BtnSpotifyPlayPause.Click += async (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"BtnSpotifyPlayPause clicked. IsAuthenticated: {_spotifyViewModel?.IsAuthenticated}");
                    if (_spotifyViewModel != null)
                    {
                        if (!_spotifyViewModel.IsAuthenticated)
                        {
                            await _spotifyViewModel.AuthorizeCommand.ExecuteAsync(null);
                        }
                        else if (_spotifyViewModel.IsPlaying)
                        {
                            await _spotifyViewModel.PauseCommand.ExecuteAsync(null);
                        }
                        else
                        {
                            await _spotifyViewModel.PlayCommand.ExecuteAsync(null);
                        }
                    }
                };
                System.Diagnostics.Debug.WriteLine("BtnSpotifyPlayPause wired successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: BtnSpotifyPlayPause not found!");
            }

            if (BtnSpotifyNext != null)
            {
                BtnSpotifyNext.Click += async (s, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"BtnSpotifyNext clicked. IsAuthenticated: {_spotifyViewModel?.IsAuthenticated}");
                    if (_spotifyViewModel != null)
                    {
                        if (!_spotifyViewModel.IsAuthenticated)
                        {
                            await _spotifyViewModel.AuthorizeCommand.ExecuteAsync(null);
                        }
                        else
                        {
                            await _spotifyViewModel.NextCommand.ExecuteAsync(null);
                        }
                    }
                };
                System.Diagnostics.Debug.WriteLine("BtnSpotifyNext wired successfully");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("ERROR: BtnSpotifyNext not found!");
            }

            if (BtnSpotifyLike != null)
            {
                BtnSpotifyLike.Click += async (s, e) =>
                {
                    if (_spotifyViewModel != null)
                    {
                        if (!_spotifyViewModel.IsAuthenticated)
                        {
                            await _spotifyViewModel.AuthorizeCommand.ExecuteAsync(null);
                        }
                        else
                        {
                            await _spotifyViewModel.ToggleLikeCommand.ExecuteAsync(null);
                        }
                    }
                };
            }

            // Mini-bar stays live for the whole session while authenticated
            ((INotifyPropertyChanged)_spotifyViewModel).PropertyChanged += OnSpotifyMiniPropertyChanged;

            UpdateSpotifyMiniDisplay();
            System.Diagnostics.Debug.WriteLine("UpdateSpotifyMiniDisplay called");
            
            if (_spotifyViewModel.IsAuthenticated)
            {
                _ = _spotifyViewModel.RefreshCommand.ExecuteAsync(null);
                _spotifyViewModel.StartAutoRefresh();
            }

            System.Diagnostics.Debug.WriteLine("InitializeSpotifyMiniPlayer completed successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Spotify Init Error: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void OnSpotifyMiniPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpotifyViewModel.CurrentTrack) ||
            e.PropertyName == nameof(SpotifyViewModel.CurrentArtist) ||
            e.PropertyName == nameof(SpotifyViewModel.AlbumArtUrl) ||
            e.PropertyName == nameof(SpotifyViewModel.IsPlaying) ||
            e.PropertyName == nameof(SpotifyViewModel.IsAuthenticated) ||
            e.PropertyName == nameof(SpotifyViewModel.ProgressMs) ||
            e.PropertyName == nameof(SpotifyViewModel.IsCurrentTrackLiked))
        {
            Dispatcher.BeginInvoke(UpdateSpotifyMiniDisplay);
        }
    }

    private void UpdateSpotifyMiniDisplay()
    {
        try
        {
            if (_spotifyViewModel == null || SpotifyMiniArt == null)
                return;

            // Mini sidebar cover: load via HttpClient — UriSource on remote HTTPS is flaky here
            // (async decode / TLS) and was leaving only the ♪ placeholder visible.
            if (_spotifyViewModel.IsAuthenticated)
                RequestMiniAlbumArt(_spotifyViewModel.AlbumArtUrl);
            else
                RequestMiniAlbumArt(null);

            if (SpotifyMiniTitle != null)
            {
                SpotifyMiniTitle.Text = _spotifyViewModel.IsAuthenticated
                    ? (_spotifyViewModel.CurrentTrack ?? string.Empty)
                    : string.Empty;
            }

            if (SpotifyMiniArtist != null)
            {
                SpotifyMiniArtist.Text = _spotifyViewModel.IsAuthenticated
                    ? (_spotifyViewModel.CurrentArtist ?? string.Empty)
                    : string.Empty;
            }

            StartSpotifyMiniMarquees();
            
            // Update play/pause button
            if (BtnSpotifyPlayPause != null)
            {
                var playPauseBtn = BtnSpotifyPlayPause.Content as TextBlock;
                if (playPauseBtn == null)
                {
                    playPauseBtn = new TextBlock 
                    { 
                        FontFamily = new FontFamily("Segoe UI Symbol"),
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54)), 
                        FontWeight = FontWeights.Bold 
                    };
                    BtnSpotifyPlayPause.Content = playPauseBtn;
                }
                playPauseBtn.Text = _spotifyViewModel.IsPlaying ? "\u23F8" : "\u25B6";
            }

            if (SpotifyLikeIcon != null)
            {
                SpotifyLikeIcon.Text = _spotifyViewModel.IsCurrentTrackLiked ? "\u2665" : "\u2661";
                SpotifyLikeIcon.Foreground = _spotifyViewModel.IsCurrentTrackLiked
                    ? new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54))
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateSpotifyMiniDisplay Error: {ex.Message}");
        }
    }

    private void RequestMiniAlbumArt(string? coverUrl)
    {
        if (SpotifyMiniArt == null)
            return;

        var loadId = Interlocked.Increment(ref _miniArtLoadId);

        if (string.IsNullOrEmpty(coverUrl))
        {
            SpotifyMiniArt.Source = null;
            if (SpotifyMiniPlaceholder != null)
                SpotifyMiniPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await MiniArtHttpClient.GetByteArrayAsync(coverUrl).ConfigureAwait(false);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (loadId != _miniArtLoadId)
                        return;
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.StreamSource = ms;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        SpotifyMiniArt.Source = bmp;
                        if (SpotifyMiniPlaceholder != null)
                            SpotifyMiniPlaceholder.Visibility = Visibility.Collapsed;
                    }
                    catch
                    {
                        SpotifyMiniArt.Source = null;
                        if (SpotifyMiniPlaceholder != null)
                            SpotifyMiniPlaceholder.Visibility = Visibility.Visible;
                    }
                });
            }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (loadId != _miniArtLoadId)
                        return;
                    SpotifyMiniArt.Source = null;
                    if (SpotifyMiniPlaceholder != null)
                        SpotifyMiniPlaceholder.Visibility = Visibility.Visible;
                });
            }
        });
    }

    /// <summary>
    /// Starts marquee animations for any lines whose text changed since last start.
    /// Must be called after Text has been assigned. Safe to call on every
    /// <c>PropertyChanged</c>: restarts only happen when the text actually changed.
    /// </summary>
    private void StartSpotifyMiniMarquees()
    {
        var newTitle = SpotifyMiniTitle?.Text ?? string.Empty;
        var newArtist = SpotifyMiniArtist?.Text ?? string.Empty;

        var titleChanged = !string.Equals(newTitle, _miniTitleActiveText, StringComparison.Ordinal);
        var artistChanged = !string.Equals(newArtist, _miniArtistActiveText, StringComparison.Ordinal);

        if (!titleChanged && !artistChanged)
            return;

        if (titleChanged)
        {
            _miniTitleActiveText = newTitle;
            if (SpotifyMiniTitleScroll != null)
            {
                SpotifyMiniTitleScroll.BeginAnimation(TranslateTransform.XProperty, null);
                SpotifyMiniTitleScroll.X = 0;
            }
        }

        if (artistChanged)
        {
            _miniArtistActiveText = newArtist;
            if (SpotifyMiniArtistScroll != null)
            {
                SpotifyMiniArtistScroll.BeginAnimation(TranslateTransform.XProperty, null);
                SpotifyMiniArtistScroll.X = 0;
            }
        }

        // Defer to after the layout pass so TextBlock DesiredSize reflects the new text
        // and the parent Border has its final ActualWidth.
        Dispatcher.BeginInvoke(
            (Action)(() =>
            {
                try
                {
                    UpdateLayout();
                    if (titleChanged)
                        StartMarqueeForLine(SpotifyMiniTitle, SpotifyMiniTitleScroll);
                    if (artistChanged)
                        StartMarqueeForLine(SpotifyMiniArtist, SpotifyMiniArtistScroll);
                }
                catch
                {
                    // ignore
                }
            }),
            DispatcherPriority.Loaded);
    }

    private static void StartMarqueeForLine(TextBlock? tb, TranslateTransform? transform)
    {
        if (tb == null || transform == null)
            return;

        // Clear any previous animation holdover on this transform.
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;

        var clipWidth = GetMiniTextClipWidth(tb);
        var textWidth = MeasureMiniLineText(tb, tb.Text);

        if (string.IsNullOrEmpty(tb.Text) || textWidth <= clipWidth + 0.5)
            return;

        var scrollAmount = textWidth - clipWidth + MiniTextEndOvershootPx;
        var scrollSec = Math.Max(MiniTextMinScrollSec, scrollAmount / MiniTextScrollSpeedPxPerSec);

        // Keyframe timeline (one full cycle, then RepeatBehavior.Forever):
        //   0s                      -> X=0           (cycle start)
        //   startPauseSec           -> X=0           (hold at start, 2 s)
        //   + scrollSec             -> X=-scrollAmt  (linear scroll to end)
        //   + endPauseSec           -> X=-scrollAmt  (hold at end, 1 s)
        //   + returnSec             -> X=0           (ease-out back to start)
        var anim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.HoldEnd,
        };

        var t = 0.0;
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += MiniTextStartPauseSec;
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += scrollSec;
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-scrollAmount, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += MiniTextEndPauseSec;
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(-scrollAmount, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        t += MiniTextReturnSec;
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t)),
            new KeySpline(0.25, 0.1, 0.25, 1.0))); // ease-out

        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void StopSpotifyMiniMarquees()
    {
        if (SpotifyMiniTitleScroll != null)
        {
            SpotifyMiniTitleScroll.BeginAnimation(TranslateTransform.XProperty, null);
            SpotifyMiniTitleScroll.X = 0;
        }

        if (SpotifyMiniArtistScroll != null)
        {
            SpotifyMiniArtistScroll.BeginAnimation(TranslateTransform.XProperty, null);
            SpotifyMiniArtistScroll.X = 0;
        }

        _miniTitleActiveText = string.Empty;
        _miniArtistActiveText = string.Empty;
    }

    /// <summary>
    /// Width of the clipping strip surrounding this scrolling TextBlock. The TextBlock lives inside
    /// a Canvas inside a fixed-width Border; we walk up to the first Border (the clip).
    /// </summary>
    private static double GetMiniTextClipWidth(TextBlock? tb)
    {
        for (DependencyObject? p = tb?.Parent; p != null; p = (p as FrameworkElement)?.Parent)
        {
            if (p is Border fe && fe.ActualWidth > 2)
                return fe.ActualWidth;
        }

        return MiniTextClipFallbackWidth;
    }

    private static double MeasureMiniLineText(TextBlock tb, string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        try
        {
            var dpi = 1.25;
            try { dpi = VisualTreeHelper.GetDpi(tb).PixelsPerDip; } catch { }

            var tfMode = TextOptions.GetTextFormattingMode(tb);
            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                tb.FontSize,
                Brushes.White,
                dpi);
            var wFormatted = ft.WidthIncludingTrailingWhitespace;

            var probe = new TextBlock
            {
                Text = text,
                FontFamily = tb.FontFamily,
                FontSize = tb.FontSize,
                FontStyle = tb.FontStyle,
                FontWeight = tb.FontWeight,
                FontStretch = tb.FontStretch,
                TextWrapping = TextWrapping.NoWrap,
                SnapsToDevicePixels = tb.SnapsToDevicePixels,
            };
            TextOptions.SetTextFormattingMode(probe, tfMode);
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var wProbe = probe.DesiredSize.Width;

            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var wLayout = tb.DesiredSize.Width;

            // Small +2 px allows for sub-pixel rounding; the big tail buffer is applied at scroll time.
            return Math.Max(Math.Max(wFormatted, wProbe), wLayout) + 2;
        }
        catch
        {
            return 0;
        }
    }

    private async Task InstallExtensionsAsync()
    {
        // We need a temporary WebView to access the Profile for extension installation
        var tempWebView = new WebView2 { Visibility = Visibility.Collapsed };
        WebViewContainer.Children.Add(tempWebView);
        try
        {
            await tempWebView.EnsureCoreWebView2Async(_webViewEnvironment);
            await _extensionService.InstallIfNeededAsync(tempWebView.CoreWebView2);
        }
        catch
        {
            // Non-critical: fallback to AdBlockService
        }
        finally
        {
            WebViewContainer.Children.Remove(tempWebView);
            tempWebView.Dispose();
        }
    }

    private async void OnTabSwitchRequested(ShortcutItem item)
    {
        // Built-in panels (weather, etc.) don't use WebView2
        if (!string.IsNullOrEmpty(item.BuiltInType))
        {
            try
            {
                await HandleBuiltInTabSwitchAsync(item);
                ApplyOverlayMode(item.OverlayMode);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Built-in tab switch failed: {ex.Message}");
            }
            return;
        }

        if (_webViewEnvironment == null || _cts.IsCancellationRequested) return;

        // Guard against rapid double-clicks creating duplicate WebView2s
        if (!_webViews.ContainsKey(item.Id) && !_pendingCreations.Add(item.Id))
            return;

        try
        {
            var webView = await GetOrCreateWebViewAsync(item);
            await SwitchToWebViewAsync(item.Id, webView);
            ApplyOverlayMode(item.OverlayMode);
        }
        catch (OperationCanceledException)
        {
            // Shutdown during tab creation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WebView2 creation failed for tab '{item.Name}': {ex.Message}");
        }
        finally
        {
            _pendingCreations.Remove(item.Id);
        }
    }

    private void OnContentDeselected(ShortcutItem item)
    {
        HideAllBuiltInPanels(restoreNavBar: true);

        if (!string.IsNullOrEmpty(item.BuiltInType))
        {
            item.IsOpen = false;
            return;
        }

        // Web tab: optionally dispose (same as "Fermer l'onglet"), otherwise just hide
        if (item.CloseOnCollapse)
        {
            DisposeWebView(item);
            return;
        }

        if (_webViews.TryGetValue(item.Id, out var webView))
        {
            webView.Visibility = Visibility.Collapsed;
            if (_activeWebView == webView)
                _activeWebView = null;

            if (webView.CoreWebView2 != null && !item.KeepInBackground)
            {
                try { _ = webView.CoreWebView2.TrySuspendAsync(); } catch { }
            }

            // Tab stays "open" in memory for a fast reopen
            item.IsOpen = true;
        }
        else
        {
            item.IsOpen = false;
        }
    }

    /// <summary>
    /// Disposes the WebView2 for a shortcut (same effect as "Fermer l'onglet").
    /// </summary>
    private void DisposeWebView(ShortcutItem item)
    {
        if (!_webViews.TryGetValue(item.Id, out var webView))
        {
            item.IsOpen = false;
            return;
        }

        _webViews.Remove(item.Id);
        _lruOrder.Remove(item.Id);

        if (_activeWebView == webView)
            _activeWebView = null;

        WebViewContainer.Children.Remove(webView);
        webView.Dispose();
        item.IsOpen = false;
    }

    private async Task HandleBuiltInTabSwitchAsync(ShortcutItem item)
    {
        await DeactivateActiveWebViewAsync();
        HideAllBuiltInPanels(restoreNavBar: false);

        switch (item.BuiltInType)
        {
            case "weather":
            {
                if (_weatherViewModel == null)
                {
                    var config = _viewModel.GetConfig();
                    _weatherViewModel = new WeatherViewModel(
                        new WeatherService(),
                        config.WeatherLatitude,
                        config.WeatherLongitude,
                        config.WeatherTimezone,
                        config.WeatherCityName);
                    WeatherPanelInstance.DataContext = _weatherViewModel;
                }

                WeatherPanelInstance.Visibility = Visibility.Visible;
                NavBar.Visibility = Visibility.Collapsed;
                _weatherPanelActive = true;
                item.IsOpen = true;
                _ = _weatherViewModel.RefreshCommand.ExecuteAsync(null);
                break;
            }
            case "hue":
            {
                if (_hueViewModel == null)
                {
                    var config = _viewModel.GetConfig();
                    _hueViewModel = new HueViewModel(new HueService(), config);
                    HuePanelInstance.DataContext = _hueViewModel;
                }

                HuePanelInstance.Visibility = Visibility.Visible;
                NavBar.Visibility = Visibility.Collapsed;
                _huePanelActive = true;
                item.IsOpen = true;

                if (_hueViewModel.IsConfigured)
                    _ = _hueViewModel.RefreshCommand.ExecuteAsync(null);
                else if (_hueViewModel.PairingStep == HuePairingStep.Idle)
                    _ = _hueViewModel.DiscoverBridgeCommand.ExecuteAsync(null);
                break;
            }
            case "shutter":
            {
                if (_shutterViewModel == null)
                {
                    var config = _viewModel.GetConfig();
                    _shutterViewModel = new ShutterViewModel(config);
                    ShutterPanelInstance.DataContext = _shutterViewModel;
                }

                ShutterPanelInstance.Visibility = Visibility.Visible;
                NavBar.Visibility = Visibility.Collapsed;
                _shutterPanelActive = true;
                item.IsOpen = true;

                if (_shutterViewModel.State == ShutterConnectionState.Idle ||
                    _shutterViewModel.State == ShutterConnectionState.Error)
                    _ = _shutterViewModel.ConnectCommand.ExecuteAsync(null);
                break;
            }
            case "spotify":
            {
                if (_spotifyViewModel == null)
                {
                    var config = _viewModel.GetConfig();
                    _spotifyViewModel = new SpotifyViewModel(new SpotifyService(), config);
                    ((INotifyPropertyChanged)_spotifyViewModel).PropertyChanged += OnSpotifyMiniPropertyChanged;
                }
                if (SpotifyPanelInstance.DataContext != _spotifyViewModel)
                    SpotifyPanelInstance.DataContext = _spotifyViewModel;

                SpotifyPanelInstance.Visibility = Visibility.Visible;
                NavBar.Visibility = Visibility.Collapsed;
                _spotifyPanelActive = true;
                item.IsOpen = true;

                if (_spotifyViewModel.IsAuthenticated)
                {
                    _ = _spotifyViewModel.RefreshCommand.ExecuteAsync(null);
                    _spotifyViewModel.StartAutoRefresh();
                }
                break;
            }
        }
    }

    /// <summary>
    /// Hides every built-in panel. Optionally restores the browser nav bar.
    /// Does not stop Spotify auto-refresh (mini-bar keeps updating).
    /// </summary>
    private void HideAllBuiltInPanels(bool restoreNavBar = true)
    {
        WeatherPanelInstance.Visibility = Visibility.Collapsed;
        _weatherPanelActive = false;
        HuePanelInstance.Visibility = Visibility.Collapsed;
        _huePanelActive = false;
        ShutterPanelInstance.Visibility = Visibility.Collapsed;
        _shutterPanelActive = false;
        SpotifyPanelInstance.Visibility = Visibility.Collapsed;
        _spotifyPanelActive = false;

        if (restoreNavBar)
            NavBar.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Collapses and optionally suspends the active WebView2 to free Chromium resources.
    /// </summary>
    private async Task DeactivateActiveWebViewAsync()
    {
        if (_activeWebView == null) return;

        _activeWebView.Visibility = Visibility.Collapsed;
        var oldItem = FindShortcutByWebView(_activeWebView);
        if (_activeWebView.CoreWebView2 != null && oldItem is not { KeepInBackground: true })
        {
            try
            {
                await _activeWebView.CoreWebView2.TrySuspendAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TrySuspendAsync failed: {ex.Message}");
            }
        }

        _activeWebView = null;
    }

    private async Task<WebView2> GetOrCreateWebViewAsync(ShortcutItem item)
    {
        if (_webViews.TryGetValue(item.Id, out var existing))
            return existing;

        var webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B),
            Visibility = Visibility.Collapsed,
        };

        WebViewContainer.Children.Add(webView);
        await webView.EnsureCoreWebView2Async(_webViewEnvironment);

        // Apply per-tab user agent
        webView.CoreWebView2.Settings.UserAgent = item.UseDesktopUserAgent ? DesktopUserAgent : MobileUserAgent;

        // Keep new-window links in the same WebView
        webView.CoreWebView2.NewWindowRequested += (s, args) =>
        {
            args.Handled = true;
            webView.CoreWebView2.Navigate(args.Uri);
        };

        // Ad blocking: use fallback AdBlockService only if uBOL extension failed to load
        if (!_extensionService.IsUBlockInstalled)
        {
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Script);
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Image);
            webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            webView.CoreWebView2.WebResourceRequested += (s, args) =>
            {
                if (_adBlockService.IsExempted(webView.CoreWebView2.Source))
                    return;
                if (_adBlockService.ShouldBlock(args.Request.Uri))
                {
                    args.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 403, "Blocked", "");
                }
            };
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(AdBlockService.AdHidingScript);
        }

        // Navigate to the shortcut's URL
        webView.CoreWebView2.Navigate(ConfigService.NormalizeUrl(item.Url));

        _webViews[item.Id] = webView;
        item.IsOpen = true;
        return webView;
    }

    private async Task SwitchToWebViewAsync(string id, WebView2 target)
    {
        if (_activeWebView == target) return;

        HideAllBuiltInPanels(restoreNavBar: true);

        await DeactivateActiveWebViewAsync();

        // Resume and show the new tab
        target.CoreWebView2?.Resume();
        target.Visibility = Visibility.Visible;
        _activeWebView = target;

        // Update LRU order (tracking only — no eviction cap)
        _lruOrder.Remove(id);
        _lruOrder.AddFirst(id);
    }

    private void OnTabRemoved(string shortcutId)
    {
        if (!_webViews.TryGetValue(shortcutId, out var webView)) return;

        _webViews.Remove(shortcutId);
        _lruOrder.Remove(shortcutId);

        if (_activeWebView == webView)
            _activeWebView = null;

        WebViewContainer.Children.Remove(webView);
        webView.Dispose();

        var item = _viewModel.Shortcuts.FirstOrDefault(s => s.Id == shortcutId);
        if (item != null) item.IsOpen = false;
    }

    private void OnNavigateRequested(string url)
    {
        if (_activeWebView?.CoreWebView2 == null) return;

        try
        {
            _activeWebView.CoreWebView2.Navigate(ConfigService.NormalizeUrl(url));
        }
        catch
        {
            // Invalid URL
        }
    }

    private void OnNavigationAction(string action)
    {
        if (_activeWebView?.CoreWebView2 == null) return;

        switch (action)
        {
            case SidebarViewModel.NavBack:
                if (_activeWebView.CanGoBack) _activeWebView.GoBack();
                break;
            case SidebarViewModel.NavForward:
                if (_activeWebView.CanGoForward) _activeWebView.GoForward();
                break;
            case SidebarViewModel.NavRefresh:
                _activeWebView.CoreWebView2.Reload();
                break;
        }
    }

    private void OnWidthChanged(int newWidthWpf)
    {
        bool isOverlay = _viewModel.IsExpanded
            && _viewModel.SelectedShortcut?.OverlayMode == true;

        var dpiScale = GetDpiScale();

        // Collapsing: hide content BEFORE shrinking window
        if (!_viewModel.IsExpanded)
            ContentPanel.Visibility = Visibility.Collapsed;

        if (isOverlay)
        {
            // Reserve only the icon strip; the panel floats over the desktop
            var collapsedPx = (int)(_viewModel.CollapsedWidth * dpiScale);
            _appBarManager.SetPosition(collapsedPx);
            PositionOverlay();
        }
        else
        {
            var widthPx = (int)(newWidthWpf * dpiScale);
            _appBarManager.SetPosition(widthPx);
            // Overlay may have moved the HWND away from the AppBar rect; snap it back
            // when the reserved size did not change (SetPosition no-op).
            if (!_viewModel.IsExpanded)
                _appBarManager.SyncWindowToAppBar();
        }

        // Expanding: show content AFTER window has resized
        if (_viewModel.IsExpanded)
            ContentPanel.Visibility = Visibility.Visible;

        SyncMouseHookForOverlay();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isClosing)
        {
            // Hide to tray instead of closing
            e.Cancel = true;
            Hide();
            _appBarManager.Unregister();
            UninstallMouseHook();
            return;
        }

        CleanupResources();
        _viewModel.SaveConfig();
    }

    /// <summary>
    /// Releases hooks, WebViews, and services. Safe to call multiple times.
    /// </summary>
    public void CleanupResources()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        StopSpotifyMiniMarquees();
        UninstallMouseHook();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _windowSource?.RemoveHook(WindowWndProc);
        _appBarManager.Unregister();

        _viewModel.TabSwitchRequested -= OnTabSwitchRequested;
        _viewModel.TabRemoved -= OnTabRemoved;
        _viewModel.NavigateRequested -= OnNavigateRequested;
        _viewModel.NavigationAction -= OnNavigationAction;
        _viewModel.WidthChanged -= OnWidthChanged;
        _viewModel.ContentDeselected -= OnContentDeselected;
        _viewModel.AdBlockChanged -= OnAdBlockChanged;
        _viewModel.SettingsApplied -= OnSettingsApplied;
        _appBarManager.PositionChanged -= OnAppBarPositionChanged;

        if (_spotifyViewModel != null)
        {
            ((INotifyPropertyChanged)_spotifyViewModel).PropertyChanged -= OnSpotifyMiniPropertyChanged;
            _spotifyViewModel.Dispose();
            _spotifyViewModel = null;
        }

        _shutterViewModel?.Dispose();
        _shutterViewModel = null;

        try
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
            _cts.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _viewModel.CancelPendingOperations();

        foreach (var webView in _webViews.Values)
        {
            WebViewContainer.Children.Remove(webView);
            webView.Dispose();
        }
        _webViews.Clear();
        _lruOrder.Clear();
        _activeWebView = null;
    }

    /// <summary>
    /// Actually closes the window (bypasses hide-to-tray).
    /// </summary>
    public void ForceClose()
    {
        _isClosing = true;
        CleanupResources();
        Close();
    }

    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            _appBarManager.Unregister();
            UninstallMouseHook();
        }
        else
        {
            Show();
            var dpiScale = GetDpiScale();
            var widthPx = (int)(_viewModel.CurrentWidth * dpiScale);
            _appBarManager.Register(this, widthPx, IsDockedLeft());
            if (_viewModel.IsExpanded && _viewModel.SelectedShortcut?.OverlayMode == true)
                ApplyOverlayMode(true);
            else
                SyncMouseHookForOverlay();
        }
    }

    private double GetDpiScale()
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }

    // Button event handlers
    private void BtnSpotifyTab_Click(object sender, RoutedEventArgs e)
    {
        // Prefer the real shortcut so user's selection / ordering stays consistent.
        var spotifyItem = _viewModel.Shortcuts.FirstOrDefault(s => s.BuiltInType == "spotify");
        if (spotifyItem == null)
        {
            // User deleted the Spotify shortcut from the list — keep the button usable with a cached stand-in.
            _spotifyFallbackItem ??= new ShortcutItem { Name = "Spotify", BuiltInType = "spotify" };
            spotifyItem = _spotifyFallbackItem;
        }
        _viewModel.SelectShortcutCommand.Execute(spotifyItem);
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddShortcutDialog { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result != null)
        {
            _viewModel.AddShortcutCommand.Execute(dialog.Result);
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is PowerSideBar.App app)
            app.OpenSettings();
    }
    // ── Drag & drop reorder with visual feedback ──────────

    private FrameworkElement? _currentDropTarget;

    private void Shortcut_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
    }

    private void Shortcut_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var button = (FrameworkElement)sender;
            if (button.DataContext is ShortcutItem item)
            {
                // Dim the dragged item's container
                var container = FindParent<StackPanel>(button);
                if (container != null) container.Opacity = 0.35;

                DragDrop.DoDragDrop(button, new DataObject(DragDropFormat, item), DragDropEffects.Move);

                // Restore opacity after drag ends (DoDragDrop is synchronous)
                if (container != null) container.Opacity = 1.0;
                ClearAllDropIndicators();
            }
        }
    }

    /// <summary>
    /// DragOver on the StackPanel wrapper — shows insertion indicator.
    /// </summary>
    private void ShortcutDrop_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DragDropFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var panel = sender as FrameworkElement;
        if (panel == null) return;

        // Clear previous target indicators
        if (_currentDropTarget != null && _currentDropTarget != panel)
            ClearDropIndicators(_currentDropTarget);

        _currentDropTarget = panel;

        // Determine if cursor is in top or bottom half
        var pos = e.GetPosition(panel);
        bool above = pos.Y < panel.ActualHeight / 2;

        // Show the appropriate indicator line
        ShowDropIndicator(panel, above);
    }

    private void ShortcutDrop_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement panel)
        {
            ClearDropIndicators(panel);
            if (_currentDropTarget == panel)
                _currentDropTarget = null;
        }
    }

    private void ShortcutDrop_Drop(object sender, DragEventArgs e)
    {
        ClearAllDropIndicators();

        if (!e.Data.GetDataPresent(DragDropFormat)) return;

        var draggedItem = (ShortcutItem)e.Data.GetData(DragDropFormat)!;
        var targetItem = ((FrameworkElement)sender).DataContext as ShortcutItem;

        if (targetItem != null && targetItem != draggedItem)
        {
            _viewModel.MoveShortcut(draggedItem, targetItem);
        }
    }

    // ── Drop indicator helpers ──────────────────────────────

    private static void ShowDropIndicator(FrameworkElement container, bool above)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(container))
        {
            if (child is Border b)
            {
                if (b.Tag as string == "DropAbove")
                    b.Visibility = above ? Visibility.Visible : Visibility.Collapsed;
                else if (b.Tag as string == "DropBelow")
                    b.Visibility = above ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }

    private static void ClearDropIndicators(FrameworkElement container)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(container))
        {
            if (child is Border b && (b.Tag as string == "DropAbove" || b.Tag as string == "DropBelow"))
                b.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearAllDropIndicators()
    {
        if (_currentDropTarget != null)
        {
            ClearDropIndicators(_currentDropTarget);
            _currentDropTarget = null;
        }
    }

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T found) return found;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    // Context menu handlers
    private static ShortcutItem? GetShortcutFromMenu(object sender)
    {
        // Walk up the MenuItem/ContextMenu hierarchy to find the PlacementTarget
        if (sender is not MenuItem mi) return null;

        object? current = mi.Parent;
        while (current is MenuItem parentItem)
        {
            current = parentItem.Parent;
        }

        return (current as ContextMenu)?.PlacementTarget is FrameworkElement fe
            ? fe.DataContext as ShortcutItem
            : null;
    }

    private void ShortcutContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu cm || cm.PlacementTarget is not FrameworkElement fe
            || fe.DataContext is not ShortcutItem item) return;

        // Update checkmarks and visibility by walking all MenuItems recursively
        bool isBuiltIn = !string.IsNullOrEmpty(item.BuiltInType);
        foreach (var mi in GetAllMenuItems(cm))
        {
            if (mi.Name == "MenuUaMobile")
            {
                mi.IsChecked = !item.UseDesktopUserAgent;
                if (mi.Parent is MenuItem parent) parent.Visibility = isBuiltIn ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (mi.Name == "MenuUaDesktop") mi.IsChecked = item.UseDesktopUserAgent;
            else if (mi.Name == "MenuKeepBackground")
            {
                mi.IsChecked = item.KeepInBackground;
                mi.Visibility = isBuiltIn ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (mi.Name == "MenuOverlayMode")
            {
                mi.IsChecked = item.OverlayMode;
            }
            else if (mi.Name == "MenuCloseOnCollapse")
            {
                mi.IsChecked = item.CloseOnCollapse;
                mi.Visibility = isBuiltIn ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (mi.Name == "MenuMoveToGroup")
            {
                BuildMoveToGroupSubmenu(mi, item);
            }
        }
    }

    private void BuildMoveToGroupSubmenu(MenuItem parent, ShortcutItem item)
    {
        parent.Items.Clear();

        var groups = _viewModel.GetGroups();

        // "Aucun groupe" option (remove from group)
        var noneItem = new MenuItem
        {
            Header = Loc.T("menu.no_group"),
            IsCheckable = true,
            IsChecked = string.IsNullOrEmpty(item.GroupId),
        };
        noneItem.Click += (_, _) =>
        {
            _viewModel.RemoveShortcutFromGroup(item);
        };
        parent.Items.Add(noneItem);

        if (groups.Count > 0)
            parent.Items.Add(new Separator());

        // One entry per existing group
        foreach (var group in groups)
        {
            var g = group; // capture
            var groupItem = new MenuItem
            {
                Header = g.Name,
                IsCheckable = true,
                IsChecked = item.GroupId == g.Id,
            };
            groupItem.Click += (_, _) =>
            {
                _viewModel.MoveShortcutToGroup(item, g);
            };
            parent.Items.Add(groupItem);
        }

        parent.Items.Add(new Separator());

        // "Nouveau groupe..." option
        var newGroupItem = new MenuItem { Header = Loc.T("menu.new_group") };
        newGroupItem.Click += (_, _) =>
        {
            var dialog = new RenameDialog("") { Owner = this, Title = Loc.T("dialog.new_group") };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultName))
            {
                var newGroup = _viewModel.AddGroup(dialog.ResultName);
                _viewModel.MoveShortcutToGroup(item, newGroup);
            }
        };
        parent.Items.Add(newGroupItem);
    }

    private static IEnumerable<MenuItem> GetAllMenuItems(ItemsControl parent)
    {
        foreach (var mi in parent.Items.OfType<MenuItem>())
        {
            yield return mi;
            foreach (var sub in GetAllMenuItems(mi))
                yield return sub;
        }
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        var dialog = new RenameDialog(item.Name) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            item.Name = dialog.ResultName;
            _viewModel.SaveConfig();
        }
    }

    private void MenuUaMobile_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null || !item.UseDesktopUserAgent) return;

        item.UseDesktopUserAgent = false;
        ApplyUserAgentAndReload(item);
    }

    private void MenuUaDesktop_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null || item.UseDesktopUserAgent) return;

        item.UseDesktopUserAgent = true;
        ApplyUserAgentAndReload(item);
    }

    private void MenuKeepBackground_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        item.KeepInBackground = !item.KeepInBackground;
        _viewModel.SaveConfig();
    }

    private void MenuOverlayMode_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        item.OverlayMode = !item.OverlayMode;
        _viewModel.SaveConfig();

        // Apply immediately if this is the active tab
        if (_viewModel.SelectedShortcut == item && _viewModel.IsExpanded)
        {
            ApplyOverlayMode(item.OverlayMode);
        }
    }

    private void MenuCloseOnCollapse_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        item.CloseOnCollapse = !item.CloseOnCollapse;
        _viewModel.SaveConfig();
    }

    private void ApplyOverlayMode(bool overlay)
    {
        var dpiScale = GetDpiScale();

        if (overlay && _viewModel.IsExpanded)
        {
            // AppBar reserves only icon strip width, panel floats on top
            var collapsedPx = (int)(_viewModel.CollapsedWidth * dpiScale);
            _appBarManager.SetPosition(collapsedPx);
            PositionOverlay();
        }
        else
        {
            // AppBar reserves the full sidebar width (push mode)
            var widthPx = (int)(_viewModel.CurrentWidth * dpiScale);
            if (!_appBarManager.IsRegistered)
                _appBarManager.Register(this, widthPx, IsDockedLeft());
            else
                _appBarManager.SetPosition(widthPx);
            _appBarManager.SyncWindowToAppBar();
        }

        SyncMouseHookForOverlay();
    }

    private void OnAppBarPositionChanged()
    {
        // After AppBar repositions the window (e.g. POSCHANGED), reapply overlay if needed
        if (_viewModel.IsExpanded && _viewModel.SelectedShortcut?.OverlayMode == true)
        {
            PositionOverlay();
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke((Action)RefreshSidebarPlacement, DispatcherPriority.Background);
    }

    private IntPtr WindowWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            RefreshSidebarPlacement();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_DPICHANGED)
        {
            var suggestedRect = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                suggestedRect.Left, suggestedRect.Top,
                suggestedRect.Right - suggestedRect.Left, suggestedRect.Bottom - suggestedRect.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            RefreshSidebarPlacement();
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void RefreshSidebarPlacement()
    {
        if (!_appBarManager.IsRegistered)
            return;

        var dpiScale = GetDpiScale();
        var appBarWidthPx = _viewModel.IsExpanded && _viewModel.SelectedShortcut?.OverlayMode == true
            ? (int)(_viewModel.CollapsedWidth * dpiScale)
            : (int)(_viewModel.CurrentWidth * dpiScale);

        _appBarManager.SetPosition(appBarWidthPx);

        if (_viewModel.IsExpanded && _viewModel.SelectedShortcut?.OverlayMode == true)
            PositionOverlay();
    }

    private static NativeMethods.RECT GetPrimaryMonitorBoundsPx()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };

            if (NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
                return monitorInfo.rcMonitor;
        }

        return new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight,
        };
    }

    private static NativeMethods.RECT GetPrimaryWorkAreaPx()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = 0, Y = 0 }, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
        {
            return new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = (int)SystemParameters.PrimaryScreenWidth,
                Bottom = (int)SystemParameters.PrimaryScreenHeight,
            };
        }

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            return new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = (int)SystemParameters.PrimaryScreenWidth,
                Bottom = (int)SystemParameters.PrimaryScreenHeight,
            };
        }

        return monitorInfo.rcWork;
    }

    private void PositionOverlay()
    {
        // Float over the desktop, flush to the physical dock edge of the monitor.
        // Must NOT use rcWork edges — they already exclude our AppBar strip,
        // which left a permanent empty gap the size of the reserved bar.
        var dpiScale = GetDpiScale();
        var widthPx = (int)(_viewModel.CurrentWidth * dpiScale);
        var monitor = GetPrimaryMonitorBoundsPx();
        var workArea = GetPrimaryWorkAreaPx();
        var left = IsDockedLeft() ? monitor.Left : monitor.Right - widthPx;
        var top = workArea.Top;
        var height = Math.Max(0, workArea.Bottom - workArea.Top);

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
            left, top,
            widthPx, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private ShortcutItem? FindShortcutByWebView(WebView2 webView)
    {
        var id = _webViews.FirstOrDefault(kv => kv.Value == webView).Key;
        return id != null ? _viewModel.Shortcuts.FirstOrDefault(s => s.Id == id) : null;
    }

    private void ApplyUserAgentAndReload(ShortcutItem item)
    {
        if (_webViews.TryGetValue(item.Id, out var webView) && webView.CoreWebView2 != null)
        {
            webView.CoreWebView2.Settings.UserAgent = item.UseDesktopUserAgent ? DesktopUserAgent : MobileUserAgent;
            webView.CoreWebView2.Reload();
        }
        _viewModel.SaveConfig();
    }

    private void MenuCloseTab_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        // Built-in panels: just hide, no WebView2 to dispose
        if (!string.IsNullOrEmpty(item.BuiltInType))
        {
            var closingActive =
                (_weatherPanelActive && item.BuiltInType == "weather") ||
                (_huePanelActive && item.BuiltInType == "hue") ||
                (_shutterPanelActive && item.BuiltInType == "shutter") ||
                (_spotifyPanelActive && item.BuiltInType == "spotify");

            if (closingActive)
                HideAllBuiltInPanels(restoreNavBar: true);

            item.IsOpen = false;
            item.IsSelected = false;
            if (_viewModel.SelectedShortcut == item)
            {
                _viewModel.SelectedShortcut = null;
                if (_viewModel.IsExpanded)
                    _viewModel.ToggleExpandCommand.Execute(null);
            }
            return;
        }

        // Dispose the WebView2 but keep the shortcut in the list
        DisposeWebView(item);

        // Deselect and collapse
        item.IsSelected = false;
        if (_viewModel.SelectedShortcut == item)
        {
            _viewModel.SelectedShortcut = null;
            if (_viewModel.IsExpanded)
                _viewModel.ToggleExpandCommand.Execute(null);
        }
    }

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = GetShortcutFromMenu(sender);
        if (item == null) return;

        var result = MessageBox.Show(
            Loc.Tf("confirm.delete_shortcut", item.Name),
            Loc.T("confirm.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.RemoveShortcutCommand.Execute(item);
        }
    }

    // ── Group header handlers ──────────────────────────────

    private void GroupHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _suppressNextGroupClick = false;
    }

    private void GroupHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var diff = _dragStartPoint - e.GetPosition(null);
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ShortcutGroup group)
            {
                _suppressNextGroupClick = true;
                fe.Opacity = 0.35;

                DragDrop.DoDragDrop(fe, new DataObject(GroupDragDropFormat, group), DragDropEffects.Move);

                fe.Opacity = 1.0;
                if (fe is Border draggedBorder)
                    draggedBorder.Background = Brushes.Transparent;
                ClearAllDropIndicators();
            }
        }
    }

    private void GroupHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (_suppressNextGroupClick)
        {
            _suppressNextGroupClick = false;
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is ShortcutGroup group)
        {
            _viewModel.ToggleGroupExpanded(group);
        }
    }

    private void GroupHeader_DragOver(object sender, DragEventArgs e)
    {
        var isShortcut = e.Data.GetDataPresent(DragDropFormat);
        var isGroup = e.Data.GetDataPresent(GroupDragDropFormat);
        if (!isShortcut && !isGroup)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // Clear shortcut drop indicators
        ClearAllDropIndicators();

        // Highlight the group header
        if (sender is Border border)
            border.Background = GroupHighlightBrush;
    }

    private void GroupHeader_DragLeave(object sender, DragEventArgs e)
    {
        // Reset group header highlight
        if (sender is Border border)
            border.Background = Brushes.Transparent;
    }

    private void GroupHeader_Drop(object sender, DragEventArgs e)
    {
        // Reset highlight
        if (sender is Border border)
            border.Background = Brushes.Transparent;

        ClearAllDropIndicators();

        if (sender is not FrameworkElement fe || fe.DataContext is not ShortcutGroup targetGroup)
            return;

        if (e.Data.GetDataPresent(GroupDragDropFormat))
        {
            var draggedGroup = (ShortcutGroup)e.Data.GetData(GroupDragDropFormat)!;
            if (draggedGroup != targetGroup)
                _viewModel.MoveGroup(draggedGroup, targetGroup);
            return;
        }

        if (!e.Data.GetDataPresent(DragDropFormat)) return;

        var draggedItem = (ShortcutItem)e.Data.GetData(DragDropFormat)!;
        _viewModel.MoveShortcutToGroup(draggedItem, targetGroup);
    }

    private static ShortcutGroup? GetGroupFromMenu(object sender)
    {
        if (sender is not MenuItem mi) return null;

        object? current = mi.Parent;
        while (current is MenuItem parentItem)
            current = parentItem.Parent;

        return (current as ContextMenu)?.PlacementTarget is FrameworkElement fe
            ? fe.DataContext as ShortcutGroup
            : null;
    }

    private void MenuRenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupFromMenu(sender);
        if (group == null) return;

        var dialog = new RenameDialog(group.Name) { Owner = this, Title = Loc.T("dialog.rename_group") };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ResultName))
        {
            _viewModel.RenameGroup(group, dialog.ResultName);
            _viewModel.RebuildDisplayItems();
        }
    }

    private void MenuDeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetGroupFromMenu(sender);
        if (group == null) return;

        var result = MessageBox.Show(
            Loc.Tf("confirm.delete_group", group.Name),
            Loc.T("confirm.title"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _viewModel.RemoveGroup(group);
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _viewModel.GoBackCommand.Execute(null);
    private void BtnForward_Click(object sender, RoutedEventArgs e) => _viewModel.GoForwardCommand.Execute(null);
    private void BtnHome_Click(object sender, RoutedEventArgs e) => _viewModel.GoHomeCommand.Execute(null);
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshCommand.Execute(null);

    // Close overlay panel on click outside
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // This handler is no longer used; mouse clicks are handled by the global mouse hook
    }

    private void SyncMouseHookForOverlay()
    {
        bool needHook = IsVisible
            && _viewModel.IsExpanded
            && _viewModel.SelectedShortcut?.OverlayMode == true;

        if (needHook)
            InstallMouseHook();
        else
            UninstallMouseHook();
    }

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
            return; // Already installed

        _mouseHookProc = MouseHookCallback;
        
        // Get the module handle for the current process
        var module = typeof(SidebarWindow).Module;
        var moduleHandle = System.Runtime.InteropServices.Marshal.GetHINSTANCE(module);

        _mouseHookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL,
            _mouseHookProc,
            moduleHandle,
            0);

        // If hook installation failed, log it but don't crash
        if (_mouseHookHandle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("Failed to install global mouse hook");
        }
    }

    private void UninstallMouseHook()
    {
        if (_mouseHookHandle == IntPtr.Zero)
            return;

        NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
        _mouseHookHandle = IntPtr.Zero;
        _mouseHookProc = null;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN) && !_closingOverlay)
        {
            var selectedItem = _viewModel.SelectedShortcut;
            if (selectedItem?.OverlayMode == true && _viewModel.IsExpanded)
            {
                try
                {
                    // Get window bounds
                    var hwnd = new WindowInteropHelper(this).Handle;
                    if (NativeMethods.GetWindowRect(hwnd, out var windowRect))
                    {
                        // Get mouse position
                        var hookStruct = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                        var mouseX = hookStruct.pt.X;
                        var mouseY = hookStruct.pt.Y;

                        // Check if click is outside window bounds
                        if (mouseX < windowRect.Left || mouseX >= windowRect.Right ||
                            mouseY < windowRect.Top || mouseY >= windowRect.Bottom)
                        {
                            // Click was outside — queue UI work without blocking the hook thread
                            _closingOverlay = true;
                            Dispatcher.BeginInvoke(() =>
                            {
                                try
                                {
                                    var current = _viewModel.SelectedShortcut;
                                    if (current?.OverlayMode != true || !_viewModel.IsExpanded)
                                        return;

                                    current.IsSelected = false;
                                    _viewModel.SelectedShortcut = null;
                                    if (_viewModel.IsExpanded)
                                        _viewModel.ToggleExpandCommand.Execute(null);
                                }
                                finally
                                {
                                    _closingOverlay = false;
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Mouse hook callback error: {ex.Message}");
                    _closingOverlay = false;
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // Resize grip handlers
    private bool _isResizing;
    private double _resizeStartX;
    private int _resizeStartWidth;

    private void ResizeGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isResizing = true;
        _resizeStartX = PointToScreen(e.GetPosition(this)).X;
        _resizeStartWidth = _viewModel.CurrentWidth;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void ResizeGrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isResizing) return;

        var currentX = PointToScreen(e.GetPosition(this)).X;
        var dpiScale = GetDpiScale();
        // Right dock: drag left = wider. Left dock: drag right = wider.
        var deltaWpf = IsDockedLeft()
            ? (int)((currentX - _resizeStartX) / dpiScale)
            : (int)((_resizeStartX - currentX) / dpiScale);
        var newWidth = Math.Clamp(_resizeStartWidth + deltaWpf, 200, 1200);

        // In overlay mode, also reposition the window (SetCurrentTabWidth fires WidthChanged)
        _viewModel.SetCurrentTabWidth(newWidth);
    }

    private void ResizeGrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        ((UIElement)sender).ReleaseMouseCapture();
        _viewModel.SaveConfig();
    }
}
