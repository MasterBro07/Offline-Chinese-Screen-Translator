using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AutoTranslator.Desktop;

/// <summary>Tray icon with its context menu. All members must be used on the UI thread.</summary>
internal sealed class TrayController : IDisposable
{
    public const string ModeOcr = "Ocr";
    public const string ModeUiAutomation = "UiAutomation";
    public const string ModeBoth = "Both";

    private const int MaxTooltipLength = 63; // NotifyIcon.Text limit

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _modeOcrItem;
    private readonly ToolStripMenuItem _modeUiaItem;
    private readonly ToolStripMenuItem _modeBothItem;
    private readonly ToolStripMenuItem _scopeWindowItem;
    private readonly ToolStripMenuItem _scopeMonitorItem;
    private readonly ToolStripMenuItem _statusItem;

    private readonly Icon _activeIcon;
    private readonly Icon _pausedIcon;
    private readonly IntPtr _activeIconHandle;
    private readonly IntPtr _pausedIconHandle;
    private bool _disposed;

    public TrayController()
    {
        _activeIcon = CreateIcon(Color.FromArgb(0x1E, 0x88, 0xE5), out _activeIconHandle);
        _pausedIcon = CreateIcon(Color.FromArgb(0x80, 0x80, 0x80), out _pausedIconHandle);

        _enabledItem = new ToolStripMenuItem("Übersetzung aktiv (Strg+Alt+T)");
        _enabledItem.Click += (sender, e) => Raise(ToggleRequested);

        _modeOcrItem = new ToolStripMenuItem("OCR (Bildschirmtext lesen)");
        _modeOcrItem.Click += (sender, e) => Raise(ModeSelected, ModeOcr);
        _modeUiaItem = new ToolStripMenuItem("UI-Automation (Programmtexte)");
        _modeUiaItem.Click += (sender, e) => Raise(ModeSelected, ModeUiAutomation);
        _modeBothItem = new ToolStripMenuItem("Beides");
        _modeBothItem.Click += (sender, e) => Raise(ModeSelected, ModeBoth);

        var modeMenu = new ToolStripMenuItem("Texterkennung");
        modeMenu.DropDownItems.Add(_modeOcrItem);
        modeMenu.DropDownItems.Add(_modeUiaItem);
        modeMenu.DropDownItems.Add(_modeBothItem);

        _scopeWindowItem = new ToolStripMenuItem("Aktives Fenster");
        _scopeWindowItem.Click += (sender, e) => Raise(ScopeSelected, ScreenCapture.ScopeForegroundWindow);
        _scopeMonitorItem = new ToolStripMenuItem("Ganzer Hauptbildschirm");
        _scopeMonitorItem.Click += (sender, e) => Raise(ScopeSelected, ScreenCapture.ScopePrimaryMonitor);

        var scopeMenu = new ToolStripMenuItem("Bereich");
        scopeMenu.DropDownItems.Add(_scopeWindowItem);
        scopeMenu.DropDownItems.Add(_scopeMonitorItem);

        _statusItem = new ToolStripMenuItem("Status: wird gestartet …");
        _statusItem.Enabled = false;

        var helpItem = new ToolStripMenuItem("Einrichtung / Hilfe…");
        helpItem.Click += (sender, e) => Raise(HelpRequested);

        var exitItem = new ToolStripMenuItem("Beenden");
        exitItem.Click += (sender, e) => Raise(ExitRequested);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_enabledItem);
        _menu.Items.Add(modeMenu);
        _menu.Items.Add(scopeMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(helpItem);
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon();
        _notifyIcon.Icon = _activeIcon;
        _notifyIcon.Text = "AutoTranslator";
        _notifyIcon.ContextMenuStrip = _menu;
        _notifyIcon.DoubleClick += (sender, e) => Raise(HelpRequested);
        _notifyIcon.Visible = true;
    }

    public event EventHandler? ToggleRequested;

    public event EventHandler<string>? ModeSelected;

    public event EventHandler<string>? ScopeSelected;

    public event EventHandler? HelpRequested;

    public event EventHandler? ExitRequested;

    /// <summary>Refreshes check marks, status line, icon and tooltip.</summary>
    public void Update(bool enabled, string mode, string scope, string status)
    {
        if (_disposed)
        {
            return;
        }

        _enabledItem.Checked = enabled;
        _modeOcrItem.Checked = string.Equals(mode, ModeOcr, StringComparison.OrdinalIgnoreCase);
        _modeUiaItem.Checked = string.Equals(mode, ModeUiAutomation, StringComparison.OrdinalIgnoreCase);
        _modeBothItem.Checked = string.Equals(mode, ModeBoth, StringComparison.OrdinalIgnoreCase);

        bool monitor = string.Equals(scope, ScreenCapture.ScopePrimaryMonitor, StringComparison.OrdinalIgnoreCase);
        _scopeMonitorItem.Checked = monitor;
        _scopeWindowItem.Checked = !monitor;

        string statusLine = "Status: " + status;
        if (_statusItem.Text != statusLine)
        {
            _statusItem.Text = statusLine;
        }

        Icon icon = enabled ? _activeIcon : _pausedIcon;
        if (!ReferenceEquals(_notifyIcon.Icon, icon))
        {
            _notifyIcon.Icon = icon;
        }

        string tooltip = Truncate("AutoTranslator – " + status, MaxTooltipLength);
        if (_notifyIcon.Text != tooltip)
        {
            _notifyIcon.Text = tooltip;
        }
    }

    /// <summary>Shows a balloon notification (used for one-time hints).</summary>
    public void ShowNotification(string title, string text)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _notifyIcon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Showing the tray notification failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _activeIcon.Dispose();
            _pausedIcon.Dispose();
            NativeMethods.DestroyIcon(_activeIconHandle);
            NativeMethods.DestroyIcon(_pausedIconHandle);
        }
        catch (Exception ex)
        {
            Log.Error("Tray cleanup failed", ex);
        }
    }

    private void Raise(EventHandler? handler)
    {
        try
        {
            handler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("Tray menu action failed", ex);
        }
    }

    private void Raise(EventHandler<string>? handler, string value)
    {
        try
        {
            handler?.Invoke(this, value);
        }
        catch (Exception ex)
        {
            Log.Error("Tray menu action failed", ex);
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        // Line breaks are not allowed in a tooltip.
        string singleLine = text.Replace('\r', ' ').Replace('\n', ' ');
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine.Substring(0, maxLength - 1) + "…";
    }

    /// <summary>Draws the tray icon in code so that no binary asset is needed.</summary>
    private static Icon CreateIcon(Color background, out IntPtr handle)
    {
        using (var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        {
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
                graphics.Clear(Color.Transparent);

                using (var brush = new SolidBrush(background))
                {
                    graphics.FillEllipse(brush, 1, 1, 30, 30);
                }

                using (var font = new Font("Segoe UI", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    graphics.DrawString("A", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
                }
            }

            handle = bitmap.GetHicon();
            return Icon.FromHandle(handle);
        }
    }
}
