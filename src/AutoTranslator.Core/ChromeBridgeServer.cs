using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoTranslator.Core;

/// <summary>
/// Localhost WebSocket server the Chrome extension connects to. The extension acts as
/// the translation engine; see "Bridge protocol" in docs/ARCHITECTURE.md.
/// </summary>
public sealed class ChromeBridgeServer : ITranslator, IAsyncDisposable
{
    private const string BridgePath = "/bridge";
    private const string AllowedOriginPrefix = "chrome-extension://";
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private const int MaxConcurrentPageRequests = 4;   // handler calls running at once, per client
    private const int MaxQueuedPageRequests = 32;      // accepted but unanswered, per client
    private const int MaxPageItems = 200;
    private const int MaxPageChars = 20000;

    private sealed class Client
    {
        public Client(WebSocket socket, string origin)
        {
            Socket = socket;
            Origin = origin;
        }

        public WebSocket Socket { get; }
        public string Origin { get; }
        /// <summary>Protocol version from the client's hello; 0 until it said hello.</summary>
        public int Version;
        /// <summary>page-translate requests accepted and not yet answered.</summary>
        public int PageRequests;
        public SemaphoreSlim PageGate { get; } = new(MaxConcurrentPageRequests, MaxConcurrentPageRequests);
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource Cts { get; } = new();
        public TaskCompletionSource ReceiveDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Closed;
        public bool Ready;
        public string Detail = "";
    }

    private sealed record Pending(Client Client, int Count, TaskCompletionSource<IReadOnlyList<string>> Completion);

    private readonly int _requestedPort;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _clientLock = new();
    private readonly object _statusLock = new();
    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private Client? _client;
    private long _nextId;
    private int _port;
    private volatile bool _offersPremiumEngine;
    private string? _trustedOrigin;
    private (bool Ready, string Text) _lastStatus = (false, NotConnectedText);

    private const string NotConnectedText = "Chrome-Erweiterung nicht verbunden";

    public ChromeBridgeServer(int port = 47613)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _port = port;
    }

    /// <summary>The port being listened on; the real one after <see cref="StartAsync"/> when constructed with 0.</summary>
    public int Port => Volatile.Read(ref _port);

    /// <summary>How long <see cref="TranslateAsync"/> waits for an answer.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Interval of the keep-alive pings; applies to clients connecting afterwards.</summary>
    public TimeSpan PingInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>True while an extension is connected, ready or not.</summary>
    public bool IsConnected
    {
        get { lock (_clientLock) return _client is not null; }
    }

    public bool IsReady
    {
        get { lock (_clientLock) return _client is { Ready: true }; }
    }

    /// <summary>
    /// True when the app can translate page text with a better engine than the on-device one.
    /// Drives <c>app-state.premium</c>; version 2 clients are told about every change.
    /// </summary>
    public bool OffersPremiumEngine
    {
        get => _offersPremiumEngine;
        set
        {
            Client? client;
            lock (_clientLock)
            {
                if (_offersPremiumEngine == value) return;
                _offersPremiumEngine = value;
                client = _client;
            }
            if (client is not null) _ = SendAppStateAsync(client);
        }
    }

    /// <summary>Engine name reported in <c>app-state.engine</c> while <see cref="OffersPremiumEngine"/> is true.</summary>
    public string PremiumEngineName { get; set; } = "claude";

    /// <summary>
    /// The only extension origin whose <c>page-translate</c> requests are served, because they spend
    /// the user's API credit. Null: the first origin that connects gets pinned.
    /// </summary>
    public string? TrustedOrigin
    {
        get { lock (_clientLock) return _trustedOrigin; }
        set { lock (_clientLock) _trustedOrigin = string.IsNullOrWhiteSpace(value) ? null : NormalizeOrigin(value); }
    }

    /// <summary>Raised once when an origin gets pinned, with that origin; store it in the settings.</summary>
    public event Action<string>? OriginPinned;

    /// <summary>
    /// Translates page text for the extension: (texts, source, target, ct) → translations in the
    /// same order and number. Called concurrently.
    /// </summary>
    public Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<string>>>?
        PageTranslateHandler { get; set; }

    public string StatusText
    {
        get { lock (_clientLock) return DescribeStatus(_client); }
    }

    public event EventHandler? StatusChanged;

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null) return;

            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Logging.ClearProviders();
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
            builder.WebHost.UseKestrelCore();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.AddServerHeader = false;
                o.Listen(IPAddress.Loopback, _requestedPort);
            });

            var app = builder.Build();
            app.UseWebSockets();
            app.Run(HandleRequestAsync);

            try
            {
                await app.StartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
            if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                Volatile.Write(ref _port, uri.Port);
            }
            _app = app;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var app = _app;
            if (app is null) return;
            _app = null;

            Client? client;
            lock (_clientLock)
            {
                client = _client;
                _client = null;
            }
            if (client is not null) await CloseClientAsync(client, "server stopping").ConfigureAwait(false);
            NotifyStatus();

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await app.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                // Shutting down anyway.
            }
            await app.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ct.ThrowIfCancellationRequested();
        if (texts.Count == 0) return Array.Empty<string>();

        Client? client;
        lock (_clientLock) client = _client;
        if (client is null) throw new InvalidOperationException(NotConnectedText);

        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(client, texts.Count, completion);
        try
        {
            // The client may have gone away between the lookup and the registration.
            if (client.Closed) throw new InvalidOperationException("Chrome-Erweiterung getrennt");

            var message = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "translate",
                id,
                source = sourceLanguage,
                target = targetLanguage,
                texts,
            });
            try
            {
                await SendAsync(client, message, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                throw new InvalidOperationException("Chrome-Erweiterung getrennt", ex);
            }

            try
            {
                return await completion.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Zeitüberschreitung: keine Antwort von der Chrome-Erweiterung.");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Path.Value, BridgePath, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        // Web pages can open WebSockets to localhost too; only extensions may use the bridge.
        var origin = context.Request.Headers.Origin.ToString();
        if (!origin.StartsWith(AllowedOriginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var client = new Client(socket, NormalizeOrigin(origin));

        Client? previous;
        string? pinned = null;
        lock (_clientLock)
        {
            previous = _client;
            _client = client;
            // Trust on first use: the first extension that connects may spend API credit.
            if (_trustedOrigin is null) pinned = _trustedOrigin = client.Origin;
        }
        if (previous is not null) _ = CloseClientAsync(previous, "replaced by a new connection");
        if (pinned is not null)
        {
            try { OriginPinned?.Invoke(pinned); } catch { /* listener errors are not ours */ }
        }
        NotifyStatus();

        var pingTask = PingLoopAsync(client);
        try
        {
            await ReceiveLoopAsync(client).ConfigureAwait(false);
        }
        finally
        {
            lock (_clientLock)
            {
                if (ReferenceEquals(_client, client)) _client = null;
            }
            NotifyStatus();
            await CloseClientAsync(client, "closing").ConfigureAwait(false);
            await pingTask.ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(Client client)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        try
        {
            while (client.Socket.State == WebSocketState.Open)
            {
                var result = await client.Socket.ReceiveAsync(buffer.AsMemory(), client.Cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes) return;
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await HandleMessageAsync(client, message.GetBuffer().AsMemory(0, (int)message.Length)).ConfigureAwait(false);
                }
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException
                                       or ObjectDisposedException or IOException)
        {
            // Connection is gone; the caller cleans up.
        }
        finally
        {
            client.ReceiveDone.TrySetResult();
        }
    }

    private async Task HandleMessageAsync(Client client, ReadOnlyMemory<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var type = GetString(root, "type");
            switch (type)
            {
                case "hello":
                case "status":
                    var ready = root.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True;
                    var detail = GetString(root, "detail") ?? "";
                    lock (_clientLock)
                    {
                        client.Ready = ready;
                        client.Detail = detail;
                        if (type == "hello")
                        {
                            client.Version = root.TryGetProperty("version", out var v)
                                             && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version)
                                ? Math.Max(1, version)
                                : 1;
                        }
                    }
                    NotifyStatus();
                    if (type == "hello") await SendAppStateAsync(client).ConfigureAwait(false);
                    break;

                case "page-translate":
                    HandlePageTranslate(client, root);
                    break;

                case "result":
                    if (TakePending(client, root) is { } resultFor)
                    {
                        if (TryReadTranslations(root, out var translations) && translations.Count == resultFor.Count)
                        {
                            resultFor.Completion.TrySetResult(translations);
                        }
                        else
                        {
                            resultFor.Completion.TrySetException(new InvalidOperationException(
                                "Ungültige Antwort der Chrome-Erweiterung: Anzahl der Übersetzungen stimmt nicht."));
                        }
                    }
                    break;

                case "error":
                    if (TakePending(client, root) is { } errorFor)
                    {
                        var text = GetString(root, "message");
                        errorFor.Completion.TrySetException(new InvalidOperationException(
                            string.IsNullOrWhiteSpace(text) ? "Unbekannter Fehler der Chrome-Erweiterung." : text));
                    }
                    break;

                case "ping":
                    try
                    {
                        await SendAsync(client, "{\"type\":\"pong\"}"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The receive loop notices a dead socket by itself.
                    }
                    break;
            }
        }
    }

    /// <summary>Tells a version 2 client whether page text can be translated through the app.</summary>
    private async Task SendAppStateAsync(Client client)
    {
        // Version 1 clients do not know the message; clients that have not said hello get it after their hello.
        if (Volatile.Read(ref client.Version) < 2 || client.Closed) return;
        try
        {
            // Read the flag inside the send lock so the last frame on the wire carries the latest value.
            await client.SendLock.WaitAsync(client.Cts.Token).ConfigureAwait(false);
            try
            {
                var premium = _offersPremiumEngine;
                var message = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "app-state",
                    premium,
                    engine = premium ? PremiumEngineName : "chrome",
                });
                await client.Socket.SendAsync(message.AsMemory(), WebSocketMessageType.Text, true, client.Cts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                client.SendLock.Release();
            }
        }
        catch
        {
            // The receive loop notices a dead socket by itself.
        }
    }

    /// <summary>Validates a page-translate request and starts it; never blocks the receive loop.</summary>
    private void HandlePageTranslate(Client client, JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement)) return; // nothing to answer to
        var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText();
        if (string.IsNullOrEmpty(id)) return;

        var handler = PageTranslateHandler;
        if (handler is null || !_offersPremiumEngine)
        {
            _ = SendPageErrorAsync(client, id, "Die bessere Übersetzung ist in der App nicht aktiv.");
            return;
        }

        bool trusted;
        lock (_clientLock) trusted = string.Equals(_trustedOrigin, client.Origin, StringComparison.OrdinalIgnoreCase);
        if (!trusted)
        {
            _ = SendPageErrorAsync(client, id, "Diese Erweiterung ist in der App nicht freigegeben.");
            return;
        }

        if (!TryReadPageTexts(root, out var texts))
        {
            _ = SendPageErrorAsync(client, id,
                $"Ungültige Anfrage: höchstens {MaxPageItems} Texte mit zusammen {MaxPageChars} Zeichen.");
            return;
        }

        if (Interlocked.Increment(ref client.PageRequests) > MaxQueuedPageRequests)
        {
            Interlocked.Decrement(ref client.PageRequests);
            _ = SendPageErrorAsync(client, id, "Zu viele gleichzeitige Anfragen.");
            return;
        }

        var source = GetString(root, "source") ?? "zh";
        var target = GetString(root, "target") ?? "en";
        _ = Task.Run(() => RunPageTranslateAsync(client, handler, id, texts, source, target));
    }

    private async Task RunPageTranslateAsync(Client client,
        Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<string>>> handler,
        string id, List<string> texts, string source, string target)
    {
        try
        {
            IReadOnlyList<string> translations;
            if (texts.Count == 0)
            {
                translations = Array.Empty<string>();
            }
            else
            {
                var ct = client.Cts.Token;
                await client.PageGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    translations = await handler(texts, source, target, ct).ConfigureAwait(false);
                }
                finally
                {
                    client.PageGate.Release();
                }
            }

            if (translations is null || translations.Count != texts.Count)
            {
                await SendPageErrorAsync(client, id, "Übersetzer lieferte eine unerwartete Anzahl von Ergebnissen.")
                    .ConfigureAwait(false);
                return;
            }

            var message = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "page-result",
                id,
                translations = translations.Select(t => t ?? ""),
            });
            await SendAsync(client, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (client.Closed) return; // nobody left to tell
            await SendPageErrorAsync(client, id,
                string.IsNullOrWhiteSpace(ex.Message) || ex is OperationCanceledException
                    ? "Übersetzung fehlgeschlagen."
                    : ex.Message).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref client.PageRequests);
        }
    }

    private static async Task SendPageErrorAsync(Client client, string id, string text)
    {
        try
        {
            var message = JsonSerializer.SerializeToUtf8Bytes(new { type = "page-error", id, message = text });
            await SendAsync(client, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The receive loop notices a dead socket by itself.
        }
    }

    /// <summary>Reads "texts": an array of strings within the size limits.</summary>
    private static bool TryReadPageTexts(JsonElement root, out List<string> texts)
    {
        texts = new List<string>();
        if (!root.TryGetProperty("texts", out var array) || array.ValueKind != JsonValueKind.Array) return false;
        if (array.GetArrayLength() > MaxPageItems) return false;
        long chars = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            var text = item.GetString() ?? "";
            chars += text.Length;
            if (chars > MaxPageChars) return false;
            texts.Add(text);
        }
        return true;
    }

    private static string NormalizeOrigin(string origin) => origin.Trim().TrimEnd('/');

    private Pending? TakePending(Client client, JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement)) return null;
        var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.GetRawText();
        if (id is null || !_pending.TryGetValue(id, out var pending) || !ReferenceEquals(pending.Client, client)) return null;
        return _pending.TryRemove(id, out pending) ? pending : null;
    }

    private static bool TryReadTranslations(JsonElement root, out List<string> translations)
    {
        translations = new List<string>();
        if (!root.TryGetProperty("translations", out var array) || array.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) translations.Add(item.GetString() ?? "");
            else if (item.ValueKind == JsonValueKind.Null) translations.Add("");
            else return false;
        }
        return true;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private async Task PingLoopAsync(Client client)
    {
        var ping = "{\"type\":\"ping\"}"u8.ToArray();
        try
        {
            using var timer = new PeriodicTimer(PingInterval);
            while (await timer.WaitForNextTickAsync(client.Cts.Token).ConfigureAwait(false))
            {
                await SendAsync(client, ping, client.Cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Cancelled or the socket died; either way the receive loop ends the connection.
        }
    }

    /// <summary>Sends one text frame; sends on a socket must not overlap.</summary>
    private static async Task SendAsync(Client client, byte[] utf8Json, CancellationToken ct)
    {
        await client.SendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cancelling a send in flight would abort the socket, so only the client's own token is used here.
            await client.Socket.SendAsync(utf8Json.AsMemory(), WebSocketMessageType.Text, true, client.Cts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    /// <summary>Fails the client's pending requests and closes its socket. Safe to call more than once.</summary>
    private async Task CloseClientAsync(Client client, string reason)
    {
        client.Closed = true;
        foreach (var entry in _pending)
        {
            if (ReferenceEquals(entry.Value.Client, client) && _pending.TryRemove(entry.Key, out var pending))
            {
                pending.Completion.TrySetException(new InvalidOperationException("Chrome-Erweiterung getrennt"));
            }
        }

        try
        {
            if (client.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await client.SendLock.WaitAsync(timeout.Token).ConfigureAwait(false);
                try
                {
                    await client.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    client.SendLock.Release();
                }
            }
        }
        catch
        {
            // Best effort; the abort below settles it.
        }

        // Give the peer a moment to answer the close frame; aborting right away would
        // reset the connection and turn a clean close into an error on the other side.
        if (!client.ReceiveDone.Task.IsCompleted)
        {
            await Task.WhenAny(client.ReceiveDone.Task, Task.Delay(TimeSpan.FromMilliseconds(500))).ConfigureAwait(false);
        }
        var finished = client.ReceiveDone.Task.IsCompleted;
        try { client.Cts.Cancel(); } catch (ObjectDisposedException) { }
        if (!finished)
        {
            // Abort can throw when the request finished concurrently; there is nothing left to abort then.
            try { client.Socket.Abort(); } catch { }
        }
    }

    private static string DescribeStatus(Client? client)
    {
        if (client is null) return NotConnectedText;
        if (client.Ready) return "Verbunden – bereit";
        return string.IsNullOrWhiteSpace(client.Detail)
            ? "Verbunden – Übersetzer nicht bereit"
            : "Verbunden – Sprachpaket fehlt: " + client.Detail;
    }

    private void NotifyStatus()
    {
        lock (_statusLock)
        {
            (bool, string) current;
            lock (_clientLock) current = (_client is { Ready: true }, DescribeStatus(_client));
            if (current == _lastStatus) return;
            _lastStatus = current;
        }
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { /* listener errors are not ours */ }
    }
}
