using System.Net;
using MageRide.TestKit;
using MageRide.Voip.Domain;
using MageRide.Voip.Endpoints;
using MageRide.Voip.Tests.Infrastructure;

namespace MageRide.Voip.Tests.Integration;

/// <summary>
/// <b>Definition of done: "call_log rows distinguish free_voip from client-reported
/// direct_dial."</b>
/// </summary>
[Collection(VoipCollection.Name)]
public sealed class CallTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_free_voip_call_starts_a_session_and_a_direct_dial_starts_nothing()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        var bearer = harness.Tokens.Passenger(ride.PassengerId);

        var voip = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            bearer);

        var dialled = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.DirectDial },
            bearer);

        // The in-app call carries a session to join.
        Assert.NotNull(voip.Session);
        Assert.Equal($"ride_{ride.Id:D}", voip.Session!.RoomName);
        Assert.Equal(VoipHarness.WsUrl, voip.Session.WsUrl);

        // The tel: dial carries none, and could not: the platform never sees the PSTN leg (AL-48).
        Assert.Null(dialled.Session);
        Assert.Equal(CallTypes.DirectDial, dialled.CallType);

        var rows = await harness.CallLogAsync(ride.Id);

        Assert.Equal([CallTypes.FreeVoip, CallTypes.DirectDial], rows.Select(row => row.CallType));
        Assert.All(rows, row => Assert.Equal(ride.PassengerId, row.CallerId));
        Assert.All(rows, row => Assert.Equal(CalleeRoles.Driver, row.CalleeRole));

        // One session, from the in-app call alone.
        var session = Assert.Single(await harness.SessionsAsync(ride.Id));

        Assert.Equal($"ride_{ride.Id:D}", session.LivekitRoom);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public async Task Both_parties_calling_join_one_session_rather_than_opening_two()
    {
        // D3' gives a ride one room, and the driver ringing back is the same conversation. Without
        // `ux_voip_sessions_open_room` (migration 1311) each tap would open a rival session and the
        // teardown at trip end would close whichever it happened to find.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(ride.PassengerId));

        await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Passenger, callType = CallTypes.FreeVoip },
            harness.Tokens.Driver(ride.DriverId));

        Assert.Single(await harness.SessionsAsync(ride.Id));
        Assert.Equal(2, (await harness.CallLogAsync(ride.Id)).Count);
    }

    [Fact]
    public async Task A_direct_dial_is_logged_even_where_in_app_calling_is_absent()
    {
        // The fallback has to be recordable exactly when VoIP is not there, or the one number that
        // measures AL-48's fallback is missing for the deployments that use it most.
        await using var harness = await VoipHarness.StartAsync(postgres, withLiveKit: false);

        var ride = await harness.Seed.RideAsync();

        var dialled = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.DirectDial },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Null(dialled.Session);
        Assert.Single(await harness.CallLogAsync(ride.Id));
        Assert.Empty(await harness.SessionsAsync(ride.Id));
    }

    [Fact]
    public async Task The_masked_call_type_AL_48_removed_is_refused()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        using var response = await harness.PostAsync(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = "normal_masked" },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await harness.CallLogAsync(ride.Id));
    }

    [Fact]
    public async Task A_parcels_sender_or_recipient_cannot_be_reached_in_app()
    {
        // P-09: they may have no MageRide account at all, so there is nobody to admit to a room.
        // Their Call button is a tel: link and always was (I-28.1) — which this still logs.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        var bearer = harness.Tokens.Driver(ride.DriverId);

        using var refused = await harness.PostAsync(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Recipient, callType = CallTypes.FreeVoip },
            bearer);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var dialled = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Recipient, callType = CallTypes.DirectDial },
            bearer);

        Assert.Equal(CallTypes.DirectDial, dialled.CallType);
    }

    [Fact]
    public async Task A_proxy_booker_cannot_start_a_call_of_any_kind()
    {
        // P-05 again, on the other route: the fence is one decision made once, so `/token` and
        // `/calls/start` cannot come to different conclusions about the same ride.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync(proxy: true);
        var booker = harness.Tokens.Passenger(ride.BookerId);

        foreach (var callType in CallTypes.All)
        {
            using var response = await harness.PostAsync(
                "/v1/calls/start",
                new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType },
                booker);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Empty(await harness.CallLogAsync(ride.Id));
    }

    [Fact]
    public async Task A_failed_call_is_recorded_as_the_signal_the_fallback_hangs_on()
    {
        // ADD §14's "Call normally instead?" and ADD §16's call-setup SLO both need this: without
        // it a call that never connected is indistinguishable from one that did, and a direct_dial
        // that followed a failure is indistinguishable from a user who simply preferred to dial.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        var bearer = harness.Tokens.Passenger(ride.PassengerId);

        var call = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            bearer);

        using var recorded = await harness.PostAsync(
            $"/v1/calls/{call.CallId}/outcome", new { outcome = CallOutcomes.VoipFailed }, bearer);

        Assert.Equal(HttpStatusCode.NoContent, recorded.StatusCode);

        var row = Assert.Single(await harness.CallLogAsync(ride.Id));

        Assert.Equal(CallOutcomes.VoipFailed, row.Outcome);
        Assert.NotNull(row.EndedAt);
    }

    [Fact]
    public async Task An_outcome_can_only_be_reported_once_and_only_by_the_caller()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        var call = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            harness.Tokens.Passenger(ride.PassengerId));

        // The other party did not place this call and cannot say how it went — and is answered 404
        // rather than 403, because a call id is guessable and "that id exists" is itself something
        // a stranger should not learn about two other people's conversation.
        using var byDriver = await harness.PostAsync(
            $"/v1/calls/{call.CallId}/outcome",
            new { outcome = CallOutcomes.Completed },
            harness.Tokens.Driver(ride.DriverId));

        Assert.Equal(HttpStatusCode.NotFound, byDriver.StatusCode);

        using var first = await harness.PostAsync(
            $"/v1/calls/{call.CallId}/outcome",
            new { outcome = CallOutcomes.Completed },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        // A resumed app reporting again must not overwrite the first answer with a worse one.
        using var second = await harness.PostAsync(
            $"/v1/calls/{call.CallId}/outcome",
            new { outcome = CallOutcomes.VoipFailed },
            harness.Tokens.Passenger(ride.PassengerId));

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(CallOutcomes.Completed, Assert.Single(await harness.CallLogAsync(ride.Id)).Outcome);
    }

    [Fact]
    public async Task An_outcome_no_spec_names_is_refused()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();
        var bearer = harness.Tokens.Passenger(ride.PassengerId);

        var call = await harness.PostAsync<StartCallResponse>(
            "/v1/calls/start",
            new { rideId = ride.Id, calleeRole = CalleeRoles.Driver, callType = CallTypes.FreeVoip },
            bearer);

        using var response = await harness.PostAsync(
            $"/v1/calls/{call.CallId}/outcome", new { outcome = "went_to_voicemail" }, bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
