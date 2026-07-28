namespace MageRide.Provisioning.Credentials;

/// <summary>
/// A freshly minted device credential. The secret half is present <b>once</b>, on the response
/// that minted it (D3': "Returned once, at mint or rotation").
/// </summary>
/// <param name="Serial">Certificate serial number, or the PSK's serial. Uppercase hex; this is
/// what <c>prov.tracker_bindings.credential_serial</c> and <c>prov.device_certs.serial</c> hold
/// and what a revocation names.</param>
/// <param name="Type"><see cref="Domain.CredentialTypes"/>.</param>
/// <param name="ClientCertPem">For <c>x509</c>: the private key, the leaf and the issuing
/// intermediate, concatenated. A leaf on its own is not usable — the device has no key for it and
/// the broker cannot chain it to the root.</param>
/// <param name="PskToken">For <c>psk</c>: the signed pre-shared token.</param>
/// <param name="MaterialHash">SHA-256 of the secret half. This is what
/// <c>prov.device_certs.pem_or_token_hash</c> stores — the column's comment is "never the
/// credential itself", and a hash is enough to recognise a credential that is presented back.</param>
public sealed record DeviceCredential(
    string Serial,
    string Type,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset RotatesAt,
    string? ClientCertPem,
    string? PskToken,
    byte[] MaterialHash);

/// <summary>A certificate the CRL must carry (RFC 5280 §5.1).</summary>
public sealed record RevokedCredential(string Serial, DateTimeOffset RevokedAt, string? Reason);
