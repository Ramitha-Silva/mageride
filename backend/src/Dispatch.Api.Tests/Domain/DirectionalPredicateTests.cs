using MageRide.Dispatch.Domain;
using MageRide.Dispatch.Eligibility;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;

namespace MageRide.Dispatch.Tests.Domain;

/// <summary>
/// D5' §12.1's three clauses, one at a time (DT-02). No database, no clock, no driver — the
/// predicate is a pure function and this is what makes each clause's boundary assertable rather
/// than inferred from which driver happened to win a round.
/// </summary>
public sealed class DirectionalPredicateTests
{
    /// <summary>Colombo Fort — every case's pickup.</summary>
    private static readonly GeoPoint Pickup = new(6.9344, 79.8428);

    /// <summary>Dehiwala, ~9.5 km south-south-east of the pickup: the ride's vector.</summary>
    private static readonly GeoPoint Dropoff = new(6.8514, 79.8653);

    /// <summary>A driver standing 70 m from the pickup.</summary>
    private static readonly GeoPoint AtPickup = new(6.9350, 79.8430);

    /// <summary>Panadura, ~25 km further along the same coast road — the same way the ride goes.</summary>
    private static readonly GeoPoint SouthDestination = new(6.7132, 79.9026);

    /// <summary>Negombo, ~30 km north — the opposite way.</summary>
    private static readonly GeoPoint NorthDestination = new(7.2083, 79.8358);

    private static readonly DirectionalConfigRow Defaults = DirectionalConfigRow.Defaults;

    [Fact]
    public void A_ride_that_heads_the_drivers_way_passes_all_three_clauses()
    {
        var verdict = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, Dropoff, SouthDestination, Defaults);

        Assert.True(verdict.Matched);
        Assert.Null(verdict.FailedOn);

        // Each clause, at the value the row will carry: the two bearings agree, the pickup is a
        // stone's throw away, and the drop-off leaves the driver kilometres closer to home.
        Assert.InRange(verdict.BearingDiffDeg, 0d, Defaults.ThetaMaxDeg);
        Assert.InRange(verdict.DetourM, 0d, Defaults.DetourMaxM);
        Assert.True(verdict.ProgressM > Defaults.ProgressMinM);
    }

    [Fact]
    public void A_ride_heading_the_other_way_fails_on_the_bearing()
    {
        var verdict = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, Dropoff, NorthDestination, Defaults);

        Assert.False(verdict.Matched);
        Assert.Equal(DirectionalClauses.Bearing, verdict.FailedOn);
        Assert.True(verdict.BearingDiffDeg > Defaults.ThetaMaxDeg);

        // And it is a genuine reversal rather than a marginal miss — the ride runs almost exactly
        // opposite to the way the driver is going.
        Assert.InRange(verdict.BearingDiffDeg, 150d, 180d);
    }

    [Fact]
    public void A_pickup_beyond_the_detour_ceiling_fails_even_when_the_bearing_agrees()
    {
        // 3 km north of the pickup, still heading south afterwards: the ride points the right way
        // and getting to it is the problem.
        var farDriver = new GeoPoint(6.9614, 79.8428);

        var verdict = DirectionalPredicate.Evaluate(
            farDriver, detourM: 3_000, Pickup, Dropoff, SouthDestination, Defaults);

        Assert.False(verdict.Matched);
        Assert.Equal(DirectionalClauses.Detour, verdict.FailedOn);
        Assert.InRange(verdict.BearingDiffDeg, 0d, Defaults.ThetaMaxDeg);
    }

    [Fact]
    public void A_ride_that_makes_no_headway_fails_on_the_progress_clause()
    {
        // The destination is 200 m along the ride's own bearing: the driver is very nearly there
        // already, so a 9 km ride in that direction overshoots and leaves them further away. This
        // is the case the bearing clause alone cannot catch, which is why §12.1 has three.
        var almostThere = new GeoPoint(6.932665, 79.843268);

        var verdict = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, Dropoff, almostThere, Defaults);

        Assert.False(verdict.Matched);
        Assert.Equal(DirectionalClauses.Progress, verdict.FailedOn);

        // Self-checking: the other two clauses genuinely passed, so `progress` is the clause under
        // test and not the first one that happened to fail.
        Assert.InRange(verdict.BearingDiffDeg, 0d, Defaults.ThetaMaxDeg);
        Assert.InRange(verdict.DetourM, 0d, Defaults.DetourMaxM);
        Assert.True(verdict.ProgressM < 0);
    }

    [Fact]
    public void A_ride_with_no_dropoff_keeps_the_candidate_and_says_it_could_not_be_measured()
    {
        var verdict = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, dropoff: null, SouthDestination, Defaults);

        // DT-05 bounds this predicate to removing candidates. Removing one on a measurement that
        // was never taken would be a decision made on no evidence, so the candidate stays and the
        // audit row says exactly why nothing was decided.
        Assert.True(verdict.Matched);
        Assert.Equal(DirectionalClauses.Unevaluable, verdict.FailedOn);
    }

    [Fact]
    public void The_thresholds_that_were_live_travel_with_the_decision()
    {
        // Badulla way, ~84 km due east. The ride to Dehiwala runs 75° off that heading — too far
        // under the default θ — but it does still leave the driver ~2 km closer, so the bearing is
        // the only clause that decides and widening θ is the only thing that changes.
        var eastDestination = new GeoPoint(6.9344, 80.6);
        var widened = Defaults with { ThetaMaxDeg = 180 };

        var strict = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, Dropoff, eastDestination, Defaults);

        var loose = DirectionalPredicate.Evaluate(
            AtPickup, detourM: 70, Pickup, Dropoff, eastDestination, widened);

        // Same geometry, different admin configuration, opposite outcomes — and each row carries
        // the θ it was judged against, so neither becomes unreadable when the other is live (R-11).
        Assert.False(strict.Matched);
        Assert.Equal(DirectionalClauses.Bearing, strict.FailedOn);
        Assert.True(loose.Matched);
        Assert.Equal(45, strict.ThetaMaxDeg);
        Assert.Equal(180, loose.ThetaMaxDeg);
        Assert.Equal(strict.BearingDiffDeg, loose.BearingDiffDeg, 9);
    }

    /// <summary>
    /// Compass arithmetic, not subtraction. A ride due north and a driver heading 10° east of north
    /// are 20° apart; naive subtraction makes them 340° apart and rejects every ride that crosses
    /// due north — which in Colombo is every ride up the A3.
    /// </summary>
    [Theory]
    [InlineData(350d, 10d, 20d)]
    [InlineData(10d, 350d, 20d)]
    [InlineData(0d, 180d, 180d)]
    [InlineData(90d, 270d, 180d)]
    [InlineData(-10d, 10d, 20d)]
    [InlineData(370d, 10d, 0d)]
    public void Angular_difference_wraps_around_the_compass(double first, double second, double expected) =>
        Assert.Equal(expected, GeoMath.AngularDifferenceDeg(first, second), 9);

    [Fact]
    public void Bearings_are_degrees_clockwise_from_north()
    {
        var origin = new GeoPoint(6.9344, 79.8428);

        Assert.Equal(0d, GeoMath.InitialBearingDeg(origin, origin with { Latitude = 7.0344 }), 1);
        Assert.Equal(90d, GeoMath.InitialBearingDeg(origin, origin with { Longitude = 79.9428 }), 1);
        Assert.Equal(180d, GeoMath.InitialBearingDeg(origin, origin with { Latitude = 6.8344 }), 1);
        Assert.Equal(270d, GeoMath.InitialBearingDeg(origin, origin with { Longitude = 79.7428 }), 1);
    }

    /// <summary>
    /// The sphere this predicate measures on and the spheroid PostGIS ranks candidates on agree to
    /// well inside the tolerances DT-02 works at — which is what lets the detour clause take
    /// <c>ST_Distance</c> metres from the post-filter and the progress clause compute its own.
    /// </summary>
    [Fact]
    public void Distances_are_metres_and_agree_with_the_post_filters_scale()
    {
        // One degree of latitude is ~111.2 km anywhere on Earth.
        var oneDegreeNorth = GeoMath.DistanceM(new GeoPoint(6.9344, 79.8428), new GeoPoint(7.9344, 79.8428));

        Assert.InRange(oneDegreeNorth, 111_000d, 111_400d);
        Assert.Equal(0d, GeoMath.DistanceM(Pickup, Pickup), 6);
    }
}
