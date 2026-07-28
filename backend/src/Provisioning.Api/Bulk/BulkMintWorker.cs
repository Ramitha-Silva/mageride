using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Persistence;
using MageRide.Provisioning.Trackers;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Bulk;

/// <summary>
/// Drains the credential-mint queue a bulk job leaves behind (T-09, NFR-43: 5,000 rows ≤ 5 min).
/// </summary>
/// <remarks>
/// <para>
/// <b>Each row goes through the ordinary bind path.</b> Not a bulk-only shortcut: the anti-clone
/// rule, the ownership check, the outbox event and the cache prime are the same code a single
/// <c>POST /v1/trackers/bind</c> runs, so a fleet cannot be onboarded into a state the one-at-a-time
/// endpoint would have refused. A row that fails records the same kebab error code the HTTP API
/// would have returned, which is what makes the report speak the operator's vocabulary.
/// </para>
/// <para>
/// <b>It binds as the operator who submitted the job, not as an admin.</b> Re-checking fleet
/// membership per row costs two indexed queries and buys something worth having: an operator whose
/// access is revoked halfway through a 5,000-row upload stops binding at the row where it
/// happened, rather than finishing on an authority they no longer hold.
/// </para>
/// <para>
/// Rows and jobs are both claimed <c>FOR UPDATE SKIP LOCKED</c>, so adding replicas adds
/// throughput instead of contention.
/// </para>
/// </remarks>
public sealed class BulkMintWorker(
    IServiceProvider services,
    IOptions<ProvisioningOptions> options,
    TimeProvider clock,
    ILogger<BulkMintWorker> logger) : BackgroundService
{
    private readonly ProvisioningOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Bulk tracker mint worker polling every {Interval} (T-09)", _options.BulkMintInterval);

        using var ticker = new PeriodicTimer(_options.BulkMintInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Loops until the queue is empty rather than doing one batch per tick: the tick is
                // how quickly a *new* job starts, and NFR-43's budget is about how quickly a
                // started one finishes.
                while (await DrainOnceAsync(stoppingToken) > 0)
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Bulk mint pass failed; retrying on the next tick");
            }

            try
            {
                await ticker.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Claims one job and mints up to a batch of its rows.
    /// </summary>
    /// <returns>How many rows were attempted; 0 when there was nothing to do.</returns>
    internal async Task<int> DrainOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var jobs = scope.ServiceProvider.GetRequiredService<IBulkJobRepository>();
        var trackers = scope.ServiceProvider.GetRequiredService<ITrackerService>();

        await using var claim = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var job = await jobs.ClaimNextProcessingAsync(claim.Connection, claim.Transaction, cancellationToken);

        if (job is null)
        {
            await claim.RollbackAsync(cancellationToken);
            return 0;
        }

        var rows = await jobs.ClaimPendingRowsAsync(
            claim.Connection, claim.Transaction, job.Id, _options.BulkMintBatchSize, cancellationToken);

        if (rows.Count == 0)
        {
            // Nothing pending but the job still says PROCESSING — a worker died between binding a
            // row and recording it, or the last batch has only just landed. The recount is what
            // finishes it.
            await jobs.RecountAsync(claim.Connection, claim.Transaction, job.Id, clock.GetUtcNow(), cancellationToken);
            await claim.CommitAsync(cancellationToken);

            return 0;
        }

        // The claim is released before any binding is minted. Holding it across 50 binds would keep
        // one transaction open for the whole batch, and a bind opens its own — nesting the two on
        // one pooled connection is how a service deadlocks itself under load.
        await claim.RollbackAsync(cancellationToken);

        var outcomes = new List<RowOutcome>(rows.Count);

        foreach (var row in rows)
        {
            outcomes.Add(await MintAsync(trackers, job, row, cancellationToken));
        }

        await using var record = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        foreach (var outcome in outcomes)
        {
            await jobs.CompleteRowAsync(
                record.Connection,
                record.Transaction,
                job.Id,
                outcome.RowNumber,
                outcome.Status,
                outcome.BindingId,
                outcome.ErrorCode,
                outcome.ErrorDetail,
                cancellationToken);
        }

        var recounted = await jobs.RecountAsync(
            record.Connection, record.Transaction, job.Id, clock.GetUtcNow(), cancellationToken);

        await record.CommitAsync(cancellationToken);

        if (recounted.Status == BulkJobStatuses.Completed)
        {
            logger.LogInformation(
                "Bulk job {JobId} finished: {Succeeded} bound, {Failed} failed of {Total}",
                job.Id,
                recounted.SucceededRows,
                recounted.FailedRows,
                recounted.TotalRows);
        }

        return rows.Count;
    }

    private async Task<RowOutcome> MintAsync(
        ITrackerService trackers, BulkJob job, BulkRow row, CancellationToken cancellationToken)
    {
        if (row.VehicleId is not { } vehicleId)
        {
            // Validation resolves every row it passes, so this is unreachable by construction —
            // recorded rather than thrown, because a row that somehow arrived unresolved must not
            // stall the whole job behind it.
            return new RowOutcome(
                row.RowNumber, BulkRowStatuses.Failed, null,
                MageRideErrors.VehicleNotFound.Code, "row reached the minter without a resolved vehicle");
        }

        try
        {
            var bound = await trackers.BindTrackerAsync(
                new BindTrackerCommand(
                    job.RequestedBy,
                    IsAdmin: false,
                    row.Imei,
                    vehicleId.ToString(),
                    BindMethods.Manual,
                    BindCode: null,
                    job.CredentialType,
                    RemoteAddress: null),
                cancellationToken);

            return new RowOutcome(row.RowNumber, BulkRowStatuses.Bound, bound.Binding.Id, null, null);
        }
        catch (MageRideException exception)
        {
            // The same kebab code a single bind would have answered with. `imei-duplicate` here
            // means the T-08 rule fired and both records are held — the row failed, and the fleet
            // has an admin alert waiting for it.
            logger.LogWarning(
                "Bulk job {JobId} row {Row} ({Imei}) failed: {Code}",
                job.Id, row.RowNumber, row.Imei, exception.Error.Code);

            return new RowOutcome(
                row.RowNumber, BulkRowStatuses.Failed, null, exception.Error.Code, exception.Detail);
        }
        catch (Exception exception)
        {
            // Anything else is this service's fault rather than the row's, and it still has to be
            // recorded: a row left PENDING would be re-claimed forever and the job would never
            // finish.
            logger.LogError(
                exception, "Bulk job {JobId} row {Row} ({Imei}) failed unexpectedly", job.Id, row.RowNumber, row.Imei);

            return new RowOutcome(
                row.RowNumber, BulkRowStatuses.Failed, null, MageRideErrors.InternalError.Code, "mint failed");
        }
    }

    private sealed record RowOutcome(
        int RowNumber, string Status, Guid? BindingId, string? ErrorCode, string? ErrorDetail);
}
