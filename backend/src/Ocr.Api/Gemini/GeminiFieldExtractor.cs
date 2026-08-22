using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Redaction;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Gemini;

/// <summary>What the model returned for one document, before any verdict is applied to it.</summary>
/// <param name="DocumentType">
/// What the model said the image ACTUALLY shows, already reduced to <see cref="DocumentTypes"/>'s
/// closed set (Δ MCS-21). <c>unclear</c> whenever it was absent, unrecognised or unreadable — the
/// three cases that carry no information and are never acted on.
/// </param>
public sealed record GeminiExtraction(IReadOnlyList<ExtractedField> Fields, string DocumentType);

/// <summary>
/// D6' §7.5's primary path: Gemini Flash, on the redacted image where there is one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ MCS-07: this takes an <see cref="OutboundDocument"/>, which may or may not have been
/// redacted.</b> It used to take a <see cref="RedactedDocument"/> and nothing else, which made the
/// D-36 pre-pass a compile-time precondition of the external call; that also made a box with no
/// tesseract binary or no OpenCV cascade extract nothing at all, by any path. The pass now runs
/// when it can and is skipped when it cannot. <c>PerimeterGuardHandler</c> still sits on this
/// client and still refuses any image the pipeline did not admit for this job.
/// </para>
/// <para>
/// <b>The prompt follows the image.</b> A redacted document is described as redacted and the model
/// is told never to reconstruct what is behind a mask; a raw one is not, because telling a model
/// that a portrait it can see plainly has been blurred is how it decides the fields beside it are
/// unreliable too. <see cref="GeminiPrompts"/> holds both.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A refusal, a timeout, an unparseable answer and a missing API key all
/// return <see langword="null"/>, and the caller falls to the on-prem engine. D6' §8.3 is explicit
/// that "OCR down → Tesseract", and this service's fence is that Gemini being unavailable must not
/// stop a driver onboarding — only auto-approval.
/// </para>
/// </remarks>
public sealed class GeminiFieldExtractor
{
    /// <summary>The named client the resilience pipeline and the perimeter guard are attached to.</summary>
    public const string HttpClientName = "gemini";

    /// <summary>Google's API-key header. Not a bearer, and not a query parameter — it would be logged.</summary>
    public const string ApiKeyHeader = "x-goog-api-key";

    private readonly IHttpClientFactory _clients;
    private readonly OcrOptions _options;
    private readonly ILogger<GeminiFieldExtractor> _logger;

    public GeminiFieldExtractor(
        IHttpClientFactory clients, IOptions<OcrOptions> options, ILogger<GeminiFieldExtractor> logger)
    {
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Whether this path is configured at all.</summary>
    public bool IsConfigured =>
        _options.Gemini.Enabled
        && !string.IsNullOrWhiteSpace(_options.Gemini.BaseUrl)
        && !string.IsNullOrWhiteSpace(_options.Gemini.ApiKey);

    /// <summary>Reads <paramref name="document"/>, or returns null so the caller falls back.</summary>
    public async Task<GeminiExtraction?> ExtractAsync(
        OutboundDocument document, ExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
        {
            return null;
        }

        var payload = BuildRequest(document, request);

        try
        {
            var client = _clients.CreateClient(HttpClientName);

            using var message = new HttpRequestMessage(
                HttpMethod.Post, $"v1beta/models/{_options.Gemini.Model}:generateContent")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            message.Headers.TryAddWithoutValidation(ApiKeyHeader, _options.Gemini.ApiKey);

            using var response = await client.SendAsync(message, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Gemini answered {Status} for a {Kind} document; falling back to on-prem Tesseract (D6' §8.3).",
                    (int)response.StatusCode,
                    request.Kind);

                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return Parse(body, request);
        }
        catch (PerimeterViolationException)
        {
            // The guard already logged it as critical. Rethrowing would fail the whole extraction;
            // falling back keeps the driver moving while the defect is visible in the log and in
            // `docs.extractions.redaction_applied`.
            return null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception, "Gemini could not be reached; falling back to on-prem Tesseract (D6' §8.3).");

            return null;
        }
    }

    private string BuildRequest(OutboundDocument document, ExtractionRequest request)
    {
        var prompt = GeminiPrompts.For(request.Kind, DocumentSides.Normalise(request.Side), document.IsRedacted);

        var payload = new GeminiRequest(
            [new GeminiContent("user",
            [
                new GeminiPart(prompt, null),
                new GeminiPart(null, new GeminiInlineData(document.ContentType, Convert.ToBase64String(document.Bytes.Span))),
            ])],
            // Zero temperature: this is a transcription, and a model free to vary is a model that
            // returns a different expiry for the same certificate on a retry.
            new GeminiGenerationConfig(
                0,
                "application/json",
                _options.Gemini.MaxOutputTokens,
                ResponseSchema));

        return JsonSerializer.Serialize(payload, GeminiJson.Options);
    }

    /// <summary>
    /// The shape the model must answer in (Δ MCS-21).
    /// </summary>
    /// <remarks>
    /// Static: the field LIST varies per document kind, but the schema deliberately does not
    /// constrain which keys appear — the prompt names them and
    /// <see cref="GeminiFieldExtractor"/> already drops anything unasked-for. What this pins is the
    /// one thing prose could not: that <c>document_type</c> is present and is one of the closed
    /// set, so "the model could not tell" and "the model ignored the instruction" stop being the
    /// same null.
    /// </remarks>
    private static readonly GeminiSchema ResponseSchema = new(
        "object",
        Properties: new Dictionary<string, GeminiSchema>(StringComparer.Ordinal)
        {
            ["document_type"] = new("string", Enum: DocumentTypes.All),
            ["fields"] = new(
                "array",
                Items: new(
                    "object",
                    Properties: new Dictionary<string, GeminiSchema>(StringComparer.Ordinal)
                    {
                        ["key"] = new("string"),
                        ["value"] = new("string"),
                        ["confidence"] = new("number"),
                    },
                    Required: ["key"])),
        },
        Required: ["document_type", "fields"]);

    private GeminiExtraction? Parse(string body, ExtractionRequest request)
    {
        GeminiResponse? response;

        try
        {
            response = JsonSerializer.Deserialize<GeminiResponse>(body, GeminiJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Gemini returned an unreadable envelope for a {Kind} document.", request.Kind);

            return null;
        }

        var text = response?.Candidates?
            .FirstOrDefault()?.Content?.Parts?
            .Select(part => part.Text)
            .FirstOrDefault(part => !string.IsNullOrWhiteSpace(part));

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogWarning(
                "Gemini returned no content for a {Kind} document (finish reason {Reason}).",
                request.Kind,
                response?.Candidates?.FirstOrDefault()?.FinishReason ?? "none");

            return null;
        }

        GeminiFields? fields;

        try
        {
            fields = JsonSerializer.Deserialize<GeminiFields>(Unfence(text), GeminiJson.Options);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Gemini returned unparseable JSON for a {Kind} document.", request.Kind);

            return null;
        }

        if (fields?.Fields is not { Count: > 0 })
        {
            return null;
        }

        var accepted = DocumentFieldKeys.AcceptedFor(request.Kind, DocumentSides.Normalise(request.Side));

        var extracted = fields.Fields
            // A key nobody asked for is dropped rather than stored: `registry.document_fields`
            // takes free text, so an invented key would land in the officer queue as a row about a
            // field the wizard has no screen for.
            .Where(field => field.Key is not null && accepted.Contains(field.Key, StringComparer.Ordinal))
            // reg_no_match is this service's own comparison (D5' §14.1a), never the model's opinion.
            .Where(field => field.Key != DocumentFieldKeys.RegNoMatch)
            .GroupBy(field => field.Key!, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(field => new ExtractedField(
                field.Key!,
                FieldValues.Normalise(field.Key!, field.Value),
                Clamp(field.Confidence)))
            .ToArray();

        return new GeminiExtraction(extracted, DocumentTypes.Reduce(fields.DocumentType));
    }

    /// <summary>
    /// Strips a <c>```json</c> fence if the model wrapped its answer in one.
    /// </summary>
    /// <remarks>
    /// <c>responseMimeType: application/json</c> is supposed to make this unnecessary, and mostly
    /// does. Mostly is not a property to build a verdict on, and the alternative is dropping a whole
    /// document to a Verification Officer because of three backticks.
    /// </remarks>
    private static string Unfence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);

        return firstNewline < 0 || lastFence <= firstNewline
            ? trimmed
            : trimmed[(firstNewline + 1)..lastFence].Trim();
    }

    /// <summary>
    /// A confidence outside 0–1, or absent, reads as none — which is treated exactly like a
    /// below-threshold one.
    /// </summary>
    /// <remarks>
    /// <b>Δ MCS-17 — TRUNCATED to three places, not rounded to them.</b> This used to round-trip
    /// through <c>ToString("0.###")</c>, which rounds half away from zero: a model that reported
    /// <c>0.7996</c> — having been told by <see cref="GeminiPrompts"/> that below 0.8 means "a
    /// human should check it" — arrived as <c>0.8m</c> and cleared a <c>0.80</c> threshold it had
    /// explicitly declined to meet. The number is the ONLY input to the auto-verify decision
    /// (<c>FieldVerdicts.IsPending</c>, and registry-svc's <c>DeriveVerifyStatus</c> re-derives the
    /// same rule), so rounding it upward across the boundary silently converts an officer review
    /// into a trusted value. Truncation can only ever move a value further from auto-verified,
    /// which is the safe direction for a number nothing measures.
    /// </remarks>
    private static decimal? Clamp(double? confidence) =>
        confidence is null || double.IsNaN(confidence.Value) || confidence is < 0 or > 1
            ? null
            : Math.Floor((decimal)confidence.Value * 1000m) / 1000m;
}

/// <summary>
/// Gemini's wire shapes. Snake case, which is why they do not use <c>MageRideJson</c>.
/// </summary>
internal static class GeminiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

internal sealed record GeminiRequest(
    IReadOnlyList<GeminiContent> Contents, GeminiGenerationConfig GenerationConfig);

internal sealed record GeminiContent(string Role, IReadOnlyList<GeminiPart> Parts);

internal sealed record GeminiPart(string? Text, GeminiInlineData? InlineData);

internal sealed record GeminiInlineData(string MimeType, string Data);

/// <param name="ResponseSchema">
/// Δ MCS-21 — the shape is now CONSTRAINED rather than requested in prose.
///
/// Without it, a model that ignored the instruction and returned only <c>fields</c> is
/// indistinguishable from one that looked at the document and could not say — both arrive as a null
/// <c>document_type</c>. Both reduce to <c>unclear</c> and neither is acted on, so nothing unsafe
/// happens either way; but the feature would then silently never fire and nothing would say so.
/// </param>
internal sealed record GeminiGenerationConfig(
    double Temperature,
    string ResponseMimeType,
    int MaxOutputTokens,
    GeminiSchema? ResponseSchema = null);

/// <summary>A Gemini <c>responseSchema</c> node — the OpenAPI subset the API accepts.</summary>
internal sealed record GeminiSchema(
    string Type,
    IReadOnlyDictionary<string, GeminiSchema>? Properties = null,
    GeminiSchema? Items = null,
    IReadOnlyList<string>? Enum = null,
    IReadOnlyList<string>? Required = null);

internal sealed record GeminiResponse(IReadOnlyList<GeminiCandidate>? Candidates);

/// <param name="FinishReason">
/// Why the model stopped — <c>STOP</c>, <c>MAX_TOKENS</c>, <c>SAFETY</c>, …
///
/// <b>Δ MCS-17 — the explicit name is load-bearing.</b> <see cref="GeminiJson.Options"/> sets
/// <c>SnakeCaseLower</c>, so System.Text.Json looked for <c>finish_reason</c>; Google emits proto3
/// lowerCamelCase <c>finishReason</c>, and <c>PropertyNameCaseInsensitive</c> bridges case, never an
/// underscore. So this was ALWAYS null, and the diagnostic at the one place an empty extraction is
/// reported logged <c>"none"</c> every time — making a truncated answer (<c>MAX_TOKENS</c>, and the
/// budget is 2048 with no thinking budget set), a blocked one (<c>SAFETY</c>) and a genuinely empty
/// one indistinguishable. All three fall through to Tesseract identically and silently.
/// </param>
internal sealed record GeminiCandidate(
    GeminiContentResponse? Content,
    [property: JsonPropertyName("finishReason")] string? FinishReason);

internal sealed record GeminiContentResponse(IReadOnlyList<GeminiPartResponse>? Parts);

internal sealed record GeminiPartResponse(string? Text);

/// <param name="DocumentType">
/// What the model says the image actually shows (Δ MCS-21). Constrained by the response schema to
/// <see cref="DocumentTypes.All"/>, and reduced to <c>unclear</c> by <see cref="DocumentTypes.Reduce"/>
/// if it arrives absent or as anything else — a missing classification and a wrong one are the same
/// "no information", and neither is ever acted on.
/// </param>
internal sealed record GeminiFields(
    [property: JsonPropertyName("document_type")] string? DocumentType,
    IReadOnlyList<GeminiField>? Fields);

internal sealed record GeminiField(string? Key, string? Value, double? Confidence);
