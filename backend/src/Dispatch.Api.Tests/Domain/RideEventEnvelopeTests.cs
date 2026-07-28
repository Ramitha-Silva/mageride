using System.Text.Json;
using MageRide.Dispatch.Messaging;
using MageRide.Dispatch.Timers;
using MageRide.Shared.Http;

namespace MageRide.Dispatch.Tests.Domain;

/// <summary>
/// The <c>ride.events</c> reader, against the envelope ride-svc actually produces (D6' §2.2).
/// </summary>
public sealed class RideEventEnvelopeTests
{
    /// <summary>
    /// A <c>ride.requested</c> as <c>RideEvents.Build</c> serialises one: camelCase, nulls omitted.
    /// </summary>
    private const string RideRequested =
        """
        {"eventId":"11111111-1111-4111-8111-111111111111","eventType":"ride.requested",
         "rideId":"22222222-2222-4222-8222-222222222222","version":1,
         "ts":"2026-07-28T04:00:00+00:00",
         "payload":{"passengerId":"33333333-3333-4333-8333-333333333333",
                    "bookerId":"33333333-3333-4333-8333-333333333333",
                    "riderId":"33333333-3333-4333-8333-333333333333",
                    "kind":"passenger","isProxy":false,"state":"Requested",
                    "vehicleType":"three_wheeler","paymentMethod":"cash",
                    "fareEstimateMinor":74000,"currency":"LKR",
                    "pickup":{"lat":6.9344,"lng":79.8428},
                    "dropoff":{"lat":6.8514,"lng":79.8653}}}
        """;

    [Fact]
    public void A_ride_requested_becomes_a_dispatch_request()
    {
        var envelope = RideEventEnvelope.TryParse(RideRequested);

        Assert.NotNull(envelope);
        Assert.Equal(RideEventTypes.Requested, envelope.EventType);

        var request = envelope.ToDispatchRequest();

        Assert.NotNull(request);
        Assert.Equal(Guid.Parse("22222222-2222-4222-8222-222222222222"), request.RideId);
        Assert.Equal(6.9344, request.Pickup.Latitude, 6);
        Assert.Equal(79.8428, request.Pickup.Longitude, 6);
        Assert.Equal("three_wheeler", request.VehicleType);
        Assert.Equal("cash", request.PaymentMethod);
        Assert.Equal(74_000, request.FareEstimateMinor);
        Assert.Equal("LKR", request.Currency);
    }

    /// <summary>
    /// The whole reason the cascade can be event-driven: <c>offer.expired</c> carries the same
    /// payload, so a re-offer needs no read of <c>rides.rides</c>.
    /// </summary>
    [Fact]
    public void An_offer_expired_carries_everything_a_second_round_needs()
    {
        var json = RideRequested
            .Replace("ride.requested", "offer.expired", StringComparison.Ordinal)
            .Replace("\"state\":\"Requested\"", "\"state\":\"Matching\"", StringComparison.Ordinal);

        var request = RideEventEnvelope.TryParse(json)?.ToDispatchRequest();

        Assert.NotNull(request);
        Assert.Equal("three_wheeler", request.VehicleType);
    }

    /// <summary>
    /// ride-svc clears <c>offered_driver_id</c> and <c>current_offer_id</c> before building the
    /// envelope, so neither <c>offer.declined</c> nor <c>offer.expired</c> names the driver. This
    /// is why <c>IDispatchService.ReleaseLiveOfferAsync</c> is keyed by ride — recorded as a
    /// contract gap in the C023 handoff.
    /// </summary>
    [Fact]
    public void An_offer_declined_names_neither_the_driver_nor_the_offer()
    {
        var json = RideRequested.Replace("ride.requested", "offer.declined", StringComparison.Ordinal);

        var envelope = RideEventEnvelope.TryParse(json);

        Assert.NotNull(envelope);
        Assert.Null(envelope.Payload!.DriverId);
        Assert.Null(envelope.Payload.OfferId);
    }

    [Fact]
    public void A_ride_accepted_names_the_winning_driver()
    {
        var json = RideRequested
            .Replace("ride.requested", "ride.accepted", StringComparison.Ordinal)
            .Replace(
                "\"kind\":\"passenger\"",
                "\"driverId\":\"44444444-4444-4444-8444-444444444444\",\"kind\":\"passenger\"",
                StringComparison.Ordinal);

        var envelope = RideEventEnvelope.TryParse(json);

        Assert.Equal(Guid.Parse("44444444-4444-4444-8444-444444444444"), envelope!.Payload!.DriverId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"eventType\":\"ride.requested\"}")]                                    // no rideId
    [InlineData("{\"rideId\":\"22222222-2222-4222-8222-222222222222\",\"eventType\":\"\"}")]
    public void An_unusable_message_parses_to_null(string json) =>
        Assert.Null(RideEventEnvelope.TryParse(json));

    [Fact]
    public void An_envelope_with_no_pickup_produces_no_dispatch_request()
    {
        var json = JsonSerializer.Serialize(
            new
            {
                eventId = Guid.NewGuid(),
                eventType = RideEventTypes.Requested,
                rideId = Guid.NewGuid(),
                version = 1,
                ts = DateTimeOffset.UtcNow,
                payload = new { vehicleType = "three_wheeler" },
            },
            MageRideJson.StorageOptions);

        Assert.Null(RideEventEnvelope.TryParse(json)!.ToDispatchRequest());
    }

    [Fact]
    public void A_producer_that_grew_a_field_is_still_readable()
    {
        // D6' §2.3 makes delivery at-least-once across a rolling deploy, so the consumer has to
        // tolerate an envelope written by a newer ride-svc.
        var json = RideRequested.Replace(
            "\"kind\":\"passenger\"", "\"kind\":\"passenger\",\"somethingNew\":42", StringComparison.Ordinal);

        Assert.NotNull(RideEventEnvelope.TryParse(json)?.ToDispatchRequest());
    }

    [Theory]
    [InlineData("offer:22222222-2222-4222-8222-222222222222", true)]
    [InlineData("offer:not-a-guid", false)]
    [InlineData("lock:driver-offer:22222222-2222-4222-8222-222222222222", false)]
    [InlineData("driver:availability:22222222-2222-4222-8222-222222222222", false)]
    [InlineData("", false)]
    public void Only_an_offer_key_triggers_the_keyspace_expiry_path(string key, bool expected) =>
        Assert.Equal(expected, OfferKeyspaceListener.RideIdFromOfferKey(key) is not null);
}
