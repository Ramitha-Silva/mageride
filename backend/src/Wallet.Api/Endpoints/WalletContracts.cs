using MageRide.Shared.Primitives;

namespace MageRide.Wallet.Endpoints;

// =============================================================================================
// The wire shapes of backend/contracts/wallet.yaml. The contract wins over this file: it is what
// C012/C013 generate the KMP client from and what C118 asserts the running service against.
//
// Public because the test suite deserialises them, the same reason Query.Api's and Content.Api's
// contracts are public.
// =============================================================================================

/// <summary>`GET /v1/wallet/{userId}` (US-9.7).</summary>
public sealed record WalletResponse(
    Guid UserId,
    long BalanceMinor,
    long AvailableMinor,
    long OutstandingDebtMinor,
    string Currency,
    DateTimeOffset UpdatedAt);

/// <summary>One line of the wallet history (US-9A.19).</summary>
/// <param name="AmountMinor">
/// Signed — a debit is negative. One of the ledger columns §0 exempts from the non-negative rule.
/// </param>
public sealed record WalletTransactionResponse(
    Guid TransactionId,
    Guid EntryId,
    string Kind,
    long AmountMinor,
    string Currency,
    long BalanceAfterMinor,
    string? Reference,
    DateTimeOffset OccurredAt);

/// <summary>One credit transfer, from the caller's point of view (US-9A.11).</summary>
public sealed record TransferResponse(
    Guid TransferId,
    Guid CounterpartyDriverId,
    string? CounterpartyName,
    long AmountMinor,
    string Currency,
    string Direction,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>`POST /v1/wallet/topup/*` and `GET /v1/wallet/topup/{topupId}`.</summary>
public sealed record TopupResponse(
    Guid TopupId,
    string State,
    long AmountMinor,
    string Currency,
    string? RedirectUrl,
    string? SessionToken,
    string? PaymentLink,
    string? QrPayload);

/// <summary>A provider callback body (D6' §7.1's <c>{orderId, providerTransactionId, status}</c>).</summary>
public sealed record TopupCallbackBody(
    string? ProviderTransactionId,
    string? TopupId,
    string? OrderId,
    string? Status,
    long? AmountMinor,
    string? Currency);

/// <summary>What a callback endpoint answers — the same body for a redelivery.</summary>
public sealed record CallbackAcceptedResponse(bool Received);

/// <summary>`POST /v1/wallet/topup/onepay` · `/lankaqr`.</summary>
public sealed record TopupRequestBody(long? AmountMinor, string? ReturnUrl);

/// <summary>`POST /v1/wallet/credit-transfer/initiate` (US-9A.12).</summary>
public sealed record InitiateTransferBody(string? RecipientDriverId, long? AmountMinor);

/// <summary>`POST /v1/wallet/credit-transfer/request` (US-9.10).</summary>
public sealed record RequestTransferBody(string? HolderDriverId, long? AmountMinor);

/// <summary>`POST /v1/wallet/voucher/purchase` (US-9.19).</summary>
public sealed record PurchaseVoucherBody(long? DenominationMinor, string? GatewayRef);

/// <summary>What a purchase did.</summary>
public sealed record VoucherPurchaseResponse(
    Guid PurchaseId,
    long DenominationMinor,
    int DiscountBps,
    long PaidMinor,
    long CreditedMinor,
    string Currency,
    long BalanceAfterMinor,
    Guid? EntryId,
    DateTimeOffset CreatedAt);

/// <summary>One row of the bulk-voucher ladder.</summary>
public sealed record VoucherTierResponse(
    long DenominationMinor, int DiscountBps, bool Active, DateTimeOffset? UpdatedAt);

/// <summary>The admin view: the ladder plus what has been bought at each rung (US-9A.15).</summary>
public sealed record VoucherTierUsageResponse(
    long DenominationMinor,
    int DiscountBps,
    bool Active,
    DateTimeOffset? UpdatedAt,
    int PurchaseCount,
    long PurchasedValueMinor);

/// <summary>`GET /v1/wallet/voucher/discount-tiers` and both admin tier routes.</summary>
public sealed record VoucherTiersResponse(IReadOnlyList<VoucherTierResponse> Tiers);

/// <summary>`GET /v1/wallet/admin/voucher-discount-tiers`.</summary>
public sealed record AdminVoucherTiersResponse(IReadOnlyList<VoucherTierUsageResponse> Tiers);

/// <summary>`PUT /v1/wallet/admin/voucher-discount-tiers`.</summary>
public sealed record UpdateVoucherTiersBody(IReadOnlyList<VoucherTierBody>? Tiers);

/// <summary>One submitted tier.</summary>
public sealed record VoucherTierBody(long? DenominationMinor, int? DiscountBps, bool? Active);

/// <summary>`POST /v1/internal/wallet/{driverId}/debit` · `/credit`.</summary>
public sealed record LedgerPostingBody(
    long? AmountMinor, string? Kind, string? IdempotencyKey, string? Description, string? Reference);

/// <summary>What an internal posting did.</summary>
/// <param name="AmountMinor">Signed as it was posted — a debit is negative.</param>
public sealed record LedgerPostingResultResponse(
    Guid EntryId, Guid AccountId, long AmountMinor, long BalanceAfterMinor, bool Replayed);

/// <summary>
/// A wallet-owning account, resolved or created. <b>Δ C060.</b>
/// </summary>
/// <remarks>
/// The one thing on the internal plane that moves no money. <c>billing.accounts</c> has exactly one
/// writer, and a fleet's account is created lazily by its first posting — which leaves
/// fleet-billing-svc unable to record which wallet a top-up session will credit until the
/// organisation has already been invoiced once. Rather than have it post a synthetic movement to
/// force the row into existence, the create is exposed on its own.
/// </remarks>
public sealed record LedgerAccountResponse(
    Guid AccountId, Guid OwnerId, string OwnerType, string Currency, long BalanceMinor);

/// <summary>Parses the identifiers D3' types as <c>Ulid</c> ("ULID or UUID, rendered canonically").</summary>
/// <remarks>
/// The same twelve lines reputation-svc carries. Per service rather than in the kernel because each
/// one names its own fields in the error, which is what makes a 400 actionable.
/// </remarks>
internal static class RequestIds
{
    public static Guid Require(string? value, string field) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new Shared.Errors.MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"{field} is required and must be a ULID or a UUID."],
            });

    public static Guid? Optional(string? value) =>
        Ulids.TryParse(value, out var parsed) && parsed != Guid.Empty ? parsed : null;
}
