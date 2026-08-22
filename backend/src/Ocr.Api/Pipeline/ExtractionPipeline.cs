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
/// </para>
/// <para>
/// <b>Δ MCS-07 — the redaction pass is best-effort, and no longer gates the model.</b> The old
/// chain was <c>no Tesseract ⇒ no boxes ⇒ no redaction ⇒ no Gemini</c>, with no branch that skipped
/// a link, and it was load-bearing for D-36. It also meant a deployment missing either native
/// dependency extracted <em>nothing at all</em> by any path while looking, from the outside, like a
/// service that worked. The pass now runs when it can and is skipped when it cannot; the image goes
/// to the model either way, and <see cref="OutboundDocument.IsRedacted"/> is what the prompt, the
/// perimeter ledger and <c>docs.extractions.redaction_applied</c> all read so the three agree.
/// <b>Tesseract stays the fallback for a model that is down</b> — that is D6' §8.3 and is
/// unchanged.
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
            return await PersistAsync(
                request, [], ExtractionEngines.None, DocumentTypes.Unclear, outbound: null, cancellationToken);
        }

        // One read, two consumers: the mask boxes (ADD §12.5) and the fallback fields (D6' §7.5).
        var page = await _ocr.ReadAsync(raw.Bytes, raw.ContentType, cancellationToken);

        var redaction = _redaction.Redact(raw.Bytes, raw.ContentType, page);

        // Δ MCS-07: a warning, not a stop. The pass failing now costs the masking, not the read.
        // This says only what happened HERE — whether the document then actually leaves is decided
        // below and logged there, so neither line claims something the other one prevented.
        if (!redaction.Succeeded)
        {
            _logger.LogWarning(
                "The D-36 pre-pass did not run on upload {UploadId}: {Reason}.",
                request.UploadId, redaction.Reason);
        }

        var outbound = redaction.Document is { } redacted
            ? OutboundDocument.FromRedaction(redacted)
            // The type declared on the wire is what the BYTES are, not what `docs.uploads` recorded:
            // the stored value is whatever the uploading client called the file. Falling back to the
            // stored one keeps the document intact for `MayBeSent` to refuse below, rather than
            // asserting a type here that the magic number does not support.
            : OutboundDocument.FromRaw(
                raw.Bytes, ContentTypes.InlineImageType(raw.Bytes.Span) ?? raw.ContentType);

        var fields = await ReadFieldsAsync(request, page, outbound, cancellationToken);

        return await PersistAsync(
            request, fields.Fields, fields.Engine, fields.DocumentType, outbound, cancellationToken);
    }

    private async Task<(IReadOnlyList<ExtractedField> Fields, string Engine, string DocumentType)> ReadFieldsAsync(
        ExtractionRequest request,
        OcrPage page,
        OutboundDocument outbound,
        CancellationToken cancellationToken)
    {
        if (MayBeSent(outbound))
        {
            // The one place an unmasked document is recorded as it happens, rather than as a row
            // somebody queries later. WARNING and not INFO because it is a privacy-relevant event
            // per document; the reason it is unmasked was logged once by the caller, and the
            // start-up ERROR says it will keep happening until a dependency is fixed.
            if (!outbound.IsRedacted && _gemini.IsConfigured)
            {
                _logger.LogWarning(
                    "Upload {UploadId} is going to Gemini UNREDACTED (sha256 {Hash}): any portrait and any "
                    + "identity number on it leave the perimeter as photographed. docs.extractions."
                    + "redaction_applied is false on this row (Δ MCS-07).",
                    request.UploadId, outbound.Sha256);
            }

            // Admitted *before* the call, so the guard on the way out can recognise it. Still the
            // only place in this service that admits a hash — see OutboundDocument on what it means.
            _ledger.Admit(outbound.Sha256);

            // Δ MCS-07: `{ Fields.Count: > 0 }`, not `{ }`. A non-null extraction carrying ZERO
            // fields used to count as success — engine `gemini`, succeeded `true`, Tesseract never
            // consulted — and the caller then saw only the null rows `Complete` adds for the
            // required keys. It is reachable whenever the model answers with keys this kind does not
            // accept, or with `reg_no_match` alone, both of which the extractor's own filter drops.
            // An answer with nothing usable in it is a read that did not happen, and belongs on the
            // fallback.
            if (await _gemini.ExtractAsync(outbound, request, cancellationToken) is { Fields.Count: > 0 } extraction)
            {
                return (extraction.Fields, ExtractionEngines.Gemini, extraction.DocumentType);
            }
        }

        var fallback = _fallback.Extract(page, request.Kind, request.Side);

        // Δ MCS-21 — the fallback carries `unclear`, always. There is no classifier on the on-prem
        // path and one must NOT be faked out of keywords: a document reaching Tesseract is already
        // one where the model was unavailable or refused, and inventing a type judgement from the
        // same page that produced the fields would be the least reliable input in the service
        // deciding the most consequential thing about it.
        return fallback.Count == 0
            ? ([], ExtractionEngines.None, DocumentTypes.Unclear)
            : (fallback, ExtractionEngines.Tesseract, DocumentTypes.Unclear);
    }

    /// <summary>
    /// Whether this document may go to the external model at all (Δ MCS-07).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A redacted document always may: the pass produced it, so it decoded, and
    /// <see cref="RedactionPipeline"/> re-encoded it as PNG or JPEG.
    /// </para>
    /// <para>
    /// A RAW one only may if its magic number says it is an image the model takes inline. That
    /// check used to be free: bytes that would not decode failed the redaction pass, and a failed
    /// pass meant nothing was sent. Now that the pass no longer gates the send, this is the whole
    /// of what stops a text file, a truncated upload or a PDF being posted to a third party
    /// labelled <c>image/jpeg</c> — <c>ContentTypes.FromBytes</c> guesses that for anything it does
    /// not recognise, which is right for naming a temporary file and wrong for the wire.
    /// </para>
    /// </remarks>
    private static bool MayBeSent(OutboundDocument outbound) =>
        outbound.IsRedacted || ContentTypes.InlineImageType(outbound.Bytes.Span) is not null;

    /// <summary>
    /// Completes the field set, judges it, writes the row, and shapes the answer.
    /// </summary>
    private async Task<ExtractionResult> PersistAsync(
        ExtractionRequest request,
        IReadOnlyList<ExtractedField> read,
        string engine,
        string documentType,
        OutboundDocument? outbound,
        CancellationToken cancellationToken)
    {
        var fields = Complete(request, read, documentType);

        var redaction = outbound?.Redaction;

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
                    // skipped the pass — 1301's own comment says so. Since MCS-07 that path can end
                    // at Gemini rather than at Tesseract, which is exactly what makes the per-row
                    // value worth reading: it is now the only record of which images left masked.
                    redaction is not null,
                    engine,
                    // ADD §12.5's "which file was processed" comes off the OUTBOUND document, so it
                    // is recorded even when the pass did not run and there is no RedactedDocument.
                    outbound?.RawSha256,
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
    private IReadOnlyList<ExtractedField> Complete(
        ExtractionRequest request, IReadOnlyList<ExtractedField> read, string documentType)
    {
        var side = DocumentSides.Normalise(request.Side);
        var threshold = _options.ConfidenceThreshold;

        // Δ MCS-21 — the model positively identified this as a DIFFERENT document from the one the
        // caller claimed.
        //
        // Implemented as CONFIDENCE SUPPRESSION rather than by stamping `pending`, and that is not
        // a stylistic choice: registry-svc deliberately discards this service's `verifyStatus` and
        // re-derives its own from the confidence alone (`OcrDocumentExtractionClient`, and the
        // reason is recorded there). A `VerifyStatus.Pending` set here would change this service's
        // own row and NOTHING about `registry.document_fields`, the step verdict or AL-27's
        // auto-approval — a feature that reads as working and does nothing.
        //
        // A null confidence is the one value that crosses that seam: `DeriveVerifyStatus` treats it
        // exactly like a below-threshold one, so both services reach `pending` by the same rule.
        //
        // The VALUE is kept. The officer has to see what was actually read to judge it, and a
        // document that is the wrong kind is precisely the case where the read matters most.
        var contradicted = DocumentTypes.Contradicts(documentType, request.Kind);

        if (contradicted)
        {
            _logger.LogWarning(
                "Upload {UploadId} was submitted as {Claimed} and the model identified it as {Reported}. "
                + "Every field is returned without a confidence, so registry-svc marks them pending and a "
                + "Verification Officer sees the document (Δ MCS-21).",
                request.UploadId,
                request.Kind,
                documentType);
        }

        var fields = read
            .Where(field => field.Key != DocumentFieldKeys.RegNoMatch)
            .Select(field => contradicted ? field with { Confidence = null } : field)
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
