using System.Text.Json;
using Dapper;
using MageRide.Ocr.Domain;
using MageRide.Shared.Http;
using MageRide.Shared.Persistence;

namespace MageRide.Ocr.Persistence;

/// <summary>A <c>docs.uploads</c> row, as far as this service reads one (D-36, migration 1301).</summary>
public sealed record DocumentUpload(
    Guid Id, Guid OwnerId, string StorageUrl, string? Kind, DateTimeOffset? AutoDeleteAt);

/// <summary>Everything the extraction pass has to record about one document.</summary>
public sealed record ExtractionRecord(
    Guid UploadId,
    string DocType,
    IReadOnlyList<ExtractedField> Fields,
    string Status,
    decimal? Confidence,
    bool RedactionApplied,
    string Engine,
    string? RawSha256,
    string? RedactedSha256,
    string? PolicyVersion,
    string? PassVersion,
    int? FacesBlurred,
    int? IdentifiersMasked);

/// <summary>
/// <c>docs.extractions</c> and the <c>docs.uploads</c> rows in front of it (D-36).
/// </summary>
/// <remarks>
/// <b>One extraction row per document, per pass</b> — D6' §7.5's "One <c>docs.extractions</c> row
/// per doc". A re-upload gets its own row rather than overwriting the last, because the failed
/// attempt is the audit trail behind a <c>pending_review</c> that a driver has since worked around
/// and an officer may be asked about.
/// </remarks>
public interface IExtractionRepository
{
    /// <summary>The upload a caller named, or null when there is no such row.</summary>
    Task<DocumentUpload?> FindUploadAsync(Guid uploadId, CancellationToken cancellationToken);

    /// <summary>
    /// Stamps NFR-28's deletion deadline on an upload that has none, and returns whether it did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ocr-svc claims this because it is the first thing on the platform that reads the bytes.</b>
    /// registry-svc resolves an id and says outright that filling <c>docs.uploads</c> is not its job;
    /// the surface that writes those rows for onboarding does not exist yet (C125). A licence
    /// photograph whose row carries no <c>auto_delete_at</c> is one NFR-28's sweeper will never
    /// find — kept for ever by omission rather than by decision.
    /// </para>
    /// <para>
    /// <c>now() + interval</c>, on the database clock, and only where the column is NULL: a row that
    /// already carries a deadline was given one by whoever wrote it, and moving it would be this
    /// service quietly extending somebody else's retention promise.
    /// </para>
    /// </remarks>
    Task<bool> EnsureRetentionAsync(Guid uploadId, TimeSpan retention, CancellationToken cancellationToken);

    /// <summary>Writes the extraction and its ADD §12.5 processing log. Returns the row id.</summary>
    Task<Guid> RecordAsync(ExtractionRecord record, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IExtractionRepository"/>
internal sealed class ExtractionRepository(INpgsqlConnectionFactory connections) : IExtractionRepository
{
    public async Task<DocumentUpload?> FindUploadAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<DocumentUpload>(new CommandDefinition(
            "SELECT id, owner_id, storage_url, kind, auto_delete_at FROM docs.uploads WHERE id = @UploadId;",
            new { UploadId = uploadId },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> EnsureRetentionAsync(
        Guid uploadId, TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE docs.uploads
               SET auto_delete_at = now() + @Retention
             WHERE id = @UploadId AND auto_delete_at IS NULL;
            """,
            new { UploadId = uploadId, Retention = retention },
            cancellationToken: cancellationToken));

        return affected > 0;
    }

    public async Task<Guid> RecordAsync(ExtractionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var connection = await connections.OpenAsync(cancellationToken);

        // The fields go in as a JSONB object of key → {value, confidence, verifyStatus}, not as an
        // array: `extracted` is read by a human looking at one document, and an object answers
        // "what did it make of the expiry" without a scan. registry.document_fields is the
        // queryable form, and it has exactly one writer — registry-svc (C029's fence).
        var extracted = JsonSerializer.Serialize(
            record.Fields.ToDictionary(
                field => field.Key,
                field => new
                {
                    value = field.Value,
                    confidence = field.Confidence,
                    verifyStatus = field.VerifyStatus,
                    source = field.Source,
                },
                StringComparer.Ordinal),
            MageRideJson.Options);

        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            """
            INSERT INTO docs.extractions (
                upload_id, doc_type, extracted, confidence, status, redaction_applied,
                engine, raw_sha256, redacted_sha256,
                redaction_policy_version, redaction_pass_version, faces_blurred, identifiers_masked)
            VALUES (
                @UploadId, @DocType, @Extracted::jsonb, @Confidence, @Status, @RedactionApplied,
                @Engine, @RawSha256, @RedactedSha256,
                @PolicyVersion, @PassVersion, @FacesBlurred, @IdentifiersMasked)
            RETURNING id;
            """,
            new
            {
                record.UploadId,
                record.DocType,
                Extracted = extracted,
                record.Confidence,
                record.Status,
                record.RedactionApplied,
                record.Engine,
                record.RawSha256,
                record.RedactedSha256,
                record.PolicyVersion,
                record.PassVersion,
                record.FacesBlurred,
                record.IdentifiersMasked,
            },
            cancellationToken: cancellationToken));
    }
}
