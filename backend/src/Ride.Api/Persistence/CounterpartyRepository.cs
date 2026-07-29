using Dapper;
using Npgsql;

namespace MageRide.Ride.Persistence;

/// <summary>
/// The one fact AL-48 puts on <c>RideDetail.counterpartyPhone</c>: a participant's real MSISDN.
/// </summary>
/// <remarks>
/// <para>
/// <b>AL-48 withdrew number masking.</b> D5' BR-28.3 as amended: "Normal call = <b>direct cellular
/// dial of the counterparty's real MSISDN</b>, which the API exposes <b>only after driver
/// acceptance</b>; withheld for rides cancelled before assignment." There is no masking bridge to
/// mint a number from, so the number has to come from the account.
/// </para>
/// <para>
/// This reads <c>iam.users</c>, which belongs to iam-svc — a <b>read and only ever a read</b>, on
/// the same footing and for the same reason as <see cref="DriverSummaryRepository"/>'s join into
/// <c>registry.vehicles</c>: the contract puts the field on a ride-svc response, another context
/// owns the fact, and query-svc (C048) — which will own this read model — does not exist. iam-svc
/// publishes no "number for this user id" route either; <c>GET /v1/users/lookup</c> answers the
/// opposite question (is this <em>number</em> registered, P-03). Raised in the C037 handoff.
/// </para>
/// </remarks>
public interface ICounterpartyRepository
{
    /// <summary>
    /// <paramref name="userId"/>'s number in E.164, or <see langword="null"/> when the account has
    /// been erased (E-06) — a deleted account must not resurrect a number through a ride.
    /// </summary>
    Task<string?> FindPhoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICounterpartyRepository"/>
public sealed class CounterpartyRepository : ICounterpartyRepository
{
    public Task<string?> FindPhoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT phone FROM iam.users WHERE id = @UserId;",
            new { UserId = userId },
            transaction,
            cancellationToken: cancellationToken));
    }
}
