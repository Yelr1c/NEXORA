using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexora.App.Services;
using Nexora.App.ViewModels;
using Nexora.Core.Monitoring;
using Nexora.Core.Settings;
using Windows.Graphics;
using Windows.UI;

namespace Nexora.App;

public sealed partial class MainWindow : Window
{
    private readonly NavigationService _navigation;
    private readonly ThemeService _theme;
    private readonly DialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly MetricsMonitor _monitor;
    private readonly ILogger<MainWindow> _logger;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _syncingSelection;

    public MainWindow(ShellViewModel shell, NavigationService navigation, ThemeService theme, DialogService dialogs, ISettingsService settings, MetricsMonitor monitor, ILogger<MainWindow> logger)
    {
        Shell = shell;
        _navigation = navigation;
        _theme = theme;
        _dialogs = dialogs;
        _settings = settings;
        _monitor = monitor;
        _logger = logger;
        InitializeComponent();
    }

    public ShellViewModel Shell { get; }

    public bool TrayAvailable { get; set; }

    /// <summary>True once the window has been shown at least once (or started hidden in the tray on purpose).</summary>
    public bool HasBeenShown { get; set; }

    public Action<string, string>? TrayBalloon { get; set; }

    public void Initialize()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.Title = "NEXORA";
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Nexora.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;
        }

        SizeAndCenter(1380, 880);

        _theme.Attach(RootGrid);
        _theme.Apply(_settings.Current);
        _navigation.AnimationsEnabled = _settings.Current.AnimationsEnabled;
        ApplyBackdrop(_settings.Current.HardwareAcceleration);
        UpdateCaptionButtons();
        RootGrid.ActualThemeChanged += (_, _) => UpdateCaptionButtons();
        RootGrid.Loaded += (_, _) => _dialogs.Attach(RootGrid.XamlRoot);
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarLayout();

        _navigation.Initialize(ContentFrame);
        _navigation.Navigated += (_, key) => SyncSelection(key);

        AppWindow.Closing += OnClosing;
        AppWindow.Changed += OnAppWindowChanged;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _monitor.SetForeground(true);
            }
        };
    }

    public void NavigateTo(string key, object? parameter = null)
    {
        ShowNavigation(key != "welcome" && (key != "scan" || _settings.Current.FirstRunCompleted || parameter is not "first-run"));
        _navigation.Navigate(key, parameter);
    }

    public void ShowNavigation(bool visible)
    {
        NavView.IsPaneVisible = visible;
        IdentityChips.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowAndActivate()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
        HasBeenShown = true;
        _ = SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _monitor.SetForeground(true);
        Shell.AppStatus = "Monitoring";
    }

    public void HideToTray()
    {
        AppWindow.Hide();
        _monitor.SetForeground(false);
        Shell.AppStatus = _settings.Current.MonitorInBackground ? "Monitoring in background" : "Idle in tray";
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            TrayBalloon?.Invoke("NEXORA is still running", "NEXORA is in the notification area. Right-click the icon for quick actions or to exit.");
        }
    }

    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    public void ApplyBackdrop(bool enabled)
    {
        if (enabled && MicaController.IsSupported())
        {
            if (SystemBackdrop is not MicaBackdrop)
            {
                SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            }

            SolidBackground.Visibility = Visibility.Collapsed;
        }
        else
        {
            SystemBackdrop = null;
            SolidBackground.Visibility = Visibility.Visible;
        }
    }

    public void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    /// <summary>Lets the user pick a game executable. Uses the Windows App SDK picker, which also works when elevated.</summary>
    public async Task<string?> PickExecutableAsync()
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
                ViewMode = Microsoft.Windows.Storage.Pickers.PickerViewMode.List,
            };
            picker.FileTypeFilter.Add(".exe");
            var result = await picker.PickSingleFileAsync();
            return result?.Path;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "File picker failed");
            ShowError("File picker unavailable", ex.Message);
            return null;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (args.SelectedItemContainer?.Tag is string key)
        {
            _navigation.Navigate(key);
        }
    }

    private void SyncSelection(string key)
    {
        _syncingSelection = true;
        try
        {
            var item = NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key);
            NavView.SelectedItem = item;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
        {
            return;
        }

        if (_settings.Current.CloseToTray && TrayAvailable)
        {
            args.Cancel = true;
            HideToTray();
            return;
        }

        args.Cancel = true;
        App.Shutdown();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange || args.DidSizeChange)
        {
            if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                if (_settings.Current.MinimizeToTray && TrayAvailable)
                {
                    HideToTray();
                }
                else
                {
                    _monitor.SetForeground(false);
                }
            }
        }

        if (args.DidVisibilityChange && sender.IsVisible)
        {
            _monitor.SetForeground(true);
        }
    }

    private void SizeAndCenter(int width, int height)
    {
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var w = (int)Math.Min(width * scale, area.WorkArea.Width * 0.94);
        var h = (int)Math.Min(height * scale, area.WorkArea.Height * 0.94);
        AppWindow.MoveAndResize(new RectInt32(area.WorkArea.X + ((area.WorkArea.Width - w) / 2), area.WorkArea.Y + ((area.WorkArea.Height - h) / 2), w, h));
    }

    private void UpdateTitleBarLayout()
    {
        var scale = AppTitleBar.XamlRoot?.RasterizationScale ?? 1.0;
        CaptionColumn.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale) + 8);
        IdentityChips.Visibility = AppTitleBar.ActualWidth > 1100 && NavView.IsPaneVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCaptionButtons()
    {
        var dark = RootGrid.ActualTheme != ElementTheme.Light;
        var foreground = dark ? Colors.White : Color.FromArgb(255, 0x10, 0x18, 0x20);
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0, 0, 0);
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0, 0, 0);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
