using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// The whole D5' §6 / ADD Appendix B.2 machine, including the §11.12 cancellation and no-show
/// matrix and the R-05 payment terminals.
/// </summary>
/// <remarks>
/// <para>
/// The table is deliberately data, not a switch: <c>RideStateMachineTests</c> walks it against
/// ADD Appendix B.2 and <c>RideCancellationMatrixTests</c> asserts that every outcome the §11.12
/// matrix produces is an edge drawn here. Two tables that describe the same machine have to agree,
/// and the only way to keep them agreeing is to check.
/// </para>
/// <para>
/// An unlisted move is <c>400 illegal-transition</c> rather than a silent success. What is still
/// missing is C037's and C049's: the package OTP gates do not add states (ADD Appendix B.2
/// invariant 6 — the machine is kind-agnostic), and <c>/dispute</c> reaches <c>Disputed</c> from
/// the payment side, which is the edge <c>PaymentPending → Disputed</c> already drawn below.
/// </para>
/// </remarks>
public static class RideTransitions
{
    /// <summary>Who asked for the move. Written to <c>rides.transitions.actor_type</c>.</summary>
    public static class Actors
    {
        public const string Rider = "rider";
        public const string Driver = "driver";
        public const string System = "system";
        public const string Admin = "admin";
    }

    private static readonly FrozenDictionary<string, FrozenSet<string>> Allowed = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // dispatch-svc begins the candidate build (D5' §6); the rider may still walk away with no
        // penalty (§11.12 row 1).
        [RideStates.Requested] =
        [
            RideStates.Matching,
            RideStates.CancelledByRiderBeforeAccept,
        ],

        // …reserves a driver and pushes the offer, 15 s TTL (D5' §3.5).
        //
        // `Matching → Accepted` is drawn because ADD §11.11's conditional UPDATE guards on
        // `state IN ('Matching','Offered')`, not on Offered alone: the 15-second TTL can bounce a
        // ride back to Matching while the winning accept is still in flight. Appendix B.2's
        // diagram draws only the Offered edge, and C015's `RideTransitions.EDGES` on the client
        // carries both for the same reason — the two tables have to agree.
        [RideStates.Matching] =
        [
            RideStates.Offered,
            RideStates.Accepted,
            RideStates.ExpiredNoDriver,
            RideStates.CancelledByRiderBeforeAccept,
        ],

        // Accept is the atomic single winner (§11.11); decline and expiry put the ride back in the
        // pool. A rider who cancels while an offer is live is still pre-acceptance, so it is the
        // no-penalty terminal (Appendix B.2: "Any pre-Accepted state + rider cancel").
        [RideStates.Offered] =
        [
            RideStates.Accepted,
            RideStates.Matching,
            RideStates.CancelledByRiderBeforeAccept,
        ],

        // The contract's `start` description allows Accepted → InProgress directly, which
        // ADD Appendix B.2's diagram does not draw. The contract wins (backend/contracts/CLAUDE.md)
        // — a driver who reached the rider without the geofence firing must still be able to start
        // the trip. Recorded in the C022 handoff.
        [RideStates.Accepted] =
        [
            RideStates.DriverArrived,
            RideStates.InProgress,
            RideStates.CancelledByRiderAfterAccept,
            RideStates.CancelledByDriver,
            RideStates.NoShowDriver,
        ],

        // `DriverArrived → CancelledByRiderAfterAccept` is **not** a row of the §11.12 table, which
        // jumps straight from the Accepted rider-cancel to the DriverArrived no-show. It is drawn
        // because Appendix B.2's catch-all is stated by acceptance and not by state — "Any
        // pre-Accepted state + rider cancel → CancelledByRiderBeforeAccept" — so a cancel after
        // acceptance is the after-accept terminal wherever it lands. The alternative reading makes
        // a rider unable to cancel precisely while a driver is waiting at their door, which is when
        // cancelling matters most. Recorded as a spec gap in the C032 handoff.
        [RideStates.DriverArrived] =
        [
            RideStates.InProgress,
            RideStates.NoShowRider,
            RideStates.CancelledByRiderAfterAccept,
            RideStates.CancelledByDriver,
            RideStates.NoShowDriver,
        ],

        [RideStates.InProgress] =
        [
            RideStates.Completed,
            RideStates.CancelledByRiderAfterAccept,
            RideStates.CancelledByDriver,
            RideStates.Disputed,
        ],

        // Automatic, inside the same transaction as `complete`: the fare is owed the moment the
        // trip ends, so the ride never rests in Completed.
        [RideStates.Completed] = [RideStates.PaymentPending],

        // R-05: only fare-svc reporting a terminal payment state moves a ride out of here, and the
        // driver's earning posts from the state it lands in — never before.
        [RideStates.PaymentPending] =
        [
            RideStates.Paid,
            RideStates.CashSettled,
            RideStates.CashOnDeliveryCollected,
            RideStates.Disputed,
        ],
    }.ToFrozenDictionary(static e => e.Key, static e => e.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Whether a ride may move from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsAllowed(string? from, string? to) =>
        from is not null && to is not null && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Every move the machine draws, for the fence test.</summary>
    public static IEnumerable<(string From, string To)> All =>
        Allowed.SelectMany(entry => entry.Value.Select(to => (entry.Key, to)));
}
