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
        LayoutImage = LayoutThumbnail.Render(profile.Displays
            .Where(d => d.Enabled)
            .Select(d => new ThumbnailDisplay(d.PositionX, d.PositionY, d.Width, d.Height, d.Primary, d.HdrEnabled == true))
            .ToList());
    }
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

        _suppressSettingSideEffects = true;
        StartWithWindows = StartupManager.IsEnabled();
        _suppressSettingSideEffects = false;

        _ = RefreshAsync();
    }

    // --- Settings ---

    [ObservableProperty] private bool _startWithWindows;

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

        RefreshHotkeyRegistrations(envelope);
    }

    // --- Hotkeys ---

    private HotkeyService? _hotkeys;

    public void AttachHotkeyService(HotkeyService hotkeys)
    {
        _hotkeys = hotkeys;
        RefreshHotkeyRegistrations(_store.Load());
    }

    private void RefreshHotkeyRegistrations(ProfileFileEnvelope envelope)
    {
        if (_hotkeys is null)
            return;
        var failures = _hotkeys.RegisterAll(envelope.Profiles
            .Where(p => p.Hotkey is { Length: > 0 })
            .Select(p => (p.Id, p.Hotkey!)));
        if (failures.Count > 0)
            ShowStatus("Some hotkeys are unavailable",
                $"Already in use by another app: {string.Join(", ", failures)}",
                Wpf.Ui.Controls.InfoBarSeverity.Warning);
    }

    public async Task OnHotkeyPressedAsync(Guid profileId)
    {
        var item = Profiles.FirstOrDefault(p => p.Id == profileId);
        if (item is not null && item.IsPossible && !IsBusy)
            await ApplyProfileAsync(item);
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
                ShowStatus("Change didn't verify — reverted automatically",
                    report.FailureReason ?? "The previous configuration was restored.",
                    Wpf.Ui.Controls.InfoBarSeverity.Warning);
            }
            else
            {
                ShowStatus("Apply failed", report.FailureReason ?? "See log.", Wpf.Ui.Controls.InfoBarSeverity.Error);
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

    private void ShowStatus(string title, string message, Wpf.Ui.Controls.InfoBarSeverity severity)
    {
        StatusTitle = title;
        StatusMessage = message;
        StatusSeverity = severity;
        StatusOpen = true;
    }
}
