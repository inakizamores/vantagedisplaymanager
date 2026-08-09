using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Vantage.App.Services;

/// <summary>An update waiting on GitHub, in the form the UI needs.</summary>
public sealed record AvailableUpdate(string Version, string? ReleaseNotes, long DownloadSizeBytes);

/// <summary>
/// In-app updates, straight from the project's GitHub releases.
///
/// The release workflow already publishes a Velopack feed with every tag, so this only has to
/// read it: check, download, then hand over to Velopack's updater, which swaps the install and
/// relaunches. Nothing here touches user data — profiles, settings and shortcut icons live in
/// <c>Documents\Vantage Display Manager</c>, deliberately outside the install folder, and preset
/// shortcuts are re-pointed by <see cref="ShortcutReconciler"/> from the after-update hook.
///
/// Every release so far is tagged as a pre-release (they all carry <c>-beta</c>), so the source
/// is configured to see them; a stable 1.0 tag would be picked up by the same feed.
/// </summary>
public sealed class UpdateService
{
    private const string RepositoryUrl = "https://github.com/inakizamores/vantagedisplaymanager";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(
                new GithubSource(RepositoryUrl, accessToken: null, prerelease: true),
                new UpdateOptions { AllowVersionDowngrade = false });
        }
        catch (Exception)
        {
            // A portable copy has no Velopack metadata — updates simply are not available.
            _manager = null;
        }
    }

    /// <summary>
    /// False for the portable build and for running from a debugger: there is no install for
    /// Velopack to replace, so the UI offers a download link instead of an update button.
    /// </summary>
    public bool CanUpdate => _manager?.IsInstalled == true;

    /// <summary>What the user is running, for display. Falls back to the assembly version.</summary>
    public static string CurrentVersion
    {
        get
        {
            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // Strip the "+<commit sha>" suffix the SDK appends — meaningless to a user.
            if (informational is { Length: > 0 })
                return informational.Split('+')[0];

            return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        }
    }

    /// <summary>Null when already up to date. Never throws — failures surface as a message.</summary>
    public async Task<(AvailableUpdate? Update, string? Error)> CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled)
            return (null, "This copy of Vantage was not installed by the setup program, so it can't update itself.");

        try
        {
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
                return (null, null);

            var asset = _pending.TargetFullRelease;
            return (new AvailableUpdate(
                asset.Version.ToString(),
                string.IsNullOrWhiteSpace(asset.NotesMarkdown) ? null : asset.NotesMarkdown,
                asset.Size), null);
        }
        catch (Exception ex)
        {
            // No network, GitHub rate limit, a malformed feed — all the same to the user.
            return (null, ex.Message);
        }
    }

    /// <summary>Downloads the pending update. Progress is a percentage.</summary>
    public async Task<string?> DownloadAsync(IProgress<int>? progress, CancellationToken ct = default)
    {
        if (_manager is null || _pending is null)
            return "No update has been found yet.";

        try
        {
            await _manager.DownloadUpdatesAsync(_pending, p => progress?.Report(p), cancelToken: ct)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Swaps in the downloaded version and relaunches. Does not return — the process is
    /// replaced. The caller must be sure no display apply is in flight.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
            return;

        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
