using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Persistence;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace MageRide.Dispatch.Penalties;

/// <summary>One accrual, as <c>cancellation.penalty.accrued</c> states it (§11.12, D5' §7.1).</summary>
public sealed record PenaltyAccrual(
    Guid PassengerId, Guid OriginalRideId, Guid AffectedDriverId, long AmountMinor, string Basis);

/// <summary>What one settlement collected.</summary>
public sealed record PenaltySettlement(IReadOnlyList<PenaltyRow> Settled, long TotalMinor);

/// <summary>
/// <c>dispatch.cancellation_penalties</c> — the passenger's accrued, uncollected debt (D-05,
/// AL-16, D5' §7.1).
/// </summary>
public interface IPenaltyService
{
    /// <summary>
    /// Records a debt stated by ride-svc. Returns <see langword="null"/> on a redelivery, which is
    /// the normal shape of at-least-once and not an error.
    /// </summary>
    Task<PenaltyRow?> AccrueAsync(PenaltyAccrual accrual, CancellationToken cancellationToken);

    Task<PenaltySettlement> OutstandingAsync(Guid passengerId, CancellationToken cancellationToken);

    /// <summary>Settles everything outstanding against one completed ride, for fare-svc.</summary>
    Task<PenaltySettlement> SettleAsync(Guid passengerId, Guid appliedRideId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPenaltyService"/>
public sealed class PenaltyService(
    INpgsqlConnectionFactory connectionFactory,
    IUnitOfWorkFactory unitOfWorkFactory,
    IPenaltyRepository penalties,
    ILogger<PenaltyService> logger) : IPenaltyService
{
    public async Task<PenaltyRow?> AccrueAsync(PenaltyAccrual accrual, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accrual);

        if (!PenaltyBases.IsKnown(accrual.Basis))
        {
            // A basis this schema does not model. Dropped rather than stored under a guess: the
            // basis is what tells fare-svc whether the amount beside it is the amount, and a wrong
            // one would settle the wrong number.
            logger.LogWarning(
                "cancellation.penalty.accrued for ride {RideId} carries basis '{Basis}', which " +
                "ck_cancellation_penalties_basis does not admit; not recording it",
                accrual.OriginalRideId, accrual.Basis);

            return null;
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var row = await penalties.TryAccrueAsync(
            unitOfWork.Connection,
            unitOfWork.Transaction,
            accrual.PassengerId,
            accrual.OriginalRideId,
            accrual.AffectedDriverId,
            accrual.AmountMinor,
            accrual.Basis,
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        if (row is null)
        {
            logger.LogDebug(
                "Penalty for ride {RideId} ({Basis}) was already accrued; the redelivery changed nothing",
                accrual.OriginalRideId, accrual.Basis);

            return null;
        }

        logger.LogInformation(
            "Passenger {PassengerId} owes {AmountMinor} minor ({Basis}) to driver {DriverId} for ride {RideId}; " +
            "it is collected on their next completed trip (D5' §7.1)",
            row.PassengerId, row.AmountMinor, row.Basis, row.AffectedDriverId, row.OriginalRideId);

        return row;
    }

    public async Task<PenaltySettlement> OutstandingAsync(Guid passengerId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var rows = await penalties.OutstandingAsync(connection, passengerId, cancellationToken);

        return new PenaltySettlement(rows, rows.Sum(static row => row.AmountMinor));
    }

    public async Task<PenaltySettlement> SettleAsync(
        Guid passengerId, Guid appliedRideId, CancellationToken cancellationToken)
    {
        if (appliedRideId == Guid.Empty)
        {
            throw new MageRideValidationException(new Dictionary<string, string[]>
            {
                ["rideId"] = ["rideId is required and must be a ULID or a UUID."],
            });
        }

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var settled = await penalties.SettleAsync(
            unitOfWork.Connection, unitOfWork.Transaction, passengerId, appliedRideId, cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        var total = settled.Sum(static row => row.AmountMinor);

        // An empty result is the answer to a replay and to "there was nothing owed", and the two
        // are deliberately the same answer: fare-svc adds what this returns to the fare, so a retry
        // that reported the debt a second time would charge it twice.
        logger.LogInformation(
            "Settled {Count} penalty row(s) totalling {TotalMinor} minor for passenger {PassengerId} on ride {RideId}",
            settled.Count, total, passengerId, appliedRideId);

        return new PenaltySettlement(settled, total);
    }
}
