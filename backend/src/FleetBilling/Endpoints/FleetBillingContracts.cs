using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;

namespace MageRide.FleetBilling.Endpoints;

/// <summary>One invoice, as <c>fleet-billing.yaml</c>'s <c>FleetInvoice</c> prints it.</summary>
/// <param name="AmountMinor">
/// Σ of the per-vehicle lines. Zero when <c>status</c> is FREE — the first month carries no amount.
/// </param>
public sealed record FleetInvoiceResponse(
    Guid InvoiceId,
    DateOnly PeriodMonth,
    DateTimeOffset PeriodMonthTzAt,
    int VehicleCount,
    long AmountMinor,
    string Currency,
    string Status,
    DateTimeOffset? DueAt,
    DateTimeOffset? OverdueAt,
    DateTimeOffset? SettledAt,
    Guid? JournalEntryId);

/// <summary>One vehicle's line on an invoice (US-13.10's breakdown).</summary>
public sealed record FleetInvoiceLineResponse(
    Guid VehicleId,
    string RegistrationNumber,
    string VehicleType,
    long AmountMinor,
    string Currency,
    string Status);

/// <summary>An invoice with its breakdown.</summary>
/// <param name="LineSumMinor">
/// Σ of <paramref name="Lines"/>, computed on the way out. It equals <c>invoice.amountMinor</c>, and
/// it is returned rather than assumed so a client — and the C060 definition of done — can check
/// rather than trust.
/// </param>
public sealed record FleetInvoiceDetailResponse(
    FleetInvoiceResponse Invoice,
    IReadOnlyList<FleetInvoiceLineResponse> Lines,
    long LineSumMinor);

/// <summary>The receipt for a settled invoice (US-13.10b's "downloadable receipt/invoice").</summary>
public sealed record FleetInvoiceReceiptResponse(
    Guid InvoiceId,
    Guid FleetId,
    string FleetName,
    DateOnly PeriodMonth,
    long AmountMinor,
    string Currency,
    int VehicleCount,
    DateTimeOffset SettledAt,
    Guid JournalEntryId);

/// <summary>The fleet wallet, as SCR-FP-010 draws it.</summary>
/// <param name="OutstandingMinor">Σ of the organisation's DUE and OVERDUE invoices.</param>
/// <param name="AvailableMinor">
/// <c>balanceMinor − outstandingMinor</c>. <b>Signed</b>, unlike a driver's — a fleet that owes more
/// than it holds is the state the screen most needs to draw, and flooring it at zero would render
/// "you can cover this" over a shortfall.
/// </param>
public sealed record FleetWalletResponse(
    long BalanceMinor,
    long OutstandingMinor,
    long AvailableMinor,
    string Currency,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<FleetWalletMovementResponse> Movements);

/// <summary>One line of the fleet wallet's history.</summary>
public sealed record FleetWalletMovementResponse(
    Guid EntryId,
    string Kind,
    long AmountMinor,
    long BalanceAfterMinor,
    string? Description,
    DateTimeOffset Ts);

/// <summary>The body of <c>POST /v1/fleets/{fleetId}/wallet/topup</c>.</summary>
public sealed record FleetTopupBody(long? AmountMinor, string? Method, string? ReturnUrl);

/// <summary>What an initiated top-up hands back for the portal to open.</summary>
public sealed record FleetTopupResponse(
    Guid TopupId,
    string State,
    long AmountMinor,
    string Currency,
    string Method,
    string? RedirectUrl,
    string? SessionToken,
    string? PaymentLink,
    string? QrPayload,
    DateTimeOffset CreatedAt,
    bool Expired);

/// <summary>The two provider callbacks, in the shape D6' §7.1/§7.2 prints them.</summary>
public sealed record TopupCallbackBody(
    string? ProviderTransactionId,
    string? TopupId,
    string? OrderId,
    string? Status,
    long? AmountMinor);

/// <summary>What a callback did. 200 for a redelivery too — that is what stops a provider retrying.</summary>
public sealed record TopupCallbackResponse(Guid TopupId, string State, bool Credited, bool Replayed);

/// <summary>What one internal run did.</summary>
public sealed record BillingRunResponse(
    DateOnly PeriodMonth,
    int InvoicesRaised,
    int LinesAdded,
    int SettlementsAttempted,
    int Settled,
    int Insufficient,
    int MarkedOverdue,
    int Notified);

/// <summary>Parses the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").</summary>
/// <remarks>
/// The same twelve lines wallet-svc, fleet-svc, reputation-svc and subscription-svc carry. Per
/// service rather than in the kernel because each names its own fields in the error, which is what
/// makes a 400 actionable.
/// </remarks>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}
