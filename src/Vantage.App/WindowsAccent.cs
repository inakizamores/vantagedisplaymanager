using Microsoft.Win32;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Vantage.App;

/// <summary>
/// Applies the user's exact Windows accent palette, picking the same shade per theme that
/// Windows itself uses. Windows precomputes the accent shades (Light3..Dark3) and stores
/// them in Explorer\Accent\AccentPalette. Native WinUI surfaces never fill with the base
/// accent: dark mode uses Light2 with black text, light mode uses Dark1 with white text.
/// Filling with the base accent (or a library-derived approximation) is what makes an app
/// look noticeably darker/duller than Settings and the shell.
/// </summary>
public static class WindowsAccent
{
    // Byte offsets into AccentPalette — 8 RGBA entries, lightest shade first.
    private const int Light3 = 0, Light2 = 4, Light1 = 8, BaseAccent = 12, Dark1 = 16, Dark2 = 20, Dark3 = 24;

    public static void Apply()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Accent", writable: false);

            if (key?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 28)
            {
                ApplyPalette(palette);
                return;
            }
        }
        catch
        {
            // Fall through to the library's own approximation.
        }

        ApplicationAccentColorManager.ApplySystemAccent();
    }

    private static void ApplyPalette(byte[] palette)
    {
        Color Shade(int offset) => Color.FromRgb(palette[offset], palette[offset + 1], palette[offset + 2]);

        var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

        // The layer WinUI's translucent hover/pressed accent states composite over.
        var backdrop = dark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;

        // Accent-filled surfaces: buttons, toggle switches, selection.
        var fill = dark ? Shade(Light2) : Shade(Dark1);
        var fillHover = Blend(fill, backdrop, 0.90);
        var fillPressed = Blend(fill, backdrop, 0.80);

        // Accent-colored text sitting on the page background — pushed further from the
        // backdrop than the fill so it stays legible.
        var textPrimary = dark ? Shade(Light3) : Shade(Dark2);
        var textSecondary = dark ? Shade(Light3) : Shade(Dark3);
        var textTertiary = dark ? Shade(Light2) : Shade(Dark1);

        // Text drawn *on* an accent fill inverts with the theme, because the fill itself does.
        var onAccent = dark ? Colors.Black : Colors.White;
        var onAccentSecondary = dark
            ? Color.FromArgb(0x80, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF);

        ApplicationAccentColorManager.Apply(Shade(BaseAccent), fill, fillHover, fillPressed);

        Set("AccentFillColorDefault", fill);
        Set("AccentFillColorSecondary", fillHover);
        Set("AccentFillColorTertiary", fillPressed);
        Set("AccentTextFillColorPrimary", textPrimary);
        Set("AccentTextFillColorSecondary", textSecondary);
        Set("AccentTextFillColorTertiary", textTertiary);
        Set("TextOnAccentFillColorPrimary", onAccent);
        Set("TextOnAccentFillColorSecondary", onAccentSecondary);
        Set("TextOnAccentFillColorSelectedText", Colors.White);
    }

    /// <summary>
    /// Overrides both the Color and the Brush resource so a control template picks the value
    /// up whichever form it binds to. Entries set directly on Application.Resources win over
    /// the theme dictionaries merged into it.
    /// </summary>
    private static void Set(string key, Color color)
    {
        var resources = System.Windows.Application.Current.Resources;
        resources[key] = color;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key + "Brush"] = brush;
    }

    private static Color Blend(Color over, Color under, double alpha) => Color.FromRgb(
        (byte)Math.Round(over.R * alpha + under.R * (1 - alpha)),
        (byte)Math.Round(over.G * alpha + under.G * (1 - alpha)),
        (byte)Math.Round(over.B * alpha + under.B * (1 - alpha)));
}
