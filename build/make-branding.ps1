# Renders every raster brand asset from one geometry definition:
#   src/Vantage.App/Assets/vantage.ico   app, window and tray icon
#   docs/assets/vantage-mark-512.png     square mark for docs and packaging
#   docs/assets/vantage-lockup-*.png     README header, light and dark GitHub themes
#   docs/assets/vantage-social.png       GitHub repository social preview (1280x640)
#
# The mark is two display panels angled inward as if seen from above; the gap between them
# is the vantage point. docs/assets/vantage-mark.svg is the vector source of truth — keep
# the constants below in sync with it.
#
# In the .ico, sizes <= 64 are classic BMP entries (the shell only reliably decodes PNG at
# 256px); 128 and 256 are PNG. Logic lives in C# to avoid PowerShell array-coercion pitfalls.
$source = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class Branding
{
    // Palette
    static readonly Color TileFrom    = ColorTranslator.FromHtml("#3A2FB8");
    static readonly Color TileTo      = ColorTranslator.FromHtml("#1E86F0");
    static readonly Color PanelIdle   = Color.White;
    static readonly Color PanelActive = ColorTranslator.FromHtml("#5CE1FF");

    // Mark geometry, expressed in the 128-unit design grid.
    const float Grid = 128f, TileRadius = 28f;
    const float HalfLen = 27.2f, HalfThick = 10f, PanelRadius = 5.5f;
    const float PanelAngle = 65.7f;
    const float PivotY = 63f, PivotLeftX = 41.6f, PivotRightX = 86.4f;
    const float CenterX = 64f, CenterY = 63f;

    const string Face = "Segoe UI";

    // ---------------------------------------------------------------- mark

    static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var p = new GraphicsPath();
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Sizes at or below this get a purpose-drawn variant instead of a shrunk one.
    public static bool IsSmall(int size) { return size <= 32; }

    static void Panel(Graphics g, float pivotX, float pivotY, float halfThick, float radius,
                      float angle, Color color)
    {
        using (var path = RoundedRect(-HalfLen, -halfThick, HalfLen * 2, halfThick * 2, radius))
        using (var m = new Matrix())
        using (var b = new SolidBrush(color))
        {
            m.Rotate(angle, MatrixOrder.Append);
            m.Translate(pivotX, pivotY, MatrixOrder.Append);
            path.Transform(m);
            g.FillPath(b, path);
        }
    }

    // Two things break when the full-detail mark is merely scaled down past ~32px. The cyan
    // panel sits over the bright end of the tile gradient, where it has roughly 1.7:1 contrast
    // against about 6:1 for the white panel, so once it is two pixels wide it dissolves and the
    // V reads as a single bar leaning left. And the gap at the vertex falls below half a pixel,
    // which antialiasing turns to mush. The small variant therefore uses two white panels,
    // thicker, converged into one solid V.
    public static void DrawMark(Graphics g, float x, float y, float size, bool small)
    {
        var state = g.Save();
        g.TranslateTransform(x, y);
        g.ScaleTransform(size / Grid, size / Grid);

        using (var tile = RoundedRect(0, 0, Grid, Grid, TileRadius))
        // Inflate the gradient rect by a unit: GDI+ otherwise bleeds the end colour into
        // the first pixel column.
        using (var brush = new LinearGradientBrush(
                   new RectangleF(-1, -1, Grid + 2, Grid + 2), TileFrom, TileTo, LinearGradientMode.ForwardDiagonal))
            g.FillPath(brush, tile);

        // Small pivots sit closer together than the full-detail ones so the two panels
        // overlap at the vertex and fuse into a solid point. At 43/85 they stop 4 units
        // short of each other, and that gap becomes a notch that reads as a w at 16px.
        float halfThick = small ? 11.0f : HalfThick;
        float radius    = small ? 4.0f  : PanelRadius;
        float pivotL    = small ? 44.0f : PivotLeftX;
        float pivotR    = small ? 84.0f : PivotRightX;
        float pivotY    = small ? 64.0f : PivotY;
        Color rightCol  = small ? PanelIdle : PanelActive;

        Panel(g, pivotL, pivotY, halfThick, radius,  PanelAngle, PanelIdle);
        Panel(g, pivotR, pivotY, halfThick, radius, -PanelAngle, rightCol);
        g.Restore(state);
    }

    // Supersample, then downscale on premultiplied alpha. GDI+ antialiasing of a thin rotated
    // shape at 16px is poor on its own, and interpolating straight alpha would drag the
    // transparent-black outside the tile into its rounded corners as a dark fringe.
    static Bitmap RenderMark(int size)
    {
        if (size >= 128)
            return RenderMarkDirect(size, IsSmall(size));

        const int Factor = 8;
        using (var big = RenderMarkDirect(size * Factor, IsSmall(size)))
        using (var pre = Premultiply(big))
        using (var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(pre, new Rectangle(0, 0, size, size),
                            new Rectangle(0, 0, pre.Width, pre.Height), GraphicsUnit.Pixel);
            }
            return Unpremultiply(scaled);
        }
    }

    static Bitmap RenderMarkDirect(int size, bool small)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            DrawMark(g, 0, 0, size, small);
        }
        return bmp;
    }

    static Bitmap MapAlpha(Bitmap src, bool forward)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        var s = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var d = dst.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int count = src.Width * src.Height * 4;
            var buf = new byte[count];
            for (int y = 0; y < src.Height; y++)
                Marshal.Copy(s.Scan0 + y * s.Stride, buf, y * src.Width * 4, src.Width * 4);

            for (int i = 0; i < count; i += 4)
            {
                int a = buf[i + 3];
                for (int c = 0; c < 3; c++)   // BGRA in memory
                {
                    int v = buf[i + c];
                    if (forward)
                        v = v * a / 255;
                    else
                        v = a == 0 ? 0 : Math.Min(255, v * 255 / a);
                    buf[i + c] = (byte)v;
                }
            }

            for (int y = 0; y < src.Height; y++)
                Marshal.Copy(buf, y * src.Width * 4, d.Scan0 + y * d.Stride, src.Width * 4);
        }
        finally
        {
            src.UnlockBits(s);
            dst.UnlockBits(d);
        }
        return dst;
    }

    static Bitmap Premultiply(Bitmap src) { return MapAlpha(src, true); }
    static Bitmap Unpremultiply(Bitmap src) { return MapAlpha(src, false); }

    // ---------------------------------------------------------------- text

    static GraphicsPath TextPath(string text, FontStyle style, float em)
    {
        var p = new GraphicsPath();
        using (var ff = new FontFamily(Face))
            p.AddString(text, ff, (int)style, em, new PointF(0, 0), StringFormat.GenericTypographic);
        return p;
    }

    // Moves a path so its ink bounds start at (x, y).
    static void PlaceInk(GraphicsPath p, float x, float y)
    {
        var b = p.GetBounds();
        using (var m = new Matrix())
        {
            m.Translate(x - b.X, y - b.Y, MatrixOrder.Append);
            p.Transform(m);
        }
    }

    static Graphics Canvas(Bitmap bmp, float scale)
    {
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);
        return g;
    }

    // ---------------------------------------------------------------- icon

    public static string Icon(string dest)
    {
        int[] bmpSizes = { 16, 20, 24, 32, 48, 64 };
        int[] pngSizes = { 128, 256 };
        var entries = new List<Tuple<int, byte[]>>();

        foreach (var s in bmpSizes)
            using (var bmp = RenderMark(s))
                entries.Add(Tuple.Create(s, BmpEntry(bmp)));
        foreach (var s in pngSizes)
            using (var bmp = RenderMark(s))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                entries.Add(Tuple.Create(s, ms.ToArray()));
            }

        using (var outStream = new MemoryStream())
        using (var w = new BinaryWriter(outStream))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)entries.Count);
            int offset = 6 + 16 * entries.Count;
            foreach (var e in entries)
            {
                byte dim = e.Item1 >= 256 ? (byte)0 : (byte)e.Item1;
                w.Write(dim); w.Write(dim);
                w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)e.Item2.Length);
                w.Write((uint)offset);
                offset += e.Item2.Length;
            }
            foreach (var e in entries)
                w.Write(e.Item2);
            w.Flush();
            File.WriteAllBytes(dest, outStream.ToArray());
            return string.Format("{0} bytes, {1} entries", outStream.Length, entries.Count);
        }
    }

    static byte[] BmpEntry(Bitmap bmp)
    {
        int size = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER is exactly 40 bytes and biSize below promises that, so every
            // field has to be written. Omitting the last one makes decoders read 40 anyway
            // and swallow the first 4 bytes of pixel data — one whole pixel at 32bpp — which
            // shifts the image one pixel left and wraps a column in from the next row.
            w.Write(40u);                 // biSize
            w.Write(size);                // biWidth
            w.Write(size * 2);            // biHeight (XOR + AND)
            w.Write((ushort)1);           // biPlanes
            w.Write((ushort)32);          // biBitCount
            w.Write(0u);                  // biCompression
            w.Write(0u);                  // biSizeImage
            w.Write(0);                   // biXPelsPerMeter
            w.Write(0);                   // biYPelsPerMeter
            w.Write(0u);                  // biClrUsed
            w.Write(0u);                  // biClrImportant

            var data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[size * 4];
                for (int y = size - 1; y >= 0; y--)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    w.Write(row);
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            int maskRowLen = ((size + 31) / 32) * 4;
            w.Write(new byte[maskRowLen * size]); // AND mask: opacity comes from alpha
            w.Flush();
            return ms.ToArray();
        }
    }

    // ---------------------------------------------------------------- png mark

    public static string MarkPng(string dest, int size)
    {
        using (var bmp = RenderMark(size))
        {
            bmp.Save(dest, ImageFormat.Png);
            return size + "px";
        }
    }

    // ---------------------------------------------------------------- lockup

    public static string Lockup(string dest, bool dark, float scale)
    {
        const float MarkSize = 92f, GapMarkText = 26f, GapLines = 10f, Pad = 4f;

        Color word = dark ? ColorTranslator.FromHtml("#F4F7FC") : ColorTranslator.FromHtml("#0F1424");
        Color sub  = dark ? ColorTranslator.FromHtml("#9AA6BE") : ColorTranslator.FromHtml("#5C6880");

        using (var wordPath = TextPath("Vantage", FontStyle.Bold, 62f))
        using (var subPath = TextPath("Display Manager", FontStyle.Regular, 21f))
        {
            var wb = wordPath.GetBounds();
            var sb = subPath.GetBounds();

            float textX = Pad + MarkSize + GapMarkText;
            float blockH = wb.Height + GapLines + sb.Height;
            float canvasH = Math.Max(MarkSize, blockH) + Pad * 2;
            float canvasW = textX + Math.Max(wb.Width, sb.Width) + Pad;
            float blockTop = (canvasH - blockH) / 2f;

            PlaceInk(wordPath, textX, blockTop);
            PlaceInk(subPath, textX, blockTop + wb.Height + GapLines);

            using (var bmp = new Bitmap((int)Math.Ceiling(canvasW * scale), (int)Math.Ceiling(canvasH * scale), PixelFormat.Format32bppArgb))
            {
                using (var g = Canvas(bmp, scale))
                using (var wbrush = new SolidBrush(word))
                using (var sbrush = new SolidBrush(sub))
                {
                    DrawMark(g, Pad, (canvasH - MarkSize) / 2f, MarkSize, false);
                    g.FillPath(wbrush, wordPath);
                    g.FillPath(sbrush, subPath);
                }
                bmp.Save(dest, ImageFormat.Png);
                return string.Format("{0}x{1}", bmp.Width, bmp.Height);
            }
        }
    }

    // ---------------------------------------------------------------- installer splash

    // Velopack's Windows setup is a thin banner window, not a full splash screen, and the
    // vpk reference pins the image at exactly 493x58. The progress bar is drawn along the
    // bottom edge at run time, so nothing important goes below the centre line.
    public static string Splash(string dest)
    {
        const int W = 493, H = 58;
        const float MarkSize = 38f, MarkX = 13f, GapMarkText = 13f, GapWordSub = 13f;

        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var brush = new LinearGradientBrush(
                           new Rectangle(-1, -1, W + 2, H + 2), TileFrom, TileTo, LinearGradientMode.ForwardDiagonal))
                    g.FillRectangle(brush, 0, 0, W, H);

                DrawMark(g, MarkX, (H - MarkSize) / 2f, MarkSize, false);

                using (var wordPath = TextPath("Vantage", FontStyle.Bold, 25f))
                using (var subPath = TextPath("Display Manager", FontStyle.Regular, 13f))
                using (var wbrush = new SolidBrush(Color.White))
                using (var sbrush = new SolidBrush(Color.FromArgb(200, 214, 233, 255)))
                using (var dbrush = new SolidBrush(Color.FromArgb(90, 255, 255, 255)))
                {
                    var wb = wordPath.GetBounds();
                    var sb = subPath.GetBounds();
                    float tx = MarkX + MarkSize + GapMarkText;

                    PlaceInk(wordPath, tx, (H - wb.Height) / 2f);
                    float dividerX = tx + wb.Width + GapWordSub;
                    using (var divider = RoundedRect(dividerX, H / 2f - 11f, 1.6f, 22f, 0.8f))
                        g.FillPath(dbrush, divider);
                    PlaceInk(subPath, dividerX + GapWordSub + 1.6f, (H - sb.Height) / 2f);

                    g.FillPath(wbrush, wordPath);
                    g.FillPath(sbrush, subPath);
                }
            }
            bmp.Save(dest, ImageFormat.Png);
            return W + "x" + H;
        }
    }

    // ---------------------------------------------------------------- banner

    // Theme-proof header for GitHub release notes, which render outside the repository and
    // so cannot use a transparent lockup that assumes a background colour.
    public static string Banner(string dest)
    {
        const int W = 1200, H = 240;
        const float MarkSize = 88f, GapMarkText = 28f, GapLines = 12f;
        var bg1 = ColorTranslator.FromHtml("#0E1330");
        var bg2 = ColorTranslator.FromHtml("#1B2A6B");

        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var brush = new LinearGradientBrush(new Rectangle(-1, -1, W + 2, H + 2), bg1, bg2, 20f))
                    g.FillRectangle(brush, 0, 0, W, H);

                using (var faint = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
                {
                    float[] xs = { -30, 154, 306, 470, 640, 820, 1010, 1140 };
                    float[] ws = { 160, 120, 130, 140, 150, 158, 108, 150 };
                    float[] hs = { 44, 58, 38, 50, 60, 40, 56, 46 };
                    for (int i = 0; i < xs.Length; i++)
                        using (var p = RoundedRect(xs[i], H + 12 - hs[i], ws[i], hs[i], 9))
                            g.FillPath(faint, p);
                }

                using (var wordPath = TextPath("Vantage", FontStyle.Bold, 54f))
                using (var tagPath = TextPath("Display profiles for Windows 11 that actually stick.", FontStyle.Regular, 21f))
                using (var wbrush = new SolidBrush(Color.White))
                using (var tbrush = new SolidBrush(ColorTranslator.FromHtml("#A7B6DC")))
                {
                    var wb = wordPath.GetBounds();
                    var tb = tagPath.GetBounds();

                    float textW = Math.Max(wb.Width, tb.Width);
                    float groupW = MarkSize + GapMarkText + textW;
                    float groupX = (W - groupW) / 2f;
                    float blockH = wb.Height + GapLines + tb.Height;
                    float blockTop = (H - blockH) / 2f - 8f;

                    DrawMark(g, groupX, (H - MarkSize) / 2f - 8f, MarkSize, false);
                    PlaceInk(wordPath, groupX + MarkSize + GapMarkText, blockTop);
                    PlaceInk(tagPath, groupX + MarkSize + GapMarkText, blockTop + wb.Height + GapLines);
                    g.FillPath(wbrush, wordPath);
                    g.FillPath(tbrush, tagPath);
                }
            }
            bmp.Save(dest, ImageFormat.Png);
            return W + "x" + H;
        }
    }

    // ---------------------------------------------------------------- social card

    public static string Social(string dest)
    {
        const int W = 1280, H = 640;
        var bg1 = ColorTranslator.FromHtml("#0E1330");
        var bg2 = ColorTranslator.FromHtml("#1B2A6B");

        using (var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                using (var brush = new LinearGradientBrush(new Rectangle(-1, -1, W + 2, H + 2), bg1, bg2, 30f))
                    g.FillRectangle(brush, 0, 0, W, H);

                // A display arrangement along the bottom edge: panels of differing size and
                // orientation bottom-aligned on one line, the way the layout editor draws
                // them. Bleeds off both edges so it reads as a band, not a stray shape.
                using (var faint = new SolidBrush(Color.FromArgb(15, 255, 255, 255)))
                {
                    float[] xs = { -46, 178, 306, 470, 634, 748, 928, 1108 };
                    float[] ws = { 208, 104, 148, 146, 96, 156, 154, 218 };
                    float[] hs = { 104, 138, 92, 118, 138, 96, 122, 108 };
                    for (int i = 0; i < xs.Length; i++)
                        using (var p = RoundedRect(xs[i], H + 22 - hs[i], ws[i], hs[i], 13))
                            g.FillPath(faint, p);
                }

                using (var wordPath = TextPath("Vantage", FontStyle.Bold, 92f))
                using (var tagPath = TextPath("Display profiles for Windows 11 that actually stick.", FontStyle.Regular, 30f))
                using (var wbrush = new SolidBrush(Color.White))
                using (var tbrush = new SolidBrush(ColorTranslator.FromHtml("#A7B6DC")))
                {
                    const float MarkSize = 164f, GapMarkWord = 42f, GapWordTag = 30f;
                    var wb = wordPath.GetBounds();
                    var tb = tagPath.GetBounds();

                    float stackH = MarkSize + GapMarkWord + wb.Height + GapWordTag + tb.Height;
                    float top = (H - stackH) / 2f - 28f;   // lift clear of the panel band

                    DrawMark(g, (W - MarkSize) / 2f, top, MarkSize, false);
                    PlaceInk(wordPath, (W - wb.Width) / 2f, top + MarkSize + GapMarkWord);
                    PlaceInk(tagPath, (W - tb.Width) / 2f, top + MarkSize + GapMarkWord + wb.Height + GapWordTag);
                    g.FillPath(wbrush, wordPath);
                    g.FillPath(tbrush, tagPath);
                }
            }
            bmp.Save(dest, ImageFormat.Png);
            return W + "x" + H;
        }
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing

$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$assets = Join-Path $root "src\Vantage.App\Assets"
$docs = Join-Path $root "docs\assets"
$build = Join-Path $root "build\assets"
New-Item -ItemType Directory -Force -Path $assets, $docs, $build | Out-Null

Write-Output ("vantage.ico             " + [Branding]::Icon((Join-Path $assets "vantage.ico")))
Write-Output ("vantage-mark-512.png    " + [Branding]::MarkPng((Join-Path $docs "vantage-mark-512.png"), 512))
Write-Output ("vantage-lockup.png      " + [Branding]::Lockup((Join-Path $docs "vantage-lockup.png"), $false, 3))
Write-Output ("vantage-lockup-dark.png " + [Branding]::Lockup((Join-Path $docs "vantage-lockup-dark.png"), $true, 3))
Write-Output ("vantage-banner.png      " + [Branding]::Banner((Join-Path $docs "vantage-banner.png")))
Write-Output ("vantage-social.png      " + [Branding]::Social((Join-Path $docs "vantage-social.png")))
Write-Output ("vantage-splash.png      " + [Branding]::Splash((Join-Path $build "vantage-splash.png")))
