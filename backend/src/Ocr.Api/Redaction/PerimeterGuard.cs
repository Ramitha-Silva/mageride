using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MageRide.Ocr.Redaction;

/// <summary>
/// Remembers which images have been through the D-36 pre-pass, so the wire can be checked against
/// the fact rather than against a promise.
/// </summary>
/// <remarks>
/// A bounded, in-process set of hashes. It is not a security boundary on its own — an attacker with
/// code execution in this process has already lost the argument — it is a <b>defect</b> boundary:
/// the thing that turns "somebody added a code path that posts the raw bytes" from a privacy
/// incident nobody notices into a 500 and a log line naming D-36.
/// </remarks>
public interface IPerimeterLedger
{
    /// <summary>Records that these bytes are a redaction output and may leave.</summary>
    void Admit(string sha256);

    /// <summary>Whether bytes with this hash were produced by the pre-pass.</summary>
    bool IsAdmitted(string sha256);
}

/// <inheritdoc />
public sealed class PerimeterLedger : IPerimeterLedger
{
    /// <summary>
    /// How many redacted documents are remembered at once.
    /// </summary>
    /// <remarks>
    /// The window only has to outlive one request. Sized far above the queue's capacity so a burst
    /// cannot evict a document between its redaction and its own send — which would read as a
    /// violation and refuse a compliant call.
    /// </remarks>
    private const int Capacity = 4096;

    private readonly ConcurrentDictionary<string, long> _admitted = new(StringComparer.Ordinal);
    private long _sequence;

    public void Admit(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        _admitted[sha256] = Interlocked.Increment(ref _sequence);

        if (_admitted.Count <= Capacity)
        {
            return;
        }

        // Oldest-first, and only ever down to the capacity. A Clear() here would evict a document
        // that is mid-flight and turn a burst into a false violation.
        var cutoff = Interlocked.Read(ref _sequence) - Capacity;

        foreach (var (hash, admittedAt) in _admitted)
        {
            if (admittedAt <= cutoff)
            {
                _admitted.TryRemove(hash, out _);
            }
        }
    }

    public bool IsAdmitted(string sha256) =>
        !string.IsNullOrWhiteSpace(sha256) && _admitted.ContainsKey(sha256);
}

/// <summary>Raised when an outbound request carries an image the pre-pass never produced.</summary>
public sealed class PerimeterViolationException : InvalidOperationException
{
    public PerimeterViolationException(string message) : base(message)
    {
    }

    public PerimeterViolationException() : this("An unredacted image was about to leave the perimeter (D-36).")
    {
    }

    public PerimeterViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The last thing between an image and the external model: every inline image on an outbound
/// request must hash to something <see cref="IPerimeterLedger"/> admitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ MCS-07 — read what this now proves, and what it does not.</b> It used to be D-36's second
/// fence, behind a first one made of types: the extractor took a <see cref="RedactedDocument"/>, so
/// an admitted hash was by construction a redacted image. The extractor takes an
/// <see cref="OutboundDocument"/> now and the pre-pass is best-effort, so admission means
/// <em>this is the document <c>ExtractionPipeline</c> resolved for this job</em> — not
/// <em>this was masked</em>. Whether it was masked is
/// <c>docs.extractions.redaction_applied</c>, per row.
/// </para>
/// <para>
/// <b>Why a handler and not a code review.</b> Narrower is not nothing: this still holds where no
/// review does — a payload assembled by hand, a retry that re-serialises from a cached buffer, a
/// future extractor that reaches for the bytes off object storage instead of the ones the pipeline
/// resolved, another provider's field name. It inspects what is actually on the wire.
/// </para>
/// <para>
/// <b>It fails the request rather than stripping the image.</b> A stripped request would reach
/// Gemini, return nothing useful and be indistinguishable from a bad scan; the exception is caught
/// by the extractor and the document falls back to the on-prem path.
/// </para>
/// </remarks>
public sealed class PerimeterGuardHandler : DelegatingHandler
{
    /// <summary>
    /// Base64 runs shorter than this are not images — they are the request's own small strings, and
    /// hashing every one of them on every call is work for nothing.
    /// </summary>
    private const int MinimumImageBase64Length = 512;

    /// <summary>
    /// The fallback scan, for a body that is not parseable JSON.
    /// </summary>
    /// <remarks>
    /// <b>It cannot be the primary check, and finding that out is what this guard is for.</b>
    /// <c>System.Text.Json</c>'s default encoder escapes <c>+</c> and <c>/</c> to their
    /// <c>\uXXXX</c> forms, so a base64 image in a serialised body does not survive as a contiguous
    /// base64 run and a regex over the raw text matches nothing at all — which is a guard that
    /// silently passes everything. The JSON walk below sees the decoded string; this stays for a
    /// body the parser cannot read.
    /// </remarks>
    private static readonly Regex InlineData = new(
        @"""(?:data|inline_data|inlineData|b64_json|image)""\s*:\s*""(?<payload>[A-Za-z0-9+/=\r\n]{512,})""",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private readonly IPerimeterLedger _ledger;
    private readonly ILogger<PerimeterGuardHandler> _logger;

    public PerimeterGuardHandler(IPerimeterLedger ledger, ILogger<PerimeterGuardHandler> logger)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is not null)
        {
            await InspectAsync(request, cancellationToken);
        }

        return await base.SendAsync(request, cancellationToken);
    }

    private async Task InspectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var media = request.Content!.Headers.ContentType?.MediaType;

        if (media is not null && !media.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            // A raw image posted as image/* is a violation on its face: nothing on this service
            // sends bytes to an external host except through the JSON extraction request.
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);

            Check(Convert.ToHexStringLower(SHA256.HashData(bytes)), request);
            return;
        }

        var body = await request.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(body);

            Walk(document.RootElement, request);
        }
        catch (JsonException)
        {
            // A body the model could not read either. Scanned rather than trusted.
            foreach (var match in InlineData.Matches(body).Cast<Match>())
            {
                CheckBase64(match.Groups["payload"].Value, request);
            }
        }
    }

    /// <summary>
    /// Walks every string in the payload, checking the ones long enough to be an image.
    /// </summary>
    /// <remarks>
    /// <b>Every string, not just the ones under a known key.</b> The guard exists for the payload
    /// nobody reviewed — a future extractor, a different provider's field name, a retry that
    /// re-serialises through another shape. Keying on <c>inline_data</c> would only ever catch the
    /// code that is already correct.
    /// </remarks>
    private void Walk(JsonElement element, HttpRequestMessage request)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, request);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, request);
                }

                break;

            case JsonValueKind.String:
                CheckBase64(element.GetString(), request);
                break;

            default:
                break;
        }
    }

    private void CheckBase64(string? payload, HttpRequestMessage request)
    {
        if (payload is null || payload.Length < MinimumImageBase64Length)
        {
            return;
        }

        var compact = payload
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal);

        if (!Convert.TryFromBase64String(compact, new byte[((compact.Length / 4) + 1) * 3], out var written))
        {
            // A long opaque string in some other field — a prompt, an id, a signature.
            return;
        }

        var decoded = new byte[written];

        Convert.TryFromBase64String(compact, decoded, out _);

        Check(Convert.ToHexStringLower(SHA256.HashData(decoded)), request);
    }

    private void Check(string sha256, HttpRequestMessage request)
    {
        if (_ledger.IsAdmitted(sha256))
        {
            return;
        }

        _logger.LogCritical(
            "D-36 VIOLATION BLOCKED: a request to {Host} carried an image (sha256 {Hash}) that the redaction "
            + "pre-pass never produced. The request was not sent. This is a defect in ocr-svc, not a "
            + "configuration problem.",
            request.RequestUri?.Host,
            sha256);

        throw new PerimeterViolationException(
            $"An image that did not come from the D-36 redaction pre-pass (sha256 {sha256}) was about to be sent "
            + $"to {request.RequestUri?.Host}. The request was refused.");
    }
}
