using System.Runtime.InteropServices;

namespace Nexora.Platform.Windows.Shell;

public enum TrayCommand
{
    Open = 1,
    PerformanceMonitor = 2,
    NetworkMonitor = 3,
    QuickOptimize = 4,
    Exit = 5,
}

/// <summary>
/// Native notification-area icon. WinUI 3 has no tray API, so this uses Shell_NotifyIcon with a hidden
/// window. Must be created on the UI thread (its messages are pumped by the WinUI message loop).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000 + 1;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_NULL = 0x0000;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

    private static int _classCounter;
    private readonly WndProc _wndProc;
    private readonly uint _taskbarCreated;
    private readonly string _className;
    private IntPtr _window;
    private IntPtr _icon;
    private string _tooltip;
    private bool _added;

    public TrayIcon(string iconPath, string tooltip)
    {
        _tooltip = tooltip;
        _wndProc = WindowProc;
        _className = "NEXORA.TrayWindow." + Interlocked.Increment(ref _classCounter);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (RegisterClassExW(ref wc) == 0)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        _window = CreateWindowExW(0, _className, "NEXORA tray", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        var size = GetSystemMetrics(49 /* SM_CXSMICON */);
        _icon = File.Exists(iconPath) ? LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE) : IntPtr.Zero;
        if (_icon == IntPtr.Zero)
        {
            _icon = LoadIconW(IntPtr.Zero, (IntPtr)32512 /* IDI_APPLICATION */);
        }
    }

    public event EventHandler<TrayCommand>? CommandInvoked;

    public bool IsVisible => _added;

    public void Show()
    {
        if (_added)
        {
            return;
        }

        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(0);
        _ = Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (_added)
        {
            var data = CreateData(NIF_TIP);
            _ = Shell_NotifyIconW(NIM_MODIFY, ref data);
        }
    }

    /// <summary>Shows a standard balloon/toast from the tray icon (used as a fallback for notifications).</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = 0x1; // NIIF_INFO
        _ = Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = _icon,
        szTip = _tooltip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_APP_TRAY)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse is WM_LBUTTONUP or WM_LBUTTONDBLCLK)
            {
                CommandInvoked?.Invoke(this, TrayCommand.Open);
            }
            else if (mouse == WM_RBUTTONUP)
            {
                ShowMenu();
            }

            return IntPtr.Zero;
        }

        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: re-add the icon.
            _added = false;
            Show();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            _ = AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Open, "Open NEXORA");
            _ = AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            _ = AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.PerformanceMonitor, "Performance Monitor");
            _ = AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.NetworkMonitor, "Network Monitor");
            _ = AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.QuickOptimize, "Quick Optimize");
            _ = AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            _ = AppendMenuW(menu, MF_STRING, (UIntPtr)(uint)TrayCommand.Exit, "Exit");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(_window);
            var command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, point.X, point.Y, _window, IntPtr.Zero);
            _ = PostMessageW(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command > 0 && Enum.IsDefined(typeof(TrayCommand), command))
            {
                CommandInvoked?.Invoke(this, (TrayCommand)command);
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        Hide();
        if (_window != IntPtr.Zero)
        {
            _ = DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            _ = DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpmParams);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
