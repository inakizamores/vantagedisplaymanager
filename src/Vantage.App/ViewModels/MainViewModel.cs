using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vantage.App.Services;
using Vantage.Core.Models;
using Vantage.Core.Services;

namespace Vantage.App.ViewModels;

public partial class DisplayItemViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _suppressHdrToggle;

    public DisplayItemViewModel(MainViewModel owner, DisplayState state)
    {
        _owner = owner;
        Update(state);
    }

    public string StableId { get; private set; } = "";

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private string _badgeText = "";
    [ObservableProperty] private bool _isPrimary;
    [ObservableProperty] private bool _hdrSupported;
    [ObservableProperty] private bool _hdrOn;
    [ObservableProperty] private string _iconSymbol = "Desktop24";

    public void Update(DisplayState d)
    {
        StableId = d.Identity.StableId;
        Name = d.Identity.FriendlyName ?? d.Identity.StableId;
        IsPrimary = d.IsPrimary;
        BadgeText = d.IsPrimary ? "Primary" : "";
        ModeText = $"{d.Width} × {d.Height} · {d.RefreshHz:0.###} Hz";
        var scale = d.Dpi is { } dpi ? $" · {dpi.CurrentPercent}% scale" : "";
        var hdrText = d.Hdr.Supported
            ? d.Hdr.Enabled ? " · HDR on" : " · HDR off"
            : "";
        var bpcText = d.OutputBpc is { } bpc ? $" · {bpc} bpc" : "";
        DetailText = $"{Prettify(d.OutputTechnology)}{scale}{hdrText}{bpcText}";
        IconSymbol = d.OutputTechnology.Contains("Internal") ? "Laptop24" : "Desktop24";
        HdrSupported = d.Hdr.Supported;

        _suppressHdrToggle = true;
        HdrOn = d.Hdr.Enabled;
        _suppressHdrToggle = false;
    }

    partial void OnHdrOnChanged(bool value)
    {
        if (_suppressHdrToggle)
            return;
        _ = _owner.ToggleHdrAsync(this, value);
    }

    internal void SetHdrSilently(bool value)
    {
        _suppressHdrToggle = true;
        HdrOn = value;
        _suppressHdrToggle = false;
    }

    private static string Prettify(string outputTechnology) => outputTechnology switch
    {
        "DisplayPortExternal" or "DisplayPortEmbedded" or "DisplayPortUsbTunnel" => "DisplayPort",
        "Hdmi" => "HDMI",
        "Dvi" => "DVI/HDMI",
        "Internal" or "Lvds" => "Built-in display",
        "IndirectVirtual" => "Virtual display",
        "Miracast" => "Wireless",
        var other => other,
    };
}

public partial class ProfileItemViewModel(VantageProfile profile) : ObservableObject
{
    public VantageProfile Profile { get; private set; } = profile;
    public Guid Id => Profile.Id;

    [ObservableProperty] private string _name = profile.Name;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPossible = true;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? _layoutImage;
    [ObservableProperty] private string _hotkeyText = "";
    [ObservableProperty] private string _shortcutText = "";
    [ObservableProperty] private string _shortcutTooltip = "";

    public void Update(VantageProfile profile, ProfileMatchResult match)
    {
        Profile = profile;
        Name = profile.Name;
        IsActive = match.IsActive;
        IsPossible = match.IsPossible;
        StatusText = match.IsActive ? "Active" : match.IsPossible ? "" : "Displays not connected";
        SummaryText = string.Join("  ·  ", profile.Displays.Where(d => d.Enabled)
            .Select(d => $"{d.Width}×{d.Height}@{Math.Round(d.RefreshMillihertz / 1000.0)}"));
        HotkeyText = profile.Hotkey is { Length: > 0 } h ? HotkeyService.FormatGesture(h) : "";
        LayoutImage = LayoutThumbnail.Render(ThumbnailDisplay.From(profile));

        // Shortcuts the user deleted from Explorer shouldn't keep being reported as present.
        var shortcuts = (profile.ShortcutPaths ?? []).Where(System.IO.File.Exists).ToList();
        ShortcutText = shortcuts.Count > 0 ? "Shortcut" : "";
        ShortcutTooltip = shortcuts.Count == 0
            ? "Add this preset to the Start menu as a shortcut"
            : $"Shortcut on {string.Join(" and ", shortcuts.Select(DescribeLocation).Distinct())} — click to change or remove it";
    }

    private static string DescribeLocation(string shortcutPath) =>
        string.Equals(System.IO.Path.GetDirectoryName(shortcutPath), ShortcutService.DesktopFolder, StringComparison.OrdinalIgnoreCase)
            ? "your desktop"
            : "the Start menu";
}

public partial class MainViewModel : ObservableObject
{
    private readonly DisplayService _displayService;
    private readonly ProfileStore _store;
    private readonly ApplyEngine _engine;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _suppressSettingSideEffects;

    public MainViewModel(DisplayService displayService, ProfileStore store, ApplyEngine engine)
    {
        _displayService = displayService;
        _store = store;
        _engine = engine;

        // A copy that can't self-update should say so up front, not after a dead button press.
        if (!_updates.CanUpdate)
            UpdateStatus = "This copy isn't managed by the installer — get new versions from the releases page; your profiles and settings carry over";

        _settings = AppSettings.Load();
        _suppressSettingSideEffects = true;
        StartWithWindows = StartupManager.IsEnabled();
        CheckUpdatesAtStartup = _settings.CheckForUpdatesAtStartup;
        CloseToTray = _settings.CloseToTray;
        _suppressSettingSideEffects = false;

        _ = RefreshAsync();

        // Look for a new version quietly in the background. Nothing pops up — the Settings
        // card just starts offering the update instead of a Check button. Pointless for a
        // copy that couldn't install the result (and would clobber its explanatory text).
        if (_settings.CheckForUpdatesAtStartup && _updates.CanUpdate)
            _ = CheckForUpdatesAsync(silent: true);
    }

    // --- Updates ---

    private readonly UpdateService _updates = new();

    public string VersionText => $"Vantage {UpdateService.CurrentVersion}";

    /// <summary>
    /// Shown until there is something more specific to say. The card sits next to one that
    /// explains itself in a line of grey text, and an empty second line makes it look broken —
    /// so this describes what the app does on its own rather than leaving a gap.
    /// </summary>
    private const string IdleUpdateStatus =
        "Checks for new versions at startup — updating keeps your profiles, shortcuts and settings";

    [ObservableProperty] private string _updateStatus = IdleUpdateStatus;
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private AvailableUpdate? _availableUpdate;

    public bool HasUpdate => AvailableUpdate is not null;

    /// <summary>False for the portable build and debugger runs — nothing for Velopack to swap.</summary>
    public bool CanSelfUpdate => _updates.CanUpdate;

    public bool ShowCheckButton => CanSelfUpdate && !HasUpdate;

    partial void OnAvailableUpdateChanged(AvailableUpdate? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ShowCheckButton));
    }

    [RelayCommand]
    private void OpenReleasesPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateService.ReleasesUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the releases page", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (IsCheckingForUpdates)
            return;

        try
        {
            IsCheckingForUpdates = true;
            if (!silent)
                UpdateStatus = "Checking…";

            var (update, error) = await _updates.CheckAsync();
            AvailableUpdate = update;

            UpdateStatus = (update, error, silent) switch
            {
                ({ } found, _, _) => $"Version {found.Version} is ready to install",
                (null, { } problem, false) => problem,
                (null, _, false) => "You're on the latest version",
                // A silent check that found nothing goes back to saying nothing in particular,
                // including when the network is down. Not worth nagging about on every launch.
                _ => IdleUpdateStatus,
            };
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private void InstallUpdate()
    {
        if (AvailableUpdate is not { } update)
            return;

        // Never swap the install out from under a display change in flight.
        if (IsBusy)
        {
            ShowStatus("Busy applying a profile", "Try updating again once the display change finishes.",
                Wpf.Ui.Controls.InfoBarSeverity.Informational);
            return;
        }

        new UpdateWindow(_updates, update)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        }.ShowDialog();
    }

    // --- Settings ---

    private readonly AppSettings _settings;

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _checkUpdatesAtStartup;
    [ObservableProperty] private bool _closeToTray;

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_suppressSettingSideEffects)
            return;
        try
        {
            if (value)
                StartupManager.Enable();
            else
                StartupManager.Disable();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not update startup setting", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            _suppressSettingSideEffects = true;
            StartWithWindows = StartupManager.IsEnabled();
            _suppressSettingSideEffects = false;
        }
    }

    partial void OnCheckUpdatesAtStartupChanged(bool value)
    {
        if (_suppressSettingSideEffects)
            return;
        _settings.CheckForUpdatesAtStartup = value;
        SaveSettings();
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_suppressSettingSideEffects)
            return;
        _settings.CloseToTray = value;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save settings", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            VantageDataPaths.EnsureCreatedAndMigrated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = VantageDataPaths.Root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the data folder", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    public ObservableCollection<DisplayItemViewModel> Displays { get; } = [];
    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    [ObservableProperty] private string _newProfileName = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";

    // Status InfoBar
    [ObservableProperty] private bool _statusOpen;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private Wpf.Ui.Controls.InfoBarSeverity _statusSeverity = Wpf.Ui.Controls.InfoBarSeverity.Informational;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var snapshot = await Task.Run(_displayService.Capture);
            var envelope = await Task.Run(_store.Load);

            await _dispatcher.InvokeAsync(() =>
            {
                SyncDisplays(snapshot);
                SyncProfiles(envelope, snapshot);

                // The store repaired itself from a damaged file — the user should know
                // recent changes may be gone.
                if (_store.LastRecoveryMessage is { } recovery)
                    ShowStatus("Profiles recovered", recovery, Wpf.Ui.Controls.InfoBarSeverity.Warning);
            });
        }
        catch (Exception ex)
        {
            ShowStatus("Refresh failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void SyncDisplays(SystemSnapshot snapshot)
    {
        var byId = Displays.ToDictionary(d => d.StableId, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in snapshot.Displays)
        {
            seen.Add(state.Identity.StableId);
            if (byId.TryGetValue(state.Identity.StableId, out var vm))
                vm.Update(state);
            else
                Displays.Add(new DisplayItemViewModel(this, state));
        }

        for (var i = Displays.Count - 1; i >= 0; i--)
            if (!seen.Contains(Displays[i].StableId))
                Displays.RemoveAt(i);
    }

    private void SyncProfiles(ProfileFileEnvelope envelope, SystemSnapshot snapshot)
    {
        var byId = Profiles.ToDictionary(p => p.Id);
        var seen = new HashSet<Guid>();

        foreach (var profile in envelope.Profiles)
        {
            seen.Add(profile.Id);
            var match = ProfileMatcher.Match(profile, snapshot);
            if (byId.TryGetValue(profile.Id, out var vm))
                vm.Update(profile, match);
            else
            {
                var item = new ProfileItemViewModel(profile);
                item.Update(profile, match);
                Profiles.Add(item);
            }
        }

        for (var i = Profiles.Count - 1; i >= 0; i--)
            if (!seen.Contains(Profiles[i].Id))
                Profiles.RemoveAt(i);

        HasProfiles = Profiles.Count > 0;

        RefreshHotkeyRegistrations(envelope);
    }

    /// <summary>Drives the first-run empty state under the profile list.</summary>
    [ObservableProperty] private bool _hasProfiles = true;

    // --- Hotkeys ---

    private HotkeyService? _hotkeys;

    public void AttachHotkeyService(HotkeyService hotkeys)
    {
        _hotkeys = hotkeys;
        try
        {
            RefreshHotkeyRegistrations(_store.Load());
        }
        catch (Exception ex)
        {
            // Runs during OnStartup, before any window exists — a store problem here (e.g. a
            // newer-schema file) must not take the whole app down with it.
            AppLog.Error(nameof(MainViewModel), ex, "Initial hotkey registration failed");
            ShowStatus("Profiles could not be loaded", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private string? _lastHotkeyWarning;

    private void RefreshHotkeyRegistrations(ProfileFileEnvelope envelope)
    {
        if (_hotkeys is null)
            return;

        var withHotkeys = envelope.Profiles.Where(p => p.Hotkey is { Length: > 0 }).ToList();

        // Two Vantage profiles sharing a gesture is our own duplicate, not "another app" —
        // report it as such (the second RegisterHotKey would fail with the same error either
        // way, which sends the user hunting through other software for a conflict that's here).
        var duplicateGroups = withHotkeys
            .GroupBy(p => p.Hotkey!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        var duplicates = duplicateGroups
            .Select(g => $"{HotkeyService.FormatGesture(g.Key)} is assigned to both " +
                         $"{string.Join(" and ", g.Select(p => $"'{p.Name}'"))} — only '{g.First().Name}' will respond")
            .ToList();
        var duplicateIds = duplicateGroups.SelectMany(g => g.Skip(1)).Select(p => p.Id).ToHashSet();

        var failures = _hotkeys.RegisterAll(withHotkeys.Select(p => (p.Id, p.Hotkey!)));
        var byId = envelope.Profiles.ToDictionary(p => p.Id, p => p.Name);
        var external = failures
            .Where(f => !duplicateIds.Contains(f.ProfileId))
            .Select(f => $"{HotkeyService.FormatGesture(f.Gesture)} ('{byId.GetValueOrDefault(f.ProfileId, "?")}') is in use by another app")
            .ToList();

        var problems = duplicates.Concat(external).ToList();
        var warning = problems.Count > 0 ? string.Join("  ·  ", problems) : null;

        // An unresolvable conflict would otherwise re-open this warning after every refresh —
        // every apply, save, delete and display change. Speak once per distinct problem.
        if (warning is not null && warning != _lastHotkeyWarning)
            ShowStatus("Some hotkeys are unavailable", warning, Wpf.Ui.Controls.InfoBarSeverity.Warning);
        _lastHotkeyWarning = warning;
    }

    public async Task OnHotkeyPressedAsync(Guid profileId)
    {
        var item = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (item is not null && item.IsPossible && !IsBusy)
            await ApplyProfileAsync(item);
    }

    /// <summary>
    /// Applies a profile named by id or name — the request a preset shortcut forwards to
    /// this instance over the IPC channel.
    /// </summary>
    public async Task ApplyByTargetAsync(string target)
    {
        var item = FindProfile(target);
        if (item is null)
        {
            // The shortcut may point at a profile added since this instance last looked.
            await RefreshAsync();
            item = FindProfile(target);
        }

        if (item is null)
        {
            ShowStatus("Shortcut is out of date", $"No profile called '{target}' — it was renamed or deleted.",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
            return;
        }

        await ApplyProfileAsync(item);
    }

    private ProfileItemViewModel? FindProfile(string target) => Guid.TryParse(target, out var id)
        ? Profiles.FirstOrDefault(p => p.Id == id)
        : Profiles.FirstOrDefault(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase));

    // --- Shortcuts ---

    [RelayCommand]
    private async Task CreateShortcutAsync(ProfileItemViewModel item)
    {
        if (IsBusy)
            return;

        var dialog = new ShortcutWindow(item.Profile)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.Plan is not { } plan)
            return;

        try
        {
            // Clear out shortcuts we are not about to rewrite — the name drives the file name,
            // so a renamed or relocated shortcut would otherwise be left behind as a duplicate.
            // Paths that survive are overwritten in place further down rather than deleted and
            // recreated, because replacing the file drops any Start pin made against it.
            var keep = plan.Remove
                ? []
                : plan.Locations
                    .Select(l => ShortcutService.PathFor(l, plan.Name, plan.GroupInStartMenu))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in (item.Profile.ShortcutPaths ?? []).Where(p => !keep.Contains(p)))
                ShortcutService.Delete(stale);
            item.Profile.ShortcutPaths = null;

            if (plan.Remove)
            {
                ShortcutIcon.DeleteFor(item.Id);
                item.Profile.IconPath = null;
                _store.Upsert(item.Profile);
                ShowStatus("Shortcut removed", $"'{item.Name}' no longer has a shortcut.",
                    Wpf.Ui.Controls.InfoBarSeverity.Informational);
            }
            else
            {
                // .ico and .exe carry icons already; anything else is converted to one.
                var iconPath = plan.CustomIconPath switch
                {
                    null => ShortcutIcon.WriteLayoutIcon(item.Id, ThumbnailDisplay.From(item.Profile)),
                    var custom when ShortcutIcon.IsIconContainer(custom) => custom,
                    var custom => ShortcutIcon.WriteImageIcon(item.Id, custom),
                };

                item.Profile.ShortcutPaths = plan.Locations
                    .Select(location => ShortcutService.Create(
                        location,
                        plan.Name,
                        $"{LaunchCommand.ApplySwitch} {item.Id}",
                        $"Switch displays to the '{item.Name}' preset",
                        iconPath,
                        plan.GroupInStartMenu))
                    .ToList();
                item.Profile.IconPath = plan.CustomIconPath;
                _store.Upsert(item.Profile);

                var places = string.Join(" and ", plan.Locations.Select(l => l switch
                {
                    ShortcutLocation.Desktop => "on your desktop",
                    _ when plan.GroupInStartMenu => $"in the Start menu under {ShortcutService.StartMenuFolderName}",
                    _ => "in the Start menu",
                }));
                ShowStatus("Shortcut ready", $"'{plan.Name}' is {places}. Opening it switches displays without opening the app.",
                    Wpf.Ui.Controls.InfoBarSeverity.Success);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not update the shortcut", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private static void RemoveShortcuts(VantageProfile profile)
    {
        foreach (var path in profile.ShortcutPaths ?? [])
            ShortcutService.Delete(path);
        profile.ShortcutPaths = null;
    }

    /// <summary>
    /// The Settings-page escape hatch for shortcuts an update or reinstall broke: puts every
    /// recorded shortcut back at the path it lived at, which also revives any Start pin made
    /// against it. Runs on the UI thread on purpose — it is quick, and recreating an icon
    /// needs WPF rendering, which a pool thread is not allowed to do.
    /// </summary>
    [RelayCommand]
    private async Task RepairShortcutsAsync()
    {
        if (IsBusy)
            return;

        var result = ShortcutReconciler.Restore(_store);
        await RefreshAsync();

        if (result.Total == 0)
        {
            ShowStatus("No shortcuts to repair", "No preset has a shortcut yet — use Shortcut on a preset to create one.",
                Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
        else if (result is { Recreated: 0, Repaired: 0 })
        {
            ShowStatus("Shortcuts are healthy",
                result.Intact == 1
                    ? "Your shortcut already points at this copy of Vantage."
                    : $"All {result.Intact} shortcuts already point at this copy of Vantage.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
        else
        {
            List<string> parts = [];
            if (result.Recreated > 0)
                parts.Add($"{result.Recreated} recreated");
            if (result.Repaired > 0)
                parts.Add($"{result.Repaired} repaired");
            if (result.Intact > 0)
                parts.Add($"{result.Intact} already fine");
            ShowStatus("Shortcuts repaired",
                $"{string.Join(", ", parts)}. Start pins made against a recreated shortcut come back with it.",
                Wpf.Ui.Controls.InfoBarSeverity.Success);
        }
    }

    [RelayCommand]
    private async Task SetHotkeyAsync(ProfileItemViewModel item)
    {
        var dialog = new HotkeyCaptureWindow(item.Name, item.Profile.Hotkey)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
            return;

        // Validate at capture time instead of letting the user find out at the next refresh.
        if (dialog.Gesture is { } gesture)
        {
            var display = HotkeyService.FormatGesture(gesture);
            var holder = Profiles.FirstOrDefault(p =>
                p.Id != item.Id && string.Equals(p.Profile.Hotkey, gesture, StringComparison.OrdinalIgnoreCase));
            if (holder is not null)
            {
                ShowStatus("Hotkey not saved",
                    $"{display} already switches to '{holder.Name}'. Pick a different combination, or clear it there first.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }

            // Not ours anywhere, so a probe answers for the rest of the system. Skip it when
            // the profile already owns this gesture (re-saving the same combo is fine).
            if (!string.Equals(item.Profile.Hotkey, gesture, StringComparison.OrdinalIgnoreCase)
                && _hotkeys is { } hotkeys && !hotkeys.IsGestureAvailable(gesture))
            {
                ShowStatus("Hotkey not saved",
                    $"{display} is already in use by another app. Pick a different combination.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
                return;
            }
        }

        item.Profile.Hotkey = dialog.Gesture;
        _store.Upsert(item.Profile);
        await RefreshAsync();
    }

    // --- Dialogs ---

    [RelayCommand]
    private async Task NewPresetAsync()
    {
        if (IsBusy)
            return;

        var snapshot = await Task.Run(_displayService.Capture);
        var dialog = new PresetEditorWindow(snapshot)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.CreatedProfile is not { } created)
            return;

        var existing = _store.Find(created.Name);
        if (existing is not null)
            created = created with { Id = existing.Id, CreatedAt = existing.CreatedAt, Hotkey = existing.Hotkey };
        _store.Upsert(created);
        ShowStatus("Preset created", $"'{created.Name}' is ready to apply.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task OpenLayoutEditorAsync()
    {
        if (IsBusy)
            return;

        var snapshot = await Task.Run(_displayService.Capture);
        var dialog = new LayoutEditorWindow(snapshot, _engine)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        dialog.ShowDialog();
        if (dialog.Applied)
            ShowStatus("Arrangement applied", "The new display arrangement is verified and active.", Wpf.Ui.Controls.InfoBarSeverity.Success);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName)
            ? $"Profile {Profiles.Count + 1}"
            : NewProfileName.Trim();

        try
        {
            IsBusy = true;
            BusyText = "Capturing current configuration…";
            await Task.Run(() =>
            {
                var snapshot = _displayService.Capture();
                var existing = _store.Find(name);
                var profile = ProfileStore.FromSnapshot(snapshot, name);
                if (existing is not null)
                    profile = profile with { Id = existing.Id, CreatedAt = existing.CreatedAt };
                _store.Upsert(profile);
            });
            NewProfileName = "";
            ShowStatus("Profile saved", $"'{name}' captured from the current setup.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save profile", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task ApplyProfileAsync(ProfileItemViewModel item)
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            BusyText = $"Applying '{item.Name}'…";

            var progress = new Progress<ApplyProgress>(p => BusyText = p.Message);
            var report = await Task.Run(() => _engine.ApplyAsync(item.Profile, progress));

            await RefreshAsync();

            // Fully automatic failure policy (no confirmations): the engine verified the
            // result and rolled back on hard failure — just tell the user what happened.
            if (report.Succeeded && report.Warnings.Count == 0)
            {
                ShowStatus("Profile applied", $"'{item.Name}' is now active.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            }
            else if (report.Succeeded)
            {
                ShowStatus($"'{item.Name}' applied with warnings", string.Join("  ·  ", report.Warnings),
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else if (report.AutoReverted)
            {
                AppLog.WriteBlock("Apply", $"'{item.Name}' auto-reverted: {report.FailureReason ?? "hard failure"}", report.Log);
                ShowStatus("Change didn't verify — reverted automatically",
                    report.FailureReason ?? "The previous configuration was restored.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else
            {
                // The engine's step log is the diagnosis; put it where a bug report can find it.
                AppLog.WriteBlock("Apply", $"'{item.Name}' failed: {report.FailureReason ?? "unknown"}", report.Log);
                ShowStatus("Apply failed",
                    report.FailureReason ?? $"Details were written to the log in {VantageDataPaths.Root}.",
                    Wpf.Ui.Controls.InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Apply failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private async Task OverwriteProfileAsync(ProfileItemViewModel item)
    {
        if (IsBusy)
            return;

        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Overwrite profile",
            Content = $"Replace '{item.Name}' with your current display setup? Its hotkey is kept.",
            PrimaryButtonText = "Overwrite",
            CloseButtonText = "Cancel",
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        try
        {
            IsBusy = true;
            BusyText = $"Overwriting '{item.Name}' with the current setup…";
            await Task.Run(() =>
            {
                var snapshot = _displayService.Capture();
                var updated = ProfileStore.FromSnapshot(snapshot, item.Name) with
                {
                    Id = item.Profile.Id,
                    CreatedAt = item.Profile.CreatedAt,
                    Hotkey = item.Profile.Hotkey,
                };
                _store.Upsert(updated);
            });
            ShowStatus("Profile overwritten", $"'{item.Name}' now matches your current setup.", Wpf.Ui.Controls.InfoBarSeverity.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("Could not overwrite profile", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel item)
    {
        var hasShortcuts = item.Profile.ShortcutPaths is { Count: > 0 };
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete profile",
            Content = hasShortcuts
                ? $"Delete '{item.Name}'? Its Start menu and desktop shortcuts go with it. This cannot be undone."
                : $"Delete '{item.Name}'? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        var result = await box.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        // Its shortcuts would be dead links otherwise.
        RemoveShortcuts(item.Profile);
        ShortcutIcon.DeleteFor(item.Id);
        _store.Delete(item.Id);
        await RefreshAsync();
        ShowStatus("Profile deleted", $"'{item.Name}' removed.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
    }

    public async Task ToggleHdrAsync(DisplayItemViewModel display, bool enable)
    {
        if (IsBusy)
        {
            display.SetHdrSilently(!enable);
            return;
        }

        try
        {
            IsBusy = true;
            BusyText = $"Turning HDR {(enable ? "on" : "off")} for {display.Name}…";

            var ok = await Task.Run(async () =>
            {
                var snapshot = _displayService.Capture();
                var pseudo = new VantageProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "(hdr toggle)",
                    Displays = snapshot.Displays.Select(d => new ProfileDisplay
                    {
                        Identity = d.Identity,
                        Primary = d.IsPrimary,
                        PositionX = d.PositionX,
                        PositionY = d.PositionY,
                        Width = d.Width,
                        Height = d.Height,
                        RefreshMillihertz = d.RefreshMillihertz,
                        Rotation = d.Rotation,
                        HdrEnabled = string.Equals(d.Identity.StableId, display.StableId, StringComparison.OrdinalIgnoreCase)
                            ? enable
                            : null,
                    }).ToList(),
                    Replay = snapshot.Replay,
                };
                var report = await _engine.ApplyAsync(pseudo);
                return report.Succeeded;
            });

            if (!ok)
                ShowStatus("HDR change not verified", $"Windows did not confirm the HDR change on {display.Name}.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            ShowStatus("HDR toggle failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Raised alongside every status shown in the InfoBar, so the app can mirror warnings
    /// and errors to the tray when the window is hidden — which is this app's default state.
    /// </summary>
    public event Action<string, string, Wpf.Ui.Controls.InfoBarSeverity>? StatusReported;

    /// <summary>Lets the app surface its own messages through the same InfoBar.</summary>
    public void ReportStatus(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity) =>
        ShowStatus(title, message, severity);

    private void ShowStatus(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
        StatusReported?.Invoke(title, message, severity);
    }
}
