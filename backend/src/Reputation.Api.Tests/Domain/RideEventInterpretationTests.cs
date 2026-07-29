using MageRide.Reputation.Detection;
using MageRide.Reputation.Domain;
using MageRide.Reputation.Messaging;

namespace MageRide.Reputation.Tests.Domain;

/// <summary>
/// The <c>ride.events</c> → fact mapping, and the detection-window key.
/// </summary>
/// <remarks>
/// Pure. The mapping is where D5' §7.2's "pre-acceptance cancels never count" actually lives, so it
/// is asserted here rather than only through a database round trip.
/// </remarks>
public sealed class RideEventInterpretationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>D5' §7.2: a completed ride resets both sides' runs, so it produces both facts.</summary>
    [Fact]
    public void A_completed_ride_produces_a_fact_for_each_side()
    {
        var passenger = Guid.NewGuid();
        var driver = Guid.NewGuid();

        var facts = RideEventHandler.Interpret(
            Envelope(RideEventTypes.Completed, passengerId: passenger, driverId: driver));

        Assert.Equal(2, facts.Count);
        Assert.All(facts, fact => Assert.Equal(IntakeKinds.Completion, fact.Kind));
        Assert.Contains(facts, fact => fact.SubjectId == passenger && fact.SubjectRole == SubjectRoles.Passenger);
        Assert.Contains(facts, fact => fact.SubjectId == driver && fact.SubjectRole == SubjectRoles.Driver);

        // The two facts come from one envelope and must not collide on one dedupe key, or the
        // second would look like a redelivery and the driver would never be counted.
        Assert.Equal(2, facts.Select(fact => fact.DedupeKey).Distinct().Count());
    }

    /// <summary>D5' §7.2: "pre-acceptance cancels never count".</summary>
    [Fact]
    public void A_pre_acceptance_cancel_produces_nothing()
    {
        var facts = RideEventHandler.Interpret(
            Envelope(RideEventTypes.Cancelled, passengerId: Guid.NewGuid(), reasonCode: "RIDER_CANCELLED_BEFORE_ACCEPT"));

        Assert.Empty(facts);
    }

    [Theory]
    [InlineData("RIDER_CANCELLED_AFTER_ACCEPT")]
    [InlineData("RIDER_CANCELLED_IN_TRIP")]
    public void A_post_acceptance_rider_cancel_counts_against_the_passenger(string reasonCode)
    {
        var passenger = Guid.NewGuid();

        var facts = RideEventHandler.Interpret(
            Envelope(RideEventTypes.Cancelled, passengerId: passenger, reasonCode: reasonCode));

        var fact = Assert.Single(facts);
        Assert.Equal(IntakeKinds.Cancellation, fact.Kind);
        Assert.Equal(passenger, fact.SubjectId);
        Assert.Equal(SubjectRoles.Passenger, fact.SubjectRole);
    }

    /// <summary>
    /// ride-svc emits <c>ride.cancelled</c> <em>and</em> <c>reputation.driver_cancelled</c> for a
    /// driver-side cancel. Reading the driver off both would count one cancel twice.
    /// </summary>
    [Fact]
    public void A_driver_cancel_is_counted_from_its_own_event_and_not_from_ride_cancelled()
    {
        var passenger = Guid.NewGuid();
        var driver = Guid.NewGuid();

        Assert.Empty(RideEventHandler.Interpret(
            Envelope(RideEventTypes.Cancelled, passengerId: passenger, driverId: driver, reasonCode: "DRIVER_CANCELLED")));

        var fact = Assert.Single(RideEventHandler.Interpret(
            Envelope(RideEventTypes.DriverCancelled, driverId: driver, reasonCode: "DRIVER_CANCELLED")));

        Assert.Equal(driver, fact.SubjectId);
        Assert.Equal(SubjectRoles.Driver, fact.SubjectRole);
    }

    [Fact]
    public void No_shows_are_counted_against_whoever_failed_to_appear()
    {
        var passenger = Guid.NewGuid();
        var driver = Guid.NewGuid();

        var rider = Assert.Single(RideEventHandler.Interpret(
            Envelope(RideEventTypes.NoShowRider, passengerId: passenger, driverId: driver)));
        Assert.Equal(passenger, rider.SubjectId);
        Assert.Equal(SubjectRoles.Passenger, rider.SubjectRole);

        var driverSide = Assert.Single(RideEventHandler.Interpret(
            Envelope(RideEventTypes.NoShowDriver, passengerId: passenger, driverId: driver)));
        Assert.Equal(driver, driverSide.SubjectId);
        Assert.Equal(SubjectRoles.Driver, driverSide.SubjectRole);
    }

    /// <summary>
    /// Everything else on <c>ride.events</c> is read and ignored — this service consumes the whole
    /// topic and only five types mean anything to it.
    /// </summary>
    [Theory]
    [InlineData("ride.requested")]
    [InlineData("ride.accepted")]
    [InlineData("offer.expired")]
    [InlineData("ride.settled")]
    public void Unrelated_events_produce_nothing(string eventType) =>
        Assert.Empty(RideEventHandler.Interpret(Envelope(eventType, passengerId: Guid.NewGuid())));

    [Fact]
    public void An_envelope_with_no_payload_produces_nothing() =>
        Assert.Empty(RideEventHandler.Interpret(
            new RideEventEnvelope(Guid.NewGuid(), RideEventTypes.Completed, Guid.NewGuid(), 1, Now, null)));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"eventType":"ride.completed"}""")]
    public void An_unusable_envelope_does_not_parse(string json) =>
        Assert.Null(RideEventEnvelope.TryParse(json));

    /// <summary>The detection window is a bucket from the epoch, so every replica agrees.</summary>
    [Fact]
    public void A_daily_detection_window_renders_as_a_date()
    {
        var day = TimeSpan.FromDays(1);

        Assert.Equal("2026-07-28", CollusionDetector.WindowKey(Now, day));
        Assert.Equal("2026-07-28", CollusionDetector.WindowKey(Now.AddHours(14), day));
        Assert.Equal("2026-07-29", CollusionDetector.WindowKey(Now.AddHours(15), day));
    }

    [Fact]
    public void A_sub_day_detection_window_renders_as_an_instant()
    {
        var key = CollusionDetector.WindowKey(Now.AddMinutes(37), TimeSpan.FromHours(6));

        Assert.Equal("2026-07-28T06:00:00Z", key);
    }

    private static RideEventEnvelope Envelope(
        string eventType,
        Guid? passengerId = null,
        Guid? driverId = null,
        string? reasonCode = null) =>
        new(
            EventId: Guid.NewGuid(),
            EventType: eventType,
            RideId: Guid.NewGuid(),
            Version: 1,
            Ts: Now,
            Payload: new RideEventPayload(
                PassengerId: passengerId,
                DriverId: driverId,
                VehicleId: null,
                State: null,
                FromState: null,
                ReasonCode: reasonCode,
                CancellationReason: null,
                SystemInitiated: null));
}
