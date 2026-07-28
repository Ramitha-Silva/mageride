using System.Net;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// D3' §0 makes <c>Idempotency-Key</c> mandatory on every POST and replays a duplicate from the
/// service's own command log (R-14). registry-svc's table is <c>registry.command_log</c>,
/// migration 0307.
/// </summary>
[Collection<PostgresCollection>]
public sealed class IdempotencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_post_without_the_header_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/vehicles")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                registrationNumber = RegistryHarness.NextPlate(),
                vehicleType = "three_wheeler",
                mode = "C",
                driverName = "Test",
            }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", harness.Tokens.Driver(await harness.CreateDriverAsync()));

        var response = await harness.Client.SendAsync(request);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "idempotency-key-required");
    }

    [Fact]
    public async Task Replaying_a_registration_returns_the_first_response_and_creates_no_second_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var key = Guid.NewGuid().ToString();
        var body = new
        {
            registrationNumber = RegistryHarness.NextPlate(),
            vehicleType = "three_wheeler",
            mode = "C",
            driverName = "Test",
        };

        var first = await harness.PostAsync("/v1/vehicles", body, bearer, key);
        var replay = await harness.PostAsync("/v1/vehicles", body, bearer, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);

        // Byte for byte (ADD §11.13) — the same vehicleId and createdAt, not a second row that
        // happens to look similar. Without this, a retried registration on a flaky network would
        // burn the driver's plate against a vehicle they never saw.
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            1,
            await connection.QuerySingleAsync<int>(
                "SELECT count(*) FROM registry.vehicles WHERE owner_id = @DriverId;",
                new { DriverId = driverId }));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_vehicle_is_a_conflict()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var key = Guid.NewGuid().ToString();

        await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "three_wheeler", mode = "C", driverName = "Test" },
            bearer,
            key);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "sedan", mode = "C", driverName = "Test" },
            bearer,
            key);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "idempotency-key-reuse");
    }

    [Fact]
    public async Task The_replay_log_is_registrys_own_table_not_rides()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var key = Guid.NewGuid().ToString();
        await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "three_wheeler", mode = "C", driverName = "Test" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()),
            key);

        await using var connection = await harness.OpenAsync();

        // Two bounded contexts sharing one command-log primary key would let a registration and
        // a ride collide on an identical client-generated key (C021 micro-change-set).
        Assert.Equal(
            1,
            await connection.QuerySingleAsync<int>(
                "SELECT count(*) FROM registry.command_log WHERE idempotency_key = @Key;",
                new { Key = key }));
        Assert.Equal(
            0,
            await connection.QuerySingleAsync<int>(
                "SELECT count(*) FROM rides.command_log WHERE idempotency_key = @Key;",
                new { Key = key }));
    }

    [Fact]
    public async Task A_rejected_registration_is_replayed_as_problem_json()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var bearer = harness.Tokens.Driver(await harness.CreateDriverAsync());
        var key = Guid.NewGuid().ToString();
        var body = new { registrationNumber = RegistryHarness.NextPlate(), vehicleType = "car", mode = "C", driverName = "Test" };

        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/vehicles", body, bearer, key), HttpStatusCode.BadRequest, "invalid-vehicle-type");

        // The 4xx is stored and replayed with its content type intact — that is what
        // registry.command_log.response_content_type exists for (C002 micro-change-set (a)).
        await ProblemDocument.AssertAsync(
            await harness.PostAsync("/v1/vehicles", body, bearer, key), HttpStatusCode.BadRequest, "invalid-vehicle-type");
    }
}
