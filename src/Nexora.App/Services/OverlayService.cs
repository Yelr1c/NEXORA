using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Windows.Graphics;
using Windows.UI;

namespace Nexora.App.Services;

/// <summary>
/// Small always-on-top performance overlay (FPS, frame time, CPU, GPU, ping) for borderless/windowed games.
/// It never takes focus, is hidden from Alt+Tab, and samples only while visible.
/// </summary>
public sealed class OverlayService(MetricsMonitor monitor, NetworkMonitor network, IFrameRateSource frames, UiDispatcher dispatcher, ILogger<OverlayService> logger)
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private Window? _window;
    private IDisposable? _keepAlive;
    private TextBlock? _fps;
    private TextBlock? _detail;
    private bool _closing;

    public event EventHandler? VisibilityChanged;

    public bool IsVisible { get; private set; }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    public void Show()
    {
        if (IsVisible)
        {
            return;
        }

        try
        {
            _window ??= Create();
            Position(_window);
            _window.AppWindow.Show(false);
            IsVisible = true;
            _keepAlive ??= monitor.RequestActive();
            monitor.SnapshotAvailable += OnSnapshot;
            if (frames.Status == FrameCaptureStatus.Stopped)
            {
                frames.TryStart();
            }

            if (monitor.Latest is { } latest)
            {
                Update(latest);
            }

            VisibilityChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Overlay could not be shown");
            App.ReportBackgroundError("show the overlay", ex);
        }
    }

    public void Hide()
    {
        if (!IsVisible)
        {
            return;
        }

        monitor.SnapshotAvailable -= OnSnapshot;
        _keepAlive?.Dispose();
        _keepAlive = null;
        _window?.AppWindow.Hide();
        IsVisible = false;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Shutdown()
    {
        Hide();
        _closing = true;
        _window?.Close();
        _window = null;
    }

    private Window Create()
    {
        _fps = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(255, 0x2B, 0xD0, 0x7F)), Text = "—" };
        var fpsLabel = new TextBlock { Text = "FPS", FontSize = 10, FontWeight = FontWeights.SemiBold, Opacity = 0.7, Foreground = new SolidColorBrush(Colors.White), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 5) };
        _detail = new TextBlock { FontSize = 11, LineHeight = 15, Foreground = new SolidColorBrush(Color.FromArgb(230, 0xE6, 0xED, 0xF3)), VerticalAlignment = VerticalAlignment.Center };

        var fpsRow = new StackPanel { Orientation = Orientation.Horizontal };
        fpsRow.Children.Add(_fps);
        fpsRow.Children.Add(fpsLabel);

        var accent = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 10, 2) };
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x1F, 0xB6, 0xFF), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(255, 0x7C, 0x6C, 0xFF), Offset = 1 });
        accent.Background = gradient;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(accent);
        row.Children.Add(fpsRow);
        row.Children.Add(_detail);

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x0E, 0x14)),
            Padding = new Thickness(10, 6, 14, 6),
            Children = { row },
        };
        root.RequestedTheme = ElementTheme.Dark;

        var window = new Window { Title = "NEXORA Overlay", Content = root };
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Closing += (_, e) =>
        {
            if (!_closing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        return window;
    }

    private static void Position(Window window)
    {
        var area = DisplayArea.Primary;
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)) / 96.0;
        var width = (int)(340 * scale);
        var height = (int)(52 * scale);
        window.AppWindow.MoveAndResize(new RectInt32(area.WorkArea.X + (int)(16 * scale), area.WorkArea.Y + (int)(16 * scale), width, height));
    }

    private void OnSnapshot(object? sender, MetricSnapshot snapshot) => dispatcher.Post(() => Update(snapshot));

    private void Update(MetricSnapshot s)
    {
        if (_fps is null || _detail is null)
        {
            return;
        }

        _fps.Text = s.Fps is { } fps ? fps.ToString("0", CultureInfo.CurrentCulture) : frames.Status == FrameCaptureStatus.RequiresElevation ? "admin" : "—";
        var ping = network.GetRecentStatistics(5).AverageMs;
        _detail.Text =
            $"CPU {Fmt(s.CpuUsagePercent)}   GPU {Fmt(s.GpuUsagePercent)}   RAM {Fmt(s.RamTotalBytes > 0 ? s.RamUsagePercent : null)}\n" +
            $"{(s.FrameTimeMs is { } ft ? ft.ToString("0.0", CultureInfo.CurrentCulture) + " ms" : "— ms")}   Ping {(ping is { } p ? p.ToString("0", CultureInfo.CurrentCulture) + " ms" : "—")}";
    }

    private static string Fmt(double? value) => value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + "%" : "—";

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
