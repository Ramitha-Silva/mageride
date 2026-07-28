using System.Collections.Frozen;

namespace MageRide.Ride.Domain;

/// <summary>
/// The eighteen Mode C ride-aggregate states (D5' §6 / ADD Appendix B.2), matching the
/// <c>ck_rides_state</c> CHECK landed by C004 and <c>_shared.yaml#RideState</c>.
/// </summary>
/// <remarks>
/// All eighteen are named here even though C022 only drives eight of them: the set is the
/// aggregate's vocabulary, and a service that knows only the states it happens to write cannot
/// tell "terminal" from "unknown" when it reads a row another component moved.
/// </remarks>
public static class RideStates
{
    public const string Requested = "Requested";
    public const string Matching = "Matching";
    public const string Offered = "Offered";
    public const string Accepted = "Accepted";
    public const string DriverArrived = "DriverArrived";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string PaymentPending = "PaymentPending";
    public const string Paid = "Paid";
    public const string CashSettled = "CashSettled";
    public const string CashOnDeliveryCollected = "CashOnDeliveryCollected";
    public const string Disputed = "Disputed";
    public const string CancelledByRiderBeforeAccept = "CancelledByRiderBeforeAccept";
    public const string CancelledByRiderAfterAccept = "CancelledByRiderAfterAccept";
    public const string CancelledByDriver = "CancelledByDriver";
    public const string ExpiredNoDriver = "ExpiredNoDriver";
    public const string NoShowRider = "NoShowRider";
    public const string NoShowDriver = "NoShowDriver";

    public static readonly FrozenSet<string> All = new[]
    {
        Requested, Matching, Offered, Accepted, DriverArrived, InProgress, Completed, PaymentPending,
        Paid, CashSettled, CashOnDeliveryCollected, Disputed, CancelledByRiderBeforeAccept,
        CancelledByRiderAfterAccept, CancelledByDriver, ExpiredNoDriver, NoShowRider, NoShowDriver,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// States a ride never leaves.
    /// </summary>
    /// <remarks>
    /// Deliberately **not** the same list as <c>ux_rides_open_passenger</c>'s exempt set: that
    /// index also exempts <c>Completed</c>, so the one-open-ride guard lifts at Completed and
    /// re-applies at PaymentPending. Both DDL sources print it that way and C004 landed it
    /// verbatim (C004 note (b)); Completed is plainly not terminal — the ride still owes a
    /// payment — so the domain answers the question the domain was asked.
    /// </remarks>
    public static readonly FrozenSet<string> Terminal = new[]
    {
        Paid, CashSettled, CashOnDeliveryCollected, Disputed, CancelledByRiderBeforeAccept,
        CancelledByRiderAfterAccept, CancelledByDriver, ExpiredNoDriver, NoShowRider, NoShowDriver,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The four states <c>ux_rides_driver_busy</c> holds a driver in (O2, R-10).</summary>
    public static readonly FrozenSet<string> DriverBusy = new[]
    {
        Accepted, DriverArrived, InProgress, PaymentPending,
    }.ToFrozenSet(StringComparer.Ordinal);

    public static bool IsTerminal(string? state) => state is not null && Terminal.Contains(state);

    public static bool IsKnown(string? state) => state is not null && All.Contains(state);
}
