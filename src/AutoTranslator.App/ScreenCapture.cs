using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Finds the screen area to read and grabs its pixels with GDI.
/// All coordinates are physical pixels in virtual-screen space (the process is per-monitor DPI aware).
/// </summary>
internal static class ScreenCapture
{
    public const string ScopeForegroundWindow = "ForegroundWindow";
    public const string ScopePrimaryMonitor = "PrimaryMonitor";

    private const int MinWidth = 60;
    private const int MinHeight = 30;

    public static ScreenRect GetVirtualScreen()
    {
        return new ScreenRect(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));
    }

    /// <summary>
    /// Returns the foreground window and its visible bounds, or false when there is none,
    /// it is minimized, too small, or belongs to this process (help window, tray menu).
    /// </summary>
    public static bool TryGetForegroundWindow(out IntPtr hwnd, out ScreenRect bounds)
    {
        bounds = default;
        hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        uint processId;
        NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
        if (processId == (uint)Environment.ProcessId)
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        NativeMethods.RECT rect;
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf<NativeMethods.RECT>());
        if (hr != 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
        {
            if (!NativeMethods.GetWindowRect(hwnd, out rect))
            {
                return false;
            }
        }

        ScreenRect? clipped = Intersect(
            new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
            GetVirtualScreen());
        if (clipped == null || clipped.Value.Width < MinWidth || clipped.Value.Height < MinHeight)
        {
            return false;
        }

        bounds = clipped.Value;
        return true;
    }

    /// <summary>Returns the rectangle to capture for the given scope, or null when capture should be skipped.</summary>
    public static ScreenRect? GetCaptureRect(string? scope)
    {
        IntPtr hwnd;
        ScreenRect windowBounds;
        if (!TryGetForegroundWindow(out hwnd, out windowBounds))
        {
            return null;
        }

        if (string.Equals(scope, ScopePrimaryMonitor, StringComparison.OrdinalIgnoreCase))
        {
            // The primary monitor always starts at (0,0) in virtual-screen coordinates.
            int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            if (width < MinWidth || height < MinHeight)
            {
                return null;
            }

            return new ScreenRect(0, 0, width, height);
        }

        return windowBounds;
    }

    public static ScreenRect? Intersect(ScreenRect a, ScreenRect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new ScreenRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Set by an overlay that Windows could not exclude from screen capture: it is asked to
    /// take itself off the screen for the duration of every capture. Null in the normal case.
    /// </summary>
    internal static volatile ICaptureShield? Shield;

    /// <summary>Captures the given screen rectangle. The caller owns (and must dispose) the bitmap.</summary>
    public static Bitmap Capture(ScreenRect rect)
    {
        ICaptureShield? shield = Shield;
        bool hidden = shield != null && shield.HideForCapture();
        var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(rect.X, rect.Y, 0, 0, new Size(rect.Width, rect.Height), CopyPixelOperation.SourceCopy);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            if (hidden)
            {
                shield!.RestoreAfterCapture();
            }
        }
    }

    /// <summary>Returns a resized copy. The caller owns (and must dispose) the result.</summary>
    public static Bitmap Resize(Bitmap source, int width, int height)
    {
        var result = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel);
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the pixels as tightly packed BGRA (4 bytes per pixel, alpha forced to 255) into
    /// <paramref name="buffer"/>, growing it when needed. Returns the number of bytes written.
    /// </summary>
    public static int CopyPixels(Bitmap bitmap, ref byte[]? buffer)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int rowBytes = width * 4;
        int length = rowBytes * height;
        if (buffer == null || buffer.Length < length)
        {
            buffer = new byte[length];
        }

        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == rowBytes)
            {
                Marshal.Copy(data.Scan0, buffer, 0, length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // The fourth byte is undefined after a screen copy; make the image opaque.
        byte[] pixels = buffer;
        for (int i = 3; i < length; i += 4)
        {
            pixels[i] = 255;
        }

        return length;
    }

    /// <summary>Fast 64-bit hash over the pixel bytes, used only to detect "nothing changed".</summary>
    public static ulong Hash(byte[] pixels, int length)
    {
        const ulong Prime = 1099511628211UL;
        ulong hash = 14695981039346656037UL;

        ReadOnlySpan<byte> bytes = new ReadOnlySpan<byte>(pixels, 0, length);
        ReadOnlySpan<ulong> words = MemoryMarshal.Cast<byte, ulong>(bytes);
        for (int i = 0; i < words.Length; i++)
        {
            hash = (hash ^ words[i]) * Prime;
        }

        for (int i = words.Length * 8; i < length; i++)
        {
            hash = (hash ^ pixels[i]) * Prime;
        }

        return hash;
    }
}

/// <summary>Something on screen that must not end up in the app's own screen captures.</summary>
internal interface ICaptureShield
{
    /// <summary>Takes it off the screen; false when there was nothing to hide. Any thread.</summary>
    bool HideForCapture();

    /// <summary>Puts it back. Only called after <see cref="HideForCapture"/> returned true.</summary>
    void RestoreAfterCapture();
}
