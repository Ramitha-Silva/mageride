using MageRide.Registry.Configuration;
using MageRide.Registry.Domain;
using MageRide.Registry.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Registry.Onboarding;

/// <summary>
/// E-03's document-expiry tracker: emits <c>document.expiring</c> at T−30 d / T−7 d / T−1 d, and
/// on expiry emits <c>document.expired</c> and takes the affected vehicles out of dispatch.
/// </summary>
/// <remarks>
/// <para>
/// <b>The notice ledger is what makes this a nightly job rather than a nightly nuisance.</b> Every
/// emission writes <c>registry.document_notices</c> in the same transaction as the outbox row, so
/// a sweep that runs twice, a process that dies mid-batch and a second replica all converge on
/// "each reminder exactly once" (migration 0312). That is also why the interval can be an hour:
/// the passes that find nothing cost one indexed query.
/// </para>
/// <para>
/// <b>Suspension is per vehicle even though E-03 says "driver".</b> <c>dispatch_state</c> lives on
/// <c>registry.vehicles</c>, so a vehicle document suspends its own vehicle and a vehicle-less
/// driving licence suspends every vehicle that driver owns — which is the same set, expressed in
/// the column the specs actually give it.
/// </para>
/// </remarks>
public sealed class DocumentExpiryWorker(
    IServiceProvider services,
    IOptions<RegistryOptions> options,
    TimeProvider clock,
    ILogger<DocumentExpiryWorker> logger) : BackgroundService
{
    private readonly RegistryOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Document-expiry tracker sweeping registry.documents every {Interval} (E-03)", _options.DocumentExpiryInterval);

        using var ticker = new PeriodicTimer(_options.DocumentExpiryInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A sweep that throws must not take the worker with it. Nothing was recorded for
                // the documents it did not reach, so the next tick picks them up unchanged.
                logger.LogError(ex, "Document-expiry sweep failed; retrying on the next tick");
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

    /// <summary>One sweep. Exposed so a test can drive E-03 without waiting on the ticker.</summary>
    /// <returns>How many notices were emitted.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var connectionFactory = scope.ServiceProvider.GetRequiredService<INpgsqlConnectionFactory>();
        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var vehicles = scope.ServiceProvider.GetRequiredService<IVehicleRepository>();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        var now = clock.GetUtcNow();
        IReadOnlyList<DueDocumentNotice> due;

        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            // Autocommit and read-only: the work list is a query, and the claim that matters is
            // the notice row each document's own transaction inserts below.
            due = await documents.ClaimDueNoticesAsync(
                connection, null, now, _options.DocumentExpiryBatchSize, cancellationToken);
        }

        var emitted = 0;

        foreach (var notice in due)
        {
            emitted += await EmitAsync(unitOfWorkFactory, documents, vehicles, outbox, notice, cancellationToken) ? 1 : 0;
        }

        if (emitted > 0)
        {
            logger.LogInformation("Document-expiry sweep emitted {Count} of {Due} due notices", emitted, due.Count);
        }

        return emitted;
    }

    /// <summary>
    /// One document's notice: the ledger row, the status change, the suspensions and the events,
    /// in one transaction.
    /// </summary>
    /// <remarks>
    /// Per document rather than per batch, so one vehicle whose suspension fails cannot roll back
    /// notices already earned by the rest of the batch. The ledger insert comes first and its
    /// result decides whether anything else happens — that is what makes a second replica sweeping
    /// the same second a no-op instead of a duplicate push.
    /// </remarks>
    private async Task<bool> EmitAsync(
        IUnitOfWorkFactory unitOfWorkFactory,
        IDocumentRepository documents,
        IVehicleRepository vehicles,
        IOutboxWriter outbox,
        DueDocumentNotice notice,
        CancellationToken cancellationToken)
    {
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        if (!await documents.RecordNoticeAsync(
                unitOfWork.Connection, unitOfWork.Transaction, notice.DocumentId, notice.ThresholdDays, cancellationToken))
        {
            return false;
        }

        await documents.SetStatusAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            notice.DocumentId,
            notice.IsExpired ? DocumentStatuses.Expired : DocumentStatuses.Expiring,
            cancellationToken);

        var events = new List<OutboxRecord>(Math.Max(notice.VehicleIds.Length, 1));

        foreach (var vehicleId in notice.VehicleIds)
        {
            if (notice.IsExpired)
            {
                await vehicles.SetDispatchStateAsync(
                    unitOfWork.Connection, unitOfWork.Transaction, vehicleId, DispatchStates.Suspended, cancellationToken);
            }

            events.Add(OnboardingEvents.DocumentExpiry(notice, vehicleId));
        }

        // A driver whose licence lapses before they have onboarded a vehicle has nothing to
        // suspend and still has to be told, so the notice goes out keyed by them.
        if (events.Count == 0)
        {
            events.Add(OnboardingEvents.DocumentExpiry(notice, null));
        }

        await outbox.WriteAsync(unitOfWork, events, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Document {DocumentId} ({Kind}) expires at {ExpiresAt}: emitted the T-{ThresholdDays}d notice for " +
            "{VehicleCount} vehicle(s), suspended {Suspended} (E-03)",
            notice.DocumentId,
            notice.Kind,
            notice.ExpiresAt,
            notice.ThresholdDays,
            notice.VehicleIds.Length,
            notice.IsExpired);

        return true;
    }
}
