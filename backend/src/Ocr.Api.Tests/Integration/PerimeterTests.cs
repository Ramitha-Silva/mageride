using System.Security.Cryptography;
using MageRide.Ocr.Domain;
using MageRide.Ocr.Redaction;
using MageRide.Ocr.Tests.Infrastructure;
using MageRide.TestKit;

namespace MageRide.Ocr.Tests.Integration;

/// <summary>
/// What actually goes on the wire to the external model, asserted on the bytes that came off the
/// socket.
/// </summary>
/// <remarks>
/// <para>
/// <b>C054's definition of done was "no unredacted image is sent to the external model in an
/// integration test with a network recorder". Δ MCS-07 withdrew that requirement</b>, so these
/// tests now pin the two-sided fact instead: when the pre-pass can run, the redacted image is what
/// leaves and the raw bytes appear nowhere in the request; when it cannot, the raw image leaves and
/// the row says so. Both halves are asserted, because the interesting failure is no longer "an
/// unredacted image escaped" but "nobody can tell which images did".
/// </para>
/// <para>
/// Still asserted on the socket rather than on a mock's arguments: the whole service is running,
/// the real redaction pass ran on a real image, and <see cref="GeminiRecorder"/> kept what arrived.
/// <b>`build/manifest.yaml` and `build/prompts/C054.md` still carry the original wording</b> — they
/// are generated from the manifest, so correcting them is a manifest edit plus a regeneration, and
/// it is flagged in this change's handoff rather than done by hand here.
/// </para>
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
    public async Task A_disarmed_redactor_sends_the_RAW_image_and_says_so_on_the_row()
    {
        // Δ MCS-07. This test asserted the OPPOSITE until MCS-07 — "sends nothing to the external
        // model", D-36 failing closed. The posture is now best-effort redaction: point the cascade
        // at a file that is not there and the face blur cannot run, and the document goes to Gemini
        // AS PHOTOGRAPHED rather than not going at all.
        //
        // It is kept, inverted, rather than deleted, because the fact it pins is the one nobody can
        // see from outside the service: which images left unmasked. If this ever goes quiet, the
        // change that silenced it needs to be deliberate.
        await using var harness = await OcrHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ocr:Redaction:FaceCascadePath"] = "/nonexistent/haarcascade_frontalface_default.xml",
        });

        var (uploadId, storageUrl, raw) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        // (1) The model was called, and with the bytes off storage — not a redaction of them.
        var call = Assert.Single(harness.Gemini.Calls);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), call.ImageSha256);
        Assert.True(call.Image.AsSpan().SequenceEqual(raw));
        Assert.Equal(ExtractionEngines.Gemini, result.Engine);

        // (2) The prompt does NOT claim a redaction that did not happen. A model told that a
        //     document it can read perfectly well has been masked returns nulls for legible fields.
        Assert.DoesNotContain("has been redacted", call.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("black rectangle", call.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("as photographed", call.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEVER guess", call.Prompt, StringComparison.Ordinal);

        // (3) The row is honest about it, and is the only place this is recorded.
        Assert.False(result.RedactionApplied);

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.False(row!.RedactionApplied);
        Assert.Equal(ExtractionEngines.Gemini, row.Engine);
        Assert.Null(row.RedactedSha256);
        Assert.Null(row.RedactionPolicyVersion);

        // (4) ADD §12.5's "which file was processed" survives the pass not running. It used to be
        //     written only off the RedactedDocument, so it was absent from exactly the rows a
        //     privacy review would be opened to look at.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), row.RawSha256);
    }

    [Fact]
    public async Task Bytes_that_are_not_an_image_are_not_sent_even_though_redaction_no_longer_gates_the_send()
    {
        // Δ MCS-07 kept this one true by a different mechanism, and it is worth a test of its own
        // because the mechanism moved. It used to hold for free: a document that would not decode
        // failed the redaction pass, and a failed pass meant nothing left. The pass no longer gates
        // the send, so `ExtractionPipeline.MayBeSent` is what holds it now — a magic-number check,
        // because `ContentTypes.FromBytes` GUESSES image/jpeg for anything it does not recognise.
        //
        // Without it a text file goes to a third-party model labelled as a photograph, and it is
        // the raw path that would do it — the disarmed-redactor deployment, i.e. the replica.
        await using var harness = await OcrHarness.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Ocr:Redaction:FaceCascadePath"] = "/nonexistent/haarcascade_frontalface_default.xml",
        });

        var (uploadId, storageUrl, _) = await harness.UploadAsync(
            DocumentFixtures.NotAnImage(), DocumentKinds.Insurance);

        var result = await harness.ExtractAsync(uploadId, storageUrl, DocumentKinds.Insurance);

        Assert.Empty(harness.Gemini.Calls);
        Assert.False(result.Succeeded);
        Assert.Equal(ExtractionEngines.None, result.Engine);
    }

    [Fact]
    public async Task An_unredacted_Gemini_row_is_recordable_and_is_indexed_as_such()
    {
        // Δ MCS-07, migration 1315. This test asserted the opposite until MCS-07: 1310's
        // `ck_extractions_gemini_is_redacted` refused `engine='gemini' AND NOT redaction_applied`
        // as "the one thing that must never happen", which was true while the extractor could only
        // be handed a RedactedDocument. It is now an ordinary row, and one the service must be able
        // to WRITE — `ExtractionPipeline.PersistAsync` swallows an NpgsqlException here, so the
        // constraint surviving would silently drop the audit record of every unmasked send.
        await using var harness = await OcrHarness.StartAsync(postgres);

        var (uploadId, _, _) = await harness.UploadAsync(
            DocumentFixtures.InsuranceCertificate(), DocumentKinds.Insurance);

        await harness.ExecuteAsync(
            """
            INSERT INTO docs.extractions (upload_id, doc_type, status, redaction_applied, engine)
            VALUES (@UploadId, 'insurance', 'EXTRACTED', false, 'gemini');
            """,
            new { UploadId = uploadId });

        var row = await harness.ExtractionRowAsync(uploadId);

        Assert.False(row!.RedactionApplied);
        Assert.Equal(ExtractionEngines.Gemini, row.Engine);

        // The constraint is gone by name, not merely unenforced — a NOT VALID one still rejects
        // new rows, so "the insert worked" would not on its own prove the migration ran.
        var constraints = await harness.ScalarAsync<long>(
            """
            SELECT count(*) FROM pg_constraint
             WHERE conrelid = 'docs.extractions'::regclass
               AND conname = 'ck_extractions_gemini_is_redacted';
            """);

        Assert.Equal(0L, constraints);
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
