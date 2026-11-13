using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AutoTranslator.Core;
using Microsoft.Win32;

namespace AutoTranslator.Desktop;

/// <summary>
/// Transparent, click-through, always-on-top window that covers the whole virtual screen and
/// draws the translations exactly over the original text.
/// </summary>
internal sealed class OverlayWindow : Window, IOverlayPresenter
{
    private const double MinFontSize = 9.0;
    private const double MaxFontSize = 72.0;
    private const double PaddingX = 3.0;

    private static readonly IReadOnlyList<TranslatedRegion> NoRegions = Array.Empty<TranslatedRegion>();

    private readonly Canvas _canvas = new Canvas();
    private readonly Brush _boxBrush;
    private readonly FontFamily _fontFamily = new FontFamily("Segoe UI");
    private readonly Typeface _typeface;

    // Windows 10 (and Server 2022) cannot hide a WPF "AllowsTransparency" window from screen
    // capture: SetWindowDisplayAffinity fails with error 8 for windows drawn with
    // UpdateLayeredWindow, and the app then reads and re-translates its own overlay. There the
    // window is made transparent through DWM instead (see OnSourceInitialized). Windows 11 keeps
    // the plain WPF way. Both ways are verified by tests/AutoTranslator.App.SmokeTests.
    private const int FirstWindows11Build = 22000;

    /// <summary>Smoke-test seam: force one of the two transparency techniques. Never set in production.</summary>
    internal static bool? TestUseWpfLayeredWindow;

    private readonly bool _wpfLayered;

    private IntPtr _hwnd;
    private int _originX;
    private int _originY;
    private int _physicalWidth;
    private int _physicalHeight;

    private IReadOnlyList<TranslatedRegion> _pending = NoRegions;
    private IReadOnlyList<TranslatedRegion> _current = NoRegions;
    private int _renderScheduled;
    private bool _allowClose;

    public OverlayWindow()
    {
        Title = "AutoTranslator Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        _wpfLayered = TestUseWpfLayeredWindow ?? Environment.OSVersion.Version.Build >= FirstWindows11Build;
        AllowsTransparency = _wpfLayered;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = 200;
        Height = 200;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var brush = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20));
        brush.Freeze();
        _boxBrush = brush;
        _typeface = new Typeface(_fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        _canvas.IsHitTestVisible = false;
        _canvas.Background = null;
        Content = _canvas;
    }

    /// <summary>Creates the native window (hidden) so that styles and position are set up front.</summary>
    public void Initialize()
    {
        new WindowInteropHelper(this).EnsureHandle();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Closes the window for real (normal Close requests are ignored).</summary>
    public void Shutdown()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _allowClose = true;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Closing the overlay failed", ex);
        }
    }

    // ---- IOverlayPresenter (called from background threads) ----

    public void Show(IReadOnlyList<TranslatedRegion> regions)
    {
        Volatile.Write(ref _pending, regions ?? NoRegions);
        ScheduleRender();
    }

    public void Clear()
    {
        Volatile.Write(ref _pending, NoRegions);
        ScheduleRender();
    }

    private void ScheduleRender()
    {
        // Coalesce: when several updates arrive before the UI thread runs, only the last is drawn.
        if (Interlocked.Exchange(ref _renderScheduled, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(RenderPending));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _renderScheduled, 0);
            Log.Error("Scheduling the overlay update failed", ex);
        }
    }

    private void RenderPending()
    {
        Interlocked.Exchange(ref _renderScheduled, 0);
        if (_allowClose)
        {
            return;
        }

        try
        {
            Render(Volatile.Read(ref _pending));
        }
        catch (Exception ex)
        {
            Log.Error("Drawing the overlay failed", ex);
        }
    }

    // ---- Window setup ----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        if (!_wpfLayered)
        {
            // Per-pixel transparency without UpdateLayeredWindow: WPF renders onto a transparent
            // back buffer and DWM is told that the whole client area is "glass".
            HwndSource? hwndSource = HwndSource.FromHwnd(_hwnd);
            if (hwndSource != null && hwndSource.CompositionTarget != null)
            {
                hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            int hr = NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
            if (hr != 0)
            {
                Log.Error("DwmExtendFrameIntoClientArea failed, HRESULT 0x" + hr.ToString("X8"));
            }
        }

        long exStyle = NativeMethods.GetWindowLongValue(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TRANSPARENT
                   | NativeMethods.WS_EX_LAYERED
                   | NativeMethods.WS_EX_TOOLWINDOW
                   | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongValue(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        if (!_wpfLayered)
        {
            // WS_EX_LAYERED is needed for click-through; a layered window stays invisible until
            // its attributes are set.
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        }

        // Essential: hide the overlay from screen capture, otherwise OCR would read (and cover)
        // our own translations instead of the original text.
        if (!NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE))
        {
            Log.Error("SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) failed, error "
                      + System.Runtime.InteropServices.Marshal.GetLastWin32Error()
                      + ". The overlay is visible to screen capture; OCR will read the translations.");
        }

        ApplyBounds();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        // WPF resizes the window for the new DPI; put it back over the whole virtual screen.
        Dispatcher.BeginInvoke(new Action(RefreshLayout));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(RefreshLayout));
        }
        catch (Exception ex)
        {
            Log.Error("Display change handling failed", ex);
        }
    }

    private void RefreshLayout()
    {
        if (_allowClose)
        {
            return;
        }

        try
        {
            ApplyBounds();
            Render(_current);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay layout refresh failed", ex);
        }
    }

    /// <summary>Moves the window over the whole virtual screen, in physical pixels.</summary>
    private void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        ScreenRect screen = ScreenCapture.GetVirtualScreen();
        _originX = screen.X;
        _originY = screen.Y;
        _physicalWidth = screen.Width;
        _physicalHeight = screen.Height;

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            screen.X,
            screen.Y,
            screen.Width,
            screen.Height,
            NativeMethods.SWP_NOACTIVATE);
    }

    // ---- Rendering (UI thread) ----

    private void Render(IReadOnlyList<TranslatedRegion> regions)
    {
        _current = regions;
        _canvas.Children.Clear();

        if (regions.Count == 0)
        {
            if (IsVisible)
            {
                Hide();
            }

            return;
        }

        if (!IsVisible)
        {
            Show();
            ApplyBounds();
        }
        else if (_hwnd != IntPtr.Zero)
        {
            // Stay above other top-most windows (task bar, menus).
            NativeMethods.SetWindowPos(
                _hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Physical pixels -> WPF device-independent units, using the DPI this window renders at.
        Matrix fromDevice = Matrix.Identity;
        PresentationSource? source = PresentationSource.FromVisual(this);
        if (source != null && source.CompositionTarget != null)
        {
            fromDevice = source.CompositionTarget.TransformFromDevice;
        }

        double scaleX = fromDevice.M11 > 0 ? fromDevice.M11 : 1.0;
        double scaleY = fromDevice.M22 > 0 ? fromDevice.M22 : 1.0;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double canvasWidth = _physicalWidth * scaleX;

        foreach (TranslatedRegion region in regions)
        {
            if (region == null || string.IsNullOrWhiteSpace(region.Translated))
            {
                continue;
            }

            double x = (region.Bounds.X - _originX) * scaleX;
            double y = (region.Bounds.Y - _originY) * scaleY;
            double width = region.Bounds.Width * scaleX;
            double height = region.Bounds.Height * scaleY;
            if (width < 1 || height < 1)
            {
                continue;
            }

            Border box = BuildBox(region.Translated.Trim(), x, width, height, canvasWidth, pixelsPerDip);
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            _canvas.Children.Add(box);
        }
    }

    /// <summary>
    /// Creates the box for one translation. The font starts at roughly the size of the original
    /// text and shrinks until the (wrapped) English text fits; when that is not enough the box
    /// grows to the right, up to the screen edge, and finally downwards.
    /// </summary>
    private Border BuildBox(string text, double x, double width, double height, double canvasWidth, double pixelsPerDip)
    {
        double startSize = Math.Max(MinFontSize, Math.Min(height * 0.75, MaxFontSize));
        double comfortableMinimum = Math.Max(MinFontSize, startSize * 0.8);
        double availableWidth = Math.Max(width, canvasWidth - x);

        double fontSize = startSize;
        double boxWidth = width;
        double boxHeight = height;
        bool fits = false;

        // 1. Keep the original box, shrink the font a little.
        for (double size = startSize; size >= comfortableMinimum; size *= 0.92)
        {
            Size measured = MeasureText(text, size, width - 2 * PaddingX, pixelsPerDip);
            if (measured.Height <= height + 1.0)
            {
                fontSize = size;
                fits = true;
                break;
            }
        }

        // 2. Let the box grow to the right and keep shrinking down to the minimum size.
        if (!fits)
        {
            double size = comfortableMinimum;
            while (true)
            {
                Size measured = MeasureText(text, size, availableWidth - 2 * PaddingX, pixelsPerDip);
                if (measured.Height <= height + 1.0 || size <= MinFontSize)
                {
                    fontSize = size;
                    boxWidth = Math.Max(width, Math.Min(availableWidth, measured.Width + 2 * PaddingX + 1.0));
                    boxHeight = Math.Max(height, measured.Height);
                    break;
                }

                size = Math.Max(MinFontSize, size * 0.92);
            }
        }

        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = _fontFamily,
            FontSize = fontSize,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(PaddingX, 0, PaddingX, 0),
            IsHitTestVisible = false,
        };

        return new Border
        {
            Width = boxWidth,
            Height = boxHeight,
            Background = _boxBrush,
            CornerRadius = new CornerRadius(2),
            Child = textBlock,
            IsHitTestVisible = false,
        };
    }

    private Size MeasureText(string text, double fontSize, double maxWidth, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            _typeface,
            fontSize,
            Brushes.White,
            pixelsPerDip);
        formatted.MaxTextWidth = Math.Max(1.0, Math.Min(maxWidth, 3000000.0));
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}
