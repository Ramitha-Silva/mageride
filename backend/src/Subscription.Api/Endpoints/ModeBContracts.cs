using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.ModeB;
using MageRide.Subscriptions.Persistence;

namespace MageRide.Subscriptions.Endpoints;

// =============================================================================================
// The Epic 23 wire shapes of backend/contracts/subscription.yaml. The contract wins over this file:
// it is what C012/C013 generate the KMP client from and what C118 asserts the running service
// against.
// =============================================================================================

/// <summary>`POST /v1/mode-b/{vehicleId}/access-requests` (item 8).</summary>
/// <param name="Note">
/// Accepted and not stored: <c>subscription.access_requests</c> has no column for it and D4' §18b
/// prints none. Recorded as a gap in the C048 handoff rather than answered with a 400 — a client
/// sending the field the contract declares should not be refused.
/// </param>
public sealed record RequestModeBAccessBody(string? Note);

/// <summary>`POST /v1/mode-b/access-requests/{requestId}/reject`.</summary>
public sealed record RejectModeBAccessBody(string? Reason);

/// <summary>One queued or decided request (item 15).</summary>
public sealed record AccessRequestResponse(
    Guid RequestId,
    Guid VehicleId,
    Guid PassengerId,
    string? PassengerName,
    string? PassengerMobileMasked,
    string Status,
    DateTimeOffset CreatedAt)
{
    public static AccessRequestResponse From(AccessRequestRow row, UserContact? contact)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new AccessRequestResponse(
            row.RequestId,
            row.VehicleId,
            row.PassengerId,
            contact?.Name,
            PhoneMask.Mask(contact?.Phone),
            row.Status,
            row.CreatedAt);
    }
}

/// <summary>What an accept produced — the grant and the subscription it started.</summary>
public sealed record AcceptModeBAccessResponse(Guid RequestId, Guid GrantId, Guid SubscriptionId);

/// <summary>A passenger's Mode B subscription card (SCR-PA-025).</summary>
public sealed record ModeBSubscriptionResponse(
    Guid SubscriptionId,
    Guid VehicleId,
    Guid PassengerId,
    string Billing,
    long? MonthlyFareMinor,
    string Currency,
    string Cycle,
    int? JoinDay,
    DateOnly? NextDue,
    DateTimeOffset? NextDueTzAt,
    string Status)
{
    public static ModeBSubscriptionResponse From(SubscriptionRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ModeBSubscriptionResponse(
            row.SubscriptionId,
            row.VehicleId,
            row.PassengerId,
            row.Billing,
            row.MonthlyFareMinor,
            row.Currency,
            row.Cycle,
            row.JoinDay,
            row.NextDue,
            row.NextDue is null ? null : row.NextDueTzAt,
            row.Status);
    }
}

/// <summary>One line of the owner's roster (item 16, SCR-FP-011).</summary>
public sealed record SubscriberRowResponse(
    Guid SubscriberId,
    Guid PassengerId,
    string? Name,
    string? MobileMasked,
    string Billing,
    long? MonthlyFareMinor,
    string? Currency,
    string? Cycle,
    string? ThisMonthStatus,
    bool Muted,
    string Status)
{
    public static SubscriberRowResponse From(RosterEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var row = entry.Row;

        return new SubscriberRowResponse(
            row.SubscriberId,
            row.PassengerId,
            entry.Contact?.Name,
            PhoneMask.Mask(entry.Contact?.Phone),
            // A grant with no subscription row at all is unreachable through this service — the
            // accept writes both in one transaction — so the fallback is for a hand-written row
            // rather than a state the platform produces. Free is the safe answer: it collects
            // nothing.
            row.Billing ?? SubscriptionBilling.Free,
            row.MonthlyFareMinor,
            row.Currency,
            row.Cycle,
            ThisMonthStatusOf(row.ThisMonthPaymentStatus),
            string.Equals(row.GrantStatus, GrantStatuses.Unsubscribed, StringComparison.Ordinal),
            row.GrantStatus);
    }

    /// <summary>
    /// The three values the roster shows for the current month.
    /// </summary>
    /// <remarks>
    /// <c>initiated</c> and "no row at all" are both <b>unpaid</b>, and deliberately: a passenger who
    /// opened the pay sheet and walked away has not paid, and showing the owner anything else would
    /// have them stop chasing a month that never arrived.
    /// </remarks>
    private static string ThisMonthStatusOf(string? paymentStatus) => paymentStatus switch
    {
        SubscriptionPaymentStatuses.Paid => "paid",
        SubscriptionPaymentStatuses.PendingVerification => "pending_verification",
        _ => "unpaid",
    };
}

/// <summary>`PUT /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/fare` (US-23.7).</summary>
public sealed record SetSubscriberFareBody(long? MonthlyFareMinor);

/// <summary>`POST /v1/mode-b/{vehicleId}/subscribers/{subscriberId}/mark-cash` (US-23.6).</summary>
public sealed record MarkCashBody(long? AmountMinor, string? PeriodMonth);

/// <summary>`POST /v1/mode-b/subscriptions/{subscriptionId}/pay` (US-23.3).</summary>
public sealed record PayModeBSubscriptionBody(string? Method, string? PeriodMonth);

/// <summary>Where the passenger sends the money (AL-49).</summary>
public sealed record PayToResponse(
    string? LankaqrImageUrl, string? Bank, string? Branch, string? AccountNo, string? AccountHolderName)
{
    public static PayToResponse? From(PayToDetails? details) =>
        details is null
            ? null
            : new PayToResponse(
                details.LankaqrImageUrl,
                details.Bank,
                details.Branch,
                details.AccountNo,
                details.AccountHolderName);
}

/// <summary>One subscription payment (items 16d–16i).</summary>
/// <param name="RedirectUrl">
/// A gateway session to follow. Always absent today — see the OnePay note in
/// <c>ModeBPaymentService</c>: a session would have to be opened against the <em>owner's</em>
/// merchant account and no schema binds one to a fleet.
/// </param>
public sealed record SubscriptionPaymentResponse(
    Guid PaymentId,
    Guid SubscriptionId,
    string Method,
    long AmountMinor,
    string Currency,
    string Status,
    DateOnly PeriodMonth,
    DateTimeOffset PeriodMonthTzAt,
    PayToResponse? PayTo,
    string? RedirectUrl,
    string? QrPayload,
    string? SlipUrl,
    DateTimeOffset? PaidAt)
{
    public static SubscriptionPaymentResponse From(
        PaymentRow row, PayToDetails? payTo = null, string? slipUrl = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new SubscriptionPaymentResponse(
            row.PaymentId,
            row.SubscriptionId,
            row.Method,
            row.AmountMinor,
            row.Currency,
            row.Status,
            row.PeriodMonth,
            row.PeriodMonthTzAt,
            PayToResponse.From(payTo),
            null,
            null,
            slipUrl,
            row.PaidAt);
    }
}

/// <summary>The OnePay / LankaQR confirmation body (R-19).</summary>
public sealed record SubscriptionProviderCallbackBody(
    string? ProviderTransactionId, string? PaymentId, string? Status, long? AmountMinor);

/// <summary>What a provider callback is answered with, first delivery and redelivery alike.</summary>
public sealed record CallbackAcceptedResponse(bool Received);

/// <summary>
/// Role-masks an MSISDN the way <c>_shared.yaml#/components/schemas/PhoneMasked</c> spells it —
/// <c>+9477*****67</c>, only the last two digits in the clear (AL-40/41/42).
/// </summary>
/// <remarks>
/// <b>The queue and the roster are directory reads, so they get the masked form.</b> A driver
/// deciding whether to accept a stranger onto their school van needs to recognise the number, not to
/// dial it; the clear number is a <c>PII_READ</c>-audited detail read that belongs to admin-bff.
/// </remarks>
internal static class PhoneMask
{
    /// <summary>Characters kept at the front — <c>+94</c> and the operator prefix.</summary>
    private const int PrefixLength = 5;

    /// <summary>Digits left in the clear at the end.</summary>
    private const int SuffixLength = 2;

    public static string? Mask(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var value = phone.Trim();

        if (value.Length <= SuffixLength)
        {
            return new string('*', value.Length);
        }

        var prefix = Math.Min(PrefixLength, value.Length - SuffixLength);
        var masked = value.Length - prefix - SuffixLength;

        return string.Concat(value.AsSpan(0, prefix), new string('*', masked), value.AsSpan(value.Length - SuffixLength));
    }
}
