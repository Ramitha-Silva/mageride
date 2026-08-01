using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.PublicBff.Tests.Infrastructure;

/// <summary>One alert notification-svc was asked to deliver.</summary>
/// <param name="Phones">
/// Who it was aimed at. <b>This is the assertion the web-SOS test exists to make</b> — D6' I-29.4
/// says the recipient is the booker's registered mobile, and nothing on the public surface can be
/// read to check that, because public-bff never learns the number.
/// </param>
internal sealed record SentAlert(string Type, IReadOnlyList<string> Phones, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// notification-svc's <c>/v1/internal/notify/send</c>, on a real socket.
/// </summary>
/// <remarks>
/// <b>The one component this suite stubs rather than boots, and the reason is scope.</b> D-33's
/// dual-gateway parallel send and its p99 are C051's and C052's deliverables and are asserted by
/// <c>Safety.Api.Tests</c> against two real gateways. What C066 has to prove is a different claim:
/// that a web SOS is aimed at the <em>booker</em> rather than at an emergency contact nobody has,
/// and that public-bff never sees the number. A recording socket answers that exactly.
/// </remarks>
internal sealed class NotificationStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<SentAlert> _sent = new();

    private NotificationStub(WebApplication app)
    {
        _app = app;

        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseAddress { get; }

    public IReadOnlyList<SentAlert> Sent => [.. _sent];

    /// <summary>When set, every gateway refuses — the "recorded but nobody was told" case.</summary>
    public bool Refuse { get; set; }

    public static async Task<NotificationStub> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        NotificationStub? stub = null;

        app.MapPost("/v1/internal/notify/send", async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var root = document.RootElement;

            var phones = root.TryGetProperty("phones", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(static value => value.GetString() ?? string.Empty).ToArray()
                : [];

            var data = root.TryGetProperty("data", out var values) && values.ValueKind == JsonValueKind.Object
                ? values.EnumerateObject().ToDictionary(
                    static property => property.Name,
                    static property => property.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            stub!._sent.Enqueue(new SentAlert(
                root.TryGetProperty("notificationType", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                phones,
                data));

            // The inline-delivery block safety-svc's `NotificationClient.Parse` reads. `Sent` is the
            // only value it counts as dispatched; anything else is recorded as undispatched, which
            // is what `Refuse` exercises.
            return Results.Ok(new
            {
                deliveries = new[]
                {
                    new
                    {
                        status = stub.Refuse ? "Failed" : "Sent",
                        provider = stub.Refuse ? null : "notifylk",
                        gateways = new[] { "notifylk", "dialog" },
                        error = stub.Refuse ? "stub refused" : null,
                    },
                },
            });
        });

        await app.StartAsync();

        stub = new NotificationStub(app);

        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }
}
