using Microsoft.Win32;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Vantage.App;

/// <summary>
/// Applies the user's exact Windows accent palette. Windows precomputes the accent
/// shades (Light3..Dark3) and stores them in Explorer\Accent\AccentPalette — using
/// them verbatim guarantees pixel-identical accent colors with Settings/shell,
/// instead of letting the UI library derive its own (slightly off) shades.
/// </summary>
public static class WindowsAccent
{
    public static void Apply()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent", writable: false);

            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 28)
            {
                // Layout: 8 RGBA entries — Light3, Light2, Light1, Accent, Dark1, Dark2, Dark3, complement.
                Color At(int offset) => Color.FromRgb(palette[offset], palette[offset + 1], palette[offset + 2]);

                var accent = At(12);
                var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

                // Fill = the base accent with white text (like the Settings display tile);
                // hover/pressed walk one/two shades toward the theme.
                var secondary = dark ? At(8) : At(16);
                var tertiary = dark ? At(4) : At(20);
                ApplicationAccentColorManager.Apply(accent, accent, secondary, tertiary);

                var app = System.Windows.Application.Current;
                app.Resources["TextOnAccentFillColorPrimary"] = Colors.White;
                app.Resources["TextOnAccentFillColorPrimaryBrush"] = new SolidColorBrush(Colors.White);
                app.Resources["TextOnAccentFillColorSecondaryBrush"] = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
                return;
            }
        }
        catch
        {
            // Fall through to the library's own approximation.
        }

        ApplicationAccentColorManager.ApplySystemAccent();
    }
}
