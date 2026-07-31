using System.Net;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ocr.Tests.Integration;

/// <summary>
/// <b>Definition of done: "with Gemini unavailable the Tesseract path still produces fields
/// (flagged for review)."</b>
/// </summary>
/// <remarks>
/// D6' §8.3's degraded mode — "OCR down → Tesseract" — and this component's second fence: Gemini
/// being down must not stop onboarding, only auto-approval. The engine here is the real
/// <c>tesseract</c> binary reading a real rendered document; only the model is stubbed, and only to
/// make it fail.
/// </remarks>
[Collection(OcrCollection.Name)]
public sealed class FallbackTests(PostgresFixture postgres)
{
    [Fact]
    public async Task With_Gemini_unavailable_the_on_prem_path_still_produces_fields()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Fail();

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.RevenueLicence(), DocumentKinds.RevenueLicense);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.RevenueLicense);

        Assert.True(result.Succeeded);
        Assert.Equal(ExtractionEngines.Tesseract, result.Engine);

        Assert.Equal(
            DocumentFixtures.RevenueNumber,
            result.Fields.Single(field => field.Key == DocumentFieldKeys.RevenueNo).Value);

        Assert.Equal(
            DocumentFixtures.RevenueExpiry,
            result.Fields.Single(field => field.Key == DocumentFieldKeys.RevenueExpiry).Value);
    }

    [Fact]
    public async Task Every_field_the_fallback_produced_is_flagged_for_review()
    {
        // The other half of the definition of done. Nothing on this path may auto-verify: AL-27
        // approves a vehicle with no human involvement on these fields, and "a date near a label"
        // is not a basis for that. The ceiling is what makes it structural rather than lucky.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Fail();

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.RevenueLicence(), DocumentKinds.RevenueLicense);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.RevenueLicense);

        Assert.NotEmpty(result.Fields);
        Assert.All(result.Fields, field => Assert.Equal(VerifyStatuses.Pending, field.VerifyStatus));

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(ExtractionStatuses.ManualReview, row!.Status);
        Assert.Equal(ExtractionEngines.Tesseract, row.Engine);

        // The document WAS redacted — the pre-pass runs before the model is even attempted — but
        // nothing left the perimeter on this path, so there is no redacted artefact to name.
        Assert.NotNull(row.RawSha256);
    }

    [Fact]
    public async Task The_plate_still_reads_and_the_comparison_still_happens_without_Gemini()
    {
        // A Gemini outage must not turn every photos step into "we could not tell", because that is
        // the step a vehicle cannot be approved without.
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Fail();

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.VehiclePhoto(), DocumentKinds.Registration);

        var result = await harness.ExtractAsync(
            uploadId, storageUrl, DocumentKinds.Registration, registrationNumber: DocumentFixtures.Plate);

        Assert.Equal(DocumentFixtures.Plate, result.Fields
            .Single(field => field.Key == DocumentFieldKeys.PlateText).Value);

        var match = result.Fields.Single(field => field.Key == DocumentFieldKeys.RegNoMatch);

        // It matched — and it is still pending, because the read behind it came off the capped
        // path. An officer confirms it; nothing auto-approves on it.
        Assert.Equal("true", match.Value);
        Assert.Equal(VerifyStatuses.Pending, match.VerifyStatus);
    }

    [Fact]
    public async Task A_model_that_answers_nonsense_falls_back_rather_than_failing_the_document()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        harness.Gemini.Responder = _ => (HttpStatusCode.OK, "{\"candidates\":[]}");

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.RevenueLicence(), DocumentKinds.RevenueLicense);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.RevenueLicense);

        Assert.Equal(ExtractionEngines.Tesseract, result.Engine);
        Assert.Contains(result.Fields, field => field.Value is not null);
    }

    [Fact]
    public async Task With_Gemini_unconfigured_every_document_takes_the_on_prem_path()
    {
        // A deployment with no API key is not broken — it is one with no AL-27 auto-approval, and
        // the service says so at start-up.
        await using var harness = await OcrHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ocr:Gemini:ApiKey"] = null,
        });

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.Empty(harness.Gemini.Calls);
        Assert.Equal(ExtractionEngines.Tesseract, result.Engine);
        Assert.All(result.Fields, field => Assert.Equal(VerifyStatuses.Pending, field.VerifyStatus));
    }

    [Fact]
    public async Task A_deployment_that_cannot_read_at_all_still_answers_the_caller()
    {
        // Both engines gone. The caller still gets every required key as a pending row, saves its
        // step and sends the driver to a Verification Officer — which is what D5' §14.1a does with
        // a document that did not extract.
        await using var harness = await OcrHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ocr:Tesseract:ExecutablePath"] = "/nonexistent/tesseract",
            ["Ocr:Gemini:ApiKey"] = null,
        });

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.False(result.Succeeded);
        Assert.Empty(harness.Gemini.Calls);

        var expiry = result.Fields.Single(field => field.Key == DocumentFieldKeys.InsuranceExpiry);

        Assert.Null(expiry.Value);
        Assert.Equal(VerifyStatuses.Pending, expiry.VerifyStatus);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(ExtractionStatuses.Failed, row!.Status);
        Assert.False(row.RedactionApplied);
    }

    [Fact]
    public async Task A_ceiling_at_or_above_the_threshold_is_refused_at_start_up()
    {
        // Above it, a Gemini outage would auto-verify fields the fallback found by keyword match —
        // and AL-27 approves a vehicle on those fields with no human involvement at all.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => OcrHarness.StartAsync(
            postgres,
            new Dictionary<string, string?>
            {
                ["Ocr:ConfidenceThreshold"] = "0.80",
                ["Ocr:TesseractConfidenceCeiling"] = "0.90",
            }));

        Assert.Contains("TesseractConfidenceCeiling", failure.Message, StringComparison.Ordinal);
    }
}
