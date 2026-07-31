using MageRide.TestKit;
using MageRide.Voip.Domain;
using MageRide.Voip.Tests.Infrastructure;
using Npgsql;

namespace MageRide.Voip.Tests.Integration;

/// <summary>
/// The claims migration 1311 makes, and the one 1302 already made that this service depends on.
/// </summary>
[Collection(VoipCollection.Name)]
public sealed class SchemaTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_room_cannot_have_two_open_sessions()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        await harness.ExecuteAsync(
            "INSERT INTO comms.voip_sessions (ride_id, livekit_room) VALUES (@RideId, 'ride_dup');",
            new { RideId = ride.Id });

        await Assert.ThrowsAsync<PostgresException>(() => harness.ExecuteAsync(
            "INSERT INTO comms.voip_sessions (ride_id, livekit_room) VALUES (@RideId, 'ride_dup');",
            new { RideId = ride.Id }));

        // Closed ones do not collide — a ride legitimately has several calls over its life.
        await harness.ExecuteAsync("UPDATE comms.voip_sessions SET ended_at = now() WHERE livekit_room = 'ride_dup';");

        await harness.ExecuteAsync(
            "INSERT INTO comms.voip_sessions (ride_id, livekit_room) VALUES (@RideId, 'ride_dup');",
            new { RideId = ride.Id });
    }

    [Fact]
    public async Task The_call_type_CHECK_still_admits_exactly_what_AL_48_left()
    {
        // The service's own vocabulary and the database's, compared. A drift either way is how a
        // withdrawn masking value gets written back into the log.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        foreach (var callType in CallTypes.All)
        {
            await harness.ExecuteAsync(
                """
                INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type)
                VALUES (@RideId, @CallerId, 'driver', @CallType);
                """,
                new { RideId = ride.Id, CallerId = ride.PassengerId, CallType = callType });
        }

        foreach (var withdrawn in new[] { "normal_masked", "web_masked" })
        {
            await Assert.ThrowsAsync<PostgresException>(() => harness.ExecuteAsync(
                """
                INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type)
                VALUES (@RideId, @CallerId, 'driver', @CallType);
                """,
                new { RideId = ride.Id, CallerId = ride.PassengerId, CallType = withdrawn }));
        }
    }

    [Fact]
    public async Task An_outcome_the_service_cannot_produce_is_refused_by_the_database_too()
    {
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        await Assert.ThrowsAsync<PostgresException>(() => harness.ExecuteAsync(
            """
            INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, outcome, ended_at)
            VALUES (@RideId, @CallerId, 'driver', 'free_voip', 'went_to_voicemail', now());
            """,
            new { RideId = ride.Id, CallerId = ride.PassengerId }));
    }

    [Fact]
    public async Task An_outcome_without_an_end_is_refused_and_so_is_an_end_without_an_outcome()
    {
        // An outcome describes a call that finished. Without the pairing a row can claim
        // `voip_failed` and still be open — which is exactly the row the SLO query counts.
        await using var harness = await VoipHarness.StartAsync(postgres);

        var ride = await harness.Seed.RideAsync();

        await Assert.ThrowsAsync<PostgresException>(() => harness.ExecuteAsync(
            """
            INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, outcome)
            VALUES (@RideId, @CallerId, 'driver', 'free_voip', 'completed');
            """,
            new { RideId = ride.Id, CallerId = ride.PassengerId }));

        await Assert.ThrowsAsync<PostgresException>(() => harness.ExecuteAsync(
            """
            INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, ended_at)
            VALUES (@RideId, @CallerId, 'driver', 'free_voip', now());
            """,
            new { RideId = ride.Id, CallerId = ride.PassengerId }));
    }

    [Fact]
    public async Task The_terminal_state_set_matches_the_databases_own_CHECK()
    {
        // This service copies ride-svc's terminal set rather than sharing a type, so the copy is
        // compared with the source of truth: every state named here must exist in ck_rides_state,
        // or a ride could reach a terminal this service does not recognise and keep issuing tokens.
        await using var harness = await VoipHarness.StartAsync(postgres);

        await using var connection = await postgres.OpenAsync();

        var check = await Dapper.SqlMapper.ExecuteScalarAsync<string>(
            connection,
            """
            SELECT pg_get_constraintdef(oid) FROM pg_constraint
             WHERE conrelid = 'rides.rides'::regclass AND conname = 'ck_rides_state';
            """);

        Assert.NotNull(check);
        Assert.All(RideStates.Terminal, state => Assert.Contains($"'{state}'", check, StringComparison.Ordinal));
    }
}
