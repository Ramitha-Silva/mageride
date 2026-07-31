namespace MageRide.Ocr.Redaction;

/// <summary>A rectangle on a document, in pixels.</summary>
public readonly record struct PixelRegion(int Left, int Top, int Width, int Height)
{
    public int Area => Math.Max(0, Width) * Math.Max(0, Height);
}

/// <summary>
/// An image that has been through the D-36 pre-pass, and the only thing this service will send to
/// an external model.
/// </summary>
/// <remarks>
/// <para>
/// <b>The constructor is internal to the redaction namespace's owner, and that is the point.</b>
/// <c>GeminiFieldExtractor</c> takes a <see cref="RedactedDocument"/>, not a <c>byte[]</c>: there is
/// no overload, no convenience path and no way to hand it the bytes that came off object storage.
/// D-36 says "no exceptions", and a fence that depends on every future caller remembering it is not
/// a fence. <c>PerimeterGuardHandler</c> is the second half — it checks the wire, in case a payload
/// is ever assembled somewhere this type is not.
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
