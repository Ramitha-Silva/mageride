namespace MageRide.Ocr.Domain;

/// <summary>
/// One document handed to this service for extraction — registry-svc's
/// <c>DocumentExtractionRequest</c>, arriving over HTTP.
/// </summary>
/// <param name="UploadId">
/// The <c>docs.uploads</c> row. The bytes are fetched here rather than posted, which is what keeps
/// an unredacted image on this side of the D-36 perimeter and off registry-svc's disk.
/// </param>
/// <param name="StorageUrl">Where those bytes are, resolved by the caller from <c>docs.uploads</c>.</param>
/// <param name="Kind">A <see cref="DocumentKinds"/> value. Selects the extraction prompt.</param>
/// <param name="Side"><c>front</c>/<c>back</c> for two-sided captures, else null (AL-29).</param>
/// <param name="RegistrationNumber">
/// The plate the driver entered, for the photos step's <c>reg_no_match</c>. The comparison lives
/// here, with its normalisation, so the two services cannot disagree about whether
/// <c>wp qa-1234</c> is <c>WP-QA-1234</c>.
/// </param>
public sealed record ExtractionRequest(
    Guid UploadId,
    string StorageUrl,
    string Kind,
    string? Side = null,
    string? RegistrationNumber = null);

/// <summary>
/// One field this service read, with the confidence that decides whether anybody checks it.
/// </summary>
/// <param name="Value">
/// <see langword="null"/> when a required key could not be read. The key is still emitted — see
/// <see cref="DocumentFieldKeys.RequiredFor"/>.
/// </param>
/// <param name="Confidence">
/// 0–1, or <see langword="null"/> when the engine offered none. registry-svc treats an unscored
/// value exactly like a below-threshold one, and so does <see cref="VerifyStatus"/>.
/// </param>
/// <param name="VerifyStatus">
/// This service's field-level verdict (C054's fence 3): <c>auto_verified</c> or <c>pending</c>.
/// registry-svc re-derives the same answer from <see cref="Confidence"/> against its own threshold
/// and is the only writer of <c>registry.document_fields</c>; this is carried so the row this
/// service writes to <c>docs.extractions</c> and the officer queue agree about the same document.
/// </param>
public sealed record ExtractedField(
    string Key,
    string? Value,
    decimal? Confidence,
    string VerifyStatus = VerifyStatuses.AutoVerified,
    string Source = FieldSources.Ai)
{
    public bool IsPending => VerifyStatus == VerifyStatuses.Pending;
}

/// <summary>
/// What this service made of one document.
/// </summary>
/// <param name="Succeeded">
/// Whether an extraction actually ran. <see langword="false"/> covers Gemini down, Tesseract down
/// and no engine configured at all. The caller saves the onboarding step either way and lands it
/// <c>pending_review</c>, because a failed extraction must not stop onboarding — only auto-approval.
/// </param>
/// <param name="JobId">The <c>docs.extractions</c> row, surfaced to the app as <c>ocrJobId</c>.</param>
/// <param name="Engine">A <see cref="ExtractionEngines"/> value — which path produced this.</param>
/// <param name="RedactionApplied">
/// Whether the D-36 pre-pass ran on the bytes. <see langword="false"/> is only ever seen on a
/// Tesseract-only result, where nothing left the perimeter; it is never <see langword="false"/> on
/// a Gemini one, and <c>PerimeterGuardHandler</c> is what makes that structural rather than hoped-for.
/// </param>
public sealed record ExtractionResult(
    bool Succeeded,
    IReadOnlyList<ExtractedField> Fields,
    Guid? JobId = null,
    string Engine = ExtractionEngines.None,
    bool RedactionApplied = false)
{
    /// <summary>Nothing was read. Every required field is missing, so the document needs an officer.</summary>
    public static readonly ExtractionResult Unavailable = new(false, []);

    /// <summary>Whether any field on this document needs a Verification Officer.</summary>
    public bool NeedsReview => !Succeeded || Fields.Any(entry => entry.IsPending);

    /// <summary>
    /// The <c>docs.extractions.status</c> this result implies.
    /// </summary>
    /// <remarks>
    /// Three states, not two: a document that read cleanly is <c>EXTRACTED</c>, one that read with
    /// a doubtful or missing field is <c>MANUAL_REVIEW</c> (which is what
    /// <c>ix_extractions_review</c> indexes for US-2.10), and one that did not read at all is
    /// <c>FAILED</c>. Collapsing the last two would put "the model is down" and "this photograph is
    /// blurry" on the same queue with the same explanation.
    /// </remarks>
    public string Status => !Succeeded
        ? ExtractionStatuses.Failed
        : NeedsReview ? ExtractionStatuses.ManualReview : ExtractionStatuses.Extracted;

    /// <summary>
    /// The document-level confidence written to <c>docs.extractions.confidence</c> — the
    /// <em>lowest</em> field, not the mean.
    /// </summary>
    /// <remarks>
    /// A document is only as trustworthy as its worst field: an insurance certificate whose insurer
    /// read at 0.99 and whose expiry read at 0.3 is a document nobody should act on, and a mean of
    /// 0.65 describes neither number. Null when a field carried no confidence, for the same reason
    /// an unscored field is treated as doubtful.
    /// </remarks>
    public decimal? LowestConfidence
    {
        get
        {
            if (Fields.Count == 0)
            {
                return null;
            }

            decimal lowest = 1m;

            foreach (var entry in Fields)
            {
                if (entry.Confidence is not { } confidence)
                {
                    return null;
                }

                lowest = Math.Min(lowest, confidence);
            }

            return lowest;
        }
    }
}
