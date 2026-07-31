using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.Fare.Tests.Infrastructure;

/// <summary>One call fare-svc made to somebody else.</summary>
internal sealed record RecordedCall(string Path, JsonElement Body)
{
    public string? String(string property) =>
        Body.TryGetProperty(property, out var value) ? value.GetString() : null;

    public long? Number(string property) =>
        Body.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;
}

/// <summary>
/// ride-svc and wallet-svc, as far as fare-svc can tell — a real socket that records what it was
/// asked to do.
/// </summary>
/// <remarks>
/// <para>
/// <b>The assertions this suite needs are about calls that did <em>not</em> happen.</b> "A driver-QR
/// confirm posts the driver earning and closes the ride payment with <em>no money movement through
/// MageRide</em>" is a claim about the ledger seam staying silent, and a suite with no wallet at all
/// would satisfy it trivially. A stub that is present, reachable and unused is the difference
/// between proving the fence and forgetting to test it.
/// </para>
/// <para>
/// Booting the real wallet-svc (as <c>Subscription.Api.Tests</c> does) would be stronger for the
/// refund's balanced-ledger claim, and C050's handoff records that as the next step. What it cannot
/// do is prove a negative any better than this: absence of a request is absence of a request.
/// </para>
/// </remarks>
internal sealed class DownstreamStub : IAsyncDisposable
{
    /// <summary>The interim shared secret this stub stands in for on both planes.</summary>
    public const string InternalApiKey = "c050-downstream-internal-key-not-a-secret";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedCall> _calls;

    private DownstreamStub(WebApplication app, ConcurrentQueue<RecordedCall> calls)
    {
        _app = app;
        _calls = calls;
        BaseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseAddress { get; }

    /// <summary>Everything fare-svc asked for, in order.</summary>
    public IReadOnlyList<RecordedCall> Calls => [.. _calls];

    /// <summary>The R-05 settlements reported to ride-svc.</summary>
    public IReadOnlyList<RecordedCall> Settlements =>
        [.. _calls.Where(c => c.Path.Contains("/payment-settled", StringComparison.Ordinal))];

    /// <summary>Every ledger movement asked of wallet-svc — the fence's subject.</summary>
    public IReadOnlyList<RecordedCall> LedgerPostings =>
        [.. _calls.Where(c => c.Path.Contains("/v1/internal/wallet/", StringComparison.Ordinal))];

    public static async Task<DownstreamStub> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        // The routes close over this queue and the instance exposes the same one — there is exactly
        // one, so a call recorded by a handler is a call a test can see.
        var calls = new ConcurrentQueue<RecordedCall>();

        app.MapPost("/v1/internal/rides/{rideId}/payment-settled", async (string rideId, HttpContext context) =>
        {
            await RecordAsync(calls, context);
            return Results.Ok(new { rideId, state = "Paid", version = 1 });
        });

        app.MapPost("/v1/internal/wallet/{driverId}/debit", async (HttpContext context) =>
        {
            await RecordAsync(calls, context);
            return Results.Ok(new { entryId = Guid.NewGuid(), replayed = false });
        });

        app.MapPost("/v1/internal/wallet/{driverId}/credit", async (HttpContext context) =>
        {
            await RecordAsync(calls, context);
            return Results.Ok(new { entryId = Guid.NewGuid(), replayed = false });
        });

        await app.StartAsync();

        return new DownstreamStub(app, calls);
    }

    private static async Task RecordAsync(ConcurrentQueue<RecordedCall> calls, HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);

        calls.Enqueue(new RecordedCall(context.Request.Path.Value ?? string.Empty, document.RootElement.Clone()));
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
            Console.Error.WriteLine($"warning: could not stop the downstream stub: {exception.Message}");
        }
    }
}
