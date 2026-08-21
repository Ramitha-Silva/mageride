using System.Diagnostics;
using MageRide.Ocr.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Ocr;

/// <summary>
/// The on-prem engine, run as a child process (D6' §7.5's Tesseract, ADD §12.5's box source).
/// </summary>
/// <remarks>
/// <para>
/// <b>A process, not a binding.</b> Every managed Tesseract wrapper needs the same
/// <c>libtesseract</c> + <c>libleptonica</c> pair on the host that the CLI does, plus a build of
/// itself that matches them; the CLI's TSV writer is the interface that has been stable across
/// three major versions. The cost is a fork per document, which is nothing beside the OCR itself.
/// </para>
/// <para>
/// <b>The image is written to a private temporary file, and deleted in a finally.</b> Tesseract can
/// read <c>stdin</c>, but only for a single image with no seek — and the file is the same raw
/// document the D-36 pre-pass exists to contain, so it is written under
/// <c>Ocr:Tesseract:WorkRoot</c> (a mount the deployment controls) rather than wherever
/// <c>TMPDIR</c> points, and the process never keeps one after the read.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> A missing binary, a non-zero exit and a timeout all answer
/// <see cref="OcrPage.Unavailable"/>, which fails the pipeline closed: no boxes means no redaction
/// means no Gemini.
/// </para>
/// </remarks>
public sealed class TesseractCliOcrEngine : IOcrEngine, IDisposable
{
    private readonly OcrOptions _options;
    private readonly ILogger<TesseractCliOcrEngine> _logger;
    private readonly Lazy<bool> _available;

    public TesseractCliOcrEngine(IOptions<OcrOptions> options, ILogger<TesseractCliOcrEngine> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _available = new Lazy<bool>(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool IsAvailable => _available.Value;

    public async Task<OcrPage> ReadAsync(
        ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return OcrPage.Unavailable;
        }

        var workRoot = string.IsNullOrWhiteSpace(_options.Tesseract.WorkRoot)
            ? Path.Combine(Path.GetTempPath(), "mageride-ocr")
            : _options.Tesseract.WorkRoot;

        Directory.CreateDirectory(workRoot);

        var path = Path.Combine(workRoot, $"{Guid.NewGuid():N}{Extension(contentType)}");

        try
        {
            await File.WriteAllBytesAsync(path, image.ToArray(), cancellationToken);

            var tsv = await RunAsync(path, _options.Tesseract.PageSegmentationMode, cancellationToken);

            if (tsv is null)
            {
                return OcrPage.Unavailable;
            }

            var words = TesseractTsv.Parse(tsv);

            // A page that came back completely empty is read once more the other way before it is
            // called unreadable — the two segmentation modes fail on different material and a
            // number plate is exactly where the primary one does. See PageSegmentationMode.
            if (words.Count == 0
                && _options.Tesseract.FallbackPageSegmentationMode != _options.Tesseract.PageSegmentationMode)
            {
                var retry = await RunAsync(
                    path, _options.Tesseract.FallbackPageSegmentationMode, cancellationToken);

                if (retry is not null)
                {
                    words = TesseractTsv.Parse(retry);
                }
            }

            return new OcrPage(true, words);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(
                exception,
                "The redaction pre-pass could not stage a document under {WorkRoot}, so no bounding boxes "
                + "were produced: this document goes to Gemini UNREDACTED and has no on-prem fallback "
                + "behind it (Δ MCS-07).",
                workRoot);

            return OcrPage.Unavailable;
        }
        finally
        {
            TryDelete(path);
        }
    }

    private async Task<string?> RunAsync(
        string imagePath, int pageSegmentationMode, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_options.Tesseract.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        start.ArgumentList.Add(imagePath);
        // "stdout" is the output *base name* Tesseract reserves for the stream, not a flag.
        start.ArgumentList.Add("stdout");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(_options.Tesseract.Language);
        start.ArgumentList.Add("--psm");
        start.ArgumentList.Add(pageSegmentationMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("tsv");

        using var process = new Process { StartInfo = start };

        try
        {
            process.Start();
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Tesseract could not be started from {Path}. The on-prem fallback (D6' §7.5) and the D-36 "
                + "redaction pre-pass are both unavailable; no document will be sent to Gemini.",
                _options.Tesseract.ExecutablePath);

            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Tesseract.Timeout);

        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            _logger.LogWarning(
                "Tesseract did not finish within {Timeout}. The document goes to a Verification Officer.",
                _options.Tesseract.Timeout);

            return null;
        }

        var output = await stdout;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "Tesseract exited {Code}: {Error}", process.ExitCode, (await stderr).Trim());

            return null;
        }

        return output;
    }

    private bool Probe()
    {
        try
        {
            var start = new ProcessStartInfo(_options.Tesseract.ExecutablePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("--version");

            using var process = Process.Start(start);

            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit((int)_options.Tesseract.Timeout.TotalMilliseconds))
            {
                Kill(process);
                return false;
            }

            if (process.ExitCode != 0)
            {
                return false;
            }

            _logger.LogInformation(
                "Tesseract is available at {Path} ({Version}).",
                _options.Tesseract.ExecutablePath,
                process.StandardOutput.ReadToEnd().Split('\n').FirstOrDefault()?.Trim());

            return true;
        }
        catch (Exception exception)
        {
            // Loud, and only once — the whole D-36 posture changes when this is false.
            _logger.LogError(
                exception,
                "Tesseract is NOT available at {Path}. The redaction pre-pass cannot locate ID numbers, so "
                + "NOTHING will be sent to Gemini (D-36 fails closed) and every document goes to a Verification "
                + "Officer. Install tesseract-ocr, or point Ocr:Tesseract:ExecutablePath at it.",
                _options.Tesseract.ExecutablePath);

            return false;
        }
    }

    private static string Extension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/tiff" => ".tif",
        "application/pdf" => ".pdf",
        _ => ".jpg",
    };

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // Already gone.
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A raw document left on disk is exactly what NFR-28 and D-36 are about, so this is an
            // error rather than a shrug — but it must not fail the extraction that already ran.
            _logger.LogError(
                exception, "A staged raw document could not be deleted from {Path}. It must not be left there.", path);
        }
    }

    public void Dispose()
    {
        // Nothing owned; the type is IDisposable so the seam can grow one without a registration change.
    }
}
