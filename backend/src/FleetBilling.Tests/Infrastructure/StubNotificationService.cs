using System.Collections.Concurrent;
using System.Text.Json;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.FleetBilling.Tests.Infrastructure;

/// <summary>One call this service made to notification-svc's internal plane.</summary>
internal sealed record CapturedNotification(
    string? NotificationType,
    IReadOnlyList<string> Recipients,
    IReadOnlyDictionary<string, string> Data,
    string? InternalKey,
    string? IdempotencyKey);

/// <summary>
/// A stand-in for notification-svc's <c>POST /v1/internal/notify/send</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stub here and a real service for the ledger, and the difference is deliberate.</b> The
/// settlement guarantee lives half in wallet-svc's schema, so a stub would assert this suite's own
/// arithmetic — but a dunning notice is a message this component <em>composes</em>, and what has to
/// be true of it is what is in the envelope: the D5' §14.4 type, the organisation's Owners as
/// recipients, no rendered sentence (D-26), and an idempotency key that separates one reminder
/// round from the next. Booting notification-svc would test C051's delivery pipeline again and make
/// this suite fail for FCM's reasons.
/// </para>
/// <para>
/// It answers <c>202</c>, which is the status the real route answers, so the caller's
/// success/failure branch is exercised as written.
/// </para>
/// </remarks>
internal sealed class StubNotificationService : IAsyncDisposable
{
    private readonly ConcurrentQueue<CapturedNotification> _captured = new();
    private WebApplication? _app;

    private StubNotificationService()
    {
    }

    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>Everything this stub was asked to send, in order.</summary>
    public IReadOnlyList<CapturedNotification> Sent => [.. _captured];

    /// <summary>Set to a failure status to exercise the best-effort branch.</summary>
    public int ResponseStatus { get; set; } = StatusCodes.Status202Accepted;

    public static async Task<StubNotificationService> StartAsync()
    {
        var stub = new StubNotificationService();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        app.MapPost("/v1/internal/notify/send", async (HttpContext context) =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var root = document.RootElement;

            var recipients = root.TryGetProperty("recipients", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
                : [];

            var data = root.TryGetProperty("data", out var bag) && bag.ValueKind == JsonValueKind.Object
                ? bag.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => property.Value.GetString() ?? string.Empty,
                    StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            stub._captured.Enqueue(new CapturedNotification(
                root.TryGetProperty("notificationType", out var type) ? type.GetString() : null,
                recipients,
                data,
                context.Request.Headers["X-MageRide-Internal-Key"].ToString(),
                context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString()));

            context.Response.StatusCode = stub.ResponseStatus;
        });

        await app.StartAsync();

        stub._app = app;
        stub.BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return stub;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(5));
            await _app.DisposeAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"warning: could not stop the notification stub: {exception.Message}");
        }
    }
}
