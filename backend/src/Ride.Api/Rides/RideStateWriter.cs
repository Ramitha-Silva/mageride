using MageRide.Ride.Configuration;
using MageRide.Ride.Domain;
using MageRide.Ride.Persistence;
using MageRide.Shared.Messaging;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Ride.Rides;

/// <summary>
/// The three writes that always accompany a ride state change, in the transaction that made it.
/// </summary>
/// <remarks>
/// <para>
/// ADD Appendix B.2 invariant 4 — "every state transition is recorded in <c>rides.transitions</c>
/// and surfaces a domain event through <c>rides.outbox</c>" — plus the durable timers R-04 hangs
/// off the state the ride has just entered. Keeping the three together in one call is what makes
/// "a ride in <c>DriverArrived</c> always has a no-show timer" a property of the code rather than
/// of the caller remembering.
/// </para>
/// <para>
/// Nothing here publishes to Redpanda. The outbox row commits with the change and the kernel's
/// LISTEN/NOTIFY dispatcher drains it afterwards (E-09, R-13), which is what makes "no event
/// describes a rolled-back change" true.
/// </para>
/// </remarks>
public sealed class RideStateWriter(
    IRideTransitionRepository transitions,
    IRideTimerRepository timers,
    IOutboxWriter outbox,
    IOptions<RideOptions> options,
    TimeProvider timeProvider)
{
    private readonly RideOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Records the move, re-plans the ride's timers and appends <paramref name="events"/>.
    /// </summary>
    /// <param name="ride">The row as it stands <em>after</em> the change.</param>
    public async Task RecordAsync(
        IUnitOfWork unitOfWork,
        RideRow ride,
        string? fromState,
        string actorType,
        Guid? actorId,
        string? reasonCode,
        IReadOnlyCollection<OutboxRecord> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(ride);
        ArgumentNullException.ThrowIfNull(events);

        await transitions.RecordAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            fromState,
            ride.State,
            actorType,
            actorId,
            reasonCode,
            cancellationToken);

        await ApplyTimerPlanAsync(unitOfWork, ride, cancellationToken);

        if (events.Count > 0)
        {
            await outbox.WriteAsync(unitOfWork, events, cancellationToken);
        }
    }

    /// <summary>
    /// Retires the timers the ride has outgrown and arms the one its new state calls for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>offline_grace</c> survives every non-terminal move.</b> It is armed by the last-will
    /// subscriber, not by a transition, and its window depends on the state the ride is in — so a
    /// driver who goes offline while <c>Accepted</c> and then taps Arrive should have their 60 s
    /// window become the 120 s one, not be forgiven. The timer re-plans itself when it fires
    /// (<c>RideTimerWorker</c>); retiring it here would silently forgive a driver who is still off
    /// the air.
    /// </para>
    /// <para>
    /// <b><c>cod_uncollected</c> survives them for the same reason</b> (Δ C037). It is armed at the
    /// pickup of a COD parcel and its whole purpose is to outlive the delivery: the ride moves
    /// <c>InProgress → Completed → PaymentPending</c> within the hour and the money is not
    /// reconciled for a day, which is exactly the decoupling AL-33 asks for ("COD/cash collection is
    /// decoupled from completion"). Retiring it on the way past would leave P-14 with no clock.
    /// </para>
    /// <para>
    /// A terminal state retires everything, <c>offline_grace</c> and <c>cod_uncollected</c>
    /// included: there is nothing left to take away.
    /// </para>
    /// </remarks>
    private async Task ApplyTimerPlanAsync(IUnitOfWork unitOfWork, RideRow ride, CancellationToken cancellationToken)
    {
        var retire = RideStates.IsTerminal(ride.State) ? AllKinds : LifecycleKinds;

        await timers.RetireAsync(
            unitOfWork.Connection, unitOfWork.Transaction, ride.Id, retire, cancellationToken);

        (string? Kind, TimeSpan After) armed = ride.State switch
        {
            // The driver said they were coming. If this fires, they never arrived (§11.12).
            RideStates.Accepted => (RideTimerKinds.ArrivalGrace, _options.ArrivalGrace),

            // The rider has five minutes from here (D5' §7).
            RideStates.DriverArrived => (RideTimerKinds.NoShow, _options.RiderNoShowGrace),

            // P-14, Δ C037: a COD parcel has just been picked up, so from here the driver is
            // carrying somebody's cash. ADD §11.16 arms the 24-hour window at exactly this moment
            // — not at the delivery — because the clock is about the *money in transit*, and the
            // delivery may itself be what never happens.
            RideStates.InProgress when ride.IsCashOnDelivery =>
                (RideTimerKinds.CodUncollected, _options.CodUncollectedGrace),

            // The R-20 stuck-payment watch, per ride (ADD §13.3.1).
            RideStates.PaymentPending => (RideTimerKinds.PaymentPending, _options.PaymentPendingGrace),

            _ => (null, TimeSpan.Zero),
        };

        if (armed.Kind is null)
        {
            return;
        }

        await timers.ArmAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            ride.Id,
            armed.Kind,
            timeProvider.GetUtcNow() + armed.After,
            payload: null,
            cancellationToken);
    }

    /// <summary>
    /// The kinds a lifecycle move replaces. <c>offline_grace</c> and <c>cod_uncollected</c> are
    /// deliberately absent — see the remarks above.
    /// </summary>
    private static readonly string[] LifecycleKinds =
    [
        RideTimerKinds.ArrivalGrace, RideTimerKinds.NoShow, RideTimerKinds.PaymentPending,
    ];

    private static readonly string[] AllKinds = [.. RideTimerKinds.Owned];
}
