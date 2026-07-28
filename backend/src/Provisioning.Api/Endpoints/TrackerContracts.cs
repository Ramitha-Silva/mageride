using MageRide.Provisioning.Credentials;
using MageRide.Provisioning.Domain;
using MageRide.Provisioning.Trackers;

namespace MageRide.Provisioning.Endpoints;

// The wire shapes of backend/contracts/provisioning.yaml. Records rather than the domain types so
// the contract and the schema can move independently: prov.tracker_bindings stores `battery_mv`
// and D3' returns a percentage, and neither should have to change because the other did.

/// <summary><c>POST /v1/trackers/bind</c> request body.</summary>
internal sealed record BindTrackerBody(
    string? Imei, string? VehicleId, string? Method, string? BindCode, string? CredentialType);

/// <summary><c>POST /v1/trackers/unbind</c> request body (C030 micro-change-set — see the endpoint).</summary>
internal sealed record UnbindTrackerBody(string? Imei);

/// <summary>
/// <c>POST /v1/internal/trackers/{imei}/quarantine</c> — the adapter's T-08 clone report.
/// </summary>
/// <param name="ReportedBy">Which adapter saw it, e.g. <c>adapter-gt06</c>. Goes into the alert so
/// an operator knows which protocol family and which pod to look at.</param>
/// <param name="Detail">What it saw — the two peer addresses, the session ids. Free text, shown on
/// the US-3.4 resolution screen.</param>
internal sealed record QuarantineTrackerBody(string? ReportedBy, string? Detail);

/// <summary><c>POST /v1/trackers/{imei}/switch-source</c> request and response.</summary>
internal sealed record SwitchSourceBody(string? Source);

internal sealed record SwitchSourceResponse(string Source);

/// <summary><c>provisioning.yaml#/components/schemas/Credential</c>.</summary>
internal sealed record CredentialResponse(
    string Type, string CredentialSerial, string? ClientCertPem, string? PskToken, DateTimeOffset RotatesAt)
{
    public static CredentialResponse From(DeviceCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return new CredentialResponse(
            credential.Type, credential.Serial, credential.ClientCertPem, credential.PskToken, credential.RotatesAt);
    }
}

/// <summary><c>provisioning.yaml#/components/schemas/Binding</c>.</summary>
internal sealed record BindingResponse(
    Guid BindingId,
    string Imei,
    Guid VehicleId,
    string State,
    string CredentialSerial,
    CredentialResponse Credential,
    DateTimeOffset RotatesAt)
{
    public static BindingResponse From(BoundTracker bound)
    {
        ArgumentNullException.ThrowIfNull(bound);

        return new BindingResponse(
            bound.Binding.Id,
            bound.Binding.Imei,
            bound.Binding.VehicleId,
            bound.Binding.State,
            bound.Binding.CredentialSerial,
            CredentialResponse.From(bound.Credential),
            bound.Binding.RotatesAt);
    }

    /// <summary>
    /// The read form: everything except the secret half, which existed once and was not kept.
    /// </summary>
    /// <remarks>
    /// <c>Binding.credential</c> is required by the contract's schema, so the object is present and
    /// its <c>clientCertPem</c>/<c>pskToken</c> are absent — which is the honest shape. Inventing a
    /// placeholder would suggest a credential could be fetched again, and
    /// <c>prov.device_certs.pem_or_token_hash</c> holds a hash precisely so that it cannot.
    /// </remarks>
    public static BindingResponse From(TrackerBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return new BindingResponse(
            binding.Id,
            binding.Imei,
            binding.VehicleId,
            binding.State,
            binding.CredentialSerial,
            new CredentialResponse(binding.CredentialType, binding.CredentialSerial, null, null, binding.RotatesAt),
            binding.RotatesAt);
    }
}

/// <summary><c>GET /v1/trackers/{imei}</c> — the binding plus the fleet-health rollup (US-3.12).</summary>
internal sealed record TrackerResponse(
    BindingResponse Binding, DateTimeOffset? LastSeen, int? Signal, int? Battery, int? Sats)
{
    public static TrackerResponse From(TrackerDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return new TrackerResponse(
            BindingResponse.From(detail.Binding),
            detail.Binding.LastSeenAt,
            detail.Binding.SignalStrength,
            detail.BatteryPercent,
            detail.Binding.SatCount);
    }
}

/// <summary><c>GET /v1/internal/trackers/{imei}/validate</c>.</summary>
internal sealed record ValidateResponse(bool Valid, Guid? VehicleId, string? State)
{
    public static ValidateResponse From(ValidationVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return new ValidateResponse(verdict.Valid, verdict.VehicleId, verdict.State);
    }
}

/// <summary><c>provisioning.yaml#/components/schemas/BulkJob</c>.</summary>
internal sealed record BulkJobResponse(
    Guid JobId,
    int TotalRows,
    string Status,
    int SucceededRows,
    int FailedRows,
    string? ErrorReportUrl)
{
    public static BulkJobResponse From(BulkJob job, string? errorReportUrl)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new BulkJobResponse(
            job.Id,
            job.TotalRows,
            job.Status,
            job.SucceededRows,
            job.FailedRows,
            // D3': "available when done". A link handed out while rows are still being minted
            // would download a report that is wrong by the time it is read.
            job.Status == BulkJobStatuses.Processing ? null : errorReportUrl);
    }
}
