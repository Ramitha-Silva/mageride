using System.Collections.Frozen;

namespace MageRide.Fare.Domain;

/// <summary>
/// What caused a payment to move. One trigger per thing that can actually happen to a payment.
/// </summary>
/// <remarks>
/// Triggers rather than "set the state to X": the caller says what occurred and the machine decides
/// where that lands, so a handler cannot invent a transition D5' §8.1 does not have. Every value
/// below is named by a spec line, and <see cref="PaymentStateMachine"/> is the only place they are
/// resolved.
/// </remarks>
public enum PaymentTrigger
{
    /// <summary>The passenger chose a gateway rail; fare-svc opened a session (D5' §8.1).</summary>
    GatewaySessionOpened,

    /// <summary>The gateway confirmed. <b>OnePay-only for a verified <c>Succeeded</c></b> (D-10).</summary>
    GatewaySucceeded,

    /// <summary>The gateway refused, errored or timed out (90 s, D6' §7.1).</summary>
    GatewayFailed,

    /// <summary>The passenger asked to try again — this row is closed and a new one carries the retry.</summary>
    RetryRequested,

    /// <summary>US-8.15's cash fallback, and the terminal an ordinary cash ride settles at.</summary>
    SettledInCash,

    /// <summary>P-08: a package booked COD is awaiting collection at delivery.</summary>
    CodAwaited,

    /// <summary>P-08: the driver confirmed the cash arrived at the door.</summary>
    CodCollected,

    /// <summary>AL-47: the passenger says they paid into the driver's own bank QR.</summary>
    QrClaimed,

    /// <summary>AL-47: the driver's bank app saw the money. <b>Terminal.</b></summary>
    QrConfirmed,

    /// <summary>AL-47 / E-05: either party disputes, or a COD went uncollected past P-14's 24 h.</summary>
    Disputed,

    /// <summary>R-19: a provider <c>Succeeded</c> arrived after the ride was already settled in cash.</summary>
    LateGatewaySucceeded,

    /// <summary>E-05: Finance reversed the whole amount.</summary>
    RefundedInFull,

    /// <summary>E-05: Finance reversed part of it.</summary>
    RefundedInPart,
}

/// <summary>Why a transition was refused, so the caller can pick the right status code.</summary>
public enum TransitionRefusal
{
    None,

    /// <summary>The payment has already reached a state the money cannot leave.</summary>
    AlreadySettled,

    /// <summary>Legal state, wrong moment — a confirm before a session, a COD collect on a card ride.</summary>
    NotFromHere,
}

/// <summary>
/// D5' §8.1 and the AL-47 attestation pair, as one table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The machine is data, not control flow.</b> Every handler on this surface asks
/// <see cref="Resolve"/> and applies what it is given; none of them writes a state name of its own.
/// That is what makes "every state and transition in D5' §8.1 is exercised by a test" a statement
/// about a table somebody can read against the diagram, rather than a claim about eleven scattered
/// <c>if</c>s.
/// </para>
/// <para>
/// <b>Two states in the CHECK are never written by this machine, and that is deliberate.</b>
/// <c>Retried</c> closes the row a passenger retried <em>from</em>; the retry itself is a new row
/// pointing back through <c>retry_of_payment_id</c> (§11.8, and 1002's header). <c>Pending</c> is
/// only reachable for the two gateway rails — cash, COD and driver-QR never touch a gateway, so
/// there is nothing to be pending on.
/// </para>
/// <para>
/// <b><c>FellBackToCash</c> is the cash terminal, not only the fallback.</b> D5' §8.1 names cash as
/// the default method and gives the payment CHECK no separate "cash settled" value —
/// <c>CashSettled</c> is a <em>ride</em> state (0601), and ride-svc maps <c>FellBackToCash</c> onto
/// it. So a ride paid in cash and a ride that fell back to cash reach the same payment state by
/// design; the method column is what tells them apart. Raised as a naming observation in the C050
/// handoff.
/// </para>
/// </remarks>
public static class PaymentStateMachine
{
    /// <summary>Where a trigger lands, and whether that lands the money.</summary>
    /// <param name="To">The state to write.</param>
    /// <param name="ClosesPayment">
    /// Whether R-05's terminal is reached — the point at which the driver's earning may post and
    /// ride-svc is told. <c>Disputed</c> closes the payment without earning anything.
    /// </param>
    public sealed record Transition(string From, PaymentTrigger Trigger, string To, bool ClosesPayment);

    /// <summary>
    /// The states from which no trigger may move the money.
    /// </summary>
    /// <remarks>
    /// <c>Overpaid</c> is <em>not</em> here: it is a settled payment that was then paid twice, and
    /// R-19's whole point is that it still has a refund ahead of it. <c>Disputed</c> is not here
    /// either — E-05 resolves a dispute into a refund.
    /// </remarks>
    public static readonly FrozenSet<string> Closed = new[]
    {
        RidePaymentStates.Succeeded,
        RidePaymentStates.FellBackToCash,
        RidePaymentStates.CashOnDeliveryCollected,
        RidePaymentStates.DriverConfirmedQR,
        RidePaymentStates.Retried,
        RidePaymentStates.Refunded,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<(string From, PaymentTrigger Trigger), Transition> Table =
        BuildTable();

    /// <summary>Every transition the machine has, for the tests and for the documentation.</summary>
    public static IReadOnlyCollection<Transition> All => Table.Values;

    /// <summary>
    /// Where <paramref name="trigger"/> takes a payment currently in <paramref name="from"/>.
    /// </summary>
    public static bool TryResolve(string from, PaymentTrigger trigger, out Transition transition, out TransitionRefusal refusal)
    {
        if (Table.TryGetValue((from, trigger), out var found))
        {
            transition = found;
            refusal = TransitionRefusal.None;
            return true;
        }

        transition = null!;

        // The distinction is the caller's status code: a settled payment is `409
        // payment-already-settled` and a wrong-moment one is an ordinary conflict. Collapsing them
        // would tell a passenger their card was charged when it was not.
        refusal = Closed.Contains(from) ? TransitionRefusal.AlreadySettled : TransitionRefusal.NotFromHere;
        return false;
    }

    /// <summary>The state a payment opens in for a given method, before anything has happened to it.</summary>
    /// <remarks>
    /// Every method starts at <c>Initiated</c> — §11.8's entry — including cash. What differs is the
    /// very next trigger, and that is the method's business rather than the machine's.
    /// </remarks>
    public const string Initial = RidePaymentStates.Initiated;

    private static FrozenDictionary<(string, PaymentTrigger), Transition> BuildTable()
    {
        // D5' §8.1's diagram, edge for edge, in its own order. Anything not written here cannot
        // happen — including every backwards edge, which is why there is no "un-fail" trigger.
        Transition[] transitions =
        [
            // Initiated --> Pending: OnePay/LankaQR provider (timeout 90 s)
            new(RidePaymentStates.Initiated, PaymentTrigger.GatewaySessionOpened, RidePaymentStates.Pending, false),

            // Pending --> Succeeded: provider ok
            new(RidePaymentStates.Pending, PaymentTrigger.GatewaySucceeded, RidePaymentStates.Succeeded, true),

            // Pending --> Failed: provider error/timeout
            new(RidePaymentStates.Pending, PaymentTrigger.GatewayFailed, RidePaymentStates.Failed, false),

            // Failed --> Retried: passenger retry (new row, retry_of_payment_id)
            new(RidePaymentStates.Failed, PaymentTrigger.RetryRequested, RidePaymentStates.Retried, false),

            // Failed --> FellBackToCash: after 3 retries / override (US-8.15)
            new(RidePaymentStates.Failed, PaymentTrigger.SettledInCash, RidePaymentStates.FellBackToCash, true),
            // …and from Pending, because US-8.15's passenger is stranded mid-round-trip, and from
            // Initiated, because that is where an ordinary cash ride settles from.
            new(RidePaymentStates.Pending, PaymentTrigger.SettledInCash, RidePaymentStates.FellBackToCash, true),
            new(RidePaymentStates.Initiated, PaymentTrigger.SettledInCash, RidePaymentStates.FellBackToCash, true),

            // Pending --> CashOnDelivery: package COD (P-08)
            new(RidePaymentStates.Pending, PaymentTrigger.CodAwaited, RidePaymentStates.CashOnDelivery, false),
            // A COD parcel never opens a gateway session, so the edge that actually fires is this
            // one. §8.1 draws it from Pending because the diagram has no other left-hand state.
            new(RidePaymentStates.Initiated, PaymentTrigger.CodAwaited, RidePaymentStates.CashOnDelivery, false),

            // CashOnDelivery --> CashOnDeliveryCollected: driver "Cash received"
            new(
                RidePaymentStates.CashOnDelivery,
                PaymentTrigger.CodCollected,
                RidePaymentStates.CashOnDeliveryCollected,
                true),

            // FellBackToCash --> Overpaid: late provider Succeeded after cash (R-19)
            new(RidePaymentStates.FellBackToCash, PaymentTrigger.LateGatewaySucceeded, RidePaymentStates.Overpaid, false),
            // The same accident on the other cash-shaped terminal: a COD collected in the hall and a
            // card that authorised an hour later is the identical overpayment.
            new(
                RidePaymentStates.CashOnDeliveryCollected,
                PaymentTrigger.LateGatewaySucceeded,
                RidePaymentStates.Overpaid,
                false),

            // Overpaid --> Refunded: admin refund queue
            new(RidePaymentStates.Overpaid, PaymentTrigger.RefundedInFull, RidePaymentStates.Refunded, true),

            // -- AL-47, the driver-QR attestation pair (Δ 2026-07-05 #2) ---------------------------
            // The passenger claims; the driver is prompted. Not terminal — nobody has confirmed.
            new(
                RidePaymentStates.Initiated,
                PaymentTrigger.QrClaimed,
                RidePaymentStates.QrClaimedByPassenger,
                false),

            // "Valid with or without a prior passenger claim, because the driver's bank app is the
            // only party that actually saw the money."
            new(
                RidePaymentStates.QrClaimedByPassenger,
                PaymentTrigger.QrConfirmed,
                RidePaymentStates.DriverConfirmedQR,
                true),
            new(RidePaymentStates.Initiated, PaymentTrigger.QrConfirmed, RidePaymentStates.DriverConfirmedQR, true),

            // Either party disputes. A claim with no confirm is the case AL-47 exists for, but a
            // dispute is reachable before one too.
            new(RidePaymentStates.QrClaimedByPassenger, PaymentTrigger.Disputed, RidePaymentStates.Disputed, true),
            new(RidePaymentStates.Initiated, PaymentTrigger.Disputed, RidePaymentStates.Disputed, true),
            // P-14: a COD nobody collected inside 24 h.
            new(RidePaymentStates.CashOnDelivery, PaymentTrigger.Disputed, RidePaymentStates.Disputed, true),

            // -- E-05, the refund of a payment that did succeed -----------------------------------
            new(RidePaymentStates.Succeeded, PaymentTrigger.RefundedInFull, RidePaymentStates.Refunded, false),
            new(
                RidePaymentStates.Succeeded,
                PaymentTrigger.RefundedInPart,
                RidePaymentStates.PartiallyRefunded,
                false),
            new(RidePaymentStates.Disputed, PaymentTrigger.RefundedInFull, RidePaymentStates.Refunded, false),
            new(RidePaymentStates.Disputed, PaymentTrigger.RefundedInPart, RidePaymentStates.PartiallyRefunded, false),
            new(
                RidePaymentStates.PartiallyRefunded,
                PaymentTrigger.RefundedInPart,
                RidePaymentStates.PartiallyRefunded,
                false),
        ];

        return transitions.ToFrozenDictionary(t => (t.From, t.Trigger));
    }
}
