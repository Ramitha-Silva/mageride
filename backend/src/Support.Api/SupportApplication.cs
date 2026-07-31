using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using MageRide.Support.Configuration;
using MageRide.Support.Endpoints;
using Microsoft.Extensions.Options;

namespace MageRide.Support;

/// <summary>
/// Composition root for support-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class SupportApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "support-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            UsePostgres = true,

            // **No Redis.** Nothing here is rate-limited by a shared bucket, nothing is cached across
            // replicas, and the two reads that could be — the FAQ and a ticket thread — are index
            // scans over tiny tables that a person opens by hand. A cache would introduce a second
            // opinion about what content-svc published.
            UseRedis = false,

            // **No Kafka and no outbox.** This service changes no state another service acts on: a
            // resolved ticket is read by the person who raised it, and the one downstream effect a
            // ticket can have — US-14.11's wallet reversal answering a daily-fee refund claim — is
            // an admin decision taken in admin-bff against wallet-svc, not an event. A `ticket.*`
            // topic would be produced here and consumed by nobody, and D6' §2.1 gives this service
            // none. Named in the C053 handoff, with the push notification that would be its first
            // real consumer.
            UseKafka = false,
            UseOutbox = false,

            // R-14. `support.yaml` declares `Idempotency-Key` on every POST, and it matters most on
            // the raise: a double-tapped Submit on the raise-ticket sheet, or a proxy retry, puts a
            // second identical complaint on the queue and no natural key would collide — a user may
            // legitimately raise two tickets about the same trip.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // `rides.command_log`; this service's is `support.command_log` (migration 1309), with no
        // aggregate-id column because the ticket a raise targets does not exist when the key is
        // claimed.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "support";
            commandLog.AggregateIdColumn = null;
        });

        // A screenshot is a phone photograph, and the idempotency middleware hashes the whole
        // request body to detect key reuse. Left at the 1 MiB default it would answer 413 before the
        // upload route could, with a message about buffering rather than about the screenshot.
        builder.Services.Configure<IdempotencyOptions>(idempotency =>
        {
            var limit = builder.Configuration.GetValue(
                $"{SupportOptions.SectionName}:{nameof(SupportOptions.ScreenshotMaxBytes)}",
                8L * 1024 * 1024);

            idempotency.MaxBufferedRequestBytes = (int)Math.Clamp(
                limit + (64 * 1024), idempotency.MaxBufferedRequestBytes, int.MaxValue);
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddSupportServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapSupportEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<SupportOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalSupportEndpoints(settings.InternalApiKey);
        }

        WarnAboutTicketsThatCannotBeAnswered(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which parts of this service cannot answer anybody.
    /// </summary>
    /// <remarks>
    /// The same rule content-svc, notification-svc, wallet-svc and safety-svc are written under, and
    /// it matters here for its own reason: <b>a ticket nobody can resolve looks exactly like one
    /// nobody has got to yet.</b> The sheet submits, the row is written, the user is shown "we have
    /// received your request", and there is no queue behind it.
    /// </remarks>
    private static void WarnAboutTicketsThatCannotBeAnswered(WebApplication app, SupportOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogError(
                "Support:InternalApiKey is not configured, so /v1/internal/support/** is not mapped: NO TICKET CAN BE "
                + "ASSIGNED, ANSWERED OR RESOLVED (US-16.3, US-14.13). Users can still raise them, and every one of "
                + "them stays OPEN for ever.");
        }

        if (string.IsNullOrWhiteSpace(settings.ScreenshotRoot))
        {
            logger.LogWarning(
                "Support:ScreenshotRoot is not configured, so US-16.2 screenshots are written under the system "
                + "temporary directory. A pod restart can lose the evidence while the ticket that references it "
                + "survives; mount a volume, or point this service at D-36's bucket when C125 lands.");
        }

        logger.LogInformation(
            "support-svc is up: FAQ fallback {Fallback}, screenshots up to {MaxBytes} bytes kept {Retention}, "
            + "{FinanceCategories} category routed to the finance queue.",
            string.Join(" → ", Domain.Languages.FallbackOrder),
            settings.ScreenshotMaxBytes,
            settings.ScreenshotRetention,
            string.Join(", ", Domain.TicketQueues.FinanceCategories));
    }
}
