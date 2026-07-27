using MageRide.TestKit;

namespace MageRide.Shared.Tests.Migrations;

/// <summary>
/// C010 — proves the Testcontainers harness starts a real PostgreSQL and applies
/// <c>db/migrations/*.sql</c>, and that a second apply changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-process half of the CI migration gate. The workflow's
/// <c>migrations</c> job runs <c>infra/scripts/migrate-verify.sh</c>, which additionally
/// asserts every object C003-C006 promised; these tests assert the property that gate exists
/// for — apply, re-apply is a no-op, re-apply without the journal still succeeds — so a broken
/// migration fails the fast backend job rather than waiting for the slow one.
/// </para>
/// <para>
/// Migrations go through <c>MageRide.Migrations.MigrationEngine</c>, the same DbUp pipeline the
/// <c>migrate</c> container runs, so passing here means the dev stack and the replica apply the
/// identical script set in the identical order.
/// </para>
/// </remarks>
[Collection<PostgresCollection>]
public sealed class MigrationHarnessTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_script_applies_to_an_empty_database()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        var outcome = await postgres.EnsureMigratedAsync();

        Assert.True(outcome.Successful, outcome.Error?.Message);
        Assert.True(outcome.AvailableScripts > 0, "the embedded migration set is empty");
        Assert.Equal(outcome.AvailableScripts, outcome.ScriptsApplied);
    }

    [Fact]
    public async Task The_journal_records_one_row_per_script()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        var outcome = await postgres.EnsureMigratedAsync();

        var journalled = await postgres.ScalarAsync<long>(
            "SELECT count(*) FROM public.schema_versions");

        Assert.Equal(outcome.AvailableScripts, (int)journalled);
    }

    /// <summary>
    /// The definition-of-done case: the journal must suppress every script on a second run.
    /// A migration that re-applies is how a deploy silently rewrites a live table.
    /// </summary>
    [Fact]
    public async Task A_second_apply_is_a_no_op()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await postgres.EnsureMigratedAsync();

        var second = postgres.ApplyMigrations();

        Assert.True(second.Successful, second.Error?.Message);
        Assert.Equal(0, second.ScriptsApplied);
    }

    /// <summary>
    /// Pass 3 of <c>migrate-verify.sh</c>: with the journal disabled every script executes
    /// again, which proves the DDL itself is re-runnable rather than only proving that DbUp
    /// remembers what it already did.
    /// </summary>
    [Fact]
    public async Task Re_applying_without_the_journal_still_succeeds()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        var first = await postgres.EnsureMigratedAsync();

        var replayed = postgres.ApplyMigrations(ignoreJournal: true);

        Assert.True(replayed.Successful,
            $"'{replayed.FailedScript}' is not idempotent: {replayed.Error?.Message}");
        Assert.Equal(first.AvailableScripts, replayed.ScriptsApplied);
    }

    /// <summary>
    /// The fixture must run the image the platform deploys. On <c>postgis/postgis</c> the C006
    /// scripts cannot even parse, so this failing is the difference between "migrations are
    /// broken" and "the harness is testing the wrong server".
    /// </summary>
    [Fact]
    public async Task The_container_carries_postgis_and_timescaledb()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await postgres.EnsureMigratedAsync();

        var extensions = await postgres.ScalarAsync<long>(
            "SELECT count(*) FROM pg_extension WHERE extname IN ('postgis','timescaledb','pgcrypto','citext')");

        Assert.Equal(4, extensions);
    }

    [Fact]
    public async Task The_telemetry_hypertable_exists()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);

        await postgres.EnsureMigratedAsync();

        var hypertables = await postgres.ScalarAsync<long>(
            "SELECT count(*) FROM timescaledb_information.hypertables "
            + "WHERE hypertable_schema = 'telemetry' AND hypertable_name = 'positions'");

        Assert.Equal(1, hypertables);
    }
}
