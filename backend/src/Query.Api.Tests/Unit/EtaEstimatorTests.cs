using MageRide.Query.Configuration;
using MageRide.Query.Live;
using MageRide.Shared.Geo;
using MageRide.Shared.Primitives;
using Microsoft.Extensions.Options;

namespace MageRide.Query.Tests.Unit;

/// <summary>
/// US-7.11's arrival estimate — a straight line with a detour factor, because ADD §7.6 puts routing in
/// Phase 3 and there is no road network to measure against yet.
/// </summary>
/// <remarks>
/// What these pin down is the part that would otherwise be silently wrong: which speed is used, and when
/// no number is defensible. The magnitude of the estimate is a guess by construction and is a setting for
/// that reason; the <em>rules</em> around it are not.
/// </remarks>
public sealed class EtaEstimatorTests
{
    private static readonly GeoPoint Fort = new(6.9344, 79.8428);

    [Fact]
    public void A_moving_vehicles_own_speed_is_used()
    {
        var estimator = Estimator();
        var target = Along(Fort, metres: 1_000);

        // 10 m/s over 1 300 m of road (1 000 m straight line × 1.3) is 130 s.
        var eta = estimator.Estimate(Vehicle(speedMps: 10), target);

        Assert.NotNull(eta);
        Assert.InRange(eta.Value, 125, 135);
    }

    /// <summary>
    /// A vehicle stopped at a light reports ~0 m/s. Dividing by that gives hours; below the floor the
    /// per-type average takes over.
    /// </summary>
    [Fact]
    public void A_stationary_vehicle_falls_back_to_the_per_type_average()
    {
        var estimator = Estimator();
        var target = Along(Fort, metres: 1_000);

        var stopped = estimator.Estimate(Vehicle(speedMps: 0.2, type: "bus"), target);

        // 20 km/h = 5.56 m/s over 1 300 m ≈ 234 s. What matters is that it is a plausible number and not
        // an hour and a half.
        Assert.NotNull(stopped);
        Assert.InRange(stopped.Value, 200, 270);
    }

    [Fact]
    public void An_unknown_vehicle_type_uses_the_configured_default()
    {
        var estimator = Estimator();

        var eta = estimator.Estimate(Vehicle(speedMps: null, type: "hovercraft"), Along(Fort, 1_000));

        Assert.NotNull(eta);
    }

    /// <summary>
    /// "Arriving in 94 minutes" for a bus at the edge of a 20 km search is arithmetically fine and not a
    /// thing to plan around. An absent field is the truer statement.
    /// </summary>
    [Fact]
    public void An_estimate_beyond_the_cap_is_omitted_rather_than_reported()
    {
        var estimator = Estimator(configure: options => options.MaxEta = TimeSpan.FromMinutes(10));

        Assert.Null(estimator.Estimate(Vehicle(speedMps: 8), Along(Fort, metres: 20_000)));
    }

    [Fact]
    public void A_vehicle_already_at_the_target_arrives_now()
    {
        var estimator = Estimator();

        Assert.Equal(0, estimator.Estimate(Vehicle(speedMps: 8), Fort));
    }

    [Fact]
    public void Disabling_the_estimator_removes_the_field_entirely()
    {
        var estimator = Estimator(configure: options => options.EtaEnabled = false);

        Assert.Null(estimator.Estimate(Vehicle(speedMps: 8), Along(Fort, 1_000)));
    }

    /// <summary>The detour factor is the whole difference between a straight line and a road.</summary>
    [Fact]
    public void The_detour_factor_lengthens_the_estimate()
    {
        var target = Along(Fort, metres: 2_000);

        var straight = Estimator(configure: options => options.EtaDetourFactor = 1.0)
            .Estimate(Vehicle(speedMps: 10), target);

        var withDetour = Estimator(configure: options => options.EtaDetourFactor = 1.5)
            .Estimate(Vehicle(speedMps: 10), target);

        Assert.NotNull(straight);
        Assert.NotNull(withDetour);
        Assert.Equal(straight.Value * 1.5, withDetour.Value, 1.0);
    }

    private static EtaEstimator Estimator(Action<QueryOptions>? configure = null)
    {
        var options = new QueryOptions();
        configure?.Invoke(options);

        return new EtaEstimator(Options.Create(options));
    }

    private static LiveVehicle Vehicle(double? speedMps, string type = "three_wheeler") =>
        new(Guid.NewGuid(), Fort, 90, speedMps, type, "C", DateTimeOffset.UtcNow);

    /// <summary>A point <paramref name="metres"/> due north of <paramref name="from"/>.</summary>
    private static GeoPoint Along(GeoPoint from, double metres)
    {
        var target = new GeoPoint(from.Latitude + (metres / 111_320d), from.Longitude);

        // The helper's own arithmetic is checked once, so a wrong constant here cannot masquerade as an
        // estimator bug in every test above.
        Assert.Equal(metres, GeoMath.DistanceM(from, target), metres * 0.01);

        return target;
    }
}
