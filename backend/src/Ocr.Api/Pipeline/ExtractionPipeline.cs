using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Gemini;
using MageRide.Ocr.Ocr;
using MageRide.Ocr.Persistence;
using MageRide.Ocr.Redaction;
using MageRide.Ocr.Storage;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Pipeline;

/// <summary>Runs one document through the whole pass (D6' §7.5).</summary>
public interface IExtractionPipeline
{
    Task<ExtractionResult> RunAsync(ExtractionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Fetch → on-prem read → D-36 redaction → Gemini → fallback → <c>docs.extractions</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The on-prem read happens first, on every document, whether or not Gemini will be called.</b>
/// ADD §12.5 gets the boxes it blacks out from Tesseract, so the redaction pass cannot run without
/// it — and the same page is then the fallback extractor's input, so the expensive step runs once.
/// This is also what makes the D-36 posture legible: <b>no Tesseract ⇒ no boxes ⇒ no redaction ⇒ no
/// Gemini</b>, all the way down, with no branch that skips a link.
/// </para>
/// <para>
/// <b>Nothing in here throws.</b> Every failure — no such upload, unreadable bytes, no redactor,
/// Gemini down, both engines down — produces a result the caller can save an onboarding step
/// against. D5' §14.1a sends a document that did not extract to a Verification Officer; it does not
/// stop the driver.
/// </para>
/// </remarks>
public sealed class ExtractionPipeline : IExtractionPipeline
{
    private readonly IRawDocumentStore _storage;
    private readonly IOcrEngine _ocr;
    private readonly IRedactionPipeline _redaction;
    private readonly IPerimeterLedger _ledger;
    private readonly GeminiFieldExtractor _gemini;
    private readonly TesseractFieldExtractor _fallback;
    private readonly IExtractionRepository _repository;
    private readonly OcrOptions _options;
    private readonly ILogger<ExtractionPipeline> _logger;

    public ExtractionPipeline(
        IRawDocumentStore storage,
        IOcrEngine ocr,
        IRedactionPipeline redaction,
        IPerimeterLedger ledger,
        GeminiFieldExtractor gemini,
        TesseractFieldExtractor fallback,
        IExtractionRepository repository,
        IOptions<OcrOptions> options,
        ILogger<ExtractionPipeline> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _gemini = gemini ?? throw new ArgumentNullException(nameof(gemini));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExtractionResult> RunAsync(ExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var upload = await _repository.FindUploadAsync(request.UploadId, cancellationToken);

        if (upload is null)
        {
            _logger.LogWarning(
                "docs.uploads has no row {UploadId}; there is nothing to extract.", request.UploadId);

            return ExtractionResult.Unavailable;
        }

        // NFR-28, before anything else touches the bytes: the deadline is the one thing that is
        // still owed even if every extractor is down.
        if (await _repository.EnsureRetentionAsync(request.UploadId, _options.RawRetention, cancellationToken))
        {
            _logger.LogInformation(
                "Stamped NFR-28's {Retention} deletion deadline on docs.uploads {UploadId}, which had none.",
                _options.RawRetention, request.UploadId);
        }

        var raw = await _storage.ReadAsync(upload.StorageUrl, cancellationToken);

        if (raw is null)
        {
            return await PersistAsync(request, [], ExtractionEngines.None, redaction: null, cancellationToken);
        }

        // One read, two consumers: the mask boxes (ADD §12.5) and the fallback fields (D6' §7.5).
        var page = await _ocr.ReadAsync(raw.Bytes, raw.ContentType, cancellationToken);

        var redaction = _redaction.Redact(raw.Bytes, raw.ContentType, page);

        if (!redaction.Succeeded)
        {
            _logger.LogWarning(
                "The D-36 pre-pass did not run on upload {UploadId} ({Reason}); NOTHING was sent to Gemini and "
                + "the document falls back to the on-prem path.",
                request.UploadId, redaction.Reason);
        }

        var fields = await ReadFieldsAsync(request, page, redaction.Document, cancellationToken);

        return await PersistAsync(request, fields.Fields, fields.Engine, redaction.Document, cancellationToken);
    }

    private async Task<(IReadOnlyList<ExtractedField> Fields, string Engine)> ReadFieldsAsync(
        ExtractionRequest request,
        OcrPage page,
        RedactedDocument? redacted,
        CancellationToken cancellationToken)
    {
        if (redacted is not null)
        {
            // Admitted *before* the call, so the guard on the way out can recognise it. Nothing else
            // in this service admits a hash.
            _ledger.Admit(redacted.RedactedSha256);

            if (await _gemini.ExtractAsync(redacted, request, cancellationToken) is { } extraction)
            {
                return (extraction.Fields, ExtractionEngines.Gemini);
            }
        }

        var fallback = _fallback.Extract(page, request.Kind, request.Side);

        return fallback.Count == 0
            ? ([], ExtractionEngines.None)
            : (fallback, ExtractionEngines.Tesseract);
    }

    /// <summary>
    /// Completes the field set, judges it, writes the row, and shapes the answer.
    /// </summary>
    private async Task<ExtractionResult> PersistAsync(
        ExtractionRequest request,
        IReadOnlyList<ExtractedField> read,
        string engine,
        RedactedDocument? redaction,
        CancellationToken cancellationToken)
    {
        var fields = Complete(request, read);

        var result = new ExtractionResult(
            engine != ExtractionEngines.None,
            fields,
            JobId: null,
            engine,
            redaction is not null);

        try
        {
            var jobId = await _repository.RecordAsync(
                new ExtractionRecord(
                    request.UploadId,
                    request.Kind,
                    fields,
                    result.Status,
                    result.LowestConfidence,
                    // The column defaults true, so it has to be written explicitly on the path that
                    // skipped the pass — 1301's own comment says so.
                    redaction is not null,
                    engine,
                    redaction?.RawSha256,
                    redaction?.RedactedSha256,
                    redaction?.PolicyVersion,
                    redaction?.PassVersion,
                    redaction?.FacesBlurred,
                    redaction?.IdentifiersMasked),
                cancellationToken);

            return result with { JobId = jobId };
        }
        catch (Exception exception) when (exception is Npgsql.NpgsqlException or TimeoutException)
        {
            // The fields are still good and the caller still has a step to save. A `docs.extractions`
            // row this service could not write is a gap in the audit trail, which is loud — but it is
            // not a reason to send a driver back to a Verification Officer.
            _logger.LogError(
                exception,
                "The docs.extractions row for upload {UploadId} could not be written. The extraction itself "
                + "succeeded and is being returned without an ocrJobId.",
                request.UploadId);

            return result;
        }
    }

    /// <summary>
    /// Adds the fields that are decided here rather than read, and judges every one of them.
    /// </summary>
    /// <remarks>
    /// Two things are added: <c>reg_no_match</c>, which is a comparison and not an extraction
    /// (D5' §14.1a), and a row for every required key nothing returned — with a null value, so the
    /// officer queue shows "the insurance expiry could not be read" as something to fill.
    /// </remarks>
    private IReadOnlyList<ExtractedField> Complete(ExtractionRequest request, IReadOnlyList<ExtractedField> read)
    {
        var side = DocumentSides.Normalise(request.Side);
        var threshold = _options.ConfidenceThreshold;

        var fields = read
            .Where(field => field.Key != DocumentFieldKeys.RegNoMatch)
            .Select(field => FieldVerdicts.Judge(field, threshold))
            .ToList();

        if (request.Kind == DocumentKinds.Registration)
        {
            fields.Add(MatchPlate(request, fields, threshold));
        }

        foreach (var required in DocumentFieldKeys.RequiredFor(request.Kind, side))
        {
            if (!fields.Any(field => field.Key == required))
            {
                fields.Add(new ExtractedField(required, null, null, VerifyStatuses.Pending));
            }
        }

        return fields;
    }

    /// <summary>
    /// D5' §14.1a's photos verdict: does the plate read off the image name the vehicle the driver
    /// registered?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It carries the plate's own confidence, not the comparison's.</b> A comparison is exact —
    /// there is nothing uncertain about <c>==</c> — but what went into it is a read, and a match
    /// derived from a plate nobody could make out is not evidence. So a low-confidence
    /// <c>plate_text</c> makes <c>reg_no_match</c> pending even when it matched, which is the
    /// conservative direction and the one AL-30's auto-approve depends on.
    /// </para>
    /// <para>
    /// <b>A request with no expected plate cannot match.</b> That is a caller sending the photos
    /// step without the registration it is being compared against — a bug, not a document.
    /// </para>
    /// </remarks>
    private static ExtractedField MatchPlate(
        ExtractionRequest request, IReadOnlyList<ExtractedField> fields, decimal threshold)
    {
        var plate = fields.FirstOrDefault(field => field.Key == DocumentFieldKeys.PlateText);

        if (plate?.Value is null || string.IsNullOrWhiteSpace(request.RegistrationNumber))
        {
            return new ExtractedField(DocumentFieldKeys.RegNoMatch, null, null, VerifyStatuses.Pending);
        }

        var matched = PlateNumbers.Match(plate.Value, request.RegistrationNumber);

        return FieldVerdicts.Judge(
            new ExtractedField(
                DocumentFieldKeys.RegNoMatch,
                matched ? "true" : "false",
                plate.Confidence),
            threshold);
    }
}
