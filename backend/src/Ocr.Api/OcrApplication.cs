using MageRide.Ocr.Configuration;
using MageRide.Ocr.Endpoints;
using MageRide.Ocr.Gemini;
using MageRide.Ocr.Ocr;
using MageRide.Ocr.Redaction;
using MageRide.Shared;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr;

/// <summary>
/// Composition root for ocr-svc. Lives here rather than in <c>Program.cs</c> so the test suite
/// drives the same pipeline the process runs.
/// </summary>
public static class OcrApplication
{
    /// <summary>Service name for telemetry and the Postgres application name.</summary>
    public const string ServiceName = "ocr-svc";

    /// <summary>Builds the service. <paramref name="configure"/> runs before the defaults are registered.</summary>
    public static WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = WebApplication.CreateBuilder(options);

        configure?.Invoke(builder);

        var serviceOptions = new MageRideServiceOptions
        {
            ServiceName = ServiceName,

            // `docs.uploads` and `docs.extractions` (D-36, migration 1301/1310).
            UsePostgres = true,

            // **No Redis.** Nothing here is rate-limited by a shared bucket and nothing is cached
            // across replicas — an extraction is a one-shot pass over bytes that are read once. The
            // one piece of shared state that might have looked like a cache, the perimeter ledger,
            // is deliberately in process: it exists to catch a defect in *this* process's outbound
            // path, and a hash admitted by a replica that is not the one sending would defeat it.
            UseRedis = false,

            // **No Kafka and no outbox.** This service changes no state another service acts on. The
            // verdict it produces is returned on the response its caller is waiting for, and
            // registry-svc is what turns it into `registry.document_fields`, an onboarding step and
            // — through its own outbox — `document.review_required`. A `docs.*` topic would be
            // produced here and consumed by nobody, and D6' §2.1 gives this service none.
            UseKafka = false,
            UseOutbox = false,

            // **No command log.** There is no mutation to replay: the internal route is idempotent
            // in the only way that matters — the same upload extracted twice yields the same fields
            // — and its side effect is an append-only `docs.extractions` row, which D6' §7.5 wants
            // one of "per doc [extraction pass]". Deduplicating on a key would hide a re-upload,
            // which is exactly the row a Verification Officer needs to see beside the failed one.
            UseCommandLog = false,

            // **No bearer.** This service has no user-facing surface at all; its only route is on
            // the internal plane, gated by the shared key and refused at the edge by the gateway.
            // Registering the JWT handler would demand `Jwt:*` in a deployment where no token is
            // ever presented.
            UseAuthentication = false,
        };

        builder.AddMageRideDefaults(serviceOptions);

        // The kernel's authorization is deny-by-default: `AddMageRideAuthorization` sets a fallback
        // policy of RequireAuthenticatedUser, which applies to endpoints that say nothing AND to
        // requests that match no endpoint at all. In a service with no authentication scheme
        // registered — this one, because there is no token on its plane to present — that policy can
        // never be satisfied, and every unmatched path answers **500** ("Unable to find the required
        // 'IAuthenticationService'") instead of 404. Removing the fallback is what makes an unknown
        // path a 404 again.
        //
        // Deny-by-default is not lost, it moves: this service maps exactly one route group and it is
        // gated by `Ocr:InternalApiKey`, and `Every_route_on_this_service_is_health_or_key_gated`
        // fails the suite if anything else is ever mapped here.
        builder.Services.Configure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(
            authorization => authorization.FallbackPolicy = null);

        builder.Services.AddOcrServices(builder.Configuration);

        var app = builder.Build();

        app.UseMageRideDefaults(serviceOptions);

        var settings = app.Services.GetRequiredService<IOptions<OcrOptions>>().Value;

        if (!string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            app.MapInternalOcrEndpoints(settings.InternalApiKey);
        }

        Announce(app, settings);

        return app;
    }

    /// <summary>
    /// Says, once and loudly, exactly which of this service's paths can run.
    /// </summary>
    /// <remarks>
    /// The same rule content-svc, notification-svc and support-svc are written under, and it matters
    /// here for its own reason: <b>a disarmed redactor looks exactly like a well-behaved one from
    /// the outside.</b> Documents keep going in, fields keep coming out, drivers keep onboarding.
    /// Before MCS-07 what that hid was an unreachable AL-27 auto-approve; since MCS-07 it hides
    /// unmasked faces and identity numbers going to a third party instead. Nothing else on the
    /// platform can tell the difference either way, which is why this runs at boot and not on the
    /// first driver's upload.
    /// </remarks>
    private static void Announce(WebApplication app, OcrOptions settings)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(ServiceName);

        if (string.IsNullOrWhiteSpace(settings.InternalApiKey))
        {
            logger.LogError(
                "Ocr:InternalApiKey is not configured, so /v1/internal/ocr/** is not mapped: NO DOCUMENT CAN BE "
                + "EXTRACTED AT ALL. registry-svc will save every onboarding step pending_review and no Mode-C "
                + "vehicle will ever auto-approve (AL-27).");
        }

        var engine = app.Services.GetRequiredService<IOcrEngine>();
        var redaction = app.Services.GetRequiredService<IRedactionPipeline>();
        var gemini = app.Services.GetRequiredService<GeminiFieldExtractor>();

        // Reading these three at start-up is what forces the probes to run now rather than on the
        // first driver's upload, so a misconfigured pod is loud at boot instead of at 8 a.m.
        var tesseract = engine.IsAvailable;
        var armed = redaction.IsArmed;

        if (!tesseract)
        {
            logger.LogError(
                "The on-prem OCR engine is unavailable. It is BOTH D6' §7.5's fallback for a model outage AND "
                + "the source of ADD §12.5's redaction boxes, so a Gemini outage now extracts NOTHING and every "
                + "document that does reach Gemini goes unredacted (Δ MCS-07).");
        }

        // Δ MCS-07: this pairing is the one an operator has to read together, because the two
        // switches now compose into three postures rather than two, and the middle one is the
        // dangerous one. Announced as ERROR because a document leaving unmasked is the loudest
        // fact this service has; it is not a crash, and it is not meant to be.
        if (!armed && gemini.IsConfigured)
        {
            logger.LogError(
                "The D-36 redaction pre-pass is DISARMED ({Reason}) and Gemini IS configured, so every document "
                + "is sent to the external model UNREDACTED: human faces are not blurred and NIC / licence "
                + "numbers are not masked. This is no longer the fail-closed direction — MCS-07 made the "
                + "pre-pass best-effort — so it will keep working, and keep doing that, until the dependency is "
                + "installed or Ocr:Gemini:Enabled is turned off. docs.extractions.redaction_applied records "
                + "each one; ix_extractions_unredacted indexes them.",
                redaction.DisarmedReason);
        }
        else if (!armed)
        {
            logger.LogWarning(
                "The D-36 redaction pre-pass is DISARMED ({Reason}), but Gemini is not configured either, so "
                + "nothing leaves this service. Extraction is on-prem only, capped at {Ceiling} and therefore "
                + "always reviewed; AL-27's auto-approve is unreachable.",
                redaction.DisarmedReason, settings.TesseractConfidenceCeiling);
        }
        else if (!gemini.IsConfigured)
        {
            logger.LogWarning(
                "The redaction pre-pass is armed but Gemini is not configured (Ocr:Gemini:ApiKey/BaseUrl/Enabled), "
                + "so every document takes the on-prem path and AL-27's auto-approve is unreachable.");
        }

        logger.LogInformation(
            "ocr-svc is up: redaction {Redaction} (policy {Policy}, pass {Pass}), Gemini {Gemini} on {Model}, "
            + "on-prem engine {Engine}, auto-verify at {Threshold}, fallback capped at {Ceiling}, raw retention "
            + "{Retention}.",
            // Δ MCS-07: the third value is the whole point of this line now. "DISARMED" used to
            // mean nothing left; it means the opposite when Gemini is configured, and an operator
            // grepping one word out of a boot log should not have to know which release they are on.
            armed ? "ARMED" : gemini.IsConfigured ? "DISARMED-SENDING-UNREDACTED" : "DISARMED",
            RedactionPipeline.PolicyVersion,
            RedactionPipeline.PassVersion,
            gemini.IsConfigured ? "configured" : "not configured",
            settings.Gemini.Model,
            tesseract ? "available" : "UNAVAILABLE",
            settings.ConfidenceThreshold,
            settings.TesseractConfidenceCeiling,
            settings.RawRetention);
    }
}
