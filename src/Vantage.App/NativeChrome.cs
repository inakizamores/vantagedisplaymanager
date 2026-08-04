using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Vantage.App;

/// <summary>Applies the native Win11 frame treatment (immersive dark title bar + Mica) to any window.</summary>
public static class NativeChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call from OnSourceInitialized (the HWND must exist).</summary>
    public static void Apply(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
            return;

        var dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(source.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (Vantage.Interop.WindowsVersion.IsWindows11OrGreater)
        {
            var backdrop = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(source.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
    }
}
