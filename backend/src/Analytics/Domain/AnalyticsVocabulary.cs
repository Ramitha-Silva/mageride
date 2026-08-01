namespace MageRide.Analytics.Domain;

/// <summary>
/// The literal values this read model reads out of other bounded contexts' tables.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named here rather than inlined in SQL, because every one of them is a fact owned somewhere
/// else.</b> A rollup reads six tables belonging to five services; if one of those services widened
/// a CHECK and this component kept a stale literal in a query string, the dashboard would quietly
/// under-count and nothing would fail. `VocabularyTests` compares each list below against the
/// owning service's own constants, so drift is a red test rather than a wrong number on a screen.
/// </para>
/// <para>
/// This component never <em>writes</em> any of these values. It never writes any of these tables.
/// </para>
/// </remarks>
public static class AnalyticsVocabulary
{
    /// <summary>
    /// The <c>rides.transitions.to_state</c> value that means a trip finished (D5' §6).
    /// </summary>
    /// <remarks>
    /// <b>The transition, not the ride's current state.</b> A ride never rests in <c>Completed</c> —
    /// ride-svc moves it to <c>PaymentPending</c> inside the same transaction (C022/C032) — so
    /// "completed trips today" cannot be a <c>WHERE state = 'Completed'</c>. It also must not be a
    /// count of terminal states: <c>Paid</c>, <c>CashSettled</c> and <c>CashOnDeliveryCollected</c>
    /// are reached when the <em>money</em> settles, which can be the next day, and the cancelled and
    /// no-show terminals are trips that did not happen. <c>rides.transitions</c> is append-only with
    /// one row per move, so the row where <c>to_state = 'Completed'</c> is the trip's end and its
    /// <c>ts</c> is when it ended.
    /// </remarks>
    public const string RideCompleted = "Completed";

    /// <summary>
    /// The <c>fares.ride_payments.state</c> values that mean the fare was actually collected (R-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly fare-svc's <c>RidePaymentStates.Terminal</c> — the set on which a driver's earning
    /// posts. Gross fare is what the platform's riders paid, so it is the same set for the same
    /// reason: a fare that has not reached one of these has not been collected, and one that has is
    /// not going to be collected twice.
    /// </para>
    /// <para>
    /// <c>Disputed</c> is absent because it is a terminal of the <em>ride</em> and not of the money;
    /// <c>Overpaid</c> because R-19's late callback means a refund is owed, not that revenue
    /// doubled; <c>Refunded</c> and <c>PartiallyRefunded</c> because the money went back.
    /// </para>
    /// </remarks>
    public static readonly string[] SettledPaymentStates =
        ["Succeeded", "FellBackToCash", "CashOnDeliveryCollected", "DriverConfirmedQR"];

    /// <summary>
    /// <c>iam.user_roles.role</c> for a passenger — what a "new rider" is counted from.
    /// </summary>
    /// <remarks>
    /// <b>The grant row, not <c>iam.users.role</c>.</b> `iam.users.role` is the account's
    /// <em>primary</em> role and it moves: a passenger who later signs up to drive would silently
    /// leave a past day's "new riders" the next time that day was recomputed. `iam.user_roles` is
    /// insert-only with <c>ON CONFLICT DO NOTHING</c> (C026's own comment: "an idempotent retry is
    /// not a new decision"), so <c>granted_at</c> is written once and a past day's count never moves
    /// under the rollup.
    /// </remarks>
    public const string PassengerRole = "passenger";

    /// <summary><c>iam.user_roles.role</c> for a driver. Same argument as <see cref="PassengerRole"/>.</summary>
    public const string DriverRole = "driver";

    /// <summary><c>billing.daily_fee_charges.status</c> for a fee that moved money (D-13).</summary>
    /// <remarks>
    /// <c>WAIVED_FIRST_TRIP</c> rows carry <c>amount_minor = 0</c> by CHECK, so filtering is
    /// belt-and-braces on the arithmetic — but it is not belt-and-braces on the meaning: revenue is
    /// fees that were charged, and a waived first trip is the platform choosing not to charge.
    /// </remarks>
    public const string DailyFeePaid = "PAID";

    /// <summary><c>dispatch.driver_presence.state</c> for a driver who is not online.</summary>
    /// <remarks>
    /// The live card counts every state that is not this one — <c>AVAILABLE</c>, <c>OFFERED</c> and
    /// <c>ON_RIDE</c>. A driver carrying a passenger is online; an "online drivers" card that
    /// dropped them the moment they earned something would fall as the platform got busier.
    /// </remarks>
    public const string PresenceOffline = "OFFLINE";

    /// <summary><c>registry.document_fields.verify_status</c> for a field awaiting an officer (AL-29).</summary>
    public const string FieldPending = "pending";

    /// <summary><c>registry.documents.kind</c> for the driving licence — SCR-AP-003's first queue.</summary>
    public const string DrivingLicenceKind = "driving_license";

    /// <summary><c>registry.onboarding_steps.status</c> holding a vehicle in the second queue (AL-30).</summary>
    public const string StepPendingReview = "pending_review";

    /// <summary><c>registry.fleets.status</c> for an organisation in the third queue (AL-03, US-13.A7).</summary>
    public const string FleetPending = "PENDING";

    /// <summary><c>support.tickets.status</c> for a ticket that is finished with.</summary>
    /// <remarks>
    /// The card counts everything else — <c>OPEN</c> <em>and</em> <c>IN_PROGRESS</c>. "Open tickets"
    /// on an operations dashboard is the queue somebody still has to deal with, and a ticket an
    /// agent has picked up is very much still one of those.
    /// </remarks>
    public const string TicketResolved = "RESOLVED";
}
