using System.Security.Cryptography;

namespace MageRide.Ocr.Redaction;

/// <summary>A rectangle on a document, in pixels.</summary>
public readonly record struct PixelRegion(int Left, int Top, int Width, int Height)
{
    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// An image that has been through the D-36 pre-pass: faces blurred, identity numbers blacked out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ MCS-07 — this type is no longer a precondition of the external call.</b> It was: the
/// extractor took a <see cref="RedactedDocument"/> and nothing else, so there was no overload that
/// could be handed the bytes off object storage. That fence is gone by decision, not by accident —
/// see <see cref="OutboundDocument"/>, which is what the extractor takes now, and which carries
/// whether the pass ran. What this type still is: the <em>product</em> of the pass, and the only
/// place its ADD §12.5 provenance is assembled.
/// </para>
/// <para>
/// <see cref="RawSha256"/> and <see cref="RedactedSha256"/> are ADD §12.5's "document processing
/// log: hash + policy version + redaction-pass version stored per extraction", and they are stored
/// on <c>docs.extractions</c> (migration 1310). The raw hash is what makes a later privacy review
/// able to say <em>which</em> file was processed under which policy without keeping the file.
/// </para>
/// </remarks>
public sealed class RedactedDocument
{
    internal RedactedDocument(
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string rawSha256,
        string redactedSha256,
        int facesBlurred,
        int identifiersMasked,
        string policyVersion,
        string passVersion)
    {
        Bytes = bytes;
        ContentType = contentType;
        RawSha256 = rawSha256;
        RedactedSha256 = redactedSha256;
        FacesBlurred = facesBlurred;
        IdentifiersMasked = identifiersMasked;
        PolicyVersion = policyVersion;
        PassVersion = passVersion;
    }

    /// <summary>The redacted image. Faces blurred, identity numbers blacked out.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    public string ContentType { get; }

    /// <summary>Hex sha256 of the bytes as they came off storage. The file itself is not kept here.</summary>
    public string RawSha256 { get; }

    /// <summary>Hex sha256 of <see cref="Bytes"/> — what <c>PerimeterGuardHandler</c> checks against.</summary>
    public string RedactedSha256 { get; }

    /// <summary>How many face regions were blurred (ADD §12.5, OpenCV).</summary>
    public int FacesBlurred { get; }

    /// <summary>How many NIC / licence-number boxes were blacked out (ADD §12.5, Tesseract).</summary>
    public int IdentifiersMasked { get; }

    /// <summary>Which redaction <em>policy</em> was in force — the set of things masked.</summary>
    public string PolicyVersion { get; }

    /// <summary>Which build of the pass produced it. Both are stored per extraction.</summary>
    public string PassVersion { get; }
}

/// <summary>
/// The image this service is about to send to the external model, and what was done to it first.
/// </summary>
/// <remarks>
/// <para>
/// <b>Δ MCS-07.</b> D-36's original posture was <em>no redaction ⇒ no Gemini</em>, held by the
/// extractor's parameter type. That chain also meant <em>no Tesseract ⇒ no mask boxes ⇒ no
/// redaction ⇒ no Gemini</em>: a box without the OpenCV cascade or the tesseract binary extracted
/// nothing at all by any path. The posture is now <em>redact when the pass is available, send the
/// raw image when it is not</em>, and this type is what carries which of the two happened so the
/// prompt, the <c>docs.extractions</c> row and the perimeter ledger all describe the same fact.
/// </para>
/// <para>
/// <b>The wire guard still holds, and still means something.</b> <c>PerimeterGuardHandler</c>
/// refuses any outbound image whose sha256 the pipeline did not admit; what changed is which
/// hashes get admitted, not whether the check runs. It is no longer "this was redacted" — it is
/// "this is the document the pipeline resolved for this job", which is what still catches a
/// hand-assembled body, a second provider, or a retry that re-serialises from a stale buffer.
/// </para>
/// </remarks>
public sealed class OutboundDocument
{
    private OutboundDocument(
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string rawSha256,
        string sha256,
        RedactedDocument? redaction)
    {
        Bytes = bytes;
        ContentType = contentType;
        RawSha256 = rawSha256;
        Sha256 = sha256;
        Redaction = redaction;
    }

    /// <summary>The D-36 pre-pass ran; its output is what leaves.</summary>
    public static OutboundDocument FromRedaction(RedactedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new OutboundDocument(
            document.Bytes, document.ContentType, document.RawSha256, document.RedactedSha256, document);
    }

    /// <summary>
    /// The pre-pass could not run, so the bytes off object storage leave unaltered (Δ MCS-07).
    /// </summary>
    /// <remarks>
    /// The one caller is <c>ExtractionPipeline</c>, deliberately: this is the factory that puts an
    /// unredacted portrait and an unredacted NIC in front of a third-party model, and it should be
    /// possible to find every place that happens with one search for this method.
    /// </remarks>
    public static OutboundDocument FromRaw(ReadOnlyMemory<byte> bytes, string contentType)
    {
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));

        return new OutboundDocument(bytes, contentType, sha256, sha256, redaction: null);
    }

    /// <summary>What is actually sent.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    public string ContentType { get; }

    /// <summary>
    /// Hex sha256 of the bytes as they came off storage — ADD §12.5's "which file was processed",
    /// recorded on every extraction whether or not the pass ran.
    /// </summary>
    public string RawSha256 { get; }

    /// <summary>Hex sha256 of <see cref="Bytes"/>: what the ledger admits and the guard checks.</summary>
    public string Sha256 { get; }

    /// <summary>The pre-pass's own provenance, or null when it did not run.</summary>
    public RedactedDocument? Redaction { get; }

    /// <summary>Whether faces and identity numbers were masked before this left.</summary>
    public bool IsRedacted => Redaction is not null;
}

/// <summary>
/// What the pre-pass made of one document.
/// </summary>
/// <param name="Document">
/// <see langword="null"/> when the pass could not run. There is no third state and no "redacted as
/// best we could": either the document was redacted and may leave, or it was not and may not.
/// </param>
/// <param name="Reason">Why it could not run, for the log and the officer queue. Null on success.</param>
public sealed record RedactionOutcome(RedactedDocument? Document, string? Reason)
{
    public bool Succeeded => Document is not null;

    public static RedactionOutcome Redacted(RedactedDocument document) => new(document, null);

    /// <summary>The pass could not run. Nothing leaves the perimeter (D-36).</summary>
    public static RedactionOutcome Failed(string reason) => new(null, reason);
}
