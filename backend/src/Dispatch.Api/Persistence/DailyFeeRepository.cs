using Dapper;
using Npgsql;

namespace MageRide.Dispatch.Persistence;

/// <summary>
/// Everything the D-08 pre-dispatch wallet gate needs about one candidate, in one row.
/// </summary>
/// <param name="TripsToday">
/// D5' §2.2's <c>tripsToday</c> — how many trips this driver has already accepted on the current
/// Asia/Colombo day. 0 means the next one is the free first trip.
/// </param>
/// <param name="ChargedToday">
/// A <c>PAID</c> <c>billing.daily_fee_charges</c> row already exists for
/// <c>(driver, vehicle, feeDate)</c>. The fee is a single flat charge per day (US-9.4), so this
/// alone allows the trip.
/// </param>
/// <param name="DailyFeeMinor">
/// The tier's rate from <c>billing.plans</c>, or <see langword="null"/> when Finance has not set
/// one — which §20 makes deliberate for <c>truck</c> / <c>mini_truck</c>.
/// </param>
/// <param name="BalanceMinor">
/// The <c>billing.wallets</c> mirror. Zero when the driver has no ledger account at all, which is
/// not an unknown balance but a definite absence of one — D-08's "until balance confirmable" is
/// about a read that <em>failed</em>, and a returned row is a read that succeeded. The degraded case
/// is the absence of the whole row, not of this member.
/// </param>
public sealed record DailyFeeFacts(
    Guid DriverId, int TripsToday, bool ChargedToday, int? DailyFeeMinor, long BalanceMinor);

/// <summary>
/// The billing reads behind D5' §2's daily platform fee, as the dispatch gate needs them (D-08).
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and it stays that way.</b> Charging the fee is subscription-svc's (C047) — D5'
/// §2.2 posts a balanced ledger entry and upserts <c>billing.daily_fee_charges</c> at the moment
/// the driver *accepts*, which is after dispatch has done its work. This gate only asks whether
/// that charge would succeed, so that a driver who cannot pay is not offered the ride in the first
/// place (US-9.1's "request missed: insufficient balance").
/// </para>
/// <para>
/// <b><c>tripsToday</c> is counted from <c>dispatch.offers</c>, not from <c>rides.rides</c>.</b>
/// D5' §2.2 writes it as "count(completed+accepted today for driver)", and an ACCEPTED offer is
/// dispatch's own record of exactly that fact — one row per trip the driver took, written by this
/// service. Reading ride-svc's aggregate for a count would cross the R-01 fence for a number this
/// bounded context already holds.
/// </para>
/// </remarks>
public interface IDailyFeeRepository
{
    /// <summary>
    /// One round trip for a whole round's candidates. The tuple is per (driver, vehicle) because
    /// the charge's idempotency key and the plan rate are both per vehicle (D-13).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DailyFeeFacts>> ReadAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDailyFeeRepository"/>
public sealed class DailyFeeRepository : IDailyFeeRepository
{
    public async Task<IReadOnlyDictionary<Guid, DailyFeeFacts>> ReadAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid DriverId, Guid VehicleId, string VehicleType)> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return new Dictionary<Guid, DailyFeeFacts>();
        }

        // The Colombo business date is computed once, in Postgres, and reused by both correlated
        // subqueries — D-38: a UTC-clocked service that derived it itself would straddle the day
        // boundary for five and a half hours every night, which is exactly when a driver's "first
        // trip of the day" is decided.
        //
        // status = 'PAID' rather than "a row exists": a WAIVED_FIRST_TRIP row moved no money
        // (migration 1103's ck_daily_fee_charges_waiver), so treating it as charged would give
        // every driver a free second trip as well as a free first one.
        var rows = await connection.QueryAsync<DailyFeeFacts>(new CommandDefinition(
            """
            WITH today AS (SELECT (now() AT TIME ZONE 'Asia/Colombo')::date AS fee_date),
                 candidate AS (
                   SELECT * FROM unnest(@DriverIds::uuid[], @VehicleIds::uuid[], @VehicleTypes::text[])
                            AS c(driver_id, vehicle_id, vehicle_type))
            SELECT c.driver_id AS DriverId,
                   (SELECT count(*)::int
                      FROM dispatch.offers o
                     WHERE o.driver_id = c.driver_id
                       AND o.status = 'ACCEPTED'
                       AND (o.responded_at AT TIME ZONE 'Asia/Colombo')::date = today.fee_date) AS TripsToday,
                   EXISTS (SELECT 1
                             FROM billing.daily_fee_charges f
                            WHERE f.driver_id = c.driver_id
                              AND f.vehicle_id = c.vehicle_id
                              AND f.fee_date = today.fee_date
                              AND f.status = 'PAID')                                            AS ChargedToday,
                   p.daily_fee_minor                                                            AS DailyFeeMinor,
                   -- billing.wallets first (§10 makes it the read model "the dispatch balance check"
                   -- exists for), then billing.accounts, which carries the same number denormalised
                   -- from the postings and is what survives a wallets row that was never projected.
                   -- 0 for a driver with no ledger account: they have never had a wallet, which for
                   -- this gate is a balance and not an unknown.
                   COALESCE(w.balance_minor, a.balance_minor, 0)::bigint                        AS BalanceMinor
              FROM candidate c
             CROSS JOIN today
              LEFT JOIN billing.plans p ON p.vehicle_type = c.vehicle_type
              LEFT JOIN billing.accounts a
                     ON a.owner_type = 'driver' AND a.owner_id = c.driver_id AND a.currency = 'LKR'
              LEFT JOIN billing.wallets w ON w.account_id = a.id;
            """,
            new
            {
                DriverIds = candidates.Select(static c => c.DriverId).ToArray(),
                VehicleIds = candidates.Select(static c => c.VehicleId).ToArray(),
                VehicleTypes = candidates.Select(static c => c.VehicleType).ToArray(),
            },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(static row => row.DriverId);
    }
}
