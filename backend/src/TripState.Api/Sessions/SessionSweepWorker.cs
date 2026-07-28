using Dapper;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using MageRide.TripState.Configuration;
using MageRide.TripState.Domain;
using MageRide.TripState.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MageRide.TripState.Sessions;

/// <summary>What one sweep pass closed, by reason.</summary>
public sealed record SweepResult(int Idle, int Arrived, int Offline)
{
    public int Total => Idle + Arrived + Offline;
}

/// <summary>
/// The durable timers: 30-minute idle (US-5.3), 100-metre arrival (US-5.4) and the last-will grace
/// (R-15, T-04).
/// </summary>
/// <remarks>
/// <para>
/// <b>The timer fires here and not in the app.</b> US-5.9 has the driver notified that their
/// session was auto-ended, which means the platform must have ended it — a backgrounded, crashed
/// or uninstalled driver app still has to leave a correctly closed session behind, and a phone
/// timer cannot promise that. D3' puts the same conclusion in the contract by giving the auto-end
/// an internal route.
/// </para>
/// <para>
/// <b>Every close goes through <see cref="ISessionService.AutoEndAsync"/>.</b> Not a bulk UPDATE:
/// closing a session also writes the domain log, the outbox row, the Redis key and the standby
/// cadence hint, and a sweep that took a shortcut would leave a fleet of half-closed sessions that
/// no consumer ever heard about. The claim query is the only bulk part.
/// </para>
/// <para>
/// Rows are claimed <c>FOR UPDATE SKIP LOCKED</c>, so two replicas sweeping at once close disjoint
/// sets; the <c>state = ACTIVE</c> predicate inside the close settles anything they still race on.
/// </para>
/// </remarks>
public sealed class SessionSweepWorker(
    IServiceProvider services,
    IOptions<TripStateOptions> options,
    TimeProvider clock,
    ILogger<SessionSweepWorker> logger) : BackgroundService
{
    private readonly TripStateOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Session sweep running every {Interval}: idle {Idle}, arrival {Radius} m, offline grace {Grace}",
            _options.SweepInterval, _options.IdleTimeout, _options.GeofenceRadiusM, _options.OfflineGrace);

        using var ticker = new PeriodicTimer(_options.SweepInterval);

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
                // A pass that throws must not take the worker with it. Nothing was committed for
                // the sessions it did not reach, so the next tick picks them up unchanged.
                logger.LogError(exception, "Session sweep failed; retrying on the next tick");
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

    /// <summary>One pass. Exposed so a test can drive the timers without waiting on the ticker.</summary>
    internal async Task<SweepResult> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var idle = await CloseAsync(
            (sessions, unitOfWork, token) => sessions.ClaimIdleAsync(
                unitOfWork.Connection, unitOfWork.Transaction, now - _options.IdleTimeout,
                _options.SweepBatchSize, token),
            EndReasons.IdleTimeout,
            cancellationToken);

        var arrived = await CloseAsync(
            (sessions, unitOfWork, token) => sessions.ClaimArrivedAsync(
                unitOfWork.Connection, unitOfWork.Transaction, _options.GeofenceRadiusM,
                _options.SweepBatchSize, token),
            EndReasons.DestinationGeofence,
            cancellationToken);

        var offline = await CloseAsync(
            (_, unitOfWork, token) => ClaimOfflineAsync(unitOfWork, now - _options.OfflineGrace, token),
            EndReasons.MqttOffline,
            cancellationToken);

        var result = new SweepResult(idle, arrived, offline);

        if (result.Total > 0)
        {
            logger.LogInformation(
                "Session sweep ended {Total} session(s): {Idle} idle, {Arrived} arrived, {Offline} offline",
                result.Total, result.Idle, result.Arrived, result.Offline);
        }

        return result;
    }

    /// <summary>
    /// Claims one batch, releases the claim, then closes each session through the service.
    /// </summary>
    /// <remarks>
    /// The claim transaction is released before the closes rather than held across them. Holding
    /// it would keep one transaction open while each close opens its own, and nesting the two on
    /// one pooled connection is how a service deadlocks itself under load — the same shape C030's
    /// bulk mint worker uses.
    /// </remarks>
    private async Task<int> CloseAsync(
        Func<ISessionRepository, IUnitOfWork, CancellationToken, Task<IReadOnlyList<Session>>> claim,
        string reason,
        CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();

        var unitOfWorkFactory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var service = scope.ServiceProvider.GetRequiredService<ISessionService>();

        IReadOnlyList<Session> claimed;

        await using (var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken))
        {
            claimed = await claim(sessions, unitOfWork, cancellationToken);
            await unitOfWork.RollbackAsync(cancellationToken);
        }

        var closed = 0;

        foreach (var session in claimed)
        {
            try
            {
                await service.AutoEndAsync(session.Id.ToString(), reason, cancellationToken);
                closed++;
            }
            catch (MageRide.Shared.Errors.MageRideException exception)
            {
                // The session stopped being live between the claim and the close — the driver's
                // own End Journey beat the timer to it, which is the normal race and not a fault.
                logger.LogDebug(
                    exception, "Session {SessionId} was already closed when the {Reason} sweep reached it",
                    session.Id, reason);
            }
        }

        return closed;
    }

    /// <summary>
    /// ACTIVE sessions whose vehicle has been gone longer than the grace (R-15, T-04).
    /// </summary>
    /// <remarks>
    /// Kept here rather than on <see cref="ISessionRepository"/> because it is the sweep's own
    /// question: the presence column is written by the broker subscriber and read by nothing else.
    /// </remarks>
    private static async Task<IReadOnlyList<Session>> ClaimOfflineAsync(
        IUnitOfWork unitOfWork, DateTimeOffset offlineSince, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.QueryAsync<Session>(new CommandDefinition(
            $"""
             SELECT id, vehicle_id, driver_id, mode, state, route_id, auto_end_at_destination, end_reason,
                    started_by, ended_by, started_at, ended_at, last_movement_at
               FROM trips.sessions
              WHERE state = '{SessionStates.Active}'
                AND offline_since IS NOT NULL
                AND offline_since <= @OfflineSince
              ORDER BY offline_since
                FOR UPDATE SKIP LOCKED;
             """,
            new { OfflineSince = offlineSince },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        return [.. rows];
    }
}
