using Dapper;
using MageRide.AdminBff.Auditing;
using MageRide.AdminBff.Upstream;
using MageRide.Shared.Errors;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;

namespace MageRide.AdminBff.Finance;

/// <summary>The charge a reversal is raised against (D-13, <c>billing.daily_fee_charges</c>).</summary>
public sealed record DailyFeeCharge(
    Guid DriverId,
    Guid VehicleId,
    DateOnly FeeDate,
    long AmountMinor,
    string Currency,
    string Status,
    string? RegNo,
    DateTimeOffset ChargedAt);

/// <summary>What the reversal posted (US-14.11).</summary>
public sealed record ReversalOutcome(
    Guid EntryId, long AmountMinor, string Currency, long BalanceAfterMinor, bool Replayed);

/// <summary>US-14.11's fee reversal — Finance and Super Admin only, audited, ledger-balanced.</summary>
public interface IWalletAdjustmentService
{
    Task<ReversalOutcome> ReverseFeeAsync(
        Guid driverId,
        Guid vehicleId,
        DateOnly feeDate,
        long? amountMinor,
        string reason,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IWalletAdjustmentService"/>
/// <remarks>
/// <para>
/// <b>The entry is posted by wallet-svc and not here, and that is the C065 fence.</b> wallet-svc is
/// the only writer of <c>billing.journal_postings</c> on this platform and its own file lists
/// admin-bff by name as the caller entitled to post <c>kind='adjustment'</c>. What a reversal needs
/// to be correct — the balanced pair, the Σ = 0 check before <c>trg_balanced</c> turns a slip into a
/// 500, the <c>billing.wallets</c> mirror, the driver's history line, the D-08 Redis write-through
/// and the outbox row — all happen inside one <c>LedgerService.PostAsync</c>. A second
/// implementation of that here would be a second implementation of the platform's money.
/// </para>
/// <para>
/// <b>What is decided here is what a reversal <em>is</em>:</b> which charge it compensates, that the
/// charge exists, that the amount does not exceed it, and that a human said why. The charge is read
/// rather than trusted because <c>billing.daily_fee_charges</c> is subscription-svc's row and the
/// operator is typing a date — a reversal for a day nobody was charged is a credit with no
/// justification, which is exactly the mistake an audited back-office is there to prevent.
/// </para>
/// <para>
/// <b>The ledger key is the business fact, so a double click posts once.</b>
/// <c>adjustment:fee_reversal:{driverId}:{vehicleId}:{feeDate}</c> — the same shape 1101's header
/// fixes for the daily fee itself, and the reason this route needs no command log: a retry collides
/// on <c>billing.journal_entries.idempotency_key</c> and wallet-svc answers <c>replayed: true</c>
/// with the original entry. That does mean <b>one reversal per charge, ever</b>, which is the right
/// bound: a fee charged once is reversed once, and a second correction on the same day is an
/// adjustment somebody has to argue for rather than press twice.
/// </para>
/// <para>
/// <b>The audit row is written whether or not anything moved.</b> A replay changed nothing and the
/// honest record is exactly that — <c>replayed: true</c> in the <c>after</c> image — because the
/// fact D-35 records is that an operator performed the action, not that the ledger happened to be
/// in a state where it had an effect.
/// </para>
/// </remarks>
internal sealed class WalletAdjustmentService(
    INpgsqlConnectionFactory connections,
    IAdminUpstream upstream,
    IAdminAuditContext audit,
    ILogger<WalletAdjustmentService> logger) : IWalletAdjustmentService
{
    /// <summary><c>billing.journal_entries.kind</c> for a back-office correction (1101).</summary>
    private const string AdjustmentKind = "adjustment";

    /// <summary>D-13's waived first trip. It moved no money, so there is nothing to give back.</summary>
    private const string WaivedStatus = "WAIVED_FIRST_TRIP";

    private const string FindChargeSql =
        """
        SELECT c.driver_id     AS "DriverId",
               c.vehicle_id    AS "VehicleId",
               c.fee_date      AS "FeeDate",
               c.amount_minor::bigint AS "AmountMinor",
               c.currency      AS "Currency",
               c.status        AS "Status",
               v.registration_number AS "RegNo",
               c.charged_at    AS "ChargedAt"
          FROM billing.daily_fee_charges c
          LEFT JOIN registry.vehicles v ON v.id = c.vehicle_id
         WHERE c.driver_id = @DriverId AND c.vehicle_id = @VehicleId AND c.fee_date = @FeeDate;
        """;

    public async Task<ReversalOutcome> ReverseFeeAsync(
        Guid driverId,
        Guid vehicleId,
        DateOnly feeDate,
        long? amountMinor,
        string reason,
        Guid actorId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var connection = await connections.OpenAsync(cancellationToken);

        var charge = await connection.QuerySingleOrDefaultAsync<DailyFeeCharge>(new CommandDefinition(
            FindChargeSql,
            new { DriverId = driverId, VehicleId = vehicleId, FeeDate = feeDate },
            cancellationToken: cancellationToken))
            ?? throw new MageRideException(
                MageRideErrors.NotFound,
                $"No daily fee was charged to this driver for {feeDate:yyyy-MM-dd} on that vehicle. "
                + "A reversal compensates a charge that exists (D-13).");

        if (string.Equals(charge.Status, WaivedStatus, StringComparison.Ordinal) || charge.AmountMinor == 0)
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                "That day's fee was waived under D-13's first-trip rule and moved no money, so there is "
                + "nothing to reverse.");
        }

        // Defaulting to the full charge is what the contract says, and it is also the safe default:
        // an operator who omits the amount means "undo it", and inventing a partial figure for them
        // would be this service deciding how much of somebody's money to give back.
        var amount = amountMinor ?? charge.AmountMinor;

        if (amount <= 0)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount, "A reversal moves money, so the amount must be above zero.");
        }

        if (amount > charge.AmountMinor)
        {
            throw new MageRideException(
                MageRideErrors.InvalidAmount,
                $"The fee charged was {charge.AmountMinor} and a reversal cannot exceed it. "
                + "A larger credit is an adjustment, not a reversal of this charge.");
        }

        var key = $"{AdjustmentKind}:fee_reversal:{driverId:D}:{vehicleId:D}:{feeDate:yyyy-MM-dd}";

        using var request = upstream.Request(
            AdminUpstreams.Wallet, HttpMethod.Post, $"/v1/internal/wallet/{driverId:D}/credit");

        request.Content = System.Net.Http.Json.JsonContent.Create(
            new
            {
                amountMinor = amount,
                kind = AdjustmentKind,
                idempotencyKey = key,
                description = $"Daily fee reversal for {feeDate:yyyy-MM-dd}: {reason}",
                reference = $"daily_fee:{driverId:D}:{vehicleId:D}:{feeDate:yyyy-MM-dd}",
            },
            options: MageRideJson.Options);

        var posted = await upstream.SendAsync<LedgerPostingResult>(
            AdminUpstreams.Wallet, request, context, cancellationToken);

        audit.Record(
            driverId,
            before: new
            {
                feeDate = charge.FeeDate,
                vehicleId = charge.VehicleId,
                regNo = charge.RegNo,
                chargedMinor = charge.AmountMinor,
                currency = charge.Currency,
            },
            after: new
            {
                entryId = posted.EntryId,
                reversedMinor = amount,
                currency = charge.Currency,
                balanceAfterMinor = posted.BalanceAfterMinor,
                replayed = posted.Replayed,
                reason,
                idempotencyKey = key,
            });

        logger.LogInformation(
            "Daily fee of {Charged} for {FeeDate} on vehicle {VehicleId} reversed by {ActorId}: {Reversed} "
            + "credited to driver {DriverId} as entry {EntryId} (replayed: {Replayed}). Reason: {Reason}",
            charge.AmountMinor, charge.FeeDate, vehicleId, actorId, amount, driverId,
            posted.EntryId, posted.Replayed, reason);

        return new ReversalOutcome(
            posted.EntryId, amount, charge.Currency, posted.BalanceAfterMinor, posted.Replayed);
    }

    /// <summary>wallet.yaml's <c>LedgerPostingResult</c>.</summary>
    private sealed record LedgerPostingResult(
        Guid EntryId, Guid AccountId, long AmountMinor, long BalanceAfterMinor, bool Replayed);
}
