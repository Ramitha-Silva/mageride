using MageRide.Shared.Auth;
using MageRide.Shared.Errors;
using MageRide.Shared.Primitives;
using MageRide.Wallet.Money;
using MageRide.Wallet.Persistence;
using Microsoft.AspNetCore.Http.HttpResults;

namespace MageRide.Wallet.Endpoints;

/// <summary>
/// <c>/v1/wallet/credit-transfer/**</c> — AL-01's driver-to-driver credit, in all four directions
/// (US-9.10, US-9.12, US-9.13, US-9A.12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Driver App only, and driver-to-driver only.</b> AL-01: "the credit-transfer endpoints are
/// Driver-App APIs, not portal APIs", and "reseller" is not a role, an account or a capability. Both
/// sides are ordinary driver accounts and the exact value moves.
/// </para>
/// <para>
/// <b>Driver ID, never a scanned code.</b> AL-34 removed SCR-DA/DI-023's QR path; there is no field on
/// any of these bodies for a scanned payload.
/// </para>
/// <para>
/// <b>subscription-svc's D3' routes forward here.</b> ADD §11.6 draws the request/approve workflow in
/// subscription-svc calling wallet-svc for the balance check and the movement, and `billing.*` has one
/// writer — so `POST /v1/subscriptions/credit-transfer/*` and `POST /v1/transfers/driver` (C047/C048)
/// proxy the caller's bearer to these routes rather than posting their own entries. Recorded in the C046
/// handoff.
/// </para>
/// </remarks>
public static class CreditTransferEndpoints
{
    public static IEndpointRouteBuilder MapCreditTransferEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var transfers = endpoints.MapGroup("/v1/wallet/credit-transfer")
            .WithTags("wallet")
            .RequireMageRideRole(MageRideRoles.Driver);

        transfers.MapPost("/initiate", InitiateAsync).WithName("initiateWalletCreditTransfer");
        transfers.MapPost("/request", RequestAsync).WithName("requestWalletCreditTransfer");
        transfers.MapGet("/pending", PendingAsync).WithName("listPendingWalletCreditTransfers");
        transfers.MapPost("/{transferId}/approve", ApproveAsync).WithName("approveWalletCreditTransfer");
        transfers.MapPost("/{transferId}/reject", RejectAsync).WithName("rejectWalletCreditTransfer");

        return endpoints;
    }

    /// <summary>US-9A.12 — a proactive send. Posts immediately.</summary>
    private static async Task<Created<TransferResponse>> InitiateAsync(
        InitiateTransferBody? body,
        HttpContext context,
        TransferService transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        var sender = context.User.RequireSubjectId();
        var recipient = RequestIds.Require(body?.RecipientDriverId, "recipientDriverId");

        if (body?.AmountMinor is not { } amountMinor)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor is required.");
        }

        var transfer = await transfers.SendDirectAsync(
            sender, recipient, amountMinor, cancellationToken);

        return TypedResults.Created(
            $"/v1/wallet/{sender}/transfers", Transfers.ToResponse(transfer, sender));
    }

    /// <summary>US-9.10 — ask a holder for credit. Nothing moves until they approve.</summary>
    private static async Task<Created<TransferResponse>> RequestAsync(
        RequestTransferBody? body,
        HttpContext context,
        TransferService transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        var requester = context.User.RequireSubjectId();
        var holder = RequestIds.Require(body?.HolderDriverId, "holderDriverId");

        if (body?.AmountMinor is not { } amountMinor)
        {
            throw new MageRideException(MageRideErrors.InvalidAmount, "amountMinor is required.");
        }

        var transfer = await transfers.RequestAsync(requester, holder, amountMinor, cancellationToken);

        return TypedResults.Created(
            $"/v1/wallet/{requester}/transfers", Transfers.ToResponse(transfer, requester));
    }

    /// <summary>US-9A.10 — the holder's approval inbox.</summary>
    private static async Task<Ok<CursorPage<TransferResponse>>> PendingAsync(
        HttpContext context,
        ITransferRepository transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        // The caller's own inbox, from the token: there is no {driverId} on this route precisely so that
        // one driver cannot read what another was asked.
        var holder = context.User.RequireSubjectId();
        var page = PageRequest.FromQuery(context.Request);
        var (before, beforeId) = TransferCursor.Decode(page.Cursor);

        var slab = await transfers.ReadPendingForHolderAsync(
            holder, before, beforeId, page.OverfetchLimit, cancellationToken);

        return TypedResults.Ok(
            CursorPage<TransferRow>
                .FromOverfetch(slab, page.Limit, TransferCursor.Encode)
                .Select(row => Transfers.ToResponse(row, holder)));
    }

    /// <summary>US-9.13 — debit the exact amount, credit the exact amount.</summary>
    private static async Task<Ok<TransferResponse>> ApproveAsync(
        string transferId,
        HttpContext context,
        TransferService transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        var holder = context.User.RequireSubjectId();

        var transfer = await transfers.ApproveAsync(
            holder, RequestIds.Require(transferId, "transferId"), cancellationToken);

        return TypedResults.Ok(Transfers.ToResponse(transfer, holder));
    }

    /// <summary>US-9.12 — decline. Nothing is posted.</summary>
    private static async Task<Ok<TransferResponse>> RejectAsync(
        string transferId,
        HttpContext context,
        TransferService transfers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(transfers);

        var holder = context.User.RequireSubjectId();

        var transfer = await transfers.RejectAsync(
            holder, RequestIds.Require(transferId, "transferId"), cancellationToken);

        return TypedResults.Ok(Transfers.ToResponse(transfer, holder));
    }
}
