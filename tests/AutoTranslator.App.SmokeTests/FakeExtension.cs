using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator.SmokeTests;

/// <summary>One "translate" request the app sent over the bridge.</summary>
internal sealed class BridgeRequest
{
    public BridgeRequest(DateTime utc, string id, List<string> texts)
    {
        Utc = utc;
        Id = id;
        Texts = texts;
    }

    public DateTime Utc { get; }

    public string Id { get; }

    public List<string> Texts { get; }
}

/// <summary>
/// Plays the Chrome extension: connects to the app's bridge, says hello and answers every
/// "translate" request with "EN[...]" (see "Bridge protocol" in docs/ARCHITECTURE.md).
/// </summary>
internal sealed class FakeExtension : IDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly List<BridgeRequest> _requests = new List<BridgeRequest>();
    private readonly List<string> _otherMessages = new List<string>();
    private Task? _receiveLoop;

    private FakeExtension(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public bool IsOpen
    {
        get { return _socket.State == WebSocketState.Open; }
    }

    /// <summary>Opens a WebSocket to the bridge with the given Origin header (null = no header). Throws when refused.</summary>
    public static async Task<FakeExtension> ConnectAsync(int port, string? origin)
    {
        var socket = new ClientWebSocket();
        if (origin != null)
        {
            socket.Options.SetRequestHeader("Origin", origin);
        }

        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/bridge"), timeout.Token);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var extension = new FakeExtension(socket);
        extension._receiveLoop = Task.Run(extension.ReceiveLoopAsync);
        return extension;
    }

    public Task SendHelloAsync()
    {
        return SendAsync("{\"type\":\"hello\",\"version\":1,\"engine\":\"smoke-test\",\"ready\":true,\"detail\":\"\"}");
    }

    public List<BridgeRequest> Requests()
    {
        lock (_requests)
        {
            return _requests.ToList();
        }
    }

    public List<string> OtherMessages()
    {
        lock (_requests)
        {
            return _otherMessages.ToList();
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _socket.Abort();
        }
        catch
        {
            // best effort
        }

        _socket.Dispose();
    }

    private async Task SendAsync(string json)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16384];
        var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                string json = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                await HandleAsync(json).ConfigureAwait(false);
            }
        }
        catch
        {
            // socket closed
        }
    }

    private async Task HandleAsync(string json)
    {
        using (JsonDocument document = JsonDocument.Parse(json))
        {
            JsonElement root = document.RootElement;
            string type = root.TryGetProperty("type", out JsonElement t) ? (t.GetString() ?? string.Empty) : string.Empty;
            if (type == "ping")
            {
                await SendAsync("{\"type\":\"pong\"}").ConfigureAwait(false);
                return;
            }

            if (type != "translate")
            {
                lock (_requests)
                {
                    _otherMessages.Add(json);
                }

                return;
            }

            string id = root.GetProperty("id").ToString();
            List<string> texts = root.GetProperty("texts").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
            lock (_requests)
            {
                _requests.Add(new BridgeRequest(DateTime.UtcNow, id, texts));
            }

            string answer = JsonSerializer.Serialize(new
            {
                type = "result",
                id,
                translations = texts.Select(text => "EN[" + text + "]").ToList(),
            });
            await SendAsync(answer).ConfigureAwait(false);
        }
    }
}
