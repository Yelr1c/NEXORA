using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Nexora.App;

/// <summary>
/// Custom entry point: enforces a single NEXORA instance. A second launch (Start menu, tray, startup)
/// redirects its activation to the running instance, which brings its window to the front.
/// </summary>
public static class Program
{
    private const string InstanceKey = "NEXORA-main-instance";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (TryRedirectToExistingInstance(args))
        {
            return 0;
        }

        Application.Start(callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(args);
        });
        return 0;
    }

    private static bool TryRedirectToExistingInstance(string[] args)
    {
        var elevatedRestart = args.Contains("--elevated-restart", StringComparer.OrdinalIgnoreCase);
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        // After "Restart as administrator" the previous instance may still be shutting down; wait for it.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!keyInstance.IsCurrent && elevatedRestart && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        }

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += (_, e) => App.OnRedirectedActivation(e);
            return false;
        }

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var done = CreateEventW(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            try
            {
                keyInstance.RedirectActivationToAsync(activation).AsTask().Wait(TimeSpan.FromSeconds(10));
            }
            finally
            {
                SetEvent(done);
            }
        });
        _ = CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        CloseHandle(done);

        try
        {
            using var process = Process.GetProcessById((int)keyInstance.ProcessId);
            AllowSetForegroundWindow((int)process.Id);
        }
        catch (ArgumentException)
        {
            // The other instance exited meanwhile.
        }

        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong handleCount, IntPtr[] handles, out uint index);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
