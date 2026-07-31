using MageRide.Fare.Configuration;
using MageRide.Fare.Distance;
using MageRide.Fare.Domain;
using MageRide.Fare.Persistence;
using MageRide.Fare.Pricing;
using MageRide.Shared.Errors;
using MageRide.Shared.Persistence;
using Microsoft.Extensions.Options;

namespace MageRide.Fare.Settlement;

/// <summary>What <c>POST /v1/fare/calculate</c> produced.</summary>
/// <param name="PenaltyMinor">
/// The D-05 cancellation debt collected on this trip, already inside
/// <see cref="FareBreakdown.TotalMinor"/>'s payable total. Separate so a receipt can say why the
/// fare is Rs 50 more than the quote.
/// </param>
public sealed record FinalFare(
    RidePayment Payment, FareBreakdown Breakdown, long PenaltyMinor, FilteredTrack Track, bool UsedEstimate)
{
    /// <summary>What the passenger owes: the metered fare plus any debt settled on it.</summary>
    public long AmountMinor => Payment.AmountMinor;
}

/// <summary>
/// The final fare on ride completion: measure, price, collect the old debt, record what is owed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotent on the ride, not on a header.</b> ride-svc's <c>complete</c> is at-least-once and
/// the contract puts an <c>Idempotency-Key</c> on this route, but a header dedupes identical
/// <em>requests</em> — two different keys for one ride would still produce two fares. The guard is
/// a <c>FOR UPDATE</c> read of the ride's payment inside the writing transaction: the first caller
/// creates the row and every later one is handed it back.
/// </para>
/// <para>
/// <b>The distance falls back rather than fails</b> (D5' §1.2: <c>distance_calculation_failed</c>
/// → fall back to the estimate). A ride whose tracker was offline still has to produce a fare a
/// driver can be paid, and the number the passenger was shown is the honest one to charge when the
/// platform cannot measure a better one.
/// </para>
/// <para>
/// <b>The tariff is resolved at the ride's request instant</b>, not at completion. A rate published
/// while somebody is in the car must not change what they are charged, which is the whole reason
/// migration 1001 versions the table by <c>effective_from</c>.
/// </para>
/// </remarks>
internal sealed class FareSettlementService(
    IUnitOfWorkFactory unitOfWorkFactory,
    IRideRepository rides,
    ITrackRepository tracks,
    IRidePaymentRepository payments,
    FarePricingService pricing,
    IPenaltyClient penalties,
    IWalletLedgerClient wallet,
    IOptions<FareOptions> options,
    TimeProvider clock,
    ILogger<FareSettlementService> logger)
{
    private readonly FareOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

    public async Task<FinalFare> CalculateAsync(
        Guid rideId, double? distanceKmOverride, CancellationToken cancellationToken)
    {
        var ride = await rides.ReadAsync(rideId, cancellationToken)
                   ?? throw new MageRideException(MageRideErrors.NotFound, $"No ride {rideId}.");

        if (!RideStates.Priceable.Contains(ride.State))
        {
            throw new MageRideException(
                MageRideErrors.Conflict,
                $"Ride {rideId} is {ride.State}. A fare is computed when the ride completes — "
                + $"{string.Join(" or ", RideStates.Priceable)} — and a cancelled or in-flight ride has none.");
        }

        // Returned as-is when a fare already exists: the ride has been priced, and re-pricing it
        // would let a rate change or a re-measured track move a number the passenger has been shown.
        if (await payments.FindForRideAsync(rideId, cancellationToken) is { } settled)
        {
            if (RidePaymentStates.Terminal.Contains(settled.State))
            {
                throw new MageRideException(
                    MageRideErrors.PaymentAlreadySettled,
                    $"Ride {rideId} was already paid ({settled.State}).");
            }

            logger.LogInformation(
                "Ride {RideId} already has payment {PaymentId} in {State}; returning it unchanged.",
                rideId,
                settled.Id,
                settled.State);

            return await RebuildAsync(ride, settled, cancellationToken);
        }

        var (track, distanceKm, usedEstimate) =
            await MeasureAsync(ride, distanceKmOverride, cancellationToken);

        // D5' §1.2's fallback is the *estimate*, not a re-price at zero distance: a ride whose
        // tracker was offline is charged the number the passenger was actually shown. Pricing a
        // measured 0 km would hand them the first-km charge for a journey across the city.
        var breakdown = usedEstimate
            ? await QuotedFareAsync(ride, cancellationToken)
            : await pricing.PriceAsync(ride.VehicleType, distanceKm, ride.RequestedAt, cancellationToken);

        // D-05, and in this order deliberately: settle first, add what came back. The route is
        // idempotent on (penalty_id, applied_ride_id), so a retry collects nothing and cannot charge
        // the same Rs 50 twice — whereas reading the debt, pricing it and settling afterwards would
        // re-charge it on every retry that failed in between (C035 decision 9).
        var collected = await penalties.SettleAsync(ride.PassengerId, rideId, cancellationToken);
        var penaltyMinor = collected.SettledMinor;

        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(cancellationToken: cancellationToken);

        var payment = await payments.CreateInitiatedAsync(
            unitOfWork,
            rideId,
            MethodFor(ride),
            breakdown.TotalMinor + penaltyMinor,
            breakdown.Currency,
            PayerRoleFor(ride),
            PayerFor(ride),
            cancellationToken);

        await unitOfWork.CommitAsync(cancellationToken);

        // After the commit, because a ledger posting is another service's transaction and cannot be
        // rolled back with ours. Each entry is keyed by the business fact (D5' §7.1's spelling,
        // verbatim), so a retry replays rather than double-posts.
        await ForwardPenaltiesAsync(ride, collected, cancellationToken);

        logger.LogInformation(
            "Ride {RideId} priced at {AmountMinor} {Currency} over {DistanceKm:F2} km "
            + "(estimate fallback: {UsedEstimate}, D-05 collected: {PenaltyMinor})",
            rideId,
            payment.AmountMinor,
            breakdown.Currency,
            distanceKm,
            usedEstimate,
            penaltyMinor);

        return new FinalFare(payment, breakdown, penaltyMinor, track, usedEstimate);
    }

    /// <summary>
    /// The distance the fare is charged on: the caller's, the filtered track's, or the estimate's.
    /// </summary>
    /// <remarks>
    /// <b>A caller-supplied distance is trusted.</b> The contract lets ride-svc send one and says it
    /// is the Kalman-filtered figure; the route is internal and mTLS-only, so the only callers able
    /// to send it are ones already trusted to say a ride completed at all.
    /// </remarks>
    private async Task<(FilteredTrack Track, double DistanceKm, bool UsedEstimate)> MeasureAsync(
        RideFacts ride, double? distanceKmOverride, CancellationToken cancellationToken)
    {
        if (distanceKmOverride is { } supplied && supplied > 0)
        {
            return (FilteredTrack.Empty, supplied, false);
        }

        var track = await FilterTrackAsync(ride, cancellationToken);

        if (track.DistanceKm > 0)
        {
            return (track, track.DistanceKm, false);
        }

        // D5' §1.2's `distance_calculation_failed`. The estimate is in minor units, so the distance
        // is not recoverable from it — the fare is taken from the quote directly by pricing the
        // ride at a distance that reproduces it, which is what EstimateDistanceKm does.
        logger.LogWarning(
            "Ride {RideId} produced no measurable track ({SampleCount} samples, {RejectedCount} rejected); "
            + "falling back to the quoted estimate (D5 §1.2).",
            ride.RideId,
            track.SampleCount,
            track.RejectedCount);

        return (track, 0, true);
    }

    private async Task<FilteredTrack> FilterTrackAsync(RideFacts ride, CancellationToken cancellationToken)
    {
        if (ride.AcceptedVehicleId is not { } vehicleId)
        {
            return FilteredTrack.Empty;
        }

        var window = await rides.ReadTravelWindowAsync(ride.RideId, cancellationToken);

        if (window.StartedAt is not { } startedAt)
        {
            // The ride never reached InProgress, so nothing between the endpoints was travelled by
            // the passenger. Positions before the start are the driver approaching the pickup.
            return FilteredTrack.Empty;
        }

        var endedAt = window.EndedAt ?? ride.TerminalAt ?? clock.GetUtcNow();

        var samples = await tracks.ReadAsync(
            vehicleId, startedAt, endedAt, _options.MaxTrackSamples, cancellationToken);

        return KalmanTrack.Filter(samples, _options.Kalman);
    }

    /// <summary>
    /// The two ledger legs D5' §7.1 spells out, per penalty.
    /// </summary>
    /// <remarks>
    /// <b>The next trip's driver is a pass-through, not the beneficiary</b> (AL-16, and
    /// <c>dispatch.cancellation_penalties.affected_driver_id</c>'s own column comment). The passenger
    /// pays the Rs 50 to whoever drove them this time — it is inside the fare — and the platform
    /// moves it from that driver's wallet to the driver who was stood up. Net zero for the
    /// pass-through, which is why it is safe to add to a fare they collect in cash.
    /// </remarks>
    private async Task ForwardPenaltiesAsync(
        RideFacts ride, PenaltySettlement collected, CancellationToken cancellationToken)
    {
        if (collected.Items.Count == 0)
        {
            return;
        }

        if (ride.AcceptedDriverId is not { } passThroughDriverId || !wallet.IsConfigured)
        {
            logger.LogError(
                "Ride {RideId} collected {SettledMinor} of D-05 penalty into its fare but could not forward it: "
                + "{Reason}. The affected driver has not been paid and this needs reconciliation.",
                ride.RideId,
                collected.SettledMinor,
                ride.AcceptedDriverId is null ? "the ride names no accepted driver" : "wallet-svc is not configured");

            return;
        }

        foreach (var penalty in collected.Items)
        {
            // D5' §7.1, verbatim: key = concat(penalty_id, ':', tripId). 1101's column comment pins
            // the same spelling. It is a cross-service contract — a reformat here silently starts
            // paying the penalty twice.
            var key = $"{penalty.PenaltyId}:{ride.RideId}";

            await wallet.DebitAsync(
                passThroughDriverId,
                penalty.AmountMinor,
                "penalty_settle",
                key,
                $"Cross-trip cancellation settlement collected on ride {ride.RideId} (D-05).",
                ride.RideId.ToString(),
                cancellationToken);

            await wallet.CreditAsync(
                penalty.AffectedDriverId,
                penalty.AmountMinor,
                "penalty_settle",
                key,
                $"Compensation for cancelled ride {penalty.OriginalRideId} (D-05).",
                penalty.OriginalRideId.ToString(),
                cancellationToken);
        }
    }

    /// <summary>
    /// The fare a ride was quoted, as a breakdown — D5' §1.2's fallback when the track cannot be
    /// measured.
    /// </summary>
    /// <remarks>
    /// A ride with neither a track nor a quote is priced at zero rather than refused: the fare is
    /// the passenger's side of the ledger, and a completion that cannot finish because nobody can
    /// say what it cost strands the driver as well. It is logged as the anomaly it is.
    /// </remarks>
    private Task<FareBreakdown> QuotedFareAsync(RideFacts ride, CancellationToken cancellationToken)
    {
        if (ride.FareEstimateMinor is not { } quoted)
        {
            logger.LogError(
                "Ride {RideId} has neither a measurable track nor a stored fare estimate; it is being "
                + "settled at zero. A ride booked through POST /v1/rides/request always carries a quote, so "
                + "this row was created another way.",
                ride.RideId);

            quoted = 0;
        }

        // Zeros for the parts, deliberately: the quote's own breakdown was not stored (rides.rides
        // keeps the amount and the surcharge, not the rate that produced them), and reporting a rate
        // this fare was not computed from would put a wrong explanation on a right number.
        return Task.FromResult(new FareBreakdown(
            FirstKmMinor: 0,
            PerKmMinor: 0,
            DistanceKm: 0,
            PeakSurchargePct: 0,
            NightSurchargePct: 0,
            BaseMinor: quoted,
            SurchargeMinor: 0,
            TotalMinor: quoted,
            Currency: ride.Currency));
    }

    /// <summary>Presents a fare that was already priced, without re-pricing it.</summary>
    /// <remarks>
    /// The stored amount is authoritative. The parts are <em>not</em> reconstructed: the penalty
    /// that may be inside the total is dispatch-svc's record rather than ours, and a breakdown that
    /// guessed at the split would put two different explanations of one number on two receipts.
    /// </remarks>
    private static Task<FinalFare> RebuildAsync(
        RideFacts ride, RidePayment payment, CancellationToken cancellationToken) =>
        Task.FromResult(new FinalFare(
            payment,
            new FareBreakdown(0, 0, 0, 0, 0, payment.AmountMinor, 0, payment.AmountMinor, payment.Currency),
            PenaltyMinor: 0,
            Track: FilteredTrack.Empty,
            UsedEstimate: false));

    /// <summary>
    /// The settlement method the payment row opens with, from the booking-time choice.
    /// </summary>
    /// <remarks>
    /// <c>rides.rides.payment_method</c> is the passenger's booking-time choice and
    /// <c>fares.ride_payments.method</c> is wider (AL-22): <c>scan_driver_qr</c> is chosen at
    /// settlement and never at booking, so it can only appear on a later attempt — C050's.
    /// </remarks>
    private static string MethodFor(RideFacts ride) =>
        RidePaymentMethods.All.Contains(ride.PaymentMethod) ? ride.PaymentMethod : RidePaymentMethods.Cash;

    /// <summary>
    /// P-04: cash is always paid by the rider, LankaQR and OnePay are always charged to the booker.
    /// </summary>
    private static string PayerRoleFor(RideFacts ride) =>
        ride.PaymentMethod is RidePaymentMethods.LankaQr or RidePaymentMethods.Onepay
            ? PayerRoles.Booker
            : PayerRoles.Rider;

    private static Guid PayerFor(RideFacts ride) =>
        PayerRoleFor(ride) == PayerRoles.Booker ? ride.BookerId : ride.PassengerId;
}
