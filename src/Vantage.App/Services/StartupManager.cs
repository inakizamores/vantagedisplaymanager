using Microsoft.Win32;

namespace Vantage.App.Services;

/// <summary>
/// "Start with Windows" via the per-user Run key (HKCU — no admin, no scheduled tasks).
/// The registered command always carries <c>--tray</c> so sign-in launches are
/// lightweight: tray icon only, no window, nothing heavy on the login path (BLUEPRINT P6).
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Vantage";

    /// <summary>Command-line switch that makes the app start hidden in the tray.</summary>
    public const string TrayArgument = "--tray";

    private static string DesiredCommand => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, DesiredCommand);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Keeps the registered path honest: if startup is enabled but the exe moved
    /// (portable unzip relocated, install migrated), rewrite it to the current location.
    /// Called once per launch.
    /// </summary>
    public static void ReconcileOnLaunch()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is string existing &&
                !string.Equals(existing, DesiredCommand, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, DesiredCommand);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Non-fatal — worst case the old path stays until the user re-toggles.
        }
    }
}
