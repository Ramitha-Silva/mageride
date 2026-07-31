using Dapper;
using MageRide.Fleet.Persistence;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;
using Npgsql;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// The C058 fence — "a cross-org read attempt is refused by RLS, <b>not by application filtering
/// alone</b>".
/// </summary>
/// <remarks>
/// <para>
/// Every assertion here connects as <see cref="FleetHarness.FleetReaderLogin"/>: a real login role
/// whose only privileges are the grants migration 1806 gives <c>mageride_fleet_reader</c>. There
/// is no fleet-svc code in the path. What is being asserted is that Postgres refuses the read —
/// which is the only form of the claim worth making, because an assertion made through this
/// service's own SQL would pass just as happily if the policies did not exist.
/// </para>
/// <para>
/// The container's <c>mageride</c> user is a superuser and a superuser bypasses RLS entirely, so
/// the distinction between <see cref="FleetHarness.OpenAsync"/> and
/// <see cref="FleetHarness.OpenAsFleetReaderAsync"/> is the whole test.
/// </para>
/// </remarks>
[Collection<FleetCollection>]
public sealed class RowLevelSecurityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_fleet_reader_cannot_reach_another_organisation_even_by_primary_key()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await SubmitPayoutAsync(harness, theirs, "9876543210");

        await using var reader = await harness.OpenAsFleetReaderAsync();
        await using var transaction = await reader.BeginTransactionAsync();

        await ScopeAsync(reader, transaction, mine.FleetId);

        // Named explicitly, which is the strongest form of the question: the row is asked for by
        // its own id and the policy still refuses it.
        var organisation = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM registry.fleets WHERE id = @Id;",
            new { Id = theirs.FleetId },
            transaction));

        var bankDetails = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM registry.fleet_payout_profiles WHERE fleet_id = @Id;",
            new { Id = theirs.FleetId },
            transaction));

        var team = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM iam.fleet_members WHERE fleet_id = @Id;",
            new { Id = theirs.FleetId },
            transaction));

        Assert.Equal(0, organisation);
        Assert.Equal(0, bankDetails);
        Assert.Equal(0, team);

        // And the caller's own organisation is visible, so the zeroes above are scoping rather
        // than a broken connection.
        var own = await reader.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM registry.fleets WHERE id = @Id;",
            new { Id = mine.FleetId },
            transaction));

        Assert.Equal(1, own);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// An unscoped connection sees nothing at all.
    /// </summary>
    /// <remarks>
    /// The half that makes the design safe rather than merely correct: a bug that forgot to set
    /// <c>app.fleet_id</c> returns an empty page, not the platform. 1806 uses the two-argument
    /// <c>current_setting</c> for exactly this — the one-argument form raises, and a caller who
    /// catches the error is one retry away from an unscoped read.
    /// </remarks>
    [Fact]
    public async Task An_unscoped_fleet_reader_sees_nothing()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        await harness.CreateFleetAsync();
        await harness.CreateFleetAsync();

        await using var reader = await harness.OpenAsFleetReaderAsync();

        var organisations = await reader.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM registry.fleets;");
        var members = await reader.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM iam.fleet_members;");
        var rosters = await reader.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM registry.fleet_vehicles_fleet;");

        Assert.Equal(0, organisations);
        Assert.Equal(0, members);
        Assert.Equal(0, rosters);
    }

    /// <summary>
    /// The three tables the join views exist to keep out of reach are not reachable.
    /// </summary>
    /// <remarks>
    /// If any of them is ever granted, the scoping above becomes decorative: a fleet reader could
    /// simply read the base table instead of the view. <c>registry.vehicles</c> is every vehicle on
    /// the platform, <c>iam.users</c> every person, <c>trips.sessions</c> every Mode A/B journey.
    /// </remarks>
    [Theory]
    [InlineData("registry.vehicles")]
    [InlineData("iam.users")]
    [InlineData("trips.sessions")]
    [InlineData("registry.documents")]
    public async Task A_fleet_reader_holds_no_privilege_on_the_platform_wide_tables(string relation)
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        await using var reader = await harness.OpenAsFleetReaderAsync();

        var denied = await Assert.ThrowsAsync<PostgresException>(
            () => reader.ExecuteScalarAsync<int>($"SELECT count(*)::int FROM {relation};"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    [Fact]
    public async Task A_fleet_reader_cannot_write_anything()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        await using var reader = await harness.OpenAsFleetReaderAsync();
        await using var transaction = await reader.BeginTransactionAsync();

        await ScopeAsync(reader, transaction, fleet.FleetId);

        // Its own organisation, which it can read — so what fails is the privilege, not the policy.
        var denied = await Assert.ThrowsAsync<PostgresException>(
            () => reader.ExecuteAsync(new CommandDefinition(
                "UPDATE registry.fleets SET status = 'APPROVED' WHERE id = @Id;",
                new { Id = fleet.FleetId },
                transaction)));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The join views are scoped too, and they are the only reach into the tables behind them.
    /// </summary>
    [Fact]
    public async Task The_scoped_views_join_what_the_reader_cannot_read_and_scope_the_result()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        var myVehicle = await harness.AddVehicleAsync(mine.FleetId, mine.OwnerId);
        await harness.AddVehicleAsync(theirs.FleetId, theirs.OwnerId);

        await using var reader = await harness.OpenAsFleetReaderAsync();
        await using var transaction = await reader.BeginTransactionAsync();

        await ScopeAsync(reader, transaction, mine.FleetId);

        // registry.fleet_vehicles_fleet joins registry.vehicles, which the theory above proves the
        // reader cannot touch — so the registration number can only have come through the view.
        var roster = await reader.QueryAsync<(Guid VehicleId, string Registration)>(new CommandDefinition(
            "SELECT vehicle_id, registration_number FROM registry.fleet_vehicles_fleet;",
            transaction: transaction));

        var rows = roster.ToArray();

        Assert.Single(rows);
        Assert.Equal(myVehicle, rows[0].VehicleId);
        Assert.StartsWith("TST-", rows[0].Registration, StringComparison.Ordinal);

        // iam.fleet_members_fleet joins iam.users, and deliberately projects no phone number: a
        // sub-user's mobile is their own, not the organisation's.
        var team = await reader.QueryAsync<(Guid UserId, string FleetRole)>(new CommandDefinition(
            "SELECT user_id, fleet_role FROM iam.fleet_members_fleet;", transaction: transaction));

        Assert.Single(team);

        var phoneIsProjected = await reader.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (SELECT 1 FROM information_schema.columns
                            WHERE table_schema='iam' AND table_name='fleet_members_fleet'
                              AND column_name='phone');
            """,
            transaction: transaction));

        Assert.False(phoneIsProjected);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The service reaches the same place its own way: <c>SET LOCAL ROLE</c> inside the read.
    /// </summary>
    /// <remarks>
    /// Asserted through <see cref="IFleetScopedReader"/> rather than over HTTP, because what is
    /// being checked is that the reader really assumes the role — a request that passed through a
    /// superuser connection would return exactly the same rows for exactly the wrong reason.
    /// </remarks>
    [Fact]
    public async Task The_services_own_reads_run_as_the_fleet_reader_role()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();

        var scoped = harness.Services.GetRequiredService<IFleetScopedReader>();

        var (role, guc, superuser) = await scoped.ReadAsync(
            fleet.FleetId,
            async (connection, transaction) => await connection.QuerySingleAsync<(string, string, bool)>(
                new CommandDefinition(
                    """
                    SELECT current_user,
                           coalesce(current_setting('app.fleet_id', true), ''),
                           coalesce((SELECT rolsuper FROM pg_roles WHERE rolname = current_user), false);
                    """,
                    transaction: transaction)));

        Assert.Equal(FleetScope.ReaderRole, role);
        Assert.Equal(fleet.FleetId.ToString(), guc);
        Assert.False(superuser);
    }

    /// <summary>
    /// The role and the setting do not survive the transaction that set them.
    /// </summary>
    /// <remarks>
    /// <c>SET LOCAL</c> is what makes the scoped read safe under PgBouncer transaction pooling
    /// (D7' §4.1): the next transaction on the same server connection must not inherit either.
    /// Asserted on a pooled connection taken straight afterwards, which is exactly what a second
    /// request would get.
    /// </remarks>
    [Fact]
    public async Task The_scope_does_not_leak_to_the_next_transaction()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        var scoped = harness.Services.GetRequiredService<IFleetScopedReader>();

        await scoped.ReadAsync(
            fleet.FleetId,
            async (connection, transaction) => await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT 1;", transaction: transaction)));

        var factory = harness.Services.GetRequiredService<MageRide.Shared.Persistence.INpgsqlConnectionFactory>();

        await using var next = await factory.OpenAsync();

        var (role, guc) = await next.QuerySingleAsync<(string, string)>(
            "SELECT current_user, coalesce(current_setting('app.fleet_id', true), '');");

        Assert.NotEqual(FleetScope.ReaderRole, role);
        Assert.Equal(string.Empty, guc);
    }

    private static async Task ScopeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid fleetId) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT set_config('app.fleet_id', @FleetId, true);",
            new { FleetId = fleetId.ToString() },
            transaction));

    private static async Task SubmitPayoutAsync(FleetHarness harness, SeededFleet fleet, string accountNo)
    {
        using var response = await harness.PutAsync(
            $"/v1/fleets/{fleet.FleetId}/payout-profile",
            new
            {
                bank = "Bank of Ceylon",
                branch = "Nugegoda",
                accountNo,
                accountHolderName = "Somebody Else (Pvt) Ltd",
            },
            fleet.OwnerBearer);

        Assert.True(response.IsSuccessStatusCode);
    }
}
