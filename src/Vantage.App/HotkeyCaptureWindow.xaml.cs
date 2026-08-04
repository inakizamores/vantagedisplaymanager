using System.Windows;
using System.Windows.Input;
using Vantage.App.Services;

namespace Vantage.App;

/// <summary>Captures a key combination. Result: DialogResult true + Gesture (null = remove hotkey).</summary>
public partial class HotkeyCaptureWindow : Window
{
    public string? Gesture { get; private set; }

    public HotkeyCaptureWindow(string profileName, string? currentGesture)
    {
        InitializeComponent();
        TitleText.Text = $"Hotkey for '{profileName}'";
        if (currentGesture is { Length: > 0 })
            GestureText.Text = HotkeyService.FormatGesture(currentGesture);
        ClearButton.Visibility = currentGesture is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeChrome.Apply(this);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return; // modifiers alone don't complete a gesture
        }

        if (key == Key.Escape)
        {
            Close();
            return;
        }

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        if (parts.Count == 0)
        {
            GestureText.Text = "Add a modifier (Ctrl, Alt, Shift or Win)…";
            SaveButton.IsEnabled = false;
            return;
        }

        parts.Add(key.ToString());
        Gesture = string.Join("+", parts);
        GestureText.Text = HotkeyService.FormatGesture(Gesture);
        SaveButton.IsEnabled = true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        Gesture = null;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
