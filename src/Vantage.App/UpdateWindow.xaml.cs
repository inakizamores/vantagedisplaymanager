using System.Windows;
using Vantage.App.Services;

namespace Vantage.App;

/// <summary>
/// Shows what a pending update contains, downloads it, and hands over to Velopack to swap the
/// install and relaunch. The download is cancellable right up until the swap; after that the
/// process is replaced, so there is nothing left to cancel.
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService _updates;
    private readonly AvailableUpdate _update;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;

    public UpdateWindow(UpdateService updates, AvailableUpdate update)
    {
        InitializeComponent();
        _updates = updates;
        _update = update;

        HeadlineText.Text = $"Vantage {update.Version} is available";
        SubtitleText.Text = $"You have {UpdateService.CurrentVersion}. " +
                            $"The download is {Megabytes(update.DownloadSizeBytes)}, and Vantage restarts when it finishes. " +
                            "Your profiles, shortcuts and settings are kept.";

        ReleaseNotesRenderer.Render(NotesPanel, update.ReleaseNotes ?? "");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeChrome.Apply(this);
    }

    private static string Megabytes(long bytes) =>
        bytes <= 0 ? "a few MB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Covers the title-bar X as well as "Later" — a download with no window to report
        // to shouldn't keep running.
        _cancellation.Cancel();
        _cancellation.Dispose();
        base.OnClosed(e);
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        try
        {
            _busy = true;
            UpdateButton.IsEnabled = false;
            UpdateButton.Content = "Downloading…";
            DownloadProgress.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;

            var progress = new Progress<int>(percent => DownloadProgress.Value = percent);
            var error = await _updates.DownloadAsync(progress, _cancellation.Token);

            if (error is not null)
            {
                ShowFailure($"The download failed: {error}");
                return;
            }

            UpdateButton.Content = "Restarting…";

            // Replaces this process. Nothing after this line runs — unless it throws, which
            // is why the catch below exists: a crash mid-update is the worst possible crash.
            _updates.ApplyAndRestart();
        }
        catch (Exception ex)
        {
            Vantage.Core.Services.AppLog.Error("Update", ex, "Update flow threw");
            ShowFailure($"The update failed: {ex.Message}");
        }
    }

    private void ShowFailure(string message)
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = message;
        // Failure text in the same grey as the subtitle reads as body copy; use the
        // system critical color so it reads as what it is.
        if (TryFindResource("SystemFillColorCriticalBrush") is System.Windows.Media.Brush critical)
            StatusText.Foreground = critical;
        StatusText.Visibility = Visibility.Visible;
        UpdateButton.Content = "Try again";
        UpdateButton.IsEnabled = true;
        _busy = false;
    }
}
