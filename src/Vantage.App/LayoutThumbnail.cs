using System.Windows;
using System.Windows.Media;

namespace Vantage.App;

public sealed record ThumbnailDisplay(double X, double Y, double Width, double Height, bool Primary, bool HdrOn);

/// <summary>
/// Renders a miniature of a monitor arrangement (DisplayMagician-style profile icons, but
/// proportional and accent-aware): primary display filled with the Windows accent color,
/// secondaries in neutral gray, each with a little stand. Output is a frozen DrawingImage,
/// cheap to bind into lists.
/// </summary>
public static class LayoutThumbnail
{
    public static ImageSource Render(IReadOnlyList<ThumbnailDisplay> displays, double targetWidth = 88, double targetHeight = 40)
    {
        var group = new DrawingGroup();

        if (displays.Count > 0)
        {
            // Bounding box of the arrangement in desktop coordinates.
            var minX = displays.Min(d => d.X);
            var minY = displays.Min(d => d.Y);
            var maxX = displays.Max(d => d.X + d.Width);
            var maxY = displays.Max(d => d.Y + d.Height);

            const double standHeight = 5;   // room for monitor stands below the panels
            const double gap = 2.5;         // visual breathing room between adjacent panels

            var scale = Math.Min(
                (targetWidth - 2) / Math.Max(1, maxX - minX),
                (targetHeight - 2 - standHeight) / Math.Max(1, maxY - minY));

            var accent = Application.Current?.Resources["SystemAccentColorPrimary"] is Color c
                ? c
                : Color.FromRgb(0xA9, 0x4D, 0xC1);
            var accentBrush = Freeze(new SolidColorBrush(accent));
            var accentDim = Freeze(new SolidColorBrush(Color.FromArgb(0xFF,
                (byte)(accent.R * 0.75), (byte)(accent.G * 0.75), (byte)(accent.B * 0.75))));
            var grayBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x6E)));
            var standBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)));

            foreach (var d in displays)
            {
                var rect = new Rect(
                    (d.X - minX) * scale + 1 + gap / 2,
                    (d.Y - minY) * scale + 1 + gap / 2,
                    Math.Max(3, d.Width * scale - gap),
                    Math.Max(3, d.Height * scale - gap));

                var fill = d.Primary ? accentBrush : grayBrush;
                group.Children.Add(new GeometryDrawing(
                    fill, null, new RectangleGeometry(rect, 1.5, 1.5)));

                // HDR badge: small text label, top-left corner with even padding, unscaled.
                if (d.HdrOn)
                {
                    var label = new FormattedText(
                        "HDR",
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                        6.0,
                        Brushes.White,
                        pixelsPerDip: 1.0);
                    const double pad = 3;
                    var geometry = label.BuildGeometry(new Point(rect.X + pad, rect.Y + pad));
                    geometry.Freeze();
                    group.Children.Add(new GeometryDrawing(Freeze(new SolidColorBrush(Colors.White)), null, geometry));
                }

                // Stand: small centered pedestal under the panel.
                var standWidth = Math.Max(4, rect.Width * 0.18);
                var stand = new Rect(rect.X + (rect.Width - standWidth) / 2, rect.Bottom + 1, standWidth, 2.5);
                group.Children.Add(new GeometryDrawing(
                    d.Primary ? accentDim : standBrush, null, new RectangleGeometry(stand, 1, 1)));
            }
        }

        // Pin the canvas size so items align in lists regardless of arrangement shape.
        group.Children.Insert(0, new GeometryDrawing(
            Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, targetWidth, targetHeight))));

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
