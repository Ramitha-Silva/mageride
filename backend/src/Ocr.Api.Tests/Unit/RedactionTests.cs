using MageRide.Ocr.Ocr;
using MageRide.Ocr.Redaction;
using Microsoft.Extensions.Logging.Abstractions;

namespace MageRide.Ocr.Tests.Unit;

/// <summary>Records what it was asked to redact instead of touching pixels.</summary>
internal sealed class RecordingImageEditor : IImageEditor
{
    public bool IsAvailable { get; set; } = true;

    public bool Decodes { get; set; } = true;

    public IReadOnlyList<PixelRegion> Blurred { get; private set; } = [];

    public IReadOnlyList<PixelRegion> Masked { get; private set; } = [];

    public ImageSize? Measure(ReadOnlyMemory<byte> image) =>
        IsAvailable && Decodes ? new ImageSize(720, 400) : null;

    public ReadOnlyMemory<byte>? Redact(
        ReadOnlyMemory<byte> image,
        IReadOnlyList<PixelRegion> blur,
        IReadOnlyList<PixelRegion> mask,
        string contentType)
    {
        if (!IsAvailable || !Decodes)
        {
            return null;
        }

        Blurred = blur;
        Masked = mask;

        // Different bytes, so a caller cannot pass by accidentally returning its input.
        return new byte[] { 1, 2, 3, 4 };
    }
}

internal sealed class StubFaceDetector(params PixelRegion[] faces) : IFaceDetector
{
    public bool IsAvailable { get; set; } = true;

    public IReadOnlyList<PixelRegion> Detect(ReadOnlyMemory<byte> image) => IsAvailable ? faces : [];
}

/// <summary>
/// D-36, ADD §12.5: what the pre-pass masks, and — the half that matters — when it refuses.
/// </summary>
public sealed class RedactionPipelineTests
{
    private static readonly OcrWord Nic = new("199012345678", 230, 250, 420, 40, 0.94m);
    private static readonly OcrWord Licence = new("B1234567", 230, 150, 210, 30, 0.95m);
    private static readonly OcrWord Insurer = new("CEYLINCO", 40, 100, 180, 30, 0.97m);

    private static RedactionPipeline Build(IImageEditor editor, IFaceDetector faces) =>
        new(editor, faces, NullLogger<RedactionPipeline>.Instance);

    [Fact]
    public void Every_detected_face_is_blurred()
    {
        var editor = new RecordingImageEditor();
        var face = new PixelRegion(40, 150, 140, 170);

        var outcome = Build(editor, new StubFaceDetector(face))
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer]));

        Assert.True(outcome.Succeeded);
        Assert.Equal([face], editor.Blurred);
        Assert.Equal(1, outcome.Document!.FacesBlurred);
    }

    [Fact]
    public void Every_identity_number_is_masked_and_ordinary_words_are_not()
    {
        var editor = new RecordingImageEditor();

        var outcome = Build(editor, new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer, Licence, Nic]));

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, outcome.Document!.IdentifiersMasked);
        Assert.Contains(new PixelRegion(Nic.Left, Nic.Top, Nic.Width, Nic.Height), editor.Masked);
        Assert.Contains(new PixelRegion(Licence.Left, Licence.Top, Licence.Width, Licence.Height), editor.Masked);
        Assert.DoesNotContain(new PixelRegion(Insurer.Left, Insurer.Top, Insurer.Width, Insurer.Height), editor.Masked);
    }

    [Fact]
    public void An_identifier_split_across_words_is_still_masked()
    {
        // "901234567 V" is one NIC and two words. Neither half matches on its own, and a pass that
        // only looked at single words would send the number out in the clear.
        var editor = new RecordingImageEditor();

        var digits = new OcrWord("901234567", 230, 250, 380, 40, 0.9m);
        var suffix = new OcrWord("V", 620, 250, 30, 40, 0.9m);

        var outcome = Build(editor, new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [digits, suffix]));

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, editor.Masked.Count);
    }

    [Fact]
    public void Words_on_different_lines_are_never_joined_into_an_identifier()
    {
        // Otherwise a page number under a serial reads as a NIC and a whole band of the document is
        // blacked out — including, on a vehicle photograph, the plate.
        var editor = new RecordingImageEditor();

        var top = new OcrWord("901234567", 230, 100, 380, 40, 0.9m);
        var farBelow = new OcrWord("V", 230, 300, 30, 40, 0.9m);

        Build(editor, new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [top, farBelow]));

        Assert.Empty(editor.Masked);
    }

    [Fact]
    public void An_unavailable_face_detector_refuses_the_whole_pass()
    {
        // Not "blur what we could and go". D-36 says no exceptions, and a pipeline whose compliance
        // depends on whether a library loaded is not one anybody can audit.
        var outcome = Build(new RecordingImageEditor(), new StubFaceDetector { IsAvailable = false })
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer]));

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Document);
        Assert.Contains("face detector", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unavailable_image_editor_refuses_the_whole_pass()
    {
        var outcome = Build(new RecordingImageEditor { IsAvailable = false }, new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer]));

        Assert.False(outcome.Succeeded);
        Assert.Contains("image editor", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unavailable_OCR_engine_refuses_the_pass_even_with_a_working_face_detector()
    {
        // The ID-number boxes come from the engine. Without them the numbers stay legible, so a
        // face-blur-only pass would be a D-36 breach wearing a redaction's name.
        var outcome = Build(new RecordingImageEditor(), new StubFaceDetector(new PixelRegion(0, 0, 10, 10)))
            .Redact(new byte[] { 9 }, "image/png", OcrPage.Unavailable);

        Assert.False(outcome.Succeeded);
        Assert.Contains("OCR engine", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_document_with_no_faces_on_it_still_passes()
    {
        // An insurance certificate has no portrait. "Found none" and "could not look" are the same
        // on the wire and only one of them is safe, which is why availability is checked separately.
        var outcome = Build(new RecordingImageEditor(), new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer]));

        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.Document!.FacesBlurred);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_refused()
    {
        var outcome = Build(new RecordingImageEditor { Decodes = false }, new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Insurer]));

        Assert.False(outcome.Succeeded);
        Assert.Contains("decoded", outcome.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_processing_log_is_filled_in_on_every_pass()
    {
        // ADD §12.5: hash + policy version + redaction-pass version, per extraction.
        var outcome = Build(new RecordingImageEditor(), new StubFaceDetector())
            .Redact(new byte[] { 9 }, "image/png", new OcrPage(true, [Nic]));

        var document = outcome.Document!;

        Assert.Equal(64, document.RawSha256.Length);
        Assert.Equal(64, document.RedactedSha256.Length);
        Assert.NotEqual(document.RawSha256, document.RedactedSha256);
        Assert.Equal(RedactionPipeline.PolicyVersion, document.PolicyVersion);
        Assert.Equal(RedactionPipeline.PassVersion, document.PassVersion);
    }
}
