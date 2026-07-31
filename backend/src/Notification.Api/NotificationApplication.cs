using MageRide.Notification.Configuration;
using MageRide.Notification.Endpoints;
using MageRide.Notification.Messaging;
using MageRide.Notification.Sending;
using MageRide.Notification.Sms;
using MageRide.Notification.Templates;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using Microsoft.Extensions.Options;

namespace MageRide.Notification;

/// <summary>
/// Composition root for notification-svc. Lives here rather than in <c>Program.cs</c> so the test
/// suite drives the same pipeline the process runs.
/// </summary>
public static class NotificationApplication
{
    /// <summary>Service name for telemetry, the Postgres application name and the Kafka client id.</summary>
    public const string ServiceName = "notification-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var configured = builder.Configuration.GetSection(NotificationOptions.SectionName).Get<NotificationOptions>()
                         ?? new NotificationOptions();

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // The queue, the device tokens, the recipients and the AL-44 tokens are all rows.
            UsePostgres = true,

            // P-12's buckets are Redis and have to be: the limit is per booker across every replica,
            // and an in-process counter would let N replicas pass N × 5 requests an hour. It also
            // carries content-svc's cache purge, which is what makes an edited template visible here
            // immediately rather than within a TTL.
            UseRedis = true,

            // Four topics in, nothing out — so Kafka is on for the consumers and for nothing else.
            // Tied to the switch rather than always on: this service never publishes, so a
            // deployment with the consumers off needs no broker address at all, and demanding one it
            // would not use is a required setting with no consequence.
            UseKafka = configured.ConsumersEnabled,

            // **No outbox, and that is structural.** This service produces no domain events: it is
            // the end of every fan-out on the platform, not a step in one. The state it owns — "this
            // message was sent" — has no consumer that could act on it, and the one fact another
            // service might want (a delivery receipt) is not something FCM, APNs or Notify.lk give
            // synchronously. D3' calls the send route "accepted asynchronously — delivery receipts
            // are not part of this call".
            UseOutbox = false,

            // R-14. `notification.yaml` declares `Idempotency-Key` on register-token and on the
            // internal send; the ack route is exempt (see NotifyEndpoints for why that one races a
            // three-second deadline).
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // rides.command_log; this service's is `comms.command_log` (migration 1308), with no
        // aggregate-id column because registering a device token targets no aggregate this service
        // owns.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "comms";
            commandLog.AggregateIdColumn = null;
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddNotificationServices(builder.Configuration);

        var settings = configured;

        var sms = builder.Configuration.GetSection(SmsOptions.SectionName).Get<SmsOptions>() ?? new SmsOptions();

        RefuseUnsafeTransports(builder, settings, sms);

        // Registered as singletons and *then* hosted, so one pass can be driven directly — by a test,
        // and by anything later that needs to drain the queue on demand. A hosted service registered
        // by type alone is unreachable from the container.
        builder.Services.AddSingleton<DeliveryWorker>();
        builder.Services.AddSingleton<OfferAckWorker>();
        builder.Services.AddSingleton<RetentionWorker>();

        if (settings.DeliveryEnabled)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<DeliveryWorker>());
        }

        if (settings.OfferSmsFallbackEnabled && settings.OfferAckSweepEnabled)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<OfferAckWorker>());
        }

        if (settings.RetentionSweepEnabled)
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<RetentionWorker>());
        }

        // The purge subscriber shares the singleton ITemplateSource, which is what it drops.
        builder.Services.AddHostedService<TemplateInvalidationSubscriber>();

        if (settings.ConsumersEnabled)
        {
            builder.Services.AddHostedService<DispatchEventConsumer>();
            builder.Services.AddHostedService<RideEventConsumer>();
            builder.Services.AddHostedService<WalletEventConsumer>();
            builder.Services.AddHostedService<RegistryEventConsumer>();
        }

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapNotifyEndpoints();

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalNotifyEndpoints(settings.InternalApiKey);
        }

        WarnAboutMessagesThatCannotBeSent(app, settings, sms);

        return app;
    }

    /// <summary>
    /// The two transports that write secrets into a log file are refused outside Development.
    /// </summary>
    /// <remarks>
    /// The same guard rail iam-svc puts on <c>Sms:Provider=dev</c>, and it matters more here: this
    /// service's SMS bodies carry <b>share tokens</b> — the whole credential for an unauthenticated
    /// tracking page (AL-44) — and its push bodies carry package delivery OTPs. A dev transport in
    /// production is those secrets in plaintext in a log aggregator.
    /// </remarks>
    private static void RefuseUnsafeTransports(
        WebApplicationBuilder builder, NotificationOptions settings, SmsOptions sms)
    {
        if (builder.Environment.IsDevelopment())
        {
            return;
        }

        if (string.Equals(settings.PushProvider, NotificationOptions.LogProvider, StringComparison.OrdinalIgnoreCase)
            && !settings.AllowLogTransportOutsideDevelopment)
        {
            throw new InvalidOperationException(
                $"Notification:PushProvider is '{NotificationOptions.LogProvider}' outside Development. It writes "
                + "push bodies — including package delivery OTPs — to the log instead of sending them. Set "
                + "Notification:PushProvider=live, or Notification:AllowLogTransportOutsideDevelopment=true if this "
                + "is the synthetic-data replica.");
        }

        if (string.Equals(sms.Provider, SmsOptions.DevProvider, StringComparison.OrdinalIgnoreCase)
            && !sms.AllowDevSenderOutsideDevelopment)
        {
            throw new InvalidOperationException(
                $"Sms:Provider is '{SmsOptions.DevProvider}' outside Development. It writes message bodies — "
                + "including AL-44 share tokens, which are credentials — to the log instead of sending them. Set "
                + "Sms:Provider=notifylk, or Sms:AllowDevSenderOutsideDevelopment=true if this is the "
                + "synthetic-data replica.");
        }
    }

    /// <summary>
    /// Says, once and loudly, which messages this deployment cannot send.
    /// </summary>
    /// <remarks>
    /// The same rule fare-svc, wallet-svc, subscription-svc, query-svc, fanout-svc and content-svc
    /// are written under, and it matters most here: <b>every failure below is invisible from the
    /// outside.</b> Rides are offered, packages are delivered, wallets run dry — nothing errors, no
    /// endpoint 500s, and the only symptom is a phone that never buzzes.
    /// </remarks>
    private static void WarnAboutMessagesThatCannotBeSent(
        WebApplication app, NotificationOptions settings, SmsOptions sms)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(settings.ContentBaseUrl))
        {
            logger.LogError(
                "Notification:ContentBaseUrl is not configured, so no template can be resolved. Every notification "
                + "with a body — which is all of them but the two silent data messages — fails to render and is "
                + "never sent (D-26).");
        }

        if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogError(
                "Notification:InternalApiKey is not configured, so /v1/internal/notify/** is not mapped. Nothing "
                + "another service asks this one to send goes out: no SOS (D-33), no payment receipt, no "
                + "announcement. The event-driven notifications still work.");
        }

        if (!settings.ConsumersEnabled)
        {
            logger.LogError(
                "Notification:ConsumersEnabled is off. No ride offer is pushed (E-01), no driver is told they were "
                + "assigned, no wallet runs low out loud. Dispatch still offers rides and drivers never hear about "
                + "them.");
        }

        if (!settings.DeliveryEnabled)
        {
            logger.LogError(
                "Notification:DeliveryEnabled is off. Notifications are still enqueued on comms.notifications and "
                + "nothing sends them; the queue grows and every handset stays silent.");
        }

        if (!settings.OfferSmsFallbackEnabled)
        {
            logger.LogWarning(
                "Notification:OfferSmsFallbackEnabled is off, so E-01's 3-second no-ack fallback does not run. A "
                + "driver whose handset was asleep simply misses the offer.");
        }

        if (!string.Equals(sms.Provider, SmsOptions.DevProvider, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(sms.SecondaryGateway))
        {
            logger.LogWarning(
                "Sms:SecondaryGateway is not configured. D-33 requires an SOS to go through the primary AND a "
                + "secondary gateway in parallel with a p99 of 5 s; with one gateway the SLO has nothing behind it, "
                + "and an SOS is lost whenever Notify.lk is having a bad minute.");
        }

        if (string.IsNullOrWhiteSpace(settings.WebTrackBaseUrl))
        {
            logger.LogError(
                "Notification:WebTrackBaseUrl is not configured, so no AL-44 share link can be built. The "
                + "unregistered package recipient (AL-21), the proxy rider (US-8.22) and the RiderNotRegistered "
                + "pickup confirmation (AL-45) all go unnotified — those three SMS are refused rather than sent "
                + "with a broken URL.");
        }

        if (!settings.LocationRequestLimitsEnabled)
        {
            logger.LogWarning(
                "Notification:LocationRequestLimitsEnabled is off, so P-12's 5/hour and 30/day are not enforced on "
                + "the outbound side. ride-svc still counts them at the issuing end, so this is the second of two "
                + "gates rather than the only one.");
        }

        logger.LogInformation(
            "notification-svc is up: push transport {Push}, SMS primary {Sms}, secondary gateway {Secondary}.",
            settings.PushProvider,
            sms.Provider,
            string.IsNullOrWhiteSpace(sms.SecondaryGateway) ? "none" : "configured");
    }
}
