using System.Runtime.InteropServices;
using System.Text;

namespace Nexora.App;

/// <summary>
/// Last line of defence during startup: a failure before the window is visible must never leave an invisible
/// process behind (which would also swallow every later launch via single-instance redirection).
/// Writes a crash report, tells the user, and exits.
/// </summary>
internal static class StartupGuard
{
    public static string CrashFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXORA", "startup-error.txt");

    public static void Fatal(string stage, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashFile)!);
            var report = new StringBuilder()
                .AppendLine($"NEXORA failed to start ({stage}) at {DateTimeOffset.Now:O}")
                .AppendLine($"Version {typeof(StartupGuard).Assembly.GetName().Version} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}")
                .AppendLine()
                .AppendLine(exception.ToString());
            File.WriteAllText(CrashFile, report.ToString());
        }
        catch (Exception)
        {
            // Nothing more we can do.
        }

        _ = MessageBoxW(IntPtr.Zero,
            $"NEXORA could not start.\n\n{exception.GetType().Name}: {exception.Message}\n\nA report was saved to:\n{CrashFile}",
            "NEXORA", 0x10 /* MB_ICONERROR */);
        Environment.Exit(1);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
