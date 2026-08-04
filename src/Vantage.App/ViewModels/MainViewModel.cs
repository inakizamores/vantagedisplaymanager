using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
            ? d.Hdr.Enabled
                ? $" · HDR on ({d.Hdr.BitsPerColorChannel}-bit)"
                : " · HDR off"
            : "";
        DetailText = $"{Prettify(d.OutputTechnology)}{scale}{hdrText}";
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

    public void Update(VantageProfile profile, ProfileMatchResult match)
    {
        Profile = profile;
        Name = profile.Name;
        IsActive = match.IsActive;
        IsPossible = match.IsPossible;
        StatusText = match.IsActive ? "Active" : match.IsPossible ? "" : "Displays not connected";
        SummaryText = string.Join("  ·  ", profile.Displays.Where(d => d.Enabled)
            .Select(d => $"{d.Width}×{d.Height}@{Math.Round(d.RefreshMillihertz / 1000.0)}"));
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly DisplayService _displayService;
    private readonly ProfileStore _store;
    private readonly ApplyEngine _engine;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private VantageProfile? _revertProfile;
    private DispatcherTimer? _revertTimer;
    private int _revertSecondsLeft;

    public MainViewModel(DisplayService displayService, ProfileStore store, ApplyEngine engine)
    {
        _displayService = displayService;
        _store = store;
        _engine = engine;
        _ = RefreshAsync();
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

    // Auto-revert countdown bar (BLUEPRINT §5: "keep these settings?")
    [ObservableProperty] private bool _revertBarVisible;
    [ObservableProperty] private string _revertBarText = "";

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
            CancelRevertCountdown(applyRevert: false);

            // Snapshot the pre-apply state so the countdown can restore it.
            BusyText = $"Applying '{item.Name}'…";
            var before = await Task.Run(_displayService.Capture);
            _revertProfile = ProfileStore.FromSnapshot(before, "(revert)");

            var progress = new Progress<ApplyProgress>(p => BusyText = p.Message);
            var report = await Task.Run(() => _engine.ApplyAsync(item.Profile, progress));

            await RefreshAsync();

            if (report.Succeeded)
            {
                StartRevertCountdown();
            }
            else
            {
                ShowStatus("Apply finished with problems", report.FailureReason ?? "See log.", Wpf.Ui.Controls.InfoBarSeverity.Warning);
                _revertProfile = null;
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Apply failed", ex.Message, Wpf.Ui.Controls.InfoBarSeverity.Error);
            _revertProfile = null;
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
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Delete profile",
            Content = $"Delete '{item.Name}'? This cannot be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        var result = await box.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

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

    private void StartRevertCountdown()
    {
        _revertSecondsLeft = 15;
        RevertBarVisible = true;
        UpdateRevertText();

        _revertTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _revertTimer.Tick += async (_, _) =>
        {
            _revertSecondsLeft--;
            if (_revertSecondsLeft <= 0)
            {
                await RevertNowAsync();
                return;
            }
            UpdateRevertText();
        };
        _revertTimer.Start();
    }

    private void UpdateRevertText() =>
        RevertBarText = $"Display settings changed. Reverting in {_revertSecondsLeft} s unless you keep them.";

    [RelayCommand]
    private void KeepChanges() => CancelRevertCountdown(applyRevert: false);

    [RelayCommand]
    private async Task RevertNowAsync()
    {
        var revert = _revertProfile;
        CancelRevertCountdown(applyRevert: false);
        if (revert is null)
            return;

        try
        {
            IsBusy = true;
            BusyText = "Restoring previous configuration…";
            await Task.Run(() => _engine.ApplyAsync(revert));
            ShowStatus("Reverted", "Previous display configuration restored.", Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            await RefreshAsync();
        }
    }

    private void CancelRevertCountdown(bool applyRevert)
    {
        _revertTimer?.Stop();
        _revertTimer = null;
        RevertBarVisible = false;
        if (!applyRevert)
            _revertProfile = null;
    }

    private void ShowStatus(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }
}
