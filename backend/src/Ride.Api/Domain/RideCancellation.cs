using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace MageRide.Ride.Domain;

/// <summary>
/// What happened, as the §11.12 matrix names it. The trigger plus the state the ride is in is what
/// decides the outcome — never the caller.
/// </summary>
public enum RideCancellationTrigger
{
    /// <summary>The rider (or the booker on their behalf) tapped Cancel.</summary>
    RiderCancel,

    /// <summary>The driver tapped Cancel.</summary>
    DriverCancel,

    /// <summary>
    /// The driver's MQTT last will marked the vehicle offline and the per-state grace ran out
    /// (R-15, R-16).
    /// </summary>
    DriverOfflineGraceExpired,

    /// <summary>The rider never appeared; the 5-minute window after arrival closed (D5' §7).</summary>
    RiderNoShow,

    /// <summary>The driver accepted and never reached the pickup within the grace (§11.12).</summary>
    DriverNoShow,

    /// <summary>dispatch-svc exhausted its candidate rounds (US-6A.11).</summary>
    NoDriverFound,

    /// <summary>reputation-svc locked one of the parties for fraud.</summary>
    FraudLock,

    /// <summary>An operator ended the ride through admin-bff.</summary>
    AdminIntervention,
}

/// <summary>Which of the §11.12 "Penalty" column's three amounts applies.</summary>
public enum RidePenaltyBasis
{
    /// <summary>No money changes hands (US-6A.9's pre-acceptance cancel, and every system path).</summary>
    None,

    /// <summary>The flat Rs 50 cross-trip accrual (D-05, <c>Ride:CancellationPenaltyMinor</c>).</summary>
    RiderCancellation,

    /// <summary>
    /// The whole fare, because the trip was already running (§11.12 <c>InProgress | Rider taps
    /// Cancel | full fare to driver</c>).
    /// </summary>
    FullFare,

    /// <summary>The flat Rs 100 no-show fee (§11.12, <c>Ride:NoShowPenaltyMinor</c>).</summary>
    RiderNoShow,
}

/// <summary>One cell of the D5' §7 / ADD §11.12 table.</summary>
/// <param name="ToState">The terminal (or, for <c>Disputed</c>, terminal-with-followup) state.</param>
/// <param name="ReasonCode">Written to <c>rides.transitions.reason_code</c>.</param>
/// <param name="ActorType">Written to <c>rides.transitions.actor_type</c>.</param>
/// <param name="Penalty">Which amount the "Penalty" column names.</param>
/// <param name="ReputationHit">
/// Whether the driver takes the §11.12 reputation hit — the <c>reputation.driver_cancelled</c>
/// event reputation-svc (C033) counts.
/// </param>
/// <param name="CountsTowardBookingDisable">
/// Whether this cancel increments the AL-16 consecutive counter. True for exactly the
/// post-acceptance rider cancels; "pre-acceptance cancellations never count" (US-6A.10b).
/// </param>
public sealed record RideCancellationOutcome(
    string ToState,
    string ReasonCode,
    string ActorType,
    RidePenaltyBasis Penalty,
    bool ReputationHit,
    bool CountsTowardBookingDisable)
{
    /// <summary>
    /// The event the §11.12 "Events emitted" column names for this row. Derived from the target
    /// state because that is exactly how the table is written — one event per terminal, with the
    /// penalty and reputation rows riding alongside it.
    /// </summary>
    public string EventType => ToState switch
    {
        RideStates.ExpiredNoDriver => RideEventTypes.ExpiredNoDriver,
        RideStates.NoShowRider => RideEventTypes.NoShowRider,
        RideStates.NoShowDriver => RideEventTypes.NoShowDriver,
        RideStates.Disputed => RideEventTypes.Disputed,
        _ => RideEventTypes.Cancelled,
    };
}

/// <summary>
/// The server-owned cancellation and no-show matrix (D5' §7, ADD §11.12, R-03).
/// </summary>
/// <remarks>
/// <para>
/// <b>This table is the authority, not the caller.</b> The contract's <c>POST /cancel</c> body
/// carries a <c>reason</c>, but the reason is free text as far as the outcome is concerned: what
/// decides the target state, the penalty and who is charged is (current state × trigger), and the
/// trigger is derived from who is authenticated. A client cannot ask for a cheaper terminal.
/// </para>
/// <para>
/// <b>Rows the specs do not print, and why they are here.</b>
/// </para>
/// <list type="bullet">
/// <item><c>DriverArrived + RiderCancel</c> — see the note on
/// <see cref="RideTransitions"/>; Appendix B.2's catch-all is stated by acceptance, not by state.</item>
/// <item><c>PaymentPending + DriverOfflineGraceExpired</c> — R-16 lists an "at-payment 10 min"
/// grace and D5' §6.3 ends every expired grace in "<c>CancelledByDriver</c>/<c>Disputed</c>". A
/// cancel is not available once the trip has happened and a fare is owed, so it is the dispute —
/// the same landing the in-progress row uses. Recorded as a spec gap in the C032 handoff.</item>
/// <item><c>FraudLock</c> and <c>AdminIntervention</c> — <c>ride.yaml</c>'s
/// <c>system-cancel</c> declares both reasons and neither appears in §11.12. They are mapped onto
/// the same three landings the matrix already uses, chosen by how far the ride has got: nobody
/// assigned yet ⇒ the no-penalty terminal, a driver assigned ⇒ the driver terminal, money owed ⇒
/// the dispute. There is no "cancelled by admin" state in the eighteen, and inventing one would
/// break <c>ck_rides_state</c>.</item>
/// </list>
/// </remarks>
public static class RideCancellationMatrix
{
    private static readonly FrozenDictionary<(string State, RideCancellationTrigger Trigger), RideCancellationOutcome> Cells =
        Build();

    /// <summary>The outcome for this cell, or <see langword="false"/> if the matrix has no such row.</summary>
    public static bool TryResolve(
        string? state,
        RideCancellationTrigger trigger,
        [NotNullWhen(true)] out RideCancellationOutcome? outcome)
    {
        outcome = null;
        return state is not null && Cells.TryGetValue((state, trigger), out outcome);
    }

    /// <summary>Every cell, for the matrix test.</summary>
    public static IEnumerable<(string State, RideCancellationTrigger Trigger, RideCancellationOutcome Outcome)> All =>
        Cells.Select(cell => (cell.Key.State, cell.Key.Trigger, cell.Value));

    /// <summary>The states a trigger has a row for, so a caller can say why a ride is out of reach.</summary>
    public static IReadOnlyCollection<string> StatesFor(RideCancellationTrigger trigger) =>
        [.. Cells.Keys.Where(key => key.Trigger == trigger).Select(key => key.State).Order(StringComparer.Ordinal)];

    private static FrozenDictionary<(string, RideCancellationTrigger), RideCancellationOutcome> Build()
    {
        var cells = new Dictionary<(string, RideCancellationTrigger), RideCancellationOutcome>();

        // ---- Rider cancels ------------------------------------------------------------------
        // "Requested / Matching | Rider taps Cancel | CancelledByRiderBeforeAccept | None"
        // plus Offered, which Appendix B.2's pre-Accepted catch-all covers.
        foreach (var state in new[] { RideStates.Requested, RideStates.Matching, RideStates.Offered })
        {
            cells[(state, RideCancellationTrigger.RiderCancel)] = new RideCancellationOutcome(
                RideStates.CancelledByRiderBeforeAccept,
                RideReasonCodes.RiderCancelledBeforeAccept,
                RideTransitions.Actors.Rider,
                RidePenaltyBasis.None,
                ReputationHit: false,
                CountsTowardBookingDisable: false);
        }

        // "Accepted | Rider taps Cancel | CancelledByRiderAfterAccept | Rs 50 (D-05)"
        foreach (var state in new[] { RideStates.Accepted, RideStates.DriverArrived })
        {
            cells[(state, RideCancellationTrigger.RiderCancel)] = new RideCancellationOutcome(
                RideStates.CancelledByRiderAfterAccept,
                RideReasonCodes.RiderCancelledAfterAccept,
                RideTransitions.Actors.Rider,
                RidePenaltyBasis.RiderCancellation,
                ReputationHit: false,
                CountsTowardBookingDisable: true);
        }

        // "InProgress | Rider taps Cancel | CancelledByRiderAfterAccept | Full fare to driver"
        cells[(RideStates.InProgress, RideCancellationTrigger.RiderCancel)] = new RideCancellationOutcome(
            RideStates.CancelledByRiderAfterAccept,
            RideReasonCodes.RiderCancelledInTrip,
            RideTransitions.Actors.Rider,
            RidePenaltyBasis.FullFare,
            ReputationHit: false,
            CountsTowardBookingDisable: true);

        // ---- Driver cancels -----------------------------------------------------------------
        // "Accepted | Driver taps Cancel | CancelledByDriver | Reputation hit", and the same row
        // for DriverArrived and InProgress ("+ escalation if frequent").
        foreach (var state in new[] { RideStates.Accepted, RideStates.DriverArrived, RideStates.InProgress })
        {
            cells[(state, RideCancellationTrigger.DriverCancel)] = new RideCancellationOutcome(
                RideStates.CancelledByDriver,
                RideReasonCodes.DriverCancelled,
                RideTransitions.Actors.Driver,
                RidePenaltyBasis.None,
                ReputationHit: true,
                CountsTowardBookingDisable: false);
        }

        // ---- The last-will graces (R-15, R-16) ----------------------------------------------
        // "Accepted | Driver MQTT LWT → offline > 60 s | CancelledByDriver (system)"
        // "DriverArrived | Driver MQTT LWT → offline > 120 s | CancelledByDriver (system)"
        foreach (var state in new[] { RideStates.Accepted, RideStates.DriverArrived })
        {
            cells[(state, RideCancellationTrigger.DriverOfflineGraceExpired)] = new RideCancellationOutcome(
                RideStates.CancelledByDriver,
                RideReasonCodes.DriverOfflineGraceExpired,
                RideTransitions.Actors.System,
                RidePenaltyBasis.None,
                ReputationHit: true,
                CountsTowardBookingDisable: false);
        }

        // "InProgress | Driver MQTT LWT → offline > 5 min, GPS not advancing | Disputed"
        // and R-16's at-payment 10 min, which D5' §6.3 lands the same way (see the class remarks).
        foreach (var state in new[] { RideStates.InProgress, RideStates.PaymentPending })
        {
            cells[(state, RideCancellationTrigger.DriverOfflineGraceExpired)] = new RideCancellationOutcome(
                RideStates.Disputed,
                RideReasonCodes.DriverOfflineGraceExpired,
                RideTransitions.Actors.System,
                RidePenaltyBasis.None,
                ReputationHit: false,
                CountsTowardBookingDisable: false);
        }

        // ---- No-shows -----------------------------------------------------------------------
        // "DriverArrived | Rider no-show after 5 min + 2 SMS reminders | NoShowRider | Rs 100
        //  (configurable) | driver compensation = base fare/2"
        cells[(RideStates.DriverArrived, RideCancellationTrigger.RiderNoShow)] = new RideCancellationOutcome(
            RideStates.NoShowRider,
            RideReasonCodes.RiderNoShow,
            RideTransitions.Actors.System,
            RidePenaltyBasis.RiderNoShow,
            ReputationHit: false,

            // A no-show is not a cancellation. US-6A.10b counts "cancellations made after a driver
            // has accepted", and reputation-svc keeps a separate no-show counter (D5' §4.2) — so
            // charging one event to both tallies would disable booking in two rides, not three.
            CountsTowardBookingDisable: false);

        // "Accepted/DriverArrived | Driver accepted but never reaches pickup; rider waits, grace
        //  exceeded | NoShowDriver | reputation hit + rider compensation"
        foreach (var state in new[] { RideStates.Accepted, RideStates.DriverArrived })
        {
            cells[(state, RideCancellationTrigger.DriverNoShow)] = new RideCancellationOutcome(
                RideStates.NoShowDriver,
                RideReasonCodes.DriverNoShow,
                RideTransitions.Actors.System,
                RidePenaltyBasis.None,
                ReputationHit: true,
                CountsTowardBookingDisable: false);
        }

        // ---- Dispatch gave up (US-6A.11) ----------------------------------------------------
        // Matching only: a ride that is Requested has not been offered to anybody yet and one that
        // is Offered has a live candidate, so neither can have run out of them.
        cells[(RideStates.Matching, RideCancellationTrigger.NoDriverFound)] = new RideCancellationOutcome(
            RideStates.ExpiredNoDriver,
            RideReasonCodes.NoDriverFound,
            RideTransitions.Actors.System,
            RidePenaltyBasis.None,
            ReputationHit: false,
            CountsTowardBookingDisable: false);

        // ---- Fraud lock and admin intervention ----------------------------------------------
        foreach (var trigger in new[] { RideCancellationTrigger.FraudLock, RideCancellationTrigger.AdminIntervention })
        {
            var reason = trigger == RideCancellationTrigger.FraudLock
                ? RideReasonCodes.FraudLock
                : RideReasonCodes.AdminIntervention;

            var actor = trigger == RideCancellationTrigger.FraudLock
                ? RideTransitions.Actors.System
                : RideTransitions.Actors.Admin;

            foreach (var state in new[] { RideStates.Requested, RideStates.Matching, RideStates.Offered })
            {
                cells[(state, trigger)] = new RideCancellationOutcome(
                    RideStates.CancelledByRiderBeforeAccept, reason, actor,
                    RidePenaltyBasis.None, ReputationHit: false, CountsTowardBookingDisable: false);
            }

            foreach (var state in new[] { RideStates.Accepted, RideStates.DriverArrived })
            {
                cells[(state, trigger)] = new RideCancellationOutcome(
                    RideStates.CancelledByDriver, reason, actor,
                    RidePenaltyBasis.None, ReputationHit: false, CountsTowardBookingDisable: false);
            }

            foreach (var state in new[] { RideStates.InProgress, RideStates.PaymentPending })
            {
                cells[(state, trigger)] = new RideCancellationOutcome(
                    RideStates.Disputed, reason, actor,
                    RidePenaltyBasis.None, ReputationHit: false, CountsTowardBookingDisable: false);
            }
        }

        return cells.ToFrozenDictionary();
    }
}

/// <summary>
/// <c>rides.transitions.reason_code</c> values. Stable strings: a support engineer reading the
/// audit six weeks later, and reputation-svc counting cancels, both branch on them.
/// </summary>
public static class RideReasonCodes
{
    public const string OfferSent = "OFFER_SENT";
    public const string OfferDeclined = "OFFER_DECLINED";
    public const string OfferExpired = "OFFER_EXPIRED";
    public const string DispatchCandidateBuild = "DISPATCH_CANDIDATE_BUILD";
    public const string FareHandoff = "FARE_HANDOFF";

    public const string RiderCancelledBeforeAccept = "RIDER_CANCELLED_BEFORE_ACCEPT";
    public const string RiderCancelledAfterAccept = "RIDER_CANCELLED_AFTER_ACCEPT";
    public const string RiderCancelledInTrip = "RIDER_CANCELLED_IN_TRIP";
    public const string DriverCancelled = "DRIVER_CANCELLED";
    public const string DriverOfflineGraceExpired = "DRIVER_OFFLINE_GRACE_EXPIRED";
    public const string RiderNoShow = "RIDER_NO_SHOW";
    public const string DriverNoShow = "DRIVER_NO_SHOW";
    public const string NoDriverFound = "NO_DRIVER_FOUND";
    public const string FraudLock = "FRAUD_LOCK";
    public const string AdminIntervention = "ADMIN_INTERVENTION";

    public const string PaymentSucceeded = "PAYMENT_SUCCEEDED";
    public const string PaymentCashSettled = "PAYMENT_CASH_SETTLED";
    public const string PaymentCodCollected = "PAYMENT_COD_COLLECTED";
    public const string PaymentDisputed = "PAYMENT_DISPUTED";
}
