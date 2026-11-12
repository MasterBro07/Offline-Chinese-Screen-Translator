using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class ChromeBridgeServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ChromeBridgeServer _server = new(0);
    private readonly List<ClientWebSocket> _sockets = new();

    public async Task InitializeAsync() => await _server.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var s in _sockets) s.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<ClientWebSocket> ConnectAsync(string? origin = "chrome-extension://test")
    {
        var socket = new ClientWebSocket();
        _sockets.Add(socket);
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.CollectHttpResponseDetails = true;
        using var cts = new CancellationTokenSource(Timeout);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/bridge"), cts.Token);
        return socket;
    }

    private static async Task SendAsync(ClientWebSocket socket, string json)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("socket closed");
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonDocument.Parse(ms.ToArray()).RootElement.Clone();
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"condition not reached in time (status: {_server.StatusText})");
            await Task.Delay(5);
        }
    }

    private async Task<ClientWebSocket> ConnectReadyAsync()
    {
        var socket = await ConnectAsync();
        await SendAsync(socket, """{"type":"hello","version":1,"engine":"chrome-translator","ready":true,"detail":""}""");
        await WaitUntil(() => _server.IsReady);
        return socket;
    }

    [Fact]
    public void UsesEphemeralPort()
    {
        Assert.NotEqual(0, _server.Port);
    }

    [Fact]
    public async Task HelloAndStatus_DriveReadinessAndStatusText()
    {
        var changes = 0;
        _server.StatusChanged += (_, _) => Interlocked.Increment(ref changes);
        Assert.False(_server.IsReady);
        Assert.Equal("Chrome-Erweiterung nicht verbunden", _server.StatusText);

        var socket = await ConnectAsync();
        await WaitUntil(() => _server.IsConnected);
        Assert.False(_server.IsReady);

        await SendAsync(socket, """{"type":"hello","version":1,"engine":"chrome-translator","ready":true,"detail":""}""");
        await WaitUntil(() => _server.IsReady);
        Assert.Equal("Verbunden – bereit", _server.StatusText);

        await SendAsync(socket, """{"type":"status","ready":false,"detail":"zh→en"}""");
        await WaitUntil(() => !_server.IsReady);
        Assert.Equal("Verbunden – Sprachpaket fehlt: zh→en", _server.StatusText);

        await SendAsync(socket, """{"type":"status","ready":true,"detail":""}""");
        await WaitUntil(() => _server.IsReady);

        using (var cts = new CancellationTokenSource(Timeout))
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
        await WaitUntil(() => !_server.IsConnected);
        Assert.False(_server.IsReady);
        Assert.Equal("Chrome-Erweiterung nicht verbunden", _server.StatusText);
        await WaitUntil(() => Volatile.Read(ref changes) >= 5); // the event follows the state change
    }

    [Fact]
    public async Task Translate_RoundTrip()
    {
        var socket = await ConnectReadyAsync();
        var task = _server.TranslateAsync(new[] { "你好", "世界" }, "zh", "en", default);

        var request = await ReceiveAsync(socket);
        Assert.Equal("translate", request.GetProperty("type").GetString());
        Assert.Equal("zh", request.GetProperty("source").GetString());
        Assert.Equal("en", request.GetProperty("target").GetString());
        Assert.Equal(new[] { "你好", "世界" }, request.GetProperty("texts").EnumerateArray().Select(e => e.GetString()));
        var id = request.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));

        await SendAsync(socket, JsonSerializer.Serialize(new { type = "result", id, translations = new[] { "Hello", "World" } }));
        Assert.Equal(new[] { "Hello", "World" }, await task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task ConcurrentRequests_AreCorrelatedById_AndLargeMessagesWork()
    {
        var socket = await ConnectReadyAsync();
        var big = new string('好', 20000); // larger than one receive buffer in both directions
        var tasks = Enumerable.Range(0, 8)
            .Select(i => _server.TranslateAsync(new[] { i + ":" + big }, "zh", "en", default))
            .ToArray();

        var requests = new List<JsonElement>();
        for (int i = 0; i < tasks.Length; i++) requests.Add(await ReceiveAsync(socket));

        // Answer in reverse order.
        foreach (var request in Enumerable.Reverse(requests))
        {
            var text = request.GetProperty("texts")[0].GetString()!;
            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "result",
                id = request.GetProperty("id").GetString(),
                translations = new[] { "EN:" + text },
            }));
        }

        var results = await Task.WhenAll(tasks).WaitAsync(Timeout);
        for (int i = 0; i < results.Length; i++) Assert.Equal("EN:" + i + ":" + big, results[i][0]);
    }

    [Fact]
    public async Task ErrorMessage_ThrowsInvalidOperationWithMessage()
    {
        var socket = await ConnectReadyAsync();
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        var id = (await ReceiveAsync(socket)).GetProperty("id").GetString();

        await SendAsync(socket, JsonSerializer.Serialize(new { type = "error", id, message = "Sprachpaket fehlt" }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
        Assert.Equal("Sprachpaket fehlt", ex.Message);
    }

    [Fact]
    public async Task ResultWithWrongLength_Throws()
    {
        var socket = await ConnectReadyAsync();
        var task = _server.TranslateAsync(new[] { "一", "二" }, "zh", "en", default);
        var id = (await ReceiveAsync(socket)).GetProperty("id").GetString();

        await SendAsync(socket, JsonSerializer.Serialize(new { type = "result", id, translations = new[] { "one" } }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData(null)]
    public async Task WrongOrigin_IsRejectedWith403(string? origin)
    {
        var socket = new ClientWebSocket();
        _sockets.Add(socket);
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        socket.Options.CollectHttpResponseDetails = true;
        using var cts = new CancellationTokenSource(Timeout);

        await Assert.ThrowsAsync<WebSocketException>(
            () => socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/bridge"), cts.Token));
        Assert.Equal(HttpStatusCode.Forbidden, socket.HttpStatusCode);
        Assert.False(_server.IsConnected);
    }

    [Fact]
    public async Task NoClient_ThrowsInvalidOperation()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.TranslateAsync(new[] { "你好" }, "zh", "en", default));
    }

    [Fact]
    public async Task Disconnect_FailsPendingRequests()
    {
        var socket = await ConnectReadyAsync();
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        await ReceiveAsync(socket);

        socket.Abort();

        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
        await WaitUntil(() => !_server.IsConnected);
    }

    [Fact]
    public async Task NewClient_ReplacesOldOne()
    {
        var first = await ConnectReadyAsync();
        var pending = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        await ReceiveAsync(first);

        var second = await ConnectAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Timeout));

        // The old socket gets closed by the server.
        using (var cts = new CancellationTokenSource(Timeout))
        {
            try
            {
                var result = await first.ReceiveAsync(new byte[256], cts.Token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            catch (WebSocketException)
            {
                // aborted instead of closed gracefully: also fine
            }
        }

        // Readiness belongs to the new client, which has not said hello yet.
        await WaitUntil(() => _server.IsConnected && !_server.IsReady);
        await SendAsync(second, """{"type":"hello","version":1,"ready":true,"detail":""}""");
        await WaitUntil(() => _server.IsReady);

        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        var id = (await ReceiveAsync(second)).GetProperty("id").GetString();
        await SendAsync(second, JsonSerializer.Serialize(new { type = "result", id, translations = new[] { "Hello" } }));
        Assert.Equal(new[] { "Hello" }, await task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutException()
    {
        _server.RequestTimeout = TimeSpan.FromMilliseconds(100);
        var socket = await ConnectReadyAsync();
        await Assert.ThrowsAsync<TimeoutException>(
            () => _server.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));
        Assert.True(_server.IsReady); // the connection survives a timeout
        GC.KeepAlive(socket);
    }

    [Fact]
    public async Task Cancellation_IsHonoured()
    {
        var socket = await ConnectReadyAsync();
        using var cts = new CancellationTokenSource();
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", cts.Token);
        await ReceiveAsync(socket);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Timeout));
        Assert.True(_server.IsReady);
    }

    [Fact]
    public async Task SendsPings_AndAnswersPingWithPong()
    {
        _server.PingInterval = TimeSpan.FromMilliseconds(30);
        var socket = await ConnectReadyAsync();

        Assert.Equal("ping", (await ReceiveAsync(socket)).GetProperty("type").GetString());

        await SendAsync(socket, """{"type":"ping"}""");
        await SendAsync(socket, "not json at all"); // must be ignored
        while ((await ReceiveAsync(socket)).GetProperty("type").GetString() != "pong") { }
        Assert.True(_server.IsReady);
    }

    [Fact]
    public async Task StopAsync_IsIdempotent_AndFailsPending()
    {
        var socket = await ConnectReadyAsync();
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        await ReceiveAsync(socket);

        await _server.StopAsync().WaitAsync(Timeout);
        await _server.StopAsync().WaitAsync(Timeout);
        await _server.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(Timeout));
        Assert.False(_server.IsReady);
        Assert.Equal("Chrome-Erweiterung nicht verbunden", _server.StatusText);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _server.TranslateAsync(new[] { "你好" }, "zh", "en", default));
    }
}
