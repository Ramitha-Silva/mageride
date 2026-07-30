using Dapper;
using MageRide.Shared.Persistence;
using MageRide.Wallet.Domain;
using Npgsql;

namespace MageRide.Wallet.Persistence;

/// <summary>A tier plus what drivers have bought at it (US-9A.15's admin view).</summary>
internal sealed record VoucherTierUsage(
    long DenominationMinor,
    int DiscountBps,
    bool Active,
    DateTimeOffset UpdatedAt,
    int PurchaseCount,
    long PurchasedValueMinor);

/// <summary>A completed purchase.</summary>
internal sealed record VoucherPurchaseRow(
    Guid Id,
    Guid BuyerId,
    long DenominationMinor,
    int DiscountBpsApplied,
    long PaidMinor,
    long CreditedMinor,
    string Currency,
    string? GatewayRef,
    Guid? JournalEntryId,
    DateTimeOffset CreatedAt);

/// <summary><c>billing.voucher_discount_tiers</c> and <c>billing.voucher_purchases</c>.</summary>
internal interface IVoucherRepository
{
    /// <summary>Every tier, active or not — the admin view needs both.</summary>
    Task<IReadOnlyList<VoucherTier>> ReadTiersAsync(CancellationToken cancellationToken);

    /// <summary>Tiers with per-tier purchase counts and value (US-9A.15).</summary>
    Task<IReadOnlyList<VoucherTierUsage>> ReadTiersWithUsageAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the tier set an admin submitted; returns what the table holds afterwards.</summary>
    Task<IReadOnlyList<VoucherTier>> UpsertTiersAsync(
        IReadOnlyList<VoucherTier> tiers, Guid updatedBy, CancellationToken cancellationToken);

    /// <summary>Writes the purchase row inside the ledger's transaction.</summary>
    Task<Guid> InsertPurchaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid purchaseId,
        Guid buyerId,
        VoucherPrice price,
        string? gatewayRef,
        Guid journalEntryId,
        CancellationToken cancellationToken);

    /// <summary>An earlier purchase against the same gateway reference, if one exists.</summary>
    Task<VoucherPurchaseRow?> ReadByGatewayRefAsync(
        string gatewayRef, CancellationToken cancellationToken);

    Task<VoucherPurchaseRow?> ReadAsync(Guid purchaseId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVoucherRepository"/>
internal sealed class VoucherRepository(INpgsqlConnectionFactory connections) : IVoucherRepository
{
    public async Task<IReadOnlyList<VoucherTier>> ReadTiersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VoucherTier>(
            new CommandDefinition(
                """
                SELECT denomination_minor, discount_bps, active
                  FROM billing.voucher_discount_tiers
                 ORDER BY denomination_minor;
                """,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    public async Task<IReadOnlyList<VoucherTierUsage>> ReadTiersWithUsageAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        // The usage is what makes the reseller margin visible to Finance (US-9A.15), so it counts
        // purchases at the denomination rather than at the tier's current rate — a tier whose rate was
        // changed still owns the purchases made under the old one.
        var rows = await connection.QueryAsync<VoucherTierUsage>(
            new CommandDefinition(
                """
                SELECT t.denomination_minor,
                       t.discount_bps,
                       t.active,
                       t.updated_at,
                       coalesce(p.purchase_count, 0)::int AS purchase_count,
                       coalesce(p.purchased_value_minor, 0) AS purchased_value_minor
                  FROM billing.voucher_discount_tiers t
                  LEFT JOIN (
                    SELECT denomination_minor,
                           count(*) AS purchase_count,
                           -- ::bigint because sum(bigint) is `numeric` in Postgres, and a record whose
                           -- property is a long then matches no constructor Dapper can find.
                           sum(credited_minor)::bigint AS purchased_value_minor
                      FROM billing.voucher_purchases
                     GROUP BY denomination_minor) p
                    ON p.denomination_minor = t.denomination_minor
                 ORDER BY t.denomination_minor;
                """,
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// An upsert per submitted tier, not a delete-and-replace: a denomination the admin did not send is
    /// left alone rather than silently retired, because the payload is a *form*, and a tier that
    /// disappeared because it was off-screen would change what every driver pays. Deactivating is
    /// `active: false`, which is a value the admin has to choose.
    /// </remarks>
    public async Task<IReadOnlyList<VoucherTier>> UpsertTiersAsync(
        IReadOnlyList<VoucherTier> tiers, Guid updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var tier in tiers)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO billing.voucher_discount_tiers
                          (denomination_minor, discount_bps, active, updated_by)
                    VALUES (@DenominationMinor, @DiscountBps, @Active, @UpdatedBy)
                    ON CONFLICT (denomination_minor) DO UPDATE
                       SET discount_bps = EXCLUDED.discount_bps,
                           active = EXCLUDED.active,
                           updated_by = EXCLUDED.updated_by;
                    """,
                    new
                    {
                        tier.DenominationMinor,
                        tier.DiscountBps,
                        tier.Active,
                        UpdatedBy = updatedBy,
                    },
                    transaction,
                    cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);

        return await ReadTiersAsync(cancellationToken);
    }

    public async Task<Guid> InsertPurchaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid purchaseId,
        Guid buyerId,
        VoucherPrice price,
        string? gatewayRef,
        Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(price);

        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(
                """
                INSERT INTO billing.voucher_purchases
                      (id, buyer_id, denomination_minor, discount_bps_applied, paid_minor,
                       credited_minor, gateway_ref, journal_entry_id)
                VALUES (@PurchaseId, @BuyerId, @DenominationMinor, @DiscountBps, @PaidMinor,
                        @CreditedMinor, @GatewayRef, @JournalEntryId)
                RETURNING id;
                """,
                new
                {
                    PurchaseId = purchaseId,
                    BuyerId = buyerId,
                    price.DenominationMinor,
                    price.DiscountBps,
                    price.PaidMinor,
                    price.CreditedMinor,
                    GatewayRef = gatewayRef,
                    JournalEntryId = journalEntryId,
                },
                transaction,
                cancellationToken: cancellationToken));
    }

    public async Task<VoucherPurchaseRow?> ReadByGatewayRefAsync(
        string gatewayRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayRef);

        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VoucherPurchaseRow?>(
            new CommandDefinition(
                """
                SELECT id, buyer_id, denomination_minor, discount_bps_applied, paid_minor,
                       credited_minor, currency, gateway_ref, journal_entry_id, created_at
                  FROM billing.voucher_purchases
                 WHERE gateway_ref = @GatewayRef;
                """,
                new { GatewayRef = gatewayRef },
                cancellationToken: cancellationToken));
    }

    public async Task<VoucherPurchaseRow?> ReadAsync(Guid purchaseId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<VoucherPurchaseRow?>(
            new CommandDefinition(
                """
                SELECT id, buyer_id, denomination_minor, discount_bps_applied, paid_minor,
                       credited_minor, currency, gateway_ref, journal_entry_id, created_at
                  FROM billing.voucher_purchases
                 WHERE id = @PurchaseId;
                """,
                new { PurchaseId = purchaseId },
                cancellationToken: cancellationToken));
    }
}
