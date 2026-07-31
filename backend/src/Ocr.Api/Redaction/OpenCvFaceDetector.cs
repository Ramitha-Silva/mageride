using MageRide.Ocr.Configuration;
using Microsoft.Extensions.Options;
using OpenCvSharp;

namespace MageRide.Ocr.Redaction;

/// <summary>
/// ADD §12.5's "OpenCV face-blur", detection half — a Haar cascade over the greyscale document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The cascade is a deployment asset, not a checked-in one.</b> It is OpenCV's own trained
/// classifier and ships with the library (Debian/Ubuntu <c>opencv-data</c>, Alpine
/// <c>opencv-dev</c>); vendoring a 900 KB XML into this repository would fork it from the OpenCV
/// build that reads it. <see cref="OcrOptions.RedactionOptions.FaceCascadePath"/> names it, and the
/// well-known locations are probed when it is unset.
/// </para>
/// <para>
/// <b>Missing cascade ⇒ unavailable ⇒ no Gemini call.</b> Not "no faces found". The two are
/// indistinguishable from the outside and only one of them is safe, which is the whole reason
/// <see cref="IFaceDetector.IsAvailable"/> exists separately from an empty result.
/// </para>
/// <para>
/// <b>Equalised, and scaled down before detection.</b> A licence photograph is small, dark and
/// often lit from one side; <c>EqualizeHist</c> is what OpenCV's own sample does and it changes the
/// recall on exactly that material. The downscale bounds the work: a 4 000 px scan detected at full
/// resolution costs seconds, and a face on a document is never smaller than the
/// <c>MinimumFaceFraction</c> of the page this uses as its minimum size.
/// </para>
/// </remarks>
public sealed class OpenCvFaceDetector : IFaceDetector, IDisposable
{
    /// <summary>Where OpenCV's own packages put the cascades, in the order they are tried.</summary>
    private static readonly string[] WellKnownCascadeDirectories =
    [
        "/usr/share/opencv4/haarcascades",
        "/usr/local/share/opencv4/haarcascades",
        "/usr/share/OpenCV/haarcascades",
        "/usr/local/share/OpenCV/haarcascades",
    ];

    private const string CascadeFileName = "haarcascade_frontalface_default.xml";

    private readonly OcrOptions _options;
    private readonly ILogger<OpenCvFaceDetector> _logger;
    private readonly Lock _gate = new();
    private readonly Lazy<CascadeClassifier?> _cascade;

    public OpenCvFaceDetector(IOptions<OcrOptions> options, ILogger<OpenCvFaceDetector> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cascade = new Lazy<CascadeClassifier?>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => OpenCvRuntime.IsAvailable && _cascade.Value is not null;

    public IReadOnlyList<PixelRegion> Detect(ReadOnlyMemory<byte> image)
    {
        if (_cascade.Value is not { } cascade || image.IsEmpty)
        {
            return [];
        }

        try
        {
            using var decoded = Cv2.ImDecode(image.ToArray(), ImreadModes.Grayscale);

            if (decoded.Empty())
            {
                return [];
            }

            var scale = Math.Max(1.0, decoded.Width / (double)_options.Redaction.DetectionWidth);

            using var scaled = scale > 1.0
                ? decoded.Resize(new Size((int)(decoded.Width / scale), (int)(decoded.Height / scale)))
                : decoded.Clone();

            Cv2.EqualizeHist(scaled, scaled);

            var minimum = (int)(Math.Min(scaled.Width, scaled.Height) * _options.Redaction.MinimumFaceFraction);

            Rect[] faces;

            // CascadeClassifier is not thread-safe and the worker pool runs several documents at
            // once; one classifier behind a lock beats one per document, which reloads a 900 KB XML
            // on every page.
            lock (_gate)
            {
                faces = cascade.DetectMultiScale(
                    scaled,
                    scaleFactor: 1.1,
                    minNeighbors: 4,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(Math.Max(16, minimum), Math.Max(16, minimum)));
            }

            return [.. faces.Select(face => new PixelRegion(
                (int)(face.X * scale), (int)(face.Y * scale), (int)(face.Width * scale), (int)(face.Height * scale)))];
        }
        catch (OpenCVException exception)
        {
            // An image OpenCV refuses is one no face was found on, and the pipeline still masks the
            // ID numbers — but the pass reports it, because "no faces" and "could not look" are the
            // same on the wire and are not the same thing.
            _logger.LogWarning(exception, "OpenCV refused a document during face detection.");

            return [];
        }
    }

    private CascadeClassifier? Load()
    {
        if (!OpenCvRuntime.IsAvailable)
        {
            return null;
        }

        var path = ResolvePath();

        if (path is null)
        {
            _logger.LogError(
                "No OpenCV face cascade could be found (tried Ocr:Redaction:FaceCascadePath and {Directories}). "
                + "D-36's face blur cannot run, so NOTHING will be sent to Gemini and every document falls back "
                + "to on-prem Tesseract plus a Verification Officer. Install opencv-data, or set the path.",
                string.Join(", ", WellKnownCascadeDirectories));

            return null;
        }

        try
        {
            var cascade = new CascadeClassifier(path);

            if (cascade.Empty())
            {
                cascade.Dispose();

                _logger.LogError("The face cascade at {Path} loaded empty; D-36's face blur cannot run.", path);

                return null;
            }

            _logger.LogInformation("D-36 face blur is armed from the cascade at {Path}.", path);

            return cascade;
        }
        catch (Exception exception) when (exception is OpenCVException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "The face cascade at {Path} could not be loaded.", path);

            return null;
        }
    }

    private string? ResolvePath()
    {
        var configured = _options.Redaction.FaceCascadePath;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            // A configured path that does not exist is a misconfiguration, not a reason to silently
            // use a different file than the operator asked for.
            return File.Exists(configured) ? configured : null;
        }

        return WellKnownCascadeDirectories
            .Select(directory => Path.Combine(directory, CascadeFileName))
            .FirstOrDefault(File.Exists);
    }

    public void Dispose()
    {
        if (_cascade.IsValueCreated)
        {
            _cascade.Value?.Dispose();
        }
    }
}
