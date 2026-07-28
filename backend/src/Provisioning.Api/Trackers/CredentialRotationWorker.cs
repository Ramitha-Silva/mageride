using MageRide.Provisioning.Configuration;
using MageRide.Provisioning.Persistence;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Provisioning.Trackers;

/// <summary>
/// The 90-day credential rotation (T-02, US-3.5) and the anti-clone evidence prune (T-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rotation is not revocation, and this worker is the reason that distinction has to hold.</b>
/// It mints a replacement for every ACTIVE binding whose <c>rotates_at</c> has arrived — 14 days
/// before expiry by default — and leaves the outgoing credential valid until its own
/// <c>expires_at</c>. A sweep that revoked as it rotated would take every tracker that happened to
/// be out of GSM coverage off the air, which is the population least able to come back and collect
/// a new credential.
/// </para>
/// <para>
/// <b>The replacement is not published anywhere.</b> The event this writes names the outgoing and
/// incoming serials and stops there; the secret half is returned only to a caller of
/// <c>POST /v1/internal/trackers/{imei}/rotate</c>, over mTLS, once. Putting it on
/// <c>provisioning.events</c> so the downlink relay could push it to the device would put 100,000
/// device secrets on a topic with a week's retention, and D6' §4.2's "downlink
/// <c>revokeCredential</c> cmd" is the *instruction* to re-enrol rather than the delivery of the
/// credential itself.
/// </para>
/// <para>
/// Rows are claimed <c>FOR UPDATE SKIP LOCKED</c>, so two replicas sweeping at once rotate
/// disjoint sets rather than minting two credentials for one device.
/// </para>
/// </remarks>
public sealed class CredentialRotationWorker(
    IServiceProvider services,
    IOptions<ProvisioningOptions> options,
    TimeProvider clock,
    ILogger<CredentialRotationWorker> logger) : BackgroundService
{
    private readonly ProvisioningOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Credential rotation sweeping prov.tracker_bindings every {Interval} (T-02)", _options.RotationInterval);

        using var ticker = new PeriodicTimer(_options.RotationInterval);

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
            catch (Exception exception)
            {
                // A sweep that throws must not take the worker with it. Nothing was committed for
                // the bindings it did not reach, so the next tick picks them up unchanged.
                logger.LogError(exception, "Credential rotation sweep failed; retrying on the next tick");
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

    /// <summary>One sweep. Exposed so a test can drive rotation without waiting on the ticker.</summary>
    /// <returns>How many credentials were rotated.</returns>
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var bindings = scope.ServiceProvider.GetRequiredService<ITrackerBindingRepository>();
        var sightings = scope.ServiceProvider.GetRequiredService<IImeiSightingRepository>();
        var trackers = (TrackerService)scope.ServiceProvider.GetRequiredService<ITrackerService>();

        var now = clock.GetUtcNow();

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var due = await bindings.ClaimRotationDueAsync(
            unitOfWork.Connection, unitOfWork.Transaction, now, _options.RotationBatchSize, cancellationToken);

        foreach (var binding in due)
        {
            await trackers.RotateBindingAsync(unitOfWork, binding, cancellationToken);
        }

        // Evidence that can no longer prove anything: a sighting older than the window is outside
        // every comparison the T-08 rule makes. Pruned here rather than by a retention policy so
        // the table's size is bounded by the rule that fills it.
        var pruned = await sightings.PruneAsync(
            unitOfWork.Connection, unitOfWork.Transaction, now - _options.AntiCloneWindow, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        if (due.Count > 0 || pruned > 0)
        {
            logger.LogInformation(
                "Rotation sweep renewed {Rotated} credential(s) and pruned {Pruned} stale IMEI sighting(s)",
                due.Count,
                pruned);
        }

        return due.Count;
    }
}
