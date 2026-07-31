using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MageRide.Fleet.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Options;

namespace MageRide.Fleet.Documents;

/// <summary>One AL-50 slot handed to ocr-svc for extraction (D6' §7.5).</summary>
/// <param name="UploadId">
/// The <c>docs.uploads</c> row. ocr-svc reads the bytes from storage itself — the image never
/// travels back through this service, which is what keeps the D-36 redaction pre-pass on ocr-svc's
/// side of the perimeter.
/// </param>
/// <param name="Kind">
/// The <b>stored</b> kind (<c>registration</c> | <c>insurance</c> | <c>revenue_license</c> |
/// <c>permit</c>), because it selects the extraction prompt and ocr-svc's vocabulary is
/// <c>registry.documents.kind</c>'s. The SCR-FP-004 slot label never leaves this service.
/// </param>
/// <param name="RegistrationNumber">
/// The plate on the roster, for a CR book's <c>reg_no_match</c> verdict. Given to ocr-svc rather
/// than compared here for C029's reason: splitting the comparison across two services would let
/// them disagree about normalisation.
/// </param>
public sealed record VehicleDocumentExtractionRequest(
    Guid UploadId, string StorageUrl, string Kind, string? RegistrationNumber);

/// <summary>One field ocr-svc read, with the confidence that decides whether anybody checks it.</summary>
/// <param name="Confidence">
/// 0–1. <see langword="null"/> is treated exactly like a below-threshold value — an unscored field
/// has not been verified, whatever produced it.
/// </param>
public sealed record ExtractedDocumentField(string Key, string? Value, decimal? Confidence);

/// <summary>What ocr-svc made of one document.</summary>
/// <param name="Succeeded">
/// Whether an extraction ran at all. <see langword="false"/> covers ocr-svc being down, unreachable
/// and unconfigured; the document is stored either way and its slot lands <c>pending</c>, because a
/// failed extraction must not stop an operator uploading — only approval.
/// </param>
public sealed record VehicleDocumentExtraction(
    bool Succeeded, IReadOnlyList<ExtractedDocumentField> Fields, Guid? JobId = null)
{
    /// <summary>Nothing was read. Every required field of the slot is missing.</summary>
    public static readonly VehicleDocumentExtraction Unavailable = new(false, []);
}

/// <summary>
/// The port this service calls ocr-svc through (ADD §6 <c>ocr-svc</c>, D6' §7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same seam registry-svc holds, and deliberately not a shared one.</b> What is coupled
/// between the two callers and ocr-svc is the JSON on one route, not an assembly — the judgement
/// <c>OcrDocumentExtractionClient</c> records for the Driver App's half of AL-50.
/// </para>
/// <para>
/// <b>The verdict split is C054's fence and is respected here.</b> This interface returns fields
/// with confidences and nothing else. Whether a field is <c>pending</c>, whether a slot is
/// <c>verified</c> and whether a vehicle may be approved are decided in this service, from
/// <c>registry.document_fields</c> and AL-50's required set — neither of which ocr-svc owns.
/// </para>
/// <para>
/// <b>An implementation must not throw for a document it could not read.</b> Return
/// <see cref="VehicleDocumentExtraction.Unavailable"/>; the document is stored, its slot reads
/// <c>pending</c>, and a Verification Officer takes it, which is what D5' §14.1a prescribes.
/// </para>
/// </remarks>
public interface IVehicleDocumentExtractionClient
{
    Task<VehicleDocumentExtraction> ExtractAsync(
        VehicleDocumentExtractionRequest request, CancellationToken cancellationToken);
}

/// <summary>The real client: <c>POST /v1/internal/ocr/extractions</c> on ocr-svc (C054).</summary>
public sealed class OcrVehicleDocumentExtractionClient : IVehicleDocumentExtractionClient
{
    /// <summary>The named client, so the timeout and the base address live in one place.</summary>
    public const string HttpClientName = "ocr-svc";

    /// <summary>ocr-svc's guard header (its <c>ExtractionEndpoints.ApiKeyHeader</c>).</summary>
    public const string InternalApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly IHttpClientFactory _clients;
    private readonly FleetOptions _options;
    private readonly ILogger<OcrVehicleDocumentExtractionClient> _logger;

    public OcrVehicleDocumentExtractionClient(
        IHttpClientFactory clients,
        IOptions<FleetOptions> options,
        ILogger<OcrVehicleDocumentExtractionClient> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<VehicleDocumentExtraction> ExtractAsync(
        VehicleDocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/internal/ocr/extractions")
            {
                Content = JsonContent.Create(
                    new OcrExtractionRequest(
                        request.UploadId, request.StorageUrl, request.Kind, null, request.RegistrationNumber),
                    options: MageRideJson.Options),
            };

            if (!string.IsNullOrWhiteSpace(_options.OcrInternalApiKey))
            {
                message.Headers.TryAddWithoutValidation(InternalApiKeyHeader, _options.OcrInternalApiKey);
            }

            using var response = await client.SendAsync(message, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ocr-svc answered {Status} for the {Kind} slot of upload {UploadId}; the document is stored and "
                    + "its SCR-FP-004 chip reads pending until a Verification Officer settles it.",
                    (int)response.StatusCode, request.Kind, request.UploadId);

                return VehicleDocumentExtraction.Unavailable;
            }

            var payload = await response.Content.ReadFromJsonAsync<OcrExtractionResponse>(
                MageRideJson.Options, cancellationToken);

            if (payload is null)
            {
                return VehicleDocumentExtraction.Unavailable;
            }

            return new VehicleDocumentExtraction(
                payload.Succeeded,
                [.. (payload.Fields ?? []).Select(field =>
                    new ExtractedDocumentField(field.Key, field.Value, field.Confidence))],
                payload.JobId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or System.Text.Json.JsonException
                                              && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "ocr-svc could not be reached for the {Kind} slot of upload {UploadId}; the document is stored and "
                + "goes to the Verification Officer queue unread (AL-50, D5' §14.1a).",
                request.Kind, request.UploadId);

            return VehicleDocumentExtraction.Unavailable;
        }
    }

    private sealed record OcrExtractionRequest(
        Guid UploadId, string StorageUrl, string Kind, string? Side, string? RegistrationNumber);

    /// <summary>
    /// ocr-svc's answer, as far as this service reads it.
    /// </summary>
    /// <remarks>
    /// <c>verifyStatus</c> is on the wire and is deliberately not read, for the reason registry-svc
    /// records: whether a field is pending is a property of <c>registry.document_fields</c>, which
    /// this service writes and ocr-svc does not. Reading it here would make one of the two
    /// authoritative and leave the other drifting.
    /// </remarks>
    private sealed record OcrExtractionResponse(
        bool Succeeded, IReadOnlyList<OcrExtractedField>? Fields, Guid? JobId, string? Engine);

    private sealed record OcrExtractedField(
        string Key,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
        decimal? Confidence);
}

/// <summary>
/// The answer for a deployment with no ocr-svc: nothing was read, and it says so once per document.
/// </summary>
/// <remarks>
/// Not a silent no-op. An unread document holds its slot at <c>pending</c>, which holds the vehicle
/// out of APPROVED — the correct behaviour, and completely invisible from outside without this log
/// line. Same shape as registry-svc's <c>UnconfiguredDocumentExtractionClient</c>.
/// </remarks>
public sealed class UnconfiguredVehicleDocumentExtractionClient(
    ILogger<UnconfiguredVehicleDocumentExtractionClient> logger) : IVehicleDocumentExtractionClient
{
    public Task<VehicleDocumentExtraction> ExtractAsync(
        VehicleDocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogWarning(
            "Fleet:OcrBaseUrl is not configured, so the {Kind} document {UploadId} was stored and not read. Its "
            + "SCR-FP-004 chip stays `pending`, and the vehicle cannot reach APPROVED until a Verification Officer "
            + "confirms the slot by hand (AL-50).",
            request.Kind,
            request.UploadId);

        return Task.FromResult(VehicleDocumentExtraction.Unavailable);
    }
}
