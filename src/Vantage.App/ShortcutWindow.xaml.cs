using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vantage.App.Services;
using Vantage.Core.Models;

namespace Vantage.App;

/// <summary>What the user asked for in <see cref="ShortcutWindow"/>.</summary>
public sealed record ShortcutPlan(
    string Name,
    IReadOnlyList<ShortcutLocation> Locations,
    string? CustomIconPath,
    bool GroupInStartMenu,
    bool Remove);

/// <summary>
/// Turns a profile into a launchable shortcut: where it goes, what it's called, and which
/// icon it wears. The icon defaults to the same monitor-layout picture the profile card
/// shows, so a Start-menu row full of presets is readable at a glance.
/// </summary>
public partial class ShortcutWindow : Window
{
    private readonly VantageProfile _profile;
    private readonly System.Windows.Media.ImageSource _layoutPreview;
    private readonly bool _hasExistingShortcuts;
    private string? _customIconPath;
    private bool _ready;

    public ShortcutPlan? Plan { get; private set; }

    public ShortcutWindow(VantageProfile profile)
    {
        InitializeComponent();
        _profile = profile;

        _layoutPreview = ShortcutIcon.RenderLayoutPreview(ThumbnailDisplay.From(profile));

        var existing = (profile.ShortcutPaths ?? []).Where(File.Exists).ToList();
        _hasExistingShortcuts = existing.Count > 0;

        NameBox.Text = _hasExistingShortcuts
            ? Path.GetFileNameWithoutExtension(existing[0])
            : profile.Name;

        if (_hasExistingShortcuts)
        {
            StartMenuCheck.IsChecked = existing.Any(ShortcutService.IsInStartMenu);
            DesktopCheck.IsChecked = existing.Any(p => IsIn(p, ShortcutLocation.Desktop));
            GroupCheck.IsChecked = existing.Any(ShortcutService.IsGrouped);
            RemoveButton.Visibility = Visibility.Visible;
            CreateButton.Content = "Update shortcut";
        }

        // A custom icon the user picked last time is remembered; a generated one is not,
        // since it is rebuilt from the layout every time anyway.
        if (profile.IconPath is { Length: > 0 } icon && File.Exists(icon) &&
            !string.Equals(Path.GetDirectoryName(icon), Vantage.Core.Services.VantageDataPaths.ShortcutIconsDir, StringComparison.OrdinalIgnoreCase))
        {
            _customIconPath = icon;
            CustomIconChoice.IsChecked = true;
        }

        _ready = true;
        UpdateCustomIconText();
        UpdatePreview();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeChrome.Apply(this);
    }

    private static bool IsIn(string path, ShortcutLocation location) =>
        string.Equals(Path.GetDirectoryName(path), ShortcutService.FolderFor(location), StringComparison.OrdinalIgnoreCase);

    private void OnNameChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void OnIconChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;

        // Choosing "Custom" with nothing picked yet goes straight to the file browser —
        // otherwise the radio button sits selected doing nothing.
        if (CustomIconChoice.IsChecked == true && _customIconPath is null)
            Browse();

        UpdateCustomIconText();
        UpdatePreview();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        CustomIconChoice.IsChecked = true;
        Browse();
        UpdateCustomIconText();
        UpdatePreview();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an icon",
            Filter = "Icons, images and programs|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.exe;*.dll|"
                   + "Icons|*.ico|Images|*.png;*.jpg;*.jpeg;*.bmp|Programs|*.exe;*.dll|All files|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
            _customIconPath = dialog.FileName;
        else if (_customIconPath is null)
            LayoutIconChoice.IsChecked = true;   // cancelled with nothing picked — fall back
    }

    private void UpdateCustomIconText()
    {
        var showing = CustomIconChoice.IsChecked == true && _customIconPath is not null;
        CustomIconText.Text = showing ? _customIconPath : "";
        CustomIconText.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePreview()
    {
        if (!_ready)
            return;

        var name = NameBox.Text.Trim();
        PreviewName.Text = name.Length > 0 ? name : _profile.Name;
        PreviewTarget.Text = $"Vantage.exe {LaunchCommand.ApplySwitch} {_profile.Id}";

        IconPreview.Source = CustomIconChoice.IsChecked == true && _customIconPath is { } custom
            ? ShortcutIcon.TryRenderFilePreview(custom) ?? _layoutPreview
            : _layoutPreview;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        Plan = new ShortcutPlan(NameBox.Text.Trim(), [], null, GroupInStartMenu: false, Remove: true);
        DialogResult = true;
        Close();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Give the shortcut a name.");
            return;
        }

        List<ShortcutLocation> locations = [];
        if (StartMenuCheck.IsChecked == true)
            locations.Add(ShortcutLocation.StartMenu);
        if (DesktopCheck.IsChecked == true)
            locations.Add(ShortcutLocation.Desktop);

        if (locations.Count == 0)
        {
            ShowError(_hasExistingShortcuts
                ? "Pick at least one place — or use Remove to take the shortcut away."
                : "Pick at least one place to put the shortcut.");
            return;
        }

        var customIcon = CustomIconChoice.IsChecked == true ? _customIconPath : null;
        if (CustomIconChoice.IsChecked == true && customIcon is null)
        {
            ShowError("Choose an image for the icon, or switch back to the monitor layout.");
            return;
        }

        Plan = new ShortcutPlan(name, locations, customIcon, GroupCheck.IsChecked == true, Remove: false);
        DialogResult = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
