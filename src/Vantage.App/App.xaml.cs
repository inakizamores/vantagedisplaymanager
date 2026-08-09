using System.Windows;
using H.NotifyIcon;
using Vantage.App.Services;
using Vantage.App.ViewModels;
using Vantage.Core.Services;

namespace Vantage.App;

public partial class App : Application
{
    private readonly Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private Vantage.App.Services.HotkeyService? _hotkeys;

    public MainViewModel ViewModel { get; private set; } = null!;
    public MainWindow? MainAppWindow { get; private set; }

    /// <summary>
    /// Launch decisions — single instance, and the preset-shortcut fast paths — are made in
    /// <see cref="Program"/> before WPF is loaded. By the time this runs we are the real,
    /// windowed instance; the mutex is handed over so it lives exactly as long as we do.
    /// </summary>
    public App(Mutex? singleInstanceMutex) => _singleInstanceMutex = singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var command = LaunchCommand.Parse(e.Args);

        base.OnStartup(e);

        // A tray-resident app that dies takes its hotkeys and shortcut fast path with it, so
        // an exception that escapes a dispatcher callback is logged and announced instead of
        // handed to the .NET crash dialog. AppDomain/task handlers are wired in Program.Main.
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Unhandled", args.Exception, "DispatcherUnhandledException");
            NotifyEvenIfHidden("Something went wrong",
                $"{args.Exception.Message} — details were written to the log in Documents\\Vantage Display Manager.",
                Wpf.Ui.Controls.InfoBarSeverity.Error);
            args.Handled = true;
        };

        // Follow the OS light/dark theme, then apply the user's exact accent palette
        // from Windows personalization (not the library's approximation).
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme(false);
        WindowsAccent.Apply();

        // And keep following them. A tray-resident app lives across theme changes — without
        // this, someone who flips Windows to light mode at sunset gets a dark-palette Vantage
        // until they fully exit from the tray. UserPreferenceChanged fires on both the
        // light/dark switch (General) and an accent change (Color); re-applying is cheap.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // If "start with Windows" is on, keep the registered exe path current.
        Vantage.App.Services.StartupManager.ReconcileOnLaunch();

        // Same idea for preset shortcuts, off the startup path since it touches COM and the
        // profile store. Velopack's update hook normally gets there first; this covers the
        // moves it knows nothing about, like a portable copy unzipped somewhere new.
        var shortcutStore = new ProfileStore();
        Task.Run(() =>
        {
            try
            {
                ShortcutReconciler.Reconcile(shortcutStore);
            }
            catch (Exception ex)
            {
                // Fire-and-forget must not become silent-and-forgotten.
                AppLog.Error(nameof(ShortcutReconciler), ex, "Launch-time reconcile failed");
            }
        });

        var displayService = new DisplayService();
        var store = new ProfileStore();
        ViewModel = new MainViewModel(displayService, store, new ApplyEngine(displayService));

        // The InfoBar the view model reports into lives in a window that is hidden for most
        // of this app's life. When nobody can see it, warnings and errors fall back to a
        // tray notification instead of vanishing.
        ViewModel.StatusReported += (title, message, severity) =>
        {
            if (MainAppWindow is { IsVisible: true })
                return;
            if (severity is Wpf.Ui.Controls.InfoBarSeverity.Warning or Wpf.Ui.Controls.InfoBarSeverity.Error)
                ShowTrayNotification(title, message, severity);
        };

        // Global profile hotkeys — alive even in tray-only mode.
        _hotkeys = new Vantage.App.Services.HotkeyService(profileId =>
            Dispatcher.InvokeAsync(() => ViewModel.OnHotkeyPressedAsync(profileId)));
        ViewModel.AttachHotkeyService(_hotkeys);

        CreateTrayIcon();

        // Serve the other end of the shortcut fast path from now on.
        InstanceChannel.StartServer(HandleRemoteCommand);

        // --tray (used by the sign-in Run entry) starts lightweight: tray icon only.
        if (!command.TrayOnly)
            ShowMainWindow();
    }

    /// <summary>Handles a request forwarded by a second launch (arrives on the IPC thread).</summary>
    private void HandleRemoteCommand(string message)
    {
        var command = LaunchCommand.FromMessage(message);
        Dispatcher.InvokeAsync(async () =>
        {
            if (command.ApplyTarget is { } target)
                await ViewModel.ApplyByTargetAsync(target);
            else
                ShowMainWindow();
        });
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

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color))
            return;
        Dispatcher.InvokeAsync(() =>
        {
            Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme(false);
            WindowsAccent.Apply();
            MainAppWindow?.RefreshTitleBarTheme();
            // Profile thumbnails paint the primary display in the accent color — re-render.
            _ = ViewModel?.RefreshAsync();
        });
    }

    /// <summary>Routes to the InfoBar when the window is visible, the tray balloon when not.</summary>
    private void NotifyEvenIfHidden(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        if (MainAppWindow is { IsVisible: true })
            ViewModel?.ReportStatus(title, message, severity);
        else
            ShowTrayNotification(title, message, severity);
    }

    private void ShowTrayNotification(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        try
        {
            _trayIcon?.ShowNotification(title, message,
                severity == Wpf.Ui.Controls.InfoBarSeverity.Error
                    ? H.NotifyIcon.Core.NotificationIcon.Error
                    : H.NotifyIcon.Core.NotificationIcon.Warning);
        }
        catch (Exception ex)
        {
            // A notification about a failure must never be a second failure.
            AppLog.Error("Tray", ex, "ShowNotification failed");
        }
    }

    public void ExitApplication()
    {
        _trayIcon?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SystemEvents holds handlers in a static list; an unsubscribed exit is a clean exit.
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _hotkeys?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
