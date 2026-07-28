using System.Net;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <c>db/seed/skeleton.sql</c> — this component's definition of done: "a driver has exactly one
/// selectable approved Mode C vehicle after seeding".
/// </summary>
/// <remarks>
/// Asserted through the API rather than by re-reading the rows the seed just wrote, because what
/// C022–C025 actually depend on is that <c>GET /v1/vehicles/mine</c> answers with one selectable
/// vehicle for a driver who signs in.
/// </remarks>
[Collection<PostgresCollection>]
public sealed class SkeletonSeedTests(PostgresFixture postgres)
{
    private static readonly Guid SkeletonDriverId = Guid.Parse("00000000-0000-4000-8000-00000000d001");
    private static readonly Guid SkeletonVehicleId = Guid.Parse("00000000-0000-4000-8000-00000000c001");

    [Fact]
    public async Task After_seeding_the_driver_has_exactly_one_selectable_approved_mode_c_vehicle()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);
        await ApplySeedAsync(harness);

        var response = await harness.GetAsync("/v1/vehicles/mine", harness.Tokens.Driver(SkeletonDriverId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var item = Assert.Single((await RegistryHarness.ReadJsonAsync(response)).GetProperty("items").EnumerateArray());

        Assert.Equal(SkeletonVehicleId.ToString(), item.GetProperty("vehicleId").GetString());
        Assert.Equal("WP-QA-0001", item.GetProperty("registrationNumber").GetString());
        Assert.Equal("three_wheeler", item.GetProperty("vehicleType").GetString());
        Assert.Equal("C", item.GetProperty("mode").GetString());
        Assert.Equal("APPROVED", item.GetProperty("status").GetString());
        Assert.Equal("approved", item.GetProperty("onboardingStatus").GetString());
        // ACTIVE, not DISPATCH_SUSPENDED: nothing has expired, so the dispatcher may offer to it.
        Assert.Equal("ACTIVE", item.GetProperty("dispatchState").GetString());
        Assert.True(item.GetProperty("isSelected").GetBoolean());
    }

    [Fact]
    public async Task The_seeded_driver_holds_the_driver_role()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);
        await ApplySeedAsync(harness);

        await using var connection = await harness.OpenAsync();

        // C020 decision 4: a first sign-in creates the account with the role of the app it came
        // from, and an existing account is never escalated. The seed creates the account, so the
        // seed has to grant the role — otherwise the skeleton driver signs in and is refused by
        // every route here.
        var roles = await connection.QueryAsync<string>(
            """
            SELECT role FROM iam.user_roles WHERE user_id = @DriverId
            UNION
            SELECT role FROM iam.users WHERE id = @DriverId;
            """,
            new { DriverId = SkeletonDriverId });

        Assert.Equal([MageRideRoles.Driver], roles);
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        await ApplySeedAsync(harness);
        await using var connection = await harness.OpenAsync();
        var before = await SnapshotAsync(connection);

        await ApplySeedAsync(harness);

        // Includes active_vehicle_selected_at: a re-run that re-stamped the selection would look
        // harmless and would quietly move a timestamp the dashboard displays.
        Assert.Equal(before, await SnapshotAsync(connection));
    }

    [Fact]
    public async Task The_seeded_vehicle_holds_its_plate_against_a_second_registration()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);
        await ApplySeedAsync(harness);

        var response = await harness.PostAsync(
            "/v1/vehicles",
            new { registrationNumber = "wp qa 0001", vehicleType = "sedan", mode = "C", driverName = "Someone Else" },
            harness.Tokens.Driver(await harness.CreateDriverAsync()));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Conflict, "registration-exists");
    }

    /// <summary>
    /// Runs the shipped seed file. <c>psql</c> is not assumed to exist on the test host, so the
    /// script is executed through Npgsql — the file itself is the artifact under test either way.
    /// </summary>
    private static async Task ApplySeedAsync(RegistryHarness harness)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Seed", "skeleton.sql");
        Assert.True(File.Exists(path), $"the seed script was not copied to the output: {path}");

        await using var connection = await harness.OpenAsync();
        await connection.ExecuteAsync(await File.ReadAllTextAsync(path));
    }

    private static Task<string> SnapshotAsync(Npgsql.NpgsqlConnection connection) =>
        connection.QuerySingleAsync<string>(
            """
            SELECT concat_ws('|',
                     (SELECT count(*) FROM registry.vehicles WHERE owner_id = @DriverId),
                     (SELECT max(registration_number || ':' || status || ':' || created_at::text)
                        FROM registry.vehicles WHERE owner_id = @DriverId),
                     (SELECT display_name || ':' || active_vehicle_id::text || ':' || active_vehicle_selected_at::text
                        FROM registry.driver_profiles WHERE driver_id = @DriverId));
            """,
            new { DriverId = SkeletonDriverId });
}
