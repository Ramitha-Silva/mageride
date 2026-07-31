using System.Net;
using System.Text;
using MageRide.Shared.Auth;
using MageRide.TestKit;
using MageRide.Transit.Tests.Infrastructure;

namespace MageRide.Transit.Tests.Integration;

/// <summary>
/// US-28.1 — upload, sha256 dedupe, the BR-32.1 validation pipeline and its row-level report.
/// </summary>
/// <remarks>
/// <b>Definition of done:</b> "a malformed feed fails validation and produces a downloadable
/// row-level report; nothing is activated" and "re-uploading the identical file returns 409 with
/// the existing version number".
/// </remarks>
[Trait("Category", "Gtfs")]
[Collection(TransitCollection.Name)]
public sealed class GtfsUploadTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_valid_feed_is_accepted_validated_and_previewed()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(GtfsZipBuilder.Valid().Build(), bearer);

        using var response = await harness.SendAsync(
            HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer);

        var status = await TransitHarness.JsonAsync(response);

        Assert.Equal("validated", status.GetProperty("status").GetString());
        Assert.Equal("2026-07-01", status.GetProperty("feedInfoVersion").GetString());

        // The preview card SCR-AP-016 renders: counts keyed by GTFS file name, the feed_info
        // version and the service window read out of calendar.txt.
        var counts = status.GetProperty("counts");

        Assert.Equal(1, counts.GetProperty("routes").GetInt64());
        Assert.Equal(5, counts.GetProperty("stops").GetInt64());
        Assert.Equal(2, counts.GetProperty("trips").GetInt64());
        Assert.Equal(10, counts.GetProperty("stop_times").GetInt64());
        Assert.Equal(10, counts.GetProperty("shapes").GetInt64());

        Assert.Equal("2026-01-01", status.GetProperty("serviceStart").GetString());
        Assert.Equal("2027-12-31", status.GetProperty("serviceEnd").GetString());

        Assert.Empty(status.GetProperty("errorSummary").EnumerateArray());

        // Not "no warnings": whatever feed an earlier test left active, this one drops ids from it,
        // and BR-32.1's stable-id warning is supposed to say so. What must not appear is a warning
        // about *this* feed — a short service window, a missing shapes.txt, a trip with no calls.
        Assert.All(
            status.GetProperty("warnings").EnumerateArray(),
            warning => Assert.Contains("ids should stay stable", warning.GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_feed_zipped_with_its_folder_is_still_read()
    {
        // "Zip the GTFS folder" and "zip its contents" are different archives and the feed is
        // somebody else's file (AL-56), so the one thing this must not do is refuse it for how it
        // was packed.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid().InFolder("gtfs-sl-2026-07").Build(), bearer);

        var status = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer));

        Assert.Equal("validated", status.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_malformed_feed_fails_validation_and_produces_a_downloadable_row_level_report()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        // Two independent breakages, so the report is a report and not one message: a stop_times
        // row naming a halt that is not in stops.txt, and a trip on a route that does not exist.
        var zip = GtfsZipBuilder.Valid()
            .Append("stop_times.txt", "T138-1,07:10:00,07:10:00,NOWHERE,6")
            .Append("trips.txt", "R999,WEEKDAY,T999-1,Matara,0,")
            .Build();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(zip, bearer);

        var status = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer));

        Assert.Equal("failed", status.GetProperty("status").GetString());

        // The first-five banner, capped by the contract.
        var summary = status.GetProperty("errorSummary").EnumerateArray().ToArray();

        Assert.NotEmpty(summary);
        Assert.True(summary.Length <= 5);

        var report = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report", bearer));

        var errors = report.GetProperty("errors").EnumerateArray().ToArray();

        Assert.Contains(errors, error => error.GetProperty("code").GetString() == "unknown_stop_id");
        Assert.Contains(errors, error => error.GetProperty("code").GetString() == "unknown_route_id");

        // Row-level: every finding names the file and the line an operator opens to fix it.
        foreach (var error in errors)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("file").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
        }

        Assert.Contains(errors, error => error.TryGetProperty("row", out var row) && row.GetInt64() > 0);

        // …and nothing is activated. BR-32.3: a `failed` version is kept for its report and can
        // never go live.
        using var activate = await harness.SendAsync(
            HttpMethod.Post, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/activate", bearer);

        Assert.Equal(HttpStatusCode.Conflict, activate.StatusCode);

        var problem = await TransitHarness.ProblemAsync(activate);

        Assert.EndsWith("feed-not-validated", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_downloads_as_csv_as_well_as_json()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var zip = GtfsZipBuilder.Valid()
            .Append("stop_times.txt", "T138-1,07:10:00,07:10:00,NOWHERE,6")
            .Build();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(zip, bearer);

        using var response = await harness.SendAsync(
            HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report?format=csv", bearer);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var csv = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("severity,file,row,code,message", csv, StringComparison.Ordinal);
        Assert.Contains("unknown_stop_id", csv, StringComparison.Ordinal);
        Assert.Contains("stop_times.txt", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_required_file_fails_before_anything_else_is_reported()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid().Without("stops.txt").Build(), bearer);

        var report = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report", bearer));

        var errors = report.GetProperty("errors").EnumerateArray().ToArray();

        Assert.Contains(errors, error => error.GetProperty("code").GetString() == "missing_file");

        // One finding, not five hundred: without stops.txt every stop_times row would also be an
        // "unknown_stop_id", and the one error that explains the feed would be buried.
        Assert.Single(errors);
    }

    [Fact]
    public async Task A_calendar_is_required_in_one_form_or_the_other()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid().Without("calendar.txt").Build(), bearer);

        var report = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report", bearer));

        Assert.Contains(
            report.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "missing_calendar");
    }

    [Fact]
    public async Task A_feed_whose_whole_calendar_is_exceptions_is_accepted()
    {
        // GTFS lets a service be defined entirely by calendar_dates.txt, and treating that file as
        // override-only would report every service_id in such a feed as an unknown reference.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var zip = GtfsZipBuilder.Valid()
            .Without("calendar.txt")
            .With(
                "calendar_dates.txt",
                """
                service_id,date,exception_type
                WEEKDAY,20270601,1
                WEEKDAY,20270602,1
                """)
            .Build();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(zip, bearer);

        var status = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}", bearer));

        Assert.Equal("validated", status.GetProperty("status").GetString());
        Assert.Equal("2027-06-01", status.GetProperty("serviceStart").GetString());
    }

    [Fact]
    public async Task A_stop_outside_Sri_Lanka_fails_the_feed()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        // Chennai. Inside no bbox this platform operates in (BR-32.1: 5.7–10.0 °N, 79.4–82.1 °E).
        var zip = GtfsZipBuilder.Valid()
            .Append("stops.txt", "CHN,Chennai Central,13.0827,80.2707")
            .Build();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(zip, bearer);

        var report = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report", bearer));

        Assert.Contains(
            report.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "outside_service_area");
    }

    [Fact]
    public async Task An_expired_service_window_fails_and_a_short_one_only_warns()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var expired = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid()
                .With(
                    "calendar.txt",
                    """
                    service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
                    WEEKDAY,1,1,1,1,1,0,0,20200101,20200601
                    """)
                .Build(),
            bearer,
            "expired.zip");

        var expiredStatus = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{expired:D}", bearer));

        Assert.Equal("failed", expiredStatus.GetProperty("status").GetString());

        // A window that ends soon is BR-32.1's other half: warnings alone never block activation.
        var soon = DateTime.UtcNow.AddDays(10).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        var shortWindow = await harness.UploadAndAwaitVerdictAsync(
            GtfsZipBuilder.Valid()
                .With(
                    "calendar.txt",
                    $"""
                    service_id,monday,tuesday,wednesday,thursday,friday,saturday,sunday,start_date,end_date
                    WEEKDAY,1,1,1,1,1,0,0,20260101,{soon}
                    """)
                .Build(),
            bearer,
            "short.zip");

        var shortStatus = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{shortWindow:D}", bearer));

        Assert.Equal("validated", shortStatus.GetProperty("status").GetString());
        Assert.NotEmpty(shortStatus.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task Re_uploading_the_identical_file_returns_409_with_the_existing_version()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var zip = GtfsZipBuilder.Valid("2026-11-01").Build();

        var first = await harness.UploadAndAwaitVerdictAsync(zip, bearer);

        // A different Idempotency-Key and a different filename: BR-32.1 dedupes on the *bytes*, so
        // a retry that regenerated its header key is still one feed, and so is the same file
        // uploaded a month later by a different operator.
        using var second = await harness.UploadAsync(zip, bearer, "gtfs-again.zip");

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await TransitHarness.ProblemAsync(second);

        Assert.EndsWith("feed-duplicate", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);
        Assert.Equal(first, problem.GetProperty("feedVersionId").GetGuid());
        Assert.Equal("validated", problem.GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_upload_past_the_ceiling_is_413_whether_or_not_it_declares_its_length()
    {
        // The spec ceiling is 200 MB; asserting it at 200 MB would move a gigabyte through the
        // suite to learn nothing the limit itself does not say. The option is lowered instead, so
        // what is under test is the guard rather than the number.
        await using var harness = await TransitHarness.StartAsync(
            postgres,
            new Dictionary<string, string?> { ["Transit:Gtfs:MaxUploadBytes"] = "1048576" });

        var (_, bearer) = await harness.AdminAsync();

        // Past the declared-length guard (limit + the 1 MiB multipart allowance).
        using var declared = await harness.UploadAsync(new byte[3 * 1024 * 1024], bearer, "huge.zip");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode);

        // Inside the envelope allowance but past the file ceiling: caught while streaming, which
        // is the guard a chunked upload meets.
        using var streamed = await harness.UploadAsync(new byte[1024 * 1536], bearer, "big.zip");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, streamed.StatusCode);
    }

    [Fact]
    public async Task Something_that_is_not_a_zip_fails_validation_rather_than_the_request()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        var feedVersionId = await harness.UploadAndAwaitVerdictAsync(
            Encoding.UTF8.GetBytes("this is not a zip, it is a sentence"), bearer, "notes.txt");

        var report = await TransitHarness.JsonAsync(
            await harness.SendAsync(HttpMethod.Get, $"/v1/admin/transit/gtfs/uploads/{feedVersionId:D}/report", bearer));

        Assert.Contains(
            report.GetProperty("errors").EnumerateArray(),
            error => error.GetProperty("code").GetString() == "not_a_zip");
    }

    [Fact]
    public async Task The_upload_still_demands_an_idempotency_key()
    {
        // The kernel's replay is off on this route — the body is up to 200 MB — but the contract
        // makes the header required, and a client must not be able to tell the two apart by what
        // they accept.
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();

        using var content = new MultipartFormDataContent();
        using var file = new ByteArrayContent(GtfsZipBuilder.Valid().Build());

        content.Add(file, "file", "gtfs.zip");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/admin/transit/gtfs/uploads")
        {
            Content = content,
        };

        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);

        using var response = await harness.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await TransitHarness.ProblemAsync(response);

        Assert.EndsWith("idempotency-key-required", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_Admin_and_Super_Admin_reach_the_dataset_manager()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var zip = GtfsZipBuilder.Valid().Build();

        // AL-06 deny-by-default. D2 SCR-AP-016: Verification / Support / Finance / Auditor see no
        // Transit-data nav entry at all, and the Auditor reads history through the audit log.
        using (var anonymous = await harness.UploadAsync(zip, bearer: null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var passenger = await harness.UploadAsync(zip, harness.Tokens.Passenger()))
        {
            Assert.Equal(HttpStatusCode.Forbidden, passenger.StatusCode);
        }

        foreach (var role in new[]
                 {
                     MageRideRoles.VerificationOfficer,
                     MageRideRoles.SupportCsr,
                     MageRideRoles.FinanceOfficer,
                     MageRideRoles.Auditor,
                 })
        {
            var userId = await harness.Seed.CreateUserAsync(role);

            using var refused = await harness.UploadAsync(zip, harness.Tokens.Internal(userId, role));

            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            using var listed = await harness.SendAsync(
                HttpMethod.Get, "/v1/admin/transit/gtfs/versions", harness.Tokens.Internal(userId, role));

            Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
        }

        var superAdminId = await harness.Seed.CreateUserAsync(MageRideRoles.SuperAdmin);

        using var allowed = await harness.UploadAsync(
            zip, harness.Tokens.Internal(superAdminId, MageRideRoles.SuperAdmin));

        Assert.Equal(HttpStatusCode.Accepted, allowed.StatusCode);
    }

    [Fact]
    public async Task An_unknown_feed_version_is_404_on_every_read()
    {
        await using var harness = await TransitHarness.StartAsync(postgres);

        var (_, bearer) = await harness.AdminAsync();
        var unknown = Guid.NewGuid();

        foreach (var path in new[]
                 {
                     $"/v1/admin/transit/gtfs/uploads/{unknown:D}",
                     $"/v1/admin/transit/gtfs/uploads/{unknown:D}/report",
                     $"/v1/admin/transit/gtfs/versions/{unknown:D}/download",
                 })
        {
            using var response = await harness.SendAsync(HttpMethod.Get, path, bearer);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }
}
