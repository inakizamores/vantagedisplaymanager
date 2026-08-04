using System.Windows;
using H.NotifyIcon;
using Vantage.App.ViewModels;
using Vantage.Core.Services;

namespace Vantage.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;

    public MainViewModel ViewModel { get; private set; } = null!;
    public MainWindow? MainAppWindow { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Velopack install/update/uninstall hooks — must run first (no-op outside an installed context).
        Velopack.VelopackApp.Build().Run();

        _singleInstanceMutex = new Mutex(true, @"Local\VantageDisplayManager", out var createdNew);
        if (!createdNew)
        {
            // TODO(M1): forward args over a named pipe and foreground the running instance.
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // Follow the OS light/dark theme, then apply the user's exact accent palette
        // from Windows personalization (not the library's approximation).
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme(false);
        WindowsAccent.Apply();

        // If "start with Windows" is on, keep the registered exe path current.
        Vantage.App.Services.StartupManager.ReconcileOnLaunch();

        var displayService = new DisplayService();
        var store = new ProfileStore();
        ViewModel = new MainViewModel(displayService, store, new ApplyEngine(displayService));

        CreateTrayIcon();

        // --tray (used by the sign-in Run entry) starts lightweight: tray icon only.
        if (!e.Args.Contains(Vantage.App.Services.StartupManager.TrayArgument))
            ShowMainWindow();
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Vantage Display Manager",
            IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/vantage.ico")),
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ShowMainWindow();
        _trayIcon.ContextMenu = BuildTrayMenu();
        _trayIcon.ContextMenu.Opened += (_, _) => RefreshTrayProfiles();
        _trayIcon.ForceCreate();
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var open = new System.Windows.Controls.MenuItem { Header = "Open Vantage" };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);
        menu.Items.Add(new System.Windows.Controls.Separator());
        // Profile items are refreshed on open; keep index stable relative to trailing items.
        menu.Items.Add(new System.Windows.Controls.Separator());
        var exit = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApplication();
        menu.Items.Add(exit);
        return menu;
    }

    private void RefreshTrayProfiles()
    {
        if (_trayIcon?.ContextMenu is not { } menu)
            return;

        // Remove old profile entries (everything between the two separators).
        var separators = menu.Items.OfType<System.Windows.Controls.Separator>().ToList();
        if (separators.Count < 2)
            return;
        var first = menu.Items.IndexOf(separators[0]);
        var second = menu.Items.IndexOf(separators[1]);
        for (var i = second - 1; i > first; i--)
            menu.Items.RemoveAt(i);

        var insertAt = first + 1;
        var profiles = ViewModel.Profiles.ToList();
        if (profiles.Count == 0)
        {
            menu.Items.Insert(insertAt, new System.Windows.Controls.MenuItem
            {
                Header = "No profiles yet",
                IsEnabled = false,
            });
            return;
        }

        foreach (var profile in profiles)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = profile.Name,
                IsChecked = profile.IsActive,
                IsEnabled = profile.IsPossible,
            };
            var captured = profile;
            item.Click += async (_, _) => await ViewModel.ApplyProfileCommand.ExecuteAsync(captured);
            menu.Items.Insert(insertAt++, item);
        }
    }

    public void ShowMainWindow()
    {
        if (MainAppWindow is null)
        {
            MainAppWindow = new MainWindow { DataContext = ViewModel };
            MainAppWindow.Closed += (_, _) => MainAppWindow = null;
            MainAppWindow.Show();
        }
        else
        {
            MainAppWindow.Show();
            if (MainAppWindow.WindowState == WindowState.Minimized)
                MainAppWindow.WindowState = WindowState.Normal;
            MainAppWindow.Activate();
        }
    }

    public void ExitApplication()
    {
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
