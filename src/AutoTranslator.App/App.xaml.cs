using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Application entry point: wires settings, cache, Chrome bridge, Claude engine, text source,
/// pipeline, overlay, tray icon and hotkey together. There is no main window.
/// </summary>
public partial class App : Application, ISettingsHost
{
    private const string MutexName = "Local\\AutoTranslator.SingleInstance";
    private const string AppTitle = "AutoTranslator";
    private const string CacheTagClaude = "claude";

    private static readonly TimeSpan UsageSaveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SessionEndStopTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _pipelineGate = new SemaphoreSlim(1, 1);

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    private string _settingsPath = string.Empty;
    private AppSettings _settings = new AppSettings();
    private TranslationCache? _cache;
    private ChromeBridgeServer? _bridge;
    private string? _bridgeError;

    // Engines. All of them are created once at startup; switching the engine only changes
    // which one the pipeline gets (_translator).
    private ApiKeyStore? _keyStore;
    private UsageTracker? _usage;
    private ClaudeTranslator? _claude;
    private ITranslator? _chromeTranslator;      // cache → Chrome extension
    private ITranslator? _claudeTranslator;      // cache → Claude, Chrome extension as fallback
    private ITranslator? _claudePageTranslator;  // cache → Claude only (web pages; the extension falls back itself)
    private ITranslator? _translator;            // the one the pipeline uses right now
    private int _usageDirty;
    private DateTime _usageSavedUtc = DateTime.UtcNow;

    private OverlayWindow? _overlay;
    private ITextSource? _source;
    private TranslationPipeline? _pipeline;

    private TrayController? _tray;
    private GlobalHotkey? _hotkey;
    private bool _hotkeyFailed;
    private HelpWindow? _helpWindow;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _statusTimer;
    private DispatcherTimer? _saveTimer;

    private bool _initialized;
    private bool _exiting;
    private bool _stateSaved;
    private int _statusRefreshQueued;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // ------------------------------------------------------------------ startup

    private async void OnAppStartup(object sender, StartupEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show(
                "AutoTranslator konnte nicht gestartet werden.\n\n" + ex.Message
                + "\n\nDetails stehen in:\n" + Log.FilePath,
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync();
        }
    }

    private async Task InitializeAsync()
    {
        bool createdNew;
        _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show(
                "AutoTranslator läuft bereits.\n\nDas Symbol befindet sich im Infobereich neben der Uhr "
                + "(eventuell hinter dem Pfeil „^“).",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Log.Info("AutoTranslator starting");

        string directory = AppSettings.DefaultDirectory;
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
        bool firstRun = !File.Exists(_settingsPath);

        _settings = AppSettings.Load(_settingsPath);
        _settings.TextSourceMode = NormalizeMode(_settings.TextSourceMode);
        _settings.CaptureScope = NormalizeScope(_settings.CaptureScope);
        _settings.Engine = NormalizeEngine(_settings.Engine);
        if (_settings.ClaudeDailyCharLimit < 0)
        {
            _settings.ClaudeDailyCharLimit = 0;
        }

        _cache = new TranslationCache(20000, Path.Combine(directory, "cache.json"));
        _cache.Load();

        _keyStore = new ApiKeyStore(Path.Combine(directory, "claude.key"));
        _usage = new UsageTracker(Path.Combine(directory, "usage.json"), _settings.ClaudeDailyCharLimit);
        _usage.Changed += OnUsageTrackerChanged;

        // The bridge is always started: it is the free engine, the fallback for Claude, and the
        // way web pages in Chrome reach Claude.
        _bridge = new ChromeBridgeServer(_settings.BridgePort);
        _bridge.TrustedOrigin = string.IsNullOrWhiteSpace(_settings.TrustedExtensionOrigin)
            ? null
            : _settings.TrustedExtensionOrigin;
        _bridge.OriginPinned += OnBridgeOriginPinned;
        _bridge.PageTranslateHandler = TranslatePageAsync;

        var claudeOptions = new ClaudeTranslatorOptions();
        if (!string.IsNullOrWhiteSpace(_settings.ClaudeModel))
        {
            claudeOptions.Model = _settings.ClaudeModel.Trim();
        }

        _claude = new ClaudeTranslator(_keyStore.Get, claudeOptions, _usage);

        _chromeTranslator = new CachingTranslator(_bridge, _cache);
        _claudeTranslator = new CachingTranslator(new FallbackTranslator(_claude, _bridge), _cache, CacheTagClaude);
        _claudePageTranslator = new CachingTranslator(_claude, _cache, CacheTagClaude);
        ApplyEngineState();

        // Subscribed at the two real engines, so every wrapper combination is covered.
        _bridge.StatusChanged += OnTranslatorStatusChanged;
        _claude.StatusChanged += OnTranslatorStatusChanged;

        try
        {
            await _bridge.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Starting the Chrome bridge on port " + _settings.BridgePort + " failed", ex);
            _bridgeError = "Verbindung zu Chrome nicht möglich (Port " + _settings.BridgePort + " belegt)";
            MessageBox.Show(
                "Die Verbindung zur Chrome-Erweiterung konnte nicht gestartet werden.\n\n"
                + "Der Port " + _settings.BridgePort + " wird vermutlich schon von einem anderen Programm "
                + "verwendet. Bitte das andere Programm schließen (oder den Computer neu starten) und "
                + "AutoTranslator danach erneut starten.\n\nTechnische Meldung: " + ex.Message,
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _overlay = new OverlayWindow();
        _overlay.Initialize();

        _tray = new TrayController();
        _tray.ToggleRequested += OnToggleRequested;
        _tray.ModeSelected += OnModeSelected;
        _tray.ScopeSelected += OnScopeSelected;
        _tray.EngineSelected += OnEngineSelected;
        _tray.SettingsRequested += OnSettingsRequested;
        _tray.RepairRequested += OnRepairRequested;
        _tray.HelpRequested += OnHelpRequested;
        _tray.ExitRequested += OnExitRequested;

        _hotkey = new GlobalHotkey(ToggleEnabled);
        if (!_hotkey.IsRegistered)
        {
            _hotkeyFailed = true;
            Log.Error("Hotkey Ctrl+Alt+T could not be registered (already used by another program).");
        }

        _initialized = true;
        await RestartPipelineAsync();

        _statusTimer = new DispatcherTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();

        _saveTimer = new DispatcherTimer();
        _saveTimer.Interval = TimeSpan.FromMinutes(5);
        _saveTimer.Tick += OnSaveTimerTick;
        _saveTimer.Start();

        RefreshStatus();

        if (firstRun)
        {
            SaveSettings();
            ShowHelp();
        }
        else if (_hotkeyFailed && _tray != null)
        {
            // On the first run the help window shows the same hint in its status box.
            _tray.ShowNotification(
                "Tastenkürzel nicht verfügbar",
                "Strg+Alt+T wird schon von einem anderen Programm benutzt. "
                + "Ein- und Ausschalten geht über das Menü dieses Symbols (Rechtsklick).");
        }
    }

    // ------------------------------------------------------------------ engines

    private bool IsClaudeSelected
    {
        get { return string.Equals(_settings.Engine, TrayController.EngineClaude, StringComparison.Ordinal); }
    }

    /// <summary>
    /// Selects the translator for the pipeline and tells the extension whether web pages can be
    /// translated through this app. Call after every engine or key change. UI thread only.
    /// </summary>
    private void ApplyEngineState()
    {
        bool claude = IsClaudeSelected;
        _translator = claude ? _claudeTranslator : _chromeTranslator;

        ChromeBridgeServer? bridge = _bridge;
        if (bridge != null)
        {
            bool premium = claude && _keyStore != null && _keyStore.HasKey;
            if (bridge.OffersPremiumEngine != premium)
            {
                bridge.OffersPremiumEngine = premium;
            }
        }
    }

    /// <summary>
    /// Translates web page text for the extension (bridge message "page-translate"). Runs on a
    /// background thread. Uses the same cache entries and the same daily limit as the screen
    /// translation. Throwing makes the bridge answer "page-error", and the extension then uses
    /// its own on-device translator for that batch.
    /// </summary>
    private async Task<IReadOnlyList<string>> TranslatePageAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        ITranslator? translator = _claudePageTranslator;
        ApiKeyStore? keyStore = _keyStore;
        if (_exiting || translator == null || keyStore == null || !IsClaudeSelected || !keyStore.HasKey)
        {
            throw new InvalidOperationException("Claude ist in AutoTranslator nicht eingeschaltet.");
        }

        return await translator.TranslateAsync(texts, sourceLanguage, targetLanguage, ct).ConfigureAwait(false);
    }

    private void OnBridgeOriginPinned(string origin)
    {
        // Raised on a background thread.
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_exiting)
                {
                    return;
                }

                _settings.TrustedExtensionOrigin = origin;
                SaveSettings();
                Log.Info("Chrome extension paired: " + origin);
            }));
        }
        catch (Exception ex)
        {
            Log.Error("Storing the paired extension failed", ex);
        }
    }

    private void OnUsageTrackerChanged(object? sender, EventArgs e)
    {
        // Raised on background threads; the status timer writes the file a little later.
        Interlocked.Exchange(ref _usageDirty, 1);
    }

    private void SaveUsageIfDirty()
    {
        UsageTracker? usage = _usage;
        if (usage == null || Volatile.Read(ref _usageDirty) == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _usageSavedUtc < UsageSaveInterval)
        {
            return;
        }

        _usageSavedUtc = now;
        Interlocked.Exchange(ref _usageDirty, 0);
        Task.Run(() => usage.Save());
    }

    private async Task ChangeEngineAsync(string engine)
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        string normalized = NormalizeEngine(engine);
        if (!string.Equals(_settings.Engine, normalized, StringComparison.Ordinal))
        {
            _settings.Engine = normalized;
            SaveSettings();
            ApplyEngineState();
            Log.Info("Engine changed to " + normalized);
            await RestartPipelineAsync();
        }

        RefreshStatus();
        if (_settingsWindow != null)
        {
            _settingsWindow.RefreshFromHost();
        }
    }

    // ------------------------------------------------------------------ ISettingsHost

    string ISettingsHost.Engine
    {
        get { return _settings.Engine; }
    }

    bool ISettingsHost.HasApiKey
    {
        get { return _keyStore != null && _keyStore.HasKey; }
    }

    long ISettingsHost.DailyCharLimit
    {
        get { return _settings.ClaudeDailyCharLimit; }
    }

    long ISettingsHost.CharsToday
    {
        get { return _usage != null ? _usage.CharsToday : 0; }
    }

    bool ISettingsHost.MergeLines
    {
        get { return _settings.MergeLines; }
    }

    event EventHandler? ISettingsHost.UsageChanged
    {
        add
        {
            if (_usage != null)
            {
                _usage.Changed += value;
            }
        }

        remove
        {
            if (_usage != null)
            {
                _usage.Changed -= value;
            }
        }
    }

    async void ISettingsHost.SetEngine(string engine)
    {
        try
        {
            await ChangeEngineAsync(engine);
        }
        catch (Exception ex)
        {
            Log.Error("Changing the engine failed", ex);
        }
    }

    bool ISettingsHost.SetApiKey(string? key)
    {
        if (!_initialized || _exiting || _keyStore == null)
        {
            return false;
        }

        bool persisted = _keyStore.Set(key);
        Log.Info(_keyStore.HasKey ? "API key stored" : "API key removed");

        if (_claude != null)
        {
            _claude.NotifyKeyChanged();
        }

        ApplyEngineState();
        InvalidateSource();
        RefreshStatus();
        return persisted;
    }

    void ISettingsHost.SetDailyCharLimit(long limit)
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        if (limit < 0)
        {
            limit = 0;
        }

        _settings.ClaudeDailyCharLimit = limit;
        if (_usage != null)
        {
            _usage.DailyCharLimit = limit;
        }

        SaveSettings();
        InvalidateSource();
        RefreshStatus();
    }

    async void ISettingsHost.SetMergeLines(bool mergeLines)
    {
        if (!_initialized || _exiting || _settings.MergeLines == mergeLines)
        {
            return;
        }

        try
        {
            _settings.MergeLines = mergeLines;
            SaveSettings();
            await RestartPipelineAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Changing the merge-lines option failed", ex);
        }
    }

    Task<string?> ISettingsHost.TestClaudeAsync(CancellationToken ct)
    {
        ClaudeTranslator? claude = _claude;
        if (claude == null)
        {
            return Task.FromResult<string?>("Das Programm ist noch nicht vollständig gestartet.");
        }

        return claude.TestAsync(ct);
    }

    // ------------------------------------------------------------------ pipeline

    private ITextSource CreateTextSource()
    {
        // The scope is read on every capture, so changing it needs no new source.
        Func<string> scope = () => _settings.CaptureScope;

        ITextSource source;
        switch (_settings.TextSourceMode)
        {
            case TrayController.ModeUiAutomation:
                source = new UiAutomationTextSource();
                break;
            case TrayController.ModeBoth:
                source = new CompositeTextSource(new UiAutomationTextSource(), new OcrTextSource(scope));
                break;
            default:
                source = new OcrTextSource(scope);
                break;
        }

        return new HoldingTextSource(source, ShouldHoldOverlay);
    }

    /// <summary>
    /// True while the tray menu is open in "whole primary monitor" mode. The menu makes this
    /// process the foreground one, which the capture code treats as "nothing to translate";
    /// without this the overlay would vanish every time the menu opens. Called on a background thread.
    /// </summary>
    private bool ShouldHoldOverlay()
    {
        TrayController? tray = _tray;
        return tray != null
            && tray.IsMenuOpen
            && string.Equals(_settings.CaptureScope, ScreenCapture.ScopePrimaryMonitor, StringComparison.Ordinal);
    }

    /// <summary>Stops the running pipeline (if any) and starts a new one when translation is enabled.</summary>
    private async Task RestartPipelineAsync()
    {
        if (!_initialized || _translator == null || _overlay == null)
        {
            return;
        }

        await _pipelineGate.WaitAsync();
        try
        {
            await StopPipelineCoreAsync();

            if (_exiting || !_settings.Enabled)
            {
                return;
            }

            _source = CreateTextSource();
            var options = new PipelineOptions
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(200, _settings.CaptureIntervalMs)),
                SourceLanguage = _settings.SourceLanguage,
                TargetLanguage = _settings.TargetLanguage,
                MergeLines = _settings.MergeLines,
            };

            var pipeline = new TranslationPipeline(_source, _translator, _overlay, options);
            pipeline.StatusChanged += OnPipelineStatusChanged;
            _pipeline = pipeline;
            pipeline.Start();
        }
        finally
        {
            _pipelineGate.Release();
            RefreshStatus();
        }
    }

    private async Task StopPipelineAsync()
    {
        await _pipelineGate.WaitAsync();
        try
        {
            await StopPipelineCoreAsync();
        }
        finally
        {
            _pipelineGate.Release();
        }
    }

    private async Task StopPipelineCoreAsync()
    {
        TranslationPipeline? pipeline = _pipeline;
        _pipeline = null;
        _source = null;

        if (pipeline != null)
        {
            pipeline.StatusChanged -= OnPipelineStatusChanged;
            try
            {
                await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Error("Stopping the pipeline failed", ex);
            }

            try
            {
                await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                Log.Error("Disposing the pipeline failed", ex);
            }
        }

        if (_overlay != null)
        {
            _overlay.Clear();
        }
    }

    // ------------------------------------------------------------------ user actions

    private void OnToggleRequested(object? sender, EventArgs e)
    {
        ToggleEnabled();
    }

    private async void ToggleEnabled()
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        try
        {
            _settings.Enabled = !_settings.Enabled;
            SaveSettings();
            RefreshStatus();
            await RestartPipelineAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Toggling translation failed", ex);
        }
    }

    private async void OnModeSelected(object? sender, string mode)
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        try
        {
            _settings.TextSourceMode = NormalizeMode(mode);
            SaveSettings();
            await RestartPipelineAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Changing the text source failed", ex);
        }
    }

    private void OnScopeSelected(object? sender, string scope)
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        _settings.CaptureScope = NormalizeScope(scope);
        SaveSettings();
        InvalidateSource();
        RefreshStatus();
    }

    private async void OnEngineSelected(object? sender, string engine)
    {
        try
        {
            await ChangeEngineAsync(engine);

            // Claude without a key cannot work: lead the user straight to the place to enter it.
            if (IsClaudeSelected && _keyStore != null && !_keyStore.HasKey)
            {
                ShowSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Changing the engine failed", ex);
        }
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        ShowSettings();
    }

    private void OnRepairRequested(object? sender, EventArgs e)
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        _settings.TrustedExtensionOrigin = null;
        SaveSettings();
        if (_bridge != null)
        {
            _bridge.TrustedOrigin = null;
        }

        Log.Info("Extension pairing reset");
        if (_tray != null)
        {
            _tray.ShowNotification(
                "Kopplung zurückgesetzt",
                "Die nächste Chrome-Erweiterung, die sich verbindet, wird neu gekoppelt. "
                + "Falls Webseiten danach nicht mit Claude übersetzt werden: Chrome einmal schließen und neu öffnen.");
        }
    }

    private void OnHelpRequested(object? sender, EventArgs e)
    {
        ShowHelp();
    }

    private void ShowSettings()
    {
        if (!_initialized || _exiting)
        {
            return;
        }

        try
        {
            if (_settingsWindow == null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += OnSettingsWindowClosed;
                _settingsWindow.Show();
            }
            else if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("Opening the settings window failed", ex);
        }
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        _settingsWindow = null;
    }

    private async void OnExitRequested(object? sender, EventArgs e)
    {
        await ExitAsync();
    }

    private void ShowHelp()
    {
        try
        {
            if (_helpWindow == null)
            {
                _helpWindow = new HelpWindow(BuildHelpStatus);
                _helpWindow.Closed += OnHelpWindowClosed;
                _helpWindow.Show();
            }
            else
            {
                if (_helpWindow.WindowState == WindowState.Minimized)
                {
                    _helpWindow.WindowState = WindowState.Normal;
                }

                _helpWindow.Activate();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Opening the help window failed", ex);
        }
    }

    private void OnHelpWindowClosed(object? sender, EventArgs e)
    {
        _helpWindow = null;
    }

    // ------------------------------------------------------------------ status

    private void OnTranslatorStatusChanged(object? sender, EventArgs e)
    {
        // Raised on a background thread. A capture that could not be translated earlier must be
        // repeated now, even though the screen itself has not changed.
        InvalidateSource();
        QueueStatusRefresh();
    }

    private void OnPipelineStatusChanged(object? sender, EventArgs e)
    {
        QueueStatusRefresh();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        RefreshStatus();
        SaveUsageIfDirty();
    }

    private void InvalidateSource()
    {
        IRefreshableTextSource? refreshable = _source as IRefreshableTextSource;
        if (refreshable != null)
        {
            refreshable.Invalidate();
        }
    }

    private void QueueStatusRefresh()
    {
        if (Interlocked.Exchange(ref _statusRefreshQueued, 1) == 1)
        {
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(new Action(RefreshStatus));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _statusRefreshQueued, 0);
            Log.Error("Queueing the status refresh failed", ex);
        }
    }

    /// <summary>Updates the tray menu, icon and tooltip. UI thread only.</summary>
    private void RefreshStatus()
    {
        Interlocked.Exchange(ref _statusRefreshQueued, 0);
        if (_tray == null || _exiting)
        {
            return;
        }

        try
        {
            _tray.Update(
                _settings.Enabled, _settings.TextSourceMode, _settings.CaptureScope,
                _settings.Engine, BuildEngineLabel(), BuildStatus(), !_hotkeyFailed);
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the tray status failed", ex);
        }
    }

    /// <summary>True when Claude is selected and can translate right now (key present, limit not reached).</summary>
    private bool IsClaudeWorking
    {
        get
        {
            ClaudeTranslator? claude = _claude;
            return IsClaudeSelected && claude != null && claude.IsReady;
        }
    }

    /// <summary>Short name of the engine that translates right now, for the tray.</summary>
    private string BuildEngineLabel()
    {
        if (!IsClaudeSelected)
        {
            return "Chrome";
        }

        return IsClaudeWorking ? "Claude" : "Chrome (Claude nicht bereit)";
    }

    /// <summary>Short one-line status for the tray menu and tooltip.</summary>
    private string BuildStatus()
    {
        // With Claude working, a broken Chrome bridge only costs the fallback.
        if (_bridgeError != null && !IsClaudeWorking)
        {
            return _bridgeError;
        }

        if (!_settings.Enabled)
        {
            return _hotkeyFailed
                ? "Pausiert (Einschalten über dieses Menü)"
                : "Pausiert (Strg+Alt+T zum Einschalten)";
        }

        string? warning = (_source as IRefreshableTextSource)?.Warning;
        if (!string.IsNullOrEmpty(warning))
        {
            return warning;
        }

        ITranslator? translator = _translator;
        string translatorStatus = translator != null ? (translator.StatusText ?? string.Empty) : string.Empty;
        if (translator != null && !translator.IsReady)
        {
            return translatorStatus.Length > 0 ? translatorStatus : "Chrome-Erweiterung nicht verbunden";
        }

        TranslationPipeline? pipeline = _pipeline;
        string pipelineStatus = pipeline != null ? (pipeline.StatusText ?? string.Empty) : string.Empty;
        if (pipelineStatus.Length > 0)
        {
            return pipelineStatus;
        }

        return translatorStatus.Length > 0 ? translatorStatus : "Bereit";
    }

    /// <summary>Multi-line status shown in the help window.</summary>
    private string BuildHelpStatus()
    {
        ChromeBridgeServer? bridge = _bridge;
        string chrome;
        if (_bridgeError != null)
        {
            chrome = "✗ " + _bridgeError;
        }
        else if (bridge != null && bridge.IsReady)
        {
            chrome = "✓ Chrome-Erweiterung verbunden und bereit";
        }
        else
        {
            string detail = bridge != null ? (bridge.StatusText ?? string.Empty) : string.Empty;
            chrome = "✗ Chrome-Erweiterung noch nicht bereit" + (detail.Length > 0 ? " – " + detail : string.Empty);
        }

        string engine;
        if (!IsClaudeSelected)
        {
            engine = "– Übersetzer: Chrome (kostenlos). Claude Haiku lässt sich unter „Einstellungen…“ einschalten";
        }
        else if (IsClaudeWorking)
        {
            engine = "✓ Übersetzer: Claude Haiku";
        }
        else
        {
            ClaudeTranslator? claude = _claude;
            string detail = claude != null ? (claude.StatusText ?? string.Empty) : string.Empty;
            if (_keyStore != null && !_keyStore.HasKey)
            {
                detail = "API-Schlüssel fehlt (siehe „Einstellungen…“)";
            }

            engine = "✗ Claude Haiku ist nicht bereit" + (detail.Length > 0 ? " – " + detail : string.Empty)
                + ". Solange übersetzt Chrome";
        }

        bool needsOcr = _settings.TextSourceMode != TrayController.ModeUiAutomation;
        string ocr;
        if (OcrTextSource.IsChineseOcrAvailable())
        {
            ocr = "✓ Chinesische Texterkennung (OCR) ist installiert";
        }
        else if (needsOcr)
        {
            ocr = "✗ Chinesische Texterkennung (OCR) fehlt – siehe Schritt 3";
        }
        else
        {
            ocr = "– Chinesische Texterkennung (OCR) fehlt (für „UI-Automation“ nicht nötig)";
        }

        string active = _settings.Enabled
            ? "✓ Übersetzung ist eingeschaltet"
            : "– Übersetzung ist pausiert (Strg+Alt+T)";

        string result = chrome + Environment.NewLine + engine + Environment.NewLine + ocr
            + Environment.NewLine + active;
        if (_hotkeyFailed)
        {
            result += Environment.NewLine
                + "✗ Strg+Alt+T wird von einem anderen Programm benutzt – bitte über das Menü am Symbol ein- und ausschalten";
        }

        return result;
    }

    // ------------------------------------------------------------------ persistence

    private void SaveSettings()
    {
        try
        {
            if (_settingsPath.Length > 0)
            {
                _settings.Save(_settingsPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Saving the settings failed", ex);
        }
    }

    private void OnSaveTimerTick(object? sender, EventArgs e)
    {
        TranslationCache? cache = _cache;
        UsageTracker? usage = _usage;
        if (cache == null)
        {
            return;
        }

        // Periodic save so that a crash or power loss does not lose the whole cache.
        Task.Run(() =>
        {
            try
            {
                if (usage != null)
                {
                    usage.Save();
                }

                cache.Save();
            }
            catch (Exception ex)
            {
                Log.Error("Saving the translation cache failed", ex);
            }
        });
    }

    private void SaveState()
    {
        if (_stateSaved || !_initialized)
        {
            return;
        }

        _stateSaved = true;
        SaveSettings();
        try
        {
            if (_usage != null)
            {
                _usage.Save();
            }

            if (_cache != null)
            {
                _cache.Save();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Saving the translation cache failed", ex);
        }
    }

    // ------------------------------------------------------------------ shutdown

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
            }

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
            }

            if (_hotkey != null)
            {
                _hotkey.Dispose();
                _hotkey = null;
            }

            if (_helpWindow != null)
            {
                _helpWindow.Close();
            }

            if (_settingsWindow != null)
            {
                _settingsWindow.Close();
            }

            await StopPipelineAsync();
            SaveState();
            DetachEngineEvents();

            if (_bridge != null)
            {
                try
                {
                    await _bridge.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch (Exception ex)
                {
                    Log.Error("Stopping the Chrome bridge failed", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }
        finally
        {
            if (_tray != null)
            {
                _tray.Dispose();
                _tray = null;
            }

            if (_overlay != null)
            {
                _overlay.Shutdown();
                _overlay = null;
            }

            Log.Info("AutoTranslator stopped");
            Shutdown();
        }
    }

    private void DetachEngineEvents()
    {
        if (_bridge != null)
        {
            _bridge.StatusChanged -= OnTranslatorStatusChanged;
            _bridge.OriginPinned -= OnBridgeOriginPinned;
            _bridge.PageTranslateHandler = null;
        }

        if (_claude != null)
        {
            _claude.StatusChanged -= OnTranslatorStatusChanged;
        }

        if (_usage != null)
        {
            _usage.Changed -= OnUsageTrackerChanged;
        }
    }

    private void OnAppSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down. Save first (that is what matters), then stop
        // the background work best-effort within a short time limit; WPF ends the app afterwards.
        SaveState();
        StopForSessionEnd();
    }

    /// <summary>
    /// Synchronous, time-limited stop of pipeline and bridge for Windows logoff/shutdown.
    /// The UI thread waits at most <see cref="SessionEndStopTimeout"/>; nothing here needs the
    /// dispatcher (the overlay is only asked to redraw asynchronously).
    /// </summary>
    private void StopForSessionEnd()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_statusTimer != null)
            {
                _statusTimer.Stop();
            }

            if (_saveTimer != null)
            {
                _saveTimer.Stop();
            }

            DetachEngineEvents();

            TranslationPipeline? pipeline = _pipeline;
            ChromeBridgeServer? bridge = _bridge;
            _pipeline = null;
            _source = null;

            Task stop = Task.Run(async () =>
            {
                if (pipeline != null)
                {
                    try
                    {
                        await pipeline.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Stopping the pipeline at session end failed", ex);
                    }
                }

                if (bridge != null)
                {
                    try
                    {
                        await bridge.StopAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Stopping the Chrome bridge at session end failed", ex);
                    }
                }
            });

            if (!stop.Wait(SessionEndStopTimeout))
            {
                Log.Info("Session end: background work did not stop in time");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stopping at session end failed", ex);
        }
    }

    private void OnAppExit(object sender, ExitEventArgs e)
    {
        SaveState();

        if (_tray != null)
        {
            _tray.Dispose();
            _tray = null;
        }

        if (_singleInstanceMutex != null)
        {
            try
            {
                if (_ownsMutex)
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Releasing the single-instance mutex failed", ex);
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    // ------------------------------------------------------------------ error handling

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception on the UI thread", e.Exception);
        e.Handled = true; // keep the tray app alive
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled exception (terminating: " + e.IsTerminating + ")", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    // ------------------------------------------------------------------ helpers

    private static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, TrayController.ModeUiAutomation, StringComparison.OrdinalIgnoreCase))
        {
            return TrayController.ModeUiAutomation;
        }

        if (string.Equals(mode, TrayController.ModeBoth, StringComparison.OrdinalIgnoreCase))
        {
            return TrayController.ModeBoth;
        }

        return TrayController.ModeOcr;
    }

    private static string NormalizeEngine(string? engine)
    {
        return string.Equals(engine, TrayController.EngineClaude, StringComparison.OrdinalIgnoreCase)
            ? TrayController.EngineClaude
            : TrayController.EngineChrome;
    }

    private static string NormalizeScope(string? scope)
    {
        if (string.Equals(scope, ScreenCapture.ScopePrimaryMonitor, StringComparison.OrdinalIgnoreCase))
        {
            return ScreenCapture.ScopePrimaryMonitor;
        }

        return ScreenCapture.ScopeForegroundWindow;
    }
}
