using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Vantage.Core.Models;
using Vantage.Core.Services;
using Vantage.Interop.Gdi;

namespace Vantage.App;

public partial class PresetEditorWindow : Window
{
    public sealed partial class DisplayRow : ObservableObject
    {
        private readonly List<GdiApi.GdiMode> _modes;

        public DisplayRow(DisplayState live, List<GdiApi.GdiMode> modes)
        {
            Live = live;
            _modes = modes;
            Name = live.Identity.FriendlyName ?? live.Identity.StableId;
            Resolutions = modes.Select(m => $"{m.Width} × {m.Height}").Distinct().ToList();
            HdrVisibility = live.Hdr.Supported ? Visibility.Visible : Visibility.Collapsed;
            HdrOn = live.Hdr.Enabled;

            SelectedResolution = $"{live.Width} × {live.Height}";
            if (!Resolutions.Contains(SelectedResolution))
                SelectedResolution = Resolutions.FirstOrDefault() ?? "";
        }

        public DisplayState Live { get; }
        public string Name { get; }
        public List<string> Resolutions { get; }
        public Visibility HdrVisibility { get; }

        [ObservableProperty] private string _selectedResolution = "";
        [ObservableProperty] private List<uint> _refreshRates = [];
        [ObservableProperty] private uint _selectedRefreshRate;
        [ObservableProperty] private bool _hdrOn;

        partial void OnSelectedResolutionChanged(string value)
        {
            var (w, h) = ParseResolution(value);
            RefreshRates = _modes.Where(m => m.Width == w && m.Height == h)
                .Select(m => m.RefreshHz).Distinct().OrderDescending().ToList();
            // Prefer the current rate when available at this resolution, else the highest.
            var currentHz = (uint)Math.Round(Live.RefreshMillihertz / 1000.0);
            SelectedRefreshRate = RefreshRates.Contains(currentHz) ? currentHz : RefreshRates.FirstOrDefault();
        }

        public static (uint W, uint H) ParseResolution(string text)
        {
            var parts = text.Split('×', StringSplitOptions.TrimEntries);
            return parts.Length == 2 && uint.TryParse(parts[0], out var w) && uint.TryParse(parts[1], out var h)
                ? (w, h)
                : (0, 0);
        }
    }

    private readonly SystemSnapshot _snapshot;
    private readonly List<DisplayRow> _rows;

    public VantageProfile? CreatedProfile { get; private set; }

    public PresetEditorWindow(SystemSnapshot snapshot)
    {
        InitializeComponent();
        _snapshot = snapshot;
        _rows = snapshot.Displays
            .Select(d => new DisplayRow(d, d.GdiDeviceName is { Length: > 0 } name ? GdiApi.EnumerateModes(name) : []))
            .ToList();
        Rows.ItemsSource = _rows;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeChrome.Apply(this);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Give the preset a name.");
            return;
        }

        try
        {
            var overrides = _rows.Select(row =>
            {
                var (w, h) = DisplayRow.ParseResolution(row.SelectedResolution);
                return new DisplayOverride
                {
                    StableId = row.Live.Identity.StableId,
                    Width = w > 0 ? w : null,
                    Height = h > 0 ? h : null,
                    RefreshHz = row.SelectedRefreshRate > 0 ? row.SelectedRefreshRate : null,
                    HdrEnabled = row.Live.Hdr.Supported ? row.HdrOn : null,
                };
            }).ToList();

            CreatedProfile = ProfileVariantBuilder.Build(_snapshot, name, overrides);
            DialogResult = true;
            Close();
        }
        catch (InvalidOperationException ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
