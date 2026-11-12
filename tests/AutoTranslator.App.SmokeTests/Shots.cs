using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AutoTranslator.Core;

namespace AutoTranslator.SmokeTests;

/// <summary>A screenshot as raw BGRA bytes plus the screen rectangle it shows.</summary>
internal sealed class Shot
{
    public Shot(ScreenRect rect, byte[] pixels)
    {
        Rect = rect;
        Pixels = pixels;
    }

    public ScreenRect Rect { get; }

    public byte[] Pixels { get; }

    /// <summary>Brightness (0..255) of the pixel at screen coordinates.</summary>
    public int Luma(int screenX, int screenY)
    {
        int i = ((screenY - Rect.Y) * Rect.Width + (screenX - Rect.X)) * 4;
        return (Pixels[i] * 114 + Pixels[i + 1] * 587 + Pixels[i + 2] * 299) / 1000;
    }

    public bool Contains(int screenX, int screenY)
    {
        return screenX >= Rect.X && screenY >= Rect.Y && screenX < Rect.X + Rect.Width && screenY < Rect.Y + Rect.Height;
    }
}

/// <summary>Result of comparing two screenshots of the same rectangle.</summary>
internal sealed class ShotDiff
{
    public int ChangedPixels { get; set; }

    /// <summary>Bounding box of the changed pixels in screen coordinates (empty when nothing changed).</summary>
    public ScreenRect Bounds { get; set; }

    public override string ToString()
    {
        return ChangedPixels == 0
            ? "0 px changed"
            : ChangedPixels + " px changed inside " + Shots.Describe(Bounds);
    }
}

internal static class Shots
{
    /// <summary>Grabs a screen rectangle with Graphics.CopyFromScreen (the same call the app uses).</summary>
    public static Shot Grab(ScreenRect rect)
    {
        using (var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.X, rect.Y, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }

            var pixels = new byte[rect.Width * rect.Height * 4];
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, rect.Width, rect.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int y = 0; y < rect.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rect.Width * 4, rect.Width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return new Shot(rect, pixels);
        }
    }

    public static string Save(Shot shot, string name)
    {
        string path = Path.Combine(Report.OutputDirectory, name + ".png");
        try
        {
            using (var bitmap = new Bitmap(shot.Rect.Width, shot.Rect.Height, PixelFormat.Format32bppRgb))
            {
                BitmapData data = bitmap.LockBits(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Marshal.Copy(shot.Pixels, y * bitmap.Width * 4, IntPtr.Add(data.Scan0, y * data.Stride), bitmap.Width * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                bitmap.Save(path, ImageFormat.Png);
            }

            Report.Info("screenshot saved: " + name + ".png");
        }
        catch (Exception ex)
        {
            Report.Info("saving screenshot " + name + " failed: " + ex.Message);
        }

        return path;
    }

    /// <summary>Counts pixels whose brightness differs by more than the tolerance, optionally only outside/inside rects.</summary>
    public static ShotDiff Diff(Shot a, Shot b, int tolerance = 24, ScreenRect[]? ignore = null, ScreenRect? within = null)
    {
        ScreenRect area = within ?? a.Rect;
        if (a.Rect != b.Rect)
        {
            throw new ArgumentException("Screenshots show different rectangles.");
        }

        int count = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = area.Y; y < area.Y + area.Height; y++)
        {
            for (int x = area.X; x < area.X + area.Width; x++)
            {
                if (!a.Contains(x, y))
                {
                    continue;
                }

                if (Math.Abs(a.Luma(x, y) - b.Luma(x, y)) <= tolerance)
                {
                    continue;
                }

                if (ignore != null && InAny(ignore, x, y))
                {
                    continue;
                }

                count++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return new ShotDiff
        {
            ChangedPixels = count,
            Bounds = count == 0 ? default : new ScreenRect(minX, minY, maxX - minX + 1, maxY - minY + 1),
        };
    }

    /// <summary>Fractions of dark (&lt; 90) and light (&gt; 180) pixels inside a screen rectangle.</summary>
    public static void Tones(Shot shot, ScreenRect rect, out double dark, out double light)
    {
        int total = 0, darkCount = 0, lightCount = 0;
        for (int y = rect.Y; y < rect.Y + rect.Height; y++)
        {
            for (int x = rect.X; x < rect.X + rect.Width; x++)
            {
                if (!shot.Contains(x, y))
                {
                    continue;
                }

                total++;
                int luma = shot.Luma(x, y);
                if (luma < 90)
                {
                    darkCount++;
                }
                else if (luma > 180)
                {
                    lightCount++;
                }
            }
        }

        dark = total == 0 ? 0 : (double)darkCount / total;
        light = total == 0 ? 0 : (double)lightCount / total;
    }

    public static ScreenRect Inflate(ScreenRect rect, int by)
    {
        return new ScreenRect(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);
    }

    public static bool Inside(ScreenRect inner, ScreenRect outer)
    {
        return inner.X >= outer.X && inner.Y >= outer.Y
               && inner.X + inner.Width <= outer.X + outer.Width
               && inner.Y + inner.Height <= outer.Y + outer.Height;
    }

    public static string Describe(ScreenRect rect)
    {
        return "(" + rect.X + "," + rect.Y + " " + rect.Width + "x" + rect.Height + ")";
    }

    private static bool InAny(ScreenRect[] rects, int x, int y)
    {
        foreach (ScreenRect rect in rects)
        {
            if (x >= rect.X && y >= rect.Y && x < rect.X + rect.Width && y < rect.Y + rect.Height)
            {
                return true;
            }
        }

        return false;
    }
}
