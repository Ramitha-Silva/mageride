using System.Net;
using System.Text;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Tests.Infrastructure;
using MageRide.Shared.Auth;
using MageRide.TestKit;

namespace MageRide.Provisioning.Tests.Integration;

/// <summary>T-09 / US-3.2: bulk IMEI onboarding from a CSV, with a per-row report.</summary>
[Collection<ProvisioningCollection>]
public sealed class BulkOnboardingTests(PostgresFixture postgres, RedisFixture redis)
{
    /// <summary>
    /// The DoD assertion: a thousand rows validate atomically and queue mint jobs with no partial
    /// commits.
    /// </summary>
    [Fact]
    public async Task A_thousand_row_csv_validates_atomically_and_queues_every_mint()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 1_000);

        var response = await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await ProvisioningHarness.ReadJsonAsync(response);
        var jobId = job.GetProperty("jobId").GetString()!;

        Assert.Equal(1_000, job.GetProperty("totalRows").GetInt32());
        Assert.Equal(BulkJobStatuses.Processing, job.GetProperty("status").GetString());

        // "Without partial commits": every row is queued and none is bound yet. A CSV that
        // half-arrived would leave the operator reconciling a fleet against a file.
        Assert.Equal(1_000, await PendingRowsAsync(harness, jobId));
        Assert.Equal(0, await BoundBindingsAsync(harness, fleetId));

        // D3': errorReportUrl "available when done".
        Assert.False(job.TryGetProperty("errorReportUrl", out _));

        await harness.DrainBulkAsync();

        var finished = await ProvisioningHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/fleets/{fleetId}/trackers/bulk/{jobId}", bearer));

        Assert.Equal(BulkJobStatuses.Completed, finished.GetProperty("status").GetString());
        Assert.Equal(1_000, finished.GetProperty("succeededRows").GetInt32());
        Assert.Equal(0, finished.GetProperty("failedRows").GetInt32());
        Assert.Equal(1_000, await BoundBindingsAsync(harness, fleetId));
        Assert.NotNull(finished.GetProperty("errorReportUrl").GetString());
    }

    /// <summary>
    /// Per-row failures land in the report rather than failing the batch (D3'). Every row's outcome
    /// is there, not only the problems — an operator reconciling 5,000 trackers needs to diff the
    /// report against the file they uploaded.
    /// </summary>
    [Fact]
    public async Task Bad_rows_fail_individually_and_the_report_explains_each_one()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 2);
        var duplicated = rows[0];

        var csv = new StringBuilder("imei,registrationNumber\n")
            .Append(duplicated.Imei).Append(',').Append(duplicated.Plate).Append('\n')       // 2: binds
            .Append(rows[1].Imei).Append(',').Append(rows[1].Plate).Append('\n')             // 3: binds
            .Append(duplicated.Imei).Append(',').Append(rows[1].Plate).Append('\n')          // 4: dup in file
            .Append(ProvisioningHarness.NextImei()).Append(",WP-NOPE-1\n")                   // 5: not on roster
            .Append("12345,").Append(rows[1].Plate).Append('\n')                             // 6: short imei
            .Append("only-one-column\n")                                                     // 7: malformed
            .ToString();

        var accepted = await ProvisioningHarness.ReadJsonAsync(
            await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", csv, bearer));

        var jobId = accepted.GetProperty("jobId").GetString()!;
        Assert.Equal(6, accepted.GetProperty("totalRows").GetInt32());

        await harness.DrainBulkAsync();

        var finished = await ProvisioningHarness.ReadJsonAsync(
            await harness.GetAsync($"/v1/fleets/{fleetId}/trackers/bulk/{jobId}", bearer));

        Assert.Equal(BulkJobStatuses.Completed, finished.GetProperty("status").GetString());
        Assert.Equal(2, finished.GetProperty("succeededRows").GetInt32());
        Assert.Equal(4, finished.GetProperty("failedRows").GetInt32());

        // The link is signed and needs no bearer, which is what lets the Admin Portal hand it to a
        // browser download.
        var report = await harness.Client.GetAsync(finished.GetProperty("errorReportUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);

        var text = await report.Content.ReadAsStringAsync();

        Assert.StartsWith("row,imei,registrationNumber,status,errorCode,errorDetail", text, StringComparison.Ordinal);
        Assert.Contains("\"imei-duplicate\"", text, StringComparison.Ordinal);
        Assert.Contains("\"vehicle-not-found\"", text, StringComparison.Ordinal);
        Assert.Contains("\"validation-failed\"", text, StringComparison.Ordinal);
        Assert.Contains("\"csv-invalid\"", text, StringComparison.Ordinal);

        // Line numbers are the spreadsheet's, so a report line points at the row to fix.
        Assert.Contains("\n7,", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Re-uploading last week's file is the most likely thing an operator will ever do here. Those
    /// rows are refused at validation, so the anti-clone rule never sees them — putting them
    /// through the bind path would quarantine a working fleet.
    /// </summary>
    [Fact]
    public async Task Re_uploading_the_same_csv_fails_the_rows_instead_of_quarantining_the_fleet()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 3);
        var csv = BuildCsv(rows);

        await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", csv, bearer);
        await harness.DrainBulkAsync();

        var again = await ProvisioningHarness.ReadJsonAsync(
            await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", csv, bearer));

        Assert.Equal(BulkJobStatuses.Completed, again.GetProperty("status").GetString());
        Assert.Equal(3, again.GetProperty("failedRows").GetInt32());

        foreach (var row in rows)
        {
            var binding = Assert.Single(await harness.BindingsAsync(row.Imei));
            Assert.Equal(BindingStates.Active, binding.State);
        }
    }

    /// <summary>D3': only one bulk job per fleet may be in flight.</summary>
    [Fact]
    public async Task A_second_job_while_one_is_in_flight_is_429_bulk_in_progress()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 2);

        var first = await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer);
        await ProblemDocument.AssertAsync(second, HttpStatusCode.TooManyRequests, "bulk-in-progress");

        // The slot frees when the first job finishes, not on a timer.
        await harness.DrainBulkAsync();

        var third = await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer);
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
    }

    [Fact]
    public async Task More_than_five_thousand_rows_is_413_too_many_rows()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var fleetOwnerId = await harness.CreateUserAsync("fleet_owner");
        var fleetId = await harness.CreateFleetAsync(fleetOwnerId);
        var bearer = harness.Tokens.Issue(fleetOwnerId, [MageRideRoles.FleetOwner], MageRideApps.Fleet);

        var csv = new StringBuilder("imei,registrationNumber\n");
        for (var i = 0; i < 5_001; i++)
        {
            csv.Append(ProvisioningHarness.NextImei()).Append(",WP-QA-9999\n");
        }

        var response = await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", csv.ToString(), bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.RequestEntityTooLarge, "too-many-rows");
    }

    [Fact]
    public async Task An_empty_upload_is_400_csv_invalid()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var fleetOwnerId = await harness.CreateUserAsync("fleet_owner");
        var fleetId = await harness.CreateFleetAsync(fleetOwnerId);
        var bearer = harness.Tokens.Issue(fleetOwnerId, [MageRideRoles.FleetOwner], MageRideApps.Fleet);

        var response = await harness.PostCsvAsync(
            $"/v1/fleets/{fleetId}/trackers/bulk", "imei,registrationNumber\n", bearer);

        await ProblemDocument.AssertAsync(response, HttpStatusCode.BadRequest, "csv-invalid");
    }

    /// <summary>
    /// "Not your fleet" and "no such fleet" are the same answer, so a caller cannot enumerate fleet
    /// ids — and D3' gives this operation no 404 to tell them apart with.
    /// </summary>
    [Fact]
    public async Task Another_operators_fleet_is_403_forbidden()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, _, rows) = await SeedFleetAsync(harness, 1);
        var strangerId = await harness.CreateUserAsync("fleet_owner");

        var response = await harness.PostCsvAsync(
            $"/v1/fleets/{fleetId}/trackers/bulk",
            BuildCsv(rows),
            harness.Tokens.Issue(strangerId, [MageRideRoles.FleetOwner], MageRideApps.Fleet));

        await ProblemDocument.AssertAsync(response, HttpStatusCode.Forbidden, "forbidden");
    }

    /// <summary>The link is the credential; a tampered one has proved nothing about the job.</summary>
    [Fact]
    public async Task An_unsigned_or_tampered_report_link_is_404()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 1);

        var accepted = await ProvisioningHarness.ReadJsonAsync(
            await harness.PostCsvAsync($"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer));

        var jobId = accepted.GetProperty("jobId").GetString();
        await harness.DrainBulkAsync();

        var bare = await harness.Client.GetAsync($"/v1/fleets/{fleetId}/trackers/bulk/{jobId}/errors.csv");
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);

        var forged = await harness.Client.GetAsync(
            $"/v1/fleets/{fleetId}/trackers/bulk/{jobId}/errors.csv?expires=99999999999&signature=AAAA");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    /// <summary>A fleet is one hardware generation more often than not, so the choice is per batch.</summary>
    [Fact]
    public async Task A_batch_may_choose_psk_for_a_legacy_fleet()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        Assert.SkipWhen(!redis.IsAvailable, redis.SkipReason ?? string.Empty);

        await using var harness = await ProvisioningHarness.StartAsync(postgres, redis);

        var (fleetId, bearer, rows) = await SeedFleetAsync(harness, 2);

        await harness.PostCsvAsync(
            $"/v1/fleets/{fleetId}/trackers/bulk", BuildCsv(rows), bearer, CredentialTypes.Psk);

        await harness.DrainBulkAsync();

        await using var connection = await harness.OpenAsync();
        var kinds = await Dapper.SqlMapper.QueryAsync<string>(
            connection,
            "SELECT DISTINCT credential_type FROM prov.tracker_bindings WHERE imei = ANY(@Imeis);",
            new { Imeis = rows.Select(row => row.Imei).ToArray() });

        Assert.Equal(CredentialTypes.Psk, Assert.Single(kinds));
    }

    // -------------------------------------------------------------------------------------

    private static async Task<(Guid FleetId, string Bearer, IReadOnlyList<(string Imei, string Plate)> Rows)>
        SeedFleetAsync(ProvisioningHarness harness, int count)
    {
        var fleetOwnerId = await harness.CreateUserAsync("fleet_owner");
        var driverId = await harness.CreateUserAsync();
        var fleetId = await harness.CreateFleetAsync(fleetOwnerId);

        var rows = new List<(string Imei, string Plate)>(count);

        for (var i = 0; i < count; i++)
        {
            var plate = ProvisioningHarness.NextPlate();
            var vehicleId = await harness.CreateVehicleAsync(driverId, plate);
            await harness.AddToFleetAsync(fleetId, vehicleId);

            rows.Add((ProvisioningHarness.NextImei(), plate));
        }

        return (fleetId, harness.Tokens.Issue(fleetOwnerId, [MageRideRoles.FleetOwner], MageRideApps.Fleet), rows);
    }

    private static string BuildCsv(IReadOnlyList<(string Imei, string Plate)> rows)
    {
        var csv = new StringBuilder("imei,registrationNumber\n");

        foreach (var (imei, plate) in rows)
        {
            csv.Append(imei).Append(',').Append(plate).Append('\n');
        }

        return csv.ToString();
    }

    private static async Task<int> PendingRowsAsync(ProvisioningHarness harness, string jobId)
    {
        await using var connection = await harness.OpenAsync();

        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection,
            $"SELECT count(*) FROM prov.bulk_job_rows WHERE job_id = @JobId AND status = '{BulkRowStatuses.Pending}';",
            new { JobId = Guid.Parse(jobId) });
    }

    private static async Task<int> BoundBindingsAsync(ProvisioningHarness harness, Guid fleetId)
    {
        await using var connection = await harness.OpenAsync();

        return await Dapper.SqlMapper.ExecuteScalarAsync<int>(
            connection,
            $"SELECT count(*) FROM prov.tracker_bindings WHERE fleet_id = @FleetId AND state = '{BindingStates.Active}';",
            new { FleetId = fleetId });
    }
}
