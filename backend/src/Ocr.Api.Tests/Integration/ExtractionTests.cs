using System.Net;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Endpoints;
using MageRide.Ocr.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ocr.Tests.Integration;

/// <summary>
/// The Mode-C verdict this service is responsible for (D6' §7.5, D5' §14.1a, AL-29).
/// </summary>
[Collection(OcrCollection.Name)]
public sealed class ExtractionTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_clean_read_auto_verifies_every_field_the_verdict_table_needs()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.True(result.Succeeded);
        Assert.Equal(ExtractionEngines.Gemini, result.Engine);
        Assert.Equal(DocumentFixtures.InsuranceExpiry, Value(result, DocumentFieldKeys.InsuranceExpiry));
        Assert.All(result.Fields, field => Assert.Equal(VerifyStatuses.AutoVerified, field.VerifyStatus));
        Assert.All(result.Fields, field => Assert.Equal(FieldSources.Ai, field.Source));

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(ExtractionStatuses.Extracted, row!.Status);
    }

    [Fact]
    public async Task A_low_confidence_field_is_pending_and_never_auto_verified()
    {
        // Definition of done #1.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer(
            (DocumentFieldKeys.InsuranceExpiry, "2027-03-31", 0.42m),
            (DocumentFieldKeys.Insurer, "Ceylinco", 0.97m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        var expiry = Field(result, DocumentFieldKeys.InsuranceExpiry);

        Assert.Equal(VerifyStatuses.Pending, expiry.VerifyStatus);
        // The value is still returned — a doubtful read is something an officer confirms or edits
        // (BR-25.2), not something to withhold from them.
        Assert.Equal("2027-03-31", expiry.Value);

        // Its confident neighbour is unaffected: the verdict is per field, not per document.
        Assert.Equal(VerifyStatuses.AutoVerified, Field(result, DocumentFieldKeys.Insurer).VerifyStatus);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(ExtractionStatuses.ManualReview, row!.Status);
        Assert.Equal(0.42m, row.Confidence);
    }

    [Fact]
    public async Task A_field_with_no_confidence_at_all_is_treated_exactly_like_a_doubtful_one()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer((DocumentFieldKeys.InsuranceExpiry, "2027-03-31", null));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.Equal(VerifyStatuses.Pending, Field(result, DocumentFieldKeys.InsuranceExpiry).VerifyStatus);
    }

    [Fact]
    public async Task A_required_field_that_could_not_be_read_comes_back_as_a_row_to_fill()
    {
        // C029's rule (3): the officer queue shows "the insurance expiry could not be read" rather
        // than an absence somebody has to notice.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer((DocumentFieldKeys.Insurer, "Ceylinco", 0.98m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        var expiry = Field(result, DocumentFieldKeys.InsuranceExpiry);

        Assert.Null(expiry.Value);
        Assert.Null(expiry.Confidence);
        Assert.Equal(VerifyStatuses.Pending, expiry.VerifyStatus);
        Assert.Equal(FieldSources.Ai, expiry.Source);
    }

    [Fact]
    public async Task A_plate_that_does_not_match_the_registration_is_pending()
    {
        // Definition of done #2, on this service's side of the seam: registry-svc turns a pending
        // reg_no_match into the photos step's pending_review (AL-30).
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer((DocumentFieldKeys.PlateText, "WP-QA-9999", 0.99m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.VehiclePhoto("WP-QA-9999"), DocumentKinds.Registration);

        var result = await harness.ExtractAsync(
            uploadId, storageUrl, DocumentKinds.Registration, registrationNumber: DocumentFixtures.Plate);

        var match = Field(result, DocumentFieldKeys.RegNoMatch);

        Assert.Equal("false", match.Value);
        Assert.Equal(VerifyStatuses.Pending, match.VerifyStatus);

        // What it read is kept beside the verdict, so the officer sees the two plates side by side
        // and a corrected registration can be re-judged against it (C029's decision 6).
        Assert.Equal("WP-QA-9999", Value(result, DocumentFieldKeys.PlateText));
    }

    [Fact]
    public async Task A_plate_written_differently_is_the_same_plate()
    {
        // The comparison is on the alphanumerics: separators are a writing convention, and refusing
        // a vehicle over a hyphen would send every correct onboarding to an officer.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer((DocumentFieldKeys.PlateText, "WP QA 1234", 0.97m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.VehiclePhoto(), DocumentKinds.Registration);

        var result = await harness.ExtractAsync(
            uploadId, storageUrl, DocumentKinds.Registration, registrationNumber: "wp-qa-1234");

        var match = Field(result, DocumentFieldKeys.RegNoMatch);

        Assert.Equal("true", match.Value);
        Assert.Equal(VerifyStatuses.AutoVerified, match.VerifyStatus);
    }

    [Fact]
    public async Task A_photos_request_with_no_registration_to_compare_against_cannot_verify()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.VehiclePhoto(), DocumentKinds.Registration);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Registration);

        var match = Field(result, DocumentFieldKeys.RegNoMatch);

        Assert.Null(match.Value);
        Assert.Equal(VerifyStatuses.Pending, match.VerifyStatus);
    }

    [Fact]
    public async Task The_licence_returns_the_four_fields_AL_29_expanded_it_to()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (frontId, frontUrl, _) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceFront(), DocumentKinds.DrivingLicense);

        var (backId, backUrl, _) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceBack(), DocumentKinds.DrivingLicense);

        var front = await harness.ExtractAsync(
            frontId, frontUrl, DocumentKinds.DrivingLicense, DocumentSides.Front);

        var back = await harness.ExtractAsync(
            backId, backUrl, DocumentKinds.DrivingLicense, DocumentSides.Back);

        Assert.Equal(DocumentFixtures.LicenceNumber, Value(front, DocumentFieldKeys.LicenceNo));
        Assert.Contains(front.Fields, field => field.Key == DocumentFieldKeys.NicNo);
        Assert.Equal("A1,B,C1", Value(back, DocumentFieldKeys.AllowedVehicleTypes));

        // Δ MCS-20 — the expiry is a BACK field now, and the front must not carry it at all.
        //
        // `4a` on the front is the date of ISSUE; the expiry is column 11 of the class table on the
        // reverse. Asking the front for "the date of expiry" is what returned the issue date, and
        // that value becomes `registry.documents.expires_at` and the input to E-03's sweep — so a
        // test that accepted it on the front was pinning the defect.
        Assert.Equal(DocumentFixtures.LicenceExpiry, Value(back, DocumentFieldKeys.LicenceExpiry));
        Assert.DoesNotContain(front.Fields, field => field.Key == DocumentFieldKeys.LicenceExpiry);

        // The NIC was masked out of the image, so the model answered null — and that must not hold
        // the licence step down, because it is not a required field (I-25.1).
        Assert.Null(Value(front, DocumentFieldKeys.NicNo));
        Assert.Equal(
            VerifyStatuses.AutoVerified, Field(front, DocumentFieldKeys.LicenceNo).VerifyStatus);
    }

    [Fact]
    public async Task A_fleet_route_permit_reuses_the_same_pipeline()
    {
        // AL-50: the four Fleet-Portal slots route to the same extractors. No spec names the permit
        // field keys, so they are this service's — raised in the C054 handoff.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.RoutePermit(), DocumentKinds.Permit);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Permit);

        Assert.True(result.Succeeded);
        Assert.Equal(DocumentFixtures.PermitNumber, Value(result, DocumentFieldKeys.PermitNo));
        Assert.Equal("2027-12-31", Value(result, DocumentFieldKeys.PermitExpiry));

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(DocumentKinds.Permit, row!.DocType);
    }

    [Fact]
    public async Task The_revenue_licence_needs_its_number_and_its_expiry()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.RevenueLicence(), DocumentKinds.RevenueLicense);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.RevenueLicense);

        Assert.Equal(DocumentFixtures.RevenueNumber, Value(result, DocumentFieldKeys.RevenueNo));
        Assert.Equal(DocumentFixtures.RevenueExpiry, Value(result, DocumentFieldKeys.RevenueExpiry));
    }

    [Fact]
    public async Task A_key_nobody_asked_for_is_dropped_rather_than_stored()
    {
        // `registry.document_fields.field_key` is free text, so an invented key would reach the
        // officer queue as a row about a field the wizard has no screen for.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer(
            (DocumentFieldKeys.InsuranceExpiry, "2027-03-31", 0.95m),
            ("chassis_number", "JT1234567", 0.99m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.DoesNotContain(result.Fields, field => field.Key == "chassis_number");
    }

    [Fact]
    public async Task The_model_does_not_get_to_decide_the_plate_comparison()
    {
        // reg_no_match is a comparison against a value only this service holds. A model that
        // returned it would be answering a question it was never given the other half of.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Answer(
            (DocumentFieldKeys.PlateText, "WP-QA-9999", 0.99m),
            (DocumentFieldKeys.RegNoMatch, "true", 0.99m));

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.VehiclePhoto("WP-QA-9999"), DocumentKinds.Registration);

        var result = await harness.ExtractAsync(
            uploadId, storageUrl, DocumentKinds.Registration, registrationNumber: DocumentFixtures.Plate);

        Assert.Equal("false", Value(result, DocumentFieldKeys.RegNoMatch));
    }

    [Fact]
    public async Task One_extraction_row_is_written_per_pass()
    {
        // D6' §7.5: "One docs.extractions row per doc". A re-upload gets its own row rather than
        // overwriting — the failed attempt is the audit trail behind a pending_review.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);
        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.Equal(2, await harness.ExtractionCountAsync(uploadId));
    }

    [Fact]
    public async Task An_upload_that_arrived_with_no_deletion_deadline_is_given_one()
    {
        // NFR-28. Nothing else on the platform writes docs.uploads for onboarding yet (C125), and a
        // licence photograph with no deadline is one kept for ever by omission.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        Assert.Null(await harness.AutoDeleteAtAsync(uploadId));

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        var deadline = await harness.AutoDeleteAtAsync(uploadId);

        Assert.NotNull(deadline);
        Assert.InRange(
            deadline.Value, DateTimeOffset.UtcNow.AddDays(89), DateTimeOffset.UtcNow.AddDays(91));
    }

    [Fact]
    public async Task A_deadline_somebody_else_set_is_left_alone()
    {
        // Moving it would be this service quietly extending another's retention promise.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var chosen = DateTimeOffset.UtcNow.AddDays(7);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance, chosen);

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        var deadline = await harness.AutoDeleteAtAsync(uploadId);

        Assert.Equal(chosen.ToUnixTimeSeconds(), deadline!.Value.ToUnixTimeSeconds());
    }

    private static ExtractedFieldBody Field(ExtractionResponse result, string key) =>
        Assert.Single(result.Fields, field => field.Key == key);

    private static string? Value(ExtractionResponse result, string key) => Field(result, key).Value;
}

/// <summary>The route itself: what it refuses, and what it never turns into an error.</summary>
[Collection(OcrCollection.Name)]
public sealed class ExtractionEndpointTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Without_the_internal_key_the_route_is_not_mapped_at_all()
    {
        await using var harness = await OcrHarness.StartAsync(postgres, withInternalPlane: false);

        using var response = await harness.ExtractAsync(
            new { uploadId = Guid.NewGuid(), storageUrl = "x.png", kind = DocumentKinds.Insurance }, apiKey: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_key_is_a_404_not_a_403()
    {
        // Matching what the gateway returns for the /v1/internal prefix: a caller not entitled to
        // the internal plane should not be able to map it.
        await using var harness = await OcrHarness.StartAsync(postgres);

        using var response = await harness.ExtractAsync(
            new { uploadId = Guid.NewGuid(), storageUrl = "x.png", kind = DocumentKinds.Insurance },
            apiKey: "not-the-key");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_route_on_this_service_is_health_or_key_gated()
    {
        // The service drops the kernel's deny-by-default fallback policy (see OcrApplication for
        // why an unauthenticated plane cannot satisfy it), so this is what replaces it: nothing may
        // be mapped here except the probes and the key-gated internal group. A route added outside
        // both would be reachable by anybody who can route to the pod.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var routes = harness.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
            .ToArray();

        Assert.NotEmpty(routes);
        Assert.All(routes, route => Assert.True(
            route.StartsWith("/v1/internal/ocr/", StringComparison.Ordinal)
                || route.StartsWith("/health", StringComparison.Ordinal)
                || route.StartsWith("/metrics", StringComparison.Ordinal),
            $"{route} is neither a probe nor behind Ocr:InternalApiKey."));
    }

    [Fact]
    public async Task A_kind_with_no_extractor_behind_it_is_refused()
    {
        // It would otherwise come back with no fields and read as a document nobody could make out,
        // rather than as the mistake it is.
        await using var harness = await OcrHarness.StartAsync(postgres);

        using var response = await harness.ExtractAsync(
            new { uploadId = Guid.NewGuid(), storageUrl = "x.png", kind = "bank_statement" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_that_does_not_exist_is_an_unread_document_not_an_error()
    {
        // The caller has an onboarding step to save either way (D5' §14.1a); a 5xx would put
        // registry-svc's retry between a driver and their next screen.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var result = await harness.ExtractAsync(
            Guid.NewGuid(), "missing.png", DocumentKinds.Insurance);

        Assert.False(result.Succeeded);
        Assert.Equal(ExtractionEngines.None, result.Engine);
        Assert.Empty(harness.Gemini.Calls);
    }

    [Fact]
    public async Task A_storage_url_that_climbs_out_of_the_root_is_refused()
    {
        // storage_url is a value from another service's table. A service that follows it anywhere is
        // one row away from reading the cluster's metadata endpoint.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, _, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        await harness.ExecuteAsync(
            "UPDATE docs.uploads SET storage_url = '../../../etc/hostname' WHERE id = @UploadId;",
            new { UploadId = uploadId });

        var result = await harness.ExtractAsync(uploadId, "../../../etc/hostname", DocumentKinds.Insurance);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Gemini.Calls);
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_produce_a_failed_row_and_no_outbound_call()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.NotAnImage(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Gemini.Calls);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(ExtractionStatuses.Failed, row!.Status);
        Assert.Equal(ExtractionEngines.None, row.Engine);
    }
}
