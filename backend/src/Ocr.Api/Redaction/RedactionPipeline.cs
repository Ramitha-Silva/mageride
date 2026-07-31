using System.Security.Cryptography;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Ocr;

namespace MageRide.Ocr.Redaction;

/// <summary>The D-36 pre-pass. Everything that leaves this perimeter has been through it.</summary>
public interface IRedactionPipeline
{
    /// <summary>Whether the pass can run at all. False means nothing may be sent to Gemini.</summary>
    bool IsArmed { get; }

    /// <summary>Why it cannot run, for the start-up log and the health probe. Null when armed.</summary>
    string? DisarmedReason { get; }

    /// <summary>
    /// Blurs the faces and blacks out the identity numbers on <paramref name="raw"/>.
    /// </summary>
    /// <param name="page">
    /// The on-prem OCR read of the same bytes. ADD §12.5 gets its mask boxes from Tesseract, and the
    /// pipeline reads the page once and hands it to both this pass and the fallback extractor.
    /// </param>
    RedactionOutcome Redact(ReadOnlyMemory<byte> raw, string contentType, OcrPage page);
}

/// <summary>
/// OpenCV face-blur plus Tesseract bounding-box ID masking, in that order, on every document
/// (D-36, ADD §12.5).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure is a refusal, never a partial redaction.</b> There is no path through this class
/// that returns an image with some of the pass applied: an unavailable editor, an unavailable
/// detector, an unavailable OCR engine and bytes that are not an image all answer
/// <see cref="RedactionOutcome.Failed"/>, and the caller then has nothing it is allowed to send.
/// The alternative — "blur what we could find and go" — is a pipeline whose D-36 compliance depends
/// on whether a library happened to load, which is not a property anybody can audit.
/// </para>
/// <para>
/// <b>An empty face list is not a failure.</b> An insurance certificate has no portrait on it. What
/// distinguishes that from a detector that could not look is <see cref="IFaceDetector.IsAvailable"/>,
/// checked before the document rather than inferred from the result.
/// </para>
/// </remarks>
public sealed class RedactionPipeline : IRedactionPipeline
{
    /// <summary>
    /// What this pass masks (ADD §12.5's "policy version"). Bump when the <em>set</em> changes —
    /// a new identifier family, a new region type — because that is what a privacy review compares
    /// two extractions on.
    /// </summary>
    public const string PolicyVersion = "d36.1";

    /// <summary>
    /// Which build of the pass produced a result (ADD §12.5's "redaction-pass version"). Bump when
    /// the <em>implementation</em> changes without the policy — a kernel, a padding, a detector.
    /// </summary>
    public const string PassVersion = "c054.1";

    /// <summary>How many adjacent words a single identifier may be split across by the engine.</summary>
    /// <remarks>
    /// A NIC prints as <c>900123456 V</c> and often reads as two words; the twelve-digit form is
    /// sometimes grouped in threes. Three is the window that covers both without joining a licence
    /// number to the date beside it.
    /// </remarks>
    private const int MaxWordsPerIdentifier = 3;

    private readonly IImageEditor _editor;
    private readonly IFaceDetector _faces;
    private readonly ILogger<RedactionPipeline> _logger;

    public RedactionPipeline(IImageEditor editor, IFaceDetector faces, ILogger<RedactionPipeline> logger)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _faces = faces ?? throw new ArgumentNullException(nameof(faces));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsArmed => DisarmedReason is null;

    public string? DisarmedReason => (_editor.IsAvailable, _faces.IsAvailable) switch
    {
        (false, _) => "the image editor (OpenCV) is not available",
        (_, false) => "the face detector (OpenCV cascade) is not available",
        _ => null,
    };

    public RedactionOutcome Redact(ReadOnlyMemory<byte> raw, string contentType, OcrPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (DisarmedReason is { } disarmed)
        {
            return RedactionOutcome.Failed(disarmed);
        }

        // The mask boxes come from the on-prem engine. Without them the ID numbers stay legible, so
        // this is a refusal and not a face-blur-only pass.
        if (!page.Succeeded)
        {
            return RedactionOutcome.Failed("the on-prem OCR engine could not read the document, so no ID-number boxes exist");
        }

        if (_editor.Measure(raw) is null)
        {
            return RedactionOutcome.Failed("the upload could not be decoded as an image");
        }

        var faces = _faces.Detect(raw);
        var identifiers = LocateIdentifiers(page);

        var redacted = _editor.Redact(raw, faces, identifiers, contentType);

        if (redacted is not { } bytes)
        {
            return RedactionOutcome.Failed("the redaction pass could not re-encode the document");
        }

        var document = new RedactedDocument(
            bytes,
            contentType == "image/jpeg" ? contentType : "image/png",
            Hash(raw.Span),
            Hash(bytes.Span),
            faces.Count,
            identifiers.Count,
            PolicyVersion,
            PassVersion);

        _logger.LogDebug(
            "Redacted a document under policy {Policy}/{Pass}: {Faces} face region(s) blurred, {Ids} identifier "
            + "box(es) masked.",
            PolicyVersion, PassVersion, faces.Count, identifiers.Count);

        return RedactionOutcome.Redacted(document);
    }

    /// <summary>
    /// The boxes of every NIC and licence number on the page (ADD §12.5's "regex-detected ID
    /// number … the pixels in those boxes are blacked out").
    /// </summary>
    /// <remarks>
    /// Runs over single words and over runs of up to three adjacent ones, because an engine splits
    /// <c>900123456 V</c> into two and neither half matches on its own. The union of the matched
    /// words' boxes is masked, not the merged run's bounding box, so a match that spans a line break
    /// does not black out everything between the two lines.
    /// </remarks>
    private static IReadOnlyList<PixelRegion> LocateIdentifiers(OcrPage page)
    {
        var words = page.Words;
        var masked = new HashSet<int>();

        for (var start = 0; start < words.Count; start++)
        {
            for (var length = 1; length <= MaxWordsPerIdentifier && start + length <= words.Count; length++)
            {
                var run = words.Skip(start).Take(length).ToArray();

                if (length > 1 && !OnSameLine(run))
                {
                    break;
                }

                if (!IdentifierPatterns.IsIdentifier(string.Concat(run.Select(word => word.Text))))
                {
                    continue;
                }

                for (var index = start; index < start + length; index++)
                {
                    masked.Add(index);
                }

                break;
            }
        }

        return [.. masked
            .OrderBy(index => index)
            .Select(index => new PixelRegion(
                words[index].Left, words[index].Top, words[index].Width, words[index].Height))];
    }

    /// <summary>Whether a run of words sits on one line — vertical overlap of more than half.</summary>
    private static bool OnSameLine(IReadOnlyList<OcrWord> run)
    {
        var first = run[0];

        return run.All(word =>
        {
            var overlap = Math.Min(first.Bottom, word.Bottom) - Math.Max(first.Top, word.Top);

            return overlap * 2 > Math.Min(first.Height, word.Height);
        });
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
