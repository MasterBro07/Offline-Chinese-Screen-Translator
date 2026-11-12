using System;
using System.Windows;
using System.Windows.Threading;

namespace AutoTranslator.Desktop;

/// <summary>Setup instructions (German) plus a live status line.</summary>
public partial class HelpWindow : Window
{
    private const string OcrInstallCommand =
        "Add-WindowsCapability -Online -Name \"Language.OCR~~~zh-CN~0.0.1.0\"";

    private readonly Func<string>? _statusProvider;
    private readonly DispatcherTimer _timer;

    public HelpWindow()
        : this(null)
    {
    }

    public HelpWindow(Func<string>? statusProvider)
    {
        InitializeComponent();
        _statusProvider = statusProvider;
        CommandBox.Text = OcrInstallCommand;

        _timer = new DispatcherTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += OnTimerTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateStatus();
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        try
        {
            StatusText.Text = _statusProvider != null ? _statusProvider() : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("Updating the help status failed", ex);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(OcrInstallCommand);
            CopyButton.Content = "Kopiert ✓";
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another program for a moment.
            Log.Error("Copying to the clipboard failed", ex);
            CommandBox.Focus();
            CommandBox.SelectAll();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
