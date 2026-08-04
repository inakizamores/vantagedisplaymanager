# Generates Assets\vantage.ico — Windows 11 style rounded-square gradient tile with a "V" glyph.
# Sizes <= 64 are classic BMP entries (shell/taskbar only reliably decode PNG at 256px);
# 128/256 are PNG. Logic lives in C# to avoid PowerShell array-coercion pitfalls.
$source = @"
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconBuilder
{
    public static string Build(string dest)
    {
        int[] bmpSizes = { 16, 20, 24, 32, 48, 64 };
        int[] pngSizes = { 128, 256 };
        var entries = new List<Tuple<int, byte[]>>();

        foreach (var s in bmpSizes)
            using (var bmp = Render(s))
                entries.Add(Tuple.Create(s, BmpEntry(bmp)));
        foreach (var s in pngSizes)
            using (var bmp = Render(s))
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

    private static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            int radius = Math.Max(2, (int)(size * 0.22));
            int d = radius * 2;
            using (var path = new GraphicsPath())
            {
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(size - d, 0, d, d, 270, 90);
                path.AddArc(size - d, size - d, d, d, 0, 90);
                path.AddArc(0, size - d, d, d, 90, 90);
                path.CloseFigure();
                using (var brush = new LinearGradientBrush(
                    new Rectangle(0, 0, size, size),
                    Color.FromArgb(255, 124, 58, 237),
                    Color.FromArgb(255, 37, 99, 235),
                    LinearGradientMode.ForwardDiagonal))
                {
                    g.FillPath(brush, path);
                }
            }

            float fontSize = Math.Max(6f, size * 0.58f);
            using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString("V", font, Brushes.White, new RectangleF(0, size * 0.02f, size, size), fmt);
            }
        }
        return bmp;
    }

    private static byte[] BmpEntry(Bitmap bmp)
    {
        int size = bmp.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40u);                 // biSize
            w.Write(size);                // biWidth
            w.Write(size * 2);            // biHeight (XOR + AND)
            w.Write((ushort)1);           // biPlanes
            w.Write((ushort)32);          // biBitCount
            w.Write(0u); w.Write(0u); w.Write(0); w.Write(0); w.Write(0u); // compression..clrImportant (biXPels/biYPels as Int32)

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
            w.Write(new byte[maskRowLen * size]); // AND mask: all opaque-by-alpha
            w.Flush();
            return ms.ToArray();
        }
    }
}
"@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
$dest = Join-Path $PSScriptRoot "..\src\Vantage.App\Assets\vantage.ico"
$result = [IconBuilder]::Build((Resolve-Path (Split-Path $dest)).Path + "\vantage.ico")
Write-Output "Wrote $dest ($result)"
