namespace MageRide.Provisioning.Domain;

/// <summary><c>prov.tracker_bindings.state</c> (0401 CHECK, T-08).</summary>
public static class BindingStates
{
    /// <summary>The one state in which a device may publish. Covered by <c>ux_tracker_imei_active</c>.</summary>
    public const string Active = "ACTIVE";

    /// <summary>Two devices claimed this IMEI inside the window; held until an admin resolves it (US-3.4).</summary>
    public const string Quarantined = "QUARANTINED";

    /// <summary>Unbound by its owner or decommissioned by an admin. Terminal.</summary>
    public const string Revoked = "REVOKED";

    public static bool IsKnown(string? state) =>
        state is Active or Quarantined or Revoked;
}

/// <summary>Why a binding last changed state — <c>prov.tracker_bindings.state_reason</c> (0404).</summary>
public static class BindingStateReasons
{
    /// <summary>T-08: another device presented this IMEI inside the anti-clone window.</summary>
    public const string ImeiDuplicate = "imei-duplicate";

    /// <summary>The owner released the binding so the tracker can move to another vehicle.</summary>
    public const string Unbound = "unbound";

    /// <summary>An admin retired the device (US-3.8, <c>DELETE /v1/trackers/{imei}</c>).</summary>
    public const string Decommissioned = "decommissioned";

    /// <summary>Superseded by a newer binding after the anti-clone window had passed.</summary>
    public const string Superseded = "superseded";

    /// <summary>An admin resolved a quarantine in this binding's favour (US-3.4).</summary>
    public const string AdminResolved = "admin-resolved";
}

/// <summary><c>prov.tracker_bindings.credential_type</c> / <c>prov.device_certs.kind</c> (ADD §7.7.3).</summary>
public static class CredentialTypes
{
    /// <summary>X.509 client certificate. MQTT-capable trackers reach EMQX directly with it.</summary>
    public const string X509 = "x509";

    /// <summary>Signed PSK + IMEI-HMAC. Legacy TCP devices behind a protocol adapter.</summary>
    public const string Psk = "psk";

    public static bool IsKnown(string? type) => type is X509 or Psk;
}

/// <summary>
/// <c>prov.tracker_bindings.source</c> — which of the two possible publishers is authoritative
/// for this vehicle (US-3.6, T-11).
/// </summary>
public static class PublisherSources
{
    /// <summary>The driver app's handset GPS.</summary>
    public const string Mobile = "mobile";

    /// <summary>The bound hardware tracker.</summary>
    public const string Hardware = "hardware";

    public static bool IsKnown(string? source) => source is Mobile or Hardware;
}

/// <summary>How a bind request identified itself (D3' <c>method</c>).</summary>
public static class BindMethods
{
    public const string Manual = "manual";
    public const string Qr = "qr";
    public const string AdminCode = "admin_code";

    public static bool IsKnown(string? method) => method is Manual or Qr or AdminCode;
}

/// <summary>Where a presentation of an IMEI came from — <c>prov.imei_sightings.source</c> (0404).</summary>
public static class SightingSources
{
    /// <summary>A <c>POST /v1/trackers/bind</c> request.</summary>
    public const string Bind = "bind";

    /// <summary>An adapter or broker resolving a connecting device (T-01).</summary>
    public const string Validate = "validate";
}

/// <summary>RFC 5280 §5.3.1 revocation reasons — <c>prov.device_certs.revocation_reason</c> (0404).</summary>
public static class RevocationReasons
{
    public const string Unspecified = "unspecified";
    public const string KeyCompromise = "key_compromise";
    public const string AffiliationChanged = "affiliation_changed";

    /// <summary>What a 90-day rotation writes when it retires the outgoing certificate.</summary>
    public const string Superseded = "superseded";

    /// <summary>A decommission (US-3.8) or an owner's unbind.</summary>
    public const string CessationOfOperation = "cessation_of_operation";

    /// <summary>
    /// A T-08 quarantine. The one reason RFC 5280 §5.3.1 lets a CA lift again, which is what
    /// US-3.4's admin resolution does to whichever of the two devices turns out to be genuine.
    /// </summary>
    public const string CertificateHold = "certificate_hold";
}

/// <summary>A row of <c>prov.tracker_bindings</c> — the IMEI ↔ vehicle source of truth (T-03).</summary>
public sealed record TrackerBinding(
    Guid Id,
    string Imei,
    Guid VehicleId,
    Guid? FleetId,
    string CredentialSerial,
    string CredentialType,
    string State,
    DateTimeOffset RotatesAt,
    string? Source,
    DateTimeOffset? LastSeenAt,
    short? SignalStrength,
    int? BatteryMv,
    short? SatCount,
    DateTimeOffset StateChangedAt,
    string? StateReason,
    DateTimeOffset CreatedAt)
{
    /// <summary>Whether a device holding this binding may publish.</summary>
    public bool IsActive => State == BindingStates.Active;
}

/// <summary>A row of <c>prov.device_certs</c> — one issued credential (T-02).</summary>
public sealed record DeviceCertificate(
    Guid Id,
    Guid BindingId,
    string Serial,
    string Kind,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? RevocationReason);

/// <summary>The vehicle facts a bind needs, read out of registry-svc's schema.</summary>
/// <param name="FleetId">The fleet whose roster carries this vehicle, when one does — T-11 scopes
/// tracker positions by it.</param>
public sealed record VehicleReference(Guid Id, Guid OwnerId, string RegistrationNumber, string Status, Guid? FleetId);
