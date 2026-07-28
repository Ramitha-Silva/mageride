using System.Text.Json;
using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Ride.Rides;
using MageRide.Shared.Http;
using MageRide.Shared.Observability;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Timers;

/// <summary>What one sweep pass did.</summary>
public sealed record RideTimerSweep(int Claimed, int Applied)
{
    /// <summary>Timers that fired and found nothing to do — the normal race, not a fault.</summary>
    public int NoOp => Claimed - Applied;
}

/// <summary>
/// The <c>rides.timers</c> payload of an <c>offline_grace</c> row.
/// </summary>
/// <param name="State">The state the ride was in when the last will arrived.</param>
/// <param name="OfflineSince">
/// When the vehicle went away. Fixed for the life of the outage, so re-planning the window for a
/// new state cannot push the deadline forward indefinitely.
/// </param>
public sealed record OfflineGracePayload(Guid VehicleId, string State, DateTimeOffset OfflineSince);

/// <summary>
/// The R-04 durable backstop: fires the ride aggregate's own timers (<see cref="RideTimerKinds"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The timer is what makes these guarantees real.</b> A rider who never appears, a driver who
/// accepted and drove away, a phone that lost its connection mid-trip — none of them will send a
/// request saying so. §11.12 gives every one of those an outcome, which means the platform has to
/// notice on its own. Rows are claimed <c>FOR UPDATE SKIP LOCKED</c> with a lease, so replicas
/// sweep disjoint sets and a worker killed mid-fire loses a lease rather than a backstop.
/// </para>
/// <para>
/// <b>Nothing here writes ride state directly.</b> Every fire goes through
/// <see cref="IRideCancellationService.TryApplyAsync"/>, so a timer-driven terminal is the same
/// transaction — matrix, audit row, timers, outbox — as a rider tapping Cancel. A bulk
/// <c>UPDATE</c> would be faster and would leave a fleet of cancelled rides no consumer heard
/// about.
/// </para>
/// <para>
/// <b>No Redis is involved anywhere in this path.</b> ride-svc holds no Redis connection at all
/// (<c>UseRedis = false</c>), so the R-04 property — "the durable backstop fires independently of
/// any Redis TTL" — is structural here rather than tested: there is no cache to flush.
/// </para>
/// </remarks>
public sealed class RideTimerWorker(
    IServiceProvider services,
    IOptions<RideOptions> options,
    TimeProvider timeProvider,
    ILogger<RideTimerWorker> logger) : BackgroundService
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Ride timer sweep running every {Interval}: arrival grace {Arrival}, rider no-show {NoShow}, " +
            "payment watch {Payment}",
            _options.TimerInterval, _options.ArrivalGrace, _options.RiderNoShowGrace, _options.PaymentPendingGrace);

        using var ticker = new PeriodicTimer(_options.TimerInterval);

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
                // A pass that throws must not take the worker with it. Every timer it did not reach
                // is still unfired and still due, so the next tick picks it up unchanged.
                logger.LogError(exception, "Ride timer sweep failed; retrying on the next tick");
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

    /// <summary>One pass. Exposed so a test can drive the backstops without waiting on the ticker.</summary>
    internal async Task<RideTimerSweep> SweepOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var timers = scope.ServiceProvider.GetRequiredService<IRideTimerRepository>();
        var cancellations = scope.ServiceProvider.GetRequiredService<IRideCancellationService>();

        IReadOnlyList<DueRideTimer> claimed;

        // The claim's transaction is released before anything acts on it. Holding it would keep one
        // transaction open while each fire opens its own, and nesting the two on one pooled
        // connection is how a service deadlocks itself under load.
        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            claimed = await timers.ClaimDueAsync(
                unitOfWork.Connection, unitOfWork.Transaction,
                _options.TimerBatchSize, _options.TimerLease, cancellationToken);

            await unitOfWork.CommitAsync(cancellationToken);
        }

        var applied = 0;

        foreach (var timer in claimed)
        {
            try
            {
                if (await FireAsync(scope.ServiceProvider, cancellations, timers, timer, cancellationToken))
                {
                    applied++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Left unfired and leased: the row becomes due again when the lease runs out, which
                // is exactly what a lease is for.
                logger.LogError(
                    exception, "Ride timer {TimerId} ({Kind}) on ride {RideId} could not be fired",
                    timer.Id, timer.Kind, timer.RideId);
            }
        }

        var sweep = new RideTimerSweep(claimed.Count, applied);

        if (sweep.Claimed > 0)
        {
            logger.LogInformation(
                "Ride timer sweep fired {Applied} of {Claimed} due timer(s)", sweep.Applied, sweep.Claimed);
        }

        return sweep;
    }

    /// <summary>
    /// Runs one timer and retires it. Returns whether it changed anything.
    /// </summary>
    private async Task<bool> FireAsync(
        IServiceProvider scope,
        IRideCancellationService cancellations,
        IRideTimerRepository timers,
        DueRideTimer timer,
        CancellationToken cancellationToken)
    {
        var applied = timer.Kind switch
        {
            // The ride is still Accepted, which means the driver never arrived (§11.12's
            // NoShowDriver row). If they did arrive the state moved and the matrix has no row —
            // TryApplyAsync answers null and the timer simply retires.
            RideTimerKinds.ArrivalGrace =>
                await cancellations.TryApplyAsync(timer.RideId, RideCancellationTrigger.DriverNoShow, cancellationToken)
                    is not null,

            // Five minutes after the driver arrived and the rider has not appeared (D5' §7).
            RideTimerKinds.NoShow =>
                await cancellations.TryApplyAsync(timer.RideId, RideCancellationTrigger.RiderNoShow, cancellationToken)
                    is not null,

            RideTimerKinds.OfflineGrace => await FireOfflineGraceAsync(scope, cancellations, timers, timer, cancellationToken),

            RideTimerKinds.PaymentPending => await ReportStuckPaymentAsync(scope, timer, cancellationToken),

            _ => throw new InvalidOperationException(
                $"rides.timers row {timer.Id} has kind '{timer.Kind}', which ride-svc does not own."),
        };

        await RetireAsync(scope, timers, timer.Id, cancellationToken);

        return applied;
    }

    /// <summary>
    /// The R-15/R-16 grace. Its window depends on the state the ride is in, and the ride may have
    /// moved since the last will arrived — so the timer re-plans itself rather than firing against
    /// a stale window.
    /// </summary>
    /// <remarks>
    /// Re-planning terminates: <c>offlineSince</c> is fixed for the outage and the four windows are
    /// constants, so each re-arm computes an absolute deadline from the same instant. A vehicle
    /// that comes back online has its unfired grace retired by the presence subscriber, which is
    /// the only thing that forgives it.
    /// </remarks>
    private async Task<bool> FireOfflineGraceAsync(
        IServiceProvider scope,
        IRideCancellationService cancellations,
        IRideTimerRepository timers,
        DueRideTimer timer,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize(timer);

        if (payload is null)
        {
            logger.LogWarning(
                "offline_grace timer {TimerId} on ride {RideId} carries no payload; retiring it", timer.Id, timer.RideId);

            return false;
        }

        var rides = scope.GetRequiredService<IRideRepository>();
        var connectionFactory = scope.GetRequiredService<INpgsqlConnectionFactory>();

        RideRow? ride;
        await using (var connection = await connectionFactory.OpenAsync(cancellationToken))
        {
            ride = await rides.FindAsync(connection, null, timer.RideId, cancellationToken);
        }

        if (ride is null || RideGracePolicy.For(ride.State) is not { } window)
        {
            // Terminal, or somewhere a last will means nothing (nobody is assigned before
            // acceptance). Nothing to take away.
            return false;
        }

        if (!string.Equals(ride.State, payload.State, StringComparison.Ordinal))
        {
            // The ride moved while the driver was away — Accepted's 60 s becoming DriverArrived's
            // 120 s, say. The deadline is recomputed from the same `offlineSince`, so a driver who
            // is still off the air does not get a fresh window by moving the ride along.
            var fireAt = payload.OfflineSince + window;

            await ArmAsync(
                scope, timers, timer.RideId,
                fireAt > timeProvider.GetUtcNow() ? fireAt : timeProvider.GetUtcNow(),
                payload with { State = ride.State },
                cancellationToken);

            logger.LogInformation(
                "Ride {RideId} moved {From} → {To} while its vehicle was offline; grace re-planned to {FireAt:O}",
                timer.RideId, payload.State, ride.State, fireAt);

            return false;
        }

        var applied = await cancellations.TryApplyAsync(
            timer.RideId, RideCancellationTrigger.DriverOfflineGraceExpired, cancellationToken);

        if (applied is not null)
        {
            logger.LogWarning(
                "Vehicle {VehicleId} stayed offline past the {Window} {State} grace; ride {RideId} is {ToState} " +
                "(R-15, R-16)",
                payload.VehicleId, window, payload.State, timer.RideId, applied.Ride.State);
        }

        return applied is not null;
    }

    /// <summary>
    /// ADD §13.3.1's <c>Completed+PaymentPending &gt; 10 min</c>, per ride.
    /// </summary>
    /// <remarks>
    /// Deliberately changes nothing. No row of the §11.12 matrix moves a ride out of
    /// <c>PaymentPending</c> on a timeout, and R-05 reserves that door for fare-svc; what a stuck
    /// payment needs is the on-call page ADD §13.4 describes and the ride id to run
    /// <c>runbooks/ride-stuck.md</c> against.
    /// </remarks>
    private async Task<bool> ReportStuckPaymentAsync(
        IServiceProvider scope, DueRideTimer timer, CancellationToken cancellationToken)
    {
        var rides = scope.GetRequiredService<IRideRepository>();
        var connectionFactory = scope.GetRequiredService<INpgsqlConnectionFactory>();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        var ride = await rides.FindAsync(connection, null, timer.RideId, cancellationToken);

        if (ride is null || ride.State != RideStates.PaymentPending)
        {
            return false;
        }

        MageRideDiagnostics.RideStuckDetected.Add(1, new KeyValuePair<string, object?>("state", ride.State));

        logger.LogWarning(
            "Ride {RideId} has been in {State} for over {Grace}; fare-svc has reported no terminal payment state. " +
            "Runbook: runbooks/ride-stuck.md (R-20)",
            ride.Id, ride.State, _options.PaymentPendingGrace);

        return false;
    }

    private static async Task RetireAsync(
        IServiceProvider scope, IRideTimerRepository timers, Guid timerId, CancellationToken cancellationToken)
    {
        var connectionFactory = scope.GetRequiredService<INpgsqlConnectionFactory>();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await timers.MarkFiredAsync(connection, null, timerId, cancellationToken);
    }

    private static async Task ArmAsync(
        IServiceProvider scope,
        IRideTimerRepository timers,
        Guid rideId,
        DateTimeOffset fireAt,
        OfflineGracePayload payload,
        CancellationToken cancellationToken)
    {
        var connectionFactory = scope.GetRequiredService<INpgsqlConnectionFactory>();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        await timers.ArmAsync(
            connection, null, rideId, RideTimerKinds.OfflineGrace, fireAt,
            JsonSerializer.Serialize(payload, MageRideJson.StorageOptions), cancellationToken);
    }

    private OfflineGracePayload? Deserialize(DueRideTimer timer)
    {
        if (string.IsNullOrWhiteSpace(timer.Payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OfflineGracePayload>(timer.Payload, MageRideJson.StorageOptions);
        }
        catch (JsonException exception)
        {
            logger.LogError(exception, "offline_grace timer {TimerId} carries an unreadable payload", timer.Id);
            return null;
        }
    }
}
