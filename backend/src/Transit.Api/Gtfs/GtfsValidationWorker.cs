using MageRide.Shared.Persistence;
using MageRide.Transit.Configuration;
using Microsoft.Extensions.Options;

namespace MageRide.Transit.Gtfs;

/// <summary>
/// The nudge an accepted upload gives the validation worker.
/// </summary>
/// <remarks>
/// <b>A latch, not a queue.</b> The durable queue is the <c>uploaded</c> status on the version row
/// — which is what survives this process dying between the 202 and the validation, and what lets a
/// second replica pick the work up. This only removes the wait: without it an upload sits until
/// the next poll, and SCR-AP-016's stepper shows "Uploaded" for ten seconds on a feed that could
/// have been validated immediately.
/// </remarks>
public sealed class GtfsValidationSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Raise()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled; one pending wake-up is enough.
        }
    }

    internal async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        await _signal.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// BR-32.1's validation, run off the request path (D3': "import runs outside the request path").
/// </summary>
/// <remarks>
/// <para>
/// <b>Claimed, not read.</b> <c>ClaimForValidationAsync</c> moves the row to <c>validating</c> in
/// the statement that selects it, under <c>FOR UPDATE SKIP LOCKED</c>, so two replicas running
/// this worker take two different uploads rather than validating one twice — and a feed whose
/// validator died is reclaimed by age rather than sitting at "Validating" for ever.
/// </para>
/// <para>
/// <b>A crash is the only failure that leaves work behind, and it is recoverable.</b> Anything
/// this worker throws is caught and recorded as a <c>failed</c> verdict with the reason in the
/// report: an upload that cannot be read is a fact about the feed, and leaving it at
/// <c>validating</c> would make an operator wait for an answer that is never coming.
/// </para>
/// </remarks>
internal sealed class GtfsValidationWorker(
    IServiceProvider services,
    GtfsValidationSignal signal,
    IOptions<TransitOptions> options,
    TimeProvider clock,
    ILogger<GtfsValidationWorker> logger) : BackgroundService
{
    private readonly TransitOptions.GtfsOptions _options =
        (options ?? throw new ArgumentNullException(nameof(options))).Value.Gtfs;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "GTFS validation worker started: claiming uploads every {Interval}, reclaiming a stalled validation "
            + "after {Stale}.",
            _options.ValidationPollInterval,
            _options.ValidationStaleAfter);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Claiming failed — the database is unreachable, not the feed being bad. The row
                // stays where it is and the next pass tries again.
                logger.LogError(exception, "The GTFS validation pass failed; retrying at the next interval.");
            }

            try
            {
                await signal.WaitAsync(_options.ValidationPollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Validates every claimable upload, then returns.</summary>
    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = services.CreateScope();

            var repository = scope.ServiceProvider.GetRequiredService<IGtfsFeedVersionRepository>();

            var claimed = await repository.ClaimForValidationAsync(_options.ValidationStaleAfter, cancellationToken);

            if (claimed is null)
            {
                return;
            }

            await ValidateAsync(scope.ServiceProvider, repository, claimed, cancellationToken);
        }
    }

    private async Task ValidateAsync(
        IServiceProvider scope,
        IGtfsFeedVersionRepository repository,
        FeedVersionRow version,
        CancellationToken cancellationToken)
    {
        var objects = scope.GetRequiredService<IGtfsObjectStore>();
        var validator = scope.GetRequiredService<IGtfsValidator>();

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        FeedValidationResult result;

        try
        {
            await using var zip = await objects.OpenAsync(version.StorageKey, cancellationToken);

            result = zip is null
                ? Unreadable(
                    FeedIssueCodes.MissingFile,
                    "The uploaded zip is no longer in storage, so the feed could not be validated.")
                : validator.Validate(zip, await repository.ActiveIdentityAsync(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A verdict, not a stuck row: whatever went wrong, it went wrong reading *this* feed,
            // and an operator needs to be told so rather than left watching a spinner.
            logger.LogError(exception, "Validating GTFS feed {FeedVersionId} threw; recording it as failed.", version.FeedVersionId);

            result = Unreadable(FeedIssueCodes.NotAZip, $"The feed could not be read: {exception.Message}");
        }

        await RecordAsync(scope, repository, version, result, cancellationToken);

        logger.LogInformation(
            "GTFS feed {FeedVersionId} validated in {Elapsed}: {Verdict}, {Errors} errors, {Warnings} warnings.",
            version.FeedVersionId,
            System.Diagnostics.Stopwatch.GetElapsedTime(started),
            result.Failed ? FeedStatuses.Failed : FeedStatuses.Validated,
            result.ErrorCount,
            result.WarningCount);
    }

    /// <summary>The verdict and its audit row, in one transaction (D-35).</summary>
    private async Task RecordAsync(
        IServiceProvider scope,
        IGtfsFeedVersionRepository repository,
        FeedVersionRow version,
        FeedValidationResult result,
        CancellationToken cancellationToken)
    {
        var connections = scope.GetRequiredService<INpgsqlConnectionFactory>();
        var audit = scope.GetRequiredService<IGtfsAuditRepository>();

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await repository.CompleteValidationAsync(connection, transaction, version.FeedVersionId, result, cancellationToken);

        await audit.WriteAsync(
            connection,
            transaction,
            // No actor: BR-32.1's pipeline is a queued job and `audit.events.actor_id` is nullable
            // for exactly this. The person who caused it is on the upload row.
            actorId: null,
            GtfsAuditRepository.FeedValidated,
            version.FeedVersionId,
            before: new { status = FeedStatuses.Validating },
            after: new
            {
                status = result.Failed ? FeedStatuses.Failed : FeedStatuses.Validated,
                errors = result.ErrorCount,
                warnings = result.WarningCount,
                feedInfoVersion = result.FeedInfoVersion,
            },
            clock.GetUtcNow(),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static FeedValidationResult Unreadable(string code, string message) => new(
        new FeedValidationReport([new FeedIssue("-", null, code, message)], []),
        ErrorCount: 1,
        WarningCount: 0,
        new Dictionary<string, long>(StringComparer.Ordinal),
        FeedInfoVersion: null,
        ServiceStart: null,
        ServiceEnd: null);
}
