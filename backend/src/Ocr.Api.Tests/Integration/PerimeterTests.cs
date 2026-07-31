using System.Security.Cryptography;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Redaction;
using MageRide.Ocr.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ocr.Tests.Integration;

/// <summary>
/// <b>Definition of done: "no unredacted image is sent to the external model in an integration test
/// with a network recorder."</b>
/// </summary>
/// <remarks>
/// Asserted on the bytes that came off the socket, not on a mock's arguments: the whole service is
/// running, the real redaction pass ran on a real image, and <see cref="GeminiRecorder"/> kept what
/// arrived.
/// </remarks>
[Collection(OcrCollection.Name)]
public sealed class PerimeterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task No_unredacted_image_reaches_the_external_model()
    {
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, raw) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceFront(), DocumentKinds.DrivingLicense);

        var result = await harness.ExtractAsync(
            uploadId, storageUrl, DocumentKinds.DrivingLicense, DocumentSides.Front);

        Assert.Equal(ExtractionEngines.Gemini, result.Engine);
        Assert.True(result.RedactionApplied);

        var call = Assert.Single(harness.Gemini.Calls);
        var rawSha = Convert.ToHexStringLower(SHA256.HashData(raw));

        // (1) Something was actually sent, and it was an image.
        Assert.NotEmpty(call.Image);

        // (2) It is NOT the file on disk.
        Assert.NotEqual(rawSha, call.ImageSha256);
        Assert.False(call.Image.AsSpan().SequenceEqual(raw));

        // (3) The raw bytes are nowhere in the request at all — not in another part, not
        //      base64'd a second time, not in a stray field.
        Assert.DoesNotContain(Convert.ToBase64String(raw), call.Body, StringComparison.Ordinal);

        // (4) What was sent is exactly the artefact the pre-pass produced and recorded.
        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(rawSha, row!.RawSha256);
        Assert.Equal(call.ImageSha256, row.RedactedSha256);
        Assert.True(row.RedactionApplied);
    }

    [Fact]
    public async Task The_prompt_tells_the_model_the_image_was_redacted()
    {
        // Without it a model reads a black rectangle as a printing artefact and invents a plausible
        // NIC for the space — which is precisely what I-25.1's "captured from the structured
        // response" has to be protected from.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceFront(), DocumentKinds.DrivingLicense);

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.DrivingLicense, DocumentSides.Front);

        var call = Assert.Single(harness.Gemini.Calls);

        Assert.Contains("black rectangle", call.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEVER guess", call.Prompt, StringComparison.Ordinal);
        Assert.Equal(OcrHarness.GeminiApiKey, call.ApiKey);
    }

    [Fact]
    public async Task A_disarmed_redactor_sends_nothing_to_the_external_model()
    {
        // D-36 fails closed. Point the cascade at a file that is not there and the face blur cannot
        // run; the service must then extract on-prem rather than send an image it did not redact.
        await using var harness = await OcrHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ocr:Redaction:FaceCascadePath"] = "/nonexistent/haarcascade_frontalface_default.xml",
        });

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.Empty(harness.Gemini.Calls);
        Assert.False(result.RedactionApplied);
        Assert.NotEqual(ExtractionEngines.Gemini, result.Engine);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.False(row!.RedactionApplied);
        Assert.Null(row.RedactedSha256);
    }

    [Fact]
    public async Task The_database_refuses_to_record_the_one_thing_that_must_never_happen()
    {
        // ck_extractions_gemini_is_redacted (migration 1310). The service cannot write this row —
        // the type system stops it — and the schema is the last line that would notice if it could.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, _, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => harness.ExecuteAsync(
            """
            INSERT INTO docs.extractions (upload_id, doc_type, status, redaction_applied, engine)
            VALUES (@UploadId, 'insurance', 'EXTRACTED', false, 'gemini');
            """,
            new { UploadId = uploadId }));
    }

    [Fact]
    public async Task The_processing_log_records_which_policy_masked_the_document()
    {
        // ADD §12.5: "hash + policy version + redaction-pass version stored per extraction". A
        // privacy review scopes a policy change to the extractions it affected, and cannot without.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceFront(), DocumentKinds.DrivingLicense);

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.DrivingLicense, DocumentSides.Front);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.Equal(RedactionPipeline.PolicyVersion, row!.RedactionPolicyVersion);
        Assert.Equal(RedactionPipeline.PassVersion, row.RedactionPassVersion);
        Assert.NotNull(row.IdentifiersMasked);
        Assert.NotNull(row.FacesBlurred);
    }

    [Fact]
    public async Task The_identity_numbers_on_the_licence_are_masked_out_of_what_was_sent()
    {
        // The counts come from the real Tesseract read of a real rendered licence: the licence
        // number and the NIC are both on it, and both are identifier-shaped.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.DrivingLicenceFront(), DocumentKinds.DrivingLicense);

        await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.DrivingLicense, DocumentSides.Front);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.True(
            row!.IdentifiersMasked >= 2,
            $"the licence number and the NIC are both printed on this fixture; {row.IdentifiersMasked} box(es) "
            + "were masked. If Tesseract could not read them, the pre-pass could not locate them either.");
    }
}
