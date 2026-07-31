namespace MageRide.Ocr.Ocr;

/// <summary>One word an OCR engine read, and where on the page it was.</summary>
/// <param name="Confidence">0–1. Tesseract reports 0–100; the adapter rescales.</param>
public sealed record OcrWord(string Text, int Left, int Top, int Width, int Height, decimal Confidence)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;
}

/// <summary>
/// A page of OCR output — the words with their boxes, and the text they make when joined.
/// </summary>
/// <param name="Succeeded">
/// Whether the engine ran at all. A page that ran and found nothing is a <em>successful</em> read of
/// a blank image, which is a different thing from an engine that is not installed: the first is a
/// document nobody can verify, the second is a deployment that cannot redact and must not call
/// Gemini (D-36).
/// </param>
public sealed record OcrPage(bool Succeeded, IReadOnlyList<OcrWord> Words)
{
    /// <summary>The engine could not run. No words, and no permission to leave the perimeter.</summary>
    public static readonly OcrPage Unavailable = new(false, []);

    /// <summary>A read that produced nothing — a blank or unreadable image.</summary>
    public static readonly OcrPage Empty = new(true, []);

    /// <summary>The words joined by line, for the regex passes that work on text rather than boxes.</summary>
    public string Text => string.Join(
        "\n",
        Words
            .GroupBy(word => word.Top / Math.Max(1, word.Height))
            .OrderBy(line => line.Key)
            .Select(line => string.Join(' ', line.OrderBy(word => word.Left).Select(word => word.Text))));

    /// <summary>The mean confidence of the words that carry one, or null for an empty page.</summary>
    public decimal? MeanConfidence => Words.Count == 0 ? null : Words.Average(word => word.Confidence);
}

/// <summary>
/// The on-prem OCR engine (D6' §7.5's Tesseract), used for two different jobs.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is the redaction pass's eyes and the fallback extractor's, and one pass serves both.</b>
/// ADD §12.5 runs Tesseract to find the bounding boxes of the ID numbers to black out; D6' §7.5
/// runs Tesseract to extract fields when Gemini is unavailable. Reading the page twice would double
/// the cost of the slowest step in the pipeline for no new information, so
/// <see cref="ExtractionPipeline"/> reads once and hands the same <see cref="OcrPage"/> to both.
/// </para>
/// <para>
/// <b>An implementation must not throw for an image it could not read.</b> Return
/// <see cref="OcrPage.Unavailable"/>. Everything downstream is written to degrade — an unavailable
/// engine means no redaction, which means no Gemini call, which means the document goes to a
/// Verification Officer. That is the D-36-safe direction and it does not stop a driver onboarding.
/// </para>
/// </remarks>
public interface IOcrEngine
{
    /// <summary>Whether the engine can run at all — the binary is present, the model is there.</summary>
    /// <remarks>Cheap and cached; called on the health probe, not per document.</remarks>
    bool IsAvailable { get; }

    /// <summary>Reads <paramref name="image"/> and returns its words with their boxes.</summary>
    Task<OcrPage> ReadAsync(ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken);
}
