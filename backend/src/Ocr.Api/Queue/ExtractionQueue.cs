using System.Threading.Channels;
using MageRide.Ocr.Configuration;
using MageRide.Ocr.Domain;
using Microsoft.Extensions.Options;

namespace MageRide.Ocr.Queue;

/// <summary>One queued document and the caller waiting on it.</summary>
internal sealed class ExtractionJob
{
    public ExtractionJob(ExtractionRequest request)
    {
        Request = request;
        // Run continuations on the thread pool: the worker must not be made to finish the caller's
        // response before it can pick up the next document.
        Completion = new TaskCompletionSource<ExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public ExtractionRequest Request { get; }

    public TaskCompletionSource<ExtractionResult> Completion { get; }
}

/// <summary>
/// The work queue in front of the extraction pass (ADD §6: ocr-svc is "stateless, queue-driven").
/// </summary>
/// <remarks>
/// <para>
/// <b>In process, and bounded.</b> The queue exists to bound concurrency, not to survive a restart:
/// the pass is idempotent — it reads bytes that are still there and writes a new
/// <c>docs.extractions</c> row — and its caller is a synchronous hop with D6' §8.3's 30-second
/// budget, so a document that outlived the process has outlived the request that wanted it. A
/// durable queue would deliver a result to a caller that stopped waiting, which is a
/// <c>pending_review</c> that resolves itself hours later with nothing to tell the driver.
/// </para>
/// <para>
/// <b>Full is a refusal, not a wait.</b> Every waiting document is a caller holding a request open;
/// past the capacity the honest answer is that this service cannot take it, which registry-svc turns
/// into a saved step and a Verification Officer.
/// </para>
/// </remarks>
internal sealed class ExtractionQueue
{
    private readonly Channel<ExtractionJob> _channel;

    public ExtractionQueue(IOptions<OcrOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _channel = Channel.CreateBounded<ExtractionJob>(new BoundedChannelOptions(options.Value.Queue.Capacity)
        {
            // DropWrite would silently discard the newest document; the writer checks its own
            // return value instead and the caller is told.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Queues a document. False when the queue is full.</summary>
    public bool TryEnqueue(ExtractionJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<ExtractionJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Stops accepting work, so a shutdown drains rather than abandons.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
