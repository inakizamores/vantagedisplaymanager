using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vantage.Core.Services;

namespace Vantage.App.Services;

/// <summary>
/// Builds the icons that preset shortcuts wear. A profile's icon is either the same
/// monitor-layout thumbnail the app shows on its card — rendered here into a real
/// multi-resolution <c>.ico</c> so the Start menu, the taskbar and Alt-Tab each get a
/// crisp size — or an image the user picked.
///
/// Icons are written to <see cref="VantageDataPaths.ShortcutIconsDir"/> under a name that
/// changes every time they are regenerated: Explorer caches icons by path, so reusing one
/// would leave a stale picture on the shortcut until the icon cache was rebuilt.
/// </summary>
public static class ShortcutIcon
{
    /// <summary>Sizes Windows actually asks for, from Start-menu list rows up to Large icons view.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 48, 64, 128, 256];

    /// <summary>Logical canvas the layout is drawn on before being scaled into each icon size.</summary>
    private const double LogicalWidth = 128;
    private const double LogicalHeight = 76;

    /// <summary>
    /// Below this the layout is drawn without its HDR badges. The label is 6 pt on a 76-unit
    /// canvas, so it only stays legible once the icon is 128 px or bigger; smaller than that
    /// it renders as a smudge in the corner of the panel.
    /// </summary>
    private const int BadgeThreshold = 128;

    /// <summary>Renders a profile's monitor arrangement to a fresh .ico and returns its path.</summary>
    public static string WriteLayoutIcon(Guid profileId, IReadOnlyList<ThumbnailDisplay> displays)
        => Write(profileId, size => RenderLayout(displays, size));

    /// <summary>Converts a user-picked image (png/jpg/bmp/ico) to a fresh .ico and returns its path.</summary>
    public static string WriteImageIcon(Guid profileId, string sourceImagePath)
    {
        var source = LoadImage(sourceImagePath);
        return Write(profileId, size => RenderFitted(source, size));
    }

    /// <summary>A preview of what the shortcut will look like, at one size.</summary>
    public static ImageSource RenderLayoutPreview(IReadOnlyList<ThumbnailDisplay> displays, int size = 64)
        => RenderLayout(displays, size);

    /// <summary>
    /// A preview of a user-picked icon source. Handles .exe/.dll by pulling out their first
    /// icon, the same one the shortcut would show.
    /// </summary>
    public static ImageSource? TryRenderFilePreview(string path, int size = 64)
    {
        try
        {
            return IsIconContainer(path)
                ? ExtractFirstIcon(path)
                : RenderFitted(LoadImage(path), size);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or COMException)
        {
            return null;
        }
    }

    /// <summary>True for files Windows can take an icon out of directly, with no conversion.</summary>
    public static bool IsIconContainer(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".exe" or ".dll" or ".ico";

    /// <summary>Removes every generated icon for a profile. Safe to call when none exist.</summary>
    public static void DeleteFor(Guid profileId)
    {
        if (!Directory.Exists(VantageDataPaths.ShortcutIconsDir))
            return;

        foreach (var file in Directory.EnumerateFiles(VantageDataPaths.ShortcutIconsDir, profileId + "*.ico"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A locked icon (Explorer reading it right now) is not worth failing over.
            }
        }
    }

    private static string Write(Guid profileId, Func<int, BitmapSource> render)
    {
        Directory.CreateDirectory(VantageDataPaths.ShortcutIconsDir);
        DeleteFor(profileId);

        var path = Path.Combine(
            VantageDataPaths.ShortcutIconsDir,
            $"{profileId}-{DateTime.UtcNow.Ticks & 0xFFFFFFFF:x8}.ico");

        var images = Sizes.Select(size => (Size: size, Data: Encode(render(size), size))).ToList();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((short)0);               // reserved
        writer.Write((short)1);               // type: icon
        writer.Write((short)images.Count);

        // ICONDIRENTRY per image; 256 px is encoded as 0 in the single-byte dimensions.
        var offset = 6 + images.Count * 16;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);            // palette entries (none — true color)
            writer.Write((byte)0);            // reserved
            writer.Write((short)1);           // color planes
            writer.Write((short)32);          // bits per pixel
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
            writer.Write(data);

        return path;
    }

    /// <summary>
    /// PNG above 64 px (the modern container, and far smaller for the 256 px entry), a plain
    /// 32-bit DIB below it — the format every shell version has always read.
    /// </summary>
    private static byte[] Encode(BitmapSource bitmap, int size) =>
        size > 64 ? EncodePng(bitmap) : EncodeDib(bitmap, size);

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static byte[] EncodeDib(BitmapSource bitmap, int size)
    {
        // Straight (non-premultiplied) BGRA is what a 32-bit DIB expects.
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = size * 4;
        var pixels = new byte[stride * size];
        converted.CopyPixels(pixels, stride, 0);

        // The AND mask is 1 bpp with rows padded to 4 bytes. Left all-zero: on a 32-bit
        // icon Windows takes transparency from the alpha channel, not from the mask.
        var maskStride = (size + 31) / 32 * 4;
        var mask = new byte[maskStride * size];

        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);

        // BITMAPINFOHEADER — height covers the colour data and the mask stacked together.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);                      // BI_RGB
        writer.Write(pixels.Length + mask.Length);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // DIB rows run bottom-up.
        for (var y = size - 1; y >= 0; y--)
            writer.Write(pixels, y * stride, stride);
        writer.Write(mask);

        writer.Flush();
        return buffer.ToArray();
    }

    private static BitmapSource RenderLayout(IReadOnlyList<ThumbnailDisplay> displays, int size)
    {
        var source = size < BadgeThreshold
            ? displays.Select(d => d with { HdrOn = false }).ToList()
            : displays;

        var layout = LayoutThumbnail.Render(source, LogicalWidth, LogicalHeight);

        // Full width, vertically centred — same aspect as the logical canvas, so no distortion.
        var height = size * (LogicalHeight / LogicalWidth);
        return RenderVisual(size, dc => dc.DrawImage(layout, new Rect(0, (size - height) / 2, size, height)));
    }

    private static BitmapSource RenderFitted(BitmapSource image, int size)
    {
        var scale = Math.Min(size / (double)image.PixelWidth, size / (double)image.PixelHeight);
        var width = image.PixelWidth * scale;
        var height = image.PixelHeight * scale;
        return RenderVisual(size, dc => dc.DrawImage(image, new Rect((size - width) / 2, (size - height) / 2, width, height)));
    }

    private static BitmapSource RenderVisual(int size, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            draw(dc);

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource LoadImage(string path)
    {
        // OnLoad so the file is not left open — the user may well pick it again next time.
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(path)),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnLoad);
        frame.Freeze();
        return frame;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, [Out] IntPtr[]? large, [Out] IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private static ImageSource? ExtractFirstIcon(string path)
    {
        var large = new IntPtr[1];
        if (ExtractIconEx(Path.GetFullPath(path), 0, large, null, 1) == 0 || large[0] == IntPtr.Zero)
            return null;

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(large[0]);
        }
    }
}
