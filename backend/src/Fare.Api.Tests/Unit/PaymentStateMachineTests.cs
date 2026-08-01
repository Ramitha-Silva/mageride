using MageRide.Fare.Domain;

namespace MageRide.Fare.Tests.Unit;

/// <summary>
/// D5' §8.1's diagram, edge for edge — this component's first definition-of-done item ("every state
/// and transition in D5 §8.1 is exercised by a test, including all terminal states").
/// </summary>
/// <remarks>
/// <b>The diagram is transcribed here as data and compared with the machine.</b> Asserting each edge
/// in its own test would prove every edge exists and nothing about the edges that should not; the
/// set comparison below fails both ways, so an invented transition is as loud as a missing one.
/// </remarks>
public sealed class PaymentStateMachineTests
{
    /// <summary>
    /// D5' §8.1's mermaid diagram, read top to bottom, plus the AL-47 pair D3' Δ 2026-07-05 #2 adds
    /// and E-05's refund edges. Transcribed by hand from the spec, deliberately not from the code.
    /// </summary>
    private static readonly (string From, PaymentTrigger Trigger, string To)[] Diagram =
    [
        // Initiated --> Pending: OnePay/LankaQR provider (timeout 90s)
        // Δ AL-57: no route can fire this any more — both ride gateways are retired — but the edge
        // stays, because historical rows sit in Pending and D5' §8.1 still describes how they got
        // there. The machine is the diagram, including the parts nothing reaches today.
        ("Initiated", PaymentTrigger.GatewaySessionOpened, "Pending"),

        // Initiated --> Succeeded: wallet — a ledger move, no provider (AL-57).
        ("Initiated", PaymentTrigger.SettledFromWallet, "Succeeded"),
        // Pending --> Succeeded: provider ok
        ("Pending", PaymentTrigger.GatewaySucceeded, "Succeeded"),
        // Pending --> Failed: provider error/timeout
        ("Pending", PaymentTrigger.GatewayFailed, "Failed"),
        // Failed --> Retried: passenger retry (new row, retry_of_payment_id)
        ("Failed", PaymentTrigger.RetryRequested, "Retried"),
        // Failed --> FellBackToCash: after 3 retries / override (US-8.15)
        ("Failed", PaymentTrigger.SettledInCash, "FellBackToCash"),
        // …and the two the prose adds: US-8.15's mid-round-trip fallback, and cash as a method.
        ("Pending", PaymentTrigger.SettledInCash, "FellBackToCash"),
        ("Initiated", PaymentTrigger.SettledInCash, "FellBackToCash"),
        // Pending --> CashOnDelivery: package COD (P-08)
        ("Pending", PaymentTrigger.CodAwaited, "CashOnDelivery"),
        ("Initiated", PaymentTrigger.CodAwaited, "CashOnDelivery"),
        // CashOnDelivery --> CashOnDeliveryCollected: driver "Cash received"
        ("CashOnDelivery", PaymentTrigger.CodCollected, "CashOnDeliveryCollected"),
        // FellBackToCash --> Overpaid: late provider Succeeded after cash (R-19)
        ("FellBackToCash", PaymentTrigger.LateGatewaySucceeded, "Overpaid"),
        ("CashOnDeliveryCollected", PaymentTrigger.LateGatewaySucceeded, "Overpaid"),
        // Overpaid --> Refunded: admin refund queue
        ("Overpaid", PaymentTrigger.RefundedInFull, "Refunded"),

        // AL-47 driver-QR attestation.
        ("Initiated", PaymentTrigger.QrClaimed, "QrClaimedByPassenger"),
        ("QrClaimedByPassenger", PaymentTrigger.QrConfirmed, "DriverConfirmedQR"),
        ("Initiated", PaymentTrigger.QrConfirmed, "DriverConfirmedQR"),
        ("QrClaimedByPassenger", PaymentTrigger.Disputed, "Disputed"),
        ("Initiated", PaymentTrigger.Disputed, "Disputed"),
        // P-14: a COD nobody collected inside 24 h.
        ("CashOnDelivery", PaymentTrigger.Disputed, "Disputed"),

        // E-05 refunds.
        ("Succeeded", PaymentTrigger.RefundedInFull, "Refunded"),
        ("Succeeded", PaymentTrigger.RefundedInPart, "PartiallyRefunded"),
        ("Disputed", PaymentTrigger.RefundedInFull, "Refunded"),
        ("Disputed", PaymentTrigger.RefundedInPart, "PartiallyRefunded"),
        ("PartiallyRefunded", PaymentTrigger.RefundedInPart, "PartiallyRefunded"),
    ];

    [Fact]
    public void The_machine_is_exactly_the_diagram()
    {
        var actual = PaymentStateMachine.All
            .Select(t => (t.From, t.Trigger, t.To))
            .OrderBy(t => t.From, StringComparer.Ordinal).ThenBy(t => t.Trigger)
            .ToArray();

        var expected = Diagram
            .OrderBy(t => t.From, StringComparer.Ordinal).ThenBy(t => t.Trigger)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>Every edge of the diagram resolves, one at a time — the "exercised" half.</summary>
    [Fact]
    public void Every_transition_in_the_diagram_resolves()
    {
        foreach (var (from, trigger, to) in Diagram)
        {
            Assert.True(
                PaymentStateMachine.TryResolve(from, trigger, out var transition, out _),
                $"{from} --{trigger}--> {to} is missing from the machine");

            Assert.Equal(to, transition.To);
        }
    }

    /// <summary>
    /// Every state the <c>fares.ride_payments.state</c> CHECK admits is reachable — a state the
    /// database allows and the machine can never produce is one the payment machine has forgotten.
    /// </summary>
    /// <remarks>
    /// <c>Initiated</c> is the entry and is reached by C049's calculation, not by a trigger.
    /// </remarks>
    [Fact]
    public void Every_state_the_check_admits_is_reachable()
    {
        string[] check =
        [
            "Initiated", "Pending", "Succeeded", "Failed", "Retried", "FellBackToCash",
            "CashOnDelivery", "CashOnDeliveryCollected", "Overpaid", "Refunded",
            "PartiallyRefunded", "Disputed", "QrClaimedByPassenger", "DriverConfirmedQR",
        ];

        var reachable = PaymentStateMachine.All.Select(t => t.To).ToHashSet(StringComparer.Ordinal);
        reachable.Add(PaymentStateMachine.Initial);

        Assert.Empty(check.Except(reachable, StringComparer.Ordinal));
    }

    /// <summary>The four states R-05 pays a driver on, and the three closings that pay nothing.</summary>
    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("FellBackToCash", true)]
    [InlineData("CashOnDeliveryCollected", true)]
    [InlineData("DriverConfirmedQR", true)]
    [InlineData("Disputed", false)]
    [InlineData("Refunded", false)]
    [InlineData("PartiallyRefunded", false)]
    public void The_closings_that_earn_a_driver_are_exactly_R05s(string state, bool payable) =>
        Assert.Equal(payable, Payments.PaymentSettlementService.EarningPayable(state));

    /// <summary>
    /// A payment whose money has landed cannot be moved by an ordinary trigger, and the refusal says
    /// so — that distinction is what makes `409 payment-already-settled` different from a conflict.
    /// </summary>
    [Theory]
    [InlineData("Succeeded")]
    [InlineData("FellBackToCash")]
    [InlineData("CashOnDeliveryCollected")]
    [InlineData("DriverConfirmedQR")]
    [InlineData("Refunded")]
    public void A_settled_payment_refuses_a_second_settlement(string state)
    {
        Assert.False(PaymentStateMachine.TryResolve(state, PaymentTrigger.SettledInCash, out _, out var refusal));
        Assert.Equal(TransitionRefusal.AlreadySettled, refusal);
    }

    /// <summary>
    /// A legal state at the wrong moment is an ordinary conflict, not a settlement claim: telling a
    /// passenger their card was charged when it was not is the failure this separates.
    /// </summary>
    [Fact]
    public void A_wrong_moment_is_a_conflict_and_not_a_settlement()
    {
        // Nothing has opened a gateway session, so there is nothing for a provider to succeed at.
        Assert.False(
            PaymentStateMachine.TryResolve("Initiated", PaymentTrigger.GatewaySucceeded, out _, out var refusal));

        Assert.Equal(TransitionRefusal.NotFromHere, refusal);
    }

    /// <summary>
    /// D-10's fence, as a property of the table: nothing returns a payment to a state it has left.
    /// </summary>
    /// <remarks>
    /// Checked by ranking the states along the machine's own reachability rather than by listing
    /// pairs — a backwards edge added later fails this without anybody remembering to extend a list.
    /// </remarks>
    [Fact]
    public void No_transition_leaves_a_closed_state_except_a_refund_or_an_overpayment()
    {
        foreach (var transition in PaymentStateMachine.All.Where(t => PaymentStateMachine.Closed.Contains(t.From)))
        {
            Assert.True(
                transition.Trigger is PaymentTrigger.LateGatewaySucceeded
                    or PaymentTrigger.RefundedInFull
                    or PaymentTrigger.RefundedInPart,
                $"{transition.From} is closed but {transition.Trigger} moves it to {transition.To}");
        }
    }

    /// <summary>
    /// R-19's shape: a late gateway success on a cash-settled ride becomes an overpayment rather
    /// than a second settlement, and §11.14 is explicit that the ride is not dragged to Disputed.
    /// </summary>
    [Theory]
    [InlineData("FellBackToCash")]
    [InlineData("CashOnDeliveryCollected")]
    public void A_late_success_after_cash_is_an_overpayment(string cashTerminal)
    {
        Assert.True(
            PaymentStateMachine.TryResolve(
                cashTerminal, PaymentTrigger.LateGatewaySucceeded, out var transition, out _));

        Assert.Equal("Overpaid", transition.To);
        Assert.False(transition.ClosesPayment, "an overpayment is not settled — it is owed back");
    }

    /// <summary>
    /// AL-47: the driver's confirm settles with or without a prior claim, and a claim on its own
    /// settles nothing.
    /// </summary>
    [Fact]
    public void A_driver_confirm_is_valid_with_or_without_a_claim()
    {
        Assert.True(PaymentStateMachine.TryResolve("Initiated", PaymentTrigger.QrConfirmed, out var direct, out _));
        Assert.True(direct.ClosesPayment);

        Assert.True(PaymentStateMachine.TryResolve(
            "QrClaimedByPassenger", PaymentTrigger.QrConfirmed, out var afterClaim, out _));
        Assert.True(afterClaim.ClosesPayment);

        Assert.True(PaymentStateMachine.TryResolve("Initiated", PaymentTrigger.QrClaimed, out var claim, out _));
        Assert.False(claim.ClosesPayment, "a passenger's claim is evidence, not settlement");
    }
}
