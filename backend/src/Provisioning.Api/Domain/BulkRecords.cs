namespace MageRide.Provisioning.Domain;

/// <summary><c>prov.bulk_jobs.status</c> (0405), and D3''s <c>BulkJob.status</c> enum.</summary>
public static class BulkJobStatuses
{
    /// <summary>Rows are validated and queued; credentials are still being minted.</summary>
    public const string Processing = "PROCESSING";

    /// <summary>
    /// Every row reached a terminal outcome. <b>Includes jobs where every row failed</b> — the job
    /// itself did what it was asked, and the per-row report says what happened.
    /// </summary>
    public const string Completed = "COMPLETED";

    /// <summary>The job could not be carried out at all — the worker gave up on it.</summary>
    public const string Failed = "FAILED";
}

/// <summary><c>prov.bulk_job_rows.status</c> (0405).</summary>
public static class BulkRowStatuses
{
    public const string Pending = "PENDING";
    public const string Bound = "BOUND";
    public const string Failed = "FAILED";
}

/// <summary>A bulk onboarding job — what <c>GET /v1/fleets/{id}/trackers/bulk/{jobId}</c> reports.</summary>
public sealed record BulkJob(
    Guid Id,
    Guid FleetId,
    Guid RequestedBy,
    string Status,
    int TotalRows,
    int SucceededRows,
    int FailedRows,
    string CredentialType,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>One parsed CSV row, before it has been attempted.</summary>
/// <param name="RowNumber">1-based, counting the header — the number the operator sees in a
/// spreadsheet, so a report line points at the line they have to fix.</param>
public sealed record BulkRowInput(int RowNumber, string Imei, string RegistrationNumber, Guid? VehicleId);

/// <summary>One row read back, with whatever outcome it has reached.</summary>
public sealed record BulkRow(
    Guid JobId,
    int RowNumber,
    string Imei,
    string RegistrationNumber,
    Guid? VehicleId,
    string Status,
    string? ErrorCode,
    string? ErrorDetail,
    Guid? BindingId);
