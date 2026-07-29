using MageRide.Dispatch.Domain;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Eligibility;

/// <summary>
/// D5' §12.1's three-clause Directional Travel predicate (DT-02), as a pure function.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// angularDiff(bearing(driver→destination), bearing(pickup→dropoff)) ≤ θ_max     # default 45°
/// AND  dist(pickup, driver)       ≤ detour_max                                  # default 2 km
/// AND  dist(dropoff, destination) &lt; dist(pickup, destination) − progress_min  # default 250 m
/// </code>
/// All three, or the candidate is dropped from this round. The three thresholds come from
/// <c>dispatch.directional_config</c> at the instant the round ran and are written into the audit
/// beside the measurements.
/// </para>
/// <para>
/// <b>The three clauses answer three different questions</b>, which is why none of them subsumes
/// another. The bearing says the ride points the driver's way; the detour says getting to the
/// pickup does not undo that; and the progress test says the ride actually delivers them closer to
/// where they are going, which a ride that runs parallel to their route — same bearing, no
/// headway — would not.
/// </para>
/// <para>
/// <b>Pure and synchronous</b>, like <see cref="Dispatching.CandidateScorer"/>: everything it needs
/// is already in hand, so the whole of DT-02 is testable without a database, a clock or a driver.
/// </para>
/// </remarks>
public static class DirectionalPredicate
{
    /// <summary>Runs the predicate for one candidate against one ride.</summary>
    /// <param name="driver">Where the exact post-filter found the driver.</param>
    /// <param name="detourM">
    /// <c>dist(pickup, driver)</c> — passed in rather than recomputed, because
    /// <c>ST_Distance</c> on the spheroid is what the post-filter already measured and ranked by,
    /// and a second, slightly different number for the same distance is how an audit row stops
    /// reproducing the decision beside it.
    /// </param>
    /// <param name="dropoff">
    /// <see langword="null"/> when the <c>ride.requested</c> envelope carried none. The candidate is
    /// then <b>kept</b> — see <see cref="DirectionalClauses.Unevaluable"/>.
    /// </param>
    public static DirectionalBreakdown Evaluate(
        GeoPoint driver,
        double detourM,
        GeoPoint pickup,
        GeoPoint? dropoff,
        GeoPoint destination,
        DirectionalConfigRow config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var driverBearing = GeoMath.InitialBearingDeg(driver, destination);

        if (dropoff is not { } destinationOfRide)
        {
            // Nothing to measure the ride's direction against. DT-05 bounds this predicate to
            // *removing* candidates, and a removal justified by a measurement that was never taken
            // is not a narrower candidate set — it is a guess. The row says which it was.
            return new DirectionalBreakdown(
                Matched: true,
                BearingDiffDeg: 0,
                DetourM: detourM,
                ProgressM: 0,
                DriverBearingDeg: driverBearing,
                RideBearingDeg: 0,
                ThetaMaxDeg: config.ThetaMaxDeg,
                DetourMaxM: config.DetourMaxM,
                ProgressMinM: config.ProgressMinM,
                FailedOn: DirectionalClauses.Unevaluable);
        }

        var rideBearing = GeoMath.InitialBearingDeg(pickup, destinationOfRide);
        var bearingDiff = GeoMath.AngularDifferenceDeg(driverBearing, rideBearing);

        // `dist(pickup, destination) − dist(dropoff, destination)`: how much of the driver's own
        // journey the ride covers for them. Both measured the same way, so the sphere-versus-
        // spheroid difference cancels out of the subtraction almost exactly.
        var progressM = GeoMath.DistanceM(pickup, destination) - GeoMath.DistanceM(destinationOfRide, destination);

        // Clause order is D5' §12.1's own. It decides nothing but which clause the audit blames when
        // a ride fails more than one, and "it was going the wrong way" is the answer a driver
        // asking why they got no hires is actually looking for.
        var failedOn =
            bearingDiff > config.ThetaMaxDeg ? DirectionalClauses.Bearing
            : detourM > config.DetourMaxM ? DirectionalClauses.Detour
            : progressM <= config.ProgressMinM ? DirectionalClauses.Progress
            : null;

        return new DirectionalBreakdown(
            Matched: failedOn is null,
            BearingDiffDeg: bearingDiff,
            DetourM: detourM,
            ProgressM: progressM,
            DriverBearingDeg: driverBearing,
            RideBearingDeg: rideBearing,
            ThetaMaxDeg: config.ThetaMaxDeg,
            DetourMaxM: config.DetourMaxM,
            ProgressMinM: config.ProgressMinM,
            FailedOn: failedOn);
    }
}
