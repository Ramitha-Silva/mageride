using System.Globalization;

namespace MageRide.Subscriptions.Domain;

/// <summary><c>billing.daily_fee_charges.status</c> (migration 1103's CHECK).</summary>
public static class FeeStatuses
{
    /// <summary>The fee for this Colombo day has been collected. Zero when the rate is zero.</summary>
    public const string Paid = "PAID";

    /// <summary>
    /// The D-13 free first trip, recorded explicitly so "no row" keeps meaning "not charged yet
    /// today" rather than "charged nothing".
    /// </summary>
    public const string WaivedFirstTrip = "WAIVED_FIRST_TRIP";

    /// <summary>What <c>GET /v1/fees/{driverId}/today</c> reports (D3' subscription-svc).</summary>
    public const string Unpaid = "UNPAID";
}

/// <summary>What the charge path decided to do, before anything was written.</summary>
public enum FeeOutcome
{
    /// <summary>The first trip of this Colombo day. Free — no wallet check at all (US-9.1).</summary>
    WaivedFirstTrip,

    /// <summary>A <c>PAID</c> row already exists for this (driver, vehicle, Colombo day).</summary>
    AlreadyCharged,

    /// <summary>The vehicle's rate is zero, so nothing is owed and no entry is written (Mode A).</summary>
    NothingOwed,

    /// <summary>The fee falls due now and must be debited before the trip is allowed.</summary>
    Chargeable,
}

/// <summary>
/// D5' §2.2's charge logic, as a decision this service can state without touching anything.
/// </summary>
/// <remarks>
/// <para>
/// Pure and separate from <see cref="Fees.DailyFeeService"/> on purpose: the D-13 rule is four
/// branches and three of them mean "write nothing and allow the trip". Keeping the branch here means a
/// unit test can walk all four in microseconds, and the service is left holding only the ordering
/// question — debit first, record second — which is the part that needs a database and a wallet.
/// </para>
/// <para>
/// <b><c>alreadyPaid</c> is <c>status = 'PAID'</c>, never "a row exists".</b> A
/// <c>WAIVED_FIRST_TRIP</c> row means the driver has had their free trip on this vehicle today and
/// still owes the day's fee — the row is upgraded in place on the second trip, which is exactly what
/// D5' §2.2's <c>already_charged</c> guard distinguishes. Reading it as "a row exists" would make
/// every driver's whole day free.
/// </para>
/// </remarks>
public static class DailyFeeRule
{
    /// <summary>
    /// The ledger idempotency key, spelled exactly as C005 decision 4 and 1107's header fix it:
    /// <c>daily_fee:{driverId}:{vehicleId}:{feeDate}</c>.
    /// </summary>
    /// <remarks>
    /// <b>This spelling is load-bearing and belongs to wallet-svc's UNIQUE index.</b> It is the second
    /// of the two guards that make the charge single-shot: <c>billing.daily_fee_charges</c>'s composite
    /// primary key stops this service writing two rows, and this key stops the *money* moving twice even
    /// if two replicas decide to charge at the same instant. Composed from the business fact and never
    /// from a random value — a random key would make every retry a second debit.
    /// </remarks>
    public static string LedgerKey(Guid driverId, Guid vehicleId, DateOnly feeDate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"daily_fee:{driverId}:{vehicleId}:{feeDate:yyyy-MM-dd}");

    /// <summary>
    /// Decides what the driver's next trip costs.
    /// </summary>
    /// <param name="tripsToday">
    /// Completed-or-accepted Mode C trips already taken this Colombo day, counted across every vehicle
    /// the driver used. Per <em>driver</em>, which is how D5' §2.2 counts them: the waiver is one free
    /// trip per person per day, not one per vehicle, so switching vehicles cannot buy a second one.
    /// </param>
    /// <param name="alreadyPaid">A <c>PAID</c> row exists for this (driver, vehicle, Colombo day).</param>
    /// <param name="dailyFeeMinor">The vehicle type's rate from <c>billing.plans</c>. Zero for Mode A.</param>
    /// <param name="freeTripsPerDay">
    /// <see cref="Configuration.SubscriptionOptions.FreeTripsPerDay"/>; 1 by US-9.1.
    /// </param>
    public static FeeOutcome Decide(int tripsToday, bool alreadyPaid, long dailyFeeMinor, int freeTripsPerDay)
    {
        // Ordered so the free trip is answered before anything else is consulted — US-9.1 is explicit
        // that the first trip is free "no wallet check", and a balance lookup here would be a
        // dependency the rule does not have.
        if (tripsToday < freeTripsPerDay)
        {
            return FeeOutcome.WaivedFirstTrip;
        }

        if (alreadyPaid)
        {
            return FeeOutcome.AlreadyCharged;
        }

        // A zero rate is Mode A (AL-09) or a type Finance has deliberately zeroed. Not a charge of
        // nothing: no wallet call, no journal entry, no idempotency key burned.
        return dailyFeeMinor <= 0 ? FeeOutcome.NothingOwed : FeeOutcome.Chargeable;
    }
}
