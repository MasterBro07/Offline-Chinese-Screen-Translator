using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AutoTranslator.Desktop;

/// <summary>What the settings window needs from the application. UI thread only unless noted.</summary>
internal interface ISettingsHost
{
    /// <summary>"Chrome" or "Claude".</summary>
    string Engine { get; }

    bool HasApiKey { get; }

    /// <summary>Source characters per day; 0 = unlimited.</summary>
    long DailyCharLimit { get; }

    long CharsToday { get; }

    bool MergeLines { get; }

    /// <summary>Raised when the usage counter changes. May be raised on any thread.</summary>
    event EventHandler? UsageChanged;

    void SetEngine(string engine);

    /// <summary>Null or empty removes the key. False when the key could not be written to disk.</summary>
    bool SetApiKey(string? key);

    void SetDailyCharLimit(long limit);

    void SetMergeLines(bool mergeLines);

    /// <summary>Null = connection works, otherwise a German error text.</summary>
    Task<string?> TestClaudeAsync(CancellationToken ct);
}

/// <summary>Engine choice, API key, daily limit and display options (German).</summary>
public partial class SettingsWindow : Window
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly Brush OkBrush = CreateBrush(0x1B, 0x7F, 0x3B);
    private static readonly Brush ErrorBrush = CreateBrush(0xB3, 0x26, 0x1E);
    private static readonly Brush NeutralBrush = CreateBrush(0x55, 0x55, 0x55);

    private readonly ISettingsHost _host;
    private CancellationTokenSource? _testCts;
    private int _usageRefreshQueued;
    private bool _closed;

    internal SettingsWindow(ISettingsHost host)
    {
        InitializeComponent();
        _host = host;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>Re-reads all values from the host (e.g. after the engine was changed in the tray menu).</summary>
    internal void RefreshFromHost()
    {
        if (_closed)
        {
            return;
        }

        try
        {
            bool claude = IsClaude(_host.Engine);
            EngineClaudeRadio.IsChecked = claude;
            EngineChromeRadio.IsChecked = !claude;
            MergeLinesCheck.IsChecked = _host.MergeLines;
            LimitBox.Text = _host.DailyCharLimit.ToString("N0", German);
            UpdateKeyState();
            UpdateUsage();
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the settings window failed", ex);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromHost();
        _host.UsageChanged += OnUsageChanged;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _host.UsageChanged -= OnUsageChanged;

        CancellationTokenSource? cts = _testCts;
        _testCts = null;
        if (cts != null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        KeyBox.Clear();
    }

    // ------------------------------------------------------------------ engine

    private void OnEngineClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool claude = EngineClaudeRadio.IsChecked == true;
            _host.SetEngine(claude ? TrayController.EngineClaude : TrayController.EngineChrome);
            UpdateKeyState();
        }
        catch (Exception ex)
        {
            Log.Error("Changing the engine failed", ex);
        }
    }

    // ------------------------------------------------------------------ API key

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        SaveTypedKey(true);
    }

    /// <summary>Saves the key typed into the box. Returns false when there is nothing usable to save.</summary>
    private bool SaveTypedKey(bool reportEmpty)
    {
        try
        {
            string typed = (KeyBox.Password ?? string.Empty).Trim();
            if (typed.Length == 0)
            {
                if (reportEmpty)
                {
                    ShowKeyMessage("Bitte zuerst den Schlüssel in das Feld einfügen.", ErrorBrush);
                }

                return false;
            }

            bool persisted = _host.SetApiKey(typed);
            KeyBox.Clear();
            UpdateKeyState();
            if (!persisted)
            {
                ShowKeyMessage(
                    "Der Schlüssel konnte nicht auf der Festplatte gespeichert werden. Er gilt nur, bis das Programm beendet wird.",
                    ErrorBrush);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Saving the API key failed (" + ex.GetType().Name + ")");
            ShowKeyMessage("Der Schlüssel konnte nicht gespeichert werden.", ErrorBrush);
            return false;
        }
    }

    private void OnRemoveKeyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            KeyBox.Clear();
            bool removed = _host.SetApiKey(null);
            UpdateKeyState();
            TestResultText.Text = string.Empty;
            if (!removed)
            {
                ShowKeyMessage(
                    "Die Schlüsseldatei konnte nicht gelöscht werden. Bitte die Datei „claude.key“ im Ordner %AppData%\\AutoTranslator von Hand löschen.",
                    ErrorBrush);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Removing the API key failed (" + ex.GetType().Name + ")");
        }
    }

    private void UpdateKeyState()
    {
        bool hasKey = _host.HasApiKey;
        bool claude = IsClaude(_host.Engine);

        RemoveKeyButton.IsEnabled = hasKey;
        ShowKeyMessage(
            hasKey
                ? "✓ Ein Schlüssel ist gespeichert (er wird aus Sicherheitsgründen nicht angezeigt)."
                : "Es ist noch kein Schlüssel gespeichert.",
            hasKey ? OkBrush : NeutralBrush);

        if (!claude)
        {
            EngineNoteText.Text = "Chrome übersetzt kostenlos auf diesem PC. Es wird nichts ins Internet gesendet.";
        }
        else if (hasKey)
        {
            EngineNoteText.Text = "Claude übersetzt. Wenn Claude einmal nicht erreichbar oder das Tageslimit erreicht ist, "
                + "springt automatisch Chrome ein.";
        }
        else
        {
            EngineNoteText.Text = "Claude ist gewählt, aber es fehlt noch der API-Schlüssel (siehe unten). "
                + "Bis dahin übersetzt weiterhin Chrome.";
        }
    }

    private void ShowKeyMessage(string text, Brush brush)
    {
        KeyStateText.Text = text;
        KeyStateText.Foreground = brush;
    }

    // ------------------------------------------------------------------ connection test

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        if (_testCts != null)
        {
            return; // a test is already running
        }

        // A key that was typed but not saved yet is what the user wants to test.
        SaveTypedKey(false);

        if (!_host.HasApiKey)
        {
            ShowTestResult("Bitte zuerst einen API-Schlüssel eingeben und speichern.", ErrorBrush);
            return;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _testCts = cts;
        TestButton.IsEnabled = false;
        ShowTestResult("Verbindung wird geprüft …", NeutralBrush);

        string? error;
        try
        {
            CancellationToken token = cts.Token;
            // Task.Run: nothing of the request (DNS, proxy lookup, ...) may run on the UI thread.
            error = await Task.Run(() => _host.TestClaudeAsync(token), token);
        }
        catch (OperationCanceledException)
        {
            error = _closed ? null : "Keine Antwort von Anthropic (Zeitüberschreitung). Bitte die Internetverbindung prüfen.";
        }
        catch (Exception ex)
        {
            Log.Error("Claude connection test failed (" + ex.GetType().Name + ")");
            error = "Die Verbindung ist fehlgeschlagen: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_testCts, cts))
            {
                _testCts = null;
            }

            cts.Dispose();
        }

        if (_closed)
        {
            return;
        }

        TestButton.IsEnabled = true;
        if (string.IsNullOrEmpty(error))
        {
            ShowTestResult("✓ Verbindung zu Claude funktioniert.", OkBrush);
        }
        else
        {
            ShowTestResult("✗ " + error, ErrorBrush);
        }
    }

    private void ShowTestResult(string text, Brush brush)
    {
        TestResultText.Text = text;
        TestResultText.Foreground = brush;
    }

    // ------------------------------------------------------------------ daily limit

    private void OnLimitApplyClick(object sender, RoutedEventArgs e)
    {
        ApplyLimit();
    }

    private void OnLimitKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ApplyLimit();
        }
    }

    private void ApplyLimit()
    {
        try
        {
            long limit;
            if (!TryParseLimit(LimitBox.Text, out limit))
            {
                LimitStateText.Text = "Bitte eine Zahl eingeben, zum Beispiel 300000 (0 = kein Limit).";
                LimitStateText.Foreground = ErrorBrush;
                return;
            }

            _host.SetDailyCharLimit(limit);
            LimitBox.Text = limit.ToString("N0", German);
            LimitStateText.Text = limit == 0
                ? "✓ Gespeichert: kein Tageslimit."
                : "✓ Gespeichert: höchstens " + limit.ToString("N0", German) + " Zeichen pro Tag.";
            LimitStateText.Foreground = OkBrush;
            UpdateUsage();
        }
        catch (Exception ex)
        {
            Log.Error("Changing the daily limit failed", ex);
        }
    }

    /// <summary>Accepts "300000", "300.000", "300 000"; rejects negative numbers and other text.</summary>
    private static bool TryParseLimit(string? text, out long limit)
    {
        limit = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c >= '0' && c <= '9')
            {
                digits.Append(c);
            }
            else if (c != '.' && c != '\'' && !char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return digits.Length > 0
            && digits.Length <= 15
            && long.TryParse(digits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out limit);
    }

    private void OnUsageChanged(object? sender, EventArgs e)
    {
        // May arrive on a background thread and many times per second: coalesce.
        if (Interlocked.Exchange(ref _usageRefreshQueued, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(UpdateUsage));
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _usageRefreshQueued, 0);
        }
    }

    private void UpdateUsage()
    {
        Interlocked.Exchange(ref _usageRefreshQueued, 0);
        if (_closed)
        {
            return;
        }

        try
        {
            long used = _host.CharsToday;
            long limit = _host.DailyCharLimit;
            string text = "Heute an Claude gesendet: " + used.ToString("N0", German);
            if (limit > 0)
            {
                text += " von " + limit.ToString("N0", German) + " Zeichen";
                if (used >= limit)
                {
                    text += " – Limit erreicht, bis morgen übersetzt Chrome";
                }
            }
            else
            {
                text += " Zeichen (kein Limit)";
            }

            UsageText.Text = text;
        }
        catch (Exception ex)
        {
            Log.Error("Updating the usage display failed", ex);
        }
    }

    // ------------------------------------------------------------------ other

    private void OnMergeLinesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _host.SetMergeLines(MergeLinesCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            Log.Error("Changing the merge-lines option failed", ex);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static bool IsClaude(string? engine)
    {
        return string.Equals(engine, TrayController.EngineClaude, StringComparison.OrdinalIgnoreCase);
    }

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
