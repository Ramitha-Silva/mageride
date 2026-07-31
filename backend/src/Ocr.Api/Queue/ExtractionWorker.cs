using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Pipeline;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Queue;

/// <summary>
/// Drains <see cref="ExtractionQueue"/> through <see cref="IExtractionPipeline"/> (ADD §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every job completes, including the ones that fail.</b> The caller is waiting on a
/// <see cref="TaskCompletionSource{TResult}"/>; a worker that threw and left it unset would hang a
/// request until its own timeout, and the log would say nothing about which document did it. An
/// unexpected exception is logged and answered <see cref="ExtractionResult.Unavailable"/>, which is
/// the same answer a document nobody could read gets.
/// </para>
/// <para>
/// <b>The job's own timeout is D6' §8.3's OCR budget</b> and covers the whole pass — fetch,
/// Tesseract, redaction, Gemini and the row. The per-hop timeouts underneath it are smaller so the
/// slow one can be named in the log.
/// </para>
/// </remarks>
internal sealed class ExtractionWorker : BackgroundService
{
    private readonly ExtractionQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly OcrOptions _options;
    private readonly ILogger<ExtractionWorker> _logger;

    public ExtractionWorker(
        ExtractionQueue queue,
        IServiceScopeFactory scopes,
        IOptions<OcrOptions> options,
        ILogger<ExtractionWorker> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable
            .Range(0, _options.Queue.Workers)
            .Select(index => Task.Run(() => RunAsync(index, stoppingToken), CancellationToken.None));

        _logger.LogInformation(
            "The extraction worker pool is running with {Workers} worker(s), a queue of {Capacity} and a "
            + "{Timeout} per-document budget (D6' §8.3).",
            _options.Queue.Workers, _options.Queue.Capacity, _options.Queue.JobTimeout);

        await Task.WhenAll(workers);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Refuse new work first, so the loops end when the queue drains rather than being cancelled
        // mid-document and leaving a caller's task unset.
        _queue.Complete();

        await base.StopAsync(cancellationToken);
    }

    private async Task RunAsync(int index, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Extraction worker {Worker} stopped.", index);
        }
    }

    private async Task ProcessAsync(ExtractionJob job, CancellationToken stoppingToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(_options.Queue.JobTimeout);

        try
        {
            // A scope per document: the pipeline's repository takes the scoped connection factory,
            // and a singleton worker holding one for the process's lifetime is how a pool leaks.
            await using var scope = _scopes.CreateAsyncScope();

            var pipeline = scope.ServiceProvider.GetRequiredService<IExtractionPipeline>();

            job.Completion.TrySetResult(await pipeline.RunAsync(job.Request, budget.Token));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Extraction of upload {UploadId} ({Kind}) exceeded the {Timeout} budget and was abandoned. "
                + "The document goes to a Verification Officer.",
                job.Request.UploadId, job.Request.Kind, _options.Queue.JobTimeout);

            job.Completion.TrySetResult(ExtractionResult.Unavailable);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Extraction of upload {UploadId} ({Kind}) failed unexpectedly. The document goes to a "
                + "Verification Officer rather than stopping the caller.",
                job.Request.UploadId, job.Request.Kind);

            job.Completion.TrySetResult(ExtractionResult.Unavailable);
        }
    }
}

/// <summary>Puts a document on the queue and waits for its verdict.</summary>
public interface IExtractionDispatcher
{
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed class ExtractionDispatcher : IExtractionDispatcher
{
    private readonly ExtractionQueue _queue;
    private readonly OcrOptions _options;
    private readonly ILogger<ExtractionDispatcher> _logger;

    public ExtractionDispatcher(
        ExtractionQueue queue, IOptions<OcrOptions> options, ILogger<ExtractionDispatcher> logger)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var job = new ExtractionJob(request);

        if (!_queue.TryEnqueue(job))
        {
            _logger.LogWarning(
                "The extraction queue is full ({Capacity}); upload {UploadId} was refused rather than made to "
                + "wait out its caller's budget.",
                _options.Queue.Capacity, request.UploadId);

            return ExtractionResult.Unavailable;
        }

        // The worker's own budget is shorter, so this is a backstop against a worker that never
        // ran at all — a queue accepted during shutdown, for instance.
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_options.Queue.JobTimeout + TimeSpan.FromSeconds(5));

        try
        {
            return await job.Completion.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Nothing picked up upload {UploadId} within the queue budget.", request.UploadId);

            return ExtractionResult.Unavailable;
        }
    }
}
