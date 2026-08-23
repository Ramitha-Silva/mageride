using System.Net;
using Dapper;
using MageRide.Registry.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Registry.Tests.Integration;

/// <summary>
/// <b>Δ MCS-01</b> — the multipart arms of Profile Setup and the Mode-C wizard, and the
/// <c>docs.uploads</c> writer behind them.
/// </summary>
/// <remarks>
/// <para>
/// Before this, <c>docs.uploads</c> had **no writer for any onboarding document**: the only
/// <c>INSERT</c> in this service was the AL-58 payout store, and this suite's own
/// <c>SeedUploadAsync</c> filled the table by hand "as the upload surface would". Both screens
/// therefore required three ids nothing on the platform could mint, which made them impossible to
/// complete against a real gateway on either app. These tests are what stops that returning.
/// </para>
/// <para>
/// The bytes go to the kernel's <c>IObjectStore</c>, which falls back to a filesystem root when
/// <c>Storage:*</c> is unset — so nothing here needs MinIO. What is asserted is the row: who owns
/// it, where the object is, how it was captured (AL-43) and when it dies (NFR-28).
/// </para>
/// </remarks>
[Collection<PostgresCollection>]
public sealed class OnboardingUploadTests(PostgresFixture postgres)
{
    /// <summary>The DoD line: Profile Setup completes with nothing pre-seeded.</summary>
    [Fact]
    public async Task Profile_setup_accepts_the_images_themselves_and_mints_their_uploads()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nimal Perera"), "driverName" },
        };

        RegistryHarness.AddImagePart(form, "photo", "gallery");
        RegistryHarness.AddImagePart(form, "licenseFront");
        RegistryHarness.AddImagePart(form, "licenseBack");

        var response = await harness.PutMultipartAsync(
            "/v1/drivers/profile", form, harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("Nimal Perera", body.GetProperty("displayName").GetString());

        await using var connection = await harness.OpenAsync();

        // Three uploads, all owned by the caller. Ownership is what `RequireUploadAsync` checks,
        // and it is why a driver cannot attach somebody else's licence to their own profile.
        Assert.Equal(
            3,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM docs.uploads WHERE owner_id = @DriverId;",
                new { DriverId = driverId }));

        // AL-27: the licence is captured at Profile Setup, which precedes any vehicle.
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM registry.documents
                 WHERE driver_id = @DriverId AND kind = 'driving_license' AND vehicle_id IS NULL;
                """,
                new { DriverId = driverId }));
    }

    /// <summary>AL-43: the officer queue can only sort on the signal if the signal is true.</summary>
    [Fact]
    public async Task Each_image_records_how_it_was_captured_rather_than_one_value_for_the_request()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nimal Perera"), "driverName" },
        };

        // Exactly what the Driver App does: the avatar comes out of the photo picker and the
        // licence through the SCR-DA-005 scanner, in one submission.
        RegistryHarness.AddImagePart(form, "photo", "gallery");
        RegistryHarness.AddImagePart(form, "licenseFront", "camera_dragcrop");
        RegistryHarness.AddImagePart(form, "licenseBack", "camera_dragcrop");

        var response = await harness.PutMultipartAsync(
            "/v1/drivers/profile", form, harness.Tokens.Driver(driverId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = await harness.OpenAsync();

        var rows = (await connection.QueryAsync<(string Kind, string CapturedVia, DateTimeOffset? AutoDeleteAt)>(
            """
            SELECT kind AS "Kind", captured_via AS "CapturedVia", auto_delete_at AS "AutoDeleteAt"
              FROM docs.uploads WHERE owner_id = @DriverId ORDER BY kind, captured_via;
            """,
            new { DriverId = driverId })).ToList();

        Assert.Equal(3, rows.Count);

        // A single `capturedVia` for the whole form would have recorded a scan that did not happen
        // on the avatar, or flagged two licence images that were scanned properly.
        Assert.Equal(2, rows.Count(row => row.CapturedVia == "camera_dragcrop"));
        Assert.Equal(1, rows.Count(row => row.CapturedVia == "gallery"));
        Assert.Contains(rows, row => row.Kind == "profile_photo" && row.CapturedVia == "gallery");

        // NFR-28: the licence images are raw identity evidence and carry a deadline.
        Assert.All(
            rows.Where(row => row.Kind != "profile_photo"),
            row => Assert.NotNull(row.AutoDeleteAt));

        // Δ MCS-25 — and the profile photo does NOT, because it is the one document here the
        // platform keeps *serving*. It is the avatar SCR-DA/DI-029 and -036 draw and the face
        // US-2.12 has a passenger recognise their driver by, so a deadline on it turns a working
        // screen into a broken one ninety days after Profile Setup with nothing to re-upload from.
        // Same exception, same mechanism, as the LankaQR in `DriverPayoutProfileTests`.
        Assert.All(
            rows.Where(row => row.Kind == "profile_photo"),
            row => Assert.Null(row.AutoDeleteAt));
    }

    /// <summary>
    /// The capture source is never defaulted — see <c>OnboardingDocumentStore</c>'s remarks.
    /// </summary>
    [Fact]
    public async Task An_upload_that_does_not_say_how_it_was_captured_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var image = new ByteArrayContent([0xFF, 0xD8]);
        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nimal Perera"), "driverName" },
            { image, "photo", "photo.jpg" },
        };

        var response = await harness.PutMultipartAsync(
            "/v1/drivers/profile", form, harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Nothing was written: a refusal must not leave a row behind that a later request could
        // reference.
        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM docs.uploads WHERE owner_id = @DriverId;",
                new { DriverId = driverId }));
    }

    /// <summary>The other half of the DoD: a wizard step takes its image with its fields.</summary>
    [Fact]
    public async Task A_vehicle_step_accepts_its_image_in_the_same_request_as_its_fields()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using var form = new MultipartFormDataContent();
        RegistryHarness.AddImagePart(form, "file");

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/insurance", form, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);
        Assert.Equal("revenue", body.GetProperty("nextStep").GetString());

        await using var connection = await harness.OpenAsync();

        Assert.Equal(
            1,
            await connection.ExecuteScalarAsync<int>(
                """
                SELECT count(*) FROM docs.uploads
                 WHERE owner_id = @DriverId AND kind = 'insurance' AND captured_via = 'camera_dragcrop';
                """,
                new { DriverId = driverId }));
    }

    /// <summary>
    /// Δ MCS-02 — the DoD line: a driver corrects a doubtful extracted value **without
    /// re-photographing the document**, and the correction routes to the officer queue.
    /// </summary>
    [Fact]
    public async Task A_doubtful_field_is_corrected_without_a_second_upload()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using (var upload = new MultipartFormDataContent())
        {
            RegistryHarness.AddImagePart(upload, "file");
            var saved = await harness.PutMultipartAsync(
                $"/v1/vehicles/{vehicleId}/onboarding/insurance", upload, bearer);

            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        await using var connection = await harness.OpenAsync();

        var uploadsBefore = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM docs.uploads WHERE owner_id = @DriverId;", new { DriverId = driverId });

        // The correction, with no file part at all.
        using var correction = new MultipartFormDataContent
        {
            { new StringContent("2027-03-31"), "insuranceExpiry" },
        };

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/insurance", correction, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await RegistryHarness.ReadJsonAsync(response);

        // BR-25.3: a driver-entered value is pending BY DESIGN. The step goes back to review and
        // the driver may still carry on — that is the rule, not a failure.
        Assert.Equal("PENDING_REVIEW", body.GetProperty("stepStatus").GetString());

        // Nothing was uploaded. This is the whole point: re-photographing a certificate to retype
        // its expiry is the roadside experience BR-25.3 exists to avoid.
        Assert.Equal(
            uploadsBefore,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM docs.uploads WHERE owner_id = @DriverId;", new { DriverId = driverId }));

        // One row for the key, carrying the driver's value with manual provenance (AL-29).
        var field = await connection.QuerySingleAsync<(string Value, string Source, string VerifyStatus)>(
            """
            SELECT f.field_value AS "Value", f.source AS "Source", f.verify_status AS "VerifyStatus"
              FROM registry.document_fields f
              JOIN registry.documents d ON d.id = f.document_id
             WHERE d.vehicle_id = @VehicleId AND f.field_key = 'insurance_expiry';
            """,
            new { VehicleId = Guid.Parse(vehicleId) });

        Assert.Equal("2027-03-31", field.Value);
        Assert.Equal("manual", field.Source);
        Assert.Equal("pending", field.VerifyStatus);
    }

    /// <summary>A correction needs something to correct; an empty step is a validation failure.</summary>
    [Fact]
    public async Task A_correction_on_a_step_with_no_document_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using var form = new MultipartFormDataContent
        {
            { new StringContent("2027-03-31"), "insuranceExpiry" },
        };

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/insurance", form, bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// A key the step's document kind does not accept is refused, not stored. Without the
    /// <c>AcceptedFor</c> filter a driver could write <c>reg_no_match</c> and verify their own plate.
    /// </summary>
    [Fact]
    public async Task A_correction_that_does_not_belong_to_the_step_is_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using (var upload = new MultipartFormDataContent())
        {
            RegistryHarness.AddImagePart(upload, "file");
            await harness.PutMultipartAsync($"/v1/vehicles/{vehicleId}/onboarding/insurance", upload, bearer);
        }

        // `revenue_no` belongs to the revenue licence, not to the insurance certificate.
        using var form = new MultipartFormDataContent
        {
            { new StringContent("RL-558231"), "revenueNo" },
        };

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/insurance", form, bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Δ MCS-02 — the C068 finding, closed: the licence number and its expiry are correctable.
    /// </summary>
    [Fact]
    public async Task Profile_setup_accepts_a_corrected_licence_number_and_expiry()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Nimal Perera"), "driverName" },
            { new StringContent("B1234567"), "licenceNo" },
            { new StringContent("2029-04-30"), "licenceExpiry" },
        };

        RegistryHarness.AddImagePart(form, "photo", "gallery");
        RegistryHarness.AddImagePart(form, "licenseFront");
        RegistryHarness.AddImagePart(form, "licenseBack");

        var response = await harness.PutMultipartAsync(
            "/v1/drivers/profile", form, harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = await harness.OpenAsync();

        var rows = await connection.QueryAsync<(string Key, string Value, string Source)>(
            """
            SELECT f.field_key AS "Key", f.field_value AS "Value", f.source AS "Source"
              FROM registry.document_fields f
              JOIN registry.documents d ON d.id = f.document_id
             WHERE d.driver_id = @DriverId AND f.field_key IN ('licence_no', 'licence_expiry');
            """,
            new { DriverId = driverId });

        var byKey = rows.ToDictionary(row => row.Key, StringComparer.Ordinal);

        Assert.Equal("B1234567", byKey["licence_no"].Value);
        Assert.Equal("manual", byKey["licence_no"].Source);
        Assert.Equal("2029-04-30", byKey["licence_expiry"].Value);
    }

    /// <summary>Step 4 needs both plates, and the multipart arm has to carry both (D5' §14.1a).</summary>
    [Fact]
    public async Task The_photos_step_carries_a_front_and_a_back_in_one_request()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using var form = new MultipartFormDataContent();
        RegistryHarness.AddImagePart(form, "file");
        RegistryHarness.AddImagePart(form, "fileBack");

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/photos", form, bearer);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var connection = await harness.OpenAsync();
        Assert.Equal(
            2,
            await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM docs.uploads WHERE owner_id = @DriverId AND kind = 'registration';",
                new { DriverId = driverId }));
    }

    /// <summary>A document step sent as a form with no file is told which part is missing.</summary>
    [Fact]
    public async Task A_document_step_with_no_file_part_says_so()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var bearer = harness.Tokens.Driver(driverId);
        var vehicleId = (await harness.RegisterVehicleAsync(bearer)).GetProperty("vehicleId").GetString()!;

        using var form = new MultipartFormDataContent
        {
            { new StringContent("camera_dragcrop"), "fileCapturedVia" },
        };

        var response = await harness.PutMultipartAsync(
            $"/v1/vehicles/{vehicleId}/onboarding/revenue", form, bearer);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The fence that must survive the new writer: an id is only usable by the driver who owns it.
    /// </summary>
    /// <remarks>
    /// Without it a driver could attach a stranger's licence and have its extracted number verify
    /// against their own profile. The multipart arm cannot express the attack — it mints uploads
    /// owned by the caller — but the JSON arm still takes ids, so the check still has to hold.
    /// </remarks>
    [Fact]
    public async Task An_upload_belonging_to_another_driver_is_still_refused()
    {
        Assert.SkipWhen(!postgres.IsAvailable, postgres.SkipReason ?? string.Empty);
        await using var harness = await RegistryHarness.StartAsync(postgres);

        var driverId = await harness.CreateDriverAsync();
        var strangerId = await harness.CreateDriverAsync();

        var response = await harness.PutAsync(
            "/v1/drivers/profile",
            new
            {
                driverName = "Nimal Perera",
                profilePhotoFileId = await harness.SeedUploadAsync(driverId, "profile_photo"),
                licenseFrontFileId = await harness.SeedUploadAsync(strangerId, "driving_license"),
                licenseBackFileId = await harness.SeedUploadAsync(driverId, "driving_license"),
            },
            harness.Tokens.Driver(driverId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
