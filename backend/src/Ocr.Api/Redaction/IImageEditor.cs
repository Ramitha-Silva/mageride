namespace MageRide.Ocr.Redaction;

/// <summary>The size of a decoded image, when the editor could decode it.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// The raster half of the D-36 pre-pass: decode, blur a region, black out a region, re-encode.
/// </summary>
/// <remarks>
/// <para>
/// A port rather than a direct OpenCV call so the pipeline's rules — which regions, in what order,
/// and what happens when a step cannot run — are testable without a native library, and so a
/// deployment without one degrades in the direction D-36 requires rather than crashing.
/// </para>
/// <para>
/// <b>Nothing here throws for a bad image.</b> An <see cref="ImageSize"/> of <c>null</c> from
/// <see cref="Measure"/> means "these bytes are not an image I can work on", which the pipeline
/// turns into a document nobody sends anywhere.
/// </para>
/// </remarks>
public interface IImageEditor
{
    /// <summary>Whether the editor can run — the native half loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>The image's dimensions, or <see langword="null"/> when it cannot be decoded.</summary>
    ImageSize? Measure(ReadOnlyMemory<byte> image);

    /// <summary>
    /// Blurs <paramref name="blur"/> and fills <paramref name="mask"/> with black, then re-encodes.
    /// </summary>
    /// <returns>The redacted bytes, or <see langword="null"/> when the image could not be worked on.</returns>
    ReadOnlyMemory<byte>? Redact(
        ReadOnlyMemory<byte> image,
        IReadOnlyList<PixelRegion> blur,
        IReadOnlyList<PixelRegion> mask,
        string contentType);
}

/// <summary>Finds the faces the pre-pass blurs (ADD §12.5, "OpenCV face-blur").</summary>
/// <remarks>
/// A port for the same reason as <see cref="IImageEditor"/>, and one more: the detector is a trained
/// model, so what this service can be held to is that <em>whatever it returns is blurred</em> and
/// that <em>an unavailable detector stops the Gemini call</em>. Both are properties of the pipeline
/// and both are asserted; the model's own recall is OpenCV's.
/// </remarks>
public interface IFaceDetector
{
    /// <summary>Whether the detector can run — native half loaded and cascade file present.</summary>
    bool IsAvailable { get; }

    /// <summary>Every face region on the image. Empty is a valid answer for a document with none.</summary>
    IReadOnlyList<PixelRegion> Detect(ReadOnlyMemory<byte> image);
}
