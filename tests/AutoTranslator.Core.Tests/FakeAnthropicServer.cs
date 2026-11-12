using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoTranslator.Core.Tests;

/// <summary>What the SDK sent to POST /v1/messages.</summary>
internal sealed record FakeRequest(string Path, string? ApiKey, string? Authorization, string? AnthropicVersion, JsonElement Body)
{
    /// <summary>The credential, whichever header carried it (x-api-key or Authorization: Bearer).</summary>
    public string? Key => ApiKey ?? (Authorization?.StartsWith("Bearer ", StringComparison.Ordinal) == true ? Authorization[7..] : null);

    /// <summary>The texts of the batch: the user message is a JSON array of strings.</summary>
    public string[] Texts
    {
        get
        {
            var content = Body.GetProperty("messages")[0].GetProperty("content");
            // A plain string, or a list of content blocks with one text block.
            var json = content.ValueKind == JsonValueKind.String
                ? content.GetString()!
                : content[0].GetProperty("text").GetString()!;
            return JsonSerializer.Deserialize<string[]>(json)!;
        }
    }
}

internal sealed record FakeResponse(int Status, string Body);

/// <summary>Local stand-in for the Anthropic Messages API, answering with canned JSON.</summary>
internal sealed class FakeAnthropicServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<FakeRequest> _requests = new();

    private FakeAnthropicServer(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; }

    /// <summary>Decides the answer; the default translates every text to "EN:" + text.</summary>
    public Func<FakeRequest, CancellationToken, Task<FakeResponse>> Responder { get; set; } =
        (request, _) => Task.FromResult(Translations(request.Texts.Select(t => "EN:" + t)));

    public IReadOnlyList<FakeRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public static async Task<FakeAnthropicServer> StartAsync()
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        FakeAnthropicServer? server = null;
        app.Run(context => server!.HandleAsync(context));
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeAnthropicServer(app, new Uri(address));
        return server;
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (context.Request.Method != "POST" || context.Request.Path != "/v1/messages")
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync(Error("not_found_error", "no such endpoint").Body);
            return;
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var body = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement.Clone();
        var request = new FakeRequest(
            context.Request.Path,
            context.Request.Headers["x-api-key"].FirstOrDefault(),
            context.Request.Headers.Authorization.FirstOrDefault(),
            context.Request.Headers["anthropic-version"].FirstOrDefault(),
            body);
        lock (_requests) _requests.Add(request);

        FakeResponse response;
        try
        {
            response = await Responder(request, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            return; // the client hung up
        }

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = "application/json";
        context.Response.Headers["request-id"] = "req_fake";
        await context.Response.WriteAsync(response.Body);
    }

    /// <summary>A successful Messages API response whose text block is <paramref name="text"/>.</summary>
    public static FakeResponse Message(string text, string stopReason = "end_turn") => new(200, JsonSerializer.Serialize(new
    {
        id = "msg_fake_01",
        type = "message",
        role = "assistant",
        model = "claude-haiku-5-5",
        content = new object[] { new { type = "text", text, citations = (object?)null } },
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 120, output_tokens = 40 },
    }));

    /// <summary>A successful response in the structured-output shape {"translations":[...]}.</summary>
    public static FakeResponse Translations(IEnumerable<string> translations) =>
        Message(JsonSerializer.Serialize(new { translations }));

    /// <summary>A refusal: HTTP 200 with stop_reason "refusal" and no usable content.</summary>
    public static FakeResponse Refusal() => new(200, JsonSerializer.Serialize(new
    {
        id = "msg_fake_02",
        type = "message",
        role = "assistant",
        model = "claude-haiku-5-5",
        content = Array.Empty<object>(),
        stop_reason = "refusal",
        stop_details = new { type = "refusal", category = "general_harms", explanation = "declined" },
        stop_sequence = (string?)null,
        usage = new { input_tokens = 120, output_tokens = 1 },
    }));

    public static FakeResponse Error(string type, string message, int? status = null) => new(
        status ?? type switch
        {
            "invalid_request_error" => 400,
            "authentication_error" => 401,
            "billing_error" => 402,
            "permission_error" => 403,
            "not_found_error" => 404,
            "rate_limit_error" => 429,
            "overloaded_error" => 529,
            _ => 500,
        },
        JsonSerializer.Serialize(new { type = "error", error = new { type, message }, request_id = "req_fake" }));

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _app.StopAsync(timeout.Token);
        }
        catch
        {
            // Shutting down anyway.
        }
        await _app.DisposeAsync();
    }
}
