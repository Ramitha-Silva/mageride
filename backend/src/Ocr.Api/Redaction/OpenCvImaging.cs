using MageRide.Ocr.Configuration;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace MageRide.Ocr.Redaction;

/// <summary>
/// Whether OpenCV's native half is loadable in this process, decided once.
/// </summary>
/// <remarks>
/// <c>OpenCvSharp</c> initialises its P/Invoke surface in a static constructor, so a missing
/// <c>libOpenCvSharpExtern.so</c> — or a missing <c>libgtk-3.so.0</c> underneath it — surfaces as a
/// <see cref="TypeInitializationException"/> from the first OpenCV call anywhere, not as a
/// <c>DllNotFoundException</c> from the call you were making. Probing once, in one place, is what
/// keeps that from being diagnosed twelve times.
/// </remarks>
internal static class OpenCvRuntime
{
    private static readonly Lazy<bool> Loadable = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsAvailable => Loadable.Value;

    private static bool Probe()
    {
        try
        {
            using var probe = new Mat(1, 1, MatType.CV_8UC1, Scalar.All(0));

            return probe.Total() == 1;
        }
        catch (Exception exception) when (exception is TypeInitializationException or DllNotFoundException
                                              or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}

/// <summary>
/// The raster half of D-36's pre-pass, on OpenCV — the library ADD §12.5 names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Faces are blurred, identity numbers are blacked out, and the difference is deliberate.</b> A
/// blur leaves a document that still reads as a document — Gemini can see that there is a portrait
/// where a portrait belongs, which is what stops it hallucinating a field into the space — while
/// removing the biometric. An ID number cannot be blurred, because a strong enough blur is still
/// invertible for a nine-character string over a known alphabet at a known position; it is filled.
/// </para>
/// <para>
/// <b>The blur kernel is derived from the region, not configured.</b> A fixed 31×31 kernel is
/// destructive on a passport-sized portrait and nearly transparent on a full-page scan. It is a
/// fraction of the shorter side, forced odd, with a floor — so the same setting redacts a 480 px
/// phone photograph and a 4 000 px scan to the same degree.
/// </para>
/// </remarks>
public sealed class OpenCvImageEditor : IImageEditor
{
    private readonly OcrOptions _options;
    private readonly ILogger<OpenCvImageEditor> _logger;

    public OpenCvImageEditor(IOptions<OcrOptions> options, ILogger<OpenCvImageEditor> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!OpenCvRuntime.IsAvailable)
        {
            _logger.LogError(
                "OpenCV's native library could not be loaded, so the D-36 redaction pre-pass cannot run and "
                + "NOTHING will be sent to Gemini. Every document falls back to on-prem Tesseract and a "
                + "Verification Officer. Install libgtk-3-0t64 and libatomic1 (see backend/src/Ocr.Api/CLAUDE.md).");
        }
    }

    public bool IsAvailable => OpenCvRuntime.IsAvailable;

    public ImageSize? Measure(ReadOnlyMemory<byte> image)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            using var decoded = Decode(image);

            return decoded is null ? null : new ImageSize(decoded.Width, decoded.Height);
        }
        catch (OpenCVException)
        {
            return null;
        }
    }

    public ReadOnlyMemory<byte>? Redact(
        ReadOnlyMemory<byte> image,
        IReadOnlyList<PixelRegion> blur,
        IReadOnlyList<PixelRegion> mask,
        string contentType)
    {
        ArgumentNullException.ThrowIfNull(blur);
        ArgumentNullException.ThrowIfNull(mask);

        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            using var decoded = Decode(image);

            if (decoded is null)
            {
                return null;
            }

            foreach (var region in blur)
            {
                Blur(decoded, region);
            }

            foreach (var region in mask)
            {
                Fill(decoded, region);
            }

            // PNG, whatever came in. The pre-pass is the last thing that touches these pixels before
            // they leave the perimeter, and a second JPEG generation over a blacked-out box leaves
            // ringing around its edges — faint, but a reconstruction of the glyph that was there.
            var extension = contentType == "image/jpeg" && _options.Redaction.PreserveJpeg ? ".jpg" : ".png";

            if (!Cv2.ImEncode(extension, decoded, out var encoded))
            {
                return null;
            }

            return encoded;
        }
        catch (OpenCVException exception)
        {
            _logger.LogWarning(exception, "OpenCV refused a document during the redaction pre-pass.");

            return null;
        }
    }

    private void Blur(Mat image, PixelRegion region)
    {
        var rect = Clamp(region, image.Width, image.Height);

        if (rect is not { Width: > 1, Height: > 1 })
        {
            return;
        }

        using var roi = new Mat(image, rect.Value);

        var kernel = KernelFor(rect.Value);

        Cv2.GaussianBlur(roi, roi, new Size(kernel, kernel), 0);
    }

    private static void Fill(Mat image, PixelRegion region)
    {
        var rect = Clamp(region, image.Width, image.Height);

        if (rect is null)
        {
            return;
        }

        Cv2.Rectangle(image, rect.Value, Scalar.Black, -1);
    }

    /// <summary>A fraction of the region's shorter side, forced odd — GaussianBlur requires it.</summary>
    private int KernelFor(Rect rect)
    {
        var shorter = Math.Min(rect.Width, rect.Height);
        var kernel = Math.Max(_options.Redaction.MinimumBlurKernel, shorter / _options.Redaction.BlurDivisor);

        return kernel % 2 == 0 ? kernel + 1 : kernel;
    }

    /// <summary>
    /// Trims a region to the image, growing it by the configured padding first.
    /// </summary>
    /// <remarks>
    /// The padding is why this is not a plain intersection: a bounding box from a detector or from
    /// Tesseract sits tight against the glyphs, and a mask that tight leaves the ascenders and the
    /// first pixel column of a digit readable at the edges.
    /// </remarks>
    private static Rect? Clamp(PixelRegion region, int width, int height)
    {
        const int Padding = 2;

        var left = Math.Clamp(region.Left - Padding, 0, Math.Max(0, width - 1));
        var top = Math.Clamp(region.Top - Padding, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(region.Left + region.Width + Padding, left, width);
        var bottom = Math.Clamp(region.Top + region.Height + Padding, top, height);

        return right - left <= 0 || bottom - top <= 0 ? null : new Rect(left, top, right - left, bottom - top);
    }

    private static Mat? Decode(ReadOnlyMemory<byte> image)
    {
        if (image.IsEmpty)
        {
            return null;
        }

        var decoded = Cv2.ImDecode(image.ToArray(), ImreadModes.Color);

        if (decoded.Empty())
        {
            decoded.Dispose();
            return null;
        }

        return decoded;
    }
}
