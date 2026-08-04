using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Vantage.App.ViewModels;

namespace Vantage.App;

public partial class MainWindow : Window
{
    private const int WM_DISPLAYCHANGE = 0x007E;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2; // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private readonly DispatcherTimer _displayChangeDebounce;

    public MainWindow()
    {
        InitializeComponent();

        // Display topology changes settle asynchronously (BLUEPRINT P7) —
        // debounce the burst of WM_DISPLAYCHANGE messages into one refresh.
        _displayChangeDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _displayChangeDebounce.Tick += async (_, _) =>
        {
            _displayChangeDebounce.Stop();
            if (DataContext is MainViewModel vm)
                await vm.RefreshCommand.ExecuteAsync(null);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        source.AddHook(WndProc);

        // Native Windows frame, themed: dark title bar + system caption buttons + Mica.
        // No custom chrome — the min/max/close buttons are the OS's own.
        var hwnd = source.Handle;
        var dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        if (Vantage.Interop.WindowsVersion.IsWindows11OrGreater)
        {
            var backdrop = DWMSBT_MAINWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DISPLAYCHANGE)
        {
            _displayChangeDebounce.Stop();
            _displayChangeDebounce.Start();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tray-first app: closing the window hides it, the tray keeps Vantage alive.
        e.Cancel = true;
        Hide();
    }
}
