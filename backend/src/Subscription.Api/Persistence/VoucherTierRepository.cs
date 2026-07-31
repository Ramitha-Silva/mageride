using Dapper;
using MageRide.Shared.Persistence;

namespace MageRide.Subscriptions.Persistence;

/// <summary>One rung of the bulk-voucher ladder (<c>billing.voucher_discount_tiers</c>).</summary>
/// <param name="DiscountBps">
/// Basis points off the price, per <em>voucher value</em>. 1000 = 10% — pay Rs 900, receive Rs 1,000
/// of credit. AL-01: this is the informal reseller's entire margin, and there is no per-transfer
/// commission anywhere on the platform.
/// </param>
public sealed record VoucherTier(
    long DenominationMinor, int DiscountBps, bool Active, DateTimeOffset UpdatedAt);

/// <summary>A rung as an admin submits it.</summary>
public sealed record VoucherTierInput(long DenominationMinor, int DiscountBps, bool Active);

/// <summary><c>billing.voucher_discount_tiers</c> — the Admin Portal config surface (US-9A.15).</summary>
internal interface IVoucherTierRepository
{
    Task<IReadOnlyList<VoucherTier>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Upserts the submitted rungs, recording the admin, and returns the whole ladder.</summary>
    Task<IReadOnlyList<VoucherTier>> UpsertAsync(
        IReadOnlyList<VoucherTierInput> tiers, Guid updatedBy, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IVoucherTierRepository"/>
/// <remarks>
/// <b>The same table wallet-svc's <c>PUT /v1/wallet/admin/voucher-discount-tiers</c> writes.</b> D3'
/// Part 2 prints both spellings and C007 landed both; this one is D3' subscription-svc's. They are one
/// row set with one meaning, so a write through either is visible to both — which is the only property
/// that matters until one of them is retired (raised again in the C047 handoff).
/// </remarks>
internal sealed class VoucherTierRepository(INpgsqlConnectionFactory connections) : IVoucherTierRepository
{
    private const string SelectColumns = "denomination_minor, discount_bps, active, updated_at";

    public async Task<IReadOnlyList<VoucherTier>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var rows = await connection.QueryAsync<VoucherTier>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM billing.voucher_discount_tiers ORDER BY denomination_minor;",
                cancellationToken: cancellationToken));

        return [.. rows];
    }

    /// <remarks>
    /// An upsert, not a replace, for the reason <see cref="PlanRepository.UpsertAsync"/> gives: a screen
    /// that renders four of the five denominations must not be able to un-configure the fifth. A rung is
    /// withdrawn by sending it with <c>active: false</c>, which is a decision an admin makes rather than
    /// a consequence of what a form posted.
    /// </remarks>
    public async Task<IReadOnlyList<VoucherTier>> UpsertAsync(
        IReadOnlyList<VoucherTierInput> tiers, Guid updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO billing.voucher_discount_tiers
                  (denomination_minor, discount_bps, active, updated_by)
                VALUES (@DenominationMinor, @DiscountBps, @Active, @UpdatedBy)
                ON CONFLICT (denomination_minor) DO UPDATE
                   SET discount_bps = EXCLUDED.discount_bps,
                       active       = EXCLUDED.active,
                       updated_by   = EXCLUDED.updated_by;
                """,
                tiers.Select(tier => new
                {
                    tier.DenominationMinor,
                    tier.DiscountBps,
                    tier.Active,
                    UpdatedBy = updatedBy,
                }).ToArray(),
                transaction,
                cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<VoucherTier>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM billing.voucher_discount_tiers ORDER BY denomination_minor;",
                transaction: transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return [.. rows];
    }
}
