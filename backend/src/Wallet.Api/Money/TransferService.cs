using MageRide.Shared.Errors;
using MageRide.Wallet.Configuration;
using MageRide.Wallet.Domain;
using MageRide.Wallet.Ledger;
using MageRide.Wallet.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Wallet.Money;

/// <summary>
/// Driver-to-driver credit transfer (US-9.10–9.13, US-9A.12, AL-01).
/// </summary>
/// <remarks>
/// <para>
/// <b>The exact value moves and there is no fee leg.</b> Every posting here is a two-leg entry of equal
/// and opposite amounts; the platform account is not a party to it at all. This is not a policy in code
/// that could be edited — <c>billing.journal_entries.kind</c> has no value that could carry a
/// commission (AL-01 removed <c>reseller_commission</c>), so a fee row cannot be written even by
/// mistake.
/// </para>
/// <para>
/// <b>Driver ID only.</b> AL-34 removed SCR-DA/DI-023's QR-scan path; nothing here accepts a scanned
/// payload, and the recipient is looked up by the id the requester typed.
/// </para>
/// <para>
/// <b>Two directions, one table.</b> A proactive send (<c>DIRECT</c>) is approved the moment it is
/// created — the sender is the one acting — and a request (<c>REQUESTED</c>) waits on the holder. The
/// balance is checked at the moment money moves, never at request time: what the holder can afford when
/// they answer is the only figure that matters.
/// </para>
/// </remarks>
internal sealed class TransferService(
    ITransferRepository transfers,
    IAccountRepository accounts,
    ILedgerService ledger,
    IOptions<WalletOptions> options,
    ILogger<TransferService> logger)
{
    private readonly WalletOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>US-9A.12 — the sender moves their own credit to another driver, now.</summary>
    public async Task<TransferRow> SendDirectAsync(
        Guid senderDriverId, Guid recipientDriverId, long amountMinor, CancellationToken cancellationToken)
    {
        await RequireTransferableAsync(senderDriverId, recipientDriverId, amountMinor, cancellationToken);

        var sender = await accounts.EnsureDriverAccountAsync(senderDriverId, cancellationToken);
        var recipient = await accounts.EnsureDriverAccountAsync(recipientDriverId, cancellationToken);

        var transferId = Guid.NewGuid();

        await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.DriverTransfer,
                LedgerKeys.DriverTransfer(transferId),
                "Driver-to-driver credit transfer",
                [
                    new LedgerLeg(sender.Id, -amountMinor, transferId.ToString()),
                    new LedgerLeg(recipient.Id, amountMinor, transferId.ToString()),
                ]),
            (connection, transaction, entryId, token) =>
                transfers.InsertAsync(
                    connection,
                    transaction,
                    transferId,
                    senderDriverId,
                    recipientDriverId,
                    amountMinor,
                    TransferDirections.Direct,
                    TransferStatuses.Approved,
                    entryId,
                    token),
            cancellationToken);

        logger.LogInformation(
            "Driver {SenderId} sent {AmountMinor} to driver {RecipientId} (transfer {TransferId}, exact value).",
            senderDriverId,
            amountMinor,
            recipientDriverId,
            transferId);

        return await transfers.ReadAsync(transferId, cancellationToken)
               ?? throw new InvalidOperationException($"Transfer {transferId} was posted but cannot be read back.");
    }

    /// <summary>US-9.10 — the requester asks a holder for credit. Nothing moves.</summary>
    public async Task<TransferRow> RequestAsync(
        Guid requesterDriverId, Guid holderDriverId, long amountMinor, CancellationToken cancellationToken)
    {
        // Sender = the holder who will be debited, recipient = the requester. The table is named from
        // the money's point of view rather than the request's, which is what makes one row serve both
        // directions (1105).
        await RequireTransferableAsync(holderDriverId, requesterDriverId, amountMinor, cancellationToken);

        var transfer = await transfers.CreateRequestAsync(
            holderDriverId, requesterDriverId, amountMinor, cancellationToken);

        logger.LogInformation(
            "Driver {RequesterId} requested {AmountMinor} from driver {HolderId} (transfer {TransferId}).",
            requesterDriverId,
            amountMinor,
            holderDriverId,
            transfer.Id);

        return transfer;
    }

    /// <summary>US-9.13 — the holder approves: debit exactly, credit exactly.</summary>
    public async Task<TransferRow> ApproveAsync(
        Guid holderDriverId, Guid transferId, CancellationToken cancellationToken)
    {
        var transfer = await RequireOwnPendingAsync(holderDriverId, transferId, cancellationToken);

        var sender = await accounts.EnsureDriverAccountAsync(transfer.SenderDriverId, cancellationToken);
        var recipient = await accounts.EnsureDriverAccountAsync(transfer.RecipientDriverId, cancellationToken);

        var approved = false;

        await ledger.PostAsync(
            new LedgerEntry(
                JournalKinds.DriverTransfer,
                LedgerKeys.DriverTransfer(transfer.Id),
                "Driver-to-driver credit transfer (approved request)",
                [
                    new LedgerLeg(sender.Id, -transfer.AmountMinor, transfer.Id.ToString()),
                    new LedgerLeg(recipient.Id, transfer.AmountMinor, transfer.Id.ToString()),
                ]),
            async (connection, transaction, entryId, token) =>
            {
                approved = await transfers.TryApproveAsync(
                    connection, transaction, transfer.Id, entryId, token);

                if (!approved)
                {
                    // Two taps on Approve, or an approval racing a rejection. Throwing rolls the
                    // postings back with the claim, so the money follows the row's status.
                    throw new MageRideException(
                        MageRideErrors.Conflict,
                        $"Transfer {transfer.Id} is no longer pending.");
                }
            },
            cancellationToken);

        logger.LogInformation(
            "Driver {HolderId} approved transfer {TransferId}: {AmountMinor} to driver {RecipientId}, exact value.",
            holderDriverId,
            transfer.Id,
            transfer.AmountMinor,
            transfer.RecipientDriverId);

        return await transfers.ReadAsync(transfer.Id, cancellationToken) ?? transfer;
    }

    /// <summary>US-9.12 — the holder declines. Nothing is posted.</summary>
    public async Task<TransferRow> RejectAsync(
        Guid holderDriverId, Guid transferId, CancellationToken cancellationToken)
    {
        var transfer = await RequireOwnPendingAsync(holderDriverId, transferId, cancellationToken);

        if (!await transfers.TryRejectAsync(transfer.Id, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.Conflict, $"Transfer {transfer.Id} is no longer pending.");
        }

        return await transfers.ReadAsync(transfer.Id, cancellationToken) ?? transfer;
    }

    /// <summary>
    /// The request must exist, belong to this holder, and still be pending.
    /// </summary>
    /// <remarks>
    /// <b>A transfer that is not the caller's is a 404, not a 403.</b> The house rule everywhere on this
    /// platform: telling the two apart would make the endpoint an oracle over other drivers' credit
    /// requests. The <c>409</c> below is different — by then the caller has proved the row is theirs.
    /// </remarks>
    private async Task<TransferRow> RequireOwnPendingAsync(
        Guid holderDriverId, Guid transferId, CancellationToken cancellationToken)
    {
        var transfer = await transfers.ReadAsync(transferId, cancellationToken);

        if (transfer is null || transfer.SenderDriverId != holderDriverId)
        {
            throw new MageRideException(MageRideErrors.NotFound, "No such credit request.");
        }

        if (transfer.Status != TransferStatuses.Pending)
        {
            throw new MageRideException(
                MageRideErrors.Conflict, $"That request is already {transfer.Status.ToLowerInvariant()}.");
        }

        return transfer;
    }

    private async Task RequireTransferableAsync(
        Guid senderDriverId, Guid recipientDriverId, long amountMinor, CancellationToken cancellationToken)
    {
        if (amountMinor <= 0 || amountMinor > _options.MaxTransferMinor)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                $"A transfer is between 1 and {_options.MaxTransferMinor} minor units.");
        }

        if (senderDriverId == recipientDriverId)
        {
            // `ck_credit_transfers_not_self` (1105) also refuses it; the message here is what a driver
            // who typed their own id sees.
            throw new MageRideValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["recipientDriverId"] = ["A driver cannot transfer credit to themselves."],
            });
        }

        // Both sides must be drivers. AL-01's whole point is that this is a driver-to-driver facility
        // between ordinary driver accounts; a passenger has no wallet to receive it and a fleet's wallet
        // is not reachable by Driver ID.
        if (!await accounts.IsDriverAsync(recipientDriverId, cancellationToken))
        {
            throw new MageRideException(
                MageRideErrors.NotFound, "No driver holds that Driver ID.");
        }
    }
}
