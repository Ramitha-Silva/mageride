using System.Collections.Frozen;

namespace MageRide.Voip.Domain;

/// <summary>
/// The <c>comms.call_log.call_type</c> CHECK (migration 1302), as narrowed by AL-48.
/// </summary>
/// <remarks>
/// <b>There are two values and there will not be a third.</b> AL-48 withdrew number masking
/// outright: the `normal_masked` PSTN bridge, the web proxy-DID lease and D-25's masked-SMS relay
/// are all removed, and D6' I-30.2 says so in as many words. `direct_dial` is not a call this
/// service places — it is a `tel:` link the client opened and then told us about.
/// </remarks>
public static class CallTypes
{
    /// <summary>An in-app LiveKit session. The only call this service actually starts.</summary>
    public const string FreeVoip = "free_voip";

    /// <summary>
    /// A client-side <c>tel:</c> dial of the counterparty's real number, reported after the fact.
    /// </summary>
    /// <remarks>
    /// Best-effort by construction: the platform never sees the PSTN leg, so a missing row means
    /// nothing at all and a present one means only that somebody tapped the button.
    /// </remarks>
    public const string DirectDial = "direct_dial";

    public static readonly string[] All = [FreeVoip, DirectDial];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? callType) => callType is not null && Known.Contains(callType);
}

/// <summary>The <c>comms.call_log.callee_role</c> CHECK (migration 1302).</summary>
/// <remarks>
/// Four values, because the same table records package calls (AL-33): a driver rings the
/// <c>sender</c> or the <c>recipient</c> of a parcel, neither of whom is a "passenger". Only
/// <c>driver</c> and <c>passenger</c> can be VoIP — the other two have no app to answer in and are
/// always a <c>tel:</c> dial.
/// </remarks>
public static class CalleeRoles
{
    public const string Driver = "driver";
    public const string Passenger = "passenger";
    public const string Sender = "sender";
    public const string Recipient = "recipient";

    public static readonly string[] All = [Driver, Passenger, Sender, Recipient];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? role) => role is not null && Known.Contains(role);

    /// <summary>Whether a role can be reached in-app at all.</summary>
    /// <remarks>
    /// A parcel's sender or recipient may have no MageRide account (P-09), so there is nobody to
    /// admit to a LiveKit room. Their Call button is a `tel:` link and always was (I-28.1).
    /// </remarks>
    public static bool CanBeVoip(string role) => role is Driver or Passenger;
}

/// <summary>
/// What became of a call — <c>comms.call_log.outcome</c>.
/// </summary>
/// <remarks>
/// <b>No spec names any of these.</b> 1302 leaves the column free text with no writer at all; the
/// set is coined here (migration 1311 turns it into a CHECK) because the column is the only place
/// the platform can answer "how often does in-app calling fail" — ADD §16 has a p95 call-setup SLO
/// and ADD §14 has a documented fallback, and neither is measurable if every call looks identical
/// after the fact. Raised as a micro-change-set in the C055 handoff.
/// </remarks>
public static class CallOutcomes
{
    /// <summary>The two parties spoke.</summary>
    public const string Completed = "completed";

    /// <summary>The callee did not pick up.</summary>
    public const string Missed = "missed";

    /// <summary>The callee rejected it.</summary>
    public const string Declined = "declined";

    /// <summary>The caller hung up before it connected.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The session could not be established — signalling refused, ICE never completed, no media.
    /// </summary>
    /// <remarks>
    /// This is the value AL-48's fallback hangs on: it is what the client reports when it puts up
    /// "Call normally instead?", and a `direct_dial` row that follows it on the same ride is the
    /// fallback actually being taken. Nothing else distinguishes that from a user who simply
    /// preferred to dial.
    /// </remarks>
    public const string VoipFailed = "voip_failed";

    public static readonly string[] All = [Completed, Missed, Declined, Cancelled, VoipFailed];

    private static readonly FrozenSet<string> Known = All.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsKnown(string? outcome) => outcome is not null && Known.Contains(outcome);
}

/// <summary>
/// The Mode C ride states this service has to recognise (D5' §6, <c>ck_rides_state</c>).
/// </summary>
/// <remarks>
/// A copy of ride-svc's <c>RideStates.Terminal</c>, deliberately — the two services agree on the
/// contents of a column, not on a compile-time type, and <c>TerminalStatesMatchRideSvc</c> asserts
/// the set against the database's own CHECK so a drift is a failing test rather than a call that
/// outlives its ride.
/// </remarks>
public static class RideStates
{
    /// <summary>
    /// States a ride never leaves.
    /// </summary>
    /// <remarks>
    /// <b><c>Completed</c> is not one of them</b>, and that matters here more than almost anywhere:
    /// the ride still owes a payment, the driver and the passenger are still standing next to each
    /// other, and "my driver just left with my bag" is exactly the call this service exists to
    /// carry. A token refused at `Completed` would be refused in the ninety seconds it is most
    /// needed. ride-svc's own `RideStates` draws the same line for the same reason.
    /// </remarks>
    public static readonly FrozenSet<string> Terminal = new[]
    {
        "Paid", "CashSettled", "CashOnDeliveryCollected", "Disputed",
        "CancelledByRiderBeforeAccept", "CancelledByRiderAfterAccept", "CancelledByDriver",
        "ExpiredNoDriver", "NoShowRider", "NoShowDriver",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsTerminal(string? state) => state is not null && Terminal.Contains(state);
}
