using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class ChromeBridgeServerV2Tests : IAsyncLifetime
{
    private const string OriginA = "chrome-extension://aaaa";
    private const string OriginB = "chrome-extension://bbbb";
    private const string HelloV2 = """{"type":"hello","version":2,"engine":"chrome-translator","ready":true,"detail":""}""";
    private const string HelloV1 = """{"type":"hello","version":1,"engine":"chrome-translator","ready":true,"detail":""}""";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ChromeBridgeServer _server = new(0);
    private readonly List<ClientWebSocket> _sockets = new();

    public async Task InitializeAsync() => await _server.StartAsync();

    public async Task DisposeAsync()
    {
        foreach (var s in _sockets) s.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<ClientWebSocket> ConnectAsync(string origin = OriginA)
    {
        var socket = new ClientWebSocket();
        _sockets.Add(socket);
        socket.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(Timeout);
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/bridge"), cts.Token);
        return socket;
    }

    private static async Task SendAsync(ClientWebSocket socket, string json)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
    }

    private static Task SendAsync(ClientWebSocket socket, object message) =>
        SendAsync(socket, JsonSerializer.Serialize(message));

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

    private static string? TypeOf(JsonElement message) => message.GetProperty("type").GetString();

    private async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(5);
        }
    }

    /// <summary>Connects, says hello (version 2) and consumes the app-state that follows.</summary>
    private async Task<ClientWebSocket> ConnectV2Async(string origin = OriginA)
    {
        var socket = await ConnectAsync(origin);
        await SendAsync(socket, HelloV2);
        Assert.Equal("app-state", TypeOf(await ReceiveAsync(socket)));
        return socket;
    }

    private void EnablePremium(Func<IReadOnlyList<string>, string, string, CancellationToken, Task<IReadOnlyList<string>>>? handler = null)
    {
        _server.PageTranslateHandler = handler
            ?? ((texts, _, _, _) => Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "EN:" + t).ToList()));
        _server.OffersPremiumEngine = true;
    }

    private static object PageRequest(string id, params string[] texts) =>
        new { type = "page-translate", id, source = "zh", target = "en", texts };

    // ---- app-state -------------------------------------------------------------------------

    [Fact]
    public async Task HelloV2_IsAnsweredWithAppState()
    {
        var socket = await ConnectAsync();
        await SendAsync(socket, HelloV2);

        var state = await ReceiveAsync(socket);

        Assert.Equal("app-state", TypeOf(state));
        Assert.False(state.GetProperty("premium").GetBoolean());
        Assert.Equal("chrome", state.GetProperty("engine").GetString());
        await WaitUntil(() => _server.IsReady);
    }

    [Fact]
    public async Task HelloV2_ReportsPremium_WhenOffered()
    {
        _server.OffersPremiumEngine = true;
        var socket = await ConnectAsync();
        await SendAsync(socket, HelloV2);

        var state = await ReceiveAsync(socket);

        Assert.True(state.GetProperty("premium").GetBoolean());
        Assert.Equal("claude", state.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task AppState_IsResentWhenPremiumChanges_AndOnlyThen()
    {
        var socket = await ConnectV2Async();

        _server.OffersPremiumEngine = true;
        var on = await ReceiveAsync(socket);
        Assert.Equal("app-state", TypeOf(on));
        Assert.True(on.GetProperty("premium").GetBoolean());

        _server.OffersPremiumEngine = true; // unchanged: nothing is sent
        _server.OffersPremiumEngine = false;
        var off = await ReceiveAsync(socket);
        Assert.False(off.GetProperty("premium").GetBoolean());
        Assert.Equal("chrome", off.GetProperty("engine").GetString());

        await SendAsync(socket, """{"type":"ping"}""");
        Assert.Equal("pong", TypeOf(await ReceiveAsync(socket)));
    }

    [Fact]
    public async Task Version1Client_NeverGetsAppState()
    {
        var socket = await ConnectAsync();
        await SendAsync(socket, HelloV1);
        await WaitUntil(() => _server.IsReady);

        _server.OffersPremiumEngine = true;
        await SendAsync(socket, """{"type":"ping"}""");

        Assert.Equal("pong", TypeOf(await ReceiveAsync(socket)));
    }

    [Fact]
    public async Task Version2Client_StillWorksAsOnDeviceEngine()
    {
        var socket = await ConnectV2Async();
        await WaitUntil(() => _server.IsReady);
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);

        var request = await ReceiveAsync(socket);
        Assert.Equal("translate", TypeOf(request));
        await SendAsync(socket, new { type = "result", id = request.GetProperty("id").GetString(), translations = new[] { "Hello" } });

        Assert.Equal(new[] { "Hello" }, await task.WaitAsync(Timeout));
    }

    // ---- origin pinning --------------------------------------------------------------------

    [Fact]
    public async Task FirstOrigin_IsPinnedOnce()
    {
        var pinned = new List<string>();
        _server.OriginPinned += origin => { lock (pinned) pinned.Add(origin); };
        Assert.Null(_server.TrustedOrigin);

        await ConnectV2Async(OriginA);
        Assert.Equal(OriginA, _server.TrustedOrigin);

        await ConnectV2Async(OriginB);
        await ConnectV2Async(OriginA);

        Assert.Equal(OriginA, _server.TrustedOrigin);
        lock (pinned) Assert.Equal(new[] { OriginA }, pinned);
    }

    [Fact]
    public async Task PresetTrustedOrigin_IsKept_AndNothingIsPinned()
    {
        var raised = 0;
        _server.OriginPinned += _ => Interlocked.Increment(ref raised);
        _server.TrustedOrigin = OriginA;

        await ConnectV2Async(OriginB);

        Assert.Equal(OriginA, _server.TrustedOrigin);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task ResettingTrustedOrigin_PinsTheNextConnection()
    {
        await ConnectV2Async(OriginA);
        _server.TrustedOrigin = null;

        await ConnectV2Async(OriginB);

        Assert.Equal(OriginB, _server.TrustedOrigin);
    }

    [Fact]
    public async Task UntrustedOrigin_MayActAsEngine_ButNotSpendCredit()
    {
        var calls = 0;
        _server.TrustedOrigin = OriginA + "/"; // a trailing slash does not matter
        EnablePremium((texts, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<string>>(texts.ToList());
        });
        var socket = await ConnectV2Async(OriginB);
        await WaitUntil(() => _server.IsReady);

        await SendAsync(socket, PageRequest("p1", "你好"));
        var answer = await ReceiveAsync(socket);
        Assert.Equal("page-error", TypeOf(answer));
        Assert.Equal("p1", answer.GetProperty("id").GetString());
        Assert.Equal("Diese Erweiterung ist in der App nicht freigegeben.", answer.GetProperty("message").GetString());
        Assert.Equal(0, calls);

        // Version 1 behaviour is untouched for that client.
        var task = _server.TranslateAsync(new[] { "你好" }, "zh", "en", default);
        var request = await ReceiveAsync(socket);
        await SendAsync(socket, new { type = "result", id = request.GetProperty("id").GetString(), translations = new[] { "Hello" } });
        Assert.Equal(new[] { "Hello" }, await task.WaitAsync(Timeout));
    }

    // ---- page-translate --------------------------------------------------------------------

    [Fact]
    public async Task PageTranslate_RoundTrip()
    {
        (IReadOnlyList<string> Texts, string Source, string Target)? seen = null;
        EnablePremium((texts, source, target, _) =>
        {
            seen = (texts, source, target);
            return Task.FromResult<IReadOnlyList<string>>(new[] { "Hello", "World" });
        });
        var socket = await ConnectV2Async();

        await SendAsync(socket, new { type = "page-translate", id = "p7", source = "zh-TW", target = "de", texts = new[] { "你好", "世界" } });
        var answer = await ReceiveAsync(socket);

        Assert.Equal("page-result", TypeOf(answer));
        Assert.Equal("p7", answer.GetProperty("id").GetString());
        Assert.Equal(new[] { "Hello", "World" }, answer.GetProperty("translations").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "你好", "世界" }, seen!.Value.Texts);
        Assert.Equal("zh-TW", seen.Value.Source);
        Assert.Equal("de", seen.Value.Target);
    }

    [Fact]
    public async Task PageTranslate_EmptyArray_IsAnsweredWithoutCallingTheHandler()
    {
        var calls = 0;
        EnablePremium((texts, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<string>>(texts.ToList());
        });
        var socket = await ConnectV2Async();

        await SendAsync(socket, PageRequest("p0"));
        var answer = await ReceiveAsync(socket);

        Assert.Equal("page-result", TypeOf(answer));
        Assert.Empty(answer.GetProperty("translations").EnumerateArray());
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false, true)]  // no handler
    [InlineData(true, false)]  // premium off
    public async Task PageTranslate_WithoutPremium_IsAnError(bool withHandler, bool premium)
    {
        if (withHandler) _server.PageTranslateHandler = (texts, _, _, _) => Task.FromResult<IReadOnlyList<string>>(texts.ToList());
        _server.OffersPremiumEngine = premium;
        var socket = await ConnectV2Async();

        await SendAsync(socket, PageRequest("p1", "你好"));
        var answer = await ReceiveAsync(socket);

        Assert.Equal("page-error", TypeOf(answer));
        Assert.Equal("p1", answer.GetProperty("id").GetString());
        Assert.Equal("Die bessere Übersetzung ist in der App nicht aktiv.", answer.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PageTranslate_HandlerError_IsReportedWithItsMessage()
    {
        EnablePremium((_, _, _, _) => throw new InvalidOperationException("Tageslimit für Claude erreicht"));
        var socket = await ConnectV2Async();

        await SendAsync(socket, PageRequest("p1", "你好"));
        var answer = await ReceiveAsync(socket);

        Assert.Equal("page-error", TypeOf(answer));
        Assert.Equal("Tageslimit für Claude erreicht", answer.GetProperty("message").GetString());

        // The connection survives.
        await SendAsync(socket, """{"type":"ping"}""");
        Assert.Equal("pong", TypeOf(await ReceiveAsync(socket)));
    }

    [Fact]
    public async Task PageTranslate_WrongResultCount_IsAnError()
    {
        EnablePremium((_, _, _, _) => Task.FromResult<IReadOnlyList<string>>(new[] { "only one" }));
        var socket = await ConnectV2Async();

        await SendAsync(socket, PageRequest("p1", "一", "二"));

        Assert.Equal("page-error", TypeOf(await ReceiveAsync(socket)));
    }

    [Theory]
    [InlineData("""{"type":"page-translate","id":"x","texts":"你好"}""")]
    [InlineData("""{"type":"page-translate","id":"x"}""")]
    [InlineData("""{"type":"page-translate","id":"x","texts":["你好",5]}""")]
    [InlineData("""{"type":"page-translate","id":"x","texts":["你好",null]}""")]
    [InlineData("""{"type":"page-translate","id":"x","texts":[["你好"]]}""")]
    public async Task PageTranslate_InvalidTexts_AreRejected(string json)
    {
        var calls = 0;
        EnablePremium((texts, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<string>>(texts.ToList());
        });
        var socket = await ConnectV2Async();

        await SendAsync(socket, json);
        var answer = await ReceiveAsync(socket);

        Assert.Equal("page-error", TypeOf(answer));
        Assert.Equal("x", answer.GetProperty("id").GetString());
        Assert.StartsWith("Ungültige Anfrage", answer.GetProperty("message").GetString());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PageTranslate_SizeLimits()
    {
        EnablePremium();
        var socket = await ConnectV2Async();

        await SendAsync(socket, PageRequest("ok-items", Enumerable.Repeat("好", 200).ToArray()));
        var okItems = await ReceiveAsync(socket);
        Assert.Equal("page-result", TypeOf(okItems));
        Assert.Equal(200, okItems.GetProperty("translations").GetArrayLength());

        await SendAsync(socket, PageRequest("too-many", Enumerable.Repeat("好", 201).ToArray()));
        Assert.Equal("page-error", TypeOf(await ReceiveAsync(socket)));

        await SendAsync(socket, PageRequest("ok-chars", new string('好', 10000), new string('好', 10000)));
        Assert.Equal("page-result", TypeOf(await ReceiveAsync(socket)));

        await SendAsync(socket, PageRequest("too-long", new string('好', 10000), new string('好', 10001)));
        var tooLong = await ReceiveAsync(socket);
        Assert.Equal("page-error", TypeOf(tooLong));
        Assert.Equal("too-long", tooLong.GetProperty("id").GetString());
    }

    [Fact]
    public async Task PageTranslate_WithoutId_IsIgnored()
    {
        EnablePremium();
        var socket = await ConnectV2Async();

        await SendAsync(socket, """{"type":"page-translate","texts":["你好"]}""");
        await SendAsync(socket, """{"type":"ping"}""");

        Assert.Equal("pong", TypeOf(await ReceiveAsync(socket)));
    }

    [Fact]
    public async Task PageTranslate_RunsConcurrently_Capped_WithoutBlockingTheReceiveLoop()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0, peak = 0, started = 0;
        EnablePremium(async (texts, _, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, seen);
            Interlocked.Increment(ref started);
            try
            {
                await release.Task.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
            return texts.Select(t => "EN:" + t).ToList();
        });
        var socket = await ConnectV2Async();

        for (int i = 0; i < 7; i++) await SendAsync(socket, PageRequest("p" + i, "文本" + i));
        await WaitUntil(() => Volatile.Read(ref started) == 4);

        // The handlers are still busy, yet the server keeps reading and answering.
        await SendAsync(socket, """{"type":"ping"}""");
        Assert.Equal("pong", TypeOf(await ReceiveAsync(socket)));
        Assert.Equal(4, Volatile.Read(ref started));

        release.SetResult();
        var answers = new Dictionary<string, string>();
        for (int i = 0; i < 7; i++)
        {
            var answer = await ReceiveAsync(socket);
            Assert.Equal("page-result", TypeOf(answer));
            answers[answer.GetProperty("id").GetString()!] = answer.GetProperty("translations")[0].GetString()!;
        }

        for (int i = 0; i < 7; i++) Assert.Equal("EN:文本" + i, answers["p" + i]);
        Assert.Equal(4, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task Disconnect_CancelsRunningPageRequests()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EnablePremium(async (texts, _, _, ct) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return texts.ToList();
        });
        var socket = await ConnectV2Async();
        await SendAsync(socket, PageRequest("p1", "你好"));
        await started.Task.WaitAsync(Timeout);

        socket.Abort();

        await cancelled.Task.WaitAsync(Timeout);
        await WaitUntil(() => !_server.IsConnected);
    }
}
