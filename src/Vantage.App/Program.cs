using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vantage.App.Services;
using Vantage.Core.Services;

namespace Vantage.App;

/// <summary>
/// Entry point, deliberately kept clear of WPF.
///
/// A preset shortcut launches this exe purely to say "switch to that profile". Constructing a
/// WPF <c>Application</c> — loading PresentationFramework, the theme dictionaries, the control
/// styles — costs about a second on a warm machine, and every millisecond of it is wasted on a
/// process that will never draw anything. So the shortcut paths are handled here, before any
/// WPF type is referenced: the JIT only loads those assemblies when <see cref="RunApp"/> is
/// actually called, which is why it is kept in its own non-inlined method.
///
/// Three ways out:
///   • Vantage is already running  → hand the request over the IPC channel and exit (~30 ms).
///   • Nothing running, --apply    → drive the engine head-on, no UI at all, exit.
///   • Anything else               → build the real WPF app.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Last-resort diagnostics for every path, headless ones included. Without these, a
        // crash in the tray-resident app dies silently and a bug report has nothing to say.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("Unhandled", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                "AppDomain.UnhandledException (crashing)");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unhandled", e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        // Velopack's install/update/uninstall hooks must always reach VelopackApp.Run(),
        // so they skip every shortcut path below.
        if (args.Any(a => a.StartsWith("--veloapp", StringComparison.OrdinalIgnoreCase)))
            return RunApp(null);

        var command = LaunchCommand.Parse(args);

        // --help would otherwise fall through and silently launch the GUI, which is the one
        // thing someone typing --help has said they don't want.
        if (command.Help)
        {
            AttachConsole(unchecked((uint)-1));
            Console.WriteLine("""

                Vantage Display Manager

                usage:
                  Vantage.exe                       Open the app
                  Vantage.exe --apply <id or name>  Switch to a profile and exit, no window
                  Vantage.exe --tray                Start in the tray only (no window)
                  Vantage.exe --update              Check GitHub, download, install, relaunch
                                                    (exit codes: 0 updated, 1 current,
                                                     2 not an installed copy, 3 failed)

                Everything else is scriptable through vantagectl (see: vantagectl).
                """);
            return 0;
        }

        // Before the single-instance mutex: updating is Velopack's business whether or not a
        // copy is already running, and it knows how to deal with the running one. The hooks
        // have to run first — they are what tell Velopack where it is installed, without which
        // UpdateManager reports an installed copy as portable and refuses to update it.
        if (command.Update)
        {
            RunVelopackHooks();
            return UpdateHeadless();
        }

        var mutex = new Mutex(true, @"Local\VantageDisplayManager", out var owned);

        if (!owned)
        {
            mutex.Dispose();

            if (command.ApplyTarget is null)
            {
                // A plain second launch foregrounds the running window; a sign-in --tray
                // launch has nothing to do at all.
                if (!command.TrayOnly)
                    InstanceChannel.TrySend(command.ToMessage());
                return 0;
            }

            // The warm instance already has the display service and profile store loaded,
            // so it does the switch far faster than we could from a standing start.
            if (InstanceChannel.TrySend(command.ToMessage()))
                return 0;

            // It holds the mutex but isn't answering — mid-startup, or an older build with no
            // channel. Do the work here rather than dropping the user's click.
        }

        try
        {
            if (command.ApplyTarget is { } target)
                return ApplyHeadless(target);

            return RunApp(owned ? mutex : null);
        }
        finally
        {
            if (owned && command.ApplyTarget is not null)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies a profile and exits, with no window, no tray icon and no WPF. Kept separate
    /// from <see cref="RunApp"/> so this path never pulls the UI assemblies in.
    /// </summary>
    private static int ApplyHeadless(string target)
    {
        try
        {
            var profile = new ProfileStore().Find(target);
            if (profile is null)
            {
                Warn($"Vantage has no profile called '{target}'.\n\n" +
                     "It may have been renamed or deleted — recreate the shortcut from the app.");
                return 2;
            }

            var report = new ApplyEngine(new DisplayService()).ApplyAsync(profile).GetAwaiter().GetResult();
            if (report.Succeeded)
                return 0;

            AppLog.WriteBlock("Apply", $"Headless apply of '{profile.Name}' failed: {report.FailureReason ?? "did not verify"}", report.Log);
            Warn($"'{profile.Name}' could not be applied.\n\n{report.FailureReason ?? "The change did not verify."}");
            return 3;
        }
        catch (Exception ex)
        {
            AppLog.Error("Apply", ex, $"Headless apply of '{target}' threw");
            Warn(ex.Message);
            return 3;
        }
    }

    /// <summary>
    /// <c>--update</c>: the whole update flow without the window. Prints what it finds and what
    /// it does, so it can be scripted and so the update path can be verified without clicking
    /// through the UI. Exit codes: 0 updated (process is replaced), 1 already current,
    /// 2 not an installed copy, 3 failed.
    /// </summary>
    private static int UpdateHeadless()
    {
        // A console app writing to a console it may not have: attach to the parent's if we were
        // launched from one, so `Vantage.exe --update` from a terminal actually prints.
        AttachConsole(unchecked((uint)-1));

        var updates = new UpdateService();
        Console.WriteLine($"Vantage {UpdateService.CurrentVersion}");

        if (!updates.CanUpdate)
        {
            Console.WriteLine("This copy was not installed by the setup program, so it cannot update itself.");
            return 2;
        }

        var (update, error) = updates.CheckAsync().GetAwaiter().GetResult();
        if (error is not null)
        {
            Console.WriteLine($"Could not check for updates: {error}");
            return 3;
        }

        if (update is null)
        {
            Console.WriteLine("Already on the latest version.");
            return 1;
        }

        Console.WriteLine($"Found {update.Version} ({update.DownloadSizeBytes / 1024 / 1024} MB), release notes: " +
                          $"{(update.ReleaseNotes is null ? "none" : $"{update.ReleaseNotes.Length} characters")}");
        Console.WriteLine("Downloading…");

        var lastPercent = -1;
        var progress = new Progress<int>(percent =>
        {
            if (percent / 10 == lastPercent / 10)
                return;
            lastPercent = percent;
            Console.WriteLine($"  {percent}%");
        });

        if (updates.DownloadAsync(progress).GetAwaiter().GetResult() is { } downloadError)
        {
            Console.WriteLine($"Download failed: {downloadError}");
            return 3;
        }

        Console.WriteLine($"Installing {update.Version} and restarting…");
        updates.ApplyAndRestart();
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    /// <summary>
    /// Velopack's install/update/uninstall hooks, and the call that tells it where this copy
    /// lives. A no-op outside an installed context, but it has to happen before anything asks
    /// about updates.
    ///
    /// An install or update is exactly when preset shortcuts get hurt: an update can move the
    /// folder they point into, and a full Setup.exe run over an existing copy uninstalls the
    /// old version first — whose before-uninstall hook deletes every preset shortcut on the
    /// way out. So both landing hooks run the full restore: Velopack rewrites its own Start
    /// menu entry here, and preset shortcuts are recreated or repaired from the profile store
    /// (which lives in Documents and survives all of this) rather than being left to rot.
    /// Uninstalling takes the shortcuts with it, but keeps them on the books for next time.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunVelopackHooks() =>
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => ShortcutReconciler.Restore(new ProfileStore()))
            .OnAfterUpdateFastCallback(_ => ShortcutReconciler.Restore(new ProfileStore()))
            .OnBeforeUninstallFastCallback(_ => ShortcutReconciler.RemoveAll(new ProfileStore()))
            .Run();

    /// <summary>
    /// The headless path is silent when it works. When it doesn't there is no window and no
    /// tray icon to put a message in, so use the one thing that needs neither.
    /// </summary>
    private static void Warn(string message) =>
        MessageBoxW(IntPtr.Zero, message, "Vantage Display Manager", MB_OK | MB_ICONWARNING | MB_SETFOREGROUND);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONWARNING = 0x30;
    private const uint MB_SETFOREGROUND = 0x10000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    /// <summary>
    /// Builds and runs the real application. Never inlined: keeping every WPF reference behind
    /// a call boundary is what stops the fast paths above from loading the UI assemblies.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(Mutex? singleInstanceMutex)
    {
        RunVelopackHooks();

        var app = new App(singleInstanceMutex);
        app.InitializeComponent();
        return app.Run();
    }
}
