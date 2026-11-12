using System.Text.Json;
using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeTranslator"/> through the real Anthropic SDK against a local fake
/// of the Messages API. No request ever leaves the machine.
/// </summary>
public class ClaudeTranslatorTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private FakeAnthropicServer _server = null!;

    public async Task InitializeAsync() => _server = await FakeAnthropicServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private ClaudeTranslator Create(Action<ClaudeTranslatorOptions>? configure = null, UsageTracker? usage = null,
        Func<string?>? key = null)
    {
        var options = new ClaudeTranslatorOptions { BaseUrl = _server.BaseUrl, MaxRetries = 0 };
        configure?.Invoke(options);
        return new ClaudeTranslator(key ?? (() => "sk-ant-test-key"), options, usage);
    }

    [Fact]
    public void Options_HaveDocumentedDefaults()
    {
        var options = new ClaudeTranslatorOptions();
        Assert.Equal("claude-haiku-5-5", options.Model);
        Assert.Null(options.BaseUrl);
        Assert.Equal(60, options.MaxBatchItems);
        Assert.Equal(6000, options.MaxBatchChars);
        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoKey_IsNotReady_AndNeverCallsTheApi(string? key)
    {
        var translator = Create(key: () => key);

        Assert.False(translator.IsReady);
        Assert.Equal("Claude: kein API-Schlüssel", translator.StatusText);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Equal("Claude: kein API-Schlüssel", ex.Message);
        Assert.Equal("Claude: kein API-Schlüssel", await translator.TestAsync(default));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void NotifyKeyChanged_ReevaluatesReadiness_AndRaisesStatusChanged()
    {
        string? key = null;
        var translator = Create(key: () => key);
        var changes = 0;
        translator.StatusChanged += (_, _) => changes++;

        key = "sk-ant-new";
        translator.NotifyKeyChanged();

        Assert.True(translator.IsReady);
        Assert.Equal("Claude bereit (Haiku)", translator.StatusText);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Translate_SendsOneRequestInTheExpectedShape()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Translations(new[] { "Hello", "Add to cart" }));
        var translator = Create();

        var result = await translator.TranslateAsync(new[] { "你好", "加入购物车" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "Hello", "Add to cart" }, result);
        Assert.Equal("Claude bereit (Haiku)", translator.StatusText);

        var request = Assert.Single(_server.Requests);
        Assert.Equal("/v1/messages", request.Path);
        // SDK 13.8.0 sends the API key as "Authorization: Bearer <key>" (older releases used x-api-key).
        Assert.Equal("sk-ant-test-key", request.Key);
        Assert.Equal("Bearer sk-ant-test-key", request.Authorization);
        Assert.False(string.IsNullOrEmpty(request.AnthropicVersion));

        var body = request.Body;
        Assert.Equal("claude-haiku-5-5", body.GetProperty("model").GetString());
        Assert.Equal(16000, body.GetProperty("max_tokens").GetInt64());
        Assert.Equal("disabled", body.GetProperty("thinking").GetProperty("type").GetString());
        var outputConfig = body.GetProperty("output_config");
        Assert.Equal("low", outputConfig.GetProperty("effort").GetString());
        var format = outputConfig.GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("translations").GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        // No streaming, no sampling parameters, no prefill: exactly one user message.
        Assert.False(body.TryGetProperty("stream", out var stream) && stream.GetBoolean());
        Assert.False(body.TryGetProperty("temperature", out _));
        var message = Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(new[] { "你好", "加入购物车" }, request.Texts);

        var system = body.GetProperty("system");
        var systemText = system.ValueKind == JsonValueKind.String ? system.GetString()! : system[0].GetProperty("text").GetString()!;
        Assert.Contains("Chinese (zh)", systemText);
        Assert.Contains("English (en)", systemText);
        Assert.Contains("OCR", systemText);
    }

    [Fact]
    public async Task ModelComesFromOptions()
    {
        var translator = Create(o => o.Model = "claude-sonnet-5-5");

        await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal("claude-sonnet-5-5", Assert.Single(_server.Requests).Body.GetProperty("model").GetString());
        Assert.Equal("Claude bereit (Sonnet)", translator.StatusText);
    }

    [Fact]
    public async Task BigInput_IsSplitByItemCount_AndOrderIsPreserved()
    {
        var translator = Create(o => o.MaxBatchItems = 3);
        var texts = Enumerable.Range(0, 8).Select(i => "文本" + i).ToArray();

        var result = await translator.TranslateAsync(texts, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(texts.Select(t => "EN:" + t), result);
        var batches = _server.Requests.Select(r => r.Texts).OrderBy(t => t[0], StringComparer.Ordinal).ToList();
        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { "文本0", "文本1", "文本2" }, batches[0]);
        Assert.Equal(new[] { "文本3", "文本4", "文本5" }, batches[1]);
        Assert.Equal(new[] { "文本6", "文本7" }, batches[2]);
    }

    [Fact]
    public async Task BigInput_IsSplitByCharacterCount()
    {
        var translator = Create(o => o.MaxBatchChars = 10);
        var texts = new[] { "一二三四", "五六七八", "九十", new string('长', 25), "末" };

        var result = await translator.TranslateAsync(texts, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(texts.Select(t => "EN:" + t), result);
        var sizes = _server.Requests.Select(r => string.Join("|", r.Texts)).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(
            new[] { "一二三四|五六七八|九十", new string('长', 25), "末" }.OrderBy(s => s, StringComparer.Ordinal),
            sizes);
    }

    [Fact]
    public async Task BlankItems_AreReturnedUnchanged_AndNotSent()
    {
        var translator = Create();

        var result = await translator.TranslateAsync(new[] { "", "你好", "  " }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "", "EN:你好", "  " }, result);
        Assert.Equal(new[] { "你好" }, Assert.Single(_server.Requests).Texts);

        Assert.Empty(await translator.TranslateAsync(Array.Empty<string>(), "zh", "en", default));
        Assert.Equal(new[] { " " }, await translator.TranslateAsync(new[] { " " }, "zh", "en", default));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task LengthMismatch_IsRetriedOnce()
    {
        var calls = 0;
        _server.Responder = (request, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? FakeAnthropicServer.Translations(new[] { "only one" })
            : FakeAnthropicServer.Translations(request.Texts.Select(t => "EN:" + t)));
        var translator = Create();

        var result = await translator.TranslateAsync(new[] { "一", "二" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "EN:一", "EN:二" }, result);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal("Claude bereit (Haiku)", translator.StatusText);
    }

    [Fact]
    public async Task PersistentLengthMismatch_FallsBackToOneItemPerRequest()
    {
        _server.Responder = (request, _) => Task.FromResult(request.Texts.Length > 1
            ? FakeAnthropicServer.Translations(new[] { "merged everything" })
            : FakeAnthropicServer.Translations(new[] { "EN:" + request.Texts[0] }));
        var translator = Create();

        var result = await translator.TranslateAsync(new[] { "一", "二", "三" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "EN:一", "EN:二", "EN:三" }, result);
        Assert.Equal(new[] { 3, 3, 1, 1, 1 }, _server.Requests.Select(r => r.Texts.Length));
    }

    [Fact]
    public async Task UnparsableReply_ForASingleItem_FailsAfterOneRetry()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Message("Sure! Here is the translation: Hello"));
        var translator = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Claude lieferte eine ungültige Antwort", ex.Message);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task BareArrayAndFencedReplies_AreAccepted()
    {
        var replies = new Queue<string>(new[] { "[\"Hello\"]", "```json\n{\"translations\":[\"World\"]}\n```" });
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Message(replies.Dequeue()));
        var translator = Create();

        Assert.Equal(new[] { "Hello" }, await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));
        Assert.Equal(new[] { "World" }, await translator.TranslateAsync(new[] { "世界" }, "zh", "en", default).WaitAsync(Timeout));
    }

    [Theory]
    [InlineData("authentication_error", "Claude-API-Schlüssel ist ungültig")]
    [InlineData("permission_error", "Keine Berechtigung für die Claude-API")]
    [InlineData("billing_error", "Kein Guthaben auf dem Claude-Konto")]
    [InlineData("not_found_error", "Claude-Modell „claude-haiku-5-5“ nicht gefunden")]
    [InlineData("rate_limit_error", "Claude-Anfragelimit erreicht – bitte kurz warten")]
    [InlineData("overloaded_error", "Claude ist überlastet – bitte später erneut versuchen")]
    [InlineData("api_error", "Claude ist überlastet – bitte später erneut versuchen")]
    [InlineData("invalid_request_error", "Claude-Anfrage abgelehnt (HTTP 400)")]
    public async Task ApiErrors_AreMappedToGermanMessages(string errorType, string expected)
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Error(errorType, "nope"));
        var translator = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal(expected, ex.Message);
        Assert.Equal("Claude-Fehler: " + expected, translator.StatusText);
        Assert.True(translator.IsReady); // the key is still there
        Assert.Single(_server.Requests); // MaxRetries = 0 in these tests
    }

    [Fact]
    public async Task EmptyCreditBalance_ReportedAs400_IsMappedToNoCredit()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Error("invalid_request_error",
            "Your credit balance is too low to access the Anthropic API."));
        var translator = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Kein Guthaben auf dem Claude-Konto", ex.Message);
    }

    [Fact]
    public async Task ErrorStatus_LastsUntilTheNextSuccess()
    {
        var fail = true;
        _server.Responder = (request, _) => Task.FromResult(fail
            ? FakeAnthropicServer.Error("authentication_error", "invalid x-api-key")
            : FakeAnthropicServer.Translations(request.Texts.Select(t => "EN:" + t)));
        var translator = Create();
        var changes = 0;
        translator.StatusChanged += (_, _) => Interlocked.Increment(ref changes);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));
        Assert.Equal("Claude-Fehler: Claude-API-Schlüssel ist ungültig", translator.StatusText);
        Assert.Equal(1, changes);

        fail = false;
        await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout);
        Assert.Equal("Claude bereit (Haiku)", translator.StatusText);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task RetriableErrors_AreRetriedBySdk_WhenRetriesAreEnabled()
    {
        var calls = 0;
        _server.Responder = (request, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? FakeAnthropicServer.Error("overloaded_error", "busy")
            : FakeAnthropicServer.Translations(request.Texts.Select(t => "EN:" + t)));
        var translator = Create(o => o.MaxRetries = 1);

        var result = await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "EN:你好" }, result);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task Refusal_IsAnError()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Refusal());
        var translator = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Claude hat die Übersetzung abgelehnt", ex.Message);
        Assert.Single(_server.Requests);
        Assert.Equal("Claude-Fehler: Claude hat die Übersetzung abgelehnt", translator.StatusText);
    }

    [Fact]
    public async Task MaxTokensStop_IsAnError()
    {
        _server.Responder = (_, _) => Task.FromResult(
            FakeAnthropicServer.Message("{\"translations\":[\"Hel", stopReason: "max_tokens"));
        var translator = Create();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Claude-Antwort wurde abgeschnitten", ex.Message);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task DailyLimitReached_ThrowsWithoutCallingTheApi()
    {
        var usage = new UsageTracker(null, dailyCharLimit: 5);
        var translator = Create(usage: usage);

        await translator.TranslateAsync(new[] { "你好吗" }, "zh", "en", default).WaitAsync(Timeout);
        Assert.Equal(3, usage.CharsToday);
        Assert.Single(_server.Requests);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "一二三" }, "zh", "en", default));
        Assert.Equal("Tageslimit für Claude erreicht", ex.Message);
        Assert.Equal("Claude-Fehler: Tageslimit für Claude erreicht", translator.StatusText);
        Assert.Equal(3, usage.CharsToday);
        Assert.Single(_server.Requests); // no second HTTP call

        // Something that still fits is translated.
        await translator.TranslateAsync(new[] { "好的" }, "zh", "en", default).WaitAsync(Timeout);
        Assert.Equal(5, usage.CharsToday);
    }

    [Fact]
    public async Task FailedRequests_DoNotCountAgainstTheDailyLimit()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Error("overloaded_error", "busy"));
        var usage = new UsageTracker(null, dailyCharLimit: 100);
        var translator = Create(usage: usage);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好", "世界" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal(0, usage.CharsToday);
    }

    [Fact]
    public async Task Cancellation_IsHonoured_AndIsNotAnError()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Responder = async (_, ct) =>
        {
            arrived.TrySetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var usage = new UsageTracker(null, dailyCharLimit: 100);
        var translator = Create(usage: usage);
        using var cts = new CancellationTokenSource();

        var task = translator.TranslateAsync(new[] { "你好" }, "zh", "en", cts.Token);
        await arrived.Task.WaitAsync(Timeout);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Timeout));
        Assert.Equal("Claude bereit (Haiku)", translator.StatusText);
        Assert.Equal(0, usage.CharsToday);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", cancelled.Token));
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task Timeout_ThrowsTimeoutException()
    {
        _server.Responder = async (_, ct) =>
        {
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var translator = Create(o => o.Timeout = TimeSpan.FromMilliseconds(200));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Zeitüberschreitung bei Claude", ex.Message);
        Assert.Equal("Claude-Fehler: Zeitüberschreitung bei Claude", translator.StatusText);
    }

    [Fact]
    public async Task UnreachableServer_IsANetworkError()
    {
        var baseUrl = _server.BaseUrl;
        await _server.DisposeAsync(); // nothing listens on the port any more
        var translator = new ClaudeTranslator(() => "sk-ant-test-key",
            new ClaudeTranslatorOptions { BaseUrl = baseUrl, MaxRetries = 0 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default).WaitAsync(Timeout));

        Assert.Equal("Keine Verbindung zu Claude", ex.Message);
    }

    [Fact]
    public async Task TestAsync_ReturnsNullOnSuccess_AndTheGermanErrorOtherwise()
    {
        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Translations(new[] { "Hello" }));
        var translator = Create();

        Assert.Null(await translator.TestAsync(default).WaitAsync(Timeout));
        Assert.Equal(new[] { "你好" }, Assert.Single(_server.Requests).Texts);

        _server.Responder = (_, _) => Task.FromResult(FakeAnthropicServer.Error("authentication_error", "invalid x-api-key"));
        Assert.Equal("Claude-API-Schlüssel ist ungültig", await translator.TestAsync(default).WaitAsync(Timeout));
    }

    [Fact]
    public async Task ChangedKey_IsUsedForTheNextRequest()
    {
        var key = "sk-ant-first";
        var translator = Create(key: () => key);

        await translator.TranslateAsync(new[] { "一" }, "zh", "en", default).WaitAsync(Timeout);
        key = "sk-ant-second";
        translator.NotifyKeyChanged();
        await translator.TranslateAsync(new[] { "二" }, "zh", "en", default).WaitAsync(Timeout);

        Assert.Equal(new[] { "sk-ant-first", "sk-ant-second" }, _server.Requests.Select(r => r.Key));
    }
}
