using System.Collections.Concurrent;
using MageRide.Shared.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace MageRide.AdminBff.Tests.Infrastructure;

/// <summary>One request admin-bff forwarded, as the callee saw it.</summary>
internal sealed record ForwardedCall(
    string Method, string Path, string? InternalKey, string? Authorization, string? IdempotencyKey, string Body);

/// <summary>
/// A real HTTP server standing in for safety-svc, support-svc, content-svc and transit-svc.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real socket rather than a stubbed <c>HttpMessageHandler</c>.</b> The forwarding claims worth
/// asserting are about the wire — which credential admin-bff sends to which kind of callee, whether
/// the operator's <c>Idempotency-Key</c> reaches the service that owns the command log, and whether
/// an upstream's 404 arrives at the operator as a 404 rather than a 502. A handler substituted
/// inside the process tests the mapping code and not the decision.
/// </para>
/// <para>
/// It records every call, so a test can assert on what was sent as well as on what came back, and
/// it can be told to fail the next call with a given status — which is how the error-translation
/// path is exercised without arranging a real upstream failure.
/// </para>
/// </remarks>
internal sealed class StubUpstream : IAsyncDisposable
{
    /// <summary>The shared secret the two <c>/v1/internal/**</c> planes expect (C008).</summary>
    public const string InternalKey = "stub-internal-key";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ForwardedCall> _calls = new();

    private (string Path, int Status, string Body)? _failure;

    private StubUpstream(WebApplication app)
    {
        _app = app;

        BaseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    public string BaseUrl { get; }

    public IReadOnlyList<ForwardedCall> Calls => [.. _calls];

    /// <summary>The most recent call whose path contains <paramref name="fragment"/>.</summary>
    public ForwardedCall Last(string fragment) =>
        _calls.LastOrDefault(call => call.Path.Contains(fragment, StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"No forwarded call matched '{fragment}'. Saw: {string.Join(", ", _calls.Select(c => c.Path))}");

    /// <summary>Makes the next call whose path contains <paramref name="pathFragment"/> fail.</summary>
    public void FailNext(string pathFragment, int status, string detail) =>
        _failure = (pathFragment, status,
            $$"""{"type":"https://mageride.lk/errors/not-found","title":"x","status":{{status}},"detail":"{{detail}}"}""");

    public static async Task<StubUpstream> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = "http://127.0.0.1:0",
        });

        var app = builder.Build();
        var stub = new StubUpstreamState();

        app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            var body = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;

            stub.Record(new ForwardedCall(
                context.Request.Method,
                context.Request.Path + context.Request.QueryString,
                context.Request.Headers["X-MageRide-Internal-Key"].ToString() is { Length: > 0 } key ? key : null,
                context.Request.Headers.Authorization.ToString() is { Length: > 0 } auth ? auth : null,
                context.Request.Headers[MageRideHeaders.IdempotencyKey].ToString() is { Length: > 0 } idem
                    ? idem
                    : null,
                body));

            if (stub.TakeFailure(context.Request.Path) is { } failure)
            {
                context.Response.StatusCode = failure.Status;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsync(failure.Body);
                return;
            }

            await next(context);
        });

        // safety-svc (C052) — the vehicle-report queue and the confirm/dismiss decision.
        app.MapGet("/v1/internal/safety/reports/queue", () => Results.Json(new
        {
            items = new[]
            {
                new
                {
                    reportId = SeedIds.Report,
                    vehicleId = SeedIds.ReportedVehicle,
                    reason = "Reckless driving",
                    tripId = (Guid?)null,
                    status = "PENDING",
                    createdAt = DateTimeOffset.UnixEpoch,
                },
            },
            cursor = (string?)null,
        }, MageRideJson.Options));

        app.MapPost("/v1/internal/safety/reports/{reportId:guid}/resolve", (Guid reportId) => Results.Json(new
        {
            reportId,
            status = "CONFIRMED",
            confirmedTotal = 3,
            delisted = true,
        }, MageRideJson.Options));

        // support-svc (C053) — the agent ticket queue.
        app.MapGet("/v1/internal/support/tickets", () => Results.Json(new
        {
            items = new[]
            {
                new
                {
                    ticketId = SeedIds.Ticket,
                    userId = SeedIds.TicketUser,
                    category = "payment",
                    status = "OPEN",
                    description = "Charged twice",
                    createdAt = DateTimeOffset.UnixEpoch,
                    resolvedAt = (DateTimeOffset?)null,
                },
            },
            cursor = (string?)null,
            hasMore = false,
        }, MageRideJson.Options));

        app.MapPost("/v1/internal/support/tickets/{ticketId:guid}/resolve", (Guid ticketId) => Results.Json(new
        {
            ticketId,
            userId = SeedIds.TicketUser,
            category = "payment",
            status = "RESOLVED",
            description = "Charged twice",
            response = "Refunded.",
            createdAt = DateTimeOffset.UnixEpoch,
            resolvedAt = DateTimeOffset.UnixEpoch.AddHours(1),
        }, MageRideJson.Options));

        // content-svc (C054) — content.broadcasts, which it owns.
        // A fresh id per call: two tests publishing an announcement must not end up asserting on
        // one another's audit row.
        app.MapPost("/v1/admin/content/broadcasts", () => Results.Json(new
        {
            broadcastId = Guid.CreateVersion7(),
        }, MageRideJson.Options, statusCode: StatusCodes.Status201Created));

        // transit-svc (C057) — the GTFS Dataset Manager the Configuration group proxies.
        app.Map("/v1/admin/transit/gtfs/{**path}", () => Results.Json(new
        {
            versions = Array.Empty<object>(),
        }, MageRideJson.Options));

        await app.StartAsync();

        var harness = new StubUpstream(app);
        stub.Attach(harness);

        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The recording state, held apart so the middleware closure does not need the harness that
    /// does not exist until the server has an address.
    /// </summary>
    private sealed class StubUpstreamState
    {
        private StubUpstream? _harness;

        public void Attach(StubUpstream harness) => _harness = harness;

        public void Record(ForwardedCall call) => _harness?._calls.Enqueue(call);

        public (int Status, string Body)? TakeFailure(PathString path)
        {
            if (_harness?._failure is not { } failure ||
                !path.Value!.Contains(failure.Path, StringComparison.Ordinal))
            {
                return null;
            }

            _harness._failure = null;
            return (failure.Status, failure.Body);
        }
    }
}

/// <summary>
/// Ids the stub answers with, so a test can assert on the row a forward produced.
/// </summary>
/// <remarks>
/// Only the two <em>queue</em> rows are fixed — those are read, not written, so sharing them across
/// tests costs nothing. Anything a test then audits (a resolved report, a published broadcast) gets
/// a fresh id per call, because <c>audit.events</c> is append-only and shared: two tests keyed on
/// one entity would see each other's rows.
/// </remarks>
internal static class SeedIds
{
    public static readonly Guid Report = Guid.Parse("01930000-0000-7000-8000-000000000001");
    public static readonly Guid ReportedVehicle = Guid.Parse("01930000-0000-7000-8000-000000000002");
    public static readonly Guid Ticket = Guid.Parse("01930000-0000-7000-8000-000000000003");
    public static readonly Guid TicketUser = Guid.Parse("01930000-0000-7000-8000-000000000004");
}
