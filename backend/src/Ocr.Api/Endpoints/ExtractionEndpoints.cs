using System.Text.Json.Serialization;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Queue;
using MageRide.Ocr.Redaction;
using MageRide.Shared.Errors;

namespace MageRide.Ocr.Endpoints;

/// <summary>What registry-svc (or fleet-svc, AL-50) asks this service to read.</summary>
/// <param name="Side"><c>front</c> or <c>back</c>; anything else is treated as unspecified.</param>
/// <param name="RegistrationNumber">
/// Required in practice for <c>registration</c> documents — without it there is nothing to compare
/// the plate against and <c>reg_no_match</c> can only be <c>pending</c> (D5' §14.1a).
/// </param>
public sealed record ExtractionRequestBody(
    Guid UploadId,
    string StorageUrl,
    string Kind,
    string? Side = null,
    string? RegistrationNumber = null);

/// <summary>One field, as it goes back over the wire.</summary>
/// <remarks>
/// <c>value</c> is emitted even when null — <c>MageRideJson</c> drops nulls by default, and a
/// required key that could not be read is precisely a <c>{"key": …, "value": null}</c> the caller
/// has to see. The same load-bearing <c>[JsonIgnore(Never)]</c> C029 needed on its side.
/// </remarks>
public sealed record ExtractedFieldBody(
    string Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Confidence,
    string VerifyStatus,
    string Source);

/// <summary>What this service made of one document.</summary>
public sealed record ExtractionResponse(
    bool Succeeded,
    IReadOnlyList<ExtractedFieldBody> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] Guid? JobId,
    string Engine,
    bool RedactionApplied);

/// <summary>
/// ocr-svc's only surface: <c>POST /v1/internal/ocr/extractions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and not in D3'.</b> No specification gives ocr-svc a public route — D6' §7.5 draws
/// it as <c>registry-svc → ocr-svc</c> and nothing else — so this is a Δ C054 route on the internal
/// plane the gateway refuses at the edge (C008). Raised as a micro-change-set in the handoff.
/// </para>
/// <para>
/// <b>Synchronous over a queue, deliberately.</b> ADD §6 calls this service "stateless,
/// queue-driven" and D6' §2.1's table calls the hop "Sync+fallback"; both are true here. The queue
/// bounds how many documents are being redacted and read at once — the expensive, memory-hungry
/// part — while the caller keeps a request/response seam it can save a step against, inside
/// D6' §8.3's 30-second OCR budget.
/// </para>
/// <para>
/// <b>Nothing about a document is an error.</b> Unreadable, unavailable, both engines down — all of
/// them are a <c>200</c> with <c>succeeded: false</c>, because the caller has an onboarding step to
/// save either way (D5' §14.1a) and a <c>5xx</c> would make registry-svc's retry the thing standing
/// between a driver and their next screen. The only <c>4xx</c> here is a malformed request.
/// </para>
/// </remarks>
public static class ExtractionEndpoints
{
    /// <summary>The interim shared secret's header, until the mesh lands (C042).</summary>
    public const string ApiKeyHeader = "X-MageRide-Internal-Key";

    public static IEndpointRouteBuilder MapInternalOcrEndpoints(this IEndpointRouteBuilder routes, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var group = routes.MapGroup("/v1/internal/ocr")
            // The kernel's authorization is deny-by-default (`SetFallbackPolicy`), and this service
            // registers no authentication handler at all — there is no token on this plane to
            // present. The shared key below is the credential; without this the fallback policy
            // challenges a scheme that does not exist.
            .AllowAnonymous()
            .AddEndpointFilter(new InternalKeyFilter(apiKey))
            .WithTags("ocr-internal");

        group.MapPost("/extractions", ExtractAsync)
            .WithName("extractDocument")
            .WithSummary("Extracts the fields of one uploaded document (D6' §7.5).");

        return routes;
    }

    private static async Task<IResult> ExtractAsync(
        ExtractionRequestBody body,
        IExtractionDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.UploadId == Guid.Empty || string.IsNullOrWhiteSpace(body.StorageUrl))
        {
            throw new MageRideException(
                MageRideErrors.ValidationFailed, "uploadId and storageUrl are required.");
        }

        if (!DocumentKinds.IsKnown(body.Kind))
        {
            // A kind with no extractor behind it would come back with no fields and read as a
            // document nobody could make out, rather than as the mistake it is.
            throw new MageRideException(
                MageRideErrors.ValidationFailed,
                $"'{body.Kind}' is not a document kind ocr-svc extracts ({string.Join(", ", DocumentKinds.All)}).");
        }

        var result = await dispatcher.ExtractAsync(
            new ExtractionRequest(
                body.UploadId,
                body.StorageUrl,
                body.Kind,
                DocumentSides.Normalise(body.Side),
                body.RegistrationNumber),
            cancellationToken);

        return Results.Ok(new ExtractionResponse(
            result.Succeeded,
            [.. result.Fields.Select(field => new ExtractedFieldBody(
                field.Key, field.Value, field.Confidence, field.VerifyStatus, field.Source))],
            result.JobId,
            result.Engine,
            result.RedactionApplied));
    }
}

/// <summary>
/// The D-36 posture, as a readiness signal.
/// </summary>
/// <remarks>
/// <para>
/// Reported as <b>degraded, not unhealthy</b>. A disarmed redactor is a service that still extracts
/// — and taking the pod out of rotation for it would turn "documents leave unmasked today" into
/// "no onboarding today". What it must not do is be silent.
/// </para>
/// <para>
/// <b>Δ MCS-07 — what "degraded" now means here has changed, and it changed direction.</b> It used
/// to say <em>no document is reaching Gemini</em>; every extraction was on-prem, everything was
/// reviewed by an officer, and nothing left the perimeter. It now says the opposite: documents ARE
/// reaching Gemini and are doing so <em>unredacted</em>, with faces and identity numbers intact.
/// Same probe, same colour, inverted consequence — so the reasons below say which, rather than
/// leaving an operator to read a stale meaning into a familiar word.
/// </para>
public sealed class RedactionHealthCheck(IRedactionPipeline redaction, Ocr.IOcrEngine engine)
    : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var reasons = new List<string>();

        if (!engine.IsAvailable)
        {
            reasons.Add("the on-prem OCR engine (Tesseract) is not available, so there is no fallback for a "
                + "model outage and no source of D-36 mask boxes");
        }

        if (redaction.DisarmedReason is { } disarmed)
        {
            reasons.Add(disarmed);
        }

        return Task.FromResult(reasons.Count == 0
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(
                "Gemini is reachable through the D-36 redaction pre-pass.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Degraded(
                "The D-36 pre-pass cannot run: " + string.Join("; ", reasons)
                + ". Documents are still sent to Gemini, UNREDACTED — faces and identity numbers leave the "
                + "perimeter as photographed (Δ MCS-07). Fix the dependency or turn Ocr:Gemini:Enabled off."));
    }
}
