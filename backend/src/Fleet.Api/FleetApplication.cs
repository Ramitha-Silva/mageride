using MageRide.Fleet.Configuration;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Persistence;
using MageRide.Shared;
using MageRide.Shared.Http.Idempotency;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet;

/// <summary>
/// Composition root for fleet-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class FleetApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "fleet-svc";

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

            // **No Redis.** Nothing here is rate-limited by a shared bucket and nothing is cached
            // across replicas. The reads are an organisation row, a membership row and a payout
            // version — index lookups on tables with one row per organisation, opened by a person
            // on a web portal. A cache would introduce a second opinion about whether an org is
            // approved, which is the one fact this service exists to be authoritative about.
            UseRedis = false,

            // **No Kafka and no outbox**, and this one is worth being precise about, because
            // CLAUDE.md's rule is "outbox for all cross-service events".
            //
            // *There are no cross-service events here.* Every consequence of what this service
            // writes is a **read** somebody else already does against the same tables:
            // subscription-svc reads the verified payout profile for the pay sheet (C050,
            // `ReadVerifiedPayoutProfileAsync`), iam-svc reads `iam.fleet_members` to mint the
            // `fleet_role` claim (C027), provisioning-svc and fleet-health-svc read the roster.
            // None of them is waiting to be told.
            //
            // *And there is nobody to tell.* D6' §2.1 names no fleet-svc topic; `fleet.events`
            // belongs to fleet-health-svc's alerts (C044). The one event that would earn a topic is
            // "your organisation has been approved / rejected" — and there is no fleet-org
            // notification template anywhere in the seed (migration 1904) and no consumer for it.
            // Producing to a topic nobody reads is the failure mode D6' §2.1's registry exists to
            // prevent. Named in the C058 handoff, with the notification that would be its first
            // real consumer.
            UseKafka = false,
            UseOutbox = false,

            // R-14. `fleet.yaml` declares `Idempotency-Key` on every POST, and it matters most on
            // registration: a double-submitted `POST /v1/fleets` with no replay puts a second
            // application on the Verification Officer's queue, where migration 0313's business-
            // registration index then refuses it with a 409 the operator did not earn.
            UseCommandLog = true,

            UseAuthentication = true,
        };

        // Ahead of AddMageRideDefaults so an operator's setting still wins. The kernel defaults to
        // `rides.command_log`; this service's is `registry.fleet_command_log` (migration 0313) —
        // separate from registry-svc's `registry.command_log`, because the two share a schema but
        // not a key space. No aggregate-id column: the organisation a command targets is either in
        // the request path, which the request hash covers, or does not exist yet.
        builder.Services.Configure<CommandLogOptions>(commandLog =>
        {
            commandLog.Schema = "registry";
            commandLog.Table = "fleet_command_log";
            commandLog.AggregateIdColumn = null;
        });

        // A payout document is a photograph of a bank statement, and the idempotency middleware
        // hashes the whole request body to detect key reuse. Left at the 1 MiB default it would
        // answer 413 before the upload route could, with a message about buffering rather than
        // about the document.
        builder.Services.Configure<IdempotencyOptions>(idempotency =>
        {
            var limit = builder.Configuration.GetValue(
                $"{FleetOptions.SectionName}:{nameof(FleetOptions.DocumentMaxBytes)}",
                8L * 1024 * 1024);

            idempotency.MaxBufferedRequestBytes = (int)Math.Clamp(
                limit + (64 * 1024), idempotency.MaxBufferedRequestBytes, int.MaxValue);
        });

        builder.AddMageRideDefaults(serviceOptions);
        builder.Services.AddFleetServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        app.MapFleetEndpoints();
        app.MapFleetOpsEndpoints();

        var settings = app.Services.GetRequiredService<IOptions<FleetOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalFleetEndpoints(settings.InternalApiKey);
        }

        AnnounceWhatIsSwitchedOff(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, which of this service's guarantees are not in force.
    /// </summary>
    /// <remarks>
    /// The rule content-svc, notification-svc, wallet-svc, safety-svc and support-svc are written
    /// under, and it matters here for its own reason: <b>every one of these switches fails
    /// silently and looks like normal operation.</b> An organisation nobody can approve looks
    /// exactly like one nobody has got to yet; an ungated one looks like an approved one; an
    /// unscoped read returns rows, just too many of them.
    /// </remarks>
    private static void AnnounceWhatIsSwitchedOff(WebApplication app, FleetOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogError(
                "Fleet:InternalApiKey is not configured, so /v1/internal/fleets/** is not mapped: NO FLEET "
                + "ORGANISATION CAN BE APPROVED OR REJECTED (US-13.A7, AL-39). Operators can register and submit "
                + "their KYC and payout profile, and every one of them stays PENDING for ever — which means no "
                + "vehicle onboarding, no driver assignment, and no Paid classification anywhere on the platform.");
        }

        if (!settings.VerificationGate)
        {
            logger.LogError(
                "Fleet:VerificationGate is false: AN UNAPPROVED ORGANISATION CAN ONBOARD VEHICLES AND ASSIGN "
                + "DRIVERS (US-13.A7). This is a development convenience and must not be set in any environment "
                + "carrying real operators.");
        }

        // The RLS warning belongs to the reader, which is the thing that would stop scoping.
        app.Services.GetRequiredService<FleetScopedReader>().WarnIfUnscoped();

        if (string.IsNullOrWhiteSpace(settings.DocumentRoot))
        {
            logger.LogWarning(
                "Fleet:DocumentRoot is not configured, so AL-49 payout documents and AL-50 vehicle documents are "
                + "written under the system temporary directory. A pod restart can lose a bank statement or a route "
                + "permit while the row that references it survives; mount a volume, or point this service at D-36's "
                + "bucket when C125 lands.");
        }

        // ------------------------------------------------------------------------------------
        // C059's four hops. Each is off by omission rather than by a flag, and each failure is
        // silent from the outside — an operator sees a screen that works and is wrong.
        // ------------------------------------------------------------------------------------

        if (string.IsNullOrWhiteSpace(settings.OcrBaseUrl))
        {
            logger.LogError(
                "Fleet:OcrBaseUrl is not configured, so NO VEHICLE DOCUMENT IS EVER READ (AL-50). Uploads are stored "
                + "and every SCR-FP-004 chip stays `pending`, which holds every vehicle in every fleet out of "
                + "APPROVED until a Verification Officer confirms each field by hand.");
        }

        if (string.IsNullOrWhiteSpace(settings.ProvisioningBaseUrl))
        {
            logger.LogError(
                "Fleet:ProvisioningBaseUrl is not configured, so POST /v1/fleets/{{id}}/trackers/bind is NOT MAPPED "
                + "(US-13.12). An operator gets a 404 rather than a binding — which is the safe direction, because "
                + "the alternative is believing an ST-901 is armed on a bus nothing is tracking.");
        }

        if (string.IsNullOrWhiteSpace(settings.SubscriptionBaseUrl))
        {
            logger.LogError(
                "Fleet:SubscriptionBaseUrl is not configured, so the Mode B request, subscriber and payment proxies "
                + "are NOT MAPPED (SCR-FP-011/012). An operator has no view of who is paying for their vehicles' "
                + "seats and cannot accept a subscription request from the Fleet Portal at all.");
        }

        if (!settings.ScheduleAlarmsEnabled || string.IsNullOrWhiteSpace(settings.NotificationBaseUrl))
        {
            logger.LogError(
                "The US-13.11 not-started alarm cannot reach a driver: ScheduleAlarmsEnabled is {Enabled} and "
                + "Fleet:NotificationBaseUrl is {Notification}. A booked departure nobody makes is recorded MISSED "
                + "(or, with the sweep off, is not even that) and NO DRIVER APP RINGS.",
                settings.ScheduleAlarmsEnabled ? "on" : "OFF",
                string.IsNullOrWhiteSpace(settings.NotificationBaseUrl) ? "not configured" : "configured");
        }

        logger.LogInformation(
            "fleet-svc is up: row-level security {Rls}, verification gate {Gate}, documents up to {MaxBytes} bytes "
            + "kept {Retention}, at most {MaxMembers} members and {BulkMaxRows} bulk rows per organisation; the map "
            + "shows positions newer than {MapStaleAfter} and analytics spans at most {MaxAnalyticsDays} days.",
            settings.RlsEnabled ? "on" : "OFF",
            settings.VerificationGate ? "on" : "OFF",
            settings.DocumentMaxBytes,
            settings.DocumentRetention,
            settings.MaxMembersPerFleet,
            settings.BulkMaxRows,
            settings.MapStaleAfter,
            settings.MaxAnalyticsDays);
    }
}
