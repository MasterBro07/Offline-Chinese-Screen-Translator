using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Application entry point: wires settings, cache, Chrome bridge, text source, pipeline,
/// overlay, tray icon and hotkey together. There is no main window.
/// </summary>
public partial class App : Application
{
    private const string MutexName = "Local\\AutoTranslator.SingleInstance";
    private const string AppTitle = "AutoTranslator";

    private readonly SemaphoreSlim _pipelineGate = new SemaphoreSlim(1, 1);

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    private string _settingsPath = string.Empty;
    private AppSettings _settings = new AppSettings();
    private TranslationCache? _cache;
    private ChromeBridgeServer? _bridge;
    private ITranslator? _translator;
    private string? _bridgeError;

    private OverlayWindow? _overlay;
    private ITextSource? _source;
    private TranslationPipeline? _pipeline;

    private TrayController? _tray;
    private GlobalHotkey? _hotkey;
    private HelpWindow? _helpWindow;
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

        _cache = new TranslationCache(20000, Path.Combine(directory, "cache.json"));
        _cache.Load();

        _bridge = new ChromeBridgeServer(_settings.BridgePort);
        _translator = new CachingTranslator(_bridge, _cache);
        _translator.StatusChanged += OnTranslatorStatusChanged;

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
        _tray.HelpRequested += OnHelpRequested;
        _tray.ExitRequested += OnExitRequested;

        _hotkey = new GlobalHotkey(ToggleEnabled);
        if (!_hotkey.IsRegistered)
        {
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
    }

    // ------------------------------------------------------------------ pipeline

    private ITextSource CreateTextSource()
    {
        // The scope is read on every capture, so changing it needs no new source.
        Func<string> scope = () => _settings.CaptureScope;

        switch (_settings.TextSourceMode)
        {
            case TrayController.ModeUiAutomation:
                return new UiAutomationTextSource();
            case TrayController.ModeBoth:
                return new CompositeTextSource(new UiAutomationTextSource(), new OcrTextSource(scope));
            default:
                return new OcrTextSource(scope);
        }
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

    private void OnHelpRequested(object? sender, EventArgs e)
    {
        ShowHelp();
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
            _tray.Update(_settings.Enabled, _settings.TextSourceMode, _settings.CaptureScope, BuildStatus());
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the tray status failed", ex);
        }
    }

    /// <summary>Short one-line status for the tray menu and tooltip.</summary>
    private string BuildStatus()
    {
        if (_bridgeError != null)
        {
            return _bridgeError;
        }

        if (!_settings.Enabled)
        {
            return "Pausiert (Strg+Alt+T zum Einschalten)";
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
        ITranslator? translator = _translator;
        string chrome;
        if (_bridgeError != null)
        {
            chrome = "✗ " + _bridgeError;
        }
        else if (translator != null && translator.IsReady)
        {
            chrome = "✓ Chrome-Erweiterung verbunden und bereit";
        }
        else
        {
            string detail = translator != null ? (translator.StatusText ?? string.Empty) : string.Empty;
            chrome = "✗ Chrome-Erweiterung noch nicht bereit" + (detail.Length > 0 ? " – " + detail : string.Empty);
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

        return chrome + Environment.NewLine + ocr + Environment.NewLine + active;
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
        if (cache == null)
        {
            return;
        }

        // Periodic save so that a crash or power loss does not lose the whole cache.
        Task.Run(() =>
        {
            try
            {
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

            await StopPipelineAsync();
            SaveState();

            if (_translator != null)
            {
                _translator.StatusChanged -= OnTranslatorStatusChanged;
            }

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

    private void OnAppSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down: there is no time for a graceful stop.
        SaveState();
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

    private static string NormalizeScope(string? scope)
    {
        if (string.Equals(scope, ScreenCapture.ScopePrimaryMonitor, StringComparison.OrdinalIgnoreCase))
        {
            return ScreenCapture.ScopePrimaryMonitor;
        }

        return ScreenCapture.ScopeForegroundWindow;
    }
}
