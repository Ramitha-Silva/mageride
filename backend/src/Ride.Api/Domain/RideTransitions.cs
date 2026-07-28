using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// The moves the C022 happy-path slice permits, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// This is a **subset** of the D5' §6 machine, on purpose. The cancellation and no-show matrix
/// (§11.12), the durable offer-expiry backstop (R-04), the LWT grace transitions (R-15/R-16) and
/// the payment terminals (R-05) are C032/C037/C049. An unlisted move is
/// <c>400 illegal-transition</c> rather than a silent success, so a client written against a
/// later wave fails loudly here instead of leaving a ride in a state nothing in this slice can
/// move on from.
/// </para>
/// <para>
/// The table is deliberately data, not a switch: `RideStateMachineTests` walks it against
/// ADD Appendix B.2, which is the only way a fence stays true as later waves add rows.
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
        // dispatch-svc begins the candidate build (D5' §6).
        [RideStates.Requested] = [RideStates.Matching],

        // …reserves a driver and pushes the offer, 15 s TTL (D5' §3.5).
        //
        // `Matching → Accepted` is drawn because ADD §11.11's conditional UPDATE guards on
        // `state IN ('Matching','Offered')`, not on Offered alone: the 15-second TTL can bounce a
        // ride back to Matching while the winning accept is still in flight. Appendix B.2's
        // diagram draws only the Offered edge, and C015's `RideTransitions.EDGES` on the client
        // carries both for the same reason — the two tables have to agree.
        [RideStates.Matching] = [RideStates.Offered, RideStates.Accepted],

        // Accept is the atomic single winner (§11.11); decline puts the ride back in the pool.
        [RideStates.Offered] = [RideStates.Accepted, RideStates.Matching],

        // The contract's `start` description allows Accepted → InProgress directly, which
        // ADD Appendix B.2's diagram does not draw. The contract wins (backend/contracts/CLAUDE.md)
        // — a driver who reached the rider without the geofence firing must still be able to start
        // the trip. Recorded in the C022 handoff.
        [RideStates.Accepted] = [RideStates.DriverArrived, RideStates.InProgress],

        [RideStates.DriverArrived] = [RideStates.InProgress],

        [RideStates.InProgress] = [RideStates.Completed],

        // Automatic, inside the same transaction as `complete`: the fare is owed the moment the
        // trip ends, so the ride never rests in Completed.
        [RideStates.Completed] = [RideStates.PaymentPending],
    }.ToFrozenDictionary(static e => e.Key, static e => e.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>Whether this slice may move a ride from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsAllowed(string? from, string? to) =>
        from is not null && to is not null && Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Every move the slice implements, for the fence test.</summary>
    public static IEnumerable<(string From, string To)> All =>
        Allowed.SelectMany(entry => entry.Value.Select(to => (entry.Key, to)));
}
