using MageRide.Shared.Errors;
using MageRide.Shared.Time;
using MageRide.Subscriptions.Configuration;
using MageRide.Subscriptions.Domain;
using MageRide.Subscriptions.Persistence;
using MageRide.Subscriptions.Wallet;
using Microsoft.Extensions.Options;

namespace MageRide.Subscriptions.Fees;

/// <summary>Today's fee position for one driver (D3' <c>GET /v1/fees/{driverId}/today</c>).</summary>
public sealed record TodayFeeStatus(
    Guid DriverId,
    Guid VehicleId,
    string VehicleType,
    long DailyRateMinor,
    string Status,
    long DeductedMinor,
    int TripsToday,
    bool FirstTripFree,
    DateOnly FeeDate,
    DateTimeOffset FeeDateTzAt,
    string Currency);

/// <summary>
/// The D-13 daily platform fee: one flat charge per (driver, vehicle, Asia/Colombo day), first trip
/// free, no per-trip fee and no commission.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two guards make the charge single-shot, and they guard different things.</b>
/// <c>billing.daily_fee_charges</c>'s composite primary key stops this service writing a second
/// <em>row</em>; wallet-svc's UNIQUE on <c>billing.journal_entries.idempotency_key</c> stops the
/// <em>money</em> moving twice. The second one is the load-bearing half, because the two systems are
/// not in one transaction — and it is why the key is spelled from the business fact
/// (<see cref="DailyFeeRule.LedgerKey"/>) rather than generated.
/// </para>
/// <para>
/// <b>Debit first, record second.</b> The order is the whole of the crash-safety argument. Debit-then-record
/// leaves, at worst, money taken and no row — and the retry re-sends the same ledger key, gets
/// <c>replayed: true</c> for the same entry, and writes the row it owed. Record-then-debit leaves, at
/// worst, a driver marked <c>PAID</c> who paid nothing, which no retry ever repairs because the row now
/// says the day is settled. One of those orders is recoverable and the other silently loses revenue.
/// </para>
/// <para>
/// <b>Mode A is zeroed here as well as in the rate table.</b> The fence is "bus and train pay nothing,
/// ever", and a rate table is a thing an admin edits at 2 a.m. <c>PUT /v1/admin/fees/rates</c> refuses
/// a non-zero Mode A rate, and this path ignores one if it somehow exists — two independent places, so
/// no single mistake can charge a bus.
/// </para>
/// </remarks>
internal sealed class DailyFeeService(
    IPlanRepository plans,
    IDailyFeeRepository charges,
    IVehicleRepository vehicles,
    IWalletLedgerClient wallet,
    IOptions<SubscriptionOptions> options,
    TimeProvider clock,
    ILogger<DailyFeeService> logger)
{
    /// <summary>The journal kind wallet-svc admits for this money (§10's CHECK, D-09).</summary>
    private const string DailyFeeKind = "daily_fee";

    private readonly SubscriptionOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// D5' §2.2's charge logic, run before a driver's trip is allowed.
    /// </summary>
    /// <param name="rideId">
    /// The ride being accepted, excluded from the trip count. D3' has ride-svc call this <em>after</em>
    /// the conditional accept has landed, so without it the driver's first trip of the day arrives as
    /// <c>tripsToday = 1</c> and is charged — the one thing the fence forbids.
    /// </param>
    public async Task<DailyFeeCharge> ChargeBeforeTripAsync(
        Guid driverId, Guid vehicleId, Guid? rideId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.ReadAsync(vehicleId, cancellationToken)
                      ?? throw new MageRideException(
                          MageRideErrors.VehicleNotFound, $"Vehicle {vehicleId} does not exist.");

        var rate = await RateForAsync(vehicle, cancellationToken);
        var (feeDate, tzAt) = BusinessCalendar.Stamp(clock.GetUtcNow());

        var existing = await charges.ReadAsync(driverId, vehicleId, feeDate, cancellationToken);
        var tripsToday = await charges.CountTripsAsync(driverId, feeDate, rideId, cancellationToken);

        var outcome = DailyFeeRule.Decide(
            tripsToday,
            alreadyPaid: existing?.Status == FeeStatuses.Paid,
            rate,
            _options.FreeTripsPerDay);

        // The already-charged branch returns the row untouched: `existing` is non-null whenever the
        // rule reached it, and re-upserting would move `charged_at` on a day whose money is settled.
        if (outcome == FeeOutcome.AlreadyCharged)
        {
            return existing!;
        }

        var amountMinor = 0L;

        if (outcome == FeeOutcome.Chargeable)
        {
            var posting = await wallet.DebitAsync(
                driverId,
                rate,
                DailyFeeKind,
                DailyFeeRule.LedgerKey(driverId, vehicleId, feeDate),
                $"Daily platform fee {feeDate:yyyy-MM-dd} ({vehicle.VehicleType})",
                reference: vehicleId.ToString(),
                cancellationToken);

            amountMinor = rate;

            logger.LogInformation(
                "Daily fee {AmountMinor} charged to driver {DriverId} for vehicle {VehicleId} on {FeeDate} "
                + "(entry {EntryId}, replayed {Replayed}, trips today {TripsToday}).",
                rate,
                driverId,
                vehicleId,
                feeDate,
                posting.EntryId,
                posting.Replayed,
                tripsToday);
        }

        return await charges.UpsertAsync(
            new DailyFeeCharge(
                driverId,
                vehicleId,
                feeDate,
                tzAt,
                amountMinor,
                Currencies.Lkr,
                tripsToday,
                // A waived day is the only status that is not PAID. `NothingOwed` records PAID with a
                // zero amount rather than a second waiver status — 1103's CHECK admits exactly two
                // values, and "waived first trip" would be a false reason for a Mode A vehicle whose
                // twentieth trip is equally free.
                outcome == FeeOutcome.WaivedFirstTrip ? FeeStatuses.WaivedFirstTrip : FeeStatuses.Paid,
                clock.GetUtcNow()),
            cancellationToken);
    }

    /// <summary>US-9.1/9.7's dashboard tile: what today costs and whether it has been paid.</summary>
    /// <remarks>
    /// The vehicle is the one the driver selected to go live on (US-9.6). A driver who has selected
    /// none but was charged today is answered from that charge row instead — going offline does not
    /// make the day's fee unreportable — and only a driver who has neither gets a <c>404</c>.
    /// </remarks>
    public async Task<TodayFeeStatus> TodayAsync(Guid driverId, CancellationToken cancellationToken)
    {
        var (feeDate, tzAt) = BusinessCalendar.Stamp(clock.GetUtcNow());

        var vehicle = await vehicles.ActiveVehicleAsync(driverId, cancellationToken);
        var charge = vehicle is null
            ? await charges.ReadForDayAsync(driverId, feeDate, cancellationToken)
            : await charges.ReadAsync(driverId, vehicle.VehicleId, feeDate, cancellationToken);

        vehicle ??= charge is null
            ? null
            : await vehicles.ReadAsync(charge.VehicleId, cancellationToken);

        if (vehicle is null)
        {
            throw new MageRideException(
                MageRideErrors.VehicleNotFound,
                "No vehicle is selected and none was charged today, so there is no daily rate to report. "
                + "Select a vehicle in vehicle management first (US-9.6).");
        }

        var rate = await RateForAsync(vehicle, cancellationToken);
        var tripsToday = await charges.CountTripsAsync(driverId, feeDate, excludingRideId: null, cancellationToken);
        var paid = charge?.Status == FeeStatuses.Paid;

        return new TodayFeeStatus(
            driverId,
            vehicle.VehicleId,
            vehicle.VehicleType,
            rate,
            paid ? FeeStatuses.Paid : FeeStatuses.Unpaid,
            paid ? charge!.AmountMinor : 0,
            tripsToday,
            // The platform's rule, not a statement about this driver's morning: D3's own example pairs
            // `firstTripFree: true` with `tripsToday: 3`, and the app draws it as the "1st trip free"
            // badge on the dashboard. It is false only if a deployment sets FreeTripsPerDay to zero,
            // which would break the fence and is announced at start-up.
            _options.FreeTripsPerDay > 0,
            feeDate,
            tzAt,
            charge?.Currency ?? Currencies.Lkr);
    }

    /// <summary>
    /// The vehicle type's rate, with the Mode A fence applied.
    /// </summary>
    /// <exception cref="MageRideException">
    /// <c>not-found</c> when Finance has configured no rate for the type. §20 seeds none for
    /// <c>truck</c> and <c>mini_truck</c> deliberately, so a delivery vehicle cannot go online until
    /// someone decides what it costs — refusing is the point, and inventing a rate would bill a driver
    /// a number nobody chose.
    /// </exception>
    private async Task<long> RateForAsync(VehicleFacts vehicle, CancellationToken cancellationToken)
    {
        if (vehicle.Mode == OperatingModes.PublicTransport)
        {
            return 0;
        }

        var plan = await plans.ForVehicleTypeAsync(vehicle.VehicleType, cancellationToken)
                   ?? throw new MageRideException(
                       MageRideErrors.NotFound,
                       $"No daily-fee rate is configured for vehicle type '{vehicle.VehicleType}'. "
                       + "Finance sets it in Admin Portal Config (US-14.4) before such a vehicle can go online.");

        return plan.DailyFeeMinor;
    }
}

/// <summary><c>registry.vehicles.mode</c> / <c>billing.plans.mode</c> (AL-09).</summary>
public static class OperatingModes
{
    /// <summary>Buses and trains. Free to track and free to operate — no platform fee, ever.</summary>
    public const string PublicTransport = "A";

    /// <summary>Private transport: school buses, staff transport, book hires. Monthly platform charge.</summary>
    public const string PrivateTransport = "B";

    /// <summary>Standby on-demand. The daily platform fee, from the driver's own wallet.</summary>
    public const string StandbyOnDemand = "C";

    public static bool IsKnown(string? mode) =>
        mode is PublicTransport or PrivateTransport or StandbyOnDemand;
}

/// <summary>The one currency this platform bills in (CLAUDE.md: money as minor units, LKR).</summary>
public static class Currencies
{
    public const string Lkr = "LKR";
}
