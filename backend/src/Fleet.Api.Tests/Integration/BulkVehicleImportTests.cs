using System.Net;
using MageRide.Fleet.Endpoints;
using MageRide.Fleet.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Fleet.Tests.Integration;

/// <summary>
/// US-13.1 — bulk CSV onboarding, and the per-row error report it answers with.
/// </summary>
/// <remarks>
/// The C059 definition of done's second item — "a bulk CSV of mixed-validity rows imports the valid
/// rows and returns a row-level error report" — is
/// <see cref="A_mixed_csv_imports_the_valid_rows_and_reports_the_rest"/>.
/// </remarks>
[Collection<FleetCollection>]
public sealed class BulkVehicleImportTests(PostgresFixture postgres)
{
    /// <summary><b>Definition of done:</b> the valid rows import and the rest are reported.</summary>
    [Fact]
    public async Task A_mixed_csv_imports_the_valid_rows_and_reports_the_rest()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        // A vehicle that already exists, so one row collides with the platform rather than with the
        // file — the most likely thing an operator will ever do here is re-upload last week's CSV.
        await harness.PostJsonAsync<FleetVehicleResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles",
            new { registrationNumber = "WP-BA-0001", vehicleType = "van", mode = "B" },
            fleet.OwnerBearer);

        const string Csv = """
            registrationNumber,vehicleType,mode
            WP-BB-0002,bus,A
            WP-BA-0001,van,B
            WP-BC-0003,van,B
            WP-BD-0004,car,B
            WP-BE-0005,three_wheeler,C
            WP-BF-0006,van
            WP-BC-0003,van,B
            WP-BG-0007,mini_van,B
            """;

        using var response = await harness.UploadVehicleCsvAsync(fleet.FleetId, fleet.OwnerBearer, Csv);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var job = await FleetHarness.OkAsync<BulkJobResponse>(response, "POST vehicles/bulk");

        // Eight data rows: three good, five bad — an existing plate, an unknown type, Mode C, a
        // short line, and a plate the file itself already claimed.
        Assert.Equal(8, job.TotalRows);
        Assert.Equal(3, job.ImportedRows);
        Assert.Equal(5, job.FailedRows);

        // COMPLETED, not FAILED: five bad rows out of eight is a partial import with a report, and
        // a client branching on the status must not discard the three good vehicles.
        Assert.Equal("COMPLETED", job.Status);
        Assert.NotNull(job.ErrorReportUrl);

        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", fleet.OwnerBearer);

        // The one that was already there, plus the three the file added.
        Assert.Equal(4, roster.Items.Count);
        Assert.Contains(roster.Items, vehicle => vehicle.RegistrationNumber == "WP-BB-0002");
        Assert.Contains(roster.Items, vehicle => vehicle.RegistrationNumber == "WP-BC-0003");
        Assert.Contains(roster.Items, vehicle => vehicle.RegistrationNumber == "WP-BG-0007");

        // AL-50: every imported row starts docs_pending, and documents arrive per vehicle.
        Assert.All(roster.Items, vehicle => Assert.Equal("docs_pending", vehicle.DocsStatus));

        // The report follows the signed link with no bearer at all — which is what lets the portal
        // hand it to a browser download.
        using var report = await harness.GetAsync(job.ErrorReportUrl!);

        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("text/csv", report.Content.Headers.ContentType?.MediaType);

        var csv = await report.Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("row,registrationNumber,vehicleType,mode,error,detail", lines[0]);
        Assert.Equal(6, lines.Length);

        // Each failure speaks the same kebab vocabulary the single-vehicle POST does, and each row
        // number points at the line an operator sees in their spreadsheet — header included.
        Assert.Contains(lines, line => line.StartsWith("3,", StringComparison.Ordinal)
            && line.Contains("registration-exists", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("5,", StringComparison.Ordinal)
            && line.Contains("invalid-vehicle-type", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("6,", StringComparison.Ordinal)
            && line.Contains("mode-not-allowed", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("7,", StringComparison.Ordinal)
            && line.Contains("csv-invalid", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith("8,", StringComparison.Ordinal)
            && line.Contains("registration-exists", StringComparison.Ordinal));

        // And the job reads back after the fact — a job that existed only inside the response that
        // created it could not be polled.
        var polled = await harness.GetAsync<BulkJobResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles/bulk/{job.JobId}", fleet.OwnerBearer);

        Assert.Equal(job.ImportedRows, polled.ImportedRows);
        Assert.Equal(job.FailedRows, polled.FailedRows);
    }

    /// <summary>US-13.1b: the Service payment pair may ride the CSV, and BR-31.1 holds per row.</summary>
    [Fact]
    public async Task The_csv_may_carry_the_service_payment_and_paid_is_refused_per_row_without_a_verified_profile()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        const string Csv = """
            WP-BH-1001,van,B,free
            WP-BJ-1002,van,B,paid,250000
            WP-BK-1003,bus,A,free
            """;

        using var response = await harness.UploadVehicleCsvAsync(fleet.FleetId, fleet.OwnerBearer, Csv);
        var job = await FleetHarness.OkAsync<BulkJobResponse>(response, "POST vehicles/bulk");

        // The Free Mode B row imports; the Paid one is refused because the org has no verified
        // payout profile; the Mode A row carrying a Service payment is refused because
        // `mode_b_billing` is NULL for Mode A by design and storing nothing would tell nobody.
        Assert.Equal(1, job.ImportedRows);
        Assert.Equal(2, job.FailedRows);

        var csv = await (await harness.GetAsync(job.ErrorReportUrl!)).Content.ReadAsStringAsync();

        Assert.Contains("payout-profile-not-verified", csv, StringComparison.Ordinal);
        Assert.Contains("Mode B vehicles only", csv, StringComparison.Ordinal);

        var roster = await harness.GetAsync<FleetVehiclesResponse>(
            $"/v1/fleets/{fleet.FleetId}/vehicles", fleet.OwnerBearer);

        var imported = Assert.Single(roster.Items);
        Assert.Equal("WP-BH-1001", imported.RegistrationNumber);
        Assert.Equal("free", imported.ModeBBilling);
    }

    [Fact]
    public async Task A_file_with_no_failures_offers_no_report_link()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var response = await harness.UploadVehicleCsvAsync(
            fleet.FleetId, fleet.OwnerBearer, "WP-BL-2001,van,B\nWP-BM-2002,bus,A\n");

        var job = await FleetHarness.OkAsync<BulkJobResponse>(response, "POST vehicles/bulk");

        Assert.Equal(2, job.ImportedRows);
        Assert.Equal(0, job.FailedRows);

        // A link that downloads an empty report is a button an operator learns nothing from.
        Assert.Null(job.ErrorReportUrl);
    }

    /// <summary>The signature is the credential, and a tampered one is a 404 rather than a 403.</summary>
    [Fact]
    public async Task An_unsigned_or_re_pointed_report_link_is_not_found()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var mine = await harness.CreateFleetAsync();
        var theirs = await harness.CreateFleetAsync();

        await harness.ApproveAsync(mine.FleetId);

        using var response = await harness.UploadVehicleCsvAsync(
            mine.FleetId, mine.OwnerBearer, "WP-BN-3001,van,C\n");

        var job = await FleetHarness.OkAsync<BulkJobResponse>(response, "POST vehicles/bulk");

        Assert.NotNull(job.ErrorReportUrl);

        using var unsigned = await harness.GetAsync(
            $"/v1/fleets/{mine.FleetId}/vehicles/bulk/{job.JobId}/errors.csv");

        Assert.Equal(HttpStatusCode.NotFound, unsigned.StatusCode);

        // Re-pointing the path at another organisation's id breaks the signature, which covers the
        // fleet as well as the job.
        var repointed = job.ErrorReportUrl!.Replace(
            mine.FleetId.ToString(), theirs.FleetId.ToString(), StringComparison.Ordinal);

        using var crossOrg = await harness.GetAsync(repointed);

        Assert.Equal(HttpStatusCode.NotFound, crossOrg.StatusCode);
    }

    [Fact]
    public async Task An_empty_upload_is_refused_and_so_is_one_that_is_not_a_csv_at_all()
    {
        await using var harness = await FleetHarness.StartAsync(postgres);

        var fleet = await harness.CreateFleetAsync();
        await harness.ApproveAsync(fleet.FleetId);

        using var empty = await harness.UploadVehicleCsvAsync(fleet.FleetId, fleet.OwnerBearer, "\n\n");

        var problem = await FleetHarness.ProblemAsync(empty);

        Assert.Equal(HttpStatusCode.BadRequest, problem.Status);
        Assert.Equal("csv-invalid", problem.Code);

        using var json = await harness.PostAsync(
            $"/v1/fleets/{fleet.FleetId}/vehicles/bulk", new { file = "nope" }, fleet.OwnerBearer);

        var wrongType = await FleetHarness.ProblemAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, wrongType.Status);
        Assert.Equal("csv-invalid", wrongType.Code);
    }
}
