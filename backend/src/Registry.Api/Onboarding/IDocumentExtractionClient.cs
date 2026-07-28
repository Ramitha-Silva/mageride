using MageRide.Registry.Domain;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// One document handed to ocr-svc for extraction (D6' §7.5).
/// </summary>
/// <param name="UploadId">
/// The <c>docs.uploads</c> row. ocr-svc reads the bytes from object storage itself — the image
/// never travels through registry-svc, which is also what keeps the D-36 redaction pre-pass on
/// ocr-svc's side of the perimeter.
/// </param>
/// <param name="StorageUrl">Where those bytes are, resolved from <c>docs.uploads.storage_url</c>.</param>
/// <param name="Kind">A <see cref="DocumentKinds"/> value. Selects the extraction prompt.</param>
/// <param name="Side">
/// <c>front</c> or <c>back</c> for the two-sided captures (driving licence, vehicle photos), else
/// <see langword="null"/>. A Sri Lankan licence carries the classes on the back, so the side is
/// what tells ocr-svc which fields to expect (AL-29).
/// </param>
/// <param name="RegistrationNumber">
/// The plate the driver entered, for the photos step's <c>reg_no_match</c> comparison. ocr-svc is
/// given the expected value rather than asked to return only what it read, because the comparison
/// is the verdict D5' §14.1a defines and splitting it across two services would let them disagree
/// about normalisation.
/// </param>
public sealed record DocumentExtractionRequest(
    Guid UploadId,
    string StorageUrl,
    string Kind,
    string? Side = null,
    string? RegistrationNumber = null);

/// <summary>One field ocr-svc read, with the confidence that decides whether anybody checks it.</summary>
/// <param name="Confidence">
/// 0–1. <see langword="null"/> means ocr-svc returned no confidence, which is treated exactly like
/// a below-threshold one — an unscored value has not been verified, whatever produced it.
/// </param>
public sealed record ExtractedField(string Key, string? Value, decimal? Confidence);

/// <summary>
/// What ocr-svc made of one document.
/// </summary>
/// <param name="Succeeded">
/// Whether an extraction actually ran. <see langword="false"/> covers Gemini down, Tesseract down
/// and no extractor configured at all; the step is saved either way and lands
/// <c>pending_review</c>, because C054's fence is that a failed extraction must not stop
/// onboarding — only auto-approval.
/// </param>
/// <param name="JobId">
/// The <c>docs.extractions</c> row, when there is one. Surfaced as <c>ocrJobId</c> on
/// <c>POST /v1/vehicles</c>; absent when nothing was queued, so a client is never handed an
/// identifier no service will recognise.
/// </param>
public sealed record DocumentExtraction(bool Succeeded, IReadOnlyList<ExtractedField> Fields, Guid? JobId = null)
{
    /// <summary>Nothing was read. Every required field is missing, so the step needs an officer.</summary>
    public static readonly DocumentExtraction Unavailable = new(false, []);
}

/// <summary>
/// The port registry-svc calls ocr-svc through (ADD §6 <c>ocr-svc</c>, D6' §7.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>The verdict split is deliberate and is C054's fence.</b> This interface returns
/// <em>fields with confidences</em> and nothing else. Whether a field is
/// <c>pending</c>, whether a step is <c>pending_review</c> and whether a vehicle reaches
/// <c>APPROVED</c> are decided here in registry-svc — AL-30 makes them properties of
/// <c>registry.onboarding_steps</c> and <c>registry.vehicles</c>, which ocr-svc does not own.
/// </para>
/// <para>
/// <b>An implementation must not throw for a document it could not read.</b> Gemini being
/// unavailable is an expected state (C054's Tesseract fallback exists for it), and a driver whose
/// upload is fine must still get their step saved. Return
/// <see cref="DocumentExtraction.Unavailable"/> instead; the step becomes <c>pending_review</c>
/// and a Verification Officer takes it, which is exactly what D5' §14.1a says happens to a
/// document that did not extract.
/// </para>
/// </remarks>
public interface IDocumentExtractionClient
{
    Task<DocumentExtraction> ExtractAsync(DocumentExtractionRequest request, CancellationToken cancellationToken);
}
