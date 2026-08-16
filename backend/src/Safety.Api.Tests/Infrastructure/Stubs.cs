using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.Safety.Tests.Infrastructure;

/// <summary>One SMS a gateway was asked to send, and when it was asked.</summary>
internal sealed record SentSms(string Gateway, string To, string Message, DateTimeOffset At);

/// <summary>
/// An SMS gateway on a real socket.
/// </summary>
/// <remarks>
/// <b>Two of these run at once, and that is the point of D-33.</b> "Both gateways in parallel" is a
/// claim about two requests being in flight simultaneously; a fake gateway inside notification-svc
/// would prove only that the code calls what it calls. <see cref="Delay"/> is what makes the race
/// observable — a slow primary and an instant secondary is exactly the case the parallel send exists
/// for, and a sequential fallback would show up as latency.
/// </remarks>
internal sealed class SmsGatewayStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SentSms> _sent;

    private SmsGatewayStub(WebApplication app, ConcurrentQueue<SentSms> sent, string name)
    {
        _app = app;
        _sent = sent;
        Name = name;

        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string Name { get; }

    public string BaseAddress { get; }

    public IReadOnlyList<SentSms> Sent => [.. _sent];

    /// <summary>Artificial latency before answering. Models a gateway having a bad minute.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>When set, the gateway refuses everything.</summary>
    public bool Refuse { get; set; }

    /// <summary>Fit SMS v4: JSON POST to <c>sms/send</c>, 200 with a status member.</summary>
    public static Task<SmsGatewayStub> StartPrimaryAsync() => StartAsync("primary", fitSms: true);

    /// <summary>The generic JSON POST the secondary gateway takes (D6' §7.3 prints no shape).</summary>
    public static Task<SmsGatewayStub> StartSecondaryAsync() => StartAsync("secondary", fitSms: false);

    private static async Task<SmsGatewayStub> StartAsync(string name, bool fitSms)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var sent = new ConcurrentQueue<SentSms>();
        SmsGatewayStub? stub = null;

        if (fitSms)
        {
            app.MapPost("/api/v4/sms/send", async (HttpContext context) =>
            {
                if (stub!.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(stub.Delay);
                }

                var body = await context.Request.ReadFromJsonAsync<FitSmsBody>();

                if (stub.Refuse)
                {
                    // Their real failure shape: HTTP 200 with an error body.
                    return Results.Ok(new { status = "error", message = "insufficient balance" });
                }

                sent.Enqueue(new SentSms(
                    name, body?.Recipient ?? string.Empty, body?.Message ?? string.Empty, DateTimeOffset.UtcNow));

                return Results.Ok(new
                {
                    status = "success",
                    data = new { ruid = $"stub-{sent.Count:D32}", to = body?.Recipient },
                });
            });
        }
        else
        {
            app.MapPost("/", async (SecondaryBody body) =>
            {
                if (stub!.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(stub.Delay);
                }

                if (stub.Refuse)
                {
                    return Results.StatusCode(502);
                }

                sent.Enqueue(new SentSms(name, body.To ?? string.Empty, body.Message ?? string.Empty, DateTimeOffset.UtcNow));

                return Results.Ok(new { accepted = true });
            });
        }

        await app.StartAsync();

        stub = new SmsGatewayStub(app, sent, name);
        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the {Name} SMS stub: {exception.Message}");
        }
    }

    /// <summary>Fit SMS's send body, as their field names spell it.</summary>
    private sealed record FitSmsBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("recipient")] string Recipient,
        [property: System.Text.Json.Serialization.JsonPropertyName("sender_id")] string SenderId,
        [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type,
        [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message,
        [property: System.Text.Json.Serialization.JsonPropertyName("expiry_time")] int ExpirySeconds);

    private sealed record SecondaryBody(string? To, string? From, string? Message);
}

/// <summary>
/// content-svc, as far as notification-svc needs it here: the one template an SOS renders.
/// </summary>
/// <remarks>
/// A stub rather than a real content-svc because the D-26 render path is C045's and C051's
/// definition of done, already proved in both of those suites. What this suite needs from it is
/// only that the SMS carries the raiser's name and the tracking link, so the assertions about D-33
/// are about the transport rather than the wording.
/// </remarks>
internal sealed class ContentStub : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ContentStub(WebApplication app)
    {
        _app = app;

        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseAddress { get; }

    public static async Task<ContentStub> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapGet("/v1/content/templates/{key}", (string key, string? lang) =>
        {
            var language = lang is "si" or "ta" ? lang : "en";

            // Migration 1904's `sos_alert`, in the three languages its trilingual rule requires.
            var body = key switch
            {
                "sos_alert" => language switch
                {
                    "si" => "MageRide හදිසි අවස්ථාව: {{name}}. ස්ථානය: {{link}}",
                    "ta" => "MageRide அவசரநிலை: {{name}}. இருப்பிடம்: {{link}}",
                    _ => "MageRide emergency: {{name}} has raised an SOS. Live location: {{link}}",
                },
                _ => null,
            };

            return body is null
                ? Results.NotFound()
                : Results.Ok(new { key, language, version = 1, title = (string?)null, body, placeholders = new[] { "name", "link" } });
        });

        await app.StartAsync();

        return new ContentStub(app);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the content stub: {exception.Message}");
        }
    }
}
