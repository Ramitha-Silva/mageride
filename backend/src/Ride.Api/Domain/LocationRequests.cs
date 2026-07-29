using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// <c>rides.location_requests.state</c> (migration 0606's <c>ck_location_requests_state</c>) and
/// <c>ride.yaml</c>'s <c>LocationRequestState</c> — the P-02 round-trip's five positions.
/// </summary>
/// <remarks>
/// <para>
/// <c>Pending</c> is the only live one. The other four are terminal: a request that has been
/// answered, refused, timed out or never delivered is never asked again — the booker issues a new
/// one, which is what the P-12 rate limit counts.
/// </para>
/// <para>
/// <see cref="RiderNotRegistered"/> is <b>not</b> a failure. AL-45 resolved the D1/D5-versus-web
/// contradiction in favour of the web flow: notification-svc mints a <c>pickup_confirm</c> token
/// and SMSes the link, and SCR-WT-003 feeds this same state machine through public-bff. The state
/// records which channel was taken; the request is still answerable.
/// </para>
/// </remarks>
public static class LocationRequestStates
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Declined = "Declined";
    public const string Expired = "Expired";

    /// <summary>The number belongs to no <c>iam.users</c> row; the SMS web path was taken (AL-45).</summary>
    public const string RiderNotRegistered = "RiderNotRegistered";

    public static readonly FrozenSet<string> All =
        new[] { Pending, Confirmed, Declined, Expired, RiderNotRegistered }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The states a request can still be answered from — <b>two</b>, not one.
    /// </summary>
    /// <remarks>
    /// ADD §11.15 treats <see cref="RiderNotRegistered"/> as the end of the road (the booker falls
    /// back to a map pin) and AL-45 is later and wins: the rider is SMSed a <c>pickup_confirm</c>
    /// link and SCR-WT-003 feeds this same machine through public-bff. So the request is open on a
    /// different channel, it expires on the same 300 s clock, and US-8.19's booker fallback is what
    /// happens if nobody answers rather than the only path. Both halves of that sentence are
    /// properties of this set.
    /// </remarks>
    public static readonly FrozenSet<string> Live =
        new[] { Pending, RiderNotRegistered }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>
/// <c>safety.location_request_audit.decision</c> (migration 0904's
/// <c>ck_location_request_audit_decision</c>) — P-12's durable abuse record.
/// </summary>
/// <remarks>
/// Deliberately a different vocabulary from <see cref="LocationRequestStates"/>: the audit's
/// <c>NotRegistered</c> is one word and the state is two, and the table's CHECK is what both have
/// to satisfy. Mapping them in one place is what stops the two spellings drifting.
/// </remarks>
public static class LocationRequestDecisions
{
    public const string Confirmed = "Confirmed";
    public const string Declined = "Declined";
    public const string Expired = "Expired";
    public const string NotRegistered = "NotRegistered";

    /// <summary>The audit spelling of a request state, or <see langword="null"/> for a state that is not an outcome.</summary>
    public static string? For(string state) => state switch
    {
        LocationRequestStates.Confirmed => Confirmed,
        LocationRequestStates.Declined => Declined,
        LocationRequestStates.Expired => Expired,
        LocationRequestStates.RiderNotRegistered => NotRegistered,
        _ => null,
    };
}
