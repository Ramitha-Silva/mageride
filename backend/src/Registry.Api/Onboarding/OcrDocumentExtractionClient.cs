using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MageRide.Registry.Configuration;
using MageRide.Shared.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// The real <see cref="IDocumentExtractionClient"/>: <c>POST /v1/internal/ocr/extractions</c> on
/// ocr-svc (C054, D6' §7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The caller owns its adapter</b> — the shape notification-svc uses for content-svc. What is
/// coupled between the two services is the JSON on this one route, not a shared assembly.
/// </para>
/// <para>
/// <b>The image never passes through here.</b> This sends an id and a storage URL; ocr-svc fetches
/// the bytes itself. That is what keeps an unredacted document on the far side of the D-36
/// perimeter, and it is why the multipart variant of the step route is not mapped in this service.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> ocr-svc being unreachable, slow or unhappy answers
/// <see cref="DocumentExtraction.Unavailable"/>, exactly as <c>IDocumentExtractionClient</c>'s
/// contract requires: the onboarding step still saves, lands <c>pending_review</c> and goes to a
/// Verification Officer (D5' §14.1a). A retry is deliberately not attempted — ocr-svc has D6'
/// §8.3's retry on the hop that actually fails (Gemini) and its own on-prem fallback behind it, so
/// a second call from here would spend the driver's screen time re-running a pass that already ran.
/// </para>
/// </remarks>
public sealed class OcrDocumentExtractionClient : IDocumentExtractionClient
{
    /// <summary>The named client, so the timeout and the base address live in one place.</summary>
    public const string HttpClientName = "ocr-svc";

    /// <summary>ocr-svc's guard header (its <c>ExtractionEndpoints.ApiKeyHeader</c>).</summary>
    public const string InternalApiKeyHeader = "X-MageRide-Internal-Key";

    private readonly IHttpClientFactory _clients;
    private readonly RegistryOptions _options;
    private readonly ILogger<OcrDocumentExtractionClient> _logger;

    public OcrDocumentExtractionClient(
        IHttpClientFactory clients,
        IOptions<RegistryOptions> options,
        ILogger<OcrDocumentExtractionClient> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var message = new HttpRequestMessage(HttpMethod.Post, "v1/internal/ocr/extractions")
            {
                Content = JsonContent.Create(
                    new OcrExtractionRequest(
                        request.UploadId, request.StorageUrl, request.Kind, request.Side, request.RegistrationNumber),
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
                    "ocr-svc answered {Status} for {Kind} upload {UploadId}; the step will be saved pending_review.",
                    (int)response.StatusCode, request.Kind, request.UploadId);

                return DocumentExtraction.Unavailable;
            }

            var payload = await response.Content.ReadFromJsonAsync<OcrExtractionResponse>(
                MageRideJson.Options, cancellationToken);

            if (payload is null)
            {
                return DocumentExtraction.Unavailable;
            }

            return new DocumentExtraction(
                payload.Succeeded,
                [.. (payload.Fields ?? []).Select(field =>
                    new ExtractedField(field.Key, field.Value, field.Confidence))],
                payload.JobId);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              or System.Text.Json.JsonException
                                              && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "ocr-svc could not be reached for {Kind} upload {UploadId}; the step will be saved pending_review "
                + "and routed to the Verification Officer queue (US-2.10).",
                request.Kind, request.UploadId);

            return DocumentExtraction.Unavailable;
        }
    }

    private sealed record OcrExtractionRequest(
        Guid UploadId, string StorageUrl, string Kind, string? Side, string? RegistrationNumber);

    /// <summary>
    /// ocr-svc's answer, as far as this service reads it.
    /// </summary>
    /// <remarks>
    /// <c>verifyStatus</c> is on the wire and is deliberately <b>not</b> read. Whether a field is
    /// pending is registry-svc's decision (AL-29/AL-30 make it a property of
    /// <c>registry.document_fields</c>, which this service owns and ocr-svc does not); ocr-svc
    /// derives the same verdict for its own <c>docs.extractions</c> row, from the same confidence
    /// and the same threshold. Reading it here would make one of the two authoritative and leave the
    /// other silently drifting.
    /// </remarks>
    private sealed record OcrExtractionResponse(
        bool Succeeded, IReadOnlyList<OcrExtractedField>? Fields, Guid? JobId, string? Engine);

    private sealed record OcrExtractedField(
        string Key,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Value,
        decimal? Confidence);
}
